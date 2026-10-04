using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MiToast.Services;

/// <summary>引擎安装状态（设置页状态行与按钮文案按此切换）。</summary>
public enum EngineInstallState
{
    NotInstalled,
    Downloading,
    Ready,
    Failed
}

/// <summary>
/// 本地推理引擎（llama.cpp llama-server）与模型的自动下载安装。
/// 引擎：GitHub Release 的 CPU 版 zip（约 19MB，解压取根目录的 exe 与 DLL）；
/// 模型：ModelScope 上 Qwen 官方的 Qwen3-4B-Q4_K_M.gguf（约 2.5GB，单文件，
/// 支持 HTTP Range 断点续传，流式 SHA256 与发布哈希比对）。
/// 全部落到 %AppData%\MiToast\LocalLLM\，不进程序目录、不进 git。
/// </summary>
public static class EngineInstaller
{
    // 资产固定为发布时验证过的版本：引擎 b11381（zip 内文件在根目录）、
    // 模型 Qwen3-4B Q4_K_M 单文件（尺寸与哈希来自 ModelScope 文件清单）
    private const string EngineZipUrl =
        "https://github.com/ggml-org/llama.cpp/releases/download/b11381/llama-b11381-bin-win-cpu-x64.zip";
    private const string ModelUrl =
        "https://modelscope.cn/models/Qwen/Qwen3-4B-GGUF/resolve/master/Qwen3-4B-Q4_K_M.gguf";
    private const string ModelSha256 = "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5";
    private const long ModelSizeBytes = 2497280256;

    public static string EngineDir => System.IO.Path.Combine(AppSettings.ConfigDir, "LocalLLM");
    public static string ServerExePath => System.IO.Path.Combine(EngineDir, "llama-server.exe");
    public static string ModelPath => System.IO.Path.Combine(EngineDir, "Qwen3-4B-Q4_K_M.gguf");

    /// <summary>状态变化（开始/结束下载、失败）时触发，供设置页刷新。</summary>
    public static event EventHandler? StatusChanged;

    private static int _downloading;
    private static string? _failReason;

    public static bool IsDownloading => Volatile.Read(ref _downloading) == 1;

    public static string? FailReason => _failReason;

    public static EngineInstallState GetState()
    {
        if (IsDownloading) return EngineInstallState.Downloading;
        if (File.Exists(ServerExePath) && File.Exists(ModelPath)) return EngineInstallState.Ready;
        if (_failReason != null) return EngineInstallState.Failed;
        return EngineInstallState.NotInstalled;
    }

    public static string DescribeState()
        => GetState() switch
        {
            EngineInstallState.Ready => "已就绪（llama.cpp + Qwen3-4B Q4，约 2.5GB）",
            EngineInstallState.Downloading => "正在下载引擎与模型…",
            EngineInstallState.Failed => $"下载失败：{_failReason}",
            _ => "尚未下载（首次使用需下载约 2.5GB）"
        };

