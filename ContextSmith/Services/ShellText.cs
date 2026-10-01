using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace ContextSmith.Services;

internal static partial class ShellText
{
    private static readonly ConcurrentDictionary<string, string> VendorCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>解析 MUIVerb / 默认值：支持 "@dll,-id" 间接字符串，并去掉 "&amp;" 快捷键标记。</summary>
    public static string Resolve(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();

        if (s.StartsWith('@'))
        {
            try
            {
                var sb = new StringBuilder(1024);
                if (Native.SHLoadIndirectString(s, sb, sb.Capacity, IntPtr.Zero) == 0)
                    s = sb.ToString();
            }
            catch
            {
                // 解析失败就保留原文
            }
        }

        s = AccelParen().Replace(s, "");
        s = s.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");
        return s.Trim();
    }

    /// <summary>从命令行中取出可执行文件路径（去掉引号并展开环境变量）</summary>
    public static string? ExeFromCommand(string? command)
    {
        var (exe, _) = SplitCommand(command);
        if (exe is null) return null;
        var expanded = Environment.ExpandEnvironmentVariables(exe);
        return FindFile(expanded);
    }

    /// <summary>把命令拆成 (可执行文件, 其余参数)，不展开环境变量</summary>
    public static (string? exe, string args) SplitCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return (null, "");
        var s = command.Trim();
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end <= 1) return (null, "");
            return (s[1..end], s[(end + 1)..].Trim());
        }
        var space = s.IndexOf(' ');
        return space > 0 ? (s[..space], s[(space + 1)..].Trim()) : (s, "");
    }

    /// <summary>把相对文件名（notepad.exe）补全为 System32 / Windows 下的完整路径</summary>
    public static string? FindFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Trim('"');
        try
        {
            if (Path.IsPathRooted(p)) return File.Exists(p) ? p : null;
            foreach (var dir in new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
            {
                var full = Path.Combine(dir, p);
                if (File.Exists(full)) return full;
                if (!p.Contains('.') && File.Exists(full + ".exe")) return full + ".exe";
            }
        }
        catch
        {
            // 非法路径字符等
        }
        return null;
    }

    /// <summary>读取文件版本信息中的公司名称，用于区分系统项与第三方项</summary>
    public static string? VendorOf(string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        return VendorCache.GetOrAdd(file, f =>
        {
            try
            {
                return File.Exists(f) ? FileVersionInfo.GetVersionInfo(f).CompanyName?.Trim() ?? "" : "";
            }
            catch
            {
                return "";
            }
        }) is { Length: > 0 } v ? v : null;
    }

    [GeneratedRegex(@"\(&[^)]\)")]
    private static partial Regex AccelParen();
}
