using ContextSmith.Models;
using Microsoft.Win32;

namespace ContextSmith.Services;

/// <summary>
/// 注册表访问的统一入口。所有 API 显式指定注册表视图（默认 64 位），
/// 保证扫描与写入落在同一视图，不被 Wow6432Node 重定向带偏。
/// </summary>
internal static class Reg
{
    public const string ClassesPrefix = @"SOFTWARE\Classes";

    public const string BlockedPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked";

    public static RegistryKey Base(RegHive hive, RegistryView view = RegistryView.Registry64) =>
        RegistryKey.OpenBaseKey(
            hive == RegHive.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
            view);

    /// <summary>HKCR 合并视图的基键（按位打开，用于 CLSID / ProgID 解析）。</summary>
    public static RegistryKey ClassesRootView(RegistryView view) =>
        RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);

    public static RegistryKey? Open(RegHive hive, string path, bool writable = false,
        RegistryView view = RegistryView.Registry64)
    {
        using var root = Base(hive, view);
        return root.OpenSubKey(path, writable);
    }

    public static RegistryKey OpenWritableOrThrow(RegHive hive, string path,
        RegistryView view = RegistryView.Registry64) =>
        Open(hive, path, writable: true, view)
        ?? throw new InvalidOperationException($"找不到注册表项：{hive}\\{path}（可能已被其它程序删除，请刷新）");

    public static RegistryKey Create(RegHive hive, string path, RegistryView view = RegistryView.Registry64)
    {
        using var root = Base(hive, view);
        return root.CreateSubKey(path, writable: true);
    }

    /// <summary>读取字符串值；name 为空字符串表示默认值。不展开环境变量。</summary>
    public static string? GetString(RegistryKey key, string name) =>
        key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString();

    public static bool HasValue(RegistryKey key, string name) =>
        key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase);

    public static bool IsGuid(string? s) =>
        !string.IsNullOrWhiteSpace(s) && s.Trim().StartsWith('{') && Guid.TryParse(s.Trim(), out _);

    /// <summary>读取 HKLM 与 HKCU 中所有被屏蔽的 Shell 扩展 CLSID</summary>
    public static HashSet<string> ReadBlockedClsids()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegHive.LocalMachine, RegHive.CurrentUser })
        {
            try
            {
                using var k = Open(hive, BlockedPath);
                if (k is null) continue;
                foreach (var n in k.GetValueNames()) set.Add(n.Trim());
            }
            catch
            {
                // 无权限读取时忽略
            }
        }
        return set;
    }
}
