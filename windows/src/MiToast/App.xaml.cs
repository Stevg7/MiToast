using System.Threading;
using System.Windows;
using MiToast.Services;

namespace MiToast;

public partial class App : Application
{
    /// <summary>单实例互斥体；进程生命周期内保持引用，退出时由系统自动释放。</summary>
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例约束：已有实例在运行时直接退出，避免多开导致重复监听端口/重复弹卡片。
        _singleInstanceMutex = new Mutex(initiallyOwned: true, "Local\\MiToast_SingleInstance_v1",
            out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("MiToast 已在后台运行，可从屏幕右下角系统托盘打开设置。",
                "MiToast", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 主窗口是透明通知宿主，关闭它不应退出程序；仅托盘“退出”才结束
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 提前加载设置与历史
        _ = AppSettings.Instance;
        _ = HistoryService.Instance;

        base.OnStartup(e);
        Network.NetworkManager.Instance.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 退出前把防抖未落盘的历史记录刷盘，保证最近通知不丢
        try { HistoryService.Instance.Flush(); } catch { }
        Network.NetworkManager.Instance.Stop();
        base.OnExit(e);
    }
}
