using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiToast.Models;

namespace MiToast.Network;

public class NetworkManager
{
    private static readonly Lazy<NetworkManager> _instance = new(() => new NetworkManager());
    public static NetworkManager Instance => _instance.Value;

    private const int DiscoveryPort = 9000;
    private const string DiscoveryRequest = "MTOAST_DISCOVER_REQUEST";
    private const string DiscoveryResponse = "MTOAST_DISCOVER_RESPONSE";
    private const int MaxRetryDelayMs = 30000;
    private const int KeepAliveIntervalMs = 30000;

    private CancellationTokenSource? _cts;
    private ClientWebSocket? _webSocket;
    private bool _isRunning;

    public event EventHandler<NotificationMessage>? NotificationReceived;
    public event EventHandler<string>? NotificationCleared;
    public event EventHandler<string>? StatusChanged;

    /// <summary>手机端返回妙播设备列表/妙播投射状态时触发（cast_devices 消息）。</summary>
    public event EventHandler<CastDevicesMessage>? CastDevicesUpdated;

    public string ConnectedDevice { get; private set; } = string.Empty;
    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    /// <summary>手机端勿扰模式是否开启（由 Android 端 dnd_status 广播更新）</summary>
    public bool PhoneDndEnabled { get; private set; }

    private NetworkManager() { }

    public async Task StartAsync()
    {
        if (_isRunning) return;
        _isRunning = true;
        _cts = new CancellationTokenSource();
        _ = RunAsync(_cts.Token);
    }

    public void Start()
    {
        _ = StartAsync();
    }

    public void Stop()
    {
        _isRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _webSocket?.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stopping", CancellationToken.None).Wait(); } catch { }
        try { _webSocket?.Dispose(); } catch { }
    }

    private async Task RunAsync(CancellationToken token)
    {
        int retryDelay = 1000;
        while (_isRunning && !token.IsCancellationRequested)
        {
            try
            {
                StatusChanged?.Invoke(this, "正在搜索 Android 设备...");
                var (ip, port) = await DiscoverAndroidDeviceAsync(token);
                if (string.IsNullOrEmpty(ip))
                {
                    await Task.Delay(retryDelay, token);
                    retryDelay = Math.Min(retryDelay * 2, MaxRetryDelayMs);
                    continue;
                }

                retryDelay = 1000;
                StatusChanged?.Invoke(this, $"发现设备 {ip}:{port}，正在连接...");

                await ConnectAsync(ip, port, token);
                ConnectedDevice = ip;
                StatusChanged?.Invoke(this, $"已连接到 Android 设备 ({ip})");

                var receiveTask = ReceiveLoopAsync(token);
                var keepAliveTask = KeepAliveLoopAsync(token);
                await Task.WhenAny(receiveTask, keepAliveTask);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, $"连接断开: {ex.Message}");
                try { _webSocket?.Dispose(); } catch { }
                _webSocket = null;
                await Task.Delay(retryDelay, token);
                retryDelay = Math.Min(retryDelay * 2, MaxRetryDelayMs);
            }
        }
    }

    private async Task<(string ip, int port)> DiscoverAndroidDeviceAsync(CancellationToken token)
    {
        using var udpClient = new UdpClient { EnableBroadcast = true };
        var endpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
        var sendBuf = Encoding.UTF8.GetBytes(DiscoveryRequest);

        try { await udpClient.SendAsync(sendBuf, sendBuf.Length, endpoint); } catch { return (string.Empty, 0); }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(3000);
            var result = await udpClient.ReceiveAsync(cts.Token);
            var msg = Encoding.UTF8.GetString(result.Buffer, 0, result.Buffer.Length);
            if (msg.StartsWith(DiscoveryResponse))
            {
                var parts = msg.Split('|');
                if (parts.Length >= 3 && int.TryParse(parts[2], out int port)) return (parts[1], port);
            }
        }
        catch { }

        return (string.Empty, 0);
    }

    private async Task ConnectAsync(string ip, int port, CancellationToken token)
    {
        _webSocket = new ClientWebSocket();
        var uri = new Uri($"ws://{ip}:{port}");
        await _webSocket.ConnectAsync(uri, token);
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var buffer = new byte[32768];
        using var ms = new MemoryStream();

        while (_isRunning && !token.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
        {
            try
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;
                string message = Encoding.UTF8.GetString(ms.ToArray());
                ProcessMessage(message);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch { break; }
        }
    }

    private async Task KeepAliveLoopAsync(CancellationToken token)
    {
        while (_isRunning && !token.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await Task.Delay(KeepAliveIntervalMs, token);
                if (_webSocket?.State == WebSocketState.Open)
                {
                    await _webSocket.SendAsync(
                        new ArraySegment<byte>(Encoding.UTF8.GetBytes("{\"type\":\"ping\"}")),
                        WebSocketMessageType.Text, true, token);
                }
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
        if (_webSocket?.State != WebSocketState.Open) return;
        try
        {
            var json = JsonSerializer.Serialize(payload);
            await _webSocket.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)),
                WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch { }
    }
}
