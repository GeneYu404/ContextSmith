using System.ComponentModel;
using System.Text.RegularExpressions;
using ContextSmith.Models;
using Microsoft.Win32;

namespace ContextSmith.Services;

public sealed record AddVerbRequest(
    SceneDefinition Scene,
    string KeyName,
    string DisplayName,
    string Command,
    string? Icon,
    bool Extended,
    bool RunAsAdmin,
    string Position);

/// <summary>编辑现有 shell 动词的请求。</summary>
public sealed record EditVerbRequest(
    string DisplayName,
    string Command,
    string? Icon,
    bool Extended,
    bool RunAsAdmin,
    string Position);

/// <summary>
/// 所有写注册表的操作都在这里，每次写入前自动备份。
/// 采用非破坏式禁用：shell 动词写 LegacyDisable，外壳扩展改 CLSID 前缀或写入 Blocked，
/// ShellNew 重命名为 ShellNew_CMMDisabled —— 原始数据始终保留，可随时恢复。
/// </summary>
public sealed class MenuWriter(BackupService backup)
{
    public string? SetEnabled(MenuEntry e, bool enabled)
    {
        var file = backup.Snapshot(e);
        switch (e.Kind)
        {
            case EntryKind.ShellVerb:
                SetVerbEnabled(e, enabled);
                break;
            case EntryKind.ShellEx:
                SetHandlerEnabled(e, enabled);
                break;
            case EntryKind.ShellNew:
                SetShellNewEnabled(e, enabled);
                break;
        }
        e.Enabled = enabled;
        ExplorerService.NotifyAssociationsChanged();
        return file;
    }

    private static void SetVerbEnabled(MenuEntry e, bool enabled)
    {
        using var k = Reg.OpenWritableOrThrow(e.Hive, e.KeyPath, e.View);
        if (enabled) k.DeleteValue("LegacyDisable", throwOnMissingValue: false);
        else k.SetValue("LegacyDisable", "", RegistryValueKind.String);
    }

    private static void SetHandlerEnabled(MenuEntry e, bool enabled)
    {
        using (var k = Reg.OpenWritableOrThrow(e.Hive, e.KeyPath, e.View))
        {
            var def = Reg.GetString(k, "")?.Trim() ?? "";
            if (e.Scope == DisableScope.ThisLocation && Reg.IsGuid(def.TrimStart('-')))
            {
                var clean = def.TrimStart('-');
                k.SetValue("", enabled ? clean : "-" + clean, RegistryValueKind.String);
            }
        }

        if (e.Clsid is null) return;

        if (enabled)
        {
            // 启用时把两个 hive 中的屏蔽记录都清掉
            foreach (var hive in new[] { RegHive.LocalMachine, RegHive.CurrentUser })
            {
                using var b = Reg.Open(hive, Reg.BlockedPath, writable: true);
                b?.DeleteValue(e.Clsid, throwOnMissingValue: false);
            }
        }
        else if (e.Scope == DisableScope.Global)
        {
            using var b = Reg.Create(RegHive.LocalMachine, Reg.BlockedPath);
            b.SetValue(e.Clsid, "", RegistryValueKind.String);
        }
    }

    private static void SetShellNewEnabled(MenuEntry e, bool enabled)
    {
        var from = enabled ? MenuScanner.DisabledShellNew : "ShellNew";
        var to = enabled ? "ShellNew" : MenuScanner.DisabledShellNew;
        if (string.Equals(e.KeyName, to, StringComparison.OrdinalIgnoreCase)) return;

        var parentPath = e.ParentPath;
        using var parent = Reg.OpenWritableOrThrow(e.Hive, parentPath, e.View);
        using (var existing = parent.OpenSubKey(to))
        {
            if (existing is not null)
                throw new InvalidOperationException($"{parentPath}\\{to} 已存在，无法切换。请先在注册表中处理重复项。");
        }

        var rc = Native.RegRenameKey(parent.Handle, from, to);
        if (rc != 0) throw new Win32Exception(rc);

        e.KeyName = to;
        e.KeyPath = $@"{parentPath}\{to}";
        ClearShellNewCache();
    }

