using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MiToast.UI;

/// <summary>
/// UWP/Fluent 风格窗口适配：给设置、历史等常规窗口应用 Win11 圆角与沉浸式深色标题栏。
/// 说明：DWM 的 Mica 系统背景（DWMWA_SYSTEMBACKDROP_TYPE）要求窗口表面可透出背景，
/// 而 WPF 非分层窗口的渲染交换链不支持 alpha（透明会渲染成纯黑），故页面背景改用
/// Fluent 风格的渐变画刷模拟 Mica 质感（见各窗口 ApplyTheme 中的 CreatePageBrush）。
/// </summary>
public static class FluentWindow
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    /// <summary>
    /// 应用窗口外观（可重复调用，主题切换时随 dark 刷新标题栏）。
    /// 窗口句柄尚未创建时静默跳过，由调用方在 SourceInitialized 后重试。
    /// </summary>
    public static void ApplyChrome(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        // Win11 圆角窗口（老系统忽略失败）
        int corner = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        // 标题栏跟随主题（深色模式下沉浸式深色标题栏）
        int useDark = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
    }

    /// <summary>HyperOS 风格页面背景：纯色（浅色 #F7F7F7 页面底，深色纯黑，卡片浮在上面）。</summary>
    public static Brush CreatePageBrush(bool dark)
    {
        var brush = new SolidColorBrush(dark ? Color.FromRgb(0x00, 0x00, 0x00) : Color.FromRgb(0xF7, 0xF7, 0xF7));
        brush.Freeze();
        return brush;
    }
}
