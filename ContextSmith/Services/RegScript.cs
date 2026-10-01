using System.IO;
using System.Text;
using ContextSmith.Models;
using Microsoft.Win32;

namespace ContextSmith.Services;

/// <summary>
/// 生成 .reg 文本。要点：包含中文的 .reg 必须是 UTF-16 LE + BOM，
/// 用 UTF-8 保存会让注册表编辑器把中文读成乱码 —— 这是同类工具最常见的坑。
/// 本类只负责"把即将发生的写入翻译成文本"，供编辑器实时预览与另存脚本；
/// 真正的写入仍由 MenuWriter 执行。
/// </summary>
public static class RegScript
{
    public static string Build(string title, IEnumerable<RegOp> ops)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Windows Registry Editor Version 5.00");
        sb.AppendLine();
        sb.AppendLine($"; {title} — ContextSmith");
        sb.AppendLine($"; 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        foreach (var group in ops.GroupBy(o => o.Key.FullPath))
        {
            var first = group.First();
            var deletes = group.Where(o => o.Kind is RegOpKind.DeleteKeyTree or RegOpKind.DeleteKeyIfWasMissing)
                .ToList();

            foreach (var del in deletes)
                sb.AppendLine($"[-{del.Key.FullPath}]").AppendLine();

            var rest = group.Where(o => !deletes.Contains(o)).ToList();
            if (rest.Count == 0) continue;

            sb.AppendLine($"[{first.Key.FullPath}]");
            foreach (var op in rest)
            {
                switch (op.Kind)
                {
                    case RegOpKind.CreateKey:
                        break;
                    case RegOpKind.DeleteValue:
                        sb.AppendLine($"{Value(op.ValueName)}=-");
                        break;
                    case RegOpKind.SetValue when op.ValueKind == RegistryValueKind.DWord:
                        sb.AppendLine($"{Value(op.ValueName)}=dword:{(op.Dword?.Value ?? 0):X8}");
                        break;
                    case RegOpKind.SetValue:
                        sb.AppendLine($"{Value(op.ValueName)}={Quote(op.StringValue)}");
                        break;
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ 预览用的操作集合

    /// <summary>新增菜单项将要写入的内容（与 MenuWriter.AddVerb 一一对应）。</summary>
    public static IReadOnlyList<RegOp> ForAdd(AddVerbRequest r)
    {
        var sub = r.Scene.ClassesSubPath ?? "";
        var key = new RegKeyPath(RegistryHive.CurrentUser, $@"SOFTWARE\Classes\{sub}\shell\{r.KeyName}");
        var cmdKey = new RegKeyPath(RegistryHive.CurrentUser, $@"SOFTWARE\Classes\{sub}\shell\{r.KeyName}\command");
        var command = r.RunAsAdmin ? MenuWriter.Elevate(r.Command) : r.Command;

        var ops = new List<RegOp>
        {
            RegOp.CreateKey(key),
            RegOp.SetValue(key, "MUIVerb", r.DisplayName),
        };
        if (!string.IsNullOrWhiteSpace(r.Icon)) ops.Add(RegOp.SetValue(key, "Icon", r.Icon.Trim()));
        if (r.Extended) ops.Add(RegOp.SetValue(key, "Extended", ""));
        if (r.RunAsAdmin) ops.Add(RegOp.SetValue(key, "HasLUAShield", ""));
        if (!string.IsNullOrEmpty(r.Position)) ops.Add(RegOp.SetValue(key, "Position", r.Position));
        ops.Add(RegOp.CreateKey(cmdKey));
        ops.Add(RegOp.SetValue(cmdKey, null, command));
        return ops;
    }

    /// <summary>编辑菜单项将要写入的内容（与 MenuWriter.EditVerb 一一对应）。</summary>
    public static IReadOnlyList<RegOp> ForEdit(MenuEntry e, EditVerbRequest r)
    {
        var key = ForEntry(e);
        var cmdKey = new RegKeyPath(key.Hive, key.SubKey + @"\command", key.View);
        var command = r.RunAsAdmin
            ? MenuWriter.Elevate(MenuWriter.StripElevate(r.Command))
            : MenuWriter.StripElevate(r.Command);

        return
        [
            RegOp.SetValue(key, "MUIVerb", r.DisplayName),
            string.IsNullOrWhiteSpace(r.Icon)
                ? RegOp.DeleteValue(key, "Icon")
                : RegOp.SetValue(key, "Icon", r.Icon.Trim()),
            r.Extended
                ? RegOp.SetValue(key, "Extended", "")
                : RegOp.DeleteValue(key, "Extended"),
            string.IsNullOrEmpty(r.Position)
                ? RegOp.DeleteValue(key, "Position")
                : RegOp.SetValue(key, "Position", r.Position),
            r.RunAsAdmin
                ? RegOp.SetValue(key, "HasLUAShield", "")
                : RegOp.DeleteValue(key, "HasLUAShield"),
            RegOp.SetValue(cmdKey, null, command),
        ];
    }

    /// <summary>菜单项 → 预览用键路径。32 位视图下的 HKLM Classes 落在 Wow6432Node，脚本要写物理路径才准确。</summary>
    public static RegKeyPath ForEntry(MenuEntry e)
    {
        var hive = e.Hive == RegHive.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        var sub = e.KeyPath;
        const string classes = @"SOFTWARE\Classes";
        if (e.Is32Bit && hive == RegistryHive.LocalMachine &&
            sub.StartsWith(classes, StringComparison.OrdinalIgnoreCase))
        {
            sub = @"SOFTWARE\Wow6432Node\Classes" + sub[classes.Length..];
        }
        return new RegKeyPath(hive, sub, e.View);
    }

    // ------------------------------------------------------------------ 序列化

    private static string Value(string? name) =>
        string.IsNullOrEmpty(name) ? "@" : Quote(name)!;

    private static string? Quote(string? text) => text is null ? null : "\"" + Escape(text) + "\"";

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append(@"\n"); break;
                case '\r': sb.Append(@"\r"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>写 UTF-16 LE + BOM。</summary>
    public static void Write(string filePath, string text)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.WriteByte(0xFF);
        fs.WriteByte(0xFE);
        var bytes = Encoding.Unicode.GetBytes(text);
        fs.Write(bytes, 0, bytes.Length);
    }

    public static string SafeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in title)
            sb.Append(invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c);
        var name = sb.ToString().Trim('_');
        return name.Length == 0 ? "change" : (name.Length > 60 ? name[..60] : name);
    }
}
