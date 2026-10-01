using System.Windows;
using System.Windows.Input;
using ContextSmith.Models;
using ContextSmith.Services;
using ContextSmith.ViewModels;
using ContextSmith.Views;

namespace ContextSmith;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(ShowAddDialog, ShowEditDialog);
        DataContext = _vm;
        Loaded += async (_, _) =>
        {
            await _vm.InitializeAsync();
            // 上一次因为要写 HKLM 而挂起的操作，由这个已提权的实例接着做完
            if (App.PendingResume && Elevation.IsElevated)
                await _vm.ResumePendingAsync();
        };
    }

    private AddVerbRequest? ShowAddDialog(SceneDefinition scene)
    {
        var dialog = new AddItemDialog(scene) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private EditVerbRequest? ShowEditDialog(MenuEntry entry)
    {
        var dialog = new AddItemDialog(entry) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.EditResult : null;
    }

    private void OnFind(object sender, ExecutedRoutedEventArgs e)
    {
        if (!_vm.IsScenePage) return;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
}
