using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnoBrowser.UnoApp.Models;

namespace UnoBrowser.UnoApp.Services;

public class DownloadHistoryService : IDownloadHistoryService
{
    private readonly string _filePath;
    private List<DownloadRecord> _records = new();

    public event Action? HistoryChanged;

    public IReadOnlyList<DownloadRecord> Records => _records.AsReadOnly();

    public DownloadHistoryService()
    {
        _filePath = Path.Combine(AppPaths.DownloadHistory, "download_history.json");
        LogHelper.Info($"[下载历史] 初始化，存储路径: {_filePath}");
        MigrateLegacyFile();
    }

    /// <summary>
    /// 迁移旧版本路径下的 download_history.json（%LocalAppData%/SCAssistant/），
    /// 避免升级后历史记录丢失。
    /// </summary>
    private void MigrateLegacyFile()
    {
        try
        {
            var legacyPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SCAssistant", "download_history.json"); // 旧版 SCAssistant 路径，保留迁移兼容
            if (File.Exists(legacyPath) && !File.Exists(_filePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                File.Move(legacyPath, _filePath);
                LogHelper.Info($"[下载历史] 已迁移旧文件: {legacyPath} -> {_filePath}");
            }
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[下载历史] 旧文件迁移失败: {ex.Message}");
        }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                LogHelper.Info("[下载历史] 文件不存在，初始化为空列表");
                _records = new List<DownloadRecord>();
                return;
            }

            var json = File.ReadAllText(_filePath);
            _records = JsonConvert.DeserializeObject<List<DownloadRecord>>(json) ?? new List<DownloadRecord>();
            LogHelper.Info($"[下载历史] 加载成功，共 {_records.Count} 条记录 -> {_filePath}");
        }
        catch (Exception ex)
        {
            // 文件损坏或格式异常时不崩溃，备份损坏文件后重置为空列表
            LogHelper.Error($"[下载历史] 加载失败，将重置为空列表", ex);
            try
            {
                if (File.Exists(_filePath))
                {
                    var backupPath = _filePath + ".corrupt";
                    File.Copy(_filePath, backupPath, overwrite: true);
                    LogHelper.Warn($"[下载历史] 已备份损坏文件: {backupPath}");
                }
            }
            catch (Exception backupEx)
            {
                LogHelper.Warn($"[下载历史] 备份损坏文件失败: {backupEx.Message}");
            }
            _records = new List<DownloadRecord>();
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (dir is not null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                LogHelper.Info($"[下载历史] 创建目录: {dir}");
            }

            var json = JsonConvert.SerializeObject(_records, Formatting.Indented);
            File.WriteAllText(_filePath, json);
            LogHelper.Info($"[下载历史] 保存成功，共 {_records.Count} 条记录 -> {_filePath}");
        }
        catch (Exception ex)
        {
            LogHelper.Error("[下载历史] 保存失败", ex);
        }
    }

    public void AddRecord(DownloadRecord record)
    {
        _records.Add(record);
        LogHelper.Info($"[下载历史] 添加记录: Id={record.Id}, Name={record.FileName}");
        Save();
        HistoryChanged?.Invoke();
    }

    public void UpdateRecord(DownloadRecord record)
    {
        var index = _records.FindIndex(r => r.Id == record.Id);
        if (index >= 0)
        {
            _records[index] = record;
            LogHelper.Info($"[下载历史] 更新记录: Id={record.Id}, Name={record.FileName}");
            Save();
            HistoryChanged?.Invoke();
        }
        else
        {
            LogHelper.Warn($"[下载历史] 更新失败，未找到记录: Id={record.Id}");
        }
    }

    public void RemoveRecord(DownloadRecord record)
    {
        _records.RemoveAll(r => r.Id == record.Id);
        LogHelper.Info($"[下载历史] 删除记录: Id={record.Id}，当前共 {_records.Count} 条");
        Save();
        HistoryChanged?.Invoke();
    }

    public void ClearHistory()
    {
        var count = _records.Count;
        _records.Clear();
        if (File.Exists(_filePath))
            File.Delete(_filePath);
        LogHelper.Info($"[下载历史] 清空所有记录，已清除 {count} 条");
        HistoryChanged?.Invoke();
    }
}
