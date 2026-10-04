using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiToast.Models;

namespace MiToast.Services;

/// <summary>
/// MCP 服务核心（JSON-RPC 分发 + 只读工具），供两个宿主共用：
///   - MiToastMcp.exe 的 stdio 传输（本机 AI Agent，MCP 客户端按需拉起）；
///   - 局域网 HTTP 端点 /mcp（MiToastMcp.exe --lan 或 MiToast 客户端内的开关）。
///
/// 直接读取 %AppData%\MiToast\history.json（DPAPI 加密，MiToast 主程序防抖落盘，
/// 每次调用拿最新快照），无需主程序运行，不依赖任何第三方库。
///
/// 暴露的工具（只读）：query_notifications / notification_stats，见 BuildToolList。
/// 协议：JSON-RPC 2.0，方法 initialize / notifications/* / ping / tools/list / tools/call。
/// </summary>
public static class McpCore
{
    public const string ServerName = "mitoast-history";
    public const string ServerVersion = "1.3.0";
    public const string ProtocolVersion = "2024-11-05";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string HistoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MiToast", "history.json");

    public static async Task<string?> HandleMessageAsync(string line)
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
                        return HandleToolCall(idJson, root);

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

    private static string HandleToolCall(string idJson, JsonElement root)
    {
        var parameters = root.TryGetProperty("params", out var p) ? p : default;
        string name = parameters.ValueKind == JsonValueKind.Object
                      && parameters.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

        var arguments = parameters.ValueKind == JsonValueKind.Object
                        && parameters.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object
            ? a : default;

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

    private static long GetLong(JsonElement args, string key, int fallback)
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

/// <summary>
/// MCP over HTTP 局域网服务：TcpListener 上手写的最小 HTTP/1.1（零依赖、免 URLACL，
/// HttpListener 在非管理员下无法注册任意端口前缀）。只实现 MCP Streamable HTTP 的
/// 非流式子集：POST /mcp 收一条 JSON-RPC、回一份 application/json（协议允许的响应
/// 形态），initialize 时下发 Mcp-Session-Id 但不做强制；GET/SSE 一律 405。
///
/// 安全校验：
///   - Bearer Token 由宿主提供（客户端/独立 exe 均取自设置里的配对码，与手机配对
///     同一套凭据），每次连接现取，改配对码立即生效；常量时间比较；
///   - 鉴权失败延时应答（拖慢在线爆破），单 IP 失败过多进入临时封禁；
///   - 请求行+头部上限 32KB、请求体上限 1MB、收发超时 10s；
///   - 工具面只有两个只读查询，无任何写入能力。
/// 传输为明文 HTTP：定位为家庭/可信局域网内使用（与手机端 ws:// 同一威胁模型）。
/// </summary>
public sealed class McpLanServer : IDisposable
{
    public const int DefaultPort = 18081;

    private const int MaxHeaderBytes = 32 * 1024;
    private const int MaxBodyBytes = 1024 * 1024;
    private const int IoTimeoutMs = 10_000;
    private const int FailureDelayMs = 800;
    private const int MaxFailuresPerWindow = 20;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);

    // OAuth 生命周期：访问令牌 30 天，刷新令牌 90 天且每次使用轮换；均持久化（DPAPI），重启不失效
    private static readonly TimeSpan CodeTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AccessTokenTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan RefreshTokenTtl = TimeSpan.FromDays(90);
    private const int MaxAccessTokens = 200;
    private const int MaxOAuthClients = 100; // 只读服务：注册表防滥用上限，满了挤掉最早一条
    private const string JsonType = "application/json; charset=utf-8";
    private const string HtmlType = "text/html; charset=utf-8";

    private static readonly JsonSerializerOptions McpJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Func<string> _pairCodeProvider;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<string, (long WindowStart, int Count)> _failures = new();

    // OAuth 最小实现的内存态（让支持 OAuth/DCR 的 MCP 客户端无需手工配请求头也能接入；
    // 授权动作 = 在浏览器里输入一次配对码，安全门没有消失只是换了位置）：
    // 注册的客户端回调地址、待交换的授权码（5 分钟一次性）、已签发的令牌组。
    // 令牌组持久化到 %AppData%\MiToast\mcp-tokens.json（DPAPI 加密）：主程序重启不再
    // 作废其他设备的授权，配合刷新令牌实现 30 天访问 + 90 天静默续期。
    private readonly ConcurrentDictionary<string, string[]> _oauthClients = new();
    private readonly ConcurrentDictionary<string, OAuthCode> _oauthCodes = new();
    // 令牌组以刷新令牌为主键；_accessIndex 是访问令牌 → 刷新令牌的反查索引
    private readonly ConcurrentDictionary<string, OAuthTokenSet> _tokenSets = new();
    private readonly ConcurrentDictionary<string, string> _accessIndex = new();

    private sealed class OAuthCode
    {
        public string ClientId = "";
        public string RedirectUri = "";
        public string? CodeChallenge; // PKCE S256；客户端没带就不校验
        public long ExpiresAtMs;
    }

    private sealed class OAuthTokenSet
    {
        public string AccessToken = "";
        public long AccessExpiresAtMs;
        public string RefreshToken = "";
        public long RefreshExpiresAtMs;
    }

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public bool IsRunning { get; private set; }

    /// <param name="pairCodeProvider">每次连接调用，配对码变更即时生效。</param>
    /// <param name="log">可选日志回调（鉴权失败/封禁等），客户端可为空。</param>
    public McpLanServer(Func<string> pairCodeProvider, Action<string>? log = null)
    {
        _pairCodeProvider = pairCodeProvider;
        _log = log;
    }

    /// <summary>启动监听。端口被占用等失败抛 SocketException，由宿主决定如何呈现。</summary>
    public void Start(int port)
    {
        if (IsRunning) return;
        LoadPersistedTokens(); // 恢复重启前的授权（其他设备不用重新走浏览器授权）
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start(); // 占用时抛 SocketException

        _listener = listener;
        _cts = new CancellationTokenSource();
        IsRunning = true;
        _ = AcceptLoopAsync(listener, _cts.Token);
    }

    public void Stop()
    {
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _cts?.Dispose();
        _cts = null;
        _listener = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            _ = HandleClientSafe(client, ct);
        }
    }

