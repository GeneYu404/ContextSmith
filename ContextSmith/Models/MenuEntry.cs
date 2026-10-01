using Microsoft.Win32;

namespace ContextSmith.Models;

public enum EntryKind
{
    /// <summary>shell\&lt;verb&gt; —— 普通命令项</summary>
    ShellVerb,
    /// <summary>shellex\ContextMenuHandlers\&lt;name&gt; —— COM 外壳扩展（7-Zip、WinRAR 等）</summary>
    ShellEx,
    /// <summary>.ext\ShellNew —— “新建”子菜单项</summary>
    ShellNew,
}

public enum RegHive
{
    LocalMachine,
    CurrentUser,
}

/// <summary>ShellEx 项的禁用方式</summary>
public enum DisableScope
{
    /// <summary>在默认值 CLSID 前加 "-"，只影响当前位置</summary>
    ThisLocation,
    /// <summary>写入 Shell Extensions\Blocked，对该 CLSID 全局生效</summary>
    Global,
}

/// <summary>
/// 一个真实存在于注册表中的右键菜单项。
/// KeyPath 始终是某个具体 hive 下的实际路径，而不是 HKCR 合并视图，
/// 这样写入/删除时不会落到错误的位置。
/// </summary>
public sealed class MenuEntry
{
    public required EntryKind Kind { get; init; }
    public required SceneKind Scene { get; init; }
    public required RegHive Hive { get; init; }

    /// <summary>相对 hive 根的路径，例如 SOFTWARE\Classes\*\shell\VSCode</summary>
    public required string KeyPath { get; set; }
    public required string KeyName { get; set; }

    public string DisplayName { get; set; } = "";
    public string? Command { get; set; }
    public string? Clsid { get; set; }
    public string? DllPath { get; set; }
    public string? IconSource { get; set; }
    public string? Extension { get; set; }
    public string? Vendor { get; set; }

    public bool Enabled { get; set; }
    public bool Extended { get; set; }
    public string Position { get; set; } = "";
    public bool ProgrammaticOnly { get; set; }
    public bool HasSubMenu { get; set; }
    public DisableScope Scope { get; set; } = DisableScope.ThisLocation;

    /// <summary>条目所在的注册表视图（64 位 / 32 位 Wow6432Node），读写备份都必须回到同一视图。</summary>
    public RegistryView View { get; set; } = RegistryView.Registry64;
    public bool Is32Bit => View == RegistryView.Registry32;

    /// <summary>显式写入的 Icon 值；与"从命令推导的图标"区分开，编辑时才不会误写。</summary>
    public string? IconRaw { get; set; }

    /// <summary>HasLUAShield：命令项显示 UAC 盾牌。</summary>
    public bool RunAsAdmin { get; set; }

    public bool IsMicrosoft =>
        Vendor?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true;

    public string HiveName => Hive == RegHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";

    public string FullPath => $@"{HiveName}\{KeyPath}";

    public string ParentPath => KeyPath[..KeyPath.LastIndexOf('\\')];
}
