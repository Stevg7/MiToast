using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MiToast.Models;

namespace MiToast.Services;

/// <summary>Digest 聚合结果：结构化 Markdown 与窗口内通知总数。</summary>
public sealed class DigestResult
{
    public string Markdown { get; init; } = string.Empty;
    public int TotalCount { get; init; }
}

/// <summary>
/// 把窗口内的历史通知聚合成结构化中文 digest（Markdown）。
/// 设计原则：算术与关联（金额对冲、跨渠道去重、计数、抽样、脱敏）全部在这层用规则做完，
/// 模型只负责把这份确定性的数据行文成总结——小模型做不了从原始通知里自己对账。
/// 隐私约定与归档摘要一致：验证码绝不进 digest（只计数）、完整地址截断、
/// 长数字单号只留尾 4 位、手机号打码；取餐码保留（行动项需要）。
/// </summary>
public static class DigestBuilder
{
    // 金额：¥32.50 / ￥1,234.5 / 人民币1234 / 32.50元 三种写法
    private static readonly Regex AmountRegex = new(
        @"(?:[¥￥]\s*([0-9][0-9,]*(?:\.[0-9]+)?)|人民币\s*([0-9][0-9,]*(?:\.[0-9]+)?)|([0-9][0-9,]*(?:\.[0-9]+)?)\s*元)");

    private static readonly Regex PhoneRegex = new(@"1[3-9][0-9]{9}");
    private static readonly Regex LongDigitsRegex = new(@"[0-9]{9,}");
    private static readonly Regex AddressRegex = new(@"(收货地址|详细地址|地址|住址)[：:]?\s*[^\s，。,；;]{3,}");

