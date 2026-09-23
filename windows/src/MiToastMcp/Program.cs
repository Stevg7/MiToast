using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MiToast.Models;
using MiToast.Services;

namespace MiToastMcp;

/// <summary>
/// MiToast 历史通知 MCP 服务器（stdio 传输）：
/// 供 AI Agent 通过 MCP 协议读取本机 MiToast 保存的历史通知并进行总结/分析。
///
/// 直接读取 %AppData%\MiToast\history.json（DPAPI 加密，MiToast 主程序防抖落盘，
/// Agent 每次调用拿最新快照），因此无需主程序运行、也不依赖任何第三方库，进程由
/// MCP 客户端按需拉起。
///
/// 敏感访问控制：查询/统计历史前会弹系统「Windows 安全中心」身份验证
/// （Windows Hello PIN/指纹/人脸，无 Hello 时回退密码验证），验证通过后 2 分钟内
/// 免重复弹窗；取消或验证失败不返回数据。
///
/// 暴露的工具：
///   query_notifications  — 按条件查询历史通知（limit/app/keyword/category/since）
///   notification_stats   — 按时间段统计通知数量分布（按应用与分类，默认最近 24 小时）
///
/// 协议：MCP over stdio（每行一条 JSON-RPC 2.0 消息，初始化握手 + tools/list + tools/call）。
/// </summary>
public static class Program
{
    private const string ServerName = "mitoast-history";
    private const string ServerVersion = "1.0.0";
    private const string ProtocolVersion = "2024-11-05";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string HistoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MiToast", "history.json");

    public static async Task<int> Main()
    {
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

            string? response = await HandleMessageAsync(line);
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

    private static async Task<string?> HandleMessageAsync(string line)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch
        {
            return Error(null, -32700, "Parse error");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Error(null, -32600, "Invalid Request");

            string method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
            bool hasId = root.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null;
            string idJson = hasId ? id.GetRawText() : "null";

            try
            {
                switch (method)
                {
                    case "initialize":
                        return Result(idJson, new
                        {
                            protocolVersion = ProtocolVersion,
                            capabilities = new { tools = new { } },
                            serverInfo = new { name = ServerName, version = ServerVersion }
                        });

                    case "notifications/initialized":
                    case "notifications/cancelled":
                        return null; // 通知，不回复

                    case "ping":
                        return Result(idJson, new { });

                    case "tools/list":
                        return Result(idJson, new { tools = BuildToolList() });

                    case "tools/call":
                        return await HandleToolCallAsync(idJson, root);

                    default:
                        return hasId ? Error(idJson, -32601, $"Method not found: {method}") : null;
                }
            }
            catch (Exception ex)
            {
                return hasId ? Error(idJson, -32603, $"Internal error: {ex.Message}") : null;
            }
        }
    }

