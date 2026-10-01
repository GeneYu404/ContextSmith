using System.Diagnostics;
using ContextSmith.Models;

namespace ContextSmith.Services;

public static class ExplorerService
{
    /// <summary>通知外壳文件关联已变化，大多数 shell 动词无需重启资源管理器即可刷新</summary>
    public static void NotifyAssociationsChanged()
    {
        try
        {
            Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, Native.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 重启资源管理器。先结束进程，交给 Winlogon 的 AutoRestartShell 自动拉起；
    /// 若几秒后仍未启动，再手动启动，避免从管理员进程直接启动出一个提权的 explorer。
    /// </summary>
    public static async Task RestartAsync()
    {
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try
            {
                p.Kill();
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // 忽略
            }
            finally
            {
                p.Dispose();
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(3));

        if (Process.GetProcessesByName("explorer").Length == 0)
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
    }

    /// <summary>打开注册表编辑器并定位到指定键（通过 Regedit 的 LastKey 值）</summary>
    public static void OpenInRegedit(string fullPath)
    {
        foreach (var p in Process.GetProcessesByName("regedit"))
        {
            try { p.Kill(); p.WaitForExit(2000); } catch { /* 忽略 */ }
            finally { p.Dispose(); }
        }

        using (var k = Reg.Create(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
        {
            // LastKey 的前缀是本地化的"计算机/Computer"，沿用系统已有写法
            var existing = Reg.GetString(k, "LastKey");
            var prefix = existing is not null && !existing.StartsWith("HKEY", StringComparison.OrdinalIgnoreCase)
                ? existing.Split('\\')[0]
                : null;
            k.SetValue("LastKey", prefix is null ? fullPath : $@"{prefix}\{fullPath}");
        }

        Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });
    }

    public static void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
}
