using System;
using System.Text.RegularExpressions;
using MiToast.Models;

namespace MiToast.Services;

/// <summary>
/// 通知文本的确定性提取：验证码、取餐码。原是卡片 UI 的私有逻辑，
/// 历史列表与本地总结的 digest 聚合也要用，抽到 Services 共用一处。
/// </summary>
public static class NotificationText
{
    /// <summary>
    /// 从通知文本中提取短信验证码（4-6 位数字）。
    /// 支持中文“验证码/校验码/动态码”与英文 verification/security code。
    /// </summary>
    public static string? ExtractVerificationCode(NotificationMessage msg)
    {
        string combined = $"{msg.Title} {msg.Content} {msg.BigText} {msg.HintText} {msg.Ticker}";
        if (string.IsNullOrWhiteSpace(combined)) return null;

        var directPatterns = new[]
        {
            new Regex(@"验证码[：:是\s]*([0-9]{4,6})"),
            new Regex(@"校验码[：:是\s]*([0-9]{4,6})"),
            new Regex(@"动态码[：:是\s]*([0-9]{4,6})"),
            new Regex(@"确认码[：:是\s]*([0-9]{4,6})"),
            new Regex(@"激活码[：:是\s]*([0-9]{4,6})"),
            new Regex(@"verification\s*code[^0-9]{0,20}?([0-9]{4,6})", RegexOptions.IgnoreCase),
            new Regex(@"security\s*code[^0-9]{0,20}?([0-9]{4,6})", RegexOptions.IgnoreCase),
            new Regex(@"code\s*(?:is|:)?\s*([0-9]{4,6})", RegexOptions.IgnoreCase)
        };

        foreach (var pattern in directPatterns)
        {
            var m = pattern.Match(combined);
            if (m.Success && m.Groups[1].Value.Length > 0)
                return m.Groups[1].Value;
        }

        // 关键词兜底：文本中出现验证码相关字样时，取第一个 4-6 位独立数字串
        string[] keywords = { "验证码", "校验码", "动态码", "确认码", "激活码", "verification", "security code" };
        if (Array.Exists(keywords, k => combined.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            var m = Regex.Match(combined, @"(?<![0-9])([0-9]{4,6})(?![0-9])");
            if (m.Success) return m.Groups[1].Value;
        }

        return null;
    }

    /// <summary>从取餐/取货/快递柜类通知中提取取餐码（柜号）。</summary>
    public static string? ExtractPickupCode(NotificationMessage msg)
    {
        string labelToSearch = msg.Progress?.Label ?? "";
        string combined = $"{msg.Title} {msg.Content} {msg.BigText} {msg.HintText} {labelToSearch}";

        var patterns = new[]
        {
            new Regex(@"取餐码[：: ]*([A-Za-z0-9\-]+)"),
            new Regex(@"取餐号[：: ]*([A-Za-z0-9\-]+)"),
            new Regex(@"取餐柜[：: ]*([A-Za-z0-9\-]+)"),
            new Regex(@"取货码[：: ]*([A-Za-z0-9\-]+)"),
            new Regex(@"提货码[：: ]*([A-Za-z0-9\-]+)"),
            new Regex(@"柜号[：: ]*([A-Za-z0-9\-]+)"),
            new Regex(@"码号[：: ]*([A-Za-z0-9\-]+)")
        };

        foreach (var pattern in patterns)
        {
            var m = pattern.Match(combined);
            if (m.Success && m.Groups[1].Value.Length > 0)
                return m.Groups[1].Value;
        }
        return null;
    }
}
