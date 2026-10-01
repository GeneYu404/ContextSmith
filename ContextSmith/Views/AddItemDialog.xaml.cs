using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ContextSmith.Models;
using ContextSmith.Services;
using Microsoft.Win32;

namespace ContextSmith.Views;

/// <summary>
/// 新建 / 编辑两用对话框。两个模式都带实时 .reg 写入预览，
/// 预览文本与右侧按钮另存的脚本，就是 MenuWriter 将要写入的最小集合。
/// </summary>
public partial class AddItemDialog : Window
{
    private sealed record MenuTemplate(string Name, string Key, string Command, string? Icon, bool Admin, SceneKind[] Targets);

    private static readonly SceneKind[] FileLike = [SceneKind.File, SceneKind.AllObjects];
    private static readonly SceneKind[] FolderLike = [SceneKind.Directory, SceneKind.Background, SceneKind.Drive, SceneKind.Folder];

    private readonly SceneDefinition? _scene;   // 新建模式
    private readonly MenuEntry? _edit;          // 编辑模式
    private bool _keyEdited;
    private bool _settingKey;
    private bool _loading = true;

    public AddItemDialog(SceneDefinition scene)
    {
        InitializeComponent();
        _scene = scene;

        HeaderText.Text = $"新建菜单项 · {scene.Title}";
        var arg = ArgFor(scene.Kind);
        HintText.Text = string.IsNullOrEmpty(arg)
            ? "桌面空白处没有路径参数。"
            : $"占位符：%1 = 被右键的文件/文件夹，%V = 当前目录。此场景推荐使用 {arg}，路径请用双引号包裹。";

        var templates = BuildTemplates(arg).Where(t => t.Targets.Contains(scene.Kind)).ToList();
        TemplateBox.ItemsSource = templates;
        TemplateBox.IsEnabled = templates.Count > 0;

        Loaded += (_, _) =>
        {
            _loading = false;
            UpdatePreview();
            NameBox.Focus();
        };
    }

    public AddItemDialog(MenuEntry edit)
    {
        InitializeComponent();
        _edit = edit;
        _scene = Scenes.All.FirstOrDefault(s => s.Kind == edit.Scene);

        Title = "编辑右键菜单项";
        HeaderText.Text = $"编辑菜单项 · {edit.DisplayName}";
        SubText.Text = $"写入位置：{edit.FullPath}"
                       + (edit.Is32Bit ? "（32 位视图 / Wow6432Node）" : "")
                       + "，保存后即时生效。";
        TemplatePanel.Visibility = Visibility.Collapsed;
        HintText.Text = "“以管理员身份运行”会幂等地包装 / 还原命令并同步 UAC 盾牌；右侧预览即实际写入内容。";

        SetKey(edit.KeyName);
        _keyEdited = true;
        KeyBox.IsReadOnly = true;
        NameBox.Text = edit.DisplayName;
        CommandBox.Text = edit.Command ?? "";
        IconBox.Text = edit.IconRaw ?? "";
        ExtendedBox.IsChecked = edit.Extended;
        AdminBox.IsChecked = edit.RunAsAdmin;
        PositionBox.SelectedIndex = edit.Position switch { "Top" => 1, "Bottom" => 2, _ => 0 };

        Loaded += (_, _) =>
        {
            _loading = false;
            UpdatePreview();
            NameBox.Focus();
        };
    }

    public AddVerbRequest? Result { get; private set; }
    public EditVerbRequest? EditResult { get; private set; }

    // ------------------------------------------------------------------ 工具

    private static string ArgFor(SceneKind kind) => kind switch
    {
        SceneKind.Background => "%V",
        SceneKind.Desktop => "",
        _ => "%1",
    };

    private string CurrentPosition => PositionBox.SelectedIndex switch { 1 => "Top", 2 => "Bottom", _ => "" };

    private string? CurrentIcon => string.IsNullOrWhiteSpace(IconBox.Text) ? null : IconBox.Text.Trim();

    private static List<MenuTemplate> BuildTemplates(string arg)
    {
        var a = string.IsNullOrEmpty(arg) ? "%V" : arg;
        return
        [
            new("用记事本打开", "OpenWithNotepad", "notepad.exe \"%1\"", "notepad.exe", false, FileLike),
            new("计算 SHA256", "GetSHA256",
                "powershell.exe -NoExit -Command \"Get-FileHash -Algorithm SHA256 -LiteralPath '%1' | Format-List\"",
                "powershell.exe", false, FileLike),
            new("复制完整路径", "CopyFullPath", $"cmd.exe /c echo|set /p=\"{a}\"|clip", "imageres.dll,-5302", false,
                [.. FileLike, .. FolderLike]),
            new("获取管理员所有权", "TakeOwnership",
                "cmd.exe /c takeown /f \"%1\" /r /d y && icacls \"%1\" /grant administrators:F /t",
                "imageres.dll,-78", true, [SceneKind.File, SceneKind.Directory, SceneKind.AllObjects]),
            new("在此处打开命令提示符", "CmdHere", $"cmd.exe /s /k pushd \"{a}\"", "cmd.exe", false, FolderLike),
            new("在此处打开命令提示符（管理员）", "CmdHereAdmin", $"cmd.exe /s /k pushd \"{a}\"", "cmd.exe", true, FolderLike),
            new("在此处打开 PowerShell", "PowerShellHere",
                $"powershell.exe -NoExit -Command \"Set-Location -LiteralPath '{a}'\"", "powershell.exe", false, FolderLike),
            new("在此处打开 Windows 终端", "TerminalHere", $"wt.exe -d \"{a}\"", null, false, FolderLike),
            new("重启资源管理器", "RestartExplorer", "cmd.exe /c taskkill /f /im explorer.exe & start explorer.exe",
                "explorer.exe", false, [SceneKind.Desktop, SceneKind.Background]),
        ];
    }

