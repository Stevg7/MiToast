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
/// 历史通知服务：内存保存最近 10000 条，并持久化到 %AppData%\MiToast\history.json
/// （文件经 DPAPI 按当前用户凭据加密，见 <see cref="SensitiveStorage"/>）。
/// 写入前按"相同内容"去重：应用 + 标题 + 正文完全相同的通知合并为一条（取最新时间置顶），
/// 用于抑制音乐卡片反复抓取等场景下相同内容通知在历史里堆成一片。
/// 内存与磁盘都不保存图标（Add 时剥离 IconBase64），10000 条规模下内存可控。
/// 写盘采用防抖合并：通知突发到达时不会每条都全量序列化写文件，
/// 而是在静默 1.5 秒后于后台线程一次写入；退出时由 Flush() 兜底落盘。
/// </summary>
public class HistoryService
{
    private static readonly Lazy<HistoryService> _instance = new(() => new HistoryService());
    public static HistoryService Instance => _instance.Value;

    private const int MaxItems = 10000;
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
            RemoveDuplicate(message);
            _items.Insert(0, StripIcon(message));
            if (_items.Count > MaxItems)
            {
                _items.RemoveRange(MaxItems, _items.Count - MaxItems);
            }
            _dirty = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    /// <summary>批量写入（手机端历史同步用）：一次加锁去重，只触发一次界面刷新与落盘。</summary>
    public void AddRange(IEnumerable<NotificationMessage> messages)
    {
        if (messages == null) return;
        if (!AppSettings.Instance.HistoryEnabled) return;

        lock (_lock)
        {
            foreach (var message in messages)
            {
                if (message == null) continue;
                RemoveDuplicate(message);
                _items.Insert(0, StripIcon(message));
            }
            if (_items.Count > MaxItems)
            {
                _items.RemoveRange(MaxItems, _items.Count - MaxItems);
            }
            _dirty = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    /// <summary>内存与磁盘都不保存图标：Add 时即剥离，避免 10000 条规模下图标占用内存。</summary>
    private static NotificationMessage StripIcon(NotificationMessage message)
    {
        if (message.IconBase64.Length == 0) return message;
        return new NotificationMessage
        {
            Type = message.Type,
            Id = message.Id,
            Key = message.Key,
            PackageName = message.PackageName,
            AppName = message.AppName,
            Title = message.Title,
            Content = message.Content,
            BigTitle = message.BigTitle,
            BigText = message.BigText,
            SubText = message.SubText,
            Ticker = message.Ticker,
            HintTitle = message.HintTitle,
            HintText = message.HintText,
            IconBase64 = string.Empty,
            Timestamp = message.Timestamp,
            Category = message.Category,
            IsOngoing = message.IsOngoing,
            GroupKey = message.GroupKey,
            PeopleCount = message.PeopleCount,
            Progress = message.Progress,
            MediaActions = message.MediaActions,
            MediaIsPlaying = message.MediaIsPlaying,
            MediaPositionMs = message.MediaPositionMs,
            MediaDurationMs = message.MediaDurationMs
        };
    }

    /// <summary>历史里最新一条通知的时间戳（无历史时为 0），供手机端增量同步作游标。</summary>
    public long NewestTimestamp()
    {
        lock (_lock)
        {
            return _items.Count > 0 ? _items[0].Timestamp : 0;
        }
    }

    /// <summary>
    /// 相同内容合并：命中已有条目时移除旧条目，由调用方把新消息插入置顶——
    /// 效果是把内容相同的多次推送合并显示为最新一条（时间与内容取最新）。
    /// 判定只看展示内容（应用 + 标题 + 正文），同一通知的 Key/Id 不同但内容不同时仍各记一条。
    /// </summary>
    private void RemoveDuplicate(NotificationMessage message)
    {
        string app = message.AppName.Length > 0 ? message.AppName : message.PackageName;
        string title = message.BigTitle.Length > 0 ? message.BigTitle : message.Title;
        string content = message.BigText.Length > 0 ? message.BigText : message.Content;

        int existing = _items.FindIndex(m =>
        {
            string mApp = m.AppName.Length > 0 ? m.AppName : m.PackageName;
            string mTitle = m.BigTitle.Length > 0 ? m.BigTitle : m.Title;
            string mContent = m.BigText.Length > 0 ? m.BigText : m.Content;
            return mApp == app && mTitle == title && mContent == content;
        });
        if (existing >= 0)
        {
            _items.RemoveAt(existing);
        }
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
            // 历史文件为 DPAPI 加密存储（兼容读取旧版明文文件）
            var text = SensitiveStorage.ReadHistoryText(HistoryPath);
            if (string.IsNullOrEmpty(text)) return;

            var list = JsonSerializer.Deserialize<List<NotificationMessage>>(text);
            if (list != null)
            {
                lock (_lock)
                {
                    // 文件里的旧条目同样做一遍去重（历史文件是"最新在前"顺序，
                    // 追加式重建保持该顺序，重复的旧条目被丢弃）
                    _items.Clear();
                    foreach (var m in list)
                    {
                        RemoveDuplicate(m);
                        _items.Add(m);
                        if (_items.Count >= MaxItems) break;
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

            // 序列化 + 加密写文件在后台线程执行；_ioLock 防止防抖写入与退出 Flush 并发交错
            lock (_ioLock)
            {
                Directory.CreateDirectory(ConfigDir);
                SensitiveStorage.WriteHistoryText(HistoryPath, JsonSerializer.Serialize(snapshot));
            }
        }
        catch
        {
            // 写入失败不影响通知主流程；重新置脏，下次变更或退出 Flush 时补写
            lock (_lock) _dirty = true;
        }
    }
}
