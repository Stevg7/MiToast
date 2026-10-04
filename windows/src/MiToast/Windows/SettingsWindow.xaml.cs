using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MiToast.Network;
using MiToast.Services;
using MiToast.UI;

namespace MiToast;

public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;
    // 连接文本框（地址/端口/配对码）停止输入 700ms 后应用；滑块停手 200ms 后落盘
    private readonly DispatcherTimer _connectionDebounce;
    private readonly DispatcherTimer _sliderDebounce;

    /// <summary>
    /// 装载界面期间忽略控件事件。
    /// 必须在字段初值就置位：InitializeComponent 会触发 XAML 里 IsChecked="True" 的 Checked 事件，
    /// 若此时为 false，仅"打开设置窗口"这个动作就会走一遍开关逻辑并把配置写回磁盘。
    /// </summary>
    private bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();
        WindowFade.Enable(this); // 显示淡入 / 关闭淡出（见 WindowFade）

        _connectionDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _connectionDebounce.Tick += (_, _) =>
        {
            _connectionDebounce.Stop();
            ApplyConnectionFields();
        };
        _sliderDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _sliderDebounce.Tick += (_, _) =>
        {
            _sliderDebounce.Stop();
            // 拖动期间只 NotifyChanged 实时预览（不落盘），停手后再持久化
            AppSettings.Instance.Save();
        };

        var settings = AppSettings.Instance;
        SelectPosition(settings.Position);
        WidthSlider.Value = settings.CardWidth;
        MarginSlider.Value = settings.ScreenMargin;
        GapSlider.Value = settings.NotificationGap;
        DensitySlider.Value = settings.CardDensity * 100;
        DarkModeCheck.IsChecked = settings.DarkMode;
        MusicPersistentCheck.IsChecked = settings.MusicPersistent;
        DndCheck.IsChecked = settings.DndEnabled;
        DndSyncCheck.IsChecked = settings.DndSyncPhone;
        HistoryCheck.IsChecked = settings.HistoryEnabled;
        McpLanCheck.IsChecked = settings.McpLanEnabled;
        PhoneHostBox.Text = settings.ManualPhoneHost;
        PhonePortBox.Text = settings.ManualPhonePort.ToString();
        PairCodeBox.Text = settings.PairCode;
        SweepCheck.IsChecked = settings.SubnetSweepEnabled;
        UpdateLabels();
        UpdateConnectionStatus();
        RefreshLocalSummaryStatus();
        RefreshMcpLanStatus();
        ApplyTheme(settings.DarkMode);

        _loading = false;

        // 窗口句柄创建后补一次主题应用：Mica/圆角/深色标题栏都依赖 hwnd
        SourceInitialized += (_, _) => ApplyTheme(AppSettings.Instance.DarkMode);

        // 托盘菜单或其他窗口改动设置时同步本窗口
        AppSettings.Instance.Changed += OnSettingsChanged;
        NetworkManager.Instance.StatusChanged += OnConnectionStatusChanged;
        EngineInstaller.StatusChanged += OnLocalSummaryEvent;
        SummaryService.StatusChanged += OnLocalSummaryProgress;
        SummaryService.Changed += OnLocalSummaryEvent;
        McpLanService.StatusChanged += OnMcpLanEvent;

        Closed += (_, _) =>
        {
            AppSettings.Instance.Changed -= OnSettingsChanged;
            NetworkManager.Instance.StatusChanged -= OnConnectionStatusChanged;
            EngineInstaller.StatusChanged -= OnLocalSummaryEvent;
            SummaryService.StatusChanged -= OnLocalSummaryProgress;
            SummaryService.Changed -= OnLocalSummaryEvent;
            McpLanService.StatusChanged -= OnMcpLanEvent;
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
            // 托盘或其他窗口改动设置时，本窗口的开关/下拉一并跟上
            // （文本框与滑块只有本窗口会改，不回写以免打断输入/拖动）
            SelectPosition(AppSettings.Instance.Position);
            DndCheck.IsChecked = AppSettings.Instance.DndEnabled;
            DndSyncCheck.IsChecked = AppSettings.Instance.DndSyncPhone;
            HistoryCheck.IsChecked = AppSettings.Instance.HistoryEnabled;
            McpLanCheck.IsChecked = AppSettings.Instance.McpLanEnabled;
            SweepCheck.IsChecked = AppSettings.Instance.SubnetSweepEnabled;
            ApplyTheme(AppSettings.Instance.DarkMode);
            _loading = false;
            RefreshMcpLanStatus();
        });
    }

    /// <summary>
    /// 切换窗口主题。深色模式切换带渐出渐入过场（见 <see cref="ThemeTransition"/>）；
    /// 窗口还没显示出来（构造、SourceInitialized）时直接换色，不播过场。
    /// </summary>
    private void ApplyTheme(bool dark)
        => ThemeTransition.Apply((FrameworkElement)Content, dark, () => ApplyThemeColors(dark));

    /// <summary>替换色板实例（DynamicResource 实时刷新所有元素）。取值对齐 miuix 令牌。</summary>
    private void ApplyThemeColors(bool dark)
    {
        // HyperOS 窗口外观：圆角 + 纯色页面底（浅 #F7F7F7 / 深纯黑），卡片浮在上面
        FluentWindow.ApplyChrome(this, dark);
        Resources["PageBgBrush"] = FluentWindow.CreatePageBrush(dark);
        SetBrush("CardBgBrush", dark ? Color.FromRgb(0x24, 0x24, 0x24) : Colors.White);
        SetBrush("TextPrimaryBrush", dark ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Colors.Black);
        SetBrush("TextSecondaryBrush", dark ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x00, 0x00, 0x00));
        SetBrush("BorderBrush", dark ? Color.FromRgb(0x39, 0x39, 0x39) : Color.FromRgb(0xE0, 0xE0, 0xE0));
        SetBrush("TrackBrush", dark ? Color.FromRgb(0x50, 0x50, 0x50) : Color.FromRgb(0xE6, 0xE6, 0xE6));
        SetBrush("HoverBrush", dark ? Color.FromRgb(0x4A, 0x4A, 0x4A) : Color.FromRgb(0xE8, 0xE8, 0xE8));
        SetBrush("ComboBgBrush", dark ? Color.FromRgb(0x24, 0x24, 0x24) : Colors.White);
        // 输入框/次按钮底色：miuix secondaryContainer（深色下比卡片 #242424 亮一档，靠明度差分区）
        SetBrush("FieldBgBrush", dark ? Color.FromRgb(0x43, 0x43, 0x43) : Color.FromRgb(0xF0, 0xF0, 0xF0));
        SetBrush("SecondaryFgBrush", dark ? Color.FromRgb(0xD9, 0xD9, 0xD9) : Color.FromRgb(0x30, 0x30, 0x30));
        SetBrush("SectionTitleBrush", dark ? Color.FromRgb(0x78, 0x7E, 0x96) : Color.FromRgb(0x8C, 0x93, 0xB0));
        // HyperOS 主色：暗色下是另一个蓝（#277AF7），别与浅色 #3482FF 混用
        SetBrush("PrimaryBrush", dark ? Color.FromRgb(0x27, 0x7A, 0xF7) : Color.FromRgb(0x34, 0x82, 0xFF));
        SetBrush("PrimaryHoverBrush", dark ? Color.FromRgb(0x4B, 0x8C, 0xF8) : Color.FromRgb(0x2B, 0x74, 0xE8));
        SetBrush("PrimaryPressedBrush", dark ? Color.FromRgb(0x1E, 0x6B, 0xE0) : Color.FromRgb(0x24, 0x67, 0xD6));
        SetBrush("PrimarySoftBrush", dark ? Color.FromArgb(0x29, 0x27, 0x7A, 0xF7) : Color.FromArgb(0x1F, 0x34, 0x82, 0xFF));
        SetBrush("SliderTrackBrush", dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0x00, 0x00, 0x00));
        SetBrush("DangerBrush", dark ? Color.FromRgb(0xF1, 0x25, 0x22) : Color.FromRgb(0xE9, 0x46, 0x34));
        // 自绘标题栏按钮的悬停/按下覆盖色与滚动条滑块色：半透明叠在页面背景上，深浅色各一套
        SetBrush("CaptionHoverBrush", dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        SetBrush("CaptionPressedBrush", dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00));
        SetBrush("ScrollBarThumbBrush", dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00));
        SetBrush("ScrollBarThumbHoverBrush", dark ? Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x4D, 0x00, 0x00, 0x00));
        SetBrush("ScrollBarThumbActiveBrush", dark ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x66, 0x00, 0x00, 0x00));
        // 主题换了要重新取一次状态文字的画刷
        UpdateConnectionStatus();
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

    /// <summary>勿扰开关：与托盘菜单实时同步（托盘点击 → Save → Changed → 这里回显；此处切换 → Save → 托盘菜单打开时刷新）。</summary>
    private void DndCheck_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Instance.DndEnabled = DndCheck.IsChecked == true;
        AppSettings.Instance.Save();
    }

    /// <summary>同步手机端勿扰开关：手机勿扰开启时 PC 端自动静默，立即生效并持久化。</summary>
    private void DndSyncCheck_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Instance.DndSyncPhone = DndSyncCheck.IsChecked == true;
        AppSettings.Instance.Save();
    }

    private void WidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => OnAppearanceSliderChanged();
    private void MarginSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => OnAppearanceSliderChanged();

    private void GapSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => OnAppearanceSliderChanged();

    private void DensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => OnAppearanceSliderChanged();

    /// <summary>
    /// 外观滑块拖动：立即改内存值并 NotifyChanged 让卡片实时预览（不落盘），
    /// 停手 200ms 后由防抖定时器持久化。
    /// </summary>
    private void OnAppearanceSliderChanged()
    {
        UpdateLabels();
        if (_loading) return;
        var s = AppSettings.Instance;
        s.CardWidth = WidthSlider.Value;
        s.ScreenMargin = MarginSlider.Value;
        s.NotificationGap = GapSlider.Value;
        s.CardDensity = DensitySlider.Value / 100.0;
        s.NotifyChanged();
        _sliderDebounce.Stop();
        _sliderDebounce.Start();
    }

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

    // ── 即时生效：所有设置改动立即应用，不再有“保存并应用” ──

    /// <summary>连接文本框（地址/端口/配对码）输入变化：防抖 700ms 后应用，避免每个按键都触发重连。</summary>
    private void ConnectionField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _connectionDebounce.Stop();
        _connectionDebounce.Start();
    }

    /// <summary>
    /// 应用连接文本框：Save 会规范化地址（自带端口时拆成地址 + 端口、非法清空），
    /// 回填规范化值给用户看；连接参数变了立刻强制重连（旧连接挂着旧配对码/旧地址，必须重建）。
    /// </summary>
    private void ApplyConnectionFields()
    {
        var settings = AppSettings.Instance;
        string previousHost = settings.ManualPhoneHost;
        int previousPort = settings.ManualPhonePort;
        string previousPair = settings.PairCode;

        settings.ManualPhoneHost = PhoneHostBox.Text.Trim();
        settings.ManualPhonePort = int.TryParse(PhonePortBox.Text.Trim(), out int port)
            ? port
            : LanEndpoint.DefaultPort;
        settings.PairCode = PairCodeBox.Text.Trim();
        settings.Save();

        // 回填规范化后的值；_loading 抑制回填触发的 TextChanged 再次排队
        _loading = true;
        PhoneHostBox.Text = settings.ManualPhoneHost;
        PhonePortBox.Text = settings.ManualPhonePort.ToString();
        PairCodeBox.Text = settings.PairCode;
        _loading = false;

        if (settings.ManualPhoneHost != previousHost || settings.ManualPhonePort != previousPort
            || settings.PairCode != previousPair)
        {
            NetworkManager.Instance.Reconnect(force: true);
            UpdateConnectionStatus();
        }
    }

    private void PositionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Instance.Position = (PositionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "TopRight";
        AppSettings.Instance.Save();
    }

    /// <summary>扫描本网段开关：立即生效。它改变设备发现方式，需强制重建发现流程。</summary>
    private void SweepCheck_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Instance.SubnetSweepEnabled = SweepCheck.IsChecked == true;
        AppSettings.Instance.Save();
        NetworkManager.Instance.Reconnect(force: true);
        UpdateConnectionStatus();
    }

    private void HistoryCheck_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Instance.HistoryEnabled = HistoryCheck.IsChecked == true;
        AppSettings.Instance.Save();
    }

    // ── 局域网 MCP 接口 ──

    /// <summary>MCP 局域网服务状态变化（启停/失败）→ 刷新状态行。</summary>
    private void OnMcpLanEvent(object? sender, EventArgs e)
        => Dispatcher.Invoke(RefreshMcpLanStatus);

    private void RefreshMcpLanStatus()
    {
        if (McpLanStatus == null) return;
        McpLanStatus.Text = McpLanService.Describe();
    }

    private void McpLanCheck_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (McpLanCheck.IsChecked == true && string.IsNullOrWhiteSpace(AppSettings.Instance.PairCode))
        {
            // 没有凭据的局域网接口就是裸奔：直接拒绝并回弹开关
            System.Windows.MessageBox.Show(this,
                "局域网 MCP 接口必须凭配对码访问，请先在「连接」里设置配对码。",
                "MiToast", MessageBoxButton.OK, MessageBoxImage.Information);
            McpLanCheck.IsChecked = false; // 触发 Unchecked，但此时仍在处理点击，由 _loading 保护跳过
            return;
        }
        AppSettings.Instance.McpLanEnabled = McpLanCheck.IsChecked == true;
        AppSettings.Instance.Save(); // Changed → McpLanService.ApplyFromSettings → StatusChanged → 刷新状态行
    }

    private void McpLanHelpButton_Click(object sender, RoutedEventArgs e)
    {
        new McpLanHelpWindow { Owner = this }.ShowDialog();
    }

    // ── 本地总结 ──

    /// <summary>引擎/总结状态变化（可能来自后台线程）→ 刷新状态行与按钮可用性。</summary>
    private void OnLocalSummaryEvent(object? sender, EventArgs e)
        => Dispatcher.Invoke(RefreshLocalSummaryStatus);

    /// <summary>生成过程的进度文案（下载/启动/推理中）透出到状态行。</summary>
    private void OnLocalSummaryProgress(object? sender, string message)
    {
        if (message.Length > 0) Dispatcher.Invoke(() => SetLocalSummaryStatus(message));
    }

    private void RefreshLocalSummaryStatus()
    {
        if (LocalSummaryStatus == null) return;
        LocalSummaryDownloadButton.IsEnabled = !EngineInstaller.IsDownloading;
        LocalSummaryStatus.Text = EngineInstaller.DescribeState();
    }

    private void SetLocalSummaryStatus(string text)
    {
        void Set()
        {
            if (LocalSummaryStatus != null) LocalSummaryStatus.Text = text;
        }
        if (Dispatcher.CheckAccess()) Set(); else Dispatcher.Invoke(Set);
    }

    private void LocalSummaryDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (EngineInstaller.IsDownloading) return;
        LocalSummaryDownloadButton.IsEnabled = false;
        SetLocalSummaryStatus("开始下载引擎与模型…");
        _ = DownloadLocalSummaryEngineAsync();
    }

    /// <summary>下载引擎与模型（约 2.5GB，模型支持断点续传）；完成后状态行转就绪。</summary>
    private async System.Threading.Tasks.Task DownloadLocalSummaryEngineAsync()
    {
        try
        {
            await EngineInstaller.EnsureInstalledAsync(
                (received, total) => SetLocalSummaryStatus(
                    $"正在下载模型 {received / 1048576} / {total / 1048576} MB（中断后可从断点续传）"),
                System.Threading.CancellationToken.None);
            SetLocalSummaryStatus("下载完成");
        }
        catch (Exception ex)
        {
            SetLocalSummaryStatus($"下载失败：{ex.Message}");
        }
        finally
        {
            await Dispatcher.InvokeAsync(RefreshLocalSummaryStatus);
        }
    }

    private async void LocalSummaryTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (SummaryService.IsBusy || EngineInstaller.IsDownloading) return;

        LocalSummaryTestButton.IsEnabled = false;
        SetLocalSummaryStatus("正在测试引擎（加载模型）…");
        try
        {
            string modelId = await SummaryService.TestEngineAsync(System.Threading.CancellationToken.None);
            SetLocalSummaryStatus($"引擎可用：{modelId}");
        }
        catch (Exception ex)
        {
            SetLocalSummaryStatus($"测试失败：{ex.Message}");
        }
        finally
        {
            LocalSummaryTestButton.IsEnabled = true;
        }
    }

    private void LocalSummaryOpenButton_Click(object sender, RoutedEventArgs e)
        => SummaryWindow.ShowSingleton();

    /// <summary>端口输入框只允许数字。</summary>
    private void PhonePortBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        foreach (char c in e.Text)
        {
            if (char.IsDigit(c)) continue;
            e.Handled = true;
            return;
        }
    }

    /// <summary>立刻重新搜索并连接（放弃当前退避等待）。已连接时只探测并刷新状态，不掐断健康连接。</summary>
    private void RescanButton_Click(object sender, RoutedEventArgs e)
    {
        NetworkManager.Instance.Reconnect();
        UpdateConnectionStatus();
    }

    private void OnConnectionStatusChanged(object? sender, string status)
        => Dispatcher.Invoke(UpdateConnectionStatus);

    /// <summary>把连接状态显示在设置窗口里，让"连不上"从沉默变成可见。</summary>
    private void UpdateConnectionStatus()
    {
        if (ConnectionStatusText == null) return;

        var manager = NetworkManager.Instance;
        bool connected = manager.State == ConnectionState.Connected;
        ConnectionStatusText.Text = connected ? $"● {manager.StatusText}" : $"○ {manager.StatusText}";
        ConnectionStatusText.Foreground = connected
            ? new SolidColorBrush(Color.FromRgb(0x34, 0xC7, 0x59))
            : Resources["TextSecondaryBrush"] as Brush;
    }

    // ── 自绘标题栏：窗口 CanMinimize，没有最大化键，只需最小化与关闭 ──

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
    {
        HistoryWindow.ShowSingleton();
    }
}
