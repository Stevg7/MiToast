using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace MiToast.UI;

/// <summary>
/// 窗口渐入渐出：首次显示时整窗淡入，用户关闭时先淡出再真正关窗。
/// 只接管"打开/关闭"这两个状态切换——最小化/还原沿用系统动画，主题切换的过场
/// 在 <see cref="ThemeTransition"/>（作用在 Content 上，与本类作用的 Window 互不干扰）。
///
/// 关闭走"先取消一次 Closing、动画放完再 Close"的套路；应用退出（Shutdown）时
/// WPF 要么不触发 Closing、要么忽略取消直接关窗，此时补发的 Close 落在已关闭的
/// 窗口上会被 IsLoaded 挡成空操作，不会卡住退出流程。
/// </summary>
public static class WindowFade
{
    private static readonly Duration FadeInDuration = new(TimeSpan.FromMilliseconds(200));
    private static readonly Duration FadeOutDuration = new(TimeSpan.FromMilliseconds(140));

    /// <summary>给窗口挂上渐入渐出；在窗口构造器里调用一次即可。</summary>
    public static void Enable(Window window)
    {
        // 先藏住再淡入：Opacity=0 到 Loaded（首帧已渲染）之间窗口不可见，
        // 避免"先闪一帧完整窗口、再重播淡入"的跳变
        window.Opacity = 0;

        window.Loaded += (_, _) =>
        {
            var fadeIn = new DoubleAnimation(0, 1, FadeInDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            window.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        };

        // 每个窗口一份确认标志：第一次 Closing 取消去播动画，动画结束后的二次 Close 放行
        var closeConfirmed = false;
        window.Closing += (_, e) =>
        {
            if (closeConfirmed) return;
            e.Cancel = true;
            closeConfirmed = true;

            // 淡入没播完就关（快速开关）时从当前透明度起播，避免从 1 硬跳
            var fadeOut = new DoubleAnimation(window.Opacity, 0, FadeOutDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (_, _) =>
            {
                // 应用退出场景里窗口可能已被 WPF 直接关掉，此时跳过二次 Close
                if (window.IsLoaded) window.Close();
            };
            window.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        };
    }
}
