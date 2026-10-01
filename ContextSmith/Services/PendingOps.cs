using System.IO;
using System.Text.Json;
using ContextSmith.Models;

namespace ContextSmith.Services;

public static class PendingTypes
{
    public const string SetEnabled = "SetEnabled";
    public const string SetExtended = "SetExtended";
    public const string SetPosition = "SetPosition";
    public const string Delete = "Delete";
    public const string EditVerb = "EditVerb";
    public const string Tweak = "Tweak";
    public const string Restore = "Restore";
}

/// <summary>
/// 一条待提权操作。排队时连同"写入前的现场快照"一起序列化，
/// 提权后的实例按 Type 重放写入 —— 这就是"提权续跑"。
/// </summary>
public sealed class PendingOp
{
    public string Type { get; set; } = PendingTypes.SetEnabled;
    public MenuEntry? Entry { get; set; }
    public bool Enabled { get; set; }
    public bool Flag { get; set; }
    public string Position { get; set; } = "";
    public string? TweakId { get; set; }
    public EditVerbRequest? Edit { get; set; }
    public RegistrySnapshot? Snapshot { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Glyph { get; set; } = "\uE73E";
}

public static class PendingOps
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static void Save(IReadOnlyList<PendingOp> ops)
    {
        var file = Elevation.PendingFile;
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(file, JsonSerializer.Serialize(ops, Options));
    }

    public static List<PendingOp> Load()
    {
        try
        {
            var file = Elevation.PendingFile;
            if (!File.Exists(file)) return [];
            return JsonSerializer.Deserialize<List<PendingOp>>(File.ReadAllText(file), Options) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Clear()
    {
        try
        {
            File.Delete(Elevation.PendingFile);
        }
        catch
        {
            // 忽略
        }
    }
}
