using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MiToast.Models;

namespace MiToast.Services;

/// <summary>
/// 历史通知服务：内存保存最近 500 条，并持久化到 %AppData%\MiToast\history.json。
/// 持久化时剥离图标 Base64 以控制文件体积。
/// 写盘采用防抖合并：通知突发到达时不会每条都全量序列化写文件，
/// 而是在静默 1.5 秒后于后台线程一次写入；退出时由 Flush() 兜底落盘。
/// </summary>
public class HistoryService
{
    private static readonly Lazy<HistoryService> _instance = new(() => new HistoryService());
    public static HistoryService Instance => _instance.Value;

    private const int MaxItems = 500;
    private const int SaveDebounceMs = 1500;

    private readonly List<NotificationMessage> _items = new();
    private readonly object _lock = new();
    private readonly object _ioLock = new();
    private bool _dirty;
    private int _saveScheduled;

    /// <summary>新增或清空历史时触发。</summary>
    public event EventHandler? Changed;

    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiToast");

    private static string HistoryPath => Path.Combine(ConfigDir, "history.json");

    private HistoryService()
    {
        Load();
    }

    public void Add(NotificationMessage message)
    {
        if (message == null) return;
        if (!AppSettings.Instance.HistoryEnabled) return;

        lock (_lock)
        {
            _items.Insert(0, message);
            if (_items.Count > MaxItems)
            {
                _items.RemoveRange(MaxItems, _items.Count - MaxItems);
            }
            _dirty = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    public List<NotificationMessage> GetAll()
    {
        lock (_lock)
        {
            return _items.ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
            _dirty = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    /// <summary>
    /// 防抖调度落盘：首次变更后延迟 SaveDebounceMs 写入；期间的新变更会被
    /// 同一次写入合并（全量快照），突发通知最多触发一次写盘。
    /// </summary>
    private void ScheduleSave()
    {
        if (Interlocked.CompareExchange(ref _saveScheduled, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(SaveDebounceMs).ConfigureAwait(false);
            Interlocked.Exchange(ref _saveScheduled, 0);
            Save();
        });
    }

    /// <summary>同步落盘（进程退出前调用）：有未落盘变更时立即写一次。</summary>
    public void Flush()
    {
        bool need;
        lock (_lock) need = _dirty;
        if (need) Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(HistoryPath))
            {
                var list = JsonSerializer.Deserialize<List<NotificationMessage>>(
                    File.ReadAllText(HistoryPath));
                if (list != null)
                {
                    lock (_lock)
                    {
                        _items.AddRange(list.Take(MaxItems));
                    }
                }
            }
        }
        catch
        {
            // 历史文件损坏时忽略，重新开始记录
        }
    }

    private void Save()
    {
        try
        {
            List<NotificationMessage> snapshot;
            lock (_lock)
            {
                if (!_dirty) return;
                _dirty = false;

                // 复制一份并剥离图标，避免修改内存中的原始对象
                snapshot = _items.Select(m => new NotificationMessage
                {
                    Type = m.Type,
                    Id = m.Id,
                    Key = m.Key,
                    PackageName = m.PackageName,
                    AppName = m.AppName,
                    Title = m.Title,
                    Content = m.Content,
                    BigTitle = m.BigTitle,
                    BigText = m.BigText,
                    SubText = m.SubText,
                    Ticker = m.Ticker,
                    HintTitle = m.HintTitle,
                    HintText = m.HintText,
                    IconBase64 = string.Empty,
                    Timestamp = m.Timestamp,
                    Category = m.Category,
                    IsOngoing = m.IsOngoing,
                    GroupKey = m.GroupKey,
                    PeopleCount = m.PeopleCount,
                    Progress = m.Progress
                }).ToList();
            }

            // 序列化 + 写文件在后台线程执行；_ioLock 防止防抖写入与退出 Flush 并发交错
            lock (_ioLock)
            {
                Directory.CreateDirectory(ConfigDir);
                File.WriteAllText(HistoryPath, JsonSerializer.Serialize(snapshot));
            }
        }
        catch
        {
            // 写入失败不影响通知主流程；重新置脏，下次变更或退出 Flush 时补写
            lock (_lock) _dirty = true;
        }
    }
}
