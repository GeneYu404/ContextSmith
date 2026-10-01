using ContextSmith.Models;
using Microsoft.Win32;

namespace ContextSmith.Services;

/// <summary>
/// 读取真实注册表中的右键菜单项。
/// 注意：HKEY_CLASSES_ROOT 是 HKLM\SOFTWARE\Classes 与 HKCU\SOFTWARE\Classes 的合并视图，
/// 这里分别扫描两个 hive 并记录实际位置，写入时才能准确落到原处。
/// 同时按 64 位与 32 位（Wow6432Node）两个视图各扫一遍并合并：
/// 大量装机软件的 Shell 扩展注册在 32 位视图，只扫 64 位会整类漏掉。
/// </summary>
public sealed class MenuScanner
{
    public const string DisabledShellNew = "ShellNew_CMMDisabled";

    private static readonly RegHive[] Hives = [RegHive.LocalMachine, RegHive.CurrentUser];

    /// <summary>64 位在前：同一逻辑键同时存在于两个视图时，保留 64 位项。</summary>
    private static readonly RegistryView[] Views = [RegistryView.Registry64, RegistryView.Registry32];

    private static readonly string[] ShellNewMarkers =
        ["NullFile", "FileName", "Data", "Command", "Directory", "Handler", "ItemName"];

