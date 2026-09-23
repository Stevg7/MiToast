using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MiToast.Network;

/// <summary>一块网卡上的 IPv4 地址与其子网掩码。</summary>
public readonly record struct LocalInterface(IPAddress Address, IPAddress? Mask);

/// <summary>
/// 手机定位：广播发现 + 兜底探测。
///
/// 校园网和家用路由器差别很大：广播常被 AP 隔离或交换机策略丢弃；电脑往往同时插着网线、
/// 连着 Wi-Fi，还挂着 VPN、Hyper-V、WSL 等虚拟网卡，而 255.255.255.255 只会从默认路由那块
/// 网卡发出去——只发一次很容易打偏。所以这里对每块网卡按其子网广播地址分别发送，绑定源地址
/// 强制走对应网卡，并在整个窗口期内持续收集响应（对方晚几毫秒回也能收到）。
/// </summary>
public static class DeviceLocator
{
    public const int DiscoveryPort = 9000;
    private const string DiscoveryRequest = "MTOAST_DISCOVER_REQUEST";
    private const string DiscoveryResponse = "MTOAST_DISCOVER_RESPONSE";

    private const int ProbeRounds = 3;
    private const int RoundIntervalMs = 700;
    private const int SweepConcurrency = 64;
    private const int SweepProbeTimeoutMs = 600;

