using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MiToast.Models;
using MiToast.Services;
using MiToast.UI;

namespace MiToast;

public partial class HistoryWindow : Window
{
    private static HistoryWindow? _open;

    public HistoryWindow()
    {
        InitializeComponent();
        HistoryService.Instance.Changed += OnHistoryChanged;
        AppSettings.Instance.Changed += OnSettingsChanged;
        Closed += (_, _) =>
        {
            HistoryService.Instance.Changed -= OnHistoryChanged;
            AppSettings.Instance.Changed -= OnSettingsChanged;
            if (_open == this) _open = null;
        };
        ApplyTheme(AppSettings.Instance.DarkMode);
        Reload();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() => ApplyTheme(AppSettings.Instance.DarkMode));
    }

    /// <summary>切换窗口主题色板（DynamicResource 实时刷新）。</summary>
    private void ApplyTheme(bool dark)
    {
        SetBrush("PageBgBrush", dark ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF0, 0xF0, 0xF2));
        SetBrush("CardBgBrush", dark ? Color.FromRgb(0x2C, 0x2C, 0x2E) : Colors.White);
        SetBrush("TextPrimaryBrush", dark ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x1D, 0x1D, 0x1F));
        SetBrush("TextSecondaryBrush", dark ? Color.FromRgb(0x98, 0x98, 0x9F) : Color.FromRgb(0x86, 0x86, 0x8B));
        SetBrush("TextTertiaryBrush", dark ? Color.FromRgb(0x98, 0x98, 0x9F) : Color.FromRgb(0x6E, 0x6E, 0x73));
        SetBrush("BorderBrush", dark ? Color.FromRgb(0x48, 0x48, 0x4A) : Color.FromRgb(0xD1, 0xD1, 0xD6));
        SetBrush("HoverBrush", dark ? Color.FromRgb(0x3A, 0x3A, 0x3C) : Color.FromRgb(0xF5, 0xF5, 0xF7));
        SetBrush("FieldBgBrush", dark ? Color.FromRgb(0x2C, 0x2C, 0x2E) : Colors.White);
    }

    private void SetBrush(string key, Color color)
    {
        // ResourceDictionary 中的 Freezable 会被自动冻结，必须整体替换实例。
        Resources[key] = new SolidColorBrush(color);
    }

    /// <summary>打开历史窗口；若已打开则激活已有窗口。</summary>
    public static void ShowSingleton()
    {
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
        Dispatcher.Invoke(Reload);
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

        Code = MiFocusNotification.ExtractVerificationCode(message);

        CategoryText = message.Category switch
        {
            "delivery" => "外卖配送",
            "pickup" => "到店取餐",
            "chat" => "消息",
            _ => string.Empty
        };
    }
}
