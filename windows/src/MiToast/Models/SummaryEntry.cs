using System;

namespace MiToast.Models;

/// <summary>
/// 一次本地总结的完整记录。SummaryMd 是模型输出的中文总结正文（Markdown），
/// DigestMd 是喂给模型前的结构化数据快照（金额对冲、抽样等规则都在这一步做过），
/// 留档便于核对总结与原始数据是否对得上。随 summaries.json 走 DPAPI 加密落盘。
/// </summary>
public class SummaryEntry
{
    /// <summary>生成时刻（Unix ms）。</summary>
    public long GeneratedAtMs { get; set; }

    /// <summary>统计窗口起点（Unix ms）。</summary>
    public long RangeStartMs { get; set; }

    /// <summary>统计窗口终点（Unix ms）。</summary>
    public long RangeEndMs { get; set; }

    /// <summary>窗口内的通知总条数（去重前）。</summary>
    public int TotalCount { get; set; }

    /// <summary>总结正文（Markdown）。</summary>
    public string SummaryMd { get; set; } = string.Empty;

    /// <summary>喂给模型的原始结构化 digest（Markdown），排查用。</summary>
    public string DigestMd { get; set; } = string.Empty;

    /// <summary>列表/详情展示用的窗口范围文案，如「10-01 09:00 ~ 10-04 09:00」。</summary>
    public string RangeText
    {
        get
        {
            var start = DateTimeOffset.FromUnixTimeMilliseconds(RangeStartMs).ToLocalTime();
            var end = DateTimeOffset.FromUnixTimeMilliseconds(RangeEndMs).ToLocalTime();
            return $"{start:MM-dd HH:mm} ~ {end:MM-dd HH:mm}";
        }
    }

    /// <summary>生成时间文案，如「10-04 10:20 生成」。</summary>
    public string GeneratedText
        => $"{DateTimeOffset.FromUnixTimeMilliseconds(GeneratedAtMs).ToLocalTime():MM-dd HH:mm} 生成";
}
