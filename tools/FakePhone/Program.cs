// 本机"假手机"演示服务：模拟 MiToast 手机端 WebSocket 服务器的对外协议，
// 让 PC 端 MiToast 直接连上本机（无需真机在线），推送一条"美团外卖"从下单到
// 送达的 6 阶段通知，与真实通知走完全相同的链路（WebSocket → 卡片原地更新）。
//
// 协议（见 NetworkManager.cs）：PC 连入后，手机端先发 {"type":"ping","deviceName":...}
// 握手；PC 发 {"type":"auth","token":<配对码>}（演示模式直接放行）；认证后 PC 会发
// history_sync_request，回 {"type":"history_sync","notifications":[]}；对保活 ping 回 pong。
// 消息字段为 camelCase，与模型上的 [JsonPropertyName] 一致。
//
// 用法：dotnet run --project tools/FakePhone [-- port intervalMs]
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var port = args.Length > 0 ? int.Parse(args[0]) : 18080;
var intervalMs = args.Length > 1 ? int.Parse(args[1]) : 4000;

const string pkg = "com.sankuai.meituan";
const string appName = "美团外卖";
const string simKey = pkg + "|mitoast_sim|9527";
const string merchant = "老上海馄饨·粥饭（科技园店）";
var orderDetail = "鲜肉大馄饨×1、葱油拌面×1、酸梅汤×1\n合计 ¥32.80 · 预计12:35送达";

// (current, label, content, bigText, ongoing)
var stages = new (int current, string label, string content, string bigText, bool ongoing)[]
{
    (1, "已下单", "您的订单已下单成功，等待商家接单", $"{merchant}\n{orderDetail}", true),
    (2, "已接单", "商家已接单，正在为您准备美食", $"{merchant}\n商家已接单，正在备菜制作\n{orderDetail}", true),
    (3, "商家制作中", "商家制作中，骑手正赶往商家", $"{merchant}\n商家正在出餐，骑手赶往商家取餐\n预计12:35送达", true),
    (4, "骑手已取餐", "骑手已取餐，正快马加鞭为您配送", "骑手王师傅已取餐\n距商家 0.1 公里 · 预计10分钟送达", true),
    (5, "配送中", "骑手配送中，距您1.2公里，预计5分钟送达", "骑手王师傅配送中\n距您 1.2 公里 · 预计5分钟送达\n电话可在订单详情联系骑手", true),
    (6, "已送达", "您的外卖已送达，祝您用餐愉快", "您的外卖已送达，请及时取餐\n如有问题可在订单页申请售后，祝您用餐愉快", false),
};

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
};

var outbox = new ConcurrentQueue<string>();
var authenticated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

object SendLock = new();
WebSocket socket = null!;

async Task SendAsync(object payload)
{
    var json = JsonSerializer.Serialize(payload, jsonOptions);
    var bytes = Encoding.UTF8.GetBytes(json);
    lock (SendLock)
    {
        socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None)
              .GetAwaiter().GetResult();
    }
}

async Task ReceiveLoopAsync()
{
    var buffer = new byte[8192];
    while (socket.State == WebSocketState.Open)
    {
        string message;
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                Console.WriteLine($"[fake-phone] closed by peer: {result.CloseStatus} {result.CloseStatusDescription}");
                authenticated.TrySetResult(false);
                return;
            }
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        message = Encoding.UTF8.GetString(ms.ToArray());
        using var doc = JsonDocument.Parse(message);
        var type = doc.RootElement.GetProperty("type").GetString();
        switch (type)
        {
            case "auth":
                var token = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
                Console.WriteLine($"[fake-phone] auth received (token 长度 {token?.Length ?? 0}) —— 演示模式直接放行");
                authenticated.TrySetResult(true);
                break;
            case "ping":
                await SendAsync(new { type = "pong" });
                break;
            case "history_sync_request":
                await SendAsync(new { type = "history_sync", notifications = Array.Empty<object>() });
                break;
        }
    }
}

var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();
Console.WriteLine($"[fake-phone] listening on ws://127.0.0.1:{port}/ — 等待 PC 端 MiToast 连入…");

var ctx = await listener.GetContextAsync();
socket = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
Console.WriteLine("[fake-phone] PC 已连入，发送握手 ping…");
await SendAsync(new { type = "ping", deviceName = "FakePhone-演示机" });

var receiver = ReceiveLoopAsync();

// 演示模式直接放行认证；等 PC 发来 auth 再开始推阶段
await authenticated.Task;
if (socket.State != WebSocketState.Open)
{
    Console.WriteLine("[fake-phone] 连接在认证前被关闭，退出");
    return;
}
await SendAsync(new { type = "history_sync", notifications = Array.Empty<object>() });
Console.WriteLine("[fake-phone] 认证通过，开始推送外卖 6 阶段…");

var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
foreach (var (current, label, content, bigText, ongoing) in stages)
{
    await SendAsync(new
    {
        type = "notification",
        id = $"{pkg}_9527_{now}_{current}",
        key = simKey,
        packageName = pkg,
        appName,
        title = appName,
        content,
        bigTitle = "",
        bigText,
        subText = merchant,
        ticker = "",
        hintTitle = "",
        hintText = "",
        iconBase64 = "",
        timestamp = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
        category = "delivery",
        isOngoing = ongoing,
        groupKey = "",
        peopleCount = 0,
        progress = new { current, total = 7, label },
        mediaIsPlaying = false,
        mediaPositionMs = -1,
        mediaDurationMs = -1,
    });
    Console.WriteLine($"[fake-phone] 阶段 {current}/6 已推送：{label}");
    if (current != stages[^1].current) await Task.Delay(intervalMs);
}

// 送达卡片停留几秒后清掉，模拟用户划掉通知
await Task.Delay(6000);
await SendAsync(new { type = "clear", key = simKey });
Console.WriteLine("[fake-phone] 已发送 clear，模拟卡片移除，演示结束");

listener.Stop();
