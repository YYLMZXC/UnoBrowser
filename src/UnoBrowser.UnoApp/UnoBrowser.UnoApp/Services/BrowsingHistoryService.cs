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

            // 兼容旧版纯字符串格式
            var loaded = JsonConvert.DeserializeObject<List<BrowsingHistoryRecord>>(json);
            if (loaded is not null)
            {
                _records = loaded;
            }
            else
            {
                // 尝试作为旧版字符串列表加载
                var legacyRecords = JsonConvert.DeserializeObject<List<string>>(json);
                _records = legacyRecords?
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Select(u => new BrowsingHistoryRecord { Url = u, Title = "" })
                    .ToList() ?? new List<BrowsingHistoryRecord>();
            }

            LogHelper.Info($"[浏览历史] 加载成功，共 {_records.Count} 条记录");
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
        }
        catch (Exception ex)
        {
            LogHelper.Error("[浏览历史] 保存失败", ex);
        }
    }

    /// <summary>
    /// 添加 URL 到浏览历史（去重，最新的在最前面）。
    /// 如果已存在相同 URL，更新标题。
    /// </summary>
    public void AddRecord(string url, string title = "")
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        // 去重：移除已有的相同 URL
        var existing = _records.FirstOrDefault(r => r.Url == url);
        if (existing is not null)
        {
            _records.Remove(existing);
        }

        // 插入到最前面
        _records.Insert(0, new BrowsingHistoryRecord
        {
            Url = url,
            Title = !string.IsNullOrWhiteSpace(title) ? title : existing?.Title ?? ""
        });

        // 限制最大记录数
        while (_records.Count > MaxRecords)
            _records.RemoveAt(_records.Count - 1);

        Save();
        HistoryChanged?.Invoke();
    }

    public void ClearHistory()
    {
        _records.Clear();
        if (File.Exists(_filePath))
            File.Delete(_filePath);
        LogHelper.Info("[浏览历史] 已清空所有记录");
        HistoryChanged?.Invoke();
    }
}
