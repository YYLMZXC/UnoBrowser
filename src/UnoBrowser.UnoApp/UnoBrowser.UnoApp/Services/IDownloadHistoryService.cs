using System;
using System.Collections.Generic;
using UnoBrowser.UnoApp.Models;

namespace UnoBrowser.UnoApp.Services;

public interface IDownloadHistoryService
{
    event Action? HistoryChanged;
    IReadOnlyList<DownloadRecord> Records { get; }
    void AddRecord(DownloadRecord record);
    void UpdateRecord(DownloadRecord record);
    void RemoveRecord(DownloadRecord record);
    void ClearHistory();
    void Load();
    void Save();
}