    private static object[] BuildToolList()
    {
        return new object[]
        {
            new
            {
                name = "query_notifications",
                description = "查询 MiToast 保存的历史通知。返回按时间倒序的通知列表（含应用名、标题、正文、时间、分类），" +
                              "供 Agent 阅读后总结通知内容。" +
                              "category 支持：chat（即时消息）/music（媒体播放）/payment（支付银行账单）/" +
                              "verification（验证码）/delivery（外卖配送）/pickup（到店取餐）/order（订单物流）/" +
                              "promo（推广推荐）/system（系统设备）/schedule（日程课程）/progress（其它进度）/general（未分类）。" +
                              "分类在通知采集时确定，2026-09 之前入库的旧记录可能只有 general/progress/music。",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["limit"] = new { type = "integer", description = "返回条数，默认 50，最大 500" },
                        ["app"] = new { type = "string", description = "按应用名或包名过滤（包含匹配，忽略大小写）" },
                        ["keyword"] = new { type = "string", description = "按标题/正文关键词过滤（包含匹配，忽略大小写）" },
                        ["category"] = new { type = "string", description = "按分类过滤，取值见工具说明（含匹配，忽略大小写）" },
                        ["since"] = new { type = "integer", description = "仅返回该 Unix 毫秒时间戳之后的通知" }
                    }
                }
            },
            new
            {
                name = "notification_stats",
                description = "统计历史通知的数量分布（按应用、按分类分组），默认统计最近 24 小时，供 Agent 掌握通知概况。",
                inputSchema = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["hours"] = new { type = "integer", description = "统计最近多少小时，默认 24" }
                    }
                }
            }
        };
    }

    private static async Task<string> HandleToolCallAsync(string idJson, JsonElement root)
    {
        var parameters = root.TryGetProperty("params", out var p) ? p : default;
        string name = parameters.ValueKind == JsonValueKind.Object
                      && parameters.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

        var arguments = parameters.ValueKind == JsonValueKind.Object
                        && parameters.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
            ? a : default;

        // 读取历史通知属于敏感操作：与历史页面同一套系统身份验证（Windows Hello PIN 等），
        // 验证通过后 2 分钟内免重复弹窗；取消/失败则拒绝返回数据。
        bool unlockRequired = name is "query_notifications" or "notification_stats";
        if (unlockRequired && !await HistoryLock.EnsureUnlockedAsync())
        {
            return Result(idJson, new
            {
                content = new object[]
                {
                    new
                    {
                        type = "text",
                        text = "身份验证未通过（用户取消或验证失败）。请先完成 Windows 安全中心的身份验证后再重试查询。"
                    }
                },
                isError = false
            });
        }

        string text;
        try
        {
            text = name switch
            {
                "query_notifications" => QueryNotifications(arguments),
                "notification_stats" => NotificationStats(arguments),
                _ => throw new InvalidOperationException($"Unknown tool: {name}")
            };
        }
        catch (Exception ex)
        {
            return Error(idJson, -32602, $"Invalid params: {ex.Message}");
        }

        return Result(idJson, new
        {
            content = new object[]
            {
                new { type = "text", text }
            },
            isError = false
        });
    }

    // ---------- 历史读取与工具实现 ----------

    private static List<NotificationMessage> LoadHistory()
    {
        try
        {
            // 历史文件为 DPAPI 加密存储（与主程序同一套读写逻辑，兼容旧明文文件）
            var text = SensitiveStorage.ReadHistoryText(HistoryPath);
            if (!string.IsNullOrEmpty(text))
            {
                var list = JsonSerializer.Deserialize<List<NotificationMessage>>(text);
                if (list != null) return list;
            }
        }
        catch
        {
            // 文件被主程序写入过程中读到半个文件时忽略，返回空
        }
        return new List<NotificationMessage>();
    }

    private static string QueryNotifications(JsonElement args)
    {
        int limit = GetInt(args, "limit", 50);
        limit = Math.Clamp(limit, 1, 500);
        string app = GetString(args, "app", "");
        string keyword = GetString(args, "keyword", "");
        string category = GetString(args, "category", "");
        long since = GetLong(args, "since", 0);

        var items = LoadHistory()
            .Where(m => m != null)
            .Where(m => since <= 0 || m.Timestamp > since)
            .Where(m => app.Length == 0 || MatchesApp(m, app))
            .Where(m => keyword.Length == 0 ||
                        (m.Title ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        (m.Content ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                        (m.BigText ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Where(m => category.Length == 0 ||
                        string.Equals(m.Category, category, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.Timestamp)
            .Take(limit)
            .Select(m => new
            {
                app = m.AppName.Length > 0 ? m.AppName : m.PackageName,
                package = m.PackageName,
                title = m.BigTitle.Length > 0 ? m.BigTitle : m.Title,
                content = m.BigText.Length > 0 ? m.BigText : m.Content,
                time = FormatTime(m.Timestamp),
                timestamp = m.Timestamp,
                category = m.Category,
                key = m.Key
            })
            .ToList();

        return JsonSerializer.Serialize(new { count = items.Count, notifications = items }, JsonOptions);
    }

    private static string NotificationStats(JsonElement args)
    {
        int hours = GetInt(args, "hours", 24);
        hours = Math.Clamp(hours, 1, 24 * 30);

        long cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)hours * 3600_000;
        var all = LoadHistory().Where(m => m != null).ToList();
        var recent = all.Where(m => m.Timestamp > cutoff).ToList();

        var byApp = recent
            .GroupBy(m => m.AppName.Length > 0 ? m.AppName : m.PackageName)
            .OrderByDescending(g => g.Count())
            .Select(g => new { app = g.Key, count = g.Count() })
            .ToList();

        var byCategory = recent
            .GroupBy(m => string.IsNullOrEmpty(m.Category) ? "general" : m.Category)
            .OrderByDescending(g => g.Count())
            .Select(g => new { category = g.Key, count = g.Count() })
            .ToList();

        var result = new
        {
            hours,
            totalInWindow = recent.Count,
            totalOverall = all.Count,
            earliestInWindow = recent.Count > 0 ? FormatTime(recent.Min(m => m.Timestamp)) : null,
            latestInWindow = recent.Count > 0 ? FormatTime(recent.Max(m => m.Timestamp)) : null,
            apps = byApp,
            categories = byCategory
        };
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static bool MatchesApp(NotificationMessage m, string query)
    {
        return (m.AppName ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (m.PackageName ?? "").Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatTime(long unixMs)
    {
        if (unixMs <= 0) return "";
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch
        {
            return "";
        }
    }

    private static string GetString(JsonElement args, string key, string fallback)
    {
        return args.ValueKind == JsonValueKind.Object &&
               args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;
    }

    private static int GetInt(JsonElement args, string key, int fallback)
    {
        return args.ValueKind == JsonValueKind.Object &&
               args.TryGetProperty(key, out var v) && v.TryGetInt32(out int value)
            ? value
            : fallback;
    }

    private static long GetLong(JsonElement args, string key, long fallback)
    {
        return args.ValueKind == JsonValueKind.Object &&
               args.TryGetProperty(key, out var v) && v.TryGetInt64(out long value)
            ? value
            : fallback;
    }

    // ---------- JSON-RPC 封装 ----------

    private static string Result(string idJson, object result)
        => $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"result\":{JsonSerializer.Serialize(result, JsonOptions)}}}";

    private static string Error(string? idJson, int code, string message)
        => idJson == null
            ? $"{{\"jsonrpc\":\"2.0\",\"error\":{{\"code\":{code},\"message\":\"{Escape(message)}\"}}}}"
            : $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"error\":{{\"code\":{code},\"message\":\"{Escape(message)}\"}}}}";

    private static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
