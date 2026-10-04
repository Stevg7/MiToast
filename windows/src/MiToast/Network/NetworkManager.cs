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

/// <summary>给用户的一次性连接提示（托盘气泡）：标题随失败原因变化，配对码被拒不能说成"没找到手机"。</summary>
public sealed record ConnectionHint(string Title, string Text);

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

    /// <summary>认证结算窗口：手机端配对码不对会立刻 close(4001)，等一小会儿再宣布"已连接"。</summary>
    private const int AuthSettleMs = 500;

    /// <summary>手机端拒绝配对码时的 WebSocket 关闭码（与 MiToastWebSocketServer 一致）。</summary>
    private const int AuthRejectCloseCode = 4001;

    /// <summary>发 ping 后等 pong 的上限；对方会回 pong 却没等到时判定链路半开。</summary>
    private const int PongTimeoutMs = 8000;

    /// <summary>认证结算结果。</summary>
    private const int SettleOk = 0;
    private const int SettleAuthRejected = 1;
    private const int SettleLinkLost = 2;

    /// <summary>手机端可能下发的消息类型，用于识别"连上的是不是 MiToast"。</summary>
    private static readonly HashSet<string> KnownMessageTypes = new()
    {
        "ping", "pong", "notification", "clear", "dnd_status", "cast_devices", "history_sync"
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

    /// <summary>本轮失败里出现过配对码被拒：提示要往"配对码不对"上引，不能说"没找到手机"。</summary>
    private bool _lastFailAuth;

    /// <summary>对端是否会回 pong（新版手机才会）。不会回就不按丢 pong 判死，避免误杀旧版手机。</summary>
    private volatile bool _pongSeen;

    /// <summary>最近一次收到 pong 的时刻（Environment.TickCount64），0 表示本次连接还没收到过。</summary>
    private long _lastPongAt;

    /// <summary>连通性探测去重：网络抖动会连发多轮 NetworkAddressChanged。</summary>
    private int _verifyInFlight;

    /// <summary>同一 WebSocket 上 SendAsync 不可并发（保活、探测、UI 指令、历史续拉会撞车），统一过发送锁。</summary>
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public event EventHandler<NotificationMessage>? NotificationReceived;
    public event EventHandler<string>? NotificationCleared;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<ConnectionState>? ConnectionStateChanged;

    /// <summary>长时间连不上手机/配对码被拒时触发一次，引导用户处理（标题随失败原因变化）。</summary>
    public event EventHandler<ConnectionHint>? HintRequested;

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
    /// 立即重新搜索并连接：放弃当前退避等待，让主循环马上重新走一遍发现流程。
    /// 上次成功的地址仍会作为候选优先尝试（广播被屏蔽时它是唯一可用的路径）。
    /// 已连接时先探测现有连接是否真的活着：活着只刷新状态展示，不掐断健康连接——
    /// 否则"刷新"这个动作本身就会制造"连接已断开"假象。连接参数（配对码/地址）变化时传 force 强制重连。
    /// </summary>
    public void Reconnect(bool force = false)
    {
        if (!force && IsConnected)
        {
            _ = VerifyLiveConnectionAsync();
            return;
        }

        _backoffMs = FastRetryMs;
        _scanDue = true;
        try { _webSocket?.Abort(); } catch { }
        Wake();
    }

    /// <summary>
    /// 已连接时的连通性探测：发一条 ping 等 pong。对端回过 pong（新版手机）就按 pong 判活/死；
    /// 旧版手机不回 pong，退化成"发送路径没抛异常就算活"。判定死亡才掐断重连。
    /// </summary>
    private async Task VerifyLiveConnectionAsync()
    {
        if (Interlocked.Exchange(ref _verifyInFlight, 1) != 0) return;
        try
        {
            var socket = _webSocket;
            if (socket?.State != WebSocketState.Open) return;

            long before = Interlocked.Read(ref _lastPongAt);
            bool canPong = _pongSeen;
            if (!await SendJsonAsync(new { type = "ping" }))
            {
                DropAndRescan(socket);
                return;
            }

            for (int i = 0; i < 25 && Interlocked.Read(ref _lastPongAt) == before; i++)
            {
                await Task.Delay(100);
            }

            bool ponged = Interlocked.Read(ref _lastPongAt) != before;
            if (ponged) _pongSeen = true;
            else if (canPong || socket.State != WebSocketState.Open)
            {
                DropAndRescan(socket);
                return;
            }

            // 活着：只刷新状态展示（文本没变时 SetState 自动去重，不会打扰用户）
            if (State == ConnectionState.Connected)
            {
                SetState(ConnectionState.Connected, ConnectedText(ConnectedDevice));
            }
        }
        catch { }
        finally { Interlocked.Exchange(ref _verifyInFlight, 0); }
    }

    /// <summary>判定链路已死：掐断并让主循环立刻重新搜索。</summary>
    private void DropAndRescan(ClientWebSocket socket)
    {
        _backoffMs = FastRetryMs;
        _scanDue = true;
        try { socket.Abort(); } catch { }
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

                var connection = connected.Value;
                int settle;
                using (connection.Socket)
                {
                    _webSocket = connection.Socket;
                    ConnectedDevice = connection.Endpoint.ToString();
                    RememberEndpoint(connection.Endpoint);
                    _pongSeen = false;
                    Interlocked.Exchange(ref _lastPongAt, 0);

                    // 接入认证：先发配对码，手机端校验失败会立刻 close(4001)。
                    // 认证结算之前不宣布"已连接"、也不清失败计时——否则配对码不对时状态会在
                    // "已连接/连接已断开"之间来回跳，看起来就像软件没刷新状态。
                    SetState(ConnectionState.Connecting, $"正在验证配对码（{connection.Endpoint}）...");
                    if (await SendJsonAsync(new { type = "auth", token = AppSettings.Instance.PairCode }))
                    {
                        var receiveTask = ReceiveLoopAsync(connection.Socket, token);
                        settle = await AuthSettleAsync(connection.Socket, receiveTask, token);

                        if (settle == SettleOk)
                        {
                            _backoffMs = FastRetryMs;
                            _failStreakStart = 0;
                            _hintFired = false;
                            _lastFailAuth = false;

                            SetState(ConnectionState.Connected, ConnectedText(connection.Endpoint.ToString()));

                            // 认证通过后再请求历史同步（未认证连接手机端一律拒绝）
                            await SendJsonAsync(new
                            {
                                type = "history_sync_request",
                                since = HistoryService.Instance.NewestTimestamp()
                            });

                            await Task.WhenAny(
                                receiveTask,
                                KeepAliveLoopAsync(connection.Socket, token));
                        }
                        else
                        {
                            NoteFailure(settle == SettleAuthRejected);
                            try { connection.Socket.Abort(); } catch { }
                            try { await receiveTask; } catch { }
                        }
                    }
                    else
                    {
                        settle = SettleLinkLost;
                        NoteFailure();
                    }
                }

                _webSocket = null;
                ConnectedDevice = string.Empty;
                PhoneName = string.Empty;
                if (_isRunning && !token.IsCancellationRequested)
                {
                    SetState(ConnectionState.Searching, settle == SettleAuthRejected
                        ? "配对码不正确：请在「设置 → 连接」核对手机端配对码"
                        : "连接已断开，正在重新搜索...");
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

    /// <summary>
    /// 认证结算：给手机端一点时间回应配对码。配对码正确就保持沉默（连接继续收消息），
    /// 错误立刻 close(4001)。结算没过就不算连接成功，避免"已连接/连接已断开"来回跳。
    /// </summary>
    private static async Task<int> AuthSettleAsync(ClientWebSocket socket, Task receiveTask, CancellationToken token)
    {
        long deadline = Environment.TickCount64 + AuthSettleMs;
        while (Environment.TickCount64 < deadline)
        {
            if (receiveTask.IsCompleted || socket.State != WebSocketState.Open)
            {
                return (int)(socket.CloseStatus ?? 0) == AuthRejectCloseCode ? SettleAuthRejected : SettleLinkLost;
            }
            try { await Task.Delay(50, token); }
            catch (OperationCanceledException) { return SettleLinkLost; }
        }

        return receiveTask.IsCompleted || socket.State != WebSocketState.Open
            ? ((int)(socket.CloseStatus ?? 0) == AuthRejectCloseCode ? SettleAuthRejected : SettleLinkLost)
            : SettleOk;
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
    /// 应用层保活 + 半开链路检测：定时发一条 ping，手机端收到后回 pong。
    /// 发送路径在链路悄悄断掉时会先抛异常（校园网 AP 回收空闲连接），这里立刻退出触发重连；
    /// 对会回 pong 的手机，超时没等到 pong 同样判死——Wi-Fi 切换后 socket 看着还 Open，
    /// 不这样查状态会一直卡在"已连接"。旧版手机不回 pong，不按丢 pong 判死，避免误杀。
    /// </summary>
    private async Task KeepAliveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        while (_isRunning && !token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            try
            {
                await Task.Delay(KeepAliveIntervalMs, token);
                if (socket.State != WebSocketState.Open) break;

                long before = Interlocked.Read(ref _lastPongAt);
                bool canPong = _pongSeen;
                if (!await SendRawAsync("{\"type\":\"ping\"}")) break;

                long deadline = Environment.TickCount64 + PongTimeoutMs;
                while (Interlocked.Read(ref _lastPongAt) == before && Environment.TickCount64 < deadline)
                {
                    await Task.Delay(100, token);
                }

                if (Interlocked.Read(ref _lastPongAt) != before) _pongSeen = true;
                else if (canPong) break; // 会回 pong 的手机没回：链路已半开
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
                            // 握手消息晚于连接成功到达，这里补一次状态刷新，让托盘显示手机型号；
                            // 认证结算中（Connecting）还不算连接成功，别提前把状态刷成"已连接"
                            if (State == ConnectionState.Connected)
                            {
                                SetState(ConnectionState.Connected, ConnectedText(ConnectedDevice));
                            }
                        }
                    }
                    break;
                case "pong":
                    // 手机端对保活/探测 ping 的回应，链路活性凭据
                    _pongSeen = true;
                    Interlocked.Exchange(ref _lastPongAt, Environment.TickCount64);
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

    private async Task<bool> SendJsonAsync(object payload)
        => await SendRawAsync(JsonSerializer.Serialize(payload));

    /// <summary>发送原始文本帧。同一 WebSocket 上 SendAsync 不可并发，统一过发送锁；发送失败返回 false。</summary>
    private async Task<bool> SendRawAsync(string json)
    {
        var socket = _webSocket;
        if (socket?.State != WebSocketState.Open) return false;
        try
        {
            await _sendLock.WaitAsync();
            try
            {
                if (socket.State != WebSocketState.Open) return false;
                await socket.SendAsync(
                    new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                    WebSocketMessageType.Text, true, CancellationToken.None);
                return true;
            }
            finally { _sendLock.Release(); }
        }
        catch { return false; }
    }

    /// <summary>已连接状态文案（拿到手机型号后带上型号）。</summary>
    private string ConnectedText(string endpoint) => PhoneName.Length > 0
        ? $"已连接 {PhoneName}（{endpoint}）"
        : $"已连接到 Android 设备（{endpoint}）";

    private void SetState(ConnectionState state, string text)
    {
        bool changed = state != State || !string.Equals(text, StatusText, StringComparison.Ordinal);
        State = state;
        StatusText = text;
        if (!changed) return;

        try { StatusChanged?.Invoke(this, text); } catch { }
        try { ConnectionStateChanged?.Invoke(this, state); } catch { }
    }

    /// <summary>
    /// 连续连不上时，到达阈值后提示一次（手机端没开服务、校园网屏蔽广播等）。
    /// authRejected：这轮失败里出现过配对码被拒（手机端 close 4001）——提示要引向配对码，不能说"没找到手机"。
    /// </summary>
    private void NoteFailure(bool authRejected = false)
    {
        if (authRejected) _lastFailAuth = true;
        if (_failStreakStart == 0) _failStreakStart = Environment.TickCount64;
        if (_hintFired || Environment.TickCount64 - _failStreakStart < HintAfterMs) return;

        _hintFired = true;
        var manualConfigured = LanEndpoint.TryParse(
            AppSettings.Instance.ManualPhoneHost, AppSettings.Instance.ManualPhonePort, out _);

        var hint = _lastFailAuth
            ? new ConnectionHint(
                "MiToast 配对失败",
                "手机已找到但配对码不对。请在手机 MiToast 首页查看配对码，填到「设置 → 连接 → 配对码」并保存。")
            : manualConfigured
                ? new ConnectionHint(
                    "MiToast 没找到手机",
                    "已按指定地址尝试连接但未成功。请确认手机端 MiToast 已点「启动服务」，且与电脑处于同一网络。")
                : new ConnectionHint(
                    "MiToast 没找到手机",
                    "一直没发现手机。校园网常屏蔽设备发现广播，可在「设置 → 连接」里手动填写手机地址（手机 MiToast 首页有显示）。");

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

            // 本机地址集变了（插拔网线、Wi-Fi 换了 AP/网段、VPN/虚拟网卡起停）：先探测现有连接
            // 是否还活着。虚拟网卡（WSL/Docker/Hyper-V）抖动很常见，一变就掐健康连接会让软件
            // 误报"连接已断开"甚至弹"没找到手机"；真断了探测会在秒级发现并立刻重连，不等 TCP 超时。
            if (addressesChanged && IsConnected)
            {
                _ = VerifyLiveConnectionAsync();
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
