using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiToast.Models;

namespace MiToast.Services;

/// <summary>
/// 本地总结编排：引擎就绪 → 规则聚合 digest → 本地 llama.cpp 行文 → DPAPI 落盘。
/// 只做手动触发（设置页 / 总结窗口按钮），无人值守的定时总结仍走外部路径（MCP）。
/// </summary>
public static class SummaryService
{
    private const int MaxStoredEntries = 100;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static CancellationTokenSource? _runCts;

    /// <summary>summaries.json 变化（新增/删除）。</summary>
    public static event EventHandler? Changed;

    /// <summary>生成过程的状态文案（下载进度、启动、推理中等），供状态行显示。</summary>
    public static event EventHandler<string>? StatusChanged;

    public static bool IsBusy => Gate.CurrentCount == 0;

    private static string SummariesPath => Path.Combine(AppSettings.ConfigDir, "summaries.json");

    /// <summary>模型行文的系统提示：固化归档摘要约定（结构、行动项、脱敏）。</summary>
    private const string SystemPrompt =
        "你是个人通知总结助手。用户消息是经过程序对账、去重、脱敏后的结构化通知数据。" +
        "据此写一份中文总结，Markdown 格式，要求：\n" +
        "1. 开头 2-3 句总体概况：通知总量、最活跃的应用、这段时间突出的活动。\n" +
        "2. 按类别分小节（## 支付、## 外卖/取餐/物流、## 日程、## 消息、## 其他），每节提炼要点，不要照抄全部数据行。\n" +
        "3. 支付明细、快递/取餐、日程这类条目化数据优先用 Markdown 表格呈现：表头形如 | 时间 | 应用 | 摘要 |，3-4 列、最多 6 行，单元格尽量短；每张表格前保留一句概述。其余类别仍用列表，不要把整份总结都做成表格。\n" +
        "4. 单列一节「## 需要关注/行动」：异常或大额支付、未取的快递/取餐（含取餐码）、即将到来的日程、需要回复的重要私信。没有就写「无」。\n" +
        "5. 支付金额以数据中的对账结果（合计/净额）为准，不要自行加减。\n" +
        "6. 推广、验证码、音乐播放一笔带过或省略。\n" +
        "7. 转述时保护隐私：不逐字引用地址、卡号、验证码，店铺与人名可模糊化。\n" +
        "8. 语言简洁自然，总长度 300-600 字。";

    /// <summary>取消正在进行的生成（按钮再点一次 = 取消）。无任务在跑时是空操作。</summary>
    public static void CancelActiveRun()
    {
        try { _runCts?.Cancel(); } catch { }
    }

    /// <summary>
    /// 执行一次本地总结并入库。抛出 OperationCanceledException 表示被用户取消，
    /// 其他异常（引擎缺失、模型损坏、推理失败）原样上抛，由 UI 显示。
    /// </summary>
    public static async Task<SummaryEntry> RunAsync(int windowHours, CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct))
        {
            throw new InvalidOperationException("已有总结正在生成中");
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _runCts = linked;
            var token = linked.Token;

            Report("正在检查推理引擎…");
            await EngineInstaller.EnsureInstalledAsync(
                (received, total) => Report($"正在下载模型 {received / 1048576} / {total / 1048576} MB"),
                token);

            long end = DateTimeOffset.Now.ToUnixTimeMilliseconds();
            long start = end - (long)windowHours * 3600_000L;

            Report("正在聚合通知数据…");
            var digest = DigestBuilder.Build(HistoryService.Instance.GetAll(), start, end);
            if (digest.TotalCount == 0)
            {
                throw new InvalidOperationException($"最近 {windowHours} 小时内没有通知记录");
            }

            Report("正在启动本地模型（加载约几秒到几十秒）…");
            using var host = new LlamaServerHost(EngineInstaller.ServerExePath, EngineInstaller.ModelPath);
            await host.StartAsync(token);

            Report($"正在生成本地总结（CPU 推理，约 1-4 分钟，数据 {digest.TotalCount} 条）…");
            string summaryMd = await host.ChatAsync(SystemPrompt, digest.Markdown, maxTokens: 1200, temperature: 0.3, token);

            var entry = new SummaryEntry
            {
                GeneratedAtMs = end,
                RangeStartMs = start,
                RangeEndMs = end,
                TotalCount = digest.TotalCount,
                SummaryMd = summaryMd,
                DigestMd = digest.Markdown
            };

            Report("正在保存…");
            var list = LoadSummaries();
            list.Insert(0, entry);
            if (list.Count > MaxStoredEntries)
            {
                list.RemoveRange(MaxStoredEntries, list.Count - MaxStoredEntries);
            }
            SaveSummaries(list);

            Changed?.Invoke(null, EventArgs.Empty);
            return entry;
        }
        finally
        {
            _runCts = null;
            Gate.Release();
        }
    }

    /// <summary>拉起 llama-server 验证引擎与模型可用，返回加载的模型标识。</summary>
    public static async Task<string> TestEngineAsync(CancellationToken ct)
    {
        var state = EngineInstaller.GetState();
        if (state != EngineInstallState.Ready)
        {
            throw new InvalidOperationException(state == EngineInstallState.Downloading
                ? "引擎正在下载中，请等下载完成再测试"
                : $"引擎未就绪（{EngineInstaller.FailReason ?? "尚未下载"}）");
        }

        Report("正在测试引擎（加载模型）…");
        using var host = new LlamaServerHost(EngineInstaller.ServerExePath, EngineInstaller.ModelPath);
        await host.StartAsync(ct);
        var modelId = await host.GetModelIdAsync(ct);
        Report(string.Empty);
        return modelId;
    }

    // ---------- 持久化 ----------

    public static List<SummaryEntry> LoadSummaries()
    {
        try
        {
            string? json = SensitiveStorage.ReadProtectedText(SummariesPath);
            if (json == null) return new List<SummaryEntry>();
            return JsonSerializer.Deserialize<List<SummaryEntry>>(json) ?? new List<SummaryEntry>();
        }
        catch
        {
            return new List<SummaryEntry>();
        }
    }

    /// <summary>按生成时刻删除一条总结记录。</summary>
    public static void Delete(long generatedAtMs)
    {
        var list = LoadSummaries();
        int removed = list.RemoveAll(e => e.GeneratedAtMs == generatedAtMs);
        if (removed == 0) return;
        SaveSummaries(list);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void SaveSummaries(List<SummaryEntry> list)
        => SensitiveStorage.WriteProtectedBytes(SummariesPath, JsonSerializer.SerializeToUtf8Bytes(list));

    private static void Report(string message) => StatusChanged?.Invoke(null, message);
}
