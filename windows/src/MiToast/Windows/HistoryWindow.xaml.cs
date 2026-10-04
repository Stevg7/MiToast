using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MiToast.Models;
using MiToast.Services;
using MiToast.UI;

namespace MiToast;

public partial class HistoryWindow : Window
{
    private static HistoryWindow? _open;
    // 通知突发时每次变更都全量重载 10000 条列表开销太大：防抖 300ms 合并刷新
    private readonly DispatcherTimer _reloadDebounce = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public HistoryWindow()
    {
        InitializeComponent();
        WindowFade.Enable(this); // 显示淡入 / 关闭淡出（见 WindowFade）
        _reloadDebounce.Tick += (_, _) =>
        {
            _reloadDebounce.Stop();
            Reload();
        };
        HistoryService.Instance.Changed += OnHistoryChanged;
        AppSettings.Instance.Changed += OnSettingsChanged;
        Closed += (_, _) =>
        {
            HistoryService.Instance.Changed -= OnHistoryChanged;
            AppSettings.Instance.Changed -= OnSettingsChanged;
            if (_open == this) _open = null;
        };
        // 窗口句柄创建后补一次主题应用：Mica/圆角/深色标题栏都依赖 hwnd
        SourceInitialized += (_, _) => ApplyTheme(AppSettings.Instance.DarkMode);
        ApplyTheme(AppSettings.Instance.DarkMode);
        // 最大化 ⇄ 还原时标题栏按钮要在两套字形间切换
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        Reload();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() => ApplyTheme(AppSettings.Instance.DarkMode));
    }

    /// <summary>
    /// 切换窗口主题。深色模式切换带渐出渐入过场（见 <see cref="ThemeTransition"/>）；
    /// 窗口还没显示出来（构造、SourceInitialized）时直接换色，不播过场。
    /// </summary>
    private void ApplyTheme(bool dark)
        => ThemeTransition.Apply((FrameworkElement)Content, dark, () => ApplyThemeColors(dark));

    /// <summary>替换色板实例（DynamicResource 实时刷新）。取值对齐 miuix 令牌（HyperOS）。</summary>
    private void ApplyThemeColors(bool dark)
    {
        // HyperOS 窗口外观：圆角 + 纯色页面底（浅 #F7F7F7 / 深纯黑），卡片浮在上面
        FluentWindow.ApplyChrome(this, dark);
        Resources["PageBgBrush"] = FluentWindow.CreatePageBrush(dark);
        SetBrush("CardBgBrush", dark ? Color.FromRgb(0x24, 0x24, 0x24) : Colors.White);
        SetBrush("TextPrimaryBrush", dark ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Colors.Black);
        SetBrush("TextSecondaryBrush", dark ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x00, 0x00, 0x00));
        SetBrush("TextTertiaryBrush", dark ? Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x66, 0x00, 0x00, 0x00));
        SetBrush("BorderBrush", dark ? Color.FromRgb(0x39, 0x39, 0x39) : Color.FromRgb(0xE0, 0xE0, 0xE0));
        SetBrush("TrackBrush", dark ? Color.FromRgb(0x50, 0x50, 0x50) : Color.FromRgb(0xE6, 0xE6, 0xE6));
        SetBrush("HoverBrush", dark ? Color.FromRgb(0x4A, 0x4A, 0x4A) : Color.FromRgb(0xE8, 0xE8, 0xE8));
        SetBrush("ComboBgBrush", dark ? Color.FromRgb(0x24, 0x24, 0x24) : Colors.White);
        // 输入框/次按钮底色：miuix secondaryContainer（深色下比卡片亮一档）
        SetBrush("FieldBgBrush", dark ? Color.FromRgb(0x43, 0x43, 0x43) : Color.FromRgb(0xF0, 0xF0, 0xF0));
        SetBrush("SecondaryFgBrush", dark ? Color.FromRgb(0xD9, 0xD9, 0xD9) : Color.FromRgb(0x30, 0x30, 0x30));
        SetBrush("SectionTitleBrush", dark ? Color.FromRgb(0x78, 0x7E, 0x96) : Color.FromRgb(0x8C, 0x93, 0xB0));
        SetBrush("PrimaryBrush", dark ? Color.FromRgb(0x27, 0x7A, 0xF7) : Color.FromRgb(0x34, 0x82, 0xFF));
        SetBrush("PrimaryHoverBrush", dark ? Color.FromRgb(0x4B, 0x8C, 0xF8) : Color.FromRgb(0x2B, 0x74, 0xE8));
        SetBrush("PrimaryPressedBrush", dark ? Color.FromRgb(0x1E, 0x6B, 0xE0) : Color.FromRgb(0x24, 0x67, 0xD6));
        SetBrush("PrimarySoftBrush", dark ? Color.FromArgb(0x29, 0x27, 0x7A, 0xF7) : Color.FromArgb(0x1F, 0x34, 0x82, 0xFF));
        SetBrush("DangerBrush", dark ? Color.FromRgb(0xF1, 0x25, 0x22) : Color.FromRgb(0xE9, 0x46, 0x34));
        SetBrush("CaptionHoverBrush", dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        SetBrush("CaptionPressedBrush", dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00));
        SetBrush("ScrollBarThumbBrush", dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00));
        SetBrush("ScrollBarThumbHoverBrush", dark ? Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x4D, 0x00, 0x00, 0x00));
        SetBrush("ScrollBarThumbActiveBrush", dark ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x66, 0x00, 0x00, 0x00));
    }

    // ── 自绘标题栏：拖动/双击最大化由 WindowChrome 处理，这里只接按钮点击 ──

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    /// <summary>最大化 ⇄ 还原时切换标题栏按钮字形（E922 最大化 / E923 还原）。</summary>
    private void UpdateMaximizeGlyph()
    {
        if (MaximizeGlyph != null)
        {
            MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }
    }

    private void SetBrush(string key, Color color)
    {
        // ResourceDictionary 中的 Freezable 会被自动冻结，必须整体替换实例。
        Resources[key] = new SolidColorBrush(color);
    }

    /// <summary>
    /// 打开历史窗口；若已打开则激活已有窗口。
    /// 访问受系统身份验证（Windows Hello PIN 等）保护：未解锁或解锁超时先弹系统验证，
    /// 验证通过才打开；取消验证则不打开。
    /// </summary>
    public static async void ShowSingleton()
    {
        if (!await HistoryLock.EnsureUnlockedAsync())
        {
            return;
        }

        if (_open != null)
        {
            if (_open.WindowState == WindowState.Minimized)
            {
                _open.WindowState = WindowState.Normal;
            }
            _open.Activate();
            return;
        }

        _open = new HistoryWindow();
        _open.Show();
    }

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _reloadDebounce.Stop();
            _reloadDebounce.Start();
        });
    }

    private void Reload()
    {
        if (HistoryList == null) return;

        string query = SearchBox?.Text?.Trim() ?? string.Empty;

        var items = HistoryService.Instance.GetAll()
            .Select(m => new HistoryItem(m))
            .Where(i =>
                query.Length == 0 ||
                i.AppName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                i.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                i.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        HistoryList.ItemsSource = items;
        if (CountText != null)
        {
            CountText.Text = $"共 {items.Count} 条";
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Reload();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            this,
            "确定清空全部历史通知吗？此操作不可恢复。",
            "MiToast",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.OK)
        {
            HistoryService.Instance.Clear();
        }
    }

    private void CopyItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is HistoryItem item)
        {
            string text = !string.IsNullOrEmpty(item.Code) ? item.Code : item.Content;
            if (!string.IsNullOrEmpty(text))
            {
                try
                {
                    Clipboard.SetText(text);
                }
                catch
                {
                    // 剪贴板被占用时忽略
                }
            }
        }
    }
}