    /// <summary>资源管理器会缓存"新建"菜单，清掉缓存后才会立刻反映变化</summary>
    private static void ClearShellNewCache()
    {
        try
        {
            using var k = Reg.Open(RegHive.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew", writable: true);
            k?.DeleteValue("Classes", throwOnMissingValue: false);
        }
        catch
        {
            // 忽略
        }
    }

    public void SetExtended(MenuEntry e, bool extended)
    {
        backup.Snapshot(e);
        using var k = Reg.OpenWritableOrThrow(e.Hive, e.KeyPath, e.View);
        if (extended) k.SetValue("Extended", "", RegistryValueKind.String);
        else k.DeleteValue("Extended", throwOnMissingValue: false);
        e.Extended = extended;
        ExplorerService.NotifyAssociationsChanged();
    }

    public void SetPosition(MenuEntry e, string position)
    {
        backup.Snapshot(e);
        using var k = Reg.OpenWritableOrThrow(e.Hive, e.KeyPath, e.View);
        if (string.IsNullOrEmpty(position)) k.DeleteValue("Position", throwOnMissingValue: false);
        else k.SetValue("Position", position, RegistryValueKind.String);
        e.Position = position;
        ExplorerService.NotifyAssociationsChanged();
    }

    public void Rename(MenuEntry e, string displayName)
    {
        backup.Snapshot(e);
        using var k = Reg.OpenWritableOrThrow(e.Hive, e.KeyPath, e.View);
        k.SetValue("MUIVerb", displayName, RegistryValueKind.String);
        e.DisplayName = displayName;
        ExplorerService.NotifyAssociationsChanged();
    }

    /// <summary>删除一个 shell 动词。返回备份文件路径，用于撤销。</summary>
    public string? Delete(MenuEntry e)
    {
        if (e.Kind != EntryKind.ShellVerb)
            throw new InvalidOperationException("只允许删除 Shell 命令项；外壳扩展与新建项请使用禁用。");

        var file = backup.Snapshot(e)
            ?? throw new InvalidOperationException("备份失败，已取消删除以免数据丢失。");

        using var parent = Reg.OpenWritableOrThrow(e.Hive, e.ParentPath, e.View);
        parent.DeleteSubKeyTree(e.KeyName, throwOnMissingSubKey: false);
        ExplorerService.NotifyAssociationsChanged();
        return file;
    }

    /// <summary>新增自定义命令，写入 HKCU（仅当前用户）</summary>
    public MenuEntry AddVerb(AddVerbRequest r)
    {
        if (r.Scene.ClassesSubPath is null)
            throw new InvalidOperationException("该场景不支持添加自定义命令。");

        var path = $@"{Reg.ClassesPrefix}\{r.Scene.ClassesSubPath}\shell\{r.KeyName}";
        using (var existing = Reg.Open(RegHive.CurrentUser, path))
        {
            if (existing is not null) throw new InvalidOperationException($"键名「{r.KeyName}」已存在。");
        }

        var command = r.RunAsAdmin ? Elevate(r.Command) : r.Command;

        using (var k = Reg.Create(RegHive.CurrentUser, path))
        {
            k.SetValue("MUIVerb", r.DisplayName, RegistryValueKind.String);
            if (!string.IsNullOrWhiteSpace(r.Icon)) k.SetValue("Icon", r.Icon, RegistryValueKind.String);
            if (r.Extended) k.SetValue("Extended", "", RegistryValueKind.String);
            if (r.RunAsAdmin) k.SetValue("HasLUAShield", "", RegistryValueKind.String);
            if (!string.IsNullOrEmpty(r.Position)) k.SetValue("Position", r.Position, RegistryValueKind.String);

            using var c = k.CreateSubKey("command", writable: true);
            c.SetValue("", command, RegistryValueKind.String);
        }

        ExplorerService.NotifyAssociationsChanged();

        return new MenuEntry
        {
            Kind = EntryKind.ShellVerb,
            Scene = r.Scene.Kind,
            Hive = RegHive.CurrentUser,
            KeyName = r.KeyName,
            KeyPath = path,
            DisplayName = r.DisplayName,
            Command = command,
            IconSource = string.IsNullOrWhiteSpace(r.Icon) ? ShellText.ExeFromCommand(r.Command) : r.Icon,
            Enabled = true,
            Extended = r.Extended,
            Position = r.Position,
        };
    }

    /// <summary>
    /// 编辑现有 shell 动词：显示名称 / 命令 / 图标 / Shift / 位置 / 管理员包装，一次写完。
    /// "以管理员身份运行"勾选状态会幂等地包装或还原命令，并同步 HasLUAShield 盾牌。
    /// </summary>
    public void EditVerb(MenuEntry e, EditVerbRequest r)
    {
        if (e.Kind != EntryKind.ShellVerb)
            throw new InvalidOperationException("只有 Shell 命令项支持编辑。");

        backup.Snapshot(e);
        var command = r.RunAsAdmin ? Elevate(StripElevate(r.Command)) : StripElevate(r.Command);

        using var k = Reg.OpenWritableOrThrow(e.Hive, e.KeyPath, e.View);
        k.SetValue("MUIVerb", r.DisplayName, RegistryValueKind.String);

        if (string.IsNullOrWhiteSpace(r.Icon)) k.DeleteValue("Icon", throwOnMissingValue: false);
        else k.SetValue("Icon", r.Icon.Trim(), RegistryValueKind.String);

        if (r.Extended) k.SetValue("Extended", "", RegistryValueKind.String);
        else k.DeleteValue("Extended", throwOnMissingValue: false);

        if (string.IsNullOrEmpty(r.Position)) k.DeleteValue("Position", throwOnMissingValue: false);
        else k.SetValue("Position", r.Position, RegistryValueKind.String);

        if (r.RunAsAdmin) k.SetValue("HasLUAShield", "", RegistryValueKind.String);
        else k.DeleteValue("HasLUAShield", throwOnMissingValue: false);

        using (var c = k.CreateSubKey("command", writable: true))
            c.SetValue("", command, RegistryValueKind.String);

        e.DisplayName = r.DisplayName;
        e.Command = command;
        e.IconRaw = string.IsNullOrWhiteSpace(r.Icon) ? null : r.Icon.Trim();
        e.IconSource = e.IconRaw ?? ShellText.ExeFromCommand(command);
        e.Extended = r.Extended;
        e.Position = r.Position;
        e.RunAsAdmin = r.RunAsAdmin;
        ExplorerService.NotifyAssociationsChanged();
    }

    /// <summary>
    /// 剥掉本程序生成的 PowerShell 提权包装，还原用户原始命令（幂等，可反复包装/还原）。
    /// </summary>
    public static string StripElevate(string command)
    {
        if (!command.Contains("Start-Process -Verb RunAs", StringComparison.Ordinal))
            return command;

        var m = Regex.Match(command, @"-FilePath '(.+?)'(?:\s+-ArgumentList '(.+?)')?", RegexOptions.Singleline);
        if (!m.Success) return command;

        var path = m.Groups[1].Value.Replace("''", "'");
        var args = m.Groups[2].Success ? m.Groups[2].Value.Replace("''", "'") : "";
        return args.Length > 0 ? $"\"{path}\" {args}" : $"\"{path}\"";
    }

    /// <summary>
    /// 通过 PowerShell Start-Process -Verb RunAs 提权执行。
    /// 已知限制：被右键的路径中若含有单引号，参数会被截断。
    /// </summary>
    public static string Elevate(string command)
    {
        var (exe, args) = ShellText.SplitCommand(command);
        exe ??= command;
        var argPart = args.Length > 0 ? $" -ArgumentList '{args.Replace("'", "''")}'" : "";
        var inner = $"Start-Process -Verb RunAs -FilePath '{exe.Replace("'", "''")}'{argPart}";
        return $"powershell.exe -NoProfile -WindowStyle Hidden -Command \"{inner.Replace("\"", "\\\"")}\"";
    }
}