    private async Task HandleClientSafe(TcpClient client, CancellationToken ct)
    {
        try
        {
            await HandleClient(client, ct);
        }
        catch
        {
            // 客户端断开/超时/畸形请求：连接级故障不影响服务
        }
        finally
        {
            client.Dispose();
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        string remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        client.ReceiveTimeout = IoTimeoutMs;
        client.SendTimeout = IoTimeoutMs;
        await using var stream = client.GetStream();

        Request? request = await ReadRequestAsync(stream);
        if (request == null)
        {
            await WriteAsync(stream, 400, "Bad Request", """{"error":"malformed request"}""");
            return;
        }

        int queryStart = request.Path.IndexOf('?');
        string path = (queryStart >= 0 ? request.Path[..queryStart] : request.Path).TrimEnd('/');
        string query = queryStart >= 0 ? request.Path[(queryStart + 1)..] : "";
        string method = request.Method.ToUpperInvariant();

        // ---- MCP 端点：Bearer 校验（配对码或 OAuth 签发的访问令牌） ----
        if (path == "/mcp")
        {
            if (!await IsAuthorizedAsync(request, stream, remote, ct)) return;

            string? response = await McpCore.HandleMessageAsync(request.Body);
            if (response == null)
            {
                await WriteAsync(stream, 202, "Accepted", null);
                return;
            }

            (string Key, string Value)? session = null;
            if (request.Body.Contains("\"initialize\"", StringComparison.Ordinal))
            {
                session = ("Mcp-Session-Id", Guid.NewGuid().ToString("N"));
            }
            await WriteAsync(stream, 200, "OK", response,
                session == null ? null : new[] { session.Value });
            return;
        }

        // ---- OAuth 发现 / 注册 / 授权 / 令牌（授权动作 = 浏览器输入一次配对码） ----
        if (path is "/.well-known/oauth-protected-resource" or "/.well-known/oauth-protected-resource/mcp"
            && method == "GET")
        {
            await WriteAsync(stream, 200, "OK", BuildProtectedResourceMetadata(request));
            return;
        }
        if (path is "/.well-known/oauth-authorization-server" or "/.well-known/oauth-authorization-server/mcp"
            && method == "GET")
        {
            await WriteAsync(stream, 200, "OK", BuildAuthorizationServerMetadata(request));
            return;
        }
        if (path == "/register" && method == "POST")
        {
            await HandleRegisterAsync(stream, request);
            return;
        }
        if (path == "/authorize")
        {
            if (method == "GET")
            {
                await WriteAsync(stream, 200, "OK", RenderAuthorizePage(FormDecode(query), error: null),
                    contentType: HtmlType);
            }
            else if (method == "POST")
            {
                await HandleAuthorizeSubmitAsync(stream, request, remote, ct);
            }
            else
            {
                await WriteAsync(stream, 405, "Method Not Allowed", """{"error":"use GET or POST"}""");
            }
            return;
        }
        if (path == "/token" && method == "POST")
        {
            await HandleTokenAsync(stream, request);
            return;
        }
        if (method == "OPTIONS")
        {
            // 浏览器类 MCP 客户端的预检请求
            await WriteAsync(stream, 204, "No Content", null);
            return;
        }

        await WriteAsync(stream, 405, "Method Not Allowed",
            """{"error":"use POST /mcp with JSON-RPC, or see /.well-known/oauth-protected-resource"}""");
    }

    /// <summary>
    /// /mcp 的 Bearer 校验：接受静态配对码（与手机配对同一套凭据）或 OAuth 流程签发的
    /// 访问令牌。失败一律 401 + WWW-Authenticate 指向资源元数据（规范客户端由此进入
    /// 发现→注册→浏览器授权流程，而不是把 DCR 打到 /mcp 上）。
    /// </summary>
    private async Task<bool> IsAuthorizedAsync(Request request, NetworkStream stream, string remote, CancellationToken ct)
    {
        string presented = request.Authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? request.Authorization[7..].Trim()
            : string.Empty;
        bool authorized = false;

        if (presented.Length > 0)
        {
            string pairCode = _pairCodeProvider() ?? "";
            if (pairCode.Length > 0 && CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(pairCode),
                    Encoding.UTF8.GetBytes(presented.ToUpperInvariant())))
            {
                authorized = true;
            }
            else
            {
                // OAuth 签发的访问令牌（随机 64 hex）：反查令牌组核对有效期；
                // 过期不删除——刷新令牌还有效时客户端可静默续期
                if (_accessIndex.TryGetValue(presented, out var refreshKey) &&
                    _tokenSets.TryGetValue(refreshKey, out var set) &&
                    set.AccessExpiresAtMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                {
                    authorized = true;
                }
            }
        }

        if (authorized)
        {
            _failures.TryRemove(remote, out _);
            return true;
        }

        int failures = RecordFailure(remote);
        if (failures > MaxFailuresPerWindow)
        {
            _log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {remote} 鉴权失败过多，临时封禁（第 {failures} 次）");
            await WriteAsync(stream, 403, "Forbidden", """{"error":"too many failures, retry later"}""");
            return false;
        }
        await Task.Delay(FailureDelayMs, ct);
        _log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {remote} 鉴权失败（第 {failures} 次）");
        await WriteAsync(stream, 401, "Unauthorized", """{"error":"invalid bearer token"}""",
            extraHeaders: new[]
            {
                ("WWW-Authenticate",
                    $"Bearer realm=\"MiToast MCP\", resource_metadata=\"{BaseUrl(request)}/.well-known/oauth-protected-resource\"")
            });
        return false;
    }

    // ---------- OAuth 最小实现 ----------

    private string BuildProtectedResourceMetadata(Request request)
    {
        // RFC 9728：resource = 客户端配置的 MCP 服务器 URL；宽松处理，不做严格校验
        return JsonSerializer.Serialize(new
        {
            resource = BaseUrl(request) + "/mcp",
            authorization_servers = new[] { BaseUrl(request) + "/" },
            scopes_supported = new[] { "mcp" },
            bearer_methods_supported = new[] { "header" }
        }, McpJsonOptions);
    }

    private string BuildAuthorizationServerMetadata(Request request)
    {
        // RFC 8414：本服务自身兼任授权服务器；PKCE 只支持 S256
        string baseUrl = BaseUrl(request);
        return JsonSerializer.Serialize(new
        {
            issuer = baseUrl + "/",
            authorization_endpoint = baseUrl + "/authorize",
            token_endpoint = baseUrl + "/token",
            registration_endpoint = baseUrl + "/register",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "none" }
        }, McpJsonOptions);
    }

