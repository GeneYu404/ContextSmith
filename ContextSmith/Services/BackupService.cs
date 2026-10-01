using System.Diagnostics;
using System.IO;
using ContextSmith.Models;

namespace ContextSmith.Services;

/// <summary>
/// 每次修改前用 reg.exe 导出目标键，撤销"删除"类操作时再导入回去。
/// 备份位于 %LocalAppData%\ContextSmith\Backups。
/// </summary>
public sealed class BackupService
{
    public string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ContextSmith", "Backups");

    public string? Snapshot(MenuEntry entry) => Export(entry.FullPath, entry.DisplayName, entry.Is32Bit);

    public string? Export(string fullKeyPath, string label, bool is32Bit = false)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var file = Path.Combine(Folder, $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Sanitize(label)}.reg");
            return RunReg($"export \"{fullKeyPath}\" \"{file}\" /y /reg:{(is32Bit ? 32 : 64)}") && File.Exists(file)
                ? file
                : null;
        }
        catch
        {
            return null;
        }
    }

    public bool Import(string file, bool is32Bit = false) =>
        File.Exists(file) && RunReg($"import \"{file}\" /reg:{(is32Bit ? 32 : 64)}");

    /// <summary>导出所有场景的 shell / shellex 子树，作为完整快照</summary>
    public string ExportAll()
    {
        var dir = Path.Combine(Folder, $"完整备份_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(dir);

        foreach (var scene in Scenes.All.Where(s => s.ClassesSubPath is not null))
        {
            foreach (var (hiveName, tag) in new[] { ("HKEY_LOCAL_MACHINE", "HKLM"), ("HKEY_CURRENT_USER", "HKCU") })
            {
                foreach (var part in new[] { "shell", "shellex" })
                {
                    var key = $@"{hiveName}\SOFTWARE\Classes\{scene.ClassesSubPath}\{part}";
                    // 64 位视图与 32 位视图（Wow6432Node）分别导出，第三方 32 位 Shell 扩展才不会漏
                    RunReg($"export \"{key}\" \"{Path.Combine(dir, $"{tag}_{Sanitize(scene.Title)}_{part}.reg")}\" /y /reg:64");
                    RunReg($"export \"{key}\" \"{Path.Combine(dir, $"{tag}_{Sanitize(scene.Title)}_{part}_32.reg")}\" /y /reg:32");
                }
            }
        }

        var blocked = Path.Combine(dir, "HKLM_Blocked.reg");
        RunReg($"export \"HKEY_LOCAL_MACHINE\\{Reg.BlockedPath}\" \"{blocked}\" /y /reg:64");
        return dir;
    }

    private static bool RunReg(string args)
    {
        var psi = new ProcessStartInfo("reg.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p is null) return false;
        _ = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        return p.WaitForExit(20_000) && p.ExitCode == 0;
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
        return clean.Length > 40 ? clean[..40] : clean;
    }
}
