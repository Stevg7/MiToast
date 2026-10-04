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

    /// <summary>封面方块缩略态边长与下方留白；放大态边长 = 卡片内容宽。</summary>
    private const double CompactCoverSize = 84;
    private const double CoverBottomGap = 12;
    private const double CoverCornerRadius = 16;
    private bool _coverExpanded;
    private bool _coverAnimRunning;

    private Brush? _themePrimaryBrush;

    /// <summary>HyperOS 主色（miuix 令牌）：浅 #3482FF / 深 #277AF7，按当前主题取值。</summary>
    private static Color AccentColor => AppSettings.Instance.DarkMode
        ? Color.FromRgb(0x27, 0x7A, 0xF7)
        : Color.FromRgb(0x34, 0x82, 0xFF);

    // 收藏两态几何（miuix Favorites / FavoritesFill Regular 轮廓；Compose 的 scaleY=-1 已烘焙进坐标）
    private static readonly Geometry HeartOutlineGeometry = Geometry.Parse(
        "F1 M 680.9,1071.9 L 675.9,1067.9 L 664.9,1056.9 Q 652.9,1044.9 641.9,1056.9 Q 634.9,1064.9 625.9,1071.9 Q 583.9,1105.9 532.4,1124.9 Q 480.9,1143.9 424.9,1143.9 Q 338.9,1143.9 266.4,1101.9 Q 193.9,1059.9 151.4,987.9 Q 108.9,915.9 108.9,829.9 Q 108.9,739.9 155.9,664.9 Q 211.9,569.9 339.4,440.9 Q 466.9,311.9 596.9,196.9 Q 609.9,184.9 619.9,177.4 Q 629.9,169.9 637.9,167.9 Q 654.9,162.9 670.9,167.9 Q 677.9,169.9 688.4,177.9 Q 698.9,185.9 711.9,197.9 Q 835.9,308.9 958.9,431.9 Q 1081.9,554.9 1143.9,651.9 Q 1197.9,730.9 1197.9,829.9 Q 1197.9,915.9 1155.4,987.9 Q 1112.9,1059.9 1040.9,1101.9 Q 968.9,1143.9 882.9,1143.9 Q 826.9,1143.9 774.9,1124.9 Q 722.9,1105.9 680.9,1071.9 Z M 1111.9,829.9 Q 1111.9,758.9 1072.9,699.9 Q 1025.9,627.9 940.9,538.4 Q 855.9,448.9 791.9,387.9 Q 730.9,329.9 668.9,274.9 Q 658.9,265.9 653.9,265.9 Q 648.9,265.9 638.9,274.9 Q 571.9,334.9 506.9,396.9 Q 291.9,601.9 228.9,710.9 Q 208.9,743.9 202.4,770.9 Q 195.9,797.9 195.9,829.9 Q 195.9,891.9 226.4,944.4 Q 256.9,996.9 309.9,1027.4 Q 362.9,1057.9 424.9,1057.9 Q 472.9,1057.9 515.9,1038.9 Q 558.9,1019.9 590.9,985.9 Q 603.9,971.9 613.9,962.4 Q 623.9,952.9 631.9,949.9 Q 653.9,940.9 675.9,949.9 Q 683.9,953.9 693.9,962.9 Q 703.9,971.9 716.9,985.9 Q 747.9,1019.9 791.4,1038.9 Q 834.9,1057.9 882.9,1057.9 Q 944.9,1057.9 997.9,1027.4 Q 1050.9,996.9 1081.4,944.4 Q 1111.9,891.9 1111.9,829.9 Z");
    private static readonly Geometry HeartFilledGeometry = Geometry.Parse(
        "F1 M 680.9,1071.9 L 675.9,1067.9 L 664.9,1056.9 Q 652.9,1044.9 641.9,1056.9 Q 634.9,1064.9 625.9,1071.9 Q 583.9,1105.9 532.4,1124.9 Q 480.9,1143.9 424.9,1143.9 Q 338.9,1143.9 266.4,1101.9 Q 193.9,1059.9 151.4,987.9 Q 108.9,915.9 108.9,829.9 Q 108.9,739.9 155.9,664.9 Q 211.9,569.9 339.4,440.9 Q 466.9,311.9 596.9,196.9 Q 609.9,184.9 619.9,177.4 Q 629.9,169.9 637.9,167.9 Q 654.9,162.9 670.9,167.9 Q 677.9,169.9 688.4,177.9 Q 698.9,185.9 711.9,197.9 Q 835.9,308.9 958.9,431.9 Q 1081.9,554.9 1143.9,651.9 Q 1197.9,730.9 1197.9,829.9 Q 1197.9,915.9 1155.4,987.9 Q 1112.9,1059.9 1040.9,1101.9 Q 968.9,1143.9 882.9,1143.9 Q 826.9,1143.9 774.9,1124.9 Q 722.9,1105.9 680.9,1071.9 Z");

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
        if (CoverGrid.Visibility == Visibility.Visible)
        {
            h += (_coverExpanded ? ExpandedCoverSize() : CompactCoverSize) + CoverBottomGap;
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
            // 提取逻辑在 NotificationText（UI 卡片、历史列表与本地总结共用）
            string? pickupCode = NotificationText.ExtractPickupCode(Message);
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
            _verificationCode = NotificationText.ExtractVerificationCode(Message);
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
            CoverGrid.Visibility = Visibility.Collapsed;
            _coverExpanded = false;
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
        // HyperOS 图标：播放/暂停两枚矢量按状态切换（miuix Play/Pause）
        MediaPlayIcon.Visibility = Message.MediaIsPlaying ? Visibility.Collapsed : Visibility.Visible;
        MediaPauseIcon.Visibility = Message.MediaIsPlaying ? Visibility.Visible : Visibility.Collapsed;

        // 封面随媒体面板一起出现；仅在刚显示时落位尺寸，动画途中的高频更新不打断
        if (CoverGrid.Visibility != Visibility.Visible)
        {
            CoverGrid.Visibility = Visibility.Visible;
            ApplyCoverLayout(animate: false);
        }
        UpdateCoverToggleGlyph();

        MediaPanel.Visibility = Visibility.Visible;
        UpdateMediaProgress();
    }

    /// <summary>放大态封面边长 = 卡片内容宽（RootBorder 左右内边距各 20）。</summary>
    private double ExpandedCoverSize()
        => Math.Max(CompactCoverSize, (Width > 0 ? Width : AppSettings.Instance.CardWidth) - 40);

    private void Cover_Click(object sender, MouseButtonEventArgs e) => ToggleCoverExpand();

    private void CoverToggleBtn_Click(object sender, RoutedEventArgs e) => ToggleCoverExpand();

    /// <summary>在缩略封面与整宽大封面之间切换，切换过程用尺寸动画过渡。</summary>
    private void ToggleCoverExpand()
    {
        _coverExpanded = !_coverExpanded;
        UpdateCoverToggleGlyph();
        ApplyCoverLayout(animate: true);
    }

    /// <summary>切换按钮的图标与提示：缩略态给“放大”，放大态给“收起”。</summary>
    private void UpdateCoverToggleGlyph()
    {
        if (CoverToggleGlyph == null) return;
        CoverToggleGlyph.Text = _coverExpanded ? "\uE73E" : "\uE740";
        string tip = _coverExpanded ? "收起封面" : "放大封面";
        CoverToggleBtn.ToolTip = tip;
        CoverBorder.ToolTip = tip;
    }

    /// <summary>封面按圆角裁切；放大/收起动画逐帧改尺寸，这里同步更新裁切几何。</summary>
    private void CoverGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        CoverBorder.Clip = new RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
            CoverCornerRadius, CoverCornerRadius);
    }

    /// <summary>
    /// 按当前展开态套用封面尺寸。animate=false 直接落位（首次装载、设置变化），
    /// 动画进行中收到的落位请求不打断，交给动画完成回调重算落位；
    /// animate=true 走尺寸动画，完成后释放时钟再落位，避免 FillBehavior 挂住后续布局。
    /// </summary>
    private void ApplyCoverLayout(bool animate)
    {
        if (CoverGrid == null || CoverGrid.Visibility != Visibility.Visible) return;

        if (!animate || !IsLoaded)
        {
            if (_coverAnimRunning) return;
            CoverGrid.BeginAnimation(FrameworkElement.WidthProperty, null);
            CoverGrid.BeginAnimation(FrameworkElement.HeightProperty, null);
            double size = _coverExpanded ? ExpandedCoverSize() : CompactCoverSize;
            CoverGrid.Width = size;
            CoverGrid.Height = size;
            return;
        }

        // 快速连点时从动画当前值起跳（Width/Height 读到的就是动画值），避免跳变
        double fromW = CoverGrid.Width;
        double fromH = CoverGrid.Height;
        double to = _coverExpanded ? ExpandedCoverSize() : CompactCoverSize;
        var duration = TimeSpan.FromMilliseconds(320);
        var animW = new DoubleAnimation(fromW, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        var animH = new DoubleAnimation(fromH, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        animH.Completed += (_, _) =>
        {
            _coverAnimRunning = false;
            CoverGrid.BeginAnimation(FrameworkElement.WidthProperty, null);
            CoverGrid.BeginAnimation(FrameworkElement.HeightProperty, null);
            // 动画途中卡片宽度设置可能变过，落位时重算，不沿用起跳时的目标值
            double size = _coverExpanded ? ExpandedCoverSize() : CompactCoverSize;
            CoverGrid.Width = size;
            CoverGrid.Height = size;
        };
        _coverAnimRunning = true;
        CoverGrid.BeginAnimation(FrameworkElement.WidthProperty, animW);
        CoverGrid.BeginAnimation(FrameworkElement.HeightProperty, animH);
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

        // HyperOS 图标：收藏描边/实心切换（miuix Favorites/FavoritesFill）
        MediaFavoriteGlyph.Data = _favoriteActive ? HeartFilledGeometry : HeartOutlineGeometry;
        MediaFavoriteBtn.Foreground = _favoriteActive ? (Brush)Resources["ToastAccentBrush"] : _themePrimaryBrush;

        MediaLyricBtn.Foreground = _lyricActive ? (Brush)Resources["ToastAccentBrush"] : _themePrimaryBrush;
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
        // 封面放大态边长跟卡片宽度走，设置变化后在这里重算落位
        ApplyCoverLayout(animate: false);
    }

    /// <summary>应用深浅色配色。深色模式切换时带渐出渐入过场，其余设置变化直接换色。</summary>
    public void ApplyTheme()
        => ThemeTransition.Apply(this, AppSettings.Instance.DarkMode, ApplyThemeColors);

    private void ApplyThemeColors()
    {
        bool dark = AppSettings.Instance.DarkMode;

        // HyperOS（miuix 令牌）：深色卡片 #242424、主文字 #F2F2F2、副标题 50% 白；浅色副标题 60% 黑
        Color bg = dark ? Color.FromRgb(0x24, 0x24, 0x24) : Colors.White;
        Color primary = dark ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Colors.Black;
        Color secondary = dark ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x00, 0x00, 0x00);
        Color contentColor = dark ? Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x66, 0x00, 0x00, 0x00);
        Color iconBg = dark ? Color.FromRgb(0x2D, 0x2D, 0x2D) : Color.FromRgb(0xF0, 0xF0, 0xF0);
        Color accent = dark ? Color.FromRgb(0x27, 0x7A, 0xF7) : Color.FromRgb(0x34, 0x82, 0xFF);
        Color accentHover = dark ? Color.FromRgb(0x4B, 0x8C, 0xF8) : Color.FromRgb(0x2B, 0x74, 0xE8);
        Color track = dark ? Color.FromRgb(0x4A, 0x4A, 0x4A) : Color.FromRgb(0xE6, 0xE6, 0xE6);

        // 主色/图标底/轨道在模板里以 DynamicResource 引用，随主题整体替换实例
        Resources["ToastAccentBrush"] = new SolidColorBrush(accent);
        Resources["ToastAccentHoverBrush"] = new SolidColorBrush(accentHover);
        Resources["ToastAccentSoftBrush"] = new SolidColorBrush(dark
            ? Color.FromArgb(0x33, 0x27, 0x7A, 0xF7)
            : Color.FromArgb(0x22, 0x34, 0x82, 0xFF));
        Resources["ToastIconBgBrush"] = new SolidColorBrush(iconBg);
        Resources["ToastTrackBrush"] = new SolidColorBrush(track);

        RootBorder.Background = new SolidColorBrush(bg);
        IconBackgroundBorder.Background = new SolidColorBrush(iconBg);
        CoverBorder.Background = new SolidColorBrush(iconBg);

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

        _themePrimaryBrush = new SolidColorBrush(secondary);

        // 媒体面板
        if (MediaPanel != null)
        {
            MediaPanel.Background = new SolidColorBrush(iconBg);
            MediaTrack.Background = new SolidColorBrush(track);
            MediaCurTimeText.Foreground = new SolidColorBrush(secondary);
            MediaTotalTimeText.Foreground = new SolidColorBrush(secondary);

            // 基础按钮色（HyperOS 媒体控件：未激活置灰，激活态由 UpdateMediaToggleStates 提亮为主色）
            var mediaBrush = new SolidColorBrush(secondary);
            foreach (var b in new[] { MediaFavoriteBtn, MediaPrevBtn, MediaPlayPauseBtn, MediaNextBtn, MediaLyricBtn })
            {
                b.Foreground = mediaBrush;
            }
            UpdateMediaToggleStates();
        }
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
        ImageSource source;
        if (string.IsNullOrEmpty(Message.IconBase64))
        {
            source = CreateDefaultIcon();
        }
        else
        {
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
                source = bitmap;
            }
            catch
            {
                source = CreateDefaultIcon();
            }
        }

        AppIconImage.Source = source;
        // 媒体通知的大图就是专辑封面，与头部小图标共用同一张图
        CoverImage.Source = source;
    }

    private ImageSource CreateDefaultIcon()
    {
        var bmp = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            ctx.DrawRectangle(new SolidColorBrush(AppSettings.Instance.DarkMode
                ? Color.FromRgb(0x2D, 0x2D, 0x2D)
                : Color.FromRgb(0xF0, 0xF0, 0xF0)), null, new Rect(0, 0, 64, 64));
            // 应用名首字可能是中文（微信、哔哩哔哩），字体与界面一致，避免退化成系统兜底字体
            var text = new FormattedText(
                Message.AppName.Length > 0 ? Message.AppName[0].ToString().ToUpper() : "M",
                CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface((FontFamily)FindResource("UiFontFamily"),
                    FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 26,
                new SolidColorBrush(AccentColor),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            // 按实际字形居中，换字体后字宽变化也不会偏
            ctx.DrawText(text, new Point((64 - text.Width) / 2, (64 - text.Height) / 2));
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
                Fill = leftDone ? AccentDotBrush : TrackDotBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            StageBar.Children.Add(leftEllipse);

            if (i < segments - 1)
            {
                var line = new Rectangle
                {
                    Width = lineWidth, Height = 3,
                    RadiusX = 2, RadiusY = 2,
                    Fill = leftDone && rightDone ? AccentDotBrush : TrackDotBrush,
                    VerticalAlignment = VerticalAlignment.Center
                };
                StageBar.Children.Add(line);
            }
        }

        var lastEllipse = new Ellipse
        {
            Width = 12, Height = 12,
            Fill = current >= segments ? AccentDotBrush : TrackDotBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        StageBar.Children.Add(lastEllipse);
    }

    // 阶段圆点用色：填充=主色、未填充=轨道色。属性每次取值新建实例（重建频率低，可接受）
    private static SolidColorBrush AccentDotBrush => new(AccentColor);
    private static SolidColorBrush TrackDotBrush => new(TrackColor);

    /// <summary>轨道/未填充色（阶段圆点）：深浅色各一套。</summary>
    private static Color TrackColor => AppSettings.Instance.DarkMode
        ? Color.FromRgb(0x50, 0x50, 0x50)
        : Color.FromRgb(0xE6, 0xE6, 0xE6);

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
        var accent = AccentColor;
        FlashBorderBrush.Color = accent;
        RootBorder.BorderThickness = new Thickness(2, 2, 2, 2);

        FlashBorderBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(accent, Colors.Transparent, TimeSpan.FromMilliseconds(600)));
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
