using System;
using System.Net;

namespace MiToast.Network;

/// <summary>
/// 手机端 WebSocket 服务地址。
///
/// <paramref name="Trusted"/> 表示地址来源是否可靠：手动填写、上次成功连接、广播发现都算可靠；
/// 网段扫描扫出来的候选不可信（可能只是同网段里另一个恰好开了 8080 的服务），
/// 连接后需要先校验对方确实是 MiToast 才采用。
/// </summary>
public readonly record struct LanEndpoint(string Host, int Port, bool Trusted = true)
{
    public const int DefaultPort = 8080;

    /// <summary>WebSocket 连接地址；IPv6 字面量需要方括号包裹。</summary>
    public string Url => Host.Contains(':') ? $"ws://[{Host}]:{Port}/" : $"ws://{Host}:{Port}/";

    public override string ToString()
        => Host.Contains(':') ? $"[{Host}]:{Port}"
            : Port == DefaultPort ? Host : $"{Host}:{Port}";

    /// <summary>
    /// 解析用户输入或持久化的地址，支持 <c>192.168.1.5</c>、<c>192.168.1.5:8080</c>、
    /// <c>fe80::1</c>、<c>[fe80::1]:8080</c>、<c>ws://192.168.1.5:8080</c> 以及主机名。
    /// </summary>
    public static bool TryParse(string? text, int defaultPort, out LanEndpoint endpoint)
    {
        endpoint = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        if (s.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) s = s[5..];
        else if (s.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) s = s[6..];
        s = s.TrimEnd('/');
        if (s.Length == 0) return false;

        string host;
        int port = defaultPort;

        if (s[0] == '[')
        {
            int close = s.IndexOf(']');
            if (close <= 1) return false;
            host = s[1..close];
            var rest = s[(close + 1)..];
            if (rest.Length > 0 && (rest[0] != ':' || !TryParsePort(rest[1..], out port))) return false;
        }
        else if (s.IndexOf(':') != s.LastIndexOf(':'))
        {
            // 多个冒号 = 裸 IPv6 字面量，端口用默认值
            host = s;
        }
        else
        {
            int idx = s.IndexOf(':');
            if (idx < 0)
            {
                host = s;
            }
            else
            {
                host = s[..idx];
                if (!TryParsePort(s[(idx + 1)..], out port)) return false;
            }
        }

        host = host.Trim();
        if (host.Length == 0) return false;

        if (IPAddress.TryParse(host, out var ip))
        {
            host = ip.ToString(); // 规范化（IPv6 压缩形式、去掉 IPv4 前导零）
        }
        else
        {
            // 主机名：只允许常见字符，避免用户把 "192.168.1.5 8080" 这类夹空格的内容写进配置
            foreach (char c in host)
            {
                if (!char.IsLetterOrDigit(c) && c != '.' && c != '-' && c != '_') return false;
            }
        }

        endpoint = new LanEndpoint(host, port);
        return true;
    }

    private static bool TryParsePort(string text, out int port)
    {
        port = 0;
        if (!int.TryParse(text.Trim(), out int value) || value < 1 || value > 65535) return false;
        port = value;
        return true;
    }
}
