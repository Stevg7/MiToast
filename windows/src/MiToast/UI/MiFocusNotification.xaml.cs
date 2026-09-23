using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using MiToast.Models;
using MiToast.Network;
using MiToast.Services;

namespace MiToast.UI;

public partial class MiFocusNotification : UserControl
{
    public NotificationMessage Message { get; private set; }
    public string NotificationKey => Message.Key;

    /// <summary>是否为音乐/媒体卡片（携带媒体控制 actions）</summary>
    public bool IsMediaCard => Message.MediaActions is { Count: > 0 };

    public event EventHandler? DismissRequested;

    private string? _verificationCode;
    private DispatcherTimer? _copyFeedbackTimer;

    // 收藏/歌词开关的本地视觉状态
    private bool _favoriteActive;
    private bool _lyricActive;
    private Brush? _themePrimaryBrush;
    private static readonly Brush OrangeBrushStatic =
        new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFF6900"));

    public MiFocusNotification(NotificationMessage message)
    {
        InitializeComponent();
        Message = message;
        Loaded += (_, _) =>
        {
            LoadData();
            PlayShowAnimation();
        };
        Unloaded += (_, _) => StopMediaTick();
    }

    public int CalculateHeight()
    {
        double density = Math.Clamp(AppSettings.Instance.CardDensity, 0.75, 1.25);
        double h = 0;
        h += 44 + BaseHeaderBottomMargin * density;
        h += CalculateTextHeight(TitleText) + 6;
        if (HintTitleText.Visibility == Visibility.Visible) h += CalculateTextHeight(HintTitleText) + 4;
        if (ContentText.Visibility == Visibility.Visible) h += CalculateTextHeight(ContentText) + 6;
        if (HintTextPanel.Visibility == Visibility.Visible) h += CalculateTextHeight(HintTextText);
        if (PickupCodePanel.Visibility == Visibility.Visible)
        {
            // 外边距14 + 面板内边距(12+12) + 标签行(~17) + 取餐码行(~47)
            h += 14 + 12 + 17 + 47 + 12;
        }
        if (ProgressPanel.Visibility == Visibility.Visible)
        {
            h += 12;
            if (Message.Category == "pickup" && Message.Progress != null)
                h += CalculatePickupStageBarHeight(Message.Progress.Total);
            else
                h += CalculateProgressStageBarHeight();
            h += 4 + CalculateTextHeight(StageLabelText);
        }
        if (MediaPanel.Visibility == Visibility.Visible)
        {
            // 外边距12 + 面板内边距(10+10) + 控制行40 + 间距10 + 进度行16
            h += 12 + 10 + 40 + 10 + 16 + 10;
        }
        return (int)Math.Ceiling(h + BaseVerticalPadding * 2 * density);
    }

    private static double CalculateProgressStageBarHeight() => 24;
    private static double CalculatePickupStageBarHeight(int total) => 16;

    private double CalculateTextHeight(TextBlock tb)
    {
        if (string.IsNullOrEmpty(tb.Text)) return 0;
        double available = Width > 0 ? Width - 48 : 432;
        if (available < 80) available = 432;
        tb.Measure(new Size(available, double.PositiveInfinity));
        return tb.DesiredSize.Height;
    }

    private void LoadData()
    {
        // 重置动态区域，保证 UpdateFrom 时状态正确
        CopyCodeButton.Visibility = Visibility.Collapsed;
        CopyCodeText.Text = "复制验证码";
        _verificationCode = null;

        AppNameText.Text = Message.AppName;
        TimeText.Text = Message.Timestamp > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(Message.Timestamp).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
            : DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);

        DisplayTitle = ResolveTitle();
        DisplayContent = ResolveContent();
        DisplayHintTitle = ResolveHintTitle();
        DisplayHintText = ResolveHintText();

        TitleText.Text = DisplayTitle;

        if (!string.IsNullOrEmpty(DisplayHintTitle))
        {
            HintTitleText.Text = DisplayHintTitle;
            HintTitleText.Visibility = Visibility.Visible;
        }
        else
        {
            HintTitleText.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrEmpty(DisplayContent))
        {
            ContentText.Text = DisplayContent;
            ContentText.Visibility = Visibility.Visible;
        }
        else
        {
            ContentText.Visibility = Visibility.Collapsed;
        }

        if (!string.IsNullOrEmpty(DisplayHintText))
        {
            HintTextText.Text = DisplayHintText;
            HintTextPanel.Visibility = Visibility.Visible;
        }
        else
        {
            HintTextPanel.Visibility = Visibility.Collapsed;
        }

