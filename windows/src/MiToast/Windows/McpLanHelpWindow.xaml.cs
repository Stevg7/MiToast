using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using MiToast.Services;
using MiToast.UI;

namespace MiToast;

/// <summary>
/// 「局域网 MCP 接口」的使用说明弹窗（设置页小按钮打开，模态）。
/// 端点/示例命令按当前设置现场生成：curl 示例里的配对码刻意用占位符，
/// 不把真实凭据放进剪贴板。
/// </summary>
public partial class McpLanHelpWindow : Window
{
    public McpLanHelpWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyTheme(AppSettings.Instance.DarkMode);
        ApplyTheme(AppSettings.Instance.DarkMode);

        StatusText.Text = "当前状态：" + McpLanService.Describe();
        CurlText.Text = BuildCurlSample();
    }

    private static string BuildCurlSample()
    {
        string ip = McpLanServer.LocalIpv4Addresses().FirstOrDefault() ?? "<电脑IP>";
        int port = AppSettings.Instance.McpLanPort;
        return $"curl -X POST http://{ip}:{port}/mcp \\\n" +
               "  -H \"Authorization: Bearer <配对码>\" \\\n" +
               "  -H \"Content-Type: application/json\" \\\n" +
               "  -d '{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\"," +
               "\"params\":{\"name\":\"notification_stats\",\"arguments\":{\"hours\":24}}}'";
    }

    private void CopyCurlButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CurlText.Text);
            CopyFeedback.Text = "已复制，粘贴时记得把 <配对码> 换成你的配对码";
        }
        catch
        {
            // 剪贴板被其他程序占用时忽略
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- 主题（与设置窗口同一套色板） ----------

    private void ApplyTheme(bool dark)
        => ThemeTransition.Apply((FrameworkElement)Content, dark, () => ApplyThemeColors(dark));

    private void ApplyThemeColors(bool dark)
    {
        FluentWindow.ApplyChrome(this, dark);
        Resources["PageBgBrush"] = FluentWindow.CreatePageBrush(dark);
        SetBrush("CardBgBrush", dark ? Color.FromRgb(0x24, 0x24, 0x24) : Colors.White);
        SetBrush("TextPrimaryBrush", dark ? Color.FromRgb(0xF2, 0xF2, 0xF2) : Colors.Black);
        SetBrush("TextSecondaryBrush", dark ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x00, 0x00, 0x00));
        SetBrush("TextTertiaryBrush", dark ? Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x66, 0x00, 0x00, 0x00));
        SetBrush("BorderBrush", dark ? Color.FromRgb(0x39, 0x39, 0x39) : Color.FromRgb(0xE0, 0xE0, 0xE0));
        SetBrush("HoverBrush", dark ? Color.FromRgb(0x4A, 0x4A, 0x4A) : Color.FromRgb(0xE8, 0xE8, 0xE8));
        SetBrush("FieldBgBrush", dark ? Color.FromRgb(0x43, 0x43, 0x43) : Color.FromRgb(0xF0, 0xF0, 0xF0));
        SetBrush("PrimaryBrush", dark ? Color.FromRgb(0x27, 0x7A, 0xF7) : Color.FromRgb(0x34, 0x82, 0xFF));
        SetBrush("DangerBrush", dark ? Color.FromRgb(0xF1, 0x25, 0x22) : Color.FromRgb(0xE9, 0x46, 0x34));
        SetBrush("ScrollBarThumbBrush", dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0x00, 0x00, 0x00));
        SetBrush("ScrollBarThumbHoverBrush", dark ? Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x4D, 0x00, 0x00, 0x00));
        SetBrush("ScrollBarThumbActiveBrush", dark ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x66, 0x00, 0x00, 0x00));
    }

    private void SetBrush(string key, Color color)
        => Resources[key] = new SolidColorBrush(color);
}