    /// <summary>RFC 7591 动态客户端注册：登记回调地址，返回 client_id（无密钥的公共客户端）。</summary>
    private async Task HandleRegisterAsync(NetworkStream stream, Request request)
    {
        try
        {
            using var doc = JsonDocument.Parse(request.Body);
            var root = doc.RootElement;

            var redirectUris = root.TryGetProperty("redirect_uris", out var uris) && uris.ValueKind == JsonValueKind.Array
                ? uris.EnumerateArray()
                    .Where(u => u.ValueKind == JsonValueKind.String)
                    .Select(u => u.GetString() ?? "")
                    .Where(s => s.Length > 0)
                    .ToArray()
                : Array.Empty<string>();

            if (_oauthClients.Count >= MaxOAuthClients)
            {
                // 只读服务，注册表是防滥用而非业务数据：满了就挤掉最早一条
                var oldest = _oauthClients.FirstOrDefault().Key;
                if (oldest != null) _oauthClients.TryRemove(oldest, out _);
            }

            string clientId = Guid.NewGuid().ToString("N");
            _oauthClients[clientId] = redirectUris;

            var body = new Dictionary<string, object>
            {
                ["client_id"] = clientId,
                ["client_id_issued_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["redirect_uris"] = redirectUris,
                ["token_endpoint_auth_method"] = "none",
                ["grant_types"] = new[] { "authorization_code" },
                ["response_types"] = new[] { "code" }
            };
            if (root.TryGetProperty("client_name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                body["client_name"] = name.GetString() ?? "";
            }

            await WriteAsync(stream, 201, "Created", JsonSerializer.Serialize(body, McpJsonOptions));
        }
        catch (JsonException)
        {
            await WriteAsync(stream, 400, "Bad Request", """{"error":"invalid_client_metadata"}""");
        }
    }

    /// <summary>授权确认（浏览器表单提交）：核对配对码 → 签发一次性授权码 → 302 回客户端回调地址。</summary>
    private async Task HandleAuthorizeSubmitAsync(NetworkStream stream, Request request, string remote, CancellationToken ct)
    {
        var fields = FormDecode(request.Body);
        string clientId = fields.GetValueOrDefault("client_id") ?? "";
        string redirectUri = fields.GetValueOrDefault("redirect_uri") ?? "";
        string state = fields.GetValueOrDefault("state") ?? "";
        string challenge = fields.GetValueOrDefault("code_challenge") ?? "";
        string method = fields.GetValueOrDefault("code_challenge_method") ?? "";
        string pairInput = fields.GetValueOrDefault("pair_code") ?? "";

        // 回调地址必须与注册时一致（否则就是开放重定向）
        if (!_oauthClients.TryGetValue(clientId, out var uris) || !uris.Contains(redirectUri))
        {
            await WriteAsync(stream, 400, "Bad Request",
                RenderErrorPage("未知客户端或回调地址未注册，请在客户端重新发起连接。"), contentType: HtmlType);
            return;
        }

        string pairCode = _pairCodeProvider() ?? "";
        bool ok = pairCode.Length > 0 && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(pairCode),
            Encoding.UTF8.GetBytes(pairInput.Trim().ToUpperInvariant()));

        if (!ok)
        {
            int failures = RecordFailure(remote);
            if (failures > MaxFailuresPerWindow)
            {
                _log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {remote} 授权页配对码错误过多，临时封禁");
                await WriteAsync(stream, 403, "Forbidden",
                    RenderErrorPage("失败次数过多，请 10 分钟后再试。"), contentType: HtmlType);
                return;
            }
            _log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {remote} 授权页配对码错误（第 {failures} 次）");
            await WriteAsync(stream, 200, "OK",
                RenderAuthorizePage(fields, "配对码不正确，请重新输入。"), contentType: HtmlType);
            return;
        }

        if (challenge.Length > 0 && !string.Equals(method, "S256", StringComparison.Ordinal))
        {
            await WriteAsync(stream, 400, "Bad Request",
                RenderErrorPage("仅支持 PKCE S256。"), contentType: HtmlType);
            return;
        }

        string code = NewToken();
        _oauthCodes[code] = new OAuthCode
        {
            ClientId = clientId,
            RedirectUri = redirectUri,
            CodeChallenge = challenge.Length > 0 ? challenge : null,
            ExpiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)CodeTtl.TotalMilliseconds
        };
        PruneOAuthState();
        _log?.Invoke($"[{DateTime.Now:HH:mm:ss}] {remote} 授权通过（client {clientId[..Math.Min(8, clientId.Length)]}…）");

        string separator = redirectUri.Contains('?') ? "&" : "?";
        await WriteAsync(stream, 302, "Found", null,
            extraHeaders: new[] { ("Location", $"{redirectUri}{separator}code={code}&state={Uri.EscapeDataString(state)}") });
    }

