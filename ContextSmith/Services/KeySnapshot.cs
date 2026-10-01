using ContextSmith.Models;
using Microsoft.Win32;

namespace ContextSmith.Services;

/// <summary>
/// 写入前抓现场、出错或撤销时按现场写回。这是"键快照撤销"的核心：
/// 每一次改动都先把受影响的键（自身值 + 一层子键）原样存下来，
/// 撤销不再依赖备份文件或会话内的闭包 —— 直接把注册表恢复成当时的现场，
/// 因此程序重启之后，日志里的每一条依然可以撤销。
/// </summary>
public sealed class RegistrySnapshot
{
    public List<KeyState> Keys { get; set; } = new();

    public bool TouchesLocalMachine => Keys.Any(k => k.Hive == RegHive.LocalMachine);

    public void Merge(RegistrySnapshot other) => Keys.AddRange(other.Keys);

    /// <summary>抓取单个键的现场。</summary>
    public static RegistrySnapshot CaptureKey(RegHive hive, string path, RegistryView view)
    {
        var snap = new RegistrySnapshot();
        snap.Keys.Add(new KeyState
        {
            Hive = hive,
            Path = path,
            View = view,
            Snapshot = CaptureOne(hive, path, view),
        });
        return snap;
    }

    /// <summary>
    /// 抓取一个菜单项改动会波及的全部键：
    /// 条目自身 + ShellEx 全局屏蔽写入的两个 hive 的 Blocked 键。
    /// ShellNew 走重命名，路径会变，快照跨重启恢复会指向旧路径 —— 交给会话内闭包撤销，不落快照。
    /// </summary>
    public static RegistrySnapshot? CaptureFor(MenuEntry e)
    {
        if (e.Kind == EntryKind.ShellNew) return null;

        var snap = CaptureKey(e.Hive, e.KeyPath, e.View);
        if (e.Kind == EntryKind.ShellEx && e.Clsid is not null)
        {
            snap.Merge(CaptureKey(RegHive.LocalMachine, Reg.BlockedPath, RegistryView.Registry64));
            snap.Merge(CaptureKey(RegHive.CurrentUser, Reg.BlockedPath, RegistryView.Registry64));
        }
        return snap;
    }

    /// <summary>把注册表恢复成现场：现场没有的值/子键删掉，现场有的值写回去。</summary>
    public void Restore()
    {
        foreach (var state in Keys)
        {
            if (!state.Snapshot.Existed)
            {
                using var root = Reg.Base(state.Hive, state.View);
                root.DeleteSubKeyTree(state.Path, throwOnMissingSubKey: false);
                continue;
            }

            using var k = Reg.Create(state.Hive, state.Path, state.View);

            foreach (var name in k.GetValueNames())
                if (!state.Snapshot.Values.ContainsKey(name))
                    k.DeleteValue(name, throwOnMissingValue: false);

            foreach (var (name, saved) in state.Snapshot.Values)
            {
                if (saved.ToRegistryValue() is { } data) k.SetValue(name, data, saved.Kind);
                else k.DeleteValue(name, throwOnMissingValue: false);
            }

            foreach (var child in k.GetSubKeyNames())
                if (!state.Snapshot.Children.ContainsKey(child))
                    k.DeleteSubKeyTree(child, throwOnMissingSubKey: false);

            foreach (var (child, values) in state.Snapshot.Children)
            {
                using var ck = k.CreateSubKey(child, writable: true);
                if (ck is null) continue;

                foreach (var name in ck.GetValueNames())
                    if (!values.ContainsKey(name))
                        ck.DeleteValue(name, throwOnMissingValue: false);

                foreach (var (name, saved) in values)
                {
                    if (saved.ToRegistryValue() is { } data) ck.SetValue(name, data, saved.Kind);
                    else ck.DeleteValue(name, throwOnMissingValue: false);
                }
            }
        }

        ExplorerService.NotifyAssociationsChanged();
    }

    private static KeySnapshot CaptureOne(RegHive hive, string path, RegistryView view)
    {
        var snap = new KeySnapshot();

        using var k = Reg.Open(hive, path, view: view);
        if (k is null)
        {
            // 打不开 = 不存在；权限不足会抛异常而不是返回 null，所以这里不会误判
            snap.Existed = false;
            return snap;
        }

        snap.Existed = true;
        foreach (var name in k.GetValueNames())
        {
            var kind = k.GetValueKind(name);
            var data = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            snap.Values[name] = KeySnapshot.SavedValue.From(data, kind);
        }

        foreach (var child in k.GetSubKeyNames())
        {
            using var ck = k.OpenSubKey(child);
            if (ck is null) continue;

            var map = new Dictionary<string, KeySnapshot.SavedValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in ck.GetValueNames())
            {
                var kind = ck.GetValueKind(name);
                var data = ck.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                map[name] = KeySnapshot.SavedValue.From(data, kind);
            }
            snap.Children[child] = map;
        }

        return snap;
    }

    public sealed class KeyState
    {
        public RegHive Hive { get; set; }
        public string Path { get; set; } = "";
        public RegistryView View { get; set; } = RegistryView.Registry64;
        public KeySnapshot Snapshot { get; set; } = new();
    }
}
