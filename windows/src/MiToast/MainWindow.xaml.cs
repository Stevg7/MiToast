using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MiToast.Models;
using MiToast.Network;
using MiToast.Services;
using MiToast.UI;
using WinForms = System.Windows.Forms;

namespace MiToast;

public partial class MainWindow : Window
{
    private const int AutoDismissMs = 9000;

    private readonly Dictionary<string, MiFocusNotification> _activeByKey = new();
    // 空闲内存回收：最后一张卡片关闭 30 秒后做一次压缩 GC 并把工作集还给系统
    private readonly DispatcherTimer _idleTrimTimer;
    private readonly Dictionary<string, CancellationTokenSource> _dismissCts = new();

    /// <summary>上一次 ApplySettings 时的音乐/外卖常驻开关，用于识别"刚取消常驻"这一次变化。</summary>
    private bool _musicPersistentApplied;
    private bool _deliveryPersistentApplied;

    private WinForms.NotifyIcon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        _musicPersistentApplied = Settings.MusicPersistent;
        _deliveryPersistentApplied = Settings.DeliveryPersistent;
        NetworkManager.Instance.NotificationReceived += OnNotificationReceived;
        NetworkManager.Instance.NotificationCleared += OnNotificationCleared;
        NetworkManager.Instance.StatusChanged += OnConnectionStatusChanged;
        NetworkManager.Instance.HintRequested += OnConnectionHint;
        Loaded += MainWindow_Loaded;
        AppSettings.Instance.Changed += (_, _) => Dispatcher.Invoke(ApplySettings);

        // 空闲 30 秒回收：启动 JIT 稳定后先收一次；此后每张卡片关闭都会重排，
        // 只有全部关干净且无人打扰 30 秒才真正执行（有卡片在屏时不做）
        _idleTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _idleTrimTimer.Tick += (_, _) =>
        {
            _idleTrimTimer.Stop();
            if (_activeByKey.Count == 0)
            {
                MemoryTrim.TrimNow();
            }
        };

