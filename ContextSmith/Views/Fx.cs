using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ContextSmith.Views;

/// <summary>
/// 移植自 A 组的两个小动画工具：
/// Fx.Flash —— 写注册表成功后整行闪一下绿（启用）/红（禁用）光，让"已经生效"有身体反馈；
/// Fx.Reveal —— 列表载入时按序号错峰淡入上移，避免整块内容"啪"地出现。
/// 都用代码驱动而不是 DataTrigger，因为要"每次 Token 变化都重放一次"。
/// </summary>
public static class Fx
{
    // ---------------- Flash ----------------

    public static readonly DependencyProperty FlashTokenProperty = DependencyProperty.RegisterAttached(
        "FlashToken", typeof(int), typeof(Fx), new PropertyMetadata(-1, OnFlashTokenChanged));

    public static void SetFlashToken(DependencyObject o, int v) => o.SetValue(FlashTokenProperty, v);
    public static int GetFlashToken(DependencyObject o) => (int)o.GetValue(FlashTokenProperty);

    /// <summary>闪光颜色：启用→绿色，禁用→红色。由 XAML 的 DataTrigger 按 Enabled 状态指定。</summary>
    public static readonly DependencyProperty FlashTintProperty = DependencyProperty.RegisterAttached(
        "FlashTint", typeof(Brush), typeof(Fx), new PropertyMetadata(null));

    public static void SetFlashTint(DependencyObject o, Brush v) => o.SetValue(FlashTintProperty, v);
    public static Brush? GetFlashTint(DependencyObject o) => (Brush?)o.GetValue(FlashTintProperty);

    private static void OnFlashTokenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || (int)e.NewValue <= 0) return;
        var tone = GetFlashTint(fe);
        fe.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => Flash(fe, tone)));
    }

    private static void Flash(FrameworkElement fe, Brush? tint)
    {
        // 支持 Border 与 Panel（列表行用 Grid 承载背景）
        Border? border = fe as Border;
        Panel? panel = fe as Panel;
        if ((border is null && panel is null) || tint is null) return;
        if (tint is not SolidColorBrush solid) return;

        var brush = new SolidColorBrush(Colors.Transparent);
        if (border is not null) border.Background = brush;
        else panel!.Background = brush;

        var from = solid.Color;
        var animation = new ColorAnimation
        {
            From = Color.FromArgb((byte)(from.A * 0.9), from.R, from.G, from.B),
            To = Colors.Transparent,
            Duration = TimeSpan.FromMilliseconds(950),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) =>
        {
            if (border is not null) border.Background = Brushes.Transparent;
            else if (panel is not null) panel.Background = Brushes.Transparent;
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
    }

    // ---------------- Reveal（错峰淡入） ----------------

    public static readonly DependencyProperty RevealIndexProperty = DependencyProperty.RegisterAttached(
        "RevealIndex", typeof(int), typeof(Fx), new PropertyMetadata(-1, OnRevealIndexChanged));

    public static void SetRevealIndex(DependencyObject o, int v) => o.SetValue(RevealIndexProperty, v);
    public static int GetRevealIndex(DependencyObject o) => (int)o.GetValue(RevealIndexProperty);

    private static void OnRevealIndexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((int)e.NewValue < 0) return;
        fe.Loaded += (_, _) =>
        {
            fe.Opacity = 0;
            var shift = fe.RenderTransform as TranslateTransform ?? new TranslateTransform();
            fe.RenderTransform = shift;
            shift.Y = 9;

            var delay = TimeSpan.FromMilliseconds(Math.Min((int)e.NewValue, 12) * 26);
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { BeginTime = delay };
            var move = new DoubleAnimation(9, 0, TimeSpan.FromMilliseconds(320))
            {
                BeginTime = delay,
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 },
            };

            fe.BeginAnimation(UIElement.OpacityProperty, fade);
            shift.BeginAnimation(TranslateTransform.YProperty, move);
        };
    }

    // ---------------- 抽屉滑入滑出 ----------------

    public static void SlideIn(FrameworkElement target, double fromX)
    {
        var shift = target.RenderTransform as TranslateTransform ?? new TranslateTransform();
        target.RenderTransform = shift;
        shift.X = fromX;
        target.Opacity = 0;
        target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, 0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    public static void SlideOut(FrameworkElement target, double toX, Action? done = null)
    {
        if (target.RenderTransform is not TranslateTransform shift)
        {
            target.Visibility = Visibility.Collapsed;
            return;
        }
        var anim = new DoubleAnimation(shift.X, toX, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        anim.Completed += (_, _) =>
        {
            target.Visibility = Visibility.Collapsed;
            done?.Invoke();
        };
        target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160)));
        shift.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    /// <summary>Toast 从底部抬起。</summary>
    public static void ToastIn(FrameworkElement target)
    {
        var shift = target.RenderTransform as TranslateTransform ?? new TranslateTransform();
        target.RenderTransform = shift;
        shift.Y = 22;
        target.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(22, 0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 },
        });
    }
}