    private static readonly Dictionary<string, string> KnownVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["open"] = "打开",
        ["opennewwindow"] = "在新窗口中打开",
        ["opennewtab"] = "在新标签页中打开",
        ["edit"] = "编辑",
        ["print"] = "打印",
        ["printto"] = "打印到",
        ["runas"] = "以管理员身份运行",
        ["runasuser"] = "以其他用户身份运行",
        ["find"] = "搜索",
        ["explore"] = "浏览",
        ["cmd"] = "在此处打开命令窗口",
        ["powershell"] = "在此处打开 PowerShell 窗口",
        ["pintohome"] = "固定到快速访问",
        ["pintostartscreen"] = "固定到“开始”屏幕",
        ["UpdateEncryptionSettings"] = "BitLocker 加密设置",
    };

    public List<MenuEntry> Scan(SceneDefinition scene)
    {
        var list = new List<MenuEntry>();
        // 合并键：hive + 逻辑路径（不含视图），64 位先扫先占位
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (scene.Kind == SceneKind.New)
        {
            foreach (var hive in Hives)
                foreach (var view in Views)
                    ScanShellNew(list, seen, hive, view);
        }
        else if (scene.Kind == SceneKind.FileTypes)
        {
            ScanFileTypes(list, seen, scene);
        }
        else if (scene.ClassesSubPath is { } sub)
        {
            var blocked = Reg.ReadBlockedClsids();
            foreach (var hive in Hives)
            {
                var root = $@"{Reg.ClassesPrefix}\{sub}";
                foreach (var view in Views)
                {
                    ScanVerbs(list, seen, scene, hive, root, view);
                    ScanHandlers(list, seen, scene, hive, root, view, blocked);
                }
            }
        }

        return list
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.IsMicrosoft)
            .ThenBy(e => e.DisplayName, StringComparer.CurrentCulture)
            .ToList();
    }

    private static bool TryAdd(HashSet<string> seen, RegHive hive, string keyPath) =>
        seen.Add($"{hive}|{keyPath}");

    /// <summary>8 个通配场景已覆盖的 Classes 顶层键；文件类型场景跳过它们，避免与其它页重复。</summary>
    private static readonly HashSet<string> CoveredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "*", "Directory", @"Directory\Background", "DesktopBackground", "Drive",
        "AllFilesystemObjects", "Folder",
    };

    // ------------------------------------------------------------------ 全部具体文件类型

    /// <summary>
    /// 扫描所有扩展名 / ProgID 类上注册的菜单项 —— 不属于 8 个通配场景的都在这里。
    /// 其它工具禁用的项（如 InternetShortcut\Open 写了 LegacyDisable、MSEdgePDF\runas 写了
    /// ProgrammaticAccessOnly）也只有在这个场景才能被看到和恢复。
    /// MSIX 应用（AppX* 键）通过 IExplorerCommand 注册，无法用注册表管理，跳过。
    /// </summary>
    private static void ScanFileTypes(List<MenuEntry> list, HashSet<string> seen, SceneDefinition scene)
    {
        var blocked = Reg.ReadBlockedClsids();
        foreach (var hive in Hives)
        {
            foreach (var view in Views)
            {
                using var classes = SafeOpen(hive, Reg.ClassesPrefix, view);
                if (classes is null) continue;

                foreach (var name in classes.GetSubKeyNames())
                {
                    if (CoveredClasses.Contains(name)) continue;
                    if (name.StartsWith("AppX", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var root = $@"{Reg.ClassesPrefix}\{name}";
                        ScanVerbs(list, seen, scene, hive, root, view, typeTag: name);
                        ScanHandlers(list, seen, scene, hive, root, view, blocked, typeTag: name);
                    }
                    catch
                    {
                        // 单个键异常（无权限等）跳过，不影响其余类型
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ shell\<verb>

    private static void ScanVerbs(List<MenuEntry> list, HashSet<string> seen,
        SceneDefinition scene, RegHive hive, string root, RegistryView view, string? typeTag = null)
    {
        using var shell = SafeOpen(hive, root + @"\shell", view);
        if (shell is null) return;

        foreach (var name in shell.GetSubKeyNames())
        {
            try
            {
                var keyPath = $@"{root}\shell\{name}";
                if (!TryAdd(seen, hive, keyPath)) continue;

                using var k = shell.OpenSubKey(name);
                if (k is null) continue;

                var e = new MenuEntry
                {
                    Kind = EntryKind.ShellVerb,
                    Scene = scene.Kind,
                    Hive = hive,
                    KeyName = name,
                    KeyPath = keyPath,
                    View = view,
                };

                var mui = Reg.GetString(k, "MUIVerb");
                var def = Reg.GetString(k, "");
                var text = ShellText.Resolve(!string.IsNullOrWhiteSpace(mui) ? mui : def);
                e.DisplayName = text.Length > 0 ? text : KnownVerbs.GetValueOrDefault(name, name);

                using (var cmd = k.OpenSubKey("command"))
                {
                    if (cmd is not null)
                    {
                        e.Command = Reg.GetString(cmd, "");
                        if (string.IsNullOrWhiteSpace(e.Command) && Reg.GetString(cmd, "DelegateExecute") is { } de)
                            e.Command = $"(DelegateExecute) {de}";
                    }
                }
                if (string.IsNullOrWhiteSpace(e.Command) && Reg.GetString(k, "ExplorerCommandHandler") is { } ech)
                    e.Command = $"(ExplorerCommandHandler) {ech}";

                var exe = ShellText.ExeFromCommand(e.Command);
                e.IconRaw = Reg.GetString(k, "Icon");
                e.IconSource = e.IconRaw ?? exe;
                e.Vendor = ShellText.VendorOf(exe);

                e.Enabled = !Reg.HasValue(k, "LegacyDisable");
                e.ProgrammaticOnly = Reg.HasValue(k, "ProgrammaticAccessOnly");
                e.Extended = Reg.HasValue(k, "Extended");
                e.RunAsAdmin = Reg.HasValue(k, "HasLUAShield");
                e.Position = Reg.GetString(k, "Position") ?? "";
                e.HasSubMenu = Reg.HasValue(k, "SubCommands") || k.GetSubKeyNames().Contains("shell", StringComparer.OrdinalIgnoreCase);
                if (typeTag is not null) e.Extension = typeTag;

                list.Add(e);
            }
            catch
            {
                // 个别键可能没有读取权限（TrustedInstaller），跳过
            }
        }
    }

    // ------------------------------------------------------------------ shellex\ContextMenuHandlers

    private static void ScanHandlers(List<MenuEntry> list, HashSet<string> seen,
        SceneDefinition scene, RegHive hive, string root, RegistryView view, HashSet<string> blocked,
        string? typeTag = null)
    {
        using var handlers = SafeOpen(hive, root + @"\shellex\ContextMenuHandlers", view);
        if (handlers is null) return;

        using var classesRoot = Reg.ClassesRootView(view);

        foreach (var name in handlers.GetSubKeyNames())
        {
            try
            {
                var keyPath = $@"{root}\shellex\ContextMenuHandlers\{name}";
                if (!TryAdd(seen, hive, keyPath)) continue;

                using var k = handlers.OpenSubKey(name);
                if (k is null) continue;

                var def = Reg.GetString(k, "")?.Trim() ?? "";
                var defIsGuid = Reg.IsGuid(def.TrimStart('-'));
                var nameIsGuid = Reg.IsGuid(name.TrimStart('-'));

                var clsid = defIsGuid ? def.TrimStart('-') : nameIsGuid ? name.TrimStart('-') : null;
                var disabledByPrefix = defIsGuid && def.StartsWith('-');

                var e = new MenuEntry
                {
                    Kind = EntryKind.ShellEx,
                    Scene = scene.Kind,
                    Hive = hive,
                    KeyName = name,
                    KeyPath = keyPath,
                    Clsid = clsid,
                    View = view,
                    // 键名本身不是 GUID 且默认值是 GUID 时，可以只在当前位置禁用；否则只能全局屏蔽
                    Scope = defIsGuid && !nameIsGuid ? DisableScope.ThisLocation : DisableScope.Global,
                };

                string? clsidName = null;
                if (clsid is not null)
                {
                    using var ck = classesRoot.OpenSubKey($@"CLSID\{clsid}");
                    if (ck is not null)
                    {
                        clsidName = ShellText.Resolve(Reg.GetString(ck, "LocalizedString"));
                        if (clsidName.Length == 0) clsidName = ShellText.Resolve(Reg.GetString(ck, ""));
                        using var inproc = ck.OpenSubKey("InprocServer32");
                        if (inproc is not null)
                            e.DllPath = ShellText.FindFile(Environment.ExpandEnvironmentVariables(Reg.GetString(inproc, "") ?? ""));
                    }
                }

                e.DisplayName = !nameIsGuid ? name.TrimStart('-')
                    : !string.IsNullOrWhiteSpace(clsidName) ? clsidName
                    : $"未知扩展 {name}";

                e.IconSource = e.DllPath;
                e.Vendor = ShellText.VendorOf(e.DllPath);
                e.Command = e.DllPath;
                e.Enabled = !disabledByPrefix && (clsid is null || !blocked.Contains(clsid));
                if (typeTag is not null) e.Extension = typeTag;

                list.Add(e);
            }
            catch
            {
                // 跳过无权限的键
            }
        }
    }

    // ------------------------------------------------------------------ .ext\ShellNew

    private static void ScanShellNew(List<MenuEntry> list, HashSet<string> seen, RegHive hive, RegistryView view)
    {
        using var classes = SafeOpen(hive, Reg.ClassesPrefix, view);
        if (classes is null) return;

        foreach (var ext in classes.GetSubKeyNames())
        {
            if (!ext.StartsWith('.')) continue;
            try
            {
                using var ek = classes.OpenSubKey(ext);
                if (ek is null) continue;

                var progId = Reg.GetString(ek, "");
                TryAddShellNew(list, seen, hive, view, $@"{Reg.ClassesPrefix}\{ext}", ek, ext, progId);

                if (!string.IsNullOrWhiteSpace(progId))
                {
                    using var pk = ek.OpenSubKey(progId);
                    if (pk is not null)
                        TryAddShellNew(list, seen, hive, view, $@"{Reg.ClassesPrefix}\{ext}\{progId}", pk, ext, progId);

                    // ProgID 顶层键上的 ShellNew（如 txtfile\ShellNew）：扩展名键没有 ShellNew 时，
                    // 新建菜单项往往只挂在这里，只扫 .ext 键会整类漏掉
                    using var top = classes.OpenSubKey(progId);
                    if (top is not null)
                        TryAddShellNew(list, seen, hive, view, $@"{Reg.ClassesPrefix}\{progId}", top, ext, progId);
                }
            }
            catch
            {
                // 忽略
            }
        }

        // 非扩展名类自己的 ShellNew（Folder\ShellNew、特殊对象的 ShellNew 等）
        foreach (var name in classes.GetSubKeyNames())
        {
            if (name.StartsWith('.') ||
                name.StartsWith("AppX", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var ck = classes.OpenSubKey(name);
                if (ck is null) continue;
                TryAddShellNew(list, seen, hive, view, $@"{Reg.ClassesPrefix}\{name}", ck, name, name);
            }
            catch
            {
                // 忽略
            }
        }
    }

    private static void TryAddShellNew(List<MenuEntry> list, HashSet<string> seen,
        RegHive hive, RegistryView view, string parentPath, RegistryKey parent, string ext, string? progId)
    {
        foreach (var (sub, enabled) in new[] { ("ShellNew", true), (DisabledShellNew, false) })
        {
            using var sn = parent.OpenSubKey(sub);
            if (sn is null) continue;

            var keyPath = $@"{parentPath}\{sub}";
            if (!TryAdd(seen, hive, keyPath)) continue;

            var values = sn.GetValueNames();
            if (!ShellNewMarkers.Any(m => values.Contains(m, StringComparer.OrdinalIgnoreCase))) continue;

            string name = ShellText.Resolve(Reg.GetString(sn, "ItemName"));
            string? icon = null;

            if (!string.IsNullOrWhiteSpace(progId))
            {
                using var classesRoot = Reg.ClassesRootView(view);
                using var pk = classesRoot.OpenSubKey(progId);
                if (pk is not null)
                {
                    if (name.Length == 0) name = ShellText.Resolve(Reg.GetString(pk, "FriendlyTypeName"));
                    if (name.Length == 0) name = ShellText.Resolve(Reg.GetString(pk, ""));
                    using var di = pk.OpenSubKey("DefaultIcon");
                    if (di is not null) icon = Reg.GetString(di, "");
                }
            }

            list.Add(new MenuEntry
            {
                Kind = EntryKind.ShellNew,
                Scene = SceneKind.New,
                Hive = hive,
                KeyName = sub,
                KeyPath = keyPath,
                View = view,
                Extension = ext,
                DisplayName = name.Length > 0 ? name : $"{ext} 文件",
                IconSource = icon,
                Command = values.Contains("Command", StringComparer.OrdinalIgnoreCase)
                    ? Reg.GetString(sn, "Command")
                    : values.Contains("FileName", StringComparer.OrdinalIgnoreCase)
                        ? $"模板：{Reg.GetString(sn, "FileName")}"
                        : "空白文件 (NullFile)",
                Enabled = enabled,
            });
        }
    }

    private static RegistryKey? SafeOpen(RegHive hive, string path, RegistryView view)
    {
        try
        {
            return Reg.Open(hive, path, view: view);
        }
        catch
        {
            return null;
        }
    }
}