        if (Message.Category == "pickup")
        {
            string? pickupCode = ExtractPickupCode(Message);
            if (pickupCode != null)
            {
                PickupCodeText.Text = pickupCode;
                PickupCodePanel.Visibility = Visibility.Visible;
            }
            else
            {
                PickupCodePanel.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            PickupCodePanel.Visibility = Visibility.Collapsed;
        }

        if (Message.Progress != null &&
            (Message.Category == "delivery" || Message.Category == "progress" || Message.Category == "pickup"))
        {
            ProgressPanel.Visibility = Visibility.Visible;
            StageLabelText.Text = Message.Progress.Label;
            if (Message.Progress.Total > 0)
            {
                int percent = (int)Math.Round((double)Message.Progress.Current / Message.Progress.Total * 100);
                ProgressPercentText.Text = $"{percent}%";
                BuildStageBar(Message.Progress.Current, Message.Progress.Total, Message.Category);
            }
        }
        else
        {
            ProgressPanel.Visibility = Visibility.Collapsed;
        }

        // 短信/验证码类通知显示一键复制按钮（取餐码卡片除外，避免重复）
        if (PickupCodePanel.Visibility != Visibility.Visible)
        {
            _verificationCode = ExtractVerificationCode(Message);
            if (_verificationCode != null)
            {
                CopyCodeText.Text = "复制验证码";
                CopyCodeButton.Visibility = Visibility.Visible;
            }
        }

        OpenAppButton.Visibility = string.IsNullOrEmpty(Message.PackageName)
            ? Visibility.Collapsed : Visibility.Visible;

        BuildMediaButtons();
        LoadIcon();
        ApplyTheme();
        ApplyDensity();
    }

    private string? _mediaSignature;
    private DispatcherTimer? _mediaTickTimer;
    private long _mediaPositionMs;
    private long _mediaDurationMs;
    private bool _mediaPlaying;

    /// <summary>
    /// 按媒体 actions 映射到固定的 5 个按钮（♥/⏮/播放暂停/⏭/词）。
    /// 仅在 action 集合变化时重建，高频进度更新不重建按钮避免闪烁。
    /// </summary>
    private void BuildMediaButtons()
    {
        var actions = Message.MediaActions;

        if (actions == null || actions.Count == 0)
        {
            MediaPanel.Visibility = Visibility.Collapsed;
            _mediaSignature = null;
            StopMediaTick();
            return;
        }

        string signature = string.Join(",", actions.Select(a => $"{a.Name}:{a.Index}"));
        if (signature != _mediaSignature)
        {
            _mediaSignature = signature;
            BindMediaButton(MediaFavoriteBtn, actions, "favorite");
            BindMediaButton(MediaPrevBtn, actions, "prev");
            BindMediaButton(MediaNextBtn, actions, "next");
            BindMediaButton(MediaLyricBtn, actions, "lyric");

            // 播放/暂停：取 play 或 pause action；图标由播放状态决定
            var playPause = actions.FirstOrDefault(a => a.Name is "play" or "pause");
            if (playPause != null)
            {
                MediaPlayPauseBtn.Tag = playPause.Index;
                MediaPlayPauseBtn.Visibility = Visibility.Visible;
            }
            else
            {
                MediaPlayPauseBtn.Visibility = Visibility.Collapsed;
            }

            // 收藏/歌词初始态：从 action 文案推断（如“取消收藏/已收藏”说明当前已收藏）
            var favoriteAction = actions.FirstOrDefault(a => a.Name == "favorite");
            if (favoriteAction != null)
            {
                _favoriteActive = ToggleStateFromTitle(favoriteAction,
                    activeKeywords: new[] { "已收藏", "已喜欢", "取消收藏", "取消喜欢", "unfavorite", "liked", "remove" });
            }
            var lyricAction = actions.FirstOrDefault(a => a.Name == "lyric");
            if (lyricAction != null)
            {
                _lyricActive = ToggleStateFromTitle(lyricAction,
                    activeKeywords: new[] { "关闭歌词", "隐藏歌词", "关闭", "隐藏", "off", "hide" });
            }
            UpdateMediaToggleStates();
        }

        // 播放/暂停图标随状态更新（每次更新都刷新）
        MediaPlayPauseGlyph.Text = Message.MediaIsPlaying ? "\u23F8" : "\u25B6";

        MediaPanel.Visibility = Visibility.Visible;
        UpdateMediaProgress();
    }

