using Microsoft.Win32;

namespace ContextSmith.Models;

public enum RegOpKind
{
    CreateKey,
    SetValue,
    DeleteValue,
    DeleteKeyTree,
    DeleteKeyIfWasMissing,
}

/// <summary>一个具体的注册表键位置（含 32/64 位视图），用于 .reg 预览与脚本生成。</summary>
public readonly record struct RegKeyPath(RegistryHive Hive, string SubKey,
    RegistryView View = RegistryView.Registry64)
{
    public string HiveName => Hive switch
    {
        RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
        RegistryHive.ClassesRoot => "HKEY_CLASSES_ROOT",
        _ => "HKEY_USERS",
    };

    public string FullPath => $@"{HiveName}\{SubKey}";

    public string ShortPath => Hive switch
    {
        RegistryHive.LocalMachine => $@"HKLM\{SubKey}",
        RegistryHive.CurrentUser => $@"HKCU\{SubKey}",
        RegistryHive.ClassesRoot => $@"HKCR\{SubKey}",
        _ => $@"{Hive}\{SubKey}",
    };
}

/// <summary>一条最小注册表写入描述，只服务预览/脚本（真正的写入仍由 MenuWriter 完成）。</summary>
public sealed class RegOp
{
    public required RegOpKind Kind { get; init; }
    public required RegKeyPath Key { get; init; }
    public string? ValueName { get; init; }
    public string? StringValue { get; init; }
    public RegistryValueKind ValueKind { get; init; } = RegistryValueKind.String;
    public DwordValue? Dword { get; init; }

    public static RegOp SetValue(RegKeyPath key, string? name, string value) =>
        new() { Kind = RegOpKind.SetValue, Key = key, ValueName = name, StringValue = value };

    public static RegOp SetDword(RegKeyPath key, string name, int value) =>
        new()
        {
            Kind = RegOpKind.SetValue,
            Key = key,
            ValueName = name,
            ValueKind = RegistryValueKind.DWord,
            Dword = new DwordValue(value),
        };

    public static RegOp DeleteValue(RegKeyPath key, string name) =>
        new() { Kind = RegOpKind.DeleteValue, Key = key, ValueName = name };

    public static RegOp CreateKey(RegKeyPath key) =>
        new() { Kind = RegOpKind.CreateKey, Key = key };

    public static RegOp DeleteTree(RegKeyPath key) =>
        new() { Kind = RegOpKind.DeleteKeyTree, Key = key };
}

public readonly record struct DwordValue(int Value);

/// <summary>
/// 某个键在写入前的完整现场（自身值 + 一层子键），用于回滚与"恢复快照"。
/// 序列化后随操作日志落盘，重启之后依然可以按现场把注册表写回去。
/// </summary>
public sealed class KeySnapshot
{
    public bool Existed { get; set; }

    public Dictionary<string, SavedValue> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>子键内容递归一层，够用于 command / InprocServer32 这类小结构。</summary>
    public Dictionary<string, Dictionary<string, SavedValue>> Children { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>可序列化的注册表值现场（覆盖本程序会碰到的几种类型，含 Binary）。</summary>
    public sealed class SavedValue
    {
        public RegistryValueKind Kind { get; set; } = RegistryValueKind.String;
        public string? Text { get; set; }
        public long? Number { get; set; }
        public string[]? Multi { get; set; }
        public string? Base64 { get; set; }

        public object? ToRegistryValue() => Kind switch
        {
            RegistryValueKind.DWord => Number is null ? null : unchecked((int)Number.Value),
            RegistryValueKind.QWord => Number,
            RegistryValueKind.MultiString => Multi ?? Array.Empty<string>(),
            RegistryValueKind.Binary => Base64 is null ? null : Convert.FromBase64String(Base64),
            _ => Text,
        };

        public static SavedValue From(object? data, RegistryValueKind kind) => kind switch
        {
            RegistryValueKind.DWord => new SavedValue { Kind = kind, Number = ToLong(data) },
            RegistryValueKind.QWord => new SavedValue { Kind = kind, Number = ToLong(data) },
            RegistryValueKind.MultiString => new SavedValue
            {
                Kind = kind,
                Multi = data as string[] ??
                        (data?.ToString() is { Length: > 0 } s ? new[] { s } : Array.Empty<string>()),
            },
            RegistryValueKind.Binary => new SavedValue
            {
                Kind = kind,
                Base64 = data is byte[] b ? Convert.ToBase64String(b) : null,
            },
            RegistryValueKind.None when data is byte[] raw => new SavedValue
            {
                Kind = kind,
                Base64 = Convert.ToBase64String(raw),
            },
            _ => new SavedValue { Kind = kind, Text = data?.ToString() },
        };

        private static long? ToLong(object? data) => data switch
        {
            int i => i,
            long l => l,
            string s when long.TryParse(s, out var v) => v,
            _ => null,
        };
    }
}
