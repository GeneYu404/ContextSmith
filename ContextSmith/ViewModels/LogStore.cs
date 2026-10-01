using System.IO;
using System.Text.Json;
using ContextSmith.Services;

namespace ContextSmith.ViewModels;

/// <summary>
/// 操作日志持久化。快照随日志写入 %LocalAppData%\ContextSmith\history.json，
/// 程序重启后逐条读回，撤销按钮依然可用 —— 这是"键快照撤销"的跨重启部分。
/// </summary>
public static class LogStore
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ContextSmith", "history.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class Dto
    {
        public DateTime Time { get; set; }
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Glyph { get; set; } = "";
        public bool Undone { get; set; }
        public string? BackupFile { get; set; }
        public RegistrySnapshot? Snapshot { get; set; }
    }

    public static void Save(IReadOnlyList<LogItem> log)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var dtos = log.Select(l => new Dto
        {
            Time = l.Time,
            Title = l.Title,
            Detail = l.Detail,
            Glyph = l.Glyph,
            Undone = l.Undone,
            BackupFile = l.BackupFile,
            // 会话内的撤销闭包无法序列化；带快照的条目重启后照常可撤，闭包类（新建/优化）重启后不可撤
            Snapshot = l.Snapshot,
        }).ToList();

        File.WriteAllText(FilePath, JsonSerializer.Serialize(dtos, Options));
    }

    public static List<LogItem> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];

            var dtos = JsonSerializer.Deserialize<List<Dto>>(File.ReadAllText(FilePath), Options) ?? [];
            var items = new List<LogItem>();
            foreach (var d in dtos.Take(300))
            {
                if (string.IsNullOrWhiteSpace(d.Title)) continue;
                var item = new LogItem
                {
                    Time = d.Time,
                    Title = d.Title,
                    Detail = d.Detail,
                    Glyph = string.IsNullOrEmpty(d.Glyph) ? "\uE73E" : d.Glyph,
                    BackupFile = d.BackupFile,
                    Snapshot = d.Snapshot,
                };
                item.Undone = d.Undone;
                items.Add(item);
            }
            return items;
        }
        catch
        {
            // 日志损坏不影响主流程
            return [];
        }
    }
}
