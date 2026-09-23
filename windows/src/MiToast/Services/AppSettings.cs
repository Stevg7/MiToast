using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MiToast.Services;

/// <summary>
/// 应用设置：通知显示位置、卡片大小、边距、历史开关。
/// 持久化到 %AppData%\MiToast\settings.json
/// </summary>
public class AppSettings
{
    private static readonly Lazy<AppSettings> _instance = new(Load);
    public static AppSettings Instance => _instance.Value;

    /// <summary>设置保存后触发，通知窗口实时应用。</summary>
    public event EventHandler? Changed;

    /// <summary>显示位置：TopRight / BottomRight / TopLeft / BottomLeft</summary>
    public string Position { get; set; } = "TopRight";

    /// <summary>通知卡片宽度（像素）</summary>
    public double CardWidth { get; set; } = 480;

    /// <summary>通知窗口与屏幕边缘的距离（像素）</summary>
    public double ScreenMargin { get; set; } = 40;

    /// <summary>通知卡片之间的垂直间距（像素）</summary>
    public double NotificationGap { get; set; } = 3;

    /// <summary>卡片紧凑度系数：0.75（紧凑）~ 1.25（宽松），1.0 为标准</summary>
    public double CardDensity { get; set; } = 1.0;

    /// <summary>是否在电脑上保存历史通知</summary>
    public bool HistoryEnabled { get; set; } = true;

    /// <summary>深色模式开关</summary>
    public bool DarkMode { get; set; } = false;

    /// <summary>音乐通知常驻：媒体卡片排在最前，暂停播放后也不自动消失</summary>
    public bool MusicPersistent { get; set; } = true;

    /// <summary>勿扰模式：off（关）/ pc（PC端静默）/ sync（跟随手机DND）</summary>
    public string DndMode { get; set; } = "off";

    /// <summary>
    /// 手动指定的手机地址（校园网等屏蔽设备发现广播的网络里使用；手机端首页会显示手机地址）。
    /// 留空表示只靠自动发现。可与端口一起带在字符串里（如 192.168.1.5:8080）。
    /// </summary>
    public string ManualPhoneHost { get; set; } = string.Empty;

    /// <summary>手动指定地址的端口（地址串里没写端口时使用）</summary>
    public int ManualPhonePort { get; set; } = Network.LanEndpoint.DefaultPort;

    /// <summary>
    /// 与手机端配对的接入认证码（手机首页显示，连接时发送给手机校验）。
    /// 磁盘上以 DPAPI 加密后的 base64 存储（PairCodeEnc），内存中为明文。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string PairCode { get; set; } = string.Empty;

    /// <summary>配对码的 DPAPI 密文（base64），对外不暴露明文。</summary>
    public string PairCodeEnc { get; set; } = string.Empty;

    /// <summary>上次成功连接的手机地址，自动维护：下次启动可跳过发现直接重连</summary>
    public string LastPhoneHost { get; set; } = string.Empty;

    /// <summary>上次成功连接的端口</summary>
    public int LastPhonePort { get; set; } = Network.LanEndpoint.DefaultPort;

    /// <summary>
    /// 广播发现无响应时，是否扫描本机所在网段找手机。
    /// 默认关闭：部分校园网把端口扫描视为违规行为，需要用户明确开启。
    /// </summary>
    public bool SubnetSweepEnabled { get; set; } = false;

    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiToast");

    private static string ConfigPath => Path.Combine(ConfigDir, "settings.json");

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch
        {
            // 配置损坏时回退默认值
        }
        return new AppSettings();
    }

    private void Normalize()
    {
        if (CardWidth < 300 || CardWidth > 700) CardWidth = 480;
        if (ScreenMargin < 0 || ScreenMargin > 200) ScreenMargin = 40;
        if (NotificationGap < 0 || NotificationGap > 40) NotificationGap = 3;
        if (CardDensity < 0.75 || CardDensity > 1.25) CardDensity = 1.0;
        if (Position is not ("TopRight" or "BottomRight" or "TopLeft" or "BottomLeft"))
            Position = "TopRight";
        if (DndMode is not ("off" or "pc" or "sync"))
            DndMode = "off";

        (ManualPhoneHost, ManualPhonePort) = NormalizeEndpoint(ManualPhoneHost, ManualPhonePort);
        (LastPhoneHost, LastPhonePort) = NormalizeEndpoint(LastPhoneHost, LastPhonePort);

        // 配对码：磁盘密文 → 内存明文（解不开时置空，如换用户/文件损坏）
        PairCode = DecryptPairCode(PairCodeEnc).Trim().ToUpperInvariant();
    }

    /// <summary>
    /// 校验并规范化持久化的地址：地址串自带端口时同步刷新端口字段；不可用的地址直接清空，
    /// 避免程序一直去拨一个非法地址。
    /// </summary>
    private static (string Host, int Port) NormalizeEndpoint(string? host, int port)
    {
        if (!Network.LanEndpoint.TryParse(host, port, out var endpoint))
        {
            return (string.Empty, Network.LanEndpoint.DefaultPort);
        }
        return (endpoint.Host, endpoint.Port);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            // 落盘前先规范化，保证下次读到的永远是合法值（例如手动地址写错时为清空而不是原样存下来）
            PairCode = PairCode.Trim().ToUpperInvariant();
            PairCodeEnc = EncryptPairCode(PairCode);
            Normalize();
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 写入失败不影响运行
        }
    }

    /// <summary>配对码加密：明文 → DPAPI 密文 base64（加密失败时按未配置处理）。</summary>
    private static string EncryptPairCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return string.Empty;
        try
        {
            return Convert.ToBase64String(
                SensitiveStorage.Protect(Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant())));
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>配对码解密：DPAPI 密文 base64 → 明文（失败返回空串）。</summary>
    private static string DecryptPairCode(string enc)
    {
        if (string.IsNullOrWhiteSpace(enc)) return string.Empty;
        try
        {
            var plain = SensitiveStorage.Unprotect(Convert.FromBase64String(enc));
            return plain != null ? Encoding.UTF8.GetString(plain) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>不持久化，仅通知各窗口立即应用当前设置（用于开关即时预览）。</summary>
    public void NotifyChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
