using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiToast.Models;
using MiToast.Services;

namespace MiToast.Network;

/// <summary>连接状态，供托盘与设置窗口展示。</summary>
public enum ConnectionState
{
    /// <summary>网卡没连上任何网络（校园网掉线、拔网线）</summary>
    Waiting,

    /// <summary>正在搜索手机（广播发现/扫描）</summary>
    Searching,

    /// <summary>已拿到候选地址，正在建立连接</summary>
    Connecting,

    /// <summary>已连接，通知正常同步</summary>
    Connected
}

/// <summary>
/// 与手机端 WebSocket 服务的连接管理。
///
/// 校园网环境下做过的针对性处理：
/// 1. <b>不走系统代理</b>：ClientWebSocket 默认使用系统代理设置（Windows 上读 IE/系统代理），
///    装了 Clash 之类代理软件的电脑会把 ws://192.168.x.x 也送去代理，表现为"一直连不上"；
/// 2. <b>多网卡广播</b>：笔记本常同时有线 + 无线 + 虚拟网卡，发现请求会按每块网卡的子网广播地址分别发送；
/// 3. <b>地址缓存与手动指定</b>：广播被屏蔽时直接连上次成功的地址，或用户在设置里手填手机地址；
/// 4. <b>并发竞速 + 连接超时</b>：多个候选同时错峰尝试，过期地址最多拖 5 秒；
/// 5. <b>网络变化即时重连</b>：切换 Wi-Fi / 插拔网线 / VPN 起停时立刻打断等待重新搜索，不等 TCP 超时。
/// </summary>
public class NetworkManager
{
    private static readonly Lazy<NetworkManager> _instance = new(() => new NetworkManager());
    public static NetworkManager Instance => _instance.Value;

    private const int ConnectTimeoutMs = 5000;
    private const int ConnectStaggerMs = 250;
    private const int FastRetryMs = 1000;
    private const int MaxRetryDelayMs = 30000;
    private const int DiscoveryWindowMs = 2500;
    private const int HandshakeVerifyMs = 3000;
    private const int KeepAliveIntervalMs = 20000;
    private const int HintAfterMs = 45000;

    /// <summary>手机端可能下发的消息类型，用于识别"连上的是不是 MiToast"。</summary>
    private static readonly HashSet<string> KnownMessageTypes = new()
    {
        "ping", "notification", "clear", "dnd_status", "cast_devices", "history_sync"
    };

    /// <summary>单次历史同步的最大条数（与手机端一致）；满批次说明手机端可能还有更早的离线记录。</summary>
    private const int HistorySyncBatchSize = 2000;

    private CancellationTokenSource? _cts;
    private ClientWebSocket? _webSocket;
    private volatile bool _isRunning;
    private volatile bool _scanDue = true;
    private int _backoffMs = FastRetryMs;

    /// <summary>打断退避等待的信号（网络变化、用户点"重新搜索"时立即唤醒主循环）。</summary>
    private TaskCompletionSource _wake = NewSignal();
    private string _localAddressFingerprint = string.Empty;

    private long _failStreakStart;
    private bool _hintFired;

    public event EventHandler<NotificationMessage>? NotificationReceived;
    public event EventHandler<string>? NotificationCleared;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<ConnectionState>? ConnectionStateChanged;

    /// <summary>长时间连不上手机时触发一次，提示用户手动指定地址（校园网广播受限场景）。</summary>
    public event EventHandler<string>? HintRequested;

    /// <summary>手机端返回妙播设备列表/妙播投射状态时触发（cast_devices 消息）。</summary>
    public event EventHandler<CastDevicesMessage>? CastDevicesUpdated;

    public ConnectionState State { get; private set; } = ConnectionState.Searching;
    public string StatusText { get; private set; } = "正在搜索 Android 设备...";

    /// <summary>当前连接的目标地址（如 192.168.1.5）；未连接时为空。</summary>
    public string ConnectedDevice { get; private set; } = string.Empty;

    /// <summary>手机型号（由手机端握手消息提供）。</summary>
    public string PhoneName { get; private set; } = string.Empty;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    /// <summary>手机端勿扰模式是否开启（由 Android 端 dnd_status 广播更新）</summary>
    public bool PhoneDndEnabled { get; private set; }

    private NetworkManager() { }

    public async Task StartAsync()
    {
        if (_isRunning) return;
        _isRunning = true;
        _backoffMs = FastRetryMs;
        _scanDue = true;
        _cts = new CancellationTokenSource();
        HookNetworkChange();
        await Task.Yield();
        _ = RunAsync(_cts.Token);
    }