/// <summary>历史列表展示用包装对象。</summary>
public class HistoryItem
{
    public NotificationMessage Message { get; }
    public string AppName { get; }
    public string Title { get; }
    public string Content { get; }
    public string TimeText { get; }
    public string? Code { get; }
    public string CodeText => Code != null ? $"验证码：{Code}（点右侧按钮复制）" : string.Empty;
    public Visibility CodeVisibility => Code != null ? Visibility.Visible : Visibility.Collapsed;
    public string CategoryText { get; }

    public HistoryItem(NotificationMessage message)
    {
        Message = message;
        AppName = string.IsNullOrEmpty(message.AppName) ? message.PackageName : message.AppName;

        string title = message.BigTitle.Length > 0 ? message.BigTitle : message.Title;
        string content = message.BigText.Length > 0 ? message.BigText : message.Content;
        if (content == title) content = string.Empty;

        Title = title;
        Content = content;

        TimeText = message.Timestamp > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(message.Timestamp)
                .ToLocalTime()
                .ToString("MM-dd HH:mm")
            : string.Empty;

        Code = NotificationText.ExtractVerificationCode(message);

        // 分类取值见安卓端 NotificationCategory，新增取值需同步这里与 MCP 工具说明
        CategoryText = message.Category switch
        {
            "delivery" => "外卖配送",
            "pickup" => "到店取餐",
            "order" => "订单物流",
            "chat" => "消息",
            "music" => "音乐播放",
            "payment" => "支付",
            "verification" => "验证码",
            "promo" => "推广推荐",
            "system" => "系统",
            "schedule" => "日程",
            "progress" => "进度",
            _ => string.Empty
        };
    }
}
