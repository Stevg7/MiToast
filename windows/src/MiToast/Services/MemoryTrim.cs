using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MiToast.Services;

/// <summary>
/// 空闲内存回收：托盘常驻程序不必为“峰值后的残留”买单——
/// 通知突发期间 JIT、GC 各代和 WPF 渲染都会把工作集撑高，卡片关闭后
/// 那些页面大多已不可达但 GC 不会主动归还。空闲时做一次全量压缩 GC
/// （把碎片整理掉、代清理干净），再调 SetProcessWorkingSetSize(-1,-1)
/// 把工作集还给系统：页面转入待命列表，需要时缺页自动回来，代价很低。
/// </summary>
public static class MemoryTrim
{
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    private static readonly IntPtr UseCurrentProcess = new(-1);

    /// <summary>全量压缩回收并释放工作集。只在空闲时调用（卡片全关、无落盘任务）。</summary>
    public static void TrimNow()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        SetProcessWorkingSetSize(UseCurrentProcess, new IntPtr(-1), new IntPtr(-1));
    }
}