    /// <summary>
    /// 媒体开关按钮（收藏/歌词）的文案推断：action 文案含“取消/已收藏/关闭”等词时，
    /// 说明该功能当前已处于激活态（action 表示“再次点击会执行的动作”）。
    /// </summary>
    private static bool ToggleStateFromTitle(MediaAction action, string[] activeKeywords)
    {
        var title = action.Title;
        if (string.IsNullOrEmpty(title)) return false;
        return activeKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static void BindMediaButton(Button btn, List<MediaAction> actions, string name)
    {
        var action = actions.FirstOrDefault(a => a.Name == name);
        if (action != null)
        {
            btn.Tag = action.Index;
            btn.Visibility = Visibility.Visible;
        }
        else
        {
            btn.Visibility = Visibility.Collapsed;
        }
    }

    private void MediaAction_Click(object sender, RoutedEventArgs e)
    {
        // 收藏/歌词为开关型按钮：点击即切换本地视觉态
        if (ReferenceEquals(sender, MediaFavoriteBtn))
        {
            _favoriteActive = !_favoriteActive;
            UpdateMediaToggleStates();
        }
        else if (ReferenceEquals(sender, MediaLyricBtn))
        {
            _lyricActive = !_lyricActive;
            UpdateMediaToggleStates();
        }

        if (sender is Button b && b.Tag is int idx)
            _ = NetworkManager.Instance.SendMediaAction(Message.Key, idx);
    }

    /// <summary>按当前开关状态刷新收藏（♡/♥）与歌词（词+对勾角标）的样式。</summary>
    private void UpdateMediaToggleStates()
    {
        if (MediaFavoriteBtn == null) return;

        MediaFavoriteGlyph.Text = _favoriteActive ? "\u2665" : "\u2661";
        MediaFavoriteBtn.Foreground = _favoriteActive ? OrangeBrushStatic : _themePrimaryBrush;

        MediaLyricBtn.Foreground = _lyricActive ? OrangeBrushStatic : _themePrimaryBrush;
        LyricCheckBadge.Visibility = _lyricActive ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>根据消息中的 MediaSession 进度初始化进度条，并启动/停止本地走时计时器。</summary>
    private void UpdateMediaProgress()
    {
        _mediaDurationMs = Message.MediaDurationMs;
        _mediaPlaying = Message.MediaIsPlaying;

        if (_mediaDurationMs > 0)
        {
            _mediaPositionMs = Math.Max(0, Message.MediaPositionMs);
            MediaProgressRow.Visibility = Visibility.Visible;
            MediaTotalTimeText.Text = FormatMediaTime(_mediaDurationMs);
            UpdateMediaProgressFill();
            if (_mediaPlaying) StartMediaTick();
            else StopMediaTick();
        }
        else
        {
            MediaProgressRow.Visibility = Visibility.Collapsed;
            StopMediaTick();
        }
    }

    private void StartMediaTick()
    {
        if (_mediaTickTimer != null) return;
        _mediaTickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _mediaTickTimer.Tick += (_, _) =>
        {
            _mediaPositionMs += 1000;
            if (_mediaDurationMs > 0 && _mediaPositionMs >= _mediaDurationMs)
            {
                _mediaPositionMs = _mediaDurationMs;
                StopMediaTick();
            }
            UpdateMediaProgressFill();
        };
        _mediaTickTimer.Start();
    }

    private void StopMediaTick()
    {
        _mediaTickTimer?.Stop();
        _mediaTickTimer = null;
    }

    private void UpdateMediaProgressFill()
    {
        MediaCurTimeText.Text = FormatMediaTime(_mediaPositionMs);
        double frac = _mediaDurationMs > 0
            ? Math.Clamp((double)_mediaPositionMs / _mediaDurationMs, 0, 1)
            : 0;
        MediaFillScale.ScaleX = frac;
    }

    private static string FormatMediaTime(long ms)
    {
        if (ms < 0) ms = 0;
        var span = TimeSpan.FromMilliseconds(ms);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes:D2}:{span.Seconds:D2}";
    }

    /// <summary>卡片上下内边距基准（XAML 中 RootBorder 纵向 Padding = 18）</summary>
    private const double BaseVerticalPadding = 18;
    /// <summary>头部行下边距基准（XAML 中 HeaderGrid 底部 Margin = 14）</summary>
    private const double BaseHeaderBottomMargin = 14;

    /// <summary>
    /// 按设置的紧凑度系数（0.75 紧凑 ~ 1.25 宽松）缩放卡片纵向留白：
    /// RootBorder 上下内边距与头部行底距。只调间距不调字号，文字排版保持
    /// 稳定；卡片实际高度变化后 SizeChanged 会触发宿主重新堆叠。
    /// 系数从基准常量推导，重复调用不会累加。
    /// </summary>
    public void ApplyDensity()
    {
        double d = Math.Clamp(AppSettings.Instance.CardDensity, 0.75, 1.25);
        RootBorder.Padding = new Thickness(20, BaseVerticalPadding * d, 20, BaseVerticalPadding * d);
        HeaderGrid.Margin = new Thickness(0, 0, 0, BaseHeaderBottomMargin * d);
    }

    public void ApplyTheme()
    {
        bool dark = AppSettings.Instance.DarkMode;

        Color bg = dark ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Colors.White;
        Color primary = dark ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x1D, 0x1D, 0x1F);
        Color secondary = dark ? Color.FromRgb(0x98, 0x98, 0x9F) : Color.FromRgb(0x86, 0x86, 0x8B);
        Color contentColor = dark ? Color.FromRgb(0x98, 0x98, 0x9F) : Color.FromRgb(0x6E, 0x6E, 0x73);
        Color iconBg = dark ? Color.FromRgb(0x2C, 0x2C, 0x2E) : Color.FromRgb(0xF5, 0xF5, 0xF7);

        RootBorder.Background = new SolidColorBrush(bg);
        IconBackgroundBorder.Background = new SolidColorBrush(iconBg);

        AppNameText.Foreground = new SolidColorBrush(primary);
        TimeText.Foreground = new SolidColorBrush(secondary);
        TitleText.Foreground = new SolidColorBrush(primary);
        HintTitleText.Foreground = new SolidColorBrush(secondary);
        ContentText.Foreground = new SolidColorBrush(contentColor);
        HintTextText.Foreground = new SolidColorBrush(secondary);
        ProgressPercentText.Foreground = new SolidColorBrush(secondary);

        // 按钮文字色（通过按钮 Foreground + TemplateBinding 传递到模板内 TextBlock）
        OpenAppButton.Foreground = new SolidColorBrush(secondary);
        CloseButton.Foreground = new SolidColorBrush(secondary);

        _themePrimaryBrush = new SolidColorBrush(primary);

        // 媒体面板
        if (MediaPanel != null)
        {
            MediaPanel.Background = new SolidColorBrush(iconBg);
            Color trackColor = dark ? Color.FromRgb(0x48, 0x48, 0x4A) : Color.FromRgb(0xD1, 0xD1, 0xD6);
            MediaTrack.Background = new SolidColorBrush(trackColor);
            MediaCurTimeText.Foreground = new SolidColorBrush(secondary);
            MediaTotalTimeText.Foreground = new SolidColorBrush(secondary);

            // 基础按钮色（收藏/歌词随后由 UpdateMediaToggleStates 覆盖为激活橙色）
            var mediaBrush = new SolidColorBrush(primary);
            foreach (var b in new[] { MediaFavoriteBtn, MediaPrevBtn, MediaPlayPauseBtn, MediaNextBtn, MediaLyricBtn })
            {
                b.Foreground = mediaBrush;
            }
            UpdateMediaToggleStates();
        }
    }