        // 周期兜底：长期空闲时零星后台活动（WebSocket 心跳、状态刷新）会重新撑高工作集。
        // 无条件回收——常驻媒体卡在屏时也裁：静态卡片不重绘，被裁掉的页只有交互瞬间
        // 缺页回来（毫秒级），换来的是常驻占用稳定贴着基线
        var periodicTrim = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        periodicTrim.Tick += (_, _) => ScheduleIdleTrim();
        periodicTrim.Start();
    }

    /// <summary>重排空闲回收计时（卡片关闭、启动就绪时调用；30 秒无卡片才真正回收）。</summary>
    private void ScheduleIdleTrim()
    {
        _idleTrimTimer.Stop();
        _idleTrimTimer.Start();
    }

    private static AppSettings Settings => AppSettings.Instance;

    private bool IsRightSide =>
        Settings.Position is "TopRight" or "BottomRight";

    private bool IsBottomSide =>
        Settings.Position is "BottomRight" or "BottomLeft";

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySettings();
        ScheduleIdleTrim(); // 启动 JIT 稳定后收一次工作集
        InitializeTrayIcon();
        StartFullscreenWatcher();
        UpdateTrayStatus();
    }

    // ---------- 连接状态展示 ----------

    /// <summary>
    /// 托盘提示与托盘菜单里的状态行。连不上手机时这是用户唯一能看到的信息来源，
    /// 否则程序只会安静地一条通知都不弹。
    /// </summary>
    private void UpdateTrayStatus()
    {
        if (_trayIcon == null) return;

        var manager = NetworkManager.Instance;
        string status = manager.StatusText;

        // NotifyIcon.Text 有 63 字符上限，超了会抛异常
        const int maxTooltip = 63;
        string tooltip = $"MiToast 通知同步\n{status}";
        _trayIcon.Text = tooltip.Length > maxTooltip ? tooltip[..maxTooltip] : tooltip;

        if (FindResource("TrayContextMenu") is ContextMenu menu
            && FindMenuItem(menu, "status") is { } statusItem)
        {
            statusItem.Header = status;
        }
    }

    private void OnConnectionStatusChanged(object? sender, string status) => Dispatcher.Invoke(UpdateTrayStatus);

    /// <summary>长时间连不上手机时弹一次气泡提示（校园网屏蔽广播等场景下的引导）。</summary>
    private void OnConnectionHint(object? sender, ConnectionHint hint)
    {
        Dispatcher.Invoke(() =>
        {
            if (_trayIcon == null) return;
            // 弹之前再确认一次：提示从后台线程排队到 UI 线程期间可能刚刚连上手机，
            // 这时候再弹"没找到手机"就是误报。
            if (NetworkManager.Instance.IsConnected) return;
            _trayIcon.BalloonTipTitle = hint.Title;
            _trayIcon.BalloonTipText = hint.Text;
            _trayIcon.ShowBalloonTip(10000);
        });
    }

    /// <summary>
    /// 根据设置重排通知宿主窗口与卡片：
    /// 宿主窗口覆盖整个工作区（不含任务栏），卡片在窗口内按对齐方式 + 屏幕边距布局；
    /// 空白处鼠标穿透由两层保证：UpdateWindowRegion() 把窗口区域裁到卡片外扩矩形
    /// （容纳阴影），WM_NCHITTEST 钩子再按卡片实体矩形精确判定，缝隙/阴影区一律穿透。
    /// </summary>
    private void ApplySettings()
    {
        var workArea = SystemParameters.WorkArea;

        Left = workArea.Left;
        Top = workArea.Top;
        Width = workArea.Width;
        Height = workArea.Height;

        // 刚取消常驻：屏幕上正显示的对应卡片直接移出屏幕。这里不能只"补建计时器"——
        // 手机端的媒体通知在播放/暂停时都带 ongoing 标记（不可划掉），卡片会一直留在桌面上，
        // 直到手机把媒体通知划掉；暂停状态下这件事可能永远不会发生。
        bool musicTurnedOff = _musicPersistentApplied && !Settings.MusicPersistent;
        bool deliveryTurnedOff = _deliveryPersistentApplied && !Settings.DeliveryPersistent;
        _musicPersistentApplied = Settings.MusicPersistent;
        _deliveryPersistentApplied = Settings.DeliveryPersistent;
        List<string>? unpinnedMedia = musicTurnedOff || deliveryTurnedOff ? new List<string>() : null;

        foreach (var kvp in _activeByKey)
        {
            kvp.Value.Width = Settings.CardWidth;
            kvp.Value.ApplyTheme();
            kvp.Value.ApplyDensity();

            // 音乐/外卖常驻开关变化时同步自动消失计时器：
            // 常驻卡片取消计时；其余卡片在缺失计时器时补建（已有倒计时不重置，
            // 否则改一次设置就等于给所有卡片续一次命）。
            if (IsPinnedMedia(kvp.Value.Message))
            {
                CancelAutoDismiss(kvp.Key);
            }
            else if (unpinnedMedia != null &&
                     ((musicTurnedOff && kvp.Value.IsMediaCard) ||
                      (deliveryTurnedOff && kvp.Value.Message.Category == "delivery")))
            {
                // 只清理"刚被取消常驻"的那一类卡片，不波及 ongoing 等本来就不常驻的卡片
                unpinnedMedia.Add(kvp.Key);
            }
            else if (NeedsAutoDismiss(kvp.Value.Message) && !_dismissCts.ContainsKey(kvp.Key))
            {
                StartAutoDismiss(kvp.Key);
            }
        }

        // DismissByKey 会改动 _activeByKey，遍历结束后再统一移除
        if (unpinnedMedia != null)
        {
            foreach (string key in unpinnedMedia)
            {
                DismissByKey(key);
            }
        }

        RecalculateOffsets();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

            // 跨显示器 DPI 变化后，命中区域的设备像素坐标需要重建；
            // 宿主几何与卡片堆叠也要重新贴齐工作区（WPF 会按新 DPI 重缩放窗口，
            // 拔插显示器后窗口还可能被系统挪回可见区，二者都偏离 ApplySettings 的落位）
            var source = HwndSource.FromHwnd(hwnd);
            if (source != null)
            {
                source.AddHook(WndProc);
                source.DpiChanged += (_, _) =>
                {
                    Dispatcher.Invoke(ScheduleRegionUpdate);
                    ScheduleDisplayReapply();
                };
            }
        }
        catch
        {
        }
    }

    private void OnNotificationReceived(object? sender, NotificationMessage message)
    {
        Dispatcher.Invoke(() =>
        {
            HistoryService.Instance.Add(message);

            // 勿扰：手动开关，或已开启「同步手机端勿扰」且手机正处于勿扰
            bool suppress = Settings.DndEnabled
                || (Settings.DndSyncPhone && NetworkManager.Instance.PhoneDndEnabled);

            if (suppress) return;
            ShowOrUpdateNotification(message);
        });
    }

    private void OnNotificationCleared(object? sender, string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        Dispatcher.Invoke(() =>
        {
            // 常驻模式下（音乐/媒体、外卖）卡片忽略手机端的清除，
            // 只有用户手动点卡片关闭按钮（DismissRequested → DismissByKey）才会移除。
            // 未开启常驻时卡片与其他通知一样，跟着手机端清除一起消失。
            if (_activeByKey.TryGetValue(key, out var card) && IsPinnedMedia(card.Message))
            {
                return;
            }
            DismissByKey(key);
        });
    }

    /// <summary>卡片是否需要常驻：音乐常驻开 + 媒体卡，或外卖常驻开 + 外卖配送卡。</summary>
    private static bool IsPinnedMedia(NotificationMessage message)
    {
        var settings = AppSettings.Instance;
        if (settings.MusicPersistent && message.MediaActions is { Count: > 0 }) return true;
        return settings.DeliveryPersistent && message.Category == "delivery";
    }

    /// <summary>
    /// 是否给卡片挂自动消失计时器。false 表示卡片常驻，只能手动关闭或等手机端清除。
    /// 常驻判定：音乐/媒体卡按音乐常驻开关，外卖配送卡按外卖常驻开关；
    /// 非媒体通知沿用手机端的 ongoing 标记（下载、导航等持续通知不消失）；
    /// 媒体通知不常驻时不再看 ongoing——播放/暂停中的媒体通知在手机端一律不可划掉，
    /// 按 ongoing 处理会让"取消常驻"后的音乐卡片永久停留在屏幕上。
    /// </summary>
    private static bool NeedsAutoDismiss(NotificationMessage message)
        => !IsPinnedMedia(message) && (message.MediaActions is { Count: > 0 } || !message.IsOngoing);

    private void ShowOrUpdateNotification(NotificationMessage message)
    {
        string key = message.Key.Length > 0 ? message.Key : message.Id;

        if (_activeByKey.TryGetValue(key, out var existing))
        {
            existing.UpdateFrom(message);
            existing.Width = Settings.CardWidth;
            RecalculateOffsets();
            ResetAutoDismiss(key, persistent: !NeedsAutoDismiss(message));
            return;
        }

        var notification = new MiFocusNotification(message)
        {
            Width = Settings.CardWidth,
            HorizontalAlignment = IsRightSide ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = IsBottomSide ? VerticalAlignment.Bottom : VerticalAlignment.Top,
            Tag = key
        };
        notification.DismissRequested += (_, _) => DismissByKey(key);

        // 卡片自身 Loaded 回调里会执行 LoadData（展开内容面板、确定真实高度），
        // 其后再重排偏移，避免按折叠高度堆叠导致卡片重叠。
        notification.Loaded += (_, _) => RecalculateOffsets();
        // 卡片内容变化（媒体进度行展开/折叠、文案行数变化等）会改变卡片实际高度，需同步重排与命中区域。
        notification.SizeChanged += (_, _) => RecalculateOffsets();

        NotificationHost.Children.Add(notification);
        _activeByKey[key] = notification;

        RecalculateOffsets();

        if (NeedsAutoDismiss(message))
        {
            StartAutoDismiss(key);
        }
    }

    /// <summary>
    /// 按当前设置重排所有卡片：左右边距 = ScreenMargin（贴左/贴右），
    /// 首卡距屏幕顶/底边距 = ScreenMargin，卡片间距 = Settings.NotificationGap。
    /// </summary>
    private void RecalculateOffsets()
    {
        double margin = Settings.ScreenMargin;
        double sideLeft = IsRightSide ? 0 : margin;
        double sideRight = IsRightSide ? margin : 0;

        // 常驻开着的卡片（音乐/媒体或外卖）排在最靠近屏幕边缘的位置（OrderBy 稳定排序，保持组内插入顺序）
        IEnumerable<KeyValuePair<string, MiFocusNotification>> ordered = _activeByKey;
        if (Settings.MusicPersistent || Settings.DeliveryPersistent)
        {
            ordered = _activeByKey.OrderBy(kvp => IsPinnedMedia(kvp.Value.Message) ? 0 : 1);
        }

        double stack = 0;
        double hostH = NotificationHost.ActualHeight;
        foreach (var kvp in ordered)
        {
            var notification = kvp.Value;
            double stackBefore = stack;
            notification.HorizontalAlignment = IsRightSide
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Left;

            if (IsBottomSide)
            {
                notification.VerticalAlignment = VerticalAlignment.Bottom;
                notification.Margin = new Thickness(sideLeft, 0, sideRight, margin + stack);
            }
            else
            {
                notification.VerticalAlignment = VerticalAlignment.Top;
                notification.Margin = new Thickness(sideLeft, margin + stack, sideRight, 0);
            }

            // 间距必须基于卡片真实渲染高度：手工估算的 CalculateHeight() 会偏大，
            // 误差会逐张累加导致卡片之间出现大片空白。未布局完成时才回退估算值。
            double cardHeight = notification.ActualHeight > 0
                ? notification.ActualHeight
                : notification.CalculateHeight();
            stack += cardHeight + Settings.NotificationGap;

            // 卡片溢出宿主可视区（屏幕工作区边缘）时施加渐变淡出遮罩。
            // 卡片尚未完成布局（ActualHeight=0）时本遍跳过，SizeChanged 会再次重排。
            if (notification.ActualHeight > 0 && hostH > 0)
            {
                if (IsBottomSide)
                    ApplyOverflowFade(notification, hostH - (margin + stackBefore) - cardHeight,
                        cardHeight, hostH, true);
                else
                    ApplyOverflowFade(notification, margin + stackBefore,
                        cardHeight, hostH, false);
            }
        }

        ScheduleRegionUpdate();
    }

    /// <summary>
    /// 卡片溢出宿主可视高度（屏幕工作区边缘）时，对超出方向施加纵向渐变透明遮罩，
    /// 让被边缘裁切的卡片柔和淡出而不是生硬截断；完全可见的卡片清除遮罩。
    /// 遮罩只影响绘制；跨进程点击穿透仍由 SetWindowRgn 紧贴区域保证，互不影响。
    /// </summary>
    private static void ApplyOverflowFade(MiFocusNotification card, double cardTop, double cardHeight,
        double hostH, bool bottomAnchored)
    {
        const double FadeBand = 80; // 淡出带长度（DIP）
        System.Windows.Media.LinearGradientBrush? mask = null;

        if (bottomAnchored)
        {
            // 底部锚定（卡片向上堆叠）：cardTop < 0 表示顶部被工作区上缘裁切
            if (cardTop < 0)
            {
                double clipRel = Math.Clamp(-cardTop / cardHeight, 0, 1);
                double opaqueRel = Math.Min(1, clipRel + FadeBand / cardHeight);
                mask = new System.Windows.Media.LinearGradientBrush
                {
                    StartPoint = new System.Windows.Point(0, 0),
                    EndPoint = new System.Windows.Point(0, 1)
                };
                mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
                mask.GradientStops.Add(new GradientStop(Colors.Transparent, clipRel));
                mask.GradientStops.Add(new GradientStop(Colors.Black, opaqueRel));
                mask.GradientStops.Add(new GradientStop(Colors.Black, 1));
            }
        }
        else
        {
            // 顶部锚定（卡片向下堆叠）：底边越过宿主高度即被下缘裁切
            double cardBottom = cardTop + cardHeight;
            if (cardBottom > hostH)
            {
                double clipRel = Math.Clamp((hostH - cardTop) / cardHeight, 0, 1);
                double fadeStart = Math.Max(0, clipRel - FadeBand / cardHeight);
                mask = new System.Windows.Media.LinearGradientBrush
                {
                    StartPoint = new System.Windows.Point(0, 0),
                    EndPoint = new System.Windows.Point(0, 1)
                };
                mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
                mask.GradientStops.Add(new GradientStop(Colors.Black, fadeStart));
                mask.GradientStops.Add(new GradientStop(Colors.Transparent, clipRel));
                mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            }
        }

        if (mask != null) mask.Freeze();
        if (!ReferenceEquals(card.OpacityMask, mask)) card.OpacityMask = mask;
    }

    /// <summary>
    /// 命中区域相对卡片实体矩形的外扩余量（DIP）。
    /// 关键事实：本窗口是 AllowsTransparency 分层窗口，DWM 按像素合成整张画面，
    /// SetWindowRgn 的区域只影响"鼠标命中归属"，不裁剪绘制——阴影（BlurRadius 40）
    /// 与进出场动画位移即使画在区域外也照常可见（已实测验证）。
    /// 因此区域必须紧贴卡片实体：区域外的阴影带/缝隙由 Win32 在命中测试时直接
    /// 跳过本窗口，点击穿透到下层应用（跨进程可靠，WM_NCHITTEST 钩子只能穿透同
    /// 线程窗口，不能作为主要手段）。仅保留 2px 余量吸收圆角抗锯齿与取整误差。
    /// </summary>
    private const double RegionInflate = 2;

    private bool _regionUpdateQueued;

    // ---------- 全屏检测 ----------

    /// <summary>全屏轮询间隔。前台窗口需连续命中两次（约 1.2s）才切换状态，防止瞬间最大化误判抖动。</summary>
    private static readonly TimeSpan FullscreenPollInterval = TimeSpan.FromMilliseconds(600);
    private const int FullscreenStableTicks = 2;

    private System.Windows.Threading.DispatcherTimer? _fullscreenTimer;
    private bool _foregroundFullscreen;
    private bool _prevFullscreenVote;
    private int _fullscreenVoteStreak;

    /// <summary>是否处于“前台窗口覆盖整个显示器（含任务栏区域）”的全屏状态。</summary>
    private bool IsForegroundFullscreen => _foregroundFullscreen;

    private void StartFullscreenWatcher()
    {
        if (_fullscreenTimer != null) return;
        _fullscreenTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = FullscreenPollInterval
        };
        _fullscreenTimer.Tick += (_, _) => FullscreenPollTick();
        _fullscreenTimer.Start();
    }

    /// <summary>
    /// 每拍轮询前台窗口并把宿主钉在期望的 Z 序位置：真全屏（无边框铺满宿主所在
    /// 显示器）时降到 Z 序底部并去掉 Topmost——卡片被全屏画面盖住即等效不显示；
    /// 其余时刻保持置顶并在置顶层里最上，被别的置顶窗口（任务栏、别家置顶工具）
    /// 压住后最迟一个轮询间隔自动回到最上层。
    /// </summary>
    private void FullscreenPollTick()
    {
        bool vote = IsForegroundWindowFullscreen();
        if (vote == _prevFullscreenVote)
        {
            if (++_fullscreenVoteStreak >= FullscreenStableTicks &&
                vote != _foregroundFullscreen)
            {
                _foregroundFullscreen = vote;
                _fullscreenVoteStreak = 0;
            }
        }
        else
        {
            _prevFullscreenVote = vote;
            _fullscreenVoteStreak = 1;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            if (_foregroundFullscreen)
            {
                // 去 Topmost 并压到 Z 序最底：无论独占/无边框全屏都能被盖住（每拍钉一次，幂等）
                Topmost = false;
                SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            else
            {
                EnsureTopmost(hwnd);
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 让宿主保持在置顶层最上，且只在真的被压住时才动手：掉出置顶层，或有外人的
    /// 置顶窗口（任务栏、别家置顶工具）盖在上面。已在理想位置时零动作——自家的
    /// ToolTip、托盘菜单这些置顶弹出压在卡片上方属于正常状态，绝不能被压回。
    /// 动手时先抬宿主，再把自家置顶弹出按原相对次序压回宿主之上。
    /// </summary>
    private void EnsureTopmost(IntPtr hwnd)
    {
        bool inTopmostBand = (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
        bool covered = false;
        if (inTopmostBand)
        {
            // Z 序里置顶层恒排在普通层之上，宿主上方只可能是置顶窗口；
            // 本进程的弹出（ToolTip/托盘菜单）压在上方不算被压住，外人才算
            for (var h = GetWindow(hwnd, GW_HWNDPREV); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDPREV))
            {
                if (!IsOwnProcessWindow(h))
                {
                    covered = true;
                    break;
                }
            }
        }
        if (inTopmostBand && !covered) return;

        // 抬回置顶层最上（不抢焦点，保持 NOACTIVATE）
        Topmost = true;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        // 自家置顶弹出（ToolTip / 托盘菜单）自下而上重新压到顶，相对次序不变
        var popups = new List<IntPtr>();
        for (var h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = GetWindow(h, GW_HWNDNEXT))
        {
            if (h == hwnd) continue;
            if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0 &&
                IsWindowVisible(h) && IsOwnProcessWindow(h))
            {
                popups.Add(h);
            }
        }
        for (int i = popups.Count - 1; i >= 0; i--)
        {
            SetWindowPos(popups[i], HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    private static bool IsOwnProcessWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// 判定前台窗口是否为真全屏：窗口可见、未最小化、无标题栏（无边框铺满的
    /// 视频/游戏），且矩形覆盖宿主所在显示器的整块区域（rcMonitor，含任务栏带）。
    /// 最大化窗口（含盖住自动隐藏任务栏的场景）不算全屏。容差吸收 DWM 外扩
    /// 边框与取整误差。
    /// </summary>
    private bool IsForegroundWindowFullscreen()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        var host = new WindowInteropHelper(this).Handle;
        if (fg == host) return false;
        if (!IsWindowVisible(fg)) return false;
        if (IsIconic(fg)) return false;

        // 最大化窗口不算全屏：自动隐藏任务栏时最大化窗口的矩形同样铺满显示器，
        // 但那是普通用法，卡片要照常置顶——只有真全屏（视频/游戏）才让位
        if (IsZoomed(fg)) return false;
        // 有标题栏（WS_CAPTION）样式位的窗口不算全屏：视频网站全屏、F11、
        // 播放器/游戏的无边框铺满都没有标题栏；普通窗口哪怕手动拉满整个
        // 屏幕也保留标题栏样式位
        if (((uint)GetWindowLong(fg, GWL_STYLE) & WS_CAPTION) != 0) return false;

        IntPtr hostMon = MonitorFromWindow(host, MONITOR_DEFAULTTONEAREST);
        if (hostMon == IntPtr.Zero) return false;

        var mi = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hostMon, ref mi)) return false;
        if (!GetWindowRect(fg, out RECT r)) return false;

        const int tol = 8;
        RECT m = mi.rcMonitor;
        return r.Left <= m.Left + tol && r.Top <= m.Top + tol &&
               r.Right >= m.Right - tol && r.Bottom >= m.Bottom - tol;
    }

    /// <summary>在布局稳定后（Background 优先级，晚于布局/渲染）重建窗口命中区域；多次调用自动合并。</summary>
    private void ScheduleRegionUpdate()
    {
        if (_regionUpdateQueued) return;
        _regionUpdateQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _regionUpdateQueued = false;
            UpdateWindowRegion();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// 用 Win32 SetWindowRgn 把宿主窗口的鼠标命中区域限定为所有卡片实体矩形的并集
    /// （仅外扩 <see cref="RegionInflate"/>）。区域外的一切——阴影带、卡片缝隙、
    /// 进出场动画残影——都由 Win32 直接穿透到下层窗口，这是唯一跨进程可靠的穿透手段。
    /// 分层窗口下区域不裁剪画面，阴影在区域外照常绘制。
    /// 区域坐标为窗口客户区的设备像素（DIP × DPI 缩放）。
    /// </summary>
    private void UpdateWindowRegion()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        double hostW = NotificationHost.ActualWidth;
        double hostH = NotificationHost.ActualHeight;
        if (hostW <= 0 || hostH <= 0) return;

        double scaleX = 1.0, scaleY = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            scaleX = source.CompositionTarget.TransformToDevice.M11;
            scaleY = source.CompositionTarget.TransformToDevice.M22;
        }

        using var region = new System.Drawing.Region();
        region.MakeEmpty();

        foreach (var child in NotificationHost.Children)
        {
            if (child is not MiFocusNotification card) continue;
            if (card.Visibility != Visibility.Visible ||
                card.ActualWidth <= 0 || card.ActualHeight <= 0)
            {
                continue;
            }

            var m = card.Margin;
            double x = card.HorizontalAlignment == HorizontalAlignment.Right
                ? hostW - card.ActualWidth - m.Right
                : m.Left;
            double y = card.VerticalAlignment == VerticalAlignment.Bottom
                ? hostH - card.ActualHeight - m.Bottom
                : m.Top;

            // DIP 矩形 → 外扩阴影/动画余量 → 裁剪到客户区
            var rect = new Rect(x, y, card.ActualWidth, card.ActualHeight);
            rect.Inflate(RegionInflate, RegionInflate);
            rect.Intersect(new Rect(0, 0, hostW, hostH));
            if (rect.IsEmpty) continue;

            var deviceRect = new System.Drawing.Rectangle(
                (int)Math.Floor(rect.X * scaleX),
                (int)Math.Floor(rect.Y * scaleY),
                (int)Math.Ceiling(rect.Width * scaleX),
                (int)Math.Ceiling(rect.Height * scaleY));
            region.Union(deviceRect);
        }

        try
        {
            using var graphics = Graphics.FromHwnd(hwnd);
            IntPtr hrgn = region.GetHrgn(graphics);
            // SetWindowRgn 成功后 HRGN 所有权移交系统，不可再 DeleteObject；
            // 下次设置新区域时系统会自动销毁旧区域。
            SetWindowRgn(hwnd, hrgn, true);
        }
        catch
        {
        }
    }

    private System.Windows.Threading.DispatcherTimer? _displayChangeTimer;

    /// <summary>
    /// 显示器拓扑 / 工作区 / DPI 变化（拔插显示器、改分辨率、切主屏、任务栏移动）后，
    /// 重新贴齐宿主窗口并重排卡片。系统把搁浅窗口挪回可见区、WPF 按新 DPI 重缩放
    /// 几何、SystemParameters 工作区缓存失效，这些都异步进行且顺序不保证——
    /// 去抖合并到静默 400ms 后统一重跑一次 ApplySettings，读到的就是稳定值。
    /// ApplySettings 是幂等落位，重复执行无副作用。
    /// </summary>
    private void ScheduleDisplayReapply()
    {
        if (_displayChangeTimer == null)
        {
            _displayChangeTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _displayChangeTimer.Tick += (_, _) =>
            {
                _displayChangeTimer.Stop();
                ApplySettings();
            };
        }
        // 静默期重置：一条广播链（拔屏会连发 DISPLAYCHANGE/SETTINGCHANGE/DPICHANGED）只触发一次重排
        _displayChangeTimer.Stop();
        _displayChangeTimer.Start();
    }

    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_DISPLAYCHANGE = 0x007E;

    /// <summary>
    /// 兜底命中测试：阴影/缝隙的穿透主要由 SetWindowRgn 的紧贴区域在 Win32 层完成
    /// （区域外的点根本不会命中本窗口）。此钩子处理区域内、卡片实体外的残余点
    /// （如圆角矩形四角外的透明像素、同线程窗口叠放）——落在卡片外一律返回
    /// HTTRANSPARENT；落在卡片上走默认处理（HTCLIENT），按钮正常可点。
    /// 注意：HTTRANSPARENT 仅对同线程窗口自动向下穿透，跨进程穿透必须依赖区域。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            // lParam 低/高字为物理屏幕坐标（多屏可能为负，按有符号 short 解析）
            int physX = (short)(lParam.ToInt64() & 0xFFFF);
            int physY = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            if (!IsPointOverCard(physX, physY))
            {
                handled = true;
                return new IntPtr(HTTRANSPARENT);
            }
        }
        else if (msg == WM_DISPLAYCHANGE || msg == WM_SETTINGCHANGE)
        {
            // 显示器拔插/分辨率/主屏切换（DISPLAYCHANGE）、任务栏移动等工作区变化（SETTINGCHANGE）
            ScheduleDisplayReapply();
        }
        return IntPtr.Zero;
    }

    /// <summary>物理屏幕坐标是否落在某张可见卡片的实体范围内（不含阴影外扩区）。</summary>
    private bool IsPointOverCard(int physX, int physY)
    {
        foreach (var child in NotificationHost.Children)
        {
            if (child is not MiFocusNotification card) continue;
            if (card.Visibility != Visibility.Visible ||
                card.ActualWidth <= 0 || card.ActualHeight <= 0)
            {
                continue;
            }

            // PointToScreen 输出物理像素，且包含卡片的 RenderTransform（进出场动画位移）
            var topLeft = card.PointToScreen(new System.Windows.Point(0, 0));
            var bottomRight = card.PointToScreen(new System.Windows.Point(card.ActualWidth, card.ActualHeight));
            // 不加外扩容差：卡片间距由用户设置（默认 3px），任何外扩都会吞掉缝隙使其
            // 失去穿透；卡片边界本身为闭区间，贴边点击仍可命中。
            if (physX >= topLeft.X && physX <= bottomRight.X &&
                physY >= topLeft.Y && physY <= bottomRight.Y)
            {
                return true;
            }
        }
        return false;
    }

    private void StartAutoDismiss(string key)
    {
        ResetAutoDismiss(key, persistent: false);
    }

    /// <summary>
    /// 重置自动消失计时器（卡片内容更新即重新计时）；persistent 为真时取消计时，
    /// 卡片转为常驻，只能手动关闭或等手机端清除通知。
    /// </summary>
    private void ResetAutoDismiss(string key, bool persistent)
    {
        CancelAutoDismiss(key);

        if (persistent) return;

        var cts = new CancellationTokenSource();
        _dismissCts[key] = cts;

        _ = RunAutoDismissAsync(key, cts.Token);
    }

    /// <summary>取消自动消失计时器（卡片转为常驻，或即将被移除）。</summary>
    private void CancelAutoDismiss(string key)
    {
        if (_dismissCts.TryGetValue(key, out var cts))
        {
            try { cts.Cancel(); } catch { }
            _dismissCts.Remove(key);
        }
    }

    private async System.Threading.Tasks.Task RunAutoDismissAsync(string key, CancellationToken token)
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(AutoDismissMs, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }

        Dispatcher.Invoke(() => DismissByKey(key));
    }

    private void DismissByKey(string key)
    {
        if (!_activeByKey.TryGetValue(key, out var notification)) return;

        CancelAutoDismiss(key);

        _activeByKey.Remove(key);
        notification.PlayHideAnimation((_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                NotificationHost.Children.Remove(notification);
                RecalculateOffsets();
                ScheduleIdleTrim(); // 最后一张卡关掉 30 秒后回收内存
            });
        });
    }

    // ---------- 系统托盘 ----------

    private void InitializeTrayIcon()
    {
        if (_trayIcon != null) return;

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = CreateTrayIcon(),
            Visible = true,
            Text = "MiToast 通知同步"
        };
        // 右键弹出 WPF 自绘现代化菜单（圆角/阴影/深色适配）
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Right)
            {
                Dispatcher.Invoke(ShowTrayMenu);
            }
        };
        _trayIcon.DoubleClick += (_, _) =>
            Dispatcher.Invoke(() => SettingsWindow.ShowSingleton());
    }

    /// <summary>在鼠标位置打开 WPF 托盘菜单；靠近屏幕底部时向上弹出，避免超出工作区。</summary>
    private void ShowTrayMenu()
    {
        if (FindResource("TrayContextMenu") is not ContextMenu menu) return;

        SyncTrayMenuItems(menu);
        ApplyTrayMenuTheme(menu);

        double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var cursor = WinForms.Cursor.Position;
        double x = cursor.X / scale;
        double y = cursor.Y / scale;
        var workArea = SystemParameters.WorkArea;

        // 先测量菜单真实尺寸（Absolute 模式下 WPF 不会自动做屏幕边缘翻转，
        // 必须用实际宽高钳制坐标，否则靠近屏幕右/下边缘时菜单会被裁切）。
        menu.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = menu.DesiredSize.Width > 0 ? menu.DesiredSize.Width : 240;
        double h = menu.DesiredSize.Height > 0 ? menu.DesiredSize.Height : 280;

        const double edgeGap = 6;
        // 水平方向：超出右边缘则向左收，避免右侧文字被裁切
        if (x + w > workArea.Right - edgeGap) x = workArea.Right - w - edgeGap;
        if (x < workArea.Left + edgeGap) x = workArea.Left + edgeGap;
        // 垂直方向：超出底部（托盘区在屏幕底部）则整体上移到工作区内
        if (y + h > workArea.Bottom - edgeGap) y = workArea.Bottom - h - edgeGap;
        if (y < workArea.Top + edgeGap) y = workArea.Top + edgeGap;

        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Absolute;
        menu.HorizontalOffset = x;
        menu.VerticalOffset = y;

        menu.IsOpen = true;

        // 菜单弹出要压在通知卡片（Topmost）之上：打开后钉到置顶层最上。
        // EnsureTopmost 会把自家置顶弹出保护在宿主上方，不会被卡片压回。
        try
        {
            if (PresentationSource.FromVisual(menu) is HwndSource menuSrc && menuSrc.Handle != IntPtr.Zero)
            {
                SetWindowPos(menuSrc.Handle, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }
        catch
        {
        }
    }

    private static MenuItem? FindMenuItem(ContextMenu menu, string tag)
    {
        foreach (var item in menu.Items)
        {
            if (item is MenuItem mi && mi.Tag is string t && t == tag)
            {
                return mi;
            }
        }
        return null;
    }

    private void SyncTrayMenuItems(ContextMenu menu)
    {
        if (FindMenuItem(menu, "dark") is { } darkItem)
            darkItem.IsChecked = Settings.DarkMode;
        if (FindMenuItem(menu, "musicpin") is { } pinItem)
            pinItem.IsChecked = Settings.MusicPersistent;
        if (FindMenuItem(menu, "deliverypin") is { } deliveryPinItem)
            deliveryPinItem.IsChecked = Settings.DeliveryPersistent;
        if (FindMenuItem(menu, "dnd") is { } dndItem)
            dndItem.IsChecked = Settings.DndEnabled;
        if (FindMenuItem(menu, "status") is { } statusItem)
            statusItem.Header = NetworkManager.Instance.StatusText;
    }

    /// <summary>托盘菜单配色随深色模式切换。</summary>
    private void ApplyTrayMenuTheme(ContextMenu menu)
    {
        bool dark = Settings.DarkMode;
        // ResourceDictionary 中的 Freezable 会被自动冻结，必须整体替换实例（DynamicResource 自动刷新）。
        menu.Resources["TrayMenuBgBrush"] = new SolidColorBrush(dark
            ? System.Windows.Media.Color.FromRgb(0x2C, 0x2C, 0x2E) : System.Windows.Media.Colors.White);
        menu.Resources["TrayMenuBorderBrush"] = new SolidColorBrush(dark
            ? System.Windows.Media.Color.FromRgb(0x48, 0x48, 0x4A) : System.Windows.Media.Color.FromRgb(0xE5, 0xE5, 0xEA));
        menu.Resources["TrayTextBrush"] = new SolidColorBrush(dark
            ? System.Windows.Media.Color.FromRgb(0xF5, 0xF5, 0xF7) : System.Windows.Media.Color.FromRgb(0x1D, 0x1D, 0x1F));
        menu.Resources["TraySeparatorBrush"] = new SolidColorBrush(dark
            ? System.Windows.Media.Color.FromRgb(0x48, 0x48, 0x4A) : System.Windows.Media.Color.FromRgb(0xE5, 0xE5, 0xEA));
    }

    private void TrayMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            SyncTrayMenuItems(menu);
            ApplyTrayMenuTheme(menu);
        }
    }

    private void TraySettings_Click(object sender, RoutedEventArgs e)
        => SettingsWindow.ShowSingleton();

    /// <summary>立刻重新搜索手机（放弃当前退避等待）。已连接时先探测现有连接，只刷新状态，不掐断健康连接。</summary>
    private void TrayRescan_Click(object sender, RoutedEventArgs e)
    {
        NetworkManager.Instance.Reconnect();
        UpdateTrayStatus();
    }

    private void TrayHistory_Click(object sender, RoutedEventArgs e)
        => HistoryWindow.ShowSingleton();

    private void TrayDark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi)
        {
            Settings.DarkMode = mi.IsChecked;
            Settings.Save();
        }
    }

    private void TrayMusicPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi)
        {
            Settings.MusicPersistent = mi.IsChecked;
            Settings.Save();
        }
    }

    private void TrayDeliveryPin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi)
        {
            Settings.DeliveryPersistent = mi.IsChecked;
            Settings.Save();
        }
    }

    private void TrayDnd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi)
        {
            Settings.DndEnabled = mi.IsChecked;
            Settings.Save(); // 触发 Changed，设置窗口的开关实时跟上
        }
    }

    private void TrayExit_Click(object sender, RoutedEventArgs e)
        => ShutdownApplication();

    private void ShutdownApplication()
    {
        try
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }
        catch
        {
        }

        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>运行时绘制橙色圆角方形 + 白色 M 的托盘图标。</summary>
    private static System.Drawing.Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            using var brush = new SolidBrush(System.Drawing.Color.FromArgb(255, 105, 0));
            var rect = new Rectangle(2, 2, 28, 28);
            using var path = RoundedRectangle(rect, 8);
            g.FillPath(brush, path);

            using var font = new Font("Segoe UI", 15f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString("M", font, System.Drawing.Brushes.White, new RectangleF(2, 3, 28, 26), format);
        }
        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    private static GraphicsPath RoundedRectangle(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int diameter = radius * 2;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
        }
        catch
        {
        }
        base.OnClosed(e);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    // ---------- 全屏检测 Win32 ----------

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsZoomed(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int GWL_STYLE = -16;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const uint WS_CAPTION = 0x00C00000;
    private const uint GW_HWNDPREV = 3;
    private const uint GW_HWNDNEXT = 2;
}