    /// <summary>UDP 收到 ICMP 端口不可达时不要抛异常打断接收循环（Windows 特有行为）。</summary>
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    /// <summary>本机所有可用于发现的 IPv4 网卡地址，物理网卡优先、虚拟网卡靠后。</summary>
    public static IReadOnlyList<LocalInterface> GetLocalInterfaces()
    {
        var found = new List<(LocalInterface iface, int rank)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch { continue; }

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    var bytes = ua.Address.GetAddressBytes();
                    if (bytes[0] == 127) continue;
                    // 169.254.x.x 是没拿到 DHCP 时的自动地址，上面连不通任何设备
                    if (bytes[0] == 169 && bytes[1] == 254) continue;

                    IPAddress? mask = null;
                    try { mask = ua.IPv4Mask; } catch { }

                    found.Add((new LocalInterface(ua.Address, mask), Rank(ni.NetworkInterfaceType)));
                }
            }
        }
        catch
        {
            // 枚举网卡失败时退化为空列表，上层会走"无候选"分支
        }

        return found.OrderBy(f => f.rank).Select(f => f.iface).ToList();
    }

    private static int Rank(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => 0,
        NetworkInterfaceType.Wireless80211 => 1,
        // VPN / Hyper-V / WSL / VMware 等虚拟网卡：手机不可能在这里，放最后
        _ => 2
    };

    /// <summary>
    /// 向每块网卡所在子网广播发现请求，把窗口期内收到的候选地址逐个回调。
    /// 回调可能在任意线程触发，调用方自行保证线程安全。
    /// </summary>
    public static async Task ProbeAsync(int windowMs, Action<LanEndpoint> onCandidate, CancellationToken token)
    {
        var sockets = new List<(UdpClient Client, List<IPEndPoint> Targets)>();

        foreach (var iface in GetLocalInterfaces())
        {
            UdpClient client;
            try
            {
                // 绑定到具体源地址 = 强制从这块网卡发出，多网卡环境下这是关键
                client = new UdpClient(new IPEndPoint(iface.Address, 0)) { EnableBroadcast = true };
                DisableUdpConnectionReset(client);
            }
            catch
            {
                continue;
            }

            var targets = new List<IPEndPoint> { new(IPAddress.Broadcast, DiscoveryPort) };

            // 子网定向广播（192.168.1.255 这种）只在网段不大时补充发送：校园网常见 /16 大网段，
            // 往它的定向广播地址发包会命中成千上万台设备，容易被交换设备当广播风暴处理。
            // 而 255.255.255.255 是受限广播，只在本地二层网段内传播，语义正好是"找同一网段里的设备"。
            var directed = GetDirectedBroadcast(iface);
            if (directed != null) targets.Add(new IPEndPoint(directed, DiscoveryPort));

            sockets.Add((client, targets));
        }

        if (sockets.Count == 0) return;

        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        windowCts.CancelAfter(windowMs);

        var seen = new HashSet<string>();
        var payload = Encoding.UTF8.GetBytes(DiscoveryRequest);

        try
        {
            var tasks = sockets.Select(s => ReceiveLoopAsync(s.Client, seen, onCandidate, windowCts.Token)).ToList();
            tasks.Add(SendRoundsAsync(sockets, payload, windowCts.Token));
            await Task.WhenAll(tasks);
        }
        catch
        {
            // 窗口到期/被取消属正常结束
        }
        finally
        {
            foreach (var (client, _) in sockets)
            {
                try { client.Dispose(); } catch { }
            }
        }
    }

    private static async Task SendRoundsAsync(
        List<(UdpClient Client, List<IPEndPoint> Targets)> sockets, byte[] payload, CancellationToken token)
    {
        for (int round = 0; round < ProbeRounds; round++)
        {
            if (token.IsCancellationRequested) return;

            foreach (var (client, targets) in sockets)
            {
                foreach (var target in targets)
                {
                    try { await client.SendAsync(payload.AsMemory(), target, token); }
                    catch { /* 个别网卡不允许广播，跳过 */ }
                }
            }

            try { await Task.Delay(RoundIntervalMs, token); }
            catch { return; }
        }
    }

    private static async Task ReceiveLoopAsync(
        UdpClient client, HashSet<string> seen, Action<LanEndpoint> onCandidate, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await client.ReceiveAsync(token);
                if (!TryParseResponse(result, out var reported)) continue;

                var source = result.RemoteEndPoint.Address.ToString();

                // UDP 响应的源地址是"电脑视角下手机的真实地址"，比手机自报的地址更可靠：
                // 手机可能同时挂着 VPN、流量卡或多个网段，自报的那个未必可达。
                Report(new LanEndpoint(source, reported.Port));
                if (!string.Equals(source, reported.Host, StringComparison.OrdinalIgnoreCase))
                {
                    Report(reported); // 自报地址也留着，作为备选
                }
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { await PauseAsync(token); }
            catch { await PauseAsync(token); }
        }

        void Report(LanEndpoint endpoint)
        {
            lock (seen)
            {
                if (!seen.Add($"{endpoint.Host}:{endpoint.Port}")) return;
            }
            onCandidate(endpoint);
        }

        static async Task PauseAsync(CancellationToken token)
        {
            // 出错时短暂停顿，避免在异常状态下空转
            try { await Task.Delay(100, token); } catch { }
        }
    }

    private static bool TryParseResponse(UdpReceiveResult result, out LanEndpoint reported)
    {
        reported = default;

        var message = Encoding.UTF8.GetString(result.Buffer, 0, result.Buffer.Length).Trim();
        if (!message.StartsWith(DiscoveryResponse, StringComparison.Ordinal)) return false;

        var parts = message.Split('|');
        if (parts.Length < 3) return false;
        if (!int.TryParse(parts[2].Trim(), out int port) || port < 1 || port > 65535) return false;
        if (!IPAddress.TryParse(parts[1].Trim(), out var address)) return false;

        reported = new LanEndpoint(address.ToString(), port);
        return true;
    }

    /// <summary>
    /// 兜底手段：广播被完全屏蔽时，逐个探测本机各网卡所在 /24 网段的 WebSocket 端口。
    /// 只在用户显式开启时调用——部分校园网把端口扫描视为违规行为。
    /// </summary>
    public static async Task SweepLocalSubnetsAsync(int port, Action<LanEndpoint> onCandidate, CancellationToken token)
    {
        var targets = BuildSweepTargets(GetLocalInterfaces());

        using var gate = new SemaphoreSlim(SweepConcurrency);
        var tasks = new List<Task>(targets.Count);

        foreach (var address in targets)
        {
            tasks.Add(SweepOneAsync(address));
        }

        try { await Task.WhenAll(tasks); }
        catch { }

        async Task SweepOneAsync(IPAddress address)
        {
            try { await gate.WaitAsync(token); }
            catch { return; }

            try
            {
                if (await IsPortOpenAsync(address, port, token))
                {
                    onCandidate(new LanEndpoint(address.ToString(), port, Trusted: false));
                }
            }
            catch { }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// 扫描目标：本机各网卡所在 /24 网段里的其他地址。
    /// 只扫最后一个字节——同一广播域里手机和电脑通常在同一 /24，扫更宽的网段既慢也没意义；
    /// 本机自己的地址一律跳过。
    /// </summary>
    internal static IReadOnlyList<IPAddress> BuildSweepTargets(IReadOnlyList<LocalInterface> interfaces)
    {
        var self = new HashSet<string>(interfaces.Select(i => i.Address.ToString()));
        var seen = new HashSet<string>();
        var targets = new List<IPAddress>();

        foreach (var iface in interfaces)
        {
            var bytes = iface.Address.GetAddressBytes();
            if (bytes.Length != 4) continue;

            for (int last = 1; last <= 254; last++)
            {
                var host = $"{bytes[0]}.{bytes[1]}.{bytes[2]}.{last}";
                if (self.Contains(host) || !seen.Add(host)) continue;

                targets.Add(new IPAddress(new[] { bytes[0], bytes[1], bytes[2], (byte)last }));
            }
        }

        return targets;
    }

    private static async Task<bool> IsPortOpenAsync(IPAddress address, int port, CancellationToken token)
    {
        using var client = new TcpClient(address.AddressFamily);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(SweepProbeTimeoutMs);
            await client.ConnectAsync(address, port, cts.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 网卡所在子网的定向广播地址（192.168.1.5/24 → 192.168.1.255）。
    /// 网段大于 /22 时返回 null，避免在大网段上制造广播流量。
    /// </summary>
    private static IPAddress? GetDirectedBroadcast(LocalInterface iface)
    {
        if (iface.Mask == null) return null;

        var address = iface.Address.GetAddressBytes();
        var mask = iface.Mask.GetAddressBytes();
        if (address.Length != 4 || mask.Length != 4) return null;

        // 掩码前三段都是 255（即 /24 及更窄的网段）才认为定向广播是"网段内的一次广播"
        if (mask[0] != 255 || mask[1] != 255 || mask[2] != 255) return null;

        var broadcast = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            broadcast[i] = (byte)(address[i] | ~mask[i]);
        }

        // /32 之类的掩码算出来的"广播地址"就是自己，没有意义
        return broadcast.SequenceEqual(address) ? null : new IPAddress(broadcast);
    }

    /// <summary>
    /// 关闭 SIO_UDP_CONNRESET：否则广播后若收到 ICMP 端口不可达，后续 ReceiveAsync 会直接抛
    /// SocketException，一次异常就把整轮发现的接收循环打断。
    /// </summary>
    private static void DisableUdpConnectionReset(UdpClient client)
    {
        try
        {
            client.Client.IOControl(SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
        }
        catch
        {
            // 非 Windows 或某些协议栈不支持：忽略
        }
    }
}
