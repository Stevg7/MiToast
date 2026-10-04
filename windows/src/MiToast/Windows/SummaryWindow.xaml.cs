using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using MiToast.Models;
using MiToast.Services;
using MiToast.UI;

namespace MiToast;

/// <summary>
/// 本地总结窗口：左侧历史总结列表，右侧轻量 Markdown 正文。
/// 生成是唯一的长任务入口：按钮在「立即生成 ⇄ 取消」间切换，进度走
/// SummaryService.StatusChanged（下载进度/启动/推理中），本地计时器补已用时长。
/// </summary>
public partial class SummaryWindow : Window
{
    private static SummaryWindow? _open;

    private readonly DispatcherTimer _elapsedTimer;
    private string _statusBase = string.Empty;
    private readonly Stopwatch _elapsed = new();
    private bool _busy;
    // XAML 里 ComboBox 预设选中项会在 InitializeComponent 期间触发 SelectionChanged，挡住这次回写
    private bool _initialized;

    public SummaryWindow()
    {
        InitializeComponent();
        WindowFade.Enable(this);

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => RenderStatus();

        SummaryService.Changed += OnSummariesChanged;
        SummaryService.StatusChanged += OnStatusChanged;
        AppSettings.Instance.Changed += OnSettingsChanged;
        Closed += (_, _) =>
        {
            SummaryService.Changed -= OnSummariesChanged;
            SummaryService.StatusChanged -= OnStatusChanged;
            AppSettings.Instance.Changed -= OnSettingsChanged;
            _elapsedTimer.Stop();
            if (_open == this) _open = null;
        };

        SourceInitialized += (_, _) => ApplyTheme(AppSettings.Instance.DarkMode);
        ApplyTheme(AppSettings.Instance.DarkMode);
        StateChanged += (_, _) => UpdateMaximizeGlyph();

        Reload();
        _initialized = true;
        // 已保存的时间范围回填到下拉框（24/72/168 三档，越界回默认 72）
        int saved = AppSettings.Instance.LocalSummaryWindowHours;
        RangeCombo.SelectedIndex = saved switch
        {
            <= 24 => 0,
            <= 72 => 1,
            _ => 2
        };
        SyncBusyUi(SummaryService.IsBusy);
        if (!_busy) _statusBase = string.Empty;
        RenderStatus();
    }

    // ---------- 打开入口（与历史窗口同款：单例 + 身份验证门） ----------

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

