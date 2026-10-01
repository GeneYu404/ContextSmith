using ContextSmith.Models;
using Microsoft.Win32;

namespace ContextSmith.Services;

public enum RestartNeed
{
    None,
    Explorer,
    Logoff,
}

public sealed record TweakDefinition(
    string Id,
    string Title,
    string Description,
    string Glyph,
    bool Win11Only,
    bool TouchesHklm,
    RestartNeed Restart,
    Func<bool> IsOn,
    Action<bool> Apply);

public static class TweakService
{
    private const string ClassicMenuKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
    private const string ModernShareClsid = "{E2BF9676-5F8F-435C-97EB-11607A5BEDF7}";
    private const string ShellIconsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons";
    private const string NamingKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\NamingTemplates";

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    public static readonly IReadOnlyList<TweakDefinition> All =
    [
        new(
            "classic-menu",
            "恢复 Win10 经典右键菜单",
            "Win11 默认把第三方菜单折叠进“显示更多选项”。开启后直接显示完整的经典菜单（非微软官方开关，大版本更新后可能失效，届时仍可使用 Shift+右键）。",
            "\uE7A7",
            Win11Only: true,
            TouchesHklm: false,
            RestartNeed.Explorer,
            IsOn: () =>
            {
                using var k = Reg.Open(RegHive.CurrentUser, ClassicMenuKey + @"\InprocServer32");
                return k is not null;
            },
            Apply: on =>
            {
                if (on)
                {
                    using var k = Reg.Create(RegHive.CurrentUser, ClassicMenuKey + @"\InprocServer32");
                    // 默认值必须存在且为空字符串
                    k.SetValue("", "", RegistryValueKind.String);
                }
                else
                {
                    using var root = Reg.Base(RegHive.CurrentUser);
                    root.DeleteSubKeyTree(ClassicMenuKey, throwOnMissingSubKey: false);
                }
            }),

        new(
            "menu-delay",
            "加快子菜单弹出速度",
            "把子菜单展开延迟从默认 400ms 缩短为 100ms。",
            "\uE945",
            Win11Only: false,
            TouchesHklm: false,
            RestartNeed.Logoff,
            IsOn: () =>
            {
                using var k = Reg.Open(RegHive.CurrentUser, @"Control Panel\Desktop");
                return k is not null && int.TryParse(Reg.GetString(k, "MenuShowDelay"), out var v) && v < 400;
            },
            Apply: on =>
            {
                using var k = Reg.Create(RegHive.CurrentUser, @"Control Panel\Desktop");
                k.SetValue("MenuShowDelay", on ? "100" : "400", RegistryValueKind.String);
            }),

        new(
            "shortcut-suffix",
            "去掉“ - 快捷方式”后缀",
            "右键“创建快捷方式”时不再自动在名称后追加“ - 快捷方式”。",
            "\uE71B",
            Win11Only: false,
            TouchesHklm: false,
            RestartNeed.Explorer,
            IsOn: () =>
            {
                using var k = Reg.Open(RegHive.CurrentUser, NamingKey);
                return k is not null && Reg.HasValue(k, "ShortcutNameTemplate");
            },
            Apply: on =>
            {
                using var k = Reg.Create(RegHive.CurrentUser, NamingKey);
                if (on) k.SetValue("ShortcutNameTemplate", "\"%s.lnk\"", RegistryValueKind.String);
                else k.DeleteValue("ShortcutNameTemplate", throwOnMissingValue: false);
            }),

        new(
            "shortcut-arrow",
            "隐藏快捷方式小箭头",
            "去掉快捷方式图标左下角的箭头（写入 HKLM，影响所有用户）。",
            "\uE8AD",
            Win11Only: false,
            TouchesHklm: true,
            RestartNeed.Explorer,
            IsOn: () =>
            {
                using var k = Reg.Open(RegHive.LocalMachine, ShellIconsKey);
                return k is not null && Reg.HasValue(k, "29");
            },
            Apply: on =>
            {
                using var k = Reg.Create(RegHive.LocalMachine, ShellIconsKey);
                if (on) k.SetValue("29", @"%windir%\System32\shell32.dll,-50", RegistryValueKind.ExpandString);
                else k.DeleteValue("29", throwOnMissingValue: false);
            }),

        new(
            "hide-share",
            "隐藏“共享”菜单项",
            "屏蔽现代共享扩展，Win11 新式菜单顶部的“共享”按钮也会一起消失。",
            "\uE72D",
            Win11Only: false,
            TouchesHklm: true,
            RestartNeed.Explorer,
            IsOn: () => Reg.ReadBlockedClsids().Contains(ModernShareClsid),
            Apply: on =>
            {
                using var k = Reg.Create(RegHive.LocalMachine, Reg.BlockedPath);
                if (on) k.SetValue(ModernShareClsid, "", RegistryValueKind.String);
                else k.DeleteValue(ModernShareClsid, throwOnMissingValue: false);
            }),
    ];
}
