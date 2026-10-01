using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace ContextSmith.Services;

/// <summary>
/// 权限策略（移植自 A 组设计）：默认 asInvoker，只在确实要写 HKLM 时才请求提权重启。
/// 好处是绝大多数操作（扫描、备份、写 HKCU）全程无 UAC 弹窗；
/// 提权重启会丢掉当前窗口状态，所以待执行队列先写进临时文件，由提权后的实例续跑。
/// </summary>
public static class Elevation
{
    private static bool? _cached;

    public static bool IsElevated
    {
        get
        {
            if (_cached.HasValue) return _cached.Value;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity!);
                _cached = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                _cached = false;
            }
            return _cached.Value;
        }
    }

    /// <summary>重启为管理员实例。返回 true 表示调用方应当退出当前进程。</summary>
    public static bool TryRestartElevated(string? resumeToken = null)
    {
        try
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return false;

            var args = "--resume-from-elevation";
            if (!string.IsNullOrEmpty(resumeToken)) args += $" \"{resumeToken}\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(exe),
            });
            return true;
        }
        catch
        {
            // 用户在 UAC 里点了"否"
            return false;
        }
    }

    /// <summary>挂起操作的队列文件，提权后的实例读取并逐条重放。</summary>
    public static string PendingFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ContextSmith", "pending-elevation.json");
}
