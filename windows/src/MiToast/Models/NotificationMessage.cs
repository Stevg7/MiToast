using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MiToast.Models;

public class NotificationProgress
{
    [JsonPropertyName("current")]
    public int Current { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}

public class MediaAction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>原始 action 文案（用于推断收藏/歌词开关当前状态）</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;
}

public class NotificationMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "notification";

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("packageName")]
    public string PackageName { get; set; } = string.Empty;

    [JsonPropertyName("appName")]
    public string AppName { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("bigTitle")]
    public string BigTitle { get; set; } = string.Empty;

    [JsonPropertyName("bigText")]
    public string BigText { get; set; } = string.Empty;

    [JsonPropertyName("subText")]
    public string SubText { get; set; } = string.Empty;

    [JsonPropertyName("ticker")]
    public string Ticker { get; set; } = string.Empty;

    [JsonPropertyName("hintTitle")]
    public string HintTitle { get; set; } = string.Empty;

    [JsonPropertyName("hintText")]
    public string HintText { get; set; } = string.Empty;

    [JsonPropertyName("iconBase64")]
    public string IconBase64 { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    [JsonPropertyName("category")]
    public string Category { get; set; } = "general";

    [JsonPropertyName("isOngoing")]
    public bool IsOngoing { get; set; }

    [JsonPropertyName("groupKey")]
    public string GroupKey { get; set; } = string.Empty;

    [JsonPropertyName("peopleCount")]
    public int PeopleCount { get; set; }

    [JsonPropertyName("progress")]
    public NotificationProgress? Progress { get; set; }

    [JsonPropertyName("mediaActions")]
    public List<MediaAction>? MediaActions { get; set; }

    [JsonPropertyName("mediaIsPlaying")]
    public bool MediaIsPlaying { get; set; }

    [JsonPropertyName("mediaPositionMs")]
    public long MediaPositionMs { get; set; } = -1;

    [JsonPropertyName("mediaDurationMs")]
    public long MediaDurationMs { get; set; } = -1;
}

/// <summary>妙播/投放在内的播放设备（本机、蓝牙、Cast 设备等）</summary>
public class CastDevice
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>设备类型图标：phone（本机）/ bt（蓝牙）/ tv（投屏）/ speaker（音箱）</summary>
    [JsonPropertyName("deviceType")]
    public string DeviceType { get; set; } = "speaker";

    [JsonPropertyName("selected")]
    public bool Selected { get; set; }
}

public class CastDevicesMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "cast_devices";

    [JsonPropertyName("devices")]
    public List<CastDevice> Devices { get; set; } = new();

    /// <summary>HyperOS 小米妙播生态（小爱音箱/小米电视等 Wi-Fi 设备不在 devices 列表中，需在手机控制中心选择）。</summary>
    [JsonPropertyName("miplaySupported")]
    public bool MiplaySupported { get; set; }

    /// <summary>音频正通过小米妙播投放到音箱/电视/电脑。</summary>
    [JsonPropertyName("miplayCasting")]
    public bool MiplayCasting { get; set; }

    /// <summary>音频正妙播到车机。</summary>
    [JsonPropertyName("carCasting")]
    public bool CarCasting { get; set; }

    /// <summary>妙播可由 PC 反向控制（手机端无障碍服务可用，可自动操作系统选择器）。</summary>
    [JsonPropertyName("miplayControllable")]
    public bool MiplayControllable { get; set; }
}

public class ClearNotificationMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "clear";

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;
}

public class PingMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "ping";

    [JsonPropertyName("deviceName")]
    public string DeviceName { get; set; } = string.Empty;

    [JsonPropertyName("deviceModel")]
    public string DeviceModel { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";
}