    private static string? ExtractPickupCode(NotificationMessage msg)
    {
        string labelToSearch = msg.Progress?.Label ?? "";
        string combined = $"{msg.Title} {msg.Content} {msg.BigText} {msg.HintText} {labelToSearch}";

        var patterns = new[]
        {
            new Regex(@"取餐码[：: ]*([A-Za-z0-9\-]+)"),
            new Regex(@"取餐号[：: ]*([A-Za-z0-9\-]+)"),
            new Regex(@"取餐柜[：: ]*([A-Za-z0-9\-]+)"),
            new Regex(@"取货码[：: ]*([A-Za-z0-9\-]+)"),
            new Regex(@"提货码[：: ]*([A-Za-z0-9\-]+)"),
            new Regex(@"柜号[：: ]*([A-Za-z0-9\-]+)"),
            new Regex(@"码号[：: ]*([A-Za-z0-9\-]+)")
        };

        foreach (var pattern in patterns)
        {
            var m = pattern.Match(combined);
            if (m.Success && m.Groups[1].Value.Length > 0)
                return m.Groups[1].Value;
        }
        return null;
    }

    /// <summary>
    /// 从通知文本中提取短信验证码（4-6 位数字）。
    /// 支持中文“验证码/校验码/动态码”与英文 verification/security code。
    /// </summary>
    public static string? ExtractVerificationCode(NotificationMessage msg)
    {
        string combined = $"{msg.Title} {msg.Content} {msg.BigText} {msg.HintText} {msg.Ticker}";
        if (string.IsNullOrWhiteSpace(combined)) return null;

        var directPatterns = new[]
        {
            new Regex(@"验证码[：:是\s]*([0-9]{4,6})"),
            new Regex(@"校验码[：:是\s]*([0-9]{4,6})"),
            new Regex(@"动态码[：:是\s]*([0-9]{4,6})"),
            new Regex(@"确认码[：:是\s]*([0-9]{4,6})"),
            new Regex(@"激活码[：:是\s]*([0-9]{4,6})"),
            new Regex(@"verification\s*code[^0-9]{0,20}?([0-9]{4,6})", RegexOptions.IgnoreCase),
            new Regex(@"security\s*code[^0-9]{0,20}?([0-9]{4,6})", RegexOptions.IgnoreCase),
            new Regex(@"code\s*(?:is|:)?\s*([0-9]{4,6})", RegexOptions.IgnoreCase)
        };

        foreach (var pattern in directPatterns)
        {
            var m = pattern.Match(combined);
            if (m.Success && m.Groups[1].Value.Length > 0)
                return m.Groups[1].Value;
        }

        // 关键词兜底：文本中出现验证码相关字样时，取第一个 4-6 位独立数字串
        string[] keywords = { "验证码", "校验码", "动态码", "确认码", "激活码", "verification", "security code" };
        if (Array.Exists(keywords, k => combined.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            var m = Regex.Match(combined, @"(?<![0-9])([0-9]{4,6})(?![0-9])");
            if (m.Success) return m.Groups[1].Value;
        }

        return null;
    }

    private void CopyCodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_verificationCode)) return;
        try
        {
            Clipboard.SetText(_verificationCode);
            CopyCodeText.Text = "已复制";

            _copyFeedbackTimer?.Stop();
            _copyFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            _copyFeedbackTimer.Tick += (s, ev) =>
            {
                _copyFeedbackTimer?.Stop();
                CopyCodeText.Text = "复制验证码";
            };
            _copyFeedbackTimer.Start();
        }
        catch
        {
            // 剪贴板被其他程序占用时忽略
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => DismissRequested?.Invoke(this, EventArgs.Empty);

    private void OpenAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(Message.PackageName))
            _ = NetworkManager.Instance.SendOpenApp(Message.PackageName);
    }

    private string DisplayTitle { get; set; } = "";
    private string DisplayContent { get; set; } = "";
    private string DisplayHintTitle { get; set; } = "";
    private string DisplayHintText { get; set; } = "";

    private string ResolveTitle()
    {
        if (Message.BigTitle.Length > 0) return Message.BigTitle;
        if (Message.HintTitle.Length > 0 && Message.Title == Message.Content) return Message.HintTitle;
        // 标题为空时退用 ticker（原橙色胶囊已移除，信息并入标题不丢内容）
        if (Message.Title.Length == 0 && Message.Ticker.Length > 0) return Message.Ticker;
        return Message.Title;
    }

    private string ResolveContent()
    {
        if (Message.BigText.Length > 0) return Message.BigText;
        if (Message.Content.Length > 0 && Message.Content != Message.Title) return Message.Content;
        return string.Empty;
    }

    private string ResolveHintTitle()
    {
        if (Message.HintTitle.Length > 0 && Message.HintTitle != ResolveTitle()) return Message.HintTitle;
        if (Message.SubText.Length > 0) return Message.SubText;
        return string.Empty;
    }

    private string ResolveHintText()
    {
        if (Message.HintText.Length > 0 && Message.HintText != ResolveContent()) return Message.HintText;
        return string.Empty;
    }

    private void LoadIcon()
    {
        if (string.IsNullOrEmpty(Message.IconBase64))
        {
            AppIconImage.Source = CreateDefaultIcon();
            return;
        }
        try
        {
            byte[] bytes = Convert.FromBase64String(Message.IconBase64);
            using var ms = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            AppIconImage.Source = bitmap;
        }
        catch
        {
            AppIconImage.Source = CreateDefaultIcon();
        }
    }

    private ImageSource CreateDefaultIcon()
    {
        var bmp = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            ctx.DrawRectangle(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF5F5F7")), null, new Rect(0, 0, 64, 64));
            var text = new FormattedText(
                Message.AppName.Length > 0 ? Message.AppName[0].ToString().ToUpper() : "M",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 26,
                new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFF6900")),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            ctx.DrawText(text, new Point(19, 13));
        }
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    private void BuildStageBar(int current, int total, string category = "delivery")
    {
        StageBar.Children.Clear();
        int segments = total - 1;
        double lineWidth = total <= 6 ? 56 : 42;

        for (int i = 0; i < segments; i++)
        {
            int left = i;
            int right = i + 1;
            bool leftDone = left <= current;
            bool rightDone = right <= current;

            var leftEllipse = new Ellipse
            {
                Width = 12, Height = 12,
                Fill = leftDone ? OrangeBrush : GrayBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            StageBar.Children.Add(leftEllipse);

            if (i < segments - 1)
            {
                var line = new Rectangle
                {
                    Width = lineWidth, Height = 3,
                    RadiusX = 2, RadiusY = 2,
                    Fill = leftDone && rightDone ? OrangeBrush : GrayBrush,
                    VerticalAlignment = VerticalAlignment.Center
                };
                StageBar.Children.Add(line);
            }
        }

        var lastEllipse = new Ellipse
        {
            Width = 12, Height = 12,
            Fill = current >= segments ? OrangeBrush : GrayBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        StageBar.Children.Add(lastEllipse);
    }

    private static readonly SolidColorBrush OrangeBrush = new((Color)ColorConverter.ConvertFromString("#FFFF6900"));
    private static readonly SolidColorBrush GrayBrush = new((Color)ColorConverter.ConvertFromString("#FFE5E5EA"));

    private DateTime _lastFlash = DateTime.MinValue;
    private static readonly TimeSpan FlashThrottle = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// 更新已有卡片的数据。持续通知（音乐进度/流量统计等高频更新）只静默刷新内容，
    /// 不闪烁、不重建按钮；普通通知的更新闪烁做 1.5s 限流，避免同一应用连续推送时卡片不停闪。
    /// </summary>
    public void UpdateFrom(NotificationMessage newMessage)
    {
        bool iconChanged = Message.IconBase64 != newMessage.IconBase64;
        Message = newMessage;
        LoadData();
        if (iconChanged) LoadIcon();

        if (!newMessage.IsOngoing && DateTime.Now - _lastFlash > FlashThrottle)
        {
            _lastFlash = DateTime.Now;
            PlayUpdateFlash();
        }
    }

    public void PlayShowAnimation()
    {
        CardScale.ScaleX = 0.8;
        CardScale.ScaleY = 0.8;
        CardTranslate.Y = -40;
        RootBorder.Opacity = 0;

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        // 注意：不能用 Storyboard.SetTarget 定向到 Freezable（ScaleTransform/TranslateTransform）
        // 再无参 Begin()——WPF 不会驱动这些时钟，动画会永久停在初始值（卡片缩小上浮、
        // 布局却按全尺寸堆叠，表现为卡片之间出现大段空隙）。直接对属性挂动画才可靠。
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.8, 1.0, TimeSpan.FromMilliseconds(320)) { EasingFunction = easing });
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.8, 1.0, TimeSpan.FromMilliseconds(320)) { EasingFunction = easing });
        CardTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-40, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = easing });
        RootBorder.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
    }

    public void PlayUpdateFlash()
    {
        var orange = (Color)ColorConverter.ConvertFromString("#FFFF6900");
        FlashBorderBrush.Color = orange;
        RootBorder.BorderThickness = new Thickness(2, 2, 2, 2);

        FlashBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(orange, Colors.Transparent, TimeSpan.FromMilliseconds(600)));
        RootBorder.BeginAnimation(Border.BorderThicknessProperty,
            new ThicknessAnimation(new Thickness(2, 2, 2, 2), new Thickness(0, 0, 0, 0),
                TimeSpan.FromMilliseconds(600)));
    }

    public void PlayHideAnimation(EventHandler? onComplete = null)
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };

        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1.0, 0.9, TimeSpan.FromMilliseconds(220)));
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1.0, 0.9, TimeSpan.FromMilliseconds(220)));
        CardTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, -20, TimeSpan.FromMilliseconds(250)) { EasingFunction = easing });

        // 用 AnimationClock 的 Completed 回调移除卡片（等价于原 Storyboard.Completed）
        var fadeAnim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        var clock = fadeAnim.CreateClock();
        clock.Completed += (_, _) => onComplete?.Invoke(this, EventArgs.Empty);
        RootBorder.ApplyAnimationClock(UIElement.OpacityProperty, clock);
    }
}