    public static DigestResult Build(IReadOnlyList<NotificationMessage> all, long rangeStartMs, long rangeEndMs)
    {
        // 历史列表是最新在前；聚合与展示统一转成时间正序
        var items = all
            .Where(m => m.Timestamp > 0 && m.Timestamp >= rangeStartMs && m.Timestamp <= rangeEndMs)
            .OrderBy(m => m.Timestamp)
            .ToList();

        if (items.Count == 0) return new DigestResult { Markdown = string.Empty, TotalCount = 0 };

        var md = new StringBuilder();
        var start = DateTimeOffset.FromUnixTimeMilliseconds(rangeStartMs).ToLocalTime();
        var end = DateTimeOffset.FromUnixTimeMilliseconds(rangeEndMs).ToLocalTime();
        md.AppendLine($"# 通知数据（{start:MM-dd HH:mm} ~ {end:MM-dd HH:mm}，共 {items.Count} 条）");
        md.AppendLine($"分类计数：{string.Join("、", items.GroupBy(CategoryOf).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"))}");
        md.AppendLine();

        BuildPaymentSection(md, items);
        BuildDeliverySection(md, items);
        BuildScheduleSection(md, items);
        BuildChatSection(md, items);
        BuildSampledSection(md, items, "system", "系统");
        BuildSampledSection(md, items, "general", "未分类");
        BuildCountOnlySection(md, items, "verification", "验证码", "——验证码内容一律不入档");
        BuildCountOnlySection(md, items, "promo", "推广推荐", "——一笔带过即可");
        BuildCountOnlySection(md, items, "music", "音乐播放", "——无行动价值");
        BuildCountOnlySection(md, items, "progress", "进度", "——无行动价值");

        return new DigestResult { Markdown = md.ToString().TrimEnd(), TotalCount = items.Count };
    }

    /// <summary>分类英文值 → 中文标签。取值需与 HistoryItem、MCP 工具说明保持同步。</summary>
    public static string CategoryLabel(string category) => category switch
    {
        "delivery" => "外卖配送",
        "pickup" => "到店取餐",
        "order" => "订单物流",
        "chat" => "消息",
        "music" => "音乐播放",
        "payment" => "支付",
        "verification" => "验证码",
        "promo" => "推广推荐",
        "system" => "系统",
        "schedule" => "日程",
        "progress" => "进度",
        _ => "未分类"
    };

    private static string CategoryOf(NotificationMessage m) => string.IsNullOrEmpty(m.Category) ? "general" : m.Category;

    // ---------- 支付：提金额、分方向、跨渠道去重、对账 ----------

    private sealed class PaymentRecord
    {
        public long TimeMs;
        public string App = "";
        public decimal Amount;
        public string Direction = "";
        public string Note = "";
    }

    private static void BuildPaymentSection(StringBuilder md, List<NotificationMessage> items)
    {
        var payments = items.Where(m => m.Category == "payment").ToList();
        if (payments.Count == 0) return;

        var records = new List<PaymentRecord>();
        foreach (var msg in payments)
        {
            string body = CleanBody(msg);
            var amountMatch = AmountRegex.Match(body);
            if (!amountMatch.Success) continue; // 无金额（余额播报等）不进逐笔对账，仍计入总条数

            var amountText = FirstNonEmpty(amountMatch.Groups[1].Value, amountMatch.Groups[2].Value, amountMatch.Groups[3].Value);
            if (!decimal.TryParse(amountText.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
                continue;

            // 方向关键词：先判退款，再收入，最后支取（顺序决定歧义归属）
            string direction =
                ContainsAny(body, "退款", "退回", "退货", "赔付") ? "退款" :
                ContainsAny(body, "收款", "入账", "转入", "到账", "工资", "红包", "返现", "收入") ? "收入" :
                ContainsAny(body, "支出", "付款", "消费", "扣款", "支付", "转账", "充值", "缴纳", "缴费") ? "支出" :
                "";
            if (direction.Length == 0) continue;

            records.Add(new PaymentRecord
            {
                TimeMs = msg.Timestamp,
                App = AppOf(msg),
                Amount = amount,
                Direction = direction,
                Note = MaskSensitive(Truncate(body, 30))
            });
        }

        md.AppendLine($"## 支付（共 {payments.Count} 条，可对账 {records.Count} 笔）");

        // 同一笔交易常被银行短信、云闪付、支付宝各报一次：金额相同+方向相同+3 分钟内合并为一笔
        var merged = new List<PaymentRecord>();
        foreach (var r in records)
        {
            var dup = merged.FirstOrDefault(x => x.Direction == r.Direction && x.Amount == r.Amount &&
                                                Math.Abs(x.TimeMs - r.TimeMs) <= 3 * 60_000);
            if (dup != null && !dup.App.Contains(r.App, StringComparison.Ordinal))
            {
                dup.App = $"{dup.App}/{r.App}";
            }
            else
            {
                merged.Add(r);
            }
        }

        if (merged.Count > 0)
        {
            decimal expense = Sum(merged, "支出");
            decimal refund = Sum(merged, "退款");
            decimal income = Sum(merged, "收入");
            // 净额按符号给方向：负数时「净收入」比「净支出 ¥-256.5」可读
            decimal net = expense - refund - income;
            md.AppendLine($"对账后：支出合计 {Fmt(expense)}、退款 {Fmt(refund)}、收入 {Fmt(income)}，" +
                          (net >= 0 ? $"净支出 {Fmt(net)}" : $"净收入 {Fmt(-net)}"));
            foreach (var r in merged)
            {
                md.AppendLine($"- {FmtTime(r.TimeMs)} [{r.App}] {r.Direction} {Fmt(r.Amount)} {r.Note}");
            }
            md.AppendLine("注：同一笔交易可能被多个渠道重复记录，已按金额+时间邻近合并。");
        }
        else
        {
            md.AppendLine("没有可对账的交易（金额或方向关键词未识别到）。");
        }
        md.AppendLine();
    }

    private static decimal Sum(IEnumerable<PaymentRecord> records, string direction)
        => records.Where(r => r.Direction == direction).Sum(r => r.Amount);

    private static string Fmt(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture).Insert(0, "¥");

    // ---------- 外卖/取餐/订单物流：逐条列举，取餐码保留 ----------

    private static void BuildDeliverySection(StringBuilder md, List<NotificationMessage> items)
    {
        var list = items.Where(m => m.Category is "delivery" or "pickup" or "order").ToList();
        if (list.Count == 0) return;

        md.AppendLine($"## 外卖/取餐/订单物流（共 {list.Count} 条）");
        foreach (var msg in list)
        {
            string body = MaskSensitive(Truncate(CleanBody(msg), 50));
            string line = $"- {FmtTime(msg.Timestamp)} [{AppOf(msg)}] {body}";
            // 取餐码是行动项的关键信息，单独确保在行内（正则原文提取，不受脱敏影响）
            var code = NotificationText.ExtractPickupCode(msg);
            if (code != null && !body.Contains(code, StringComparison.Ordinal))
            {
                line += $"，取餐码 {code}";
            }
            md.AppendLine(line);
        }
        md.AppendLine();
    }

    // ---------- 日程 ----------

    private static void BuildScheduleSection(StringBuilder md, List<NotificationMessage> items)
    {
        var list = items.Where(m => m.Category == "schedule").ToList();
        if (list.Count == 0) return;

        md.AppendLine($"## 日程提醒（共 {list.Count} 条）");
        foreach (var msg in list)
        {
            md.AppendLine($"- {FmtTime(msg.Timestamp)} [{AppOf(msg)}] {MaskSensitive(Truncate(CleanBody(msg), 50))}");
        }
        md.AppendLine();
    }

    // ---------- 消息：按应用计数 + 抽样 ----------

    private static void BuildChatSection(StringBuilder md, List<NotificationMessage> items)
    {
        var chats = items.Where(m => m.Category == "chat").ToList();
        if (chats.Count == 0) return;

        var byApp = chats.GroupBy(AppOf).OrderByDescending(g => g.Count()).ToList();
        md.AppendLine($"## 消息（{string.Join("、", byApp.Select(g => $"{g.Key} {g.Count()}"))}）");
        md.AppendLine("每应用抽样最多 6 条（新→旧），仅代表性内容：");
        foreach (var group in byApp.Take(6))
        {
            foreach (var msg in group.Take(6))
            {
                string title = msg.Title.Length > 0 ? msg.Title : msg.BigTitle;
                string content = FirstNonEmpty(msg.BigText, msg.Content);
                if (content.Length > 0 && content != title)
                {
                    md.AppendLine($"- [{group.Key}] {MaskSensitive(Truncate($"{title}：{content}", 60))}");
                }
                else
                {
                    md.AppendLine($"- [{group.Key}] {MaskSensitive(Truncate(title, 60))}");
                }
            }
        }
        md.AppendLine();
    }

    // ---------- 系统/未分类：计数 + 抽样（跨类关联的原料可能藏在这里） ----------

    private static void BuildSampledSection(StringBuilder md, List<NotificationMessage> items, string category, string label)
    {
        var list = items.Where(m => CategoryOf(m) == category).ToList();
        if (list.Count == 0) return;

        md.AppendLine($"## {label}（共 {list.Count} 条，抽样）");
        foreach (var msg in list.GroupBy(AppOf).OrderByDescending(g => g.Count()).Take(4).SelectMany(g => g.Take(5)))
        {
            md.AppendLine($"- {FmtTime(msg.Timestamp)} [{AppOf(msg)}] {MaskSensitive(Truncate(CleanBody(msg), 50))}");
        }
        md.AppendLine();
    }

    // ---------- 只计数的类别 ----------

    private static void BuildCountOnlySection(StringBuilder md, List<NotificationMessage> items,
        string category, string label, string suffix)
    {
        var list = items.Where(m => CategoryOf(m) == category).ToList();
        if (list.Count == 0) return;

        var topApps = string.Join("、", list.GroupBy(AppOf).OrderByDescending(g => g.Count()).Take(3)
            .Select(g => $"{g.Key}({g.Count()})"));
        md.AppendLine($"## {label}（共 {list.Count} 条，主要来源：{topApps}）{suffix}");
        md.AppendLine();
    }

    // ---------- 工具 ----------

    private static string AppOf(NotificationMessage m) => m.AppName.Length > 0 ? m.AppName : m.PackageName;

    /// <summary>
    /// 标题+正文合并成单行（方向关键词、金额可能散落在标题或正文任一处，
    /// 如标题「退款通知」+ 正文「已退回 32.50 元」，只取其一会漏）。
    /// </summary>
    private static string CleanBody(NotificationMessage m)
    {
        string main = FirstNonEmpty(m.BigText, m.Content, m.Ticker);
        string title = m.BigTitle.Length > 0 ? m.BigTitle : m.Title;
        if (main.Length == 0) return title.Replace("\r", " ").Replace("\n", " ").Trim();
        if (title.Length == 0 || main.Contains(title, StringComparison.Ordinal)) return main.Replace("\r", " ").Replace("\n", " ").Trim();
        return $"{title} {main}".Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static bool ContainsAny(string text, params string[] keywords)
        => keywords.Any(text.Contains);

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => v.Length > 0) ?? string.Empty;

    private static string FmtTime(long timeMs)
        => DateTimeOffset.FromUnixTimeMilliseconds(timeMs).ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Truncate(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    /// <summary>脱敏：手机号打码、9 位以上连续数字（单号/卡号）只留尾 4、地址整体截断。</summary>
    private static string MaskSensitive(string text)
    {
        text = PhoneRegex.Replace(text, m => m.Value[..3] + "****" + m.Value[^2..]);
        text = LongDigitsRegex.Replace(text, m => "…" + m.Value[^4..]);
        text = AddressRegex.Replace(text, "地址（略）");
        return text;
    }
}
