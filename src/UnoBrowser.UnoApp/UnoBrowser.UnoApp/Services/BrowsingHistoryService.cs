using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnoBrowser.UnoApp.Models;

namespace UnoBrowser.UnoApp.Services;

/// <summary>
/// 浏览历史服务 — JSON 持久化到 BrowsingHistory/browsing_history.json。
/// </summary>
public class BrowsingHistoryService : IBrowsingHistoryService
{
    private readonly string _filePath;
    private List<BrowsingHistoryRecord> _records = new();
    private const int MaxRecords = 200;

    public event Action? HistoryChanged;

    public IReadOnlyList<BrowsingHistoryRecord> Records => _records.AsReadOnly();

    public BrowsingHistoryService()
    {
        _filePath = Path.Combine(AppPaths.BrowsingHistory, "browsing_history.json");
        LogHelper.Info($"[浏览历史] 初始化，存储路径: {_filePath}");
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                LogHelper.Info("[浏览历史] 文件不存在，初始化为空列表");
                _records = new List<BrowsingHistoryRecord>();
                return;
            }

            var json = File.ReadAllText(_filePath);
            LogHelper.Info($"[浏览历史] 读取文件成功，JSON 长度: {json.Length}");

            // 兼容旧版纯字符串格式
            var loaded = JsonConvert.DeserializeObject<List<BrowsingHistoryRecord>>(json);
            if (loaded is not null)
            {
                _records = loaded;
                LogHelper.Info($"[浏览历史] 反序列化成功，共 {_records.Count} 条记录");
            }
            else
            {
                // 尝试作为旧版字符串列表加载
                var legacyRecords = JsonConvert.DeserializeObject<List<string>>(json);
                _records = legacyRecords?
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Select(u => new BrowsingHistoryRecord { Url = u, Title = "" })
                    .ToList() ?? new List<BrowsingHistoryRecord>();
                LogHelper.Info($"[浏览历史] 旧版格式迁移完成，共 {_records.Count} 条记录");
            }

            // 打印每条记录用于调试
            for (int i = 0; i < _records.Count; i++)
            {
                var r = _records[i];
                LogHelper.Info($"[浏览历史]   [{i}] Url={r.Url}, Title={r.Title}, Time={r.Time:yyyy-MM-dd HH:mm:ss}");
            }
        }
        catch (Exception ex)
        {
            LogHelper.Error("[浏览历史] 加载失败，将重置为空列表", ex);
            _records = new List<BrowsingHistoryRecord>();
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonConvert.SerializeObject(_records, Formatting.Indented);
            File.WriteAllText(_filePath, json);
            LogHelper.Info($"[浏览历史] 保存成功，共 {_records.Count} 条记录 -> {_filePath}");
        }
        catch (Exception ex)
        {
            LogHelper.Error("[浏览历史] 保存失败", ex);
        }
    }

    /// <summary>
    /// 添加 URL 到浏览历史（去重，最新的在最前面）。
    /// 如果已存在相同 URL，更新标题和时间。
    /// </summary>
    public void AddRecord(string url, string title = "")
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            LogHelper.Warn("[浏览历史] AddRecord 被调用但 url 为空，跳过");
            return;
        }

        LogHelper.Info($"[浏览历史] AddRecord -> Url={url}, Title={title}");

        // 去重：移除已有的相同 URL
        var existing = _records.FirstOrDefault(r => r.Url == url);
        if (existing is not null)
        {
            _records.Remove(existing);
            LogHelper.Info($"[浏览历史] 移除旧记录: {url}");
        }

        // 插入到最前面
        var record = new BrowsingHistoryRecord
        {
            Url = url,
            Title = !string.IsNullOrWhiteSpace(title) ? title : existing?.Title ?? "",
            Time = DateTime.Now
        };
        _records.Insert(0, record);
        LogHelper.Info($"[浏览历史] 插入新记录: Url={record.Url}, Title={record.Title}, Time={record.Time:yyyy-MM-dd HH:mm:ss}");

        // 限制最大记录数
        while (_records.Count > MaxRecords)
            _records.RemoveAt(_records.Count - 1);

        Save();
        LogHelper.Info($"[浏览历史] AddRecord 完成，当前共 {_records.Count} 条记录，触发 HistoryChanged");
        HistoryChanged?.Invoke();
    }

    /// <summary>删除单条历史记录（按 URL）。</summary>
    public void RemoveRecord(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        LogHelper.Info($"[浏览历史] RemoveRecord -> {url}");
        var count = _records.RemoveAll(r => r.Url == url);
        LogHelper.Info($"[浏览历史] 移除 {count} 条，剩余 {_records.Count} 条");
        Save();
        HistoryChanged?.Invoke();
    }

    /// <summary>批量删除历史记录。</summary>
    public void RemoveRecords(IEnumerable<string> urls)
    {
        var urlSet = new HashSet<string>(urls);
        var count = _records.RemoveAll(r => urlSet.Contains(r.Url));
        LogHelper.Info($"[浏览历史] 批量删除 {count} 条，剩余 {_records.Count} 条");
        Save();
        HistoryChanged?.Invoke();
    }

    public void ClearHistory()
    {
        var count = _records.Count;
        _records.Clear();
        if (File.Exists(_filePath))
            File.Delete(_filePath);
        LogHelper.Info($"[浏览历史] 已清空所有记录，共清除 {count} 条");
        HistoryChanged?.Invoke();
    }
}