    public void Start()
    {
        _ = StartAsync();
    }

    public void Stop()
    {
        _isRunning = false;
        UnhookNetworkChange();
        try { _cts?.Cancel(); } catch { }
        try { _webSocket?.Abort(); } catch { }
        try { _webSocket?.Dispose(); } catch { }
        _webSocket = null;
        SetState(ConnectionState.Waiting, "已停止");
    }

    /// <summary>
    /// 立即重新搜索并连接：放弃当前退避等待、掐断现有连接，让主循环马上重新走一遍发现流程。
    /// 上次成功的地址仍会作为候选优先尝试（广播被屏蔽时它是唯一可用的路径）。
    /// </summary>
    public void Reconnect()
    {
        _backoffMs = FastRetryMs;
        _scanDue = true;
        try { _webSocket?.Abort(); } catch { }
        Wake();
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (_isRunning && !token.IsCancellationRequested)
        {
            try
            {
                if (!NetworkInterface.GetIsNetworkAvailable())
                {
                    SetState(ConnectionState.Waiting, "本机网络未连接，等待网络恢复...");
                    _scanDue = true;
                    await DelayInterruptible(FastRetryMs * 3, token);
                    continue;
                }

                var candidates = BuildKnownCandidates();

                if (_scanDue)
                {
                    _scanDue = false;
                    SetState(ConnectionState.Searching, "正在搜索 Android 设备...");
                    AddCandidates(candidates, await DiscoverAsync(token));
                }

                if (candidates.Count == 0)
                {
                    SetState(ConnectionState.Searching, "正在搜索 Android 设备...");
                    NoteFailure();
                    await BackoffAsync(token);
                    _scanDue = true;
                    continue;
                }

                SetState(ConnectionState.Connecting, candidates.Count == 1
                    ? $"正在连接 {candidates[0]}..."
                    : $"正在尝试 {candidates.Count} 个地址...");

                var connected = await ConnectAnyAsync(candidates, token);
                if (connected == null)
                {
                    SetState(ConnectionState.Searching, "正在搜索 Android 设备...");
                    NoteFailure();
                    await BackoffAsync(token);
                    _scanDue = true;
                    continue;
                }

                _backoffMs = FastRetryMs;
                _failStreakStart = 0;
                _hintFired = false;

                var connection = connected.Value;
                using (connection.Socket)
                {
                    _webSocket = connection.Socket;
                    ConnectedDevice = connection.Endpoint.ToString();
                    RememberEndpoint(connection.Endpoint);
                    SetState(ConnectionState.Connected, PhoneName.Length > 0
                        ? $"已连接 {PhoneName}（{connection.Endpoint}）"
                        : $"已连接到 Android 设备（{connection.Endpoint}）");

                    // 接入认证：先发配对码（手机端校验失败会掐断连接），再请求历史同步。
                    // 同一 WebSocket 上 SendAsync 不可并发，必须串行 await。
                    await SendJsonAsync(new { type = "auth", token = AppSettings.Instance.PairCode });
                    await SendJsonAsync(new
                    {
                        type = "history_sync_request",
                        since = HistoryService.Instance.NewestTimestamp()
                    });

                    await Task.WhenAny(
                        ReceiveLoopAsync(connection.Socket, token),
                        KeepAliveLoopAsync(connection.Socket, token));
                }

                _webSocket = null;
                ConnectedDevice = string.Empty;
                PhoneName = string.Empty;
                if (_isRunning && !token.IsCancellationRequested)
                {
                    SetState(ConnectionState.Searching, "连接已断开，正在重新搜索...");
                }
                // 断开后重新全量搜索：手机换网络后 IP 往往变了
                _scanDue = true;
                await DelayInterruptible(FastRetryMs, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                _webSocket?.Dispose();
                _webSocket = null;
                _scanDue = true;
                try { await DelayInterruptible(_backoffMs, token); } catch { }
                _backoffMs = Math.Min(_backoffMs * 2, MaxRetryDelayMs);
            }
        }
    }

    /// <summary>不依赖发现就能拿到的候选：用户手填的地址 + 上次成功连接的地址。</summary>
    private static List<LanEndpoint> BuildKnownCandidates()
    {
        var list = new List<LanEndpoint>();
        var settings = AppSettings.Instance;

        if (LanEndpoint.TryParse(settings.ManualPhoneHost, settings.ManualPhonePort, out var manual))
        {
            list.Add(manual);
        }
        if (LanEndpoint.TryParse(settings.LastPhoneHost, settings.LastPhonePort, out var last) && !list.Contains(last))
        {
            list.Add(last);
        }
        return list;
    }

    private async Task<List<LanEndpoint>> DiscoverAsync(CancellationToken token)
    {
        var found = new List<LanEndpoint>();
        var gate = new object();

        void Collect(LanEndpoint endpoint)
        {
            lock (gate)
            {
                if (!found.Contains(endpoint)) found.Add(endpoint);
            }
        }

        try
        {
            await DeviceLocator.ProbeAsync(DiscoveryWindowMs, Collect, token);

            if (found.Count == 0 && AppSettings.Instance.SubnetSweepEnabled)
            {
                SetState(ConnectionState.Searching, "广播无响应，正在扫描本机网段...");
                await DeviceLocator.SweepLocalSubnetsAsync(LanEndpoint.DefaultPort, Collect, token);
            }
        }
        catch (OperationCanceledException) { }
        catch { }

        return found;
    }

    private static void AddCandidates(List<LanEndpoint> target, List<LanEndpoint> extra)
    {
        foreach (var endpoint in extra)
        {
            if (!target.Contains(endpoint)) target.Add(endpoint);
        }
    }

    private readonly record struct ConnectedSocket(ClientWebSocket Socket, LanEndpoint Endpoint);

    /// <summary>
    /// 并发尝试所有候选：错峰起跑，谁先连上就用谁，其余立刻取消。
    /// 校园网里常见"缓存地址已过期 + 新地址刚发现"，串行尝试会被过期地址拖满超时。
    /// </summary>
    private async Task<ConnectedSocket?> ConnectAnyAsync(List<LanEndpoint> candidates, CancellationToken token)
    {
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var winner = new TaskCompletionSource<ConnectedSocket?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new List<Task>(candidates.Count);

        for (int i = 0; i < candidates.Count; i++)
        {
            var endpoint = candidates[i];
            int stagger = Math.Min(i, 4) * ConnectStaggerMs;

            attempts.Add(TryConnectAsync(endpoint, stagger, raceCts.Token, socket =>
            {
                if (raceCts.IsCancellationRequested) return false;
                if (!winner.TrySetResult(new ConnectedSocket(socket, endpoint))) return false;
                raceCts.Cancel(); // 已经有人连上，收掉剩下的尝试
                return true;
            }));
        }

        _ = Task.WhenAll(attempts).ContinueWith(_ => winner.TrySetResult(null), TaskScheduler.Default);

        var result = await winner.Task;
        try { raceCts.Cancel(); } catch { }
        try { await Task.WhenAll(attempts); } catch { }
        return result;
    }

    private async Task TryConnectAsync(LanEndpoint endpoint, int staggerMs, CancellationToken raceToken,
        Func<ClientWebSocket, bool> onConnected)
    {
        ClientWebSocket? socket = null;
        try
        {
            if (staggerMs > 0) await Task.Delay(staggerMs, raceToken);

            socket = new ClientWebSocket();

            // 关键：默认会走系统代理（Windows 上读 IE/系统代理设置）。校园网里挂代理软件很常见，
            // 那样 ws://192.168.x.x:8080 也会被送去代理，要么连不通，要么绕一圈才通。
            socket.Options.Proxy = null;

            // 协议层心跳：定期发 ping 帧，避免校园网 AP 把空闲长连接回收掉
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(raceToken))
            {
                connectCts.CancelAfter(ConnectTimeoutMs);
                await socket.ConnectAsync(new Uri(endpoint.Url), connectCts.Token);
            }

            if (raceToken.IsCancellationRequested) return;

            // 扫描出来的候选不可信：先确认对方确实是 MiToast，避免连上同网段里另一个 8080 服务
            if (!endpoint.Trusted)
            {
                using var verifyCts = CancellationTokenSource.CreateLinkedTokenSource(raceToken);
                verifyCts.CancelAfter(HandshakeVerifyMs);

                var buffer = new byte[8192];
                using var ms = new MemoryStream();
                var first = await ReadMessageAsync(socket, buffer, ms, verifyCts.Token);

                if (first == null || !IsMiToastMessage(first)) return;

                ProcessMessage(first); // 握手消息不丢弃（里面带手机型号）
            }

            if (onConnected(socket)) socket = null; // 所有权移交，交给主循环管理
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!raceToken.IsCancellationRequested)
            {
                SetState(ConnectionState.Connecting, $"{endpoint} 连接失败：{DescribeError(ex)}");
            }
        }
        finally
        {
            try { socket?.Dispose(); } catch { }
        }
    }

    private static bool IsMiToastMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var type)) return false;
            var value = type.GetString();
            return value != null && KnownMessageTypes.Contains(value);
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeError(Exception ex) => ex switch
    {
        OperationCanceledException or TimeoutException => "连接超时",
        WebSocketException => "WebSocket 握手失败",
        _ => ex.Message
    };

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[32768];
        using var ms = new MemoryStream();

        while (_isRunning && !token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            try
            {
                var message = await ReadMessageAsync(socket, buffer, ms, token);
                if (message == null) break;
                ProcessMessage(message);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch { break; }
        }
    }

    private static async Task<string?> ReadMessageAsync(WebSocket socket, byte[] buffer, MemoryStream ms,
        CancellationToken token)
    {
        ms.SetLength(0);
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        return result.MessageType == WebSocketMessageType.Text
            ? Encoding.UTF8.GetString(ms.ToArray())
            : null;
    }

    /// <summary>
    /// 应用层保活：定时发一条 ping。手机端不会回它（协议层心跳由 KeepAliveInterval 负责），
    /// 真正的用处是持续走一遍发送路径——链路在校园网里悄悄断掉时，这里会先抛异常，
    /// 主循环随即重连，不必等 TCP 自己超时。
    /// </summary>
    private async Task KeepAliveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        while (_isRunning && !token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            try
            {
                await Task.Delay(KeepAliveIntervalMs, token);
                if (socket.State != WebSocketState.Open) break;

                await socket.SendAsync(
                    new ArraySegment<byte>(Encoding.UTF8.GetBytes("{\"type\":\"ping\"}")),
                    WebSocketMessageType.Text, true, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch { break; }
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;
            string type = typeProp.GetString() ?? string.Empty;

            switch (type)
            {
                case "ping":
                    // 手机端建立连接时下发的握手消息，顺带把设备名记下来
                    var ping = JsonSerializer.Deserialize<PingMessage>(json);
                    if (ping != null)
                    {
                        var name = (ping.DeviceName.Length > 0 ? ping.DeviceName : ping.DeviceModel).Trim();
                        if (name.Length > 0 && name != PhoneName)
                        {
                            PhoneName = name;
                            // 握手消息晚于连接成功到达，这里补一次状态刷新，让托盘显示手机型号
                            if (IsConnected)
                            {
                                SetState(ConnectionState.Connected, $"已连接 {name}（{ConnectedDevice}）");
                            }
                        }
                    }
                    break;
                case "notification":
                    var msg = JsonSerializer.Deserialize<NotificationMessage>(json)!;
                    NotificationReceived?.Invoke(this, msg);
                    break;
                case "clear":
                    if (root.TryGetProperty("key", out var keyProp))
                    {
                        NotificationCleared?.Invoke(this, keyProp.GetString() ?? string.Empty);
                    }
                    break;
                case "dnd_status":
                    if (root.TryGetProperty("enabled", out var enProp))
                    {
                        PhoneDndEnabled = enProp.GetBoolean();
                    }
                    break;
                case "cast_devices":
                    var castMsg = JsonSerializer.Deserialize<CastDevicesMessage>(json);
                    if (castMsg != null)
                    {
                        CastDevicesUpdated?.Invoke(this, castMsg);
                    }
                    break;
                case "history_sync":
                    // 手机端推送的离线历史（history_sync_request 的响应）
                    if (root.TryGetProperty("notifications", out var listProp) &&
                        listProp.ValueKind == JsonValueKind.Array)
                    {
                        var list = listProp.Deserialize<List<NotificationMessage>>();
                        if (list != null && list.Count > 0)
                        {
                            HistoryService.Instance.AddRange(list);
                            // 满批次说明手机端可能还有更早的离线记录，用新游标继续拉取下一批
                            if (list.Count >= HistorySyncBatchSize)
                            {
                                _ = SendJsonAsync(new
                                {
                                    type = "history_sync_request",
                                    since = HistoryService.Instance.NewestTimestamp()
                                });
                            }
                        }
                    }
                    break;
            }
        }
        catch { }
    }

    /// <summary>发送打开手机应用指令</summary>
    public async Task SendOpenApp(string packageName)
    {
        await SendJsonAsync(new { type = "open_app", packageName });
    }

    /// <summary>发送媒体控制指令（对应 Android 通知 Action 的 index）</summary>
    public async Task SendMediaAction(string key, int actionIndex)
    {
        await SendJsonAsync(new { type = "media_action", key, actionIndex });
    }

    /// <summary>请求手机端返回妙播设备列表</summary>
    public async Task RequestCastDevices()
    {
        await SendJsonAsync(new { type = "cast_query" });
    }

    /// <summary>请求将音乐流转到指定设备（routeId 由手机端 MediaRouter 提供）</summary>
    public async Task SendCastTransfer(string routeId)
    {
        await SendJsonAsync(new { type = "cast_transfer", routeId });
    }

    /// <summary>请求切换小米妙播放设备：target="local" 切回本机；"device" 投放到音箱/电视（手机端无障碍自动完成）</summary>
    public async Task SendMiPlaySwitch(string target)
    {
        await SendJsonAsync(new { type = "miplay_switch", target });
    }

    private async Task SendJsonAsync(object payload)
    {
        var socket = _webSocket;
        if (socket?.State != WebSocketState.Open) return;
        try
        {
            var json = JsonSerializer.Serialize(payload);
            await socket.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { }
    }

    private void SetState(ConnectionState state, string text)
    {
        bool changed = state != State || !string.Equals(text, StatusText, StringComparison.Ordinal);
        State = state;
        StatusText = text;
        if (!changed) return;

        try { StatusChanged?.Invoke(this, text); } catch { }
        try { ConnectionStateChanged?.Invoke(this, state); } catch { }
    }

    /// <summary>连续连不上时，到达阈值后提示一次（手机端没开服务、校园网屏蔽广播等）。</summary>
    private void NoteFailure()
    {
        if (_failStreakStart == 0) _failStreakStart = Environment.TickCount64;
        if (_hintFired || Environment.TickCount64 - _failStreakStart < HintAfterMs) return;

        _hintFired = true;
        var manualConfigured = LanEndpoint.TryParse(
            AppSettings.Instance.ManualPhoneHost, AppSettings.Instance.ManualPhonePort, out _);

        var hint = manualConfigured
            ? "已按指定地址尝试连接但未成功。请确认手机端 MiToast 已点「启动服务」，且与电脑处于同一网络。"
            : "一直没发现手机。校园网常屏蔽设备发现广播，可在「设置 → 连接」里手动填写手机地址（手机 MiToast 首页有显示）。";

        try { HintRequested?.Invoke(this, hint); } catch { }
    }

    private static void RememberEndpoint(LanEndpoint endpoint)
    {
        var settings = AppSettings.Instance;
        if (settings.LastPhoneHost == endpoint.Host && settings.LastPhonePort == endpoint.Port) return;

        settings.LastPhoneHost = endpoint.Host;
        settings.LastPhonePort = endpoint.Port;
        settings.Save();
    }

    private async Task BackoffAsync(CancellationToken token)
    {
        await DelayInterruptible(_backoffMs, token);
        _backoffMs = Math.Min(_backoffMs * 2, MaxRetryDelayMs);
    }

    /// <summary>可被网络变化/用户操作提前唤醒的等待。</summary>
    private async Task DelayInterruptible(int milliseconds, CancellationToken token)
    {
        var wake = Volatile.Read(ref _wake);
        await Task.WhenAny(Task.Delay(milliseconds, token), wake.Task);
    }

    private void Wake()
    {
        var previous = Interlocked.Exchange(ref _wake, NewSignal());
        try { previous.TrySetResult(); } catch { }
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void HookNetworkChange()
    {
        try
        {
            _localAddressFingerprint = LocalAddressFingerprint();
            NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        }
        catch { }
    }

    private void UnhookNetworkChange()
    {
        try { NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged; } catch { }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        try
        {
            var fingerprint = LocalAddressFingerprint();
            bool addressesChanged = !string.Equals(fingerprint, _localAddressFingerprint, StringComparison.Ordinal);
            _localAddressFingerprint = fingerprint;

            _backoffMs = FastRetryMs;
            _scanDue = true;

            // 本机地址集变了（插拔网线、Wi-Fi 换了 AP/网段、VPN 起停）：
            // 旧连接必然失效，直接掐掉立刻重连，不等 TCP 自己超时。
            if (addressesChanged && IsConnected)
            {
                try { _webSocket?.Abort(); } catch { }
            }

            Wake();
        }
        catch { }
    }

    private static string LocalAddressFingerprint()
    {
        return string.Join("|", DeviceLocator.GetLocalInterfaces()
            .Select(i => i.Address.ToString())
            .OrderBy(s => s, StringComparer.Ordinal));
    }
}
