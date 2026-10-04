using System;

namespace MiToast.Services;

/// <summary>
/// 客户端内的「局域网 MCP 接口」开关：按设置常驻启停共享的 <see cref="McpLanServer"/>。
/// 启动时与每次设置变化（AppSettings.Changed）调用 <see cref="ApplyFromSettings"/>：
/// 开关开启且配对码已配置才监听；端口变化自动重启；配对码被清空立即停服
/// （服务内部每次连接也会现取配对码，改码即时生效，无需重启）。
/// </summary>
public static class McpLanService
{
    private static readonly object Gate = new();
    private static McpLanServer? _server;
    private static int _runningPort;
    private static string? _error;

    /// <summary>状态变化（启动/停止/失败），供设置页刷新状态行。</summary>
    public static event EventHandler? StatusChanged;

    public static bool IsRunning => _server?.IsRunning == true;

    /// <summary>给设置页状态行用的一句话状态。</summary>
    public static string Describe()
    {
        lock (Gate)
        {
            if (IsRunning)
            {
                var ips = McpLanServer.LocalIpv4Addresses();
                string endpoint = ips.Count > 0
                    ? $"http://{ips[0]}:{_runningPort}/mcp"
                    : $"http://<本机IP>:{_runningPort}/mcp";
                return $"运行中：{endpoint}（Bearer 配对码鉴权，只读）";
            }
            if (!string.IsNullOrEmpty(_error))
            {
                return $"未运行：{_error}";
            }
            if (AppSettings.Instance.McpLanEnabled)
            {
                return "未运行：需要先在「连接」里设置配对码";
            }
            return "关闭（开启后监听 18081 端口，凭 Bearer 配对码只读访问）";
        }
    }

    /// <summary>按当前设置对齐服务状态（幂等，可反复调用）。</summary>
    public static void ApplyFromSettings()
    {
        var settings = AppSettings.Instance;
        bool want = settings.McpLanEnabled;
        int port = settings.McpLanPort;
        bool hasPairCode = !string.IsNullOrWhiteSpace(settings.PairCode);

        lock (Gate)
        {
            try
            {
                if (!want || !hasPairCode)
                {
                    StopLocked();
                    _error = want ? "需要先在「连接」里设置配对码" : null;
                    return;
                }

                if (_server != null && _server.IsRunning && _runningPort == port)
                {
                    _error = null; // 已按此端口运行（配对码由 provider 现取，无需重启）
                    return;
                }

                StopLocked();
                var server = new McpLanServer(() => AppSettings.Instance.PairCode);
                server.Start(port);
                _server = server;
                _runningPort = port;
                _error = null;
            }
            catch (Exception ex)
            {
                StopLocked();
                _error = ex.Message; // 典型：端口被占用
            }
        }

        // 状态/错误信息都可能变了，统一通知设置页刷新（幂等刷新无副作用）
        StatusChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>程序退出时调用。</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            StopLocked();
        }
    }

    private static bool StopLocked()
    {
        if (_server == null) return false;
        _server.Stop();
        _server = null;
        return true;
    }
}
