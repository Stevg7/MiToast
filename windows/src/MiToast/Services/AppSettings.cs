using System;
using System.IO;
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
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // 写入失败不影响运行
        }
    }

    /// <summary>不持久化，仅通知各窗口立即应用当前设置（用于开关即时预览）。</summary>
    public void NotifyChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
