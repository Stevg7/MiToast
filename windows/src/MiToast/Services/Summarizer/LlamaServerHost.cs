using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MiToast.Services;

/// <summary>
/// llama.cpp llama-server 子进程的封装。每次总结冷启动一个实例（3 天一跑的批处理
/// 不在乎几秒模型加载，换来的是零常驻占用），流程：挑空闲端口 → 拉起进程 →
/// 轮询 /health 就绪 → POST /v1/chat/completions → 用完杀掉整个进程树。
/// 纯 CPU 推理（-ngl 0）；Qwen3 是混合思考模型，启动带 --jinja 并在请求里关掉
/// 思考模式，避免输出被 &lt;think&gt; 段占满。
/// </summary>
public sealed class LlamaServerHost : IDisposable
{
    private const int HealthTimeoutMs = 90_000;
    private const int ChatTimeoutMinutes = 15;

    private static readonly Regex ThinkingBlockRegex = new(@"<think>[\s\S]*?</think>", RegexOptions.Compiled);

    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly StringBuilder _stderrTail = new();
    private Process? _process;

    public LlamaServerHost(string serverExePath, string modelPath, int contextSize = 12288)
    {
        ServerExePath = serverExePath;
        ModelPath = modelPath;
        ContextSize = contextSize;
    }

    private string ServerExePath { get; }
    private string ModelPath { get; }
    private int ContextSize { get; }
    private int Port { get; set; }

    /// <summary>
    /// 启动 llama-server 并等待模型加载完成（/health 返回 200）。
    /// 进程立即退出（模型损坏、端口被占等）时抛出带 stderr 摘要的异常。
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        Port = GetFreePort();
        var psi = new ProcessStartInfo
        {
            FileName = ServerExePath,
            // 引擎与模型路径来自本程序管理的固定目录，可能含空格，统一加引号
            Arguments = $"-m \"{ModelPath}\" --host 127.0.0.1 --port {Port} -c {ContextSize} -ngl 0 --jinja --no-webui",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        _process = Process.Start(psi);
        if (_process == null) throw new InvalidOperationException("llama-server 进程启动失败");

        // stderr 收尾 4KB，进程异常退出时作为错误上下文
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (_stderrTail)
            {
                if (_stderrTail.Length < 4096) _stderrTail.AppendLine(e.Data);
            }
        };
        _process.BeginErrorReadLine();

        // stdout 必须持续排走：llama.cpp 的日志默认写 stdout，重定向后没人读的话
        // 管道缓冲（约 4KB）会被塞满，子进程阻塞在写日志上，/health 永远起不来
        _process.OutputDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();

        long deadline = Environment.TickCount64 + HealthTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (_process.HasExited)
            {
                throw new IOException(
                    $"llama-server 启动后立即退出（exit {_process.ExitCode}）：{ReadStderrTail()}");
            }

            try
            {
                using var resp = await _http.GetAsync($"http://127.0.0.1:{Port}/health", ct);
                if (resp.IsSuccessStatusCode) return; // 加载中返回 503，就绪才是 200
            }
            catch (Exception e) when (e is HttpRequestException or HttpIOException && !ct.IsCancellationRequested)
            {
                // 服务还没监听，继续等
            }

            await Task.Delay(400, ct);
        }

        throw new TimeoutException($"等待 llama-server 就绪超时（{HealthTimeoutMs / 1000}s）：{ReadStderrTail()}");
    }

    /// <summary>发送 OpenAI 兼容的 chat completion 请求，返回模型输出文本。</summary>
    public async Task<string> ChatAsync(string systemPrompt, string userPrompt, int maxTokens, double temperature, CancellationToken ct)
    {
        if (_process == null || _process.HasExited)
        {
            throw new InvalidOperationException("llama-server 未在运行");
        }

        var body = new
        {
            model = "local",
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature,
            max_tokens = maxTokens,
            stream = false,
            // Qwen3 混合思考模型：关闭思考模式（需要启动参数 --jinja 生效）
            chat_template_kwargs = new { enable_thinking = false }
        };

        // CPU 推理可能要几分钟：请求超时给足，用户取消走外层 ct
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(ChatTimeoutMinutes));

        string responseText;
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"http://127.0.0.1:{Port}/v1/chat/completions", content, timeoutCts.Token);
            responseText = await resp.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                throw new IOException($"推理请求失败（{(int)resp.StatusCode}）：{Truncate(responseText, 300)}");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"推理超时（超过 {ChatTimeoutMinutes} 分钟未完成），可重试或换更小的模型");
        }

        using var doc = JsonDocument.Parse(responseText);
        string output = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;

        output = StripThinking(output);
        if (output.Trim().Length == 0)
        {
            throw new IOException("模型返回了空内容（可能是 max_tokens 被思考段耗尽）");
        }
        return output.Trim();
    }

    /// <summary>读取已加载的模型标识（GET /v1/models），用于「测试引擎」回显。</summary>
    public async Task<string> GetModelIdAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"http://127.0.0.1:{Port}/v1/models", ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("data")[0].GetProperty("id").GetString() ?? "unknown";
    }

    /// <summary>防御性剥离思考段：正常关闭思考模式时不会有，残留时兜底。</summary>
    private static string StripThinking(string text)
    {
        var stripped = ThinkingBlockRegex.Replace(text, string.Empty);
        int open = stripped.IndexOf("<think>", StringComparison.Ordinal);
        if (open >= 0) stripped = stripped[..open]; // 未闭合的思考段：后面不会有正文
        return stripped;
    }

    private string ReadStderrTail()
    {
        lock (_stderrTail)
        {
            var tail = _stderrTail.ToString().Trim();
            return tail.Length > 0 ? Truncate(tail, 500) : "（无 stderr 输出）";
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public void Dispose()
    {
        var process = _process;
        _process = null;
        try
        {
            if (process != null)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
            }
        }
        catch
        {
            // 进程可能已自行退出
        }
        _http.Dispose();
    }
}