        _open = new SummaryWindow();
        _open.Show();
    }

    // ---------- 主题（与历史窗口同一套色板） ----------

    private void OnSettingsChanged(object? sender, EventArgs e)
        => Dispatcher.Invoke(() => ApplyTheme(AppSettings.Instance.DarkMode));

    private void ApplyTheme(bool dark)
        => ThemeTransition.Apply((FrameworkElement)Content, dark, () => ApplyThemeColors(dark));

    private void ApplyThemeColors(bool dark)
    {
        FluentWindow.ApplyChrome(this, dark);
        Resources["PageBgBrush"] = FluentWindow.CreatePageBrush(dark);
        SetBrush("CardBgBrush", dark ? Color.FromRgb(0x24, 0x24, 0x24) : Colors.White);
        SetBrush("TextPrimaryBrush", dark ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Colors.Black);
        SetBrush("TextSecondaryBrush", dark ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x00, 0x00, 0x00));
        SetBrush("TextTertiaryBrush", dark ? Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x66, 0x00, 0x00, 0x00));
        SetBrush("BorderBrush", dark ? Color.FromRgb(0x39, 0x39, 0x39) : Color.FromRgb(0xE0, 0xE0, 0xE0));
        SetBrush("PrimaryBrush", dark ? Color.FromRgb(0x27, 0x7A, 0xF7) : Color.FromRgb(0x34, 0x82, 0xFF));
        SetBrush("DangerBrush", dark ? Color.FromRgb(0xF1, 0x25, 0x22) : Color.FromRgb(0xE9, 0x46, 0x34));
        // ComboBox/次按钮引用的填充底与悬停高亮：漏设会停留 App.xaml 的浅色默认值，
        // 深色下出现白底胶囊配白字（时间范围下拉框不可见就是这个原因）
        SetBrush("HoverBrush", dark ? Color.FromRgb(0x4A, 0x4A, 0x4A) : Color.FromRgb(0xE8, 0xE8, 0xE8));
        SetBrush("ComboBgBrush", dark ? Color.FromRgb(0x24, 0x24, 0x24) : Colors.White);
        SetBrush("FieldBgBrush", dark ? Color.FromRgb(0x43, 0x43, 0x43) : Color.FromRgb(0xF0, 0xF0, 0xF0));
        // 次按钮（复制 Markdown）文字色：不设会停留浅色默认的深灰，深底上对比度不足
        SetBrush("SecondaryFgBrush", dark ? Color.FromRgb(0xD9, 0xD9, 0xD9) : Color.FromRgb(0x30, 0x30, 0x30));
        // 全局 ScrollBar 模板引用这些键（App.xaml 只给浅色默认值），随主题换实例
        SetBrush("ScrollBarThumbBrush", dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00));
        SetBrush("ScrollBarThumbHoverBrush", dark ? Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x4D, 0x00, 0x00, 0x00));
        SetBrush("ScrollBarThumbActiveBrush", dark ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x66, 0x00, 0x00, 0x00));
    }

    private void SetBrush(string key, Color color)
        => Resources[key] = new SolidColorBrush(color);

    // ---------- 自绘标题栏 ----------

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeGlyph()
    {
        if (MaximizeGlyph != null)
        {
            MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }
    }

    // ---------- 列表与详情 ----------

    private void OnSummariesChanged(object? sender, EventArgs e)
        => Dispatcher.Invoke(Reload);

    private void Reload()
    {
        var entries = SummaryService.LoadSummaries();
        SummaryList.ItemsSource = entries;

        if (entries.Count == 0)
        {
            DetailPanel.Children.Clear();
            var hint = new TextBlock
            {
                Text = "还没有总结记录。\n选好时间范围点「立即生成」，本地模型会把这段时间的通知归纳成一份总结。",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            };
            // 资源引用而非画刷实例：窗口开着切深浅色时文字跟随主题（下同）
            hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            DetailPanel.Children.Add(hint);
        }
    }

    private void SummaryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SummaryList.SelectedItem is SummaryEntry entry)
        {
            RenderMarkdown(entry.SummaryMd);
        }
    }

    private SummaryEntry? SelectedEntry => SummaryList.SelectedItem as SummaryEntry;

    // ---------- 生成 / 取消 ----------

    private async void GenerateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            SummaryService.CancelActiveRun();
            _statusBase = "正在取消…";
            RenderStatus();
            return;
        }

        if (EngineInstaller.GetState() != EngineInstallState.Ready)
        {
            _statusBase = $"推理引擎未就绪：{EngineInstaller.DescribeState()}。请先到设置 → 本地总结里下载。";
            RenderStatus(isError: true);
            return;
        }

        int hours = GetSelectedHours();
        SyncBusyUi(true);
        _elapsed.Restart();
        _elapsedTimer.Start();

        try
        {
            await Task.Run(() => SummaryService.RunAsync(hours, CancellationToken.None));

            _statusBase = "总结完成";
            RenderStatus();
            Reload();
            // 新总结插在列表首位，自动选中展示
            SummaryList.SelectedIndex = 0;
        }
        catch (OperationCanceledException)
        {
            _statusBase = "已取消";
            RenderStatus();
        }
        catch (Exception ex)
        {
            _statusBase = $"生成失败：{ex.Message}";
            RenderStatus(isError: true);
        }
        finally
        {
            _elapsedTimer.Stop();
            _elapsed.Stop();
            SyncBusyUi(false);
        }
    }

    private void SyncBusyUi(bool busy)
    {
        _busy = busy;
        GenerateButton.Content = busy ? "取消" : "立即生成";
        RangeCombo.IsEnabled = !busy;
        if (busy)
        {
            _statusBase = "正在准备…";
            RenderStatus();
        }
    }

    private int GetSelectedHours()
    {
        if (RangeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            int.TryParse(tag, out int hours))
        {
            return hours;
        }
        return AppSettings.Instance.LocalSummaryWindowHours;
    }

    private void RangeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (RangeCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            int.TryParse(tag, out int hours))
        {
            AppSettings.Instance.LocalSummaryWindowHours = hours;
            AppSettings.Instance.Save();
        }
    }

    // ---------- 状态行 ----------

    private void OnStatusChanged(object? sender, string message)
        => Dispatcher.Invoke(() =>
        {
            _statusBase = message;
            if (message.Length == 0 && !_busy) _elapsedTimer.Stop();
            RenderStatus();
        });

    private void RenderStatus(bool isError = false)
    {
        string text = _statusBase;
        if (_busy && _elapsed.IsRunning)
        {
            text = $"{_statusBase}（已用时 {_elapsed.Elapsed:mm\\:ss}）";
        }
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty,
            isError ? "DangerBrush" : "TextSecondaryBrush");
    }

    // ---------- 复制 / 删除 ----------

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var entry = SelectedEntry;
        if (entry == null)
        {
            _statusBase = "先在左侧选择一条总结";
            RenderStatus();
            return;
        }
        try
        {
            Clipboard.SetText(entry.SummaryMd);
            _statusBase = "已复制总结 Markdown";
            RenderStatus();
        }
        catch
        {
            // 剪贴板被占用时忽略
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var entry = SelectedEntry;
        if (entry == null) return;

        var result = System.Windows.MessageBox.Show(
            this,
            $"删除 {entry.RangeText} 的这份总结吗？",
            "MiToast",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.OK) return;

        int index = SummaryList.SelectedIndex;
        SummaryService.Delete(entry.GeneratedAtMs);
        Reload();
        // 删除后选中位置平滑落在相邻项上，避免详情区残留已删内容
        if (SummaryList.Items.Count > 0)
        {
            SummaryList.SelectedIndex = Math.Min(index, SummaryList.Items.Count - 1);
        }
        else
        {
            DetailPanel.Children.Clear();
        }
    }

    // ---------- 轻量 Markdown 渲染 ----------
    // 只覆盖总结实际会用到的语法：#/##/### 标题、- 列表、**粗体**。
    // 不引第三方库，纯 TextBlock 组合。

    private void RenderMarkdown(string markdown)
    {
        DetailPanel.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown)) return;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();

            // 连续的 | 行是一个 Markdown 表格（第二行是 |---|---| 分隔行），整块交给表格渲染
            if (line.TrimStart().StartsWith("|", StringComparison.Ordinal))
            {
                var tableLines = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal))
                {
                    tableLines.Add(lines[i]);
                    i++;
                }
                i--;
                AddTable(tableLines);
                continue;
            }

            if (line.Trim().Length == 0)
            {
                AddParagraph(string.Empty, 14, FontWeights.Normal, false, extraTop: 2);
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                AddParagraph(line[4..].Trim(), 15, FontWeights.Bold, primary: false);
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                AddParagraph(line[3..].Trim(), 17, FontWeights.Bold, primary: true, extraTop: 6);
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                AddParagraph(line[2..].Trim(), 20, FontWeights.Bold, primary: true);
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                AddParagraph("•  " + line[2..].Trim(), 14, FontWeights.Normal, false, indent: 14);
            }
            else if (Regex.IsMatch(line, @"^\d+[.、] "))
            {
                AddParagraph(line, 14, FontWeights.Normal, false, indent: 14);
            }
            else
            {
                AddParagraph(line, 14, FontWeights.Normal, false);
            }
        }
    }

    /// <summary>
    /// 表格渲染：首行是表头（浅底加粗），行间用 1px 分隔线；
    /// 列宽按各列内容峰值加权（star 比例），窄窗口下单元格文字自动换行。
    /// </summary>
    private void AddTable(List<string> rows)
    {
        // 按 | 切分单元格；|---|---| 分隔行直接剔除
        var parsed = rows
            .Select(r => r.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList())
            .Where(cells => !cells.All(c => Regex.IsMatch(c, @"^:?-{3,}:?$")))
            .ToList();
        if (parsed.Count == 0) return;

        int columns = parsed.Max(r => r.Count);
        var peak = new int[columns];
        foreach (var row in parsed)
        {
            for (int c = 0; c < row.Count; c++)
            {
                peak[c] = Math.Max(peak[c], row[c].Length);
            }
        }

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 12) };
        for (int c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(peak[c], 4), GridUnitType.Star)
            });
        }

        for (int r = 0; r < parsed.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            bool header = r == 0;

            for (int c = 0; c < columns; c++)
            {
                var border = new Border
                {
                    Padding = new Thickness(10, 6, 10, 6),
                    BorderThickness = new Thickness(0, 0, 0, 1)
                };
                border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                if (header)
                {
                    border.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
                }

                var cell = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
                cell.SetResourceReference(TextBlock.ForegroundProperty,
                    header ? "TextPrimaryBrush" : "TextSecondaryBrush");
                if (header) cell.FontWeight = FontWeights.Bold;
                FillInline(cell, c < parsed[r].Count ? parsed[r][c] : string.Empty);

                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                border.Child = cell;
                grid.Children.Add(border);
            }
        }

        DetailPanel.Children.Add(grid);
    }

    /// <summary>**粗体** 内联解析：按 ** 切分，奇数段加粗，其余段继承块级样式。</summary>
    private static void FillInline(TextBlock block, string text)
    {
        var segments = text.Split("**");
        for (int i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length == 0) continue;
            var run = new Run(segments[i]);
            if (i % 2 == 1) run.FontWeight = FontWeights.Bold;
            block.Inlines.Add(run);
        }
    }

    private void AddParagraph(string text, double fontSize, FontWeight weight, bool primary,
        double indent = 0, double extraTop = 0)
    {
        var block = new TextBlock
        {
            FontSize = fontSize,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(indent, extraTop, 0, 5)
        };
        // 画刷走资源引用：深浅色切换时正文即时跟随，不用重渲染
        block.SetResourceReference(TextBlock.ForegroundProperty,
            primary ? "PrimaryBrush" : weight == FontWeights.Bold ? "TextPrimaryBrush" : "TextSecondaryBrush");

        FillInline(block, text);
        if (block.Inlines.Count == 0) block.Text = " ";

        DetailPanel.Children.Add(block);
    }
}