    /// <summary>
    /// 授权码 / 刷新令牌换访问令牌。请求体按 OAuth 标准是表单编码，但兼容 JSON——
    /// 首次接入的客户端很容易在这里栽跟头，而授权码是一次性的，不该被格式错误烧掉：
    /// 只有请求完整且凭据校验失败时才作废授权码，格式无法解析/字段不全时不消耗。
    /// </summary>
    private async Task HandleTokenAsync(NetworkStream stream, Request request)
    {
        var fields = ParseTokenRequestBody(request.Body);
        string grantType = fields.GetValueOrDefault("grant_type") ?? "";
        string code = fields.GetValueOrDefault("code") ?? "";
        string clientId = fields.GetValueOrDefault("client_id") ?? "";
        string redirectUri = fields.GetValueOrDefault("redirect_uri") ?? "";
        string verifier = fields.GetValueOrDefault("code_verifier") ?? "";
        string refreshToken = fields.GetValueOrDefault("refresh_token") ?? "";
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (grantType == "authorization_code")
        {
            // 请求不完整：不消耗授权码，客户端修正格式后可重试
            if (code.Length == 0 || redirectUri.Length == 0 || verifier.Length == 0)
            {
                await WriteAsync(stream, 400, "Bad Request", """{"error":"invalid_request"}""");
                return;
            }
            if (!_oauthCodes.TryGetValue(code, out var entry))
            {
                await WriteAsync(stream, 400, "Bad Request", """{"error":"invalid_grant"}""");
                return;
            }
            bool mismatch = entry.ExpiresAtMs <= now ||
                            (clientId.Length > 0 && clientId != entry.ClientId) ||
                            redirectUri != entry.RedirectUri;
            if (mismatch)
            {
                _oauthCodes.TryRemove(code, out _); // 凭据不符：作废，防重放
                await WriteAsync(stream, 400, "Bad Request", """{"error":"invalid_grant"}""");
                return;
            }
            if (entry.CodeChallenge != null &&
                (verifier.Length == 0 || Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(verifier))) != entry.CodeChallenge))
            {
                _oauthCodes.TryRemove(code, out _); // PKCE 猜测尝试：作废
                await WriteAsync(stream, 400, "Bad Request", """{"error":"invalid_grant"}""");
                return;
            }
            _oauthCodes.TryRemove(code, out _); // 一次性：交换成功即消耗