    // ------------------------------------------------------------------ .reg 预览

    private void UpdatePreview()
    {
        if (_loading) return;
        try
        {
            if (_edit is not null)
            {
                var r = new EditVerbRequest(
                    NameBox.Text.Trim(),
                    CommandBox.Text.Trim(),
                    CurrentIcon,
                    ExtendedBox.IsChecked == true,
                    AdminBox.IsChecked == true,
                    CurrentPosition);
                PreviewBox.Text = RegScript.Build("更新菜单项", RegScript.ForEdit(_edit, r));
            }
            else if (_scene is not null)
            {
                var r = new AddVerbRequest(
                    _scene,
                    KeyBox.Text.Trim(),
                    NameBox.Text.Trim(),
                    CommandBox.Text.Trim(),
                    CurrentIcon,
                    ExtendedBox.IsChecked == true,
                    AdminBox.IsChecked == true,
                    CurrentPosition);
                PreviewBox.Text = RegScript.Build("新增菜单项", RegScript.ForAdd(r));
            }
        }
        catch (Exception ex)
        {
            PreviewBox.Text = "// 预览生成失败：" + ex.Message;
        }
    }

    private void OnFieldChanged(object sender, RoutedEventArgs e) => UpdatePreview();

    private void OnCopyPreview(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(PreviewBox.Text ?? "");
            ShowNote("已复制 .reg 预览到剪贴板", ok: true);
        }
        catch
        {
            ShowNote("剪贴板被占用，请重试", ok: false);
        }
    }

    private void OnSaveScript(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "注册表脚本 (*.reg)|*.reg",
            FileName = RegScript.SafeFileName(HeaderText.Text) + ".reg",
            DefaultExt = ".reg",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            RegScript.Write(dlg.FileName, PreviewBox.Text ?? "");
            ShowNote($"已保存脚本：{dlg.FileName}", ok: true);
        }
        catch (Exception ex)
        {
            ShowNote("保存失败：" + ex.Message, ok: false);
        }
    }

    private void ShowNote(string text, bool ok)
    {
        ErrorText.Text = (ok ? "✓ " : "") + text;
        ErrorText.Foreground = ok ? Brushes.SeaGreen : (Brush)FindResource("DangerBrush");
        ErrorText.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ 字段联动

    private void OnTemplateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TemplateBox.SelectedItem is not MenuTemplate t) return;
        NameBox.Text = t.Name;
        SetKey(t.Key);
        _keyEdited = true;
        CommandBox.Text = t.Command;
        IconBox.Text = t.Icon ?? "";
        AdminBox.IsChecked = t.Admin;
        UpdatePreview();
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_keyEdited) return;
        var ascii = Regex.Replace(NameBox.Text, "[^A-Za-z0-9_-]", "");
        SetKey(ascii.Length >= 3 ? ascii : NameBox.Text.Length > 0 ? $"Custom_{(uint)NameBox.Text.GetHashCode() % 100000}" : "");
        UpdatePreview();
    }

    private void OnKeyChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingKey) _keyEdited = KeyBox.Text.Length > 0;
        UpdatePreview();
    }

    private void SetKey(string key)
    {
        _settingKey = true;
        KeyBox.Text = key;
        _settingKey = false;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择要执行的程序",
            Filter = "程序 (*.exe;*.bat;*.cmd)|*.exe;*.bat;*.cmd|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        var arg = ArgFor(_scene?.Kind ?? SceneKind.File);
        CommandBox.Text = string.IsNullOrEmpty(arg) ? $"\"{dlg.FileName}\"" : $"\"{dlg.FileName}\" \"{arg}\"";
        if (string.IsNullOrWhiteSpace(IconBox.Text)) IconBox.Text = dlg.FileName;
        if (string.IsNullOrWhiteSpace(NameBox.Text))
            NameBox.Text = $"用 {Path.GetFileNameWithoutExtension(dlg.FileName)} 打开";
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var command = CommandBox.Text.Trim();

        if (_edit is not null)
        {
            if (name.Length == 0 || command.Length == 0)
            {
                ShowNote("显示名称和执行命令都不能为空。", ok: false);
                return;
            }
            EditResult = new EditVerbRequest(
                name, command, CurrentIcon,
                ExtendedBox.IsChecked == true,
                AdminBox.IsChecked == true,
                CurrentPosition);
            DialogResult = true;
            return;
        }

        var key = KeyBox.Text.Trim();
        string? error =
            name.Length == 0 ? "请填写显示名称。" :
            key.Length == 0 ? "请填写注册表键名。" :
            key.IndexOfAny(['\\', '/']) >= 0 ? "键名不能包含斜杠。" :
            command.Length == 0 ? "请填写要执行的命令。" :
            null;

        if (error is not null)
        {
            ShowNote(error, ok: false);
            return;
        }

        Result = new AddVerbRequest(
            _scene!,
            key,
            name,
            command,
            CurrentIcon,
            ExtendedBox.IsChecked == true,
            AdminBox.IsChecked == true,
            CurrentPosition);

        DialogResult = true;
    }
}
