using System.IO;
using System.Windows;
using System.Windows.Media;
using ContextSmith.Models;
using ContextSmith.Services;

namespace ContextSmith.ViewModels;

public sealed class MenuEntryViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    public MenuEntryViewModel(MenuEntry model, MainViewModel owner)
    {
        Model = model;
        _owner = owner;
        Icon = IconLoader.Load(model.IconSource);
    }

    public MenuEntry Model { get; }

    public ImageSource? Icon { get; private set; }
    public bool HasIcon => Icon is not null;

    public string FallbackGlyph => Model.Kind switch
    {
        EntryKind.ShellEx => "\uE943",
        EntryKind.ShellNew => "\uE8A5",
        _ => "\uE756",
    };

    public string Name => Model.DisplayName;

    public string Subtitle => Model.Kind switch
    {
        EntryKind.ShellEx => $"{Model.Clsid ?? Model.KeyName}  ·  {Model.DllPath ?? "未找到 DLL"}",
        EntryKind.ShellNew => $"{Model.Extension}  ·  {Model.Command}",
        _ => string.IsNullOrWhiteSpace(Model.Command) ? $"shell\\{Model.KeyName}" : Model.Command!,
    };

    public string KindLabel => Model.Kind switch
    {
        EntryKind.ShellVerb => "Shell 命令",
        EntryKind.ShellEx => "外壳扩展",
        _ => "新建菜单",
    };

    public string HiveLabel => Model.Hive == RegHive.LocalMachine ? "HKLM" : "HKCU";

    public string SourceLabel => Model.IsMicrosoft ? "系统" : Model.Vendor is null ? "未知" : "第三方";

    /// <summary>
    /// 所属程序：Shell 命令按命令行里的 exe、外壳扩展按 DLL 归类。
    /// 文件类型场景条目上千，靠它分组、筛选和搜索。
    /// </summary>
    public string Program
    {
        get
        {
            string? file = Model.Kind switch
            {
                EntryKind.ShellVerb => ShellText.ExeFromCommand(Model.Command),
                EntryKind.ShellEx => Model.DllPath,
                _ => null,
            };
            if (file is { Length: > 0 })
            {
                var name = Path.GetFileName(file.Trim().Trim('"'));
                if (name.Length > 0) return name;
            }

            // 可执行文件已不存在时 ExeFromCommand 返回 null，退回按原始命令首段归类
            var (exe, _) = ShellText.SplitCommand(Model.Command);
            if (exe is { Length: > 0 })
            {
                var raw = Path.GetFileName(Environment.ExpandEnvironmentVariables(exe).Trim().Trim('"'));
                if (raw.Length > 0) return raw;
            }

            if (Model.Kind == EntryKind.ShellNew) return "新建菜单模板";
            return "无命令 / 未知程序";
        }
    }

    public bool IsShellVerb => Model.Kind == EntryKind.ShellVerb;
    public bool CanDelete => Model.Kind == EntryKind.ShellVerb;
    public bool IsGlobalScope => Model.Kind == EntryKind.ShellEx && Model.Scope == DisableScope.Global;

    public IReadOnlyList<string> Badges
    {
        get
        {
            var list = new List<string> { SourceLabel, HiveLabel };
            if (Model.Kind != EntryKind.ShellNew && Model.Extension is { Length: > 0 } type)
                list.Add(type);  // 文件类型场景：显示所属扩展名 / ProgID
            if (Model.Extended) list.Add("Shift");
            if (Model.Position == "Top") list.Add("置顶");
            if (Model.Position == "Bottom") list.Add("置底");
            if (Model.HasSubMenu) list.Add("子菜单");
            if (Model.ProgrammaticOnly) list.Add("仅程序调用");
            if (IsGlobalScope) list.Add("全局屏蔽");
            if (Model.Is32Bit) list.Add("32 位");
            if (Model.RunAsAdmin) list.Add("盾牌");
            return list;
        }
    }

    public string ScopeNote => Model.Kind switch
    {
        EntryKind.ShellVerb => "禁用时写入 LegacyDisable 值，不删除任何数据。",
        EntryKind.ShellEx when Model.Scope == DisableScope.ThisLocation =>
            "禁用时在默认值 CLSID 前加 “-”，只影响当前位置。",
        EntryKind.ShellEx =>
            "该扩展的键名就是 CLSID，只能写入 Shell Extensions\\Blocked 全局屏蔽，其它位置的同一扩展也会一起消失。",
        _ => $"禁用时把 ShellNew 重命名为 {MenuScanner.DisabledShellNew}，模板数据完整保留。",
    };

    public string FullPath => Model.FullPath;
    public string? Command => Model.Command;
    public string? Clsid => Model.Clsid;
    public string? Vendor => Model.Vendor;

    public bool Enabled
    {
        get => Model.Enabled;
        set
        {
            if (value == Model.Enabled) return;
            if (!_owner.TryApplyEnabled(this, value))
            {
                // 写入失败或已排队等待提权：延后通知，让开关回弹到真实状态
                Application.Current.Dispatcher.InvokeAsync(() => OnPropertyChanged(nameof(Enabled)));
                return;
            }
            OnPropertyChanged();
            BumpFlash();
        }
    }

    public bool Extended
    {
        get => Model.Extended;
        set
        {
            if (value == Model.Extended) return;
            if (_owner.TryApplyExtended(this, value))
            {
                Refresh();
                BumpFlash();
            }
            else Application.Current.Dispatcher.InvokeAsync(() => OnPropertyChanged(nameof(Extended)));
        }
    }

    /// <summary>0 默认，1 顶部，2 底部</summary>
    public int PositionIndex
    {
        get => Model.Position switch { "Top" => 1, "Bottom" => 2, _ => 0 };
        set
        {
            var pos = value switch { 1 => "Top", 2 => "Bottom", _ => "" };
            if (pos == Model.Position) return;
            if (_owner.TryApplyPosition(this, pos)) Refresh();
            else Application.Current.Dispatcher.InvokeAsync(() => OnPropertyChanged(nameof(PositionIndex)));
        }
    }

    /// <summary>模型被外部修改（撤销、批量操作、编辑）后刷新所有绑定与图标</summary>
    public void Refresh()
    {
        Icon = IconLoader.Load(Model.IconSource);
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(HasIcon));
        OnPropertyChanged(string.Empty);
    }

    /// <summary>写入成功后的行闪光令牌（Fx 动画按当前启用状态取色）</summary>
    public int FlashToken { get; private set; }

    public void BumpFlash()
    {
        FlashToken++;
        OnPropertyChanged(nameof(FlashToken));
    }
}