            var set = IssueTokenSet(now);
            _log?.Invoke($"[{DateTime.Now:HH:mm:ss}] 签发访问令牌（client {entry.ClientId[..Math.Min(8, entry.ClientId.Length)]}…，访问 30 天 + 刷新 90 天）");
            await WriteAsync(stream, 200, "OK", TokenResponse(set));
            return;
        }

        if (grantType == "refresh_token")
        {
            // 轮换：旧刷新令牌与旧访问令牌一并作废，签发新的一对（静默续期，无需人工）
            if (!_tokenSets.TryRemove(refreshToken, out var oldSet))
            {
                await WriteAsync(stream, 400, "Bad Request", """{"error":"invalid_grant"}""");
                return;
            }
            _accessIndex.TryRemove(oldSet.AccessToken, out _);
            if (oldSet.RefreshExpiresAtMs <= now)
            {
                await WriteAsync(stream, 400, "Bad Request", """{"error":"invalid_grant"}""");
                return;
            }

            var set = IssueTokenSet(now);
            await WriteAsync(stream, 200, "OK", TokenResponse(set));
            return;
        }

        await WriteAsync(stream, 400, "Bad Request", """{"error":"unsupported_grant_type"}""");
    }

    private OAuthTokenSet IssueTokenSet(long now)
    {
        var set = new OAuthTokenSet
        {
            AccessToken = NewToken(),
            AccessExpiresAtMs = now + (long)AccessTokenTtl.TotalMilliseconds,
            RefreshToken = NewToken(),
            RefreshExpiresAtMs = now + (long)RefreshTokenTtl.TotalMilliseconds
        };
        _tokenSets[set.RefreshToken] = set;
        _accessIndex[set.AccessToken] = set.RefreshToken;
        SavePersistedTokens();
        return set;
    }

    private string TokenResponse(OAuthTokenSet set) => JsonSerializer.Serialize(new
    {
        access_token = set.AccessToken,
        token_type = "Bearer",
        expires_in = (long)AccessTokenTtl.TotalSeconds,
        refresh_token = set.RefreshToken,
        refresh_expires_in = (long)RefreshTokenTtl.TotalSeconds
    }, McpJsonOptions);

    /// <summary>令牌请求体：OAuth 标准是表单编码，同时兼容 JSON（宽松对待接入期的格式错误）。</summary>
    private static Dictionary<string, string> ParseTokenRequestBody(string body)
    {
        if (!body.TrimStart().StartsWith('{')) return FormDecode(body);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : property.Value.ToString();
            }
        }
        catch
        {
            // JSON 也坏：返回空表，由调用方按“字段不全”处理且不消耗授权码
        }
        return result;
    }

    // ---------- 令牌持久化（DPAPI）：主程序重启后其他设备的授权依然有效 ----------

    private static string TokensPath => Path.Combine(AppSettings.ConfigDir, "mcp-tokens.json");

    private void LoadPersistedTokens()
    {
        try
        {
            var json = SensitiveStorage.ReadProtectedText(TokensPath);
            if (string.IsNullOrEmpty(json)) return;
            using var doc = JsonDocument.Parse(json);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var t in doc.RootElement.GetProperty("sets").EnumerateArray())
            {
                var set = new OAuthTokenSet
                {
                    AccessToken = t.TryGetProperty("a", out var a) ? a.GetString() ?? "" : "",
                    AccessExpiresAtMs = t.TryGetProperty("ae", out var ae) ? ae.GetInt64() : 0,
                    RefreshToken = t.TryGetProperty("r", out var r) ? r.GetString() ?? "" : "",
                    RefreshExpiresAtMs = t.TryGetProperty("re", out var re) ? re.GetInt64() : 0
                };
                if (set.AccessToken.Length == 0 || set.RefreshToken.Length == 0) continue;
                if (set.RefreshExpiresAtMs <= now) continue; // 整组过期的丢弃
                _tokenSets[set.RefreshToken] = set;
                _accessIndex[set.AccessToken] = set.RefreshToken;
            }
        }
        catch
        {
            // 文件损坏/被删：视作空，其他设备重新授权一次即可
        }
    }

    private void SavePersistedTokens()
    {
        PruneTokenSets();
        try
        {
            var sets = _tokenSets.Values.Select(s => new
            {
                a = s.AccessToken,
                ae = s.AccessExpiresAtMs,
                r = s.RefreshToken,
                re = s.RefreshExpiresAtMs
            });
            var json = JsonSerializer.Serialize(new { sets }, McpJsonOptions);
            SensitiveStorage.WriteProtectedBytes(TokensPath, Encoding.UTF8.GetBytes(json));
        }
        catch
        {
            // 落盘失败不影响内存中的令牌，仅重启后需重新授权
        }
    }

    /// <summary>清理整组过期的令牌；数量超限时挤掉刷新令牌最早过期的一批。</summary>
    private void PruneTokenSets()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var kv in _tokenSets.Where(kv => kv.Value.RefreshExpiresAtMs <= now).ToList())
        {
            _tokenSets.TryRemove(kv.Key, out _);
            _accessIndex.TryRemove(kv.Value.AccessToken, out _);
        }
        if (_tokenSets.Count > MaxAccessTokens)
        {
            foreach (var kv in _tokenSets
                         .OrderBy(kv => kv.Value.RefreshExpiresAtMs)
                         .Take(_tokenSets.Count - MaxAccessTokens).ToList())
            {
                _tokenSets.TryRemove(kv.Key, out _);
                _accessIndex.TryRemove(kv.Value.AccessToken, out _);
            }
        }
    }

    /// <summary>清理过期的授权码与令牌组；令牌数量超限时挤掉最早过期的一批。</summary>
    private void PruneOAuthState()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var kv in _oauthCodes.Where(kv => kv.Value.ExpiresAtMs <= now).ToList())
        {
            _oauthCodes.TryRemove(kv.Key, out _);
        }
        PruneTokenSets();
    }

    private static string NewToken()
        => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string BaseUrl(Request request)
    {
        if (!string.IsNullOrEmpty(request.Host))
        {
            return "http://" + request.Host;
        }
        var ip = LocalIpv4Addresses().FirstOrDefault();
        return $"http://{ip ?? "localhost"}:{DefaultPort}";
    }

    private static Dictionary<string, string> FormDecode(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string key = eq < 0 ? pair : pair[..eq];
            string value = eq < 0 ? "" : pair[(eq + 1)..];
            result[System.Net.WebUtility.UrlDecode(key)] = System.Net.WebUtility.UrlDecode(value);
        }
        return result;
    }

    private static string HtmlEscape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>浏览器授权页：同一个表单既用于 GET 首次展示，也用于 POST 失败后带错误重渲染。</summary>
    private static string RenderAuthorizePage(Dictionary<string, string> fields, string? error)
    {
        static string Hidden(string name, string value) =>
            $"<input type=\"hidden\" name=\"{name}\" value=\"{HtmlEscape(value)}\">";

        string errorBlock = string.IsNullOrEmpty(error)
            ? ""
            : $"<div class=\"err\">{HtmlEscape(error)}</div>";
        return """
            <!doctype html><html lang="zh"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>MiToast 授权</title><style>
            body{font-family:system-ui,-apple-system,"Segoe UI",sans-serif;background:#f7f7f7;margin:0;
                 display:flex;justify-content:center;padding-top:10vh}
            main{background:#fff;border-radius:16px;padding:28px;max-width:360px;width:88%;
                 box-shadow:0 4px 24px rgba(0,0,0,.08);height:fit-content}
            h1{font-size:18px;margin:0 0 8px}p{font-size:13px;color:#666;margin:0 0 16px;line-height:1.6}
            input[type=password]{width:100%;padding:12px;font-size:16px;border:1px solid #ddd;
                 border-radius:10px;box-sizing:border-box;text-transform:uppercase;letter-spacing:2px}
            button{width:100%;margin-top:14px;padding:12px;background:#3482ff;color:#fff;border:0;
                 border-radius:10px;font-size:15px;cursor:pointer}
            .err{color:#e94634;font-size:13px;margin-top:12px}
            </style></head><body><main>
            <h1>MiToast 授权请求</h1>
            <p>客户端请求访问历史通知的<b>只读查询</b>接口。请输入电脑端 MiToast 的配对码完成授权（可在 MiToast 设置 → 连接里查看）。</p>
            <form method="post" action="/authorize">
            """ + Hidden("client_id", fields.GetValueOrDefault("client_id") ?? "")
              + Hidden("redirect_uri", fields.GetValueOrDefault("redirect_uri") ?? "")
              + Hidden("state", fields.GetValueOrDefault("state") ?? "")
              + Hidden("code_challenge", fields.GetValueOrDefault("code_challenge") ?? "")
              + Hidden("code_challenge_method", fields.GetValueOrDefault("code_challenge_method") ?? "")
              + """
            <input type="password" name="pair_code" placeholder="配对码" required autofocus>
            <button type="submit">授权</button>
            """ + errorBlock + "</form></main></body></html>";
    }

    private static string RenderErrorPage(string message)
    {
        string escaped = HtmlEscape(message);
        return """
            <!doctype html><html lang="zh"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>MiToast 授权失败</title></head>
            <body style="font-family:system-ui,sans-serif;background:#f7f7f7;display:flex;justify-content:center;padding-top:15vh">
            <div style="background:#fff;border-radius:16px;padding:28px;max-width:360px;box-shadow:0 4px 24px rgba(0,0,0,.08)">
            <h2 style="margin:0 0 10px">授权失败</h2>
            <p style="color:#666;font-size:14px">
            """ + escaped + """
            </p></div></body></html>
            """;
    }

    private int RecordFailure(string remote)
    {
        long now = Environment.TickCount64;
        var entry = _failures.AddOrUpdate(
            remote,
            _ => (now, 1),
            (_, e) => now - e.WindowStart <= FailureWindow.TotalMilliseconds ? (e.WindowStart, e.Count + 1) : (now, 1));
        return entry.Count;
    }

    // ---------- 最小 HTTP/1.1 读取 ----------

    private sealed record Request(string Method, string Path, string Host, string Authorization, string Body);

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];
        int headerEnd = -1;
        byte[] all = Array.Empty<byte>();

        // 读到头部结束（\r\n\r\n）为止，头部总量超限即拒绝
        while (headerEnd < 0)
        {
            int read = await stream.ReadAsync(chunk);
            if (read == 0) return null;
            buffer.Write(chunk, 0, read);
            all = buffer.ToArray();
            if (all.Length > MaxHeaderBytes) return null;
            headerEnd = FindHeaderEnd(all);
        }

        string head = Encoding.ASCII.GetString(all, 0, headerEnd);
        string[] lines = head.Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        int contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out int len))
        {
            contentLength = Math.Max(0, len);
        }
        if (contentLength > MaxBodyBytes) return null;

        // 补读正文（头部之后可能已带了一部分）
        int bodyStart = headerEnd + 4;
        using var body = new MemoryStream();
        if (all.Length > bodyStart)
        {
            body.Write(all, bodyStart, all.Length - bodyStart);
        }
        while (body.Length < contentLength)
        {
            int read = await stream.ReadAsync(chunk);
            if (read == 0) break;
            body.Write(chunk, 0, read);
            if (body.Length > MaxBodyBytes) return null;
        }

        return new Request(
            requestLine[0],
            requestLine[1],
            headers.TryGetValue("Host", out var host) ? host : "",
            headers.TryGetValue("Authorization", out var auth) ? auth : "",
            Encoding.UTF8.GetString(body.ToArray()));
    }

    private static int FindHeaderEnd(byte[] data)
    {
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
            {
                return i;
            }
        }
        return -1;
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string reason,
        string? body, (string Key, string Value)[]? extraHeaders = null, string contentType = JsonType)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        head.Append("Connection: close\r\n");
        // 浏览器类 MCP 客户端（如 Web 版 Inspector）需要 CORS 才能直连；局域网只读接口放开无碍
        head.Append("Access-Control-Allow-Origin: *\r\n");
        head.Append("Access-Control-Allow-Headers: Authorization, Content-Type, Mcp-Session-Id\r\n");
        head.Append("Access-Control-Expose-Headers: Mcp-Session-Id, WWW-Authenticate\r\n");
        if (extraHeaders != null)
        {
            foreach (var (key, value) in extraHeaders)
            {
                head.Append(key).Append(": ").Append(value).Append("\r\n");
            }
        }
        byte[] bodyBytes = body != null ? Encoding.UTF8.GetBytes(body) : Array.Empty<byte>();
        if (status != 204) // 204 不允许带 Content-Type/Content-Length
        {
            head.Append("Content-Type: ").Append(contentType).Append("\r\n");
            head.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        }
        head.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
        if (bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes);
        }
        await stream.FlushAsync();
    }

    /// <summary>本机所有非回环 IPv4（供宿主展示可用端点）。</summary>
    public static IReadOnlyList<string> LocalIpv4Addresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .Distinct()
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