    /// <summary>
    /// 确保引擎与模型就绪；缺失的部分按 引擎 → 模型 顺序下载。
    /// 已就绪时立即返回（不打断正在进行的其他下载，重复进入由信号量挡住）。
    /// </summary>
    public static async Task EnsureInstalledAsync(Action<long, long>? modelProgress, CancellationToken ct)
    {
        if (GetState() == EngineInstallState.Ready) return;
        if (Interlocked.CompareExchange(ref _downloading, 1, 0) != 0)
        {
            throw new InvalidOperationException("引擎正在下载中，请稍候");
        }

        try
        {
            Directory.CreateDirectory(EngineDir);
            if (!File.Exists(ServerExePath))
            {
                StatusChanged?.Invoke(null, EventArgs.Empty);
                await DownloadEngineAsync(ct);
            }
            if (!File.Exists(ModelPath))
            {
                StatusChanged?.Invoke(null, EventArgs.Empty);
                await DownloadModelAsync(modelProgress, ct);
            }
            _failReason = null;
        }
        catch (Exception e)
        {
            _failReason = e.Message;
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _downloading, 0);
            StatusChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>删除模型文件回到未安装态（校验失败/损坏时由内部调用；也供“重新下载”用）。</summary>
    public static void Reset()
    {
        try
        {
            if (File.Exists(ModelPath)) File.Delete(ModelPath);
            if (File.Exists(ModelPath + ".part")) File.Delete(ModelPath + ".part");
        }
        catch
        {
        }
        _failReason = null;
        StatusChanged?.Invoke(null, EventArgs.Empty);
    }

    // ---------- 引擎 zip ----------

    private static async Task DownloadEngineAsync(CancellationToken ct)
    {
        string zipPath = System.IO.Path.Combine(EngineDir, "llama-server.zip");
        string extractDir = System.IO.Path.Combine(EngineDir, "_extract");

        try
        {
            using (var http = CreateHttp())
            {
                await DownloadToFileAsync(http, EngineZipUrl, zipPath, append: false, ct);
            }

            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            ZipFile.ExtractToDirectory(zipPath, extractDir);

            if (!File.Exists(System.IO.Path.Combine(extractDir, "llama-server.exe")))
            {
                throw new FileNotFoundException("引擎压缩包结构不符合预期（根目录无 llama-server.exe）");
            }
            // 只拷 exe 与 DLL，README 之类留在解压目录随清理删除
            foreach (var file in Directory.GetFiles(extractDir))
            {
                var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".exe" or ".dll")
                {
                    File.Copy(file, System.IO.Path.Combine(EngineDir, System.IO.Path.GetFileName(file)), overwrite: true);
                }
            }
        }
        finally
        {
            try
            {
                if (File.Exists(zipPath)) File.Delete(zipPath);
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            }
            catch
            {
            }
        }
    }

    // ---------- 模型 GGUF（断点续传 + SHA256） ----------

    private static async Task DownloadModelAsync(Action<long, long>? progress, CancellationToken ct)
    {
        string partPath = ModelPath + ".part";
        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (existing > ModelSizeBytes)
        {
            File.Delete(partPath);
            existing = 0;
        }

        using var http = CreateHttp();
        using var request = new HttpRequestMessage(HttpMethod.Get, ModelUrl);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        bool resumed = existing > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
        if (existing > 0 && resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // .part 已经下满（上次在收尾前中断）：跳过下载直接走校验
            existing = ModelSizeBytes;
        }
        else
        {
            resp.EnsureSuccessStatusCode();
            if (!resumed) existing = 0;
        }

        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // 已有 .part 即完整文件：整体读一遍喂哈希
            await HashFileAsync(partPath, hash, ct);
        }
        else
        {
            if (resumed)
            {
                // 续传前缀要进哈希：先把盘上已有部分读一遍（SSD 上秒级）
                await HashFileAsync(partPath, hash, ct);
            }

            await using var network = await resp.Content.ReadAsStreamAsync(ct);
            await using var file = new FileStream(partPath, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write);

            var buffer = new byte[1 << 20];
            long received = existing;
            var sw = Stopwatch.StartNew();
            long lastReportMs = 0;
            int read;
            while ((read = await network.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                hash.AppendData(buffer, 0, read);
                received += read;
                if (progress != null && sw.ElapsedMilliseconds - lastReportMs > 500)
                {
                    lastReportMs = sw.ElapsedMilliseconds;
                    progress(received, ModelSizeBytes);
                }
            }
            file.Flush();
            progress?.Invoke(received, ModelSizeBytes);

            if (received != ModelSizeBytes)
            {
                throw new IOException($"模型下载不完整（{received / 1048576}MB / {ModelSizeBytes / 1048576}MB），重试将从断点继续");
            }
        }

        string sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (sha != ModelSha256)
        {
            File.Delete(partPath);
            throw new IOException("模型 SHA256 校验失败（下载损坏），已删除，重试将重新下载");
        }

        File.Move(partPath, ModelPath, overwrite: true);
    }

    private static async Task HashFileAsync(string path, IncrementalHash hash, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await fs.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }
    }

    private static async Task DownloadToFileAsync(HttpClient http, string url, string targetPath, bool append, CancellationToken ct)
    {
        FileMode mode = append ? FileMode.Append : FileMode.Create;
        await using var network = await http.GetStreamAsync(url, ct);
        await using var file = new FileStream(targetPath, mode, FileAccess.Write);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await network.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MiToast/1.0 (local-summary)");
        return http;
    }
}
