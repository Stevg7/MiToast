using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiToast.Services;

namespace MiToastMcp;

/// <summary>
/// MiToast 历史通知 MCP 服务器宿主（协议与工具实现在共享层 MiToast.Services.McpCore）：
///   默认 stdio 传输——供本机 AI Agent 通过 MCP 协议读取历史通知（MCP 客户端按需拉起）；
///   `--lan [端口]` 局域网模式——同一个 exe 提供 MCP over HTTP 端点 /mcp，跨设备查询，
///   Bearer 配对码校验（传输与鉴权实现在 MiToast.Services.McpLanServer）。
///
/// 直接读取 %AppData%\MiToast\history.json（DPAPI 加密），无需主程序运行。
///
/// 访问控制：stdio 查询不做交互式身份验证（2026-10-01 起，无人值守任务无法响应
/// Windows Hello；本机进程本就能自行 spawn 本 exe，数据文件仍为 DPAPI 加密）。
/// 局域网模式（2026-10-04 起）必须持配对码访问。也可不开本进程，直接在 MiToast
/// 客户端「设置 → 历史记录 → 局域网 MCP 接口」里常驻开启同一服务。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // --lan [端口]：局域网 HTTP 模式（跨设备访问，Bearer 配对码校验）
        if (args.Length > 0 && args[0] is "--lan" or "lan")
        {
            return await RunLanAsync(args);
        }

        try
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // 控制台编码设置失败不影响运行
        }

        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            if (line.Trim().Length == 0) continue;

            string? response = await McpCore.HandleMessageAsync(line);
            if (response == null) continue; // 通知类消息（如 initialized）不回复

            try
            {
                Console.Out.WriteLine(response);
                Console.Out.Flush();
            }
            catch
            {
                return 1; // stdout 关闭（客户端退出），结束进程
            }
        }
        return 0;
    }

    private static async Task<int> RunLanAsync(string[] args)
    {
        int port = McpLanServer.DefaultPort;
        if (args.Length > 1 && int.TryParse(args[1], out int parsed) && parsed is > 0 and < 65536)
        {
            port = parsed;
        }

        string pairCode = AppSettings.Instance.PairCode;
        if (string.IsNullOrWhiteSpace(pairCode))
        {
            Console.Error.WriteLine("未配置配对码，LAN 模式拒绝启动：局域网访问必须带身份校验。" +
                                    "请先在 MiToast 设置 → 连接里设置配对码。");
            return 2;
        }

        var server = new McpLanServer(() => AppSettings.Instance.PairCode, message => Console.WriteLine(message));
        try
        {
            server.Start(port);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"端口 {port} 监听失败（可能被占用）：{ex.Message}");
            return 2;
        }

        Console.WriteLine("MiToast MCP 局域网模式已启动（只读，Bearer 校验 = 配对码）。终端点：");
        foreach (var ip in McpLanServer.LocalIpv4Addresses())
        {
            Console.WriteLine($"  http://{ip}:{port}/mcp");
        }
        Console.WriteLine($"  http://127.0.0.1:{port}/mcp （本机）");
        Console.WriteLine("注意：明文 HTTP，仅在可信局域网内使用；其他设备首次连接时需放行防火墙。Ctrl+C 退出。");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        server.Stop();
        return 0;
    }
}
