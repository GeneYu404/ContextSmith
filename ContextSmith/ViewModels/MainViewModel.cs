using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using ContextSmith.Models;
using ContextSmith.Services;

namespace ContextSmith.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly MenuScanner _scanner = new();
    private readonly BackupService _backup = new();
    private readonly MenuWriter _writer;
    private readonly Func<SceneDefinition, AddVerbRequest?> _showAddDialog;
    private readonly Func<MenuEntry, EditVerbRequest?> _showEditDialog;

    private NavItem? _selectedNav;
    private MenuEntryViewModel? _selectedEntry;
    private string _searchText = "";
    private int _filterIndex;
    private string? _programFilter;
    private int _programFilterIndex;
    private bool _isBusy;
    private bool _pendingRestart;
    private LogItem? _lastLog;
    private string? _toastText;
    private int _loadVersion;
    private int _toastVersion;

    public MainViewModel(Func<SceneDefinition, AddVerbRequest?> showAddDialog,
        Func<MenuEntry, EditVerbRequest?> showEditDialog)
    {
        _writer = new MenuWriter(_backup);
        _showAddDialog = showAddDialog;
        _showEditDialog = showEditDialog;

        foreach (var s in Scenes.All)
            NavItems.Add(new NavItem { Kind = NavKind.Scene, Title = s.Title, Glyph = s.Glyph, Scene = s });
        NavItems.Add(new NavItem { Kind = NavKind.Tweaks, Title = "系统优化", Glyph = "\uE713", StartsGroup = true });
        NavItems.Add(new NavItem { Kind = NavKind.Log, Title = "操作日志", Glyph = "\uE81C" });

        foreach (var t in TweakService.All)
            Tweaks.Add(new TweakViewModel(t, this));

        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = FilterEntry;

        RefreshCommand = new AsyncCommand(ReloadAsync);
        RestartExplorerCommand = new AsyncCommand(RestartExplorerAsync);
        BackupAllCommand = new AsyncCommand(BackupAllAsync);
        AddCommand = new RelayCommand(AddVerb, () => CurrentScene?.SupportsCustomVerbs == true);
        EditCommand = new RelayCommand(EditSelected, () => SelectedEntry?.IsShellVerb == true);
        DeleteCommand = new RelayCommand(DeleteSelected, () => SelectedEntry?.CanDelete == true);
        UndoCommand = new RelayCommand(
            p => UndoLog(p as LogItem ?? LastLog),
            p => (p as LogItem ?? LastLog)?.CanUndo == true);
        EnableAllCommand = new RelayCommand(() => SetAll(true), () => IsScenePage && Entries.Count > 0);
        DisableAllCommand = new RelayCommand(() => SetAll(false), () => IsScenePage && Entries.Count > 0);
        CopyCommand = new RelayCommand(p => CopyText(p as string));
        OpenRegeditCommand = new RelayCommand(OpenRegedit, () => SelectedEntry is not null);
        OpenBackupFolderCommand = new RelayCommand(OpenBackupFolder);
        RequestElevationCommand = new RelayCommand(RequestElevation);
        ClearLogCommand = new RelayCommand(
            () => { Log.Clear(); LastLog = null; SaveLogSafe(); },
            () => Log.Count > 0);

        LoadLog();
    }

    // ================================================================= 绑定属性

    public string OsLabel =>
        $"{(TweakService.IsWindows11 ? "Windows 11" : "Windows 10")} · Build {Environment.OSVersion.Version.Build}";

    public bool IsAdmin => Elevation.IsElevated;

    /// <summary>侧边栏的权限说明：按需提权而不是启动即 UAC。</summary>
    public string ElevationHint => IsAdmin
        ? "已获取管理员权限，可直接修改 HKLM。"
        : "普通权限运行：扫描、备份、HKCU 写入即时生效；仅写入 HKLM 时请求管理员授权并续跑。";

    public ObservableCollection<NavItem> NavItems { get; } = [];
    public ObservableCollection<MenuEntryViewModel> Entries { get; } = [];
    public ICollectionView EntriesView { get; }
    public ObservableCollection<TweakViewModel> Tweaks { get; } = [];
    public ObservableCollection<LogItem> Log { get; } = [];

    public NavItem? SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (!Set(ref _selectedNav, value)) return;
            OnPropertyChanged(nameof(CurrentScene));
            OnPropertyChanged(nameof(IsScenePage));
            OnPropertyChanged(nameof(IsTweaksPage));
            OnPropertyChanged(nameof(IsLogPage));
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageDescription));
            OnPropertyChanged(nameof(PageGlyph));
            OnPropertyChanged(nameof(RootPathHint));

            if (value?.Kind == NavKind.Scene)
            {
                _searchText = "";
                OnPropertyChanged(nameof(SearchText));
                _ = LoadSceneAsync();
            }
            else if (value?.Kind == NavKind.Tweaks)
            {
                foreach (var t in Tweaks) t.Reload();
            }
        }
    }

    public SceneDefinition? CurrentScene => SelectedNav?.Scene;
    public bool IsScenePage => SelectedNav?.Kind == NavKind.Scene;
    public bool IsTweaksPage => SelectedNav?.Kind == NavKind.Tweaks;
    public bool IsLogPage => SelectedNav?.Kind == NavKind.Log;

    public string PageTitle => CurrentScene?.Title ?? "";
    public string PageDescription => CurrentScene?.Description ?? "";
    public string PageGlyph => CurrentScene?.Glyph ?? "";

    public string RootPathHint => CurrentScene switch
    {
        { Kind: SceneKind.FileTypes } => @"HKLM / HKCU\SOFTWARE\Classes · 全部扩展名与 ProgID（按程序分组）",
        { ClassesSubPath: { } p } => $@"HKLM / HKCU\SOFTWARE\Classes\{p}\shell  ·  shellex\ContextMenuHandlers",
        _ => @"HKLM / HKCU\SOFTWARE\Classes\.<扩展名>\ShellNew",
    };

    public MenuEntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set => Set(ref _selectedEntry, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value)) EntriesView.Refresh();
        }
    }

    /// <summary>0 全部，1 已启用，2 已禁用，3 第三方</summary>
    public int FilterIndex
    {
        get => _filterIndex;
        set
        {
            if (Set(ref _filterIndex, value)) EntriesView.Refresh();
        }
    }

    /// <summary>程序筛选下拉的选项：全部程序 + 当前场景里出现过的 exe / dll。</summary>
    public ObservableCollection<string> ProgramOptions { get; } = ["全部程序"];

    /// <summary>按所属程序筛选。0 = 全部。</summary>
    public int ProgramFilterIndex
    {
        get => _programFilterIndex;
        set
        {
            if (!Set(ref _programFilterIndex, value)) return;
            _programFilter = value <= 0 || value >= ProgramOptions.Count ? null : ProgramOptions[value];
            EntriesView.Refresh();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public bool PendingRestart
    {
        get => _pendingRestart;
        private set => Set(ref _pendingRestart, value);
    }

    public LogItem? LastLog
    {
        get => _lastLog;
        private set => Set(ref _lastLog, value);
    }

    public string? ToastText
    {
        get => _toastText;
        private set => Set(ref _toastText, value);
    }

    public int EnabledCount => Entries.Count(e => e.Enabled);
    public int DisabledCount => Entries.Count - EnabledCount;
    public int ThirdPartyCount => Entries.Count(e => !e.Model.IsMicrosoft);

    // ================================================================= 命令

    public ICommand RefreshCommand { get; }
    public ICommand RestartExplorerCommand { get; }
    public ICommand BackupAllCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand EnableAllCommand { get; }
    public ICommand DisableAllCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand OpenRegeditCommand { get; }
    public ICommand OpenBackupFolderCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand RequestElevationCommand { get; }

    // ================================================================= 加载

    public async Task InitializeAsync()
    {
        SelectedNav = NavItems[0];
        await RefreshCountsAsync();
    }

    private async Task RefreshCountsAsync()
    {
        var results = await Task.Run(() => Scenes.All.Select(s => (Scene: s, List: SafeScan(s))).ToList());
        foreach (var (scene, list) in results)
        {
            var nav = NavItems.FirstOrDefault(n => n.Scene == scene);
            if (nav is not null) nav.Count = $"{list.Count(e => e.Enabled)}/{list.Count}";
        }
    }

    private List<MenuEntry> SafeScan(SceneDefinition scene)
    {
        try
        {
            return _scanner.Scan(scene);
        }
        catch
        {
            return [];
        }
    }

    private async Task LoadSceneAsync(string? keepSelectionPath = null)
    {
        if (CurrentScene is not { } scene) return;
        var version = ++_loadVersion;
        IsBusy = true;
        try
        {
            var vms = await Task.Run(() => SafeScan(scene).Select(e => new MenuEntryViewModel(e, this)).ToList());
            if (version != _loadVersion) return; // 期间用户已切换到其它场景

            Entries.Clear();
            foreach (var vm in vms) Entries.Add(vm);
            ApplySceneOrganization(scene);
            SelectedEntry = keepSelectionPath is null
                ? null
                : Entries.FirstOrDefault(e => string.Equals(e.FullPath, keepSelectionPath, StringComparison.OrdinalIgnoreCase));
            RaiseStats();
        }
        finally
        {
            if (version == _loadVersion) IsBusy = false;
        }
    }

    /// <summary>
    /// 按场景整理列表：文件类型场景上千条，按“所属程序”分组并排序，其它场景保持扫描顺序。
    /// 每次加载同时重建程序筛选下拉并复位筛选状态。
    /// </summary>
    private void ApplySceneOrganization(SceneDefinition scene)
    {
        _programFilter = null;
        _programFilterIndex = 0;
        ProgramOptions.Clear();
        ProgramOptions.Add("全部程序");
        foreach (var p in Entries.Select(e => e.Program)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase))
            ProgramOptions.Add(p);
        OnPropertyChanged(nameof(ProgramFilterIndex));

        EntriesView.SortDescriptions.Clear();
        EntriesView.GroupDescriptions.Clear();
        if (scene.Kind == SceneKind.FileTypes)
        {
            EntriesView.SortDescriptions.Add(
                new SortDescription(nameof(MenuEntryViewModel.Program), ListSortDirection.Ascending));
            EntriesView.SortDescriptions.Add(
                new SortDescription(nameof(MenuEntryViewModel.Name), ListSortDirection.Ascending));
            EntriesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MenuEntryViewModel.Program)));
        }
        EntriesView.Refresh();
    }

    private Task ReloadAsync()
    {
        if (IsScenePage) return LoadSceneAsync(SelectedEntry?.FullPath);
        foreach (var t in Tweaks) t.Reload();
        return Task.CompletedTask;
    }

    private bool FilterEntry(object o)
    {
        if (o is not MenuEntryViewModel e) return false;

        if (_programFilter is not null &&
            !string.Equals(e.Program, _programFilter, StringComparison.OrdinalIgnoreCase)) return false;

        var pass = FilterIndex switch
        {
            1 => e.Enabled,
            2 => !e.Enabled,
            3 => !e.Model.IsMicrosoft,
            _ => true,
        };
        if (!pass) return false;

        var q = SearchText.Trim();
        if (q.Length == 0) return true;

        return Has(e.Name, q) || Has(e.Model.KeyName, q) || Has(e.Model.Command, q)
            || Has(e.Model.Clsid, q) || Has(e.Model.Vendor, q) || Has(e.Model.Extension, q)
            || Has(e.Program, q);
    }

    private static bool Has(string? s, string q) => s?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;

    private static string PositionText(string position) => position switch
    {
        "Top" => "顶部",
        "Bottom" => "底部",
        _ => "默认",
    };

    private void RaiseStats()
    {
        OnPropertyChanged(nameof(EnabledCount));
        OnPropertyChanged(nameof(DisabledCount));
        OnPropertyChanged(nameof(ThirdPartyCount));
        if (SelectedNav is { Kind: NavKind.Scene } nav)
            nav.Count = $"{EnabledCount}/{Entries.Count}";
    }

    // ================================================================= 即时写入（由条目 ViewModel 调用）

    /// <summary>该条目的写入是否需要管理员：HKLM 键，或全局 Shell Extensions\Blocked。</summary>
    private static bool NeedsElevation(MenuEntry e) =>
        !Elevation.IsElevated &&
        (e.Hive == RegHive.LocalMachine ||
         (e.Kind == EntryKind.ShellEx && e.Scope == DisableScope.Global));

    /// <summary>把操作写入待执行队列并请求提权重启。恒返回 false：当前实例没有执行该操作。</summary>
    private bool QueueForElevation(PendingOp op) => QueueForElevation([op]);

    private bool QueueForElevation(List<PendingOp> ops)
    {
        try
        {
            PendingOps.Save(ops);
        }
        catch (Exception ex)
        {
            ShowError($"无法保存待提权的操作队列：{ex.Message}");
            return false;
        }

        if (!Elevation.TryRestartElevated(Elevation.PendingFile))
        {
            PendingOps.Clear();
            ShowToast("已取消管理员授权，操作未执行");
            return false;
        }

        Application.Current.Shutdown();
        return false;
    }

    private void RequestElevation()
    {
        if (Elevation.IsElevated)
        {
            ShowToast("当前已是管理员权限");
            return;
        }
        if (Elevation.TryRestartElevated()) Application.Current.Shutdown();
    }

    internal bool TryApplyEnabled(MenuEntryViewModel vm, bool enabled)
    {
        var scene = CurrentScene?.Title;
        var model = vm.Model;

        if (NeedsElevation(model))
            return QueueForElevation(new PendingOp
            {
                Type = PendingTypes.SetEnabled,
                Entry = model,
                Enabled = enabled,
                Snapshot = RegistrySnapshot.CaptureFor(model),
            });

        return Try(() =>
        {
            var snapshot = RegistrySnapshot.CaptureFor(model); // 写前抓现场
            var file = _writer.SetEnabled(model, enabled);
            AddLog(new LogItem
            {
                Title = $"{(enabled ? "启用" : "禁用")}「{vm.Name}」",
                Detail = $"{scene} · {vm.KindLabel} · {vm.HiveLabel}",
                Glyph = enabled ? "\uE73E" : "\uE711",
                BackupFile = file,
                Snapshot = snapshot,
                Undo = () => _writer.SetEnabled(model, !enabled),
            });
            if (model.Kind == EntryKind.ShellEx) PendingRestart = true;
            RaiseStats();
        });
    }

    internal bool TryApplyExtended(MenuEntryViewModel vm, bool extended)
    {
        var model = vm.Model;

        if (NeedsElevation(model))
            return QueueForElevation(new PendingOp
            {
                Type = PendingTypes.SetExtended,
                Entry = model,
                Flag = extended,
                Snapshot = RegistrySnapshot.CaptureFor(model),
            });

        return Try(() =>
        {
            var snapshot = RegistrySnapshot.CaptureFor(model);
            _writer.SetExtended(model, extended);
            AddLog(new LogItem
            {
                Title = $"「{vm.Name}」{(extended ? "改为仅按住 Shift 时显示" : "改为始终显示")}",
                Detail = vm.FullPath,
                Glyph = "\uE765",
                Snapshot = snapshot,
                Undo = () => _writer.SetExtended(model, !extended),
            });
        });
    }

    internal bool TryApplyPosition(MenuEntryViewModel vm, string position)
    {
        var model = vm.Model;
        var old = model.Position;

        if (NeedsElevation(model))
            return QueueForElevation(new PendingOp
            {
                Type = PendingTypes.SetPosition,
                Entry = model,
                Position = position,
                Snapshot = RegistrySnapshot.CaptureFor(model),
            });

        return Try(() =>
        {
            var snapshot = RegistrySnapshot.CaptureFor(model);
            _writer.SetPosition(model, position);
            AddLog(new LogItem
            {
                Title = $"「{vm.Name}」位置改为{PositionText(position)}",
                Detail = vm.FullPath,
                Glyph = "\uE8CB",
                Snapshot = snapshot,
                Undo = () => _writer.SetPosition(model, old),
            });
        });
    }

    internal bool TryApplyTweak(TweakViewModel t, bool on)
    {
        if (t.Definition.TouchesHklm && !Elevation.IsElevated)
            return QueueForElevation(new PendingOp
            {
                Type = PendingTypes.Tweak,
                TweakId = t.Definition.Id,
                Flag = on,
            });

        return Try(() =>
        {
            t.Definition.Apply(on);
            AddLog(new LogItem
            {
                Title = $"{(on ? "开启" : "关闭")}「{t.Title}」",
                Detail = t.Tags,
                Glyph = "\uE713",
                Undo = () => t.Definition.Apply(!on),
            });
            if (t.Definition.Restart == RestartNeed.Explorer) PendingRestart = true;
            ExplorerService.NotifyAssociationsChanged();
        });
    }

    // ================================================================= 其它操作

    private void EditSelected()
    {
        if (SelectedEntry is not { IsShellVerb: true } vm) return;
        var request = _showEditDialog(vm.Model);
        if (request is null) return;

        if (NeedsElevation(vm.Model))
        {
            QueueForElevation(new PendingOp
            {
                Type = PendingTypes.EditVerb,
                Entry = vm.Model,
                Edit = request,
                Snapshot = RegistrySnapshot.CaptureFor(vm.Model),
            });
            return;
        }

        var snapshot = RegistrySnapshot.CaptureFor(vm.Model);
        if (!Try(() => _writer.EditVerb(vm.Model, request))) return;

        vm.Refresh();
        vm.BumpFlash();
        AddLog(new LogItem
        {
            Title = $"编辑「{request.DisplayName}」",
            Detail = vm.FullPath,
            Glyph = "\uE70F",
            Snapshot = snapshot,
        });
    }

    private void SetAll(bool enabled)
    {
        var targets = EntriesView.Cast<MenuEntryViewModel>().Where(e => e.Enabled != enabled).ToList();
        if (targets.Count == 0)
        {
            ShowToast(enabled ? "当前列表已全部启用" : "当前列表已全部禁用");
            return;
        }
        if (!enabled && !Confirm($"确定禁用当前列表中的 {targets.Count} 项吗？\n\n每一项都会先备份，之后可在操作日志中一键撤销。"))
            return;

        if (targets.Any(t => NeedsElevation(t.Model)))
        {
            // 批量里混着需要 HKLM 的项：整批排队，一次 UAC 后由提权实例全部续跑
            QueueForElevation(targets.Select(t => new PendingOp
            {
                Type = PendingTypes.SetEnabled,
                Entry = t.Model,
                Enabled = enabled,
                Snapshot = RegistrySnapshot.CaptureFor(t.Model),
            }).ToList());
            return;
        }

        var batch = new RegistrySnapshot();  // 合并现场：一轮批量 = 一份可整体撤销的快照
        var done = new List<MenuEntry>();
        var failed = 0;
        string? firstError = null;

        foreach (var vm in targets)
        {
            try
            {
                batch.Merge(RegistrySnapshot.CaptureFor(vm.Model) ?? new RegistrySnapshot());
                _writer.SetEnabled(vm.Model, enabled);
                done.Add(vm.Model);
                vm.Refresh();
                vm.BumpFlash();
            }
            catch (Exception ex)
            {
                failed++;
                firstError ??= ex.Message;
            }
        }

        if (failed > 0 && done.Count > 0)
        {
            // 任何一项失败：按现场把本轮已写入的内容整体还原，绝不留下半套状态
            try
            {
                batch.Restore();
            }
            catch (Exception ex)
            {
                ShowError($"批量写入失败，回滚现场时又出错：{ex.Message}");
            }
            ShowError($"批量写入失败（{firstError}），本轮 {done.Count} 项更改已整体回滚。");
            done.Clear();
            _ = LoadSceneAsync(SelectedEntry?.FullPath);
            RaiseStats();
            return;
        }

        if (done.Count > 0)
        {
            AddLog(new LogItem
            {
                Title = $"{(enabled ? "启用" : "禁用")}了 {done.Count} 项",
                Detail = CurrentScene?.Title ?? "",
                Glyph = enabled ? "\uE73E" : "\uE711",
                Snapshot = batch,
                Undo = null,  // 快照本身就是撤销
            });
        }
        if (done.Any(m => m.Kind == EntryKind.ShellEx)) PendingRestart = true;
        if (failed > 0) ShowError($"{failed} 项写入失败，可能受 TrustedInstaller 保护。");
        RaiseStats();
    }

    private void AddVerb()
    {
        if (CurrentScene is not { SupportsCustomVerbs: true } scene) return;
        var request = _showAddDialog(scene);
        if (request is null) return;

        MenuEntry? created = null;
        RegistrySnapshot? snapshot = null;
        var keyPath = $@"{Reg.ClassesPrefix}\{request.Scene.ClassesSubPath}\shell\{request.KeyName}";
        if (!Try(() =>
            {
                // 新建项此刻还不存在：现场 = "此处为空"，撤销即删除整棵新建的键
                snapshot = RegistrySnapshot.CaptureKey(RegHive.CurrentUser, keyPath, Microsoft.Win32.RegistryView.Registry64);
                created = _writer.AddVerb(request);
            }) || created is null)
            return;

        var entry = created;
        AddLog(new LogItem
        {
            Title = $"新增「{request.DisplayName}」",
            Detail = $"{request.Scene.Title} · HKCU · {entry.Command}",
            Glyph = "\uE710",
            Snapshot = snapshot,
            Undo = () => _writer.Delete(entry),
        });

        if (request.Scene == CurrentScene) _ = LoadSceneAsync(entry.FullPath);
        else SelectedNav = NavItems.First(n => n.Scene == request.Scene);
    }

    private void DeleteSelected()
    {
        if (SelectedEntry is not { CanDelete: true } vm) return;
        if (!Confirm($"确定删除「{vm.Name}」吗？\n\n{vm.FullPath}\n\n删除前会自动导出备份，可在操作日志中撤销。"))
            return;

        if (NeedsElevation(vm.Model))
        {
            QueueForElevation(new PendingOp
            {
                Type = PendingTypes.Delete,
                Entry = vm.Model,
                Snapshot = RegistrySnapshot.CaptureFor(vm.Model),
            });
            return;
        }

        string? file = null;
        RegistrySnapshot? snapshot = null;
        var model = vm.Model;
        var is32 = model.Is32Bit;
        if (!Try(() =>
            {
                snapshot = RegistrySnapshot.CaptureFor(model);  // 删前抓现场：撤销 = 整键按现场还原
                file = _writer.Delete(model);
            }))
            return;

        var backupFile = file;
        AddLog(new LogItem
        {
            Title = $"删除「{vm.Name}」",
            Detail = vm.FullPath,
            Glyph = "\uE74D",
            BackupFile = backupFile,
            Snapshot = snapshot,
            Undo = () =>
            {
                if (!_backup.Import(backupFile!, is32))
                    throw new InvalidOperationException($"导入备份失败：{backupFile}");
            },
        });

        Entries.Remove(vm);
        SelectedEntry = null;
        RaiseStats();
    }

    private void UndoLog(LogItem? item)
    {
        if (item is not { CanUndo: true }) return;

        // 撤销目标含 HKLM 而当前未提权：把"按现场写回"也排队续跑
        if (item.Snapshot is { TouchesLocalMachine: true } && !Elevation.IsElevated)
        {
            QueueForElevation(new PendingOp
            {
                Type = PendingTypes.Restore,
                Snapshot = item.Snapshot,
                Title = $"已撤销：{item.Title}",
                Detail = item.Detail,
                Glyph = "\uE7A7",
            });
            return;
        }

        // 首选现场快照：直接把注册表写回当时的现场，比闭包更精确，且跨重启可用
        if (item.Snapshot is not null)
        {
            if (!Try(() => item.Snapshot.Restore())) return;
        }
        else if (item.Undo is { } undo)
        {
            if (!Try(undo)) return;
        }
        else return;

        item.Undone = true;
        AddLog(new LogItem { Title = $"已撤销：{item.Title}", Detail = item.Detail, Glyph = "\uE7A7" });
        foreach (var t in Tweaks) t.Reload();
        if (IsScenePage) _ = LoadSceneAsync(SelectedEntry?.FullPath);
    }

    /// <summary>提权后的实例启动时重放待执行队列（提权续跑）。</summary>
    public async Task ResumePendingAsync()
    {
        var ops = PendingOps.Load();
        if (ops.Count == 0) return;

        IsBusy = true;
        var ok = 0;
        var failed = 0;
        try
        {
            // 启用/禁用批量合并成一条日志（一轮排队通常只对应一批同向开关）
            var toggles = ops.Where(o => o.Type == PendingTypes.SetEnabled && o.Entry is not null).ToList();
            if (toggles.Count > 0)
            {
                var batch = new RegistrySnapshot();
                var doneEntries = new List<MenuEntry>();
                var enabled = toggles[0].Enabled;
                foreach (var op in toggles)
                {
                    try
                    {
                        if (op.Snapshot is not null) batch.Merge(op.Snapshot);
                        _writer.SetEnabled(op.Entry!, op.Enabled);
                        doneEntries.Add(op.Entry!);
                    }
                    catch
                    {
                        failed++;
                    }
                }
                if (doneEntries.Count > 0)
                {
                    ok += doneEntries.Count;
                    AddLog(new LogItem
                    {
                        Title = doneEntries.Count == 1
                            ? $"{(enabled ? "启用" : "禁用")}「{doneEntries[0].DisplayName}」"
                            : $"{(enabled ? "启用" : "禁用")}了 {doneEntries.Count} 项",
                        Detail = failed > 0 ? $"提权续跑 · {failed} 项失败" : "提权续跑",
                        Glyph = enabled ? "\uE73E" : "\uE711",
                        Snapshot = batch,
                    });
                    if (doneEntries.Any(e => e.Kind == EntryKind.ShellEx)) PendingRestart = true;
                }
            }

            foreach (var op in ops)
            {
                if (op.Type == PendingTypes.SetEnabled) continue;
                try
                {
                    switch (op.Type)
                    {
                        case PendingTypes.SetExtended:
                        {
                            var e = op.Entry!;
                            _writer.SetExtended(e, op.Flag);
                            AddLog(new LogItem
                            {
                                Title = $"「{e.DisplayName}」{(op.Flag ? "改为仅按住 Shift 时显示" : "改为始终显示")}",
                                Detail = "提权续跑",
                                Glyph = "\uE765",
                                Snapshot = op.Snapshot,
                            });
                            break;
                        }
                        case PendingTypes.SetPosition:
                        {
                            var e = op.Entry!;
                            _writer.SetPosition(e, op.Position);
                            AddLog(new LogItem
                            {
                                Title = $"「{e.DisplayName}」位置改为{PositionText(op.Position)}",
                                Detail = "提权续跑",
                                Glyph = "\uE8CB",
                                Snapshot = op.Snapshot,
                            });
                            break;
                        }
                        case PendingTypes.Delete:
                        {
                            var e = op.Entry!;
                            var file = _writer.Delete(e);
                            AddLog(new LogItem
                            {
                                Title = $"删除「{e.DisplayName}」",
                                Detail = "提权续跑",
                                Glyph = "\uE74D",
                                BackupFile = file,
                                Snapshot = op.Snapshot,
                            });
                            break;
                        }
                        case PendingTypes.EditVerb:
                        {
                            var e = op.Entry!;
                            var req = op.Edit!;
                            _writer.EditVerb(e, req);
                            AddLog(new LogItem
                            {
                                Title = $"编辑「{req.DisplayName}」",
                                Detail = "提权续跑",
                                Glyph = "\uE70F",
                                Snapshot = op.Snapshot,
                            });
                            break;
                        }
                        case PendingTypes.Tweak:
                        {
                            var def = TweakService.All.FirstOrDefault(t => t.Id == op.TweakId)
                                ?? throw new InvalidOperationException($"未知的优化项：{op.TweakId}");
                            def.Apply(op.Flag);
                            AddLog(new LogItem
                            {
                                Title = $"{(op.Flag ? "开启" : "关闭")}「{def.Title}」",
                                Detail = "提权续跑",
                                Glyph = "\uE713",
                            });
                            if (def.Restart == RestartNeed.Explorer) PendingRestart = true;
                            break;
                        }
                        case PendingTypes.Restore:
                            op.Snapshot!.Restore();
                            AddLog(new LogItem
                            {
                                Title = op.Title,
                                Detail = string.IsNullOrEmpty(op.Detail) ? "提权续跑" : op.Detail,
                                Glyph = op.Glyph,
                            });
                            break;
                        default:
                            throw new InvalidOperationException($"未知的操作类型：{op.Type}");
                    }
                    ok++;
                }
                catch
                {
                    failed++;
                }
            }
        }
        finally
        {
            PendingOps.Clear();
            IsBusy = false;
        }

        ExplorerService.NotifyAssociationsChanged();
        ShowToast(failed == 0
            ? $"提权续跑完成：{ok} 项操作已写入"
            : $"提权续跑完成：{ok} 成功 / {failed} 失败");
        foreach (var t in Tweaks) t.Reload();
        if (IsScenePage) await LoadSceneAsync(SelectedEntry?.FullPath);
    }

    private async Task RestartExplorerAsync()
    {
        IsBusy = true;
        try
        {
            await ExplorerService.RestartAsync();
            PendingRestart = false;
            AddLog(new LogItem { Title = "已重启资源管理器", Detail = "所有更改已生效", Glyph = "\uE72C" });
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task BackupAllAsync()
    {
        IsBusy = true;
        try
        {
            var dir = await Task.Run(() => _backup.ExportAll());
            AddLog(new LogItem { Title = "完整备份已完成", Detail = dir, Glyph = "\uE8F1" });
            ExplorerService.OpenFolder(dir);
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenRegedit()
    {
        if (SelectedEntry is { } e) Try(() => ExplorerService.OpenInRegedit(e.FullPath));
    }

    private void OpenBackupFolder()
    {
        Directory.CreateDirectory(_backup.Folder);
        ExplorerService.OpenFolder(_backup.Folder);
    }

    private void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
            ShowToast("已复制到剪贴板");
        }
        catch
        {
            ShowToast("剪贴板被占用，请重试");
        }
    }

    // ================================================================= 工具

    private void AddLog(LogItem item)
    {
        Log.Insert(0, item);
        if (Log.Count > 300) Log.RemoveAt(Log.Count - 1);
        LastLog = item;
        SaveLogSafe();
    }

    /// <summary>把日志（含现场快照）落盘；失败不影响主流程。</summary>
    private void SaveLogSafe()
    {
        try
        {
            LogStore.Save(Log.ToList());
        }
        catch
        {
            // 忽略：磁盘满 / 被占用时放弃这一轮落盘
        }
    }

    private void LoadLog()
    {
        foreach (var item in LogStore.Load())
            Log.Add(item);
        LastLog = Log.FirstOrDefault();
    }

    private async void ShowToast(string text)
    {
        var version = ++_toastVersion;
        ToastText = text;
        await Task.Delay(2200);
        if (version == _toastVersion) ToastText = null;
    }

    private static bool Try(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            ShowError("没有写入权限。\n\n该注册表项可能由 TrustedInstaller 保护，需要先在注册表编辑器中获取所有权后再操作。");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
        return false;
    }

    private static void ShowError(string message)
    {
        var owner = Application.Current?.MainWindow;
        if (owner is not null)
            MessageBox.Show(owner, message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(message, "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static bool Confirm(string message)
    {
        var owner = Application.Current?.MainWindow;
        var result = owner is not null
            ? MessageBox.Show(owner, message, "请确认", MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(message, "请确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
        return result == MessageBoxResult.Yes;
    }
}
