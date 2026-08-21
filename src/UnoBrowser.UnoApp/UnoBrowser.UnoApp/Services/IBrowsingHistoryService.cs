using System;
using System.Collections.Generic;
using UnoBrowser.UnoApp.Models;

namespace UnoBrowser.UnoApp.Services;

public interface IBrowsingHistoryService
{
    event Action? HistoryChanged;
    IReadOnlyList<BrowsingHistoryRecord> Records { get; }
    void AddRecord(string url, string title = "");
    void RemoveRecord(string url);
    void RemoveRecords(IEnumerable<string> urls);
    void ClearHistory();
    void Load();
    void Save();
}
