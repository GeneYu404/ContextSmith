namespace ContextSmith.Models;

public enum SceneKind
{
    File,
    Directory,
    Background,
    Desktop,
    Drive,
    AllObjects,
    Folder,
    New,
    FileTypes,
}

/// <summary>
/// 一个"右键场景"。ClassesSubPath 是相对 SOFTWARE\Classes 的路径，
/// 扫描时会分别在 HKLM 与 HKCU 两个 hive 下查找（HKCR 只是二者的合并视图）。
/// </summary>
public sealed record SceneDefinition(
    SceneKind Kind,
    string Title,
    string Description,
    string? ClassesSubPath,
    string Glyph)
{
    public bool SupportsCustomVerbs => ClassesSubPath is not null;
}

public static class Scenes
{
    public static readonly IReadOnlyList<SceneDefinition> All =
    [
        new(SceneKind.File,       "文件",         "右键单击任意文件时显示",          "*",                    "\uE8A5"),
        new(SceneKind.Directory,  "文件夹",       "右键单击文件夹时显示",            "Directory",            "\uE8B7"),
        new(SceneKind.Background, "文件夹空白处", "在资源管理器空白处右键时显示",    @"Directory\Background", "\uE8A9"),
        new(SceneKind.Desktop,    "桌面",         "在桌面空白处右键时显示",          "DesktopBackground",    "\uE7F4"),
        new(SceneKind.Drive,      "磁盘驱动器",   "右键单击磁盘分区时显示",          "Drive",                "\uEDA2"),
        new(SceneKind.AllObjects, "所有对象",     "文件与文件夹共同显示",            "AllFilesystemObjects", "\uE8FD"),
        new(SceneKind.Folder,     "Folder 通用",  "文件夹、库、此电脑等外壳对象",    "Folder",               "\uE8D5"),
        new(SceneKind.New,        "“新建”菜单",   "空白处右键 → 新建 子菜单",        null,                   "\uE8F4"),
        new(SceneKind.FileTypes,  "文件类型",     "具体扩展名与 ProgID 上注册的项（.txt、快捷方式、WinRAR 等，含被其它工具禁用过的）", null, "\uE7B4"),
    ];
}
