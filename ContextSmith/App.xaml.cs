using System.Windows;
using System.Windows.Threading;
using ContextSmith.Services;

namespace ContextSmith;

public partial class App : Application
{
    /// <summary>带 --resume-from-elevation 启动：首屏加载完成后重放待执行队列（提权续跑）。</summary>
    public static bool PendingResume { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        PendingResume = e.Args.Any(a =>
            a.StartsWith("--resume-from-elevation", StringComparison.OrdinalIgnoreCase));
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"发生未处理的错误：\n\n{e.Exception.Message}\n\n程序会继续运行，建议刷新当前列表。",
            "右键匠",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
