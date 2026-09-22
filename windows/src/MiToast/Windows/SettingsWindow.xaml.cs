using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MiToast.Services;

namespace MiToast;

public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;
    private DispatcherTimer? _savedTimer;
    private bool _loading;

    public SettingsWindow()
    {
        InitializeComponent();
        _loading = true;

        var settings = AppSettings.Instance;
        SelectPosition(settings.Position);
        WidthSlider.Value = settings.CardWidth;
        MarginSlider.Value = settings.ScreenMargin;
        GapSlider.Value = settings.NotificationGap;
        DensitySlider.Value = settings.CardDensity * 100;
        DarkModeCheck.IsChecked = settings.DarkMode;
        MusicPersistentCheck.IsChecked = settings.MusicPersistent;
        SelectDndMode(settings.DndMode);
        HistoryCheck.IsChecked = settings.HistoryEnabled;
        UpdateLabels();
        ApplyTheme(settings.DarkMode);

        _loading = false;

        // 托盘菜单或其他窗口改动设置时同步本窗口
        AppSettings.Instance.Changed += OnSettingsChanged;

        Closed += (_, _) =>
        {
            AppSettings.Instance.Changed -= OnSettingsChanged;
            if (_open == this) _open = null;
        };
    }

    /// <summary>打开设置窗口；若已打开则激活已有窗口。</summary>
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

        _open = new SettingsWindow();
        _open.Show();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _loading = true;
            DarkModeCheck.IsChecked = AppSettings.Instance.DarkMode;
            MusicPersistentCheck.IsChecked = AppSettings.Instance.MusicPersistent;
            ApplyTheme(AppSettings.Instance.DarkMode);
            _loading = false;
        });
    }

    /// <summary>切换窗口主题色板（DynamicResource 实时刷新所有元素）。</summary>
    private void ApplyTheme(bool dark)
    {
        SetBrush("PageBgBrush", dark ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF0, 0xF0, 0xF2));
        SetBrush("CardBgBrush", dark ? Color.FromRgb(0x2C, 0x2C, 0x2E) : Colors.White);
        SetBrush("TextPrimaryBrush", dark ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x1D, 0x1D, 0x1F));
        SetBrush("TextSecondaryBrush", dark ? Color.FromRgb(0x98, 0x98, 0x9F) : Color.FromRgb(0x86, 0x86, 0x8B));
        SetBrush("BorderBrush", dark ? Color.FromRgb(0x48, 0x48, 0x4A) : Color.FromRgb(0xD1, 0xD1, 0xD6));
        SetBrush("TrackBrush", dark ? Color.FromRgb(0x3A, 0x3A, 0x3C) : Color.FromRgb(0xE5, 0xE5, 0xEA));
        SetBrush("HoverBrush", dark ? Color.FromRgb(0x3A, 0x3A, 0x3C) : Color.FromRgb(0xF5, 0xF5, 0xF7));
        SetBrush("ComboBgBrush", dark ? Color.FromRgb(0x2C, 0x2C, 0x2E) : Colors.White);
    }

    private void SetBrush(string key, Color color)
    {
        // 注意：ResourceDictionary 中的 Freezable 会被 WPF 自动冻结，
        // 不能修改其 Color，必须整体替换实例（DynamicResource 引用会自动刷新）。
        Resources[key] = new SolidColorBrush(color);
    }

    private void SelectPosition(string tag)
    {
        foreach (ComboBoxItem item in PositionCombo.Items)
        {
            if ((item.Tag as string) == tag)
            {
                PositionCombo.SelectedItem = item;
                break;
            }
        }
    }

    private void SelectDndMode(string tag)
    {
        foreach (ComboBoxItem item in DndModeCombo.Items)
        {
            if ((item.Tag as string) == tag)
            {
                DndModeCombo.SelectedItem = item;
                break;
            }
        }
    }

    private void WidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdateLabels();

    private void MarginSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdateLabels();

    private void GapSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdateLabels();

    private void DensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => UpdateLabels();

    private void UpdateLabels()
    {
        if (WidthLabel == null || MarginLabel == null || GapLabel == null || DensityLabel == null) return;
        WidthLabel.Text = $"卡片宽度：{(int)WidthSlider.Value} px";
        MarginLabel.Text = $"屏幕边距：{(int)MarginSlider.Value} px";
        GapLabel.Text = $"卡片间距：{(int)GapSlider.Value} px";

        // 紧凑度以百分比展示：75~95 为紧凑，100 为标准，105~125 为宽松
        int density = (int)DensitySlider.Value;
        string densityName = density <= 95 ? "紧凑" : density >= 105 ? "宽松" : "标准";
        DensityLabel.Text = $"卡片紧凑度：{densityName}（{density}%）";
    }

    /// <summary>深色模式开关：立即应用并持久化（卡片样式即刻切换）。</summary>
    private void DarkModeCheck_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var settings = AppSettings.Instance;
        settings.DarkMode = DarkModeCheck.IsChecked == true;
        ApplyTheme(settings.DarkMode);
        settings.Save(); // Save 会触发 Changed，MainWindow 即刻重绘卡片
    }

    /// <summary>音乐通知常驻开关：立即应用并持久化。</summary>
    private void MusicPersistentCheck_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Instance.MusicPersistent = MusicPersistentCheck.IsChecked == true;
        AppSettings.Instance.Save();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Instance;
        settings.Position = (PositionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "TopRight";
        settings.CardWidth = WidthSlider.Value;
        settings.ScreenMargin = MarginSlider.Value;
        settings.NotificationGap = GapSlider.Value;
        settings.CardDensity = DensitySlider.Value / 100.0;
        settings.DarkMode = DarkModeCheck.IsChecked == true;
        settings.MusicPersistent = MusicPersistentCheck.IsChecked == true;
        settings.DndMode = (DndModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "off";
        settings.HistoryEnabled = HistoryCheck.IsChecked == true;
        settings.Save();

        // “已应用”反馈，1.8 秒后自动隐藏
        SavedHint.Visibility = Visibility.Visible;
        _savedTimer?.Stop();
        _savedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _savedTimer.Tick += (s, ev) =>
        {
            _savedTimer?.Stop();
            SavedHint.Visibility = Visibility.Collapsed;
        };
        _savedTimer.Start();
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        HistoryWindow.ShowSingleton();
    }
}