public sealed class TweakViewModel : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _isOn;

    public TweakViewModel(TweakDefinition def, MainViewModel owner)
    {
        Definition = def;
        _owner = owner;
        Reload();
    }

    public TweakDefinition Definition { get; }
    public string Title => Definition.Title;
    public string Description => Definition.Description;
    public string Glyph => Definition.Glyph;

    public bool IsApplicable => !Definition.Win11Only || TweakService.IsWindows11;

    public string Tags
    {
        get
        {
            var tags = new List<string> { Definition.Win11Only ? "Win11" : "Win10 / Win11" };
            if (Definition.TouchesHklm) tags.Add("影响所有用户");
            tags.Add(Definition.Restart switch
            {
                RestartNeed.Explorer => "重启资源管理器生效",
                RestartNeed.Logoff => "重新登录生效",
                _ => "立即生效",
            });
            return string.Join("  ·  ", tags);
        }
    }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (value == _isOn) return;
            if (_owner.TryApplyTweak(this, value))
            {
                Set(ref _isOn, value);
            }
            else
            {
                Application.Current.Dispatcher.InvokeAsync(() => OnPropertyChanged(nameof(IsOn)));
            }
        }
    }

    public void Reload()
    {
        try
        {
            _isOn = Definition.IsOn();
        }
        catch
        {
            _isOn = false;
        }
        OnPropertyChanged(nameof(IsOn));
    }
}

public sealed class LogItem : ObservableObject
{
    private bool _undone;

    public DateTime Time { get; init; } = DateTime.Now;
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public string Glyph { get; init; } = "\uE73E";

    /// <summary>会话内撤销闭包（ShellNew 重命名、系统优化等无法跨重启的动作）。</summary>
    public Action? Undo { get; init; }

    /// <summary>写入前的现场快照；随日志落盘，重启后仍可撤销。</summary>
    public RegistrySnapshot? Snapshot { get; set; }

    public string? BackupFile { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss");

    public bool Undone
    {
        get => _undone;
        set
        {
            if (Set(ref _undone, value)) OnPropertyChanged(nameof(CanUndo));
        }
    }

    public bool CanUndo => !Undone && (Undo is not null || Snapshot is not null);
}

public enum NavKind
{
    Scene,
    Tweaks,
    Log,
}

public sealed class NavItem : ObservableObject
{
    private string _count = "";

    public required NavKind Kind { get; init; }
    public required string Title { get; init; }
    public required string Glyph { get; init; }
    public SceneDefinition? Scene { get; init; }
    public bool StartsGroup { get; init; }

    public string Count
    {
        get => _count;
        set => Set(ref _count, value);
    }
}
