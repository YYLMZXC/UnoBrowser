using System;
using System.Collections.Generic;
using UnoBrowser.UnoApp.Models;

namespace UnoBrowser.UnoApp.Services;

public interface IBrowsingHistoryService
{
    event Action? HistoryChanged;
    IReadOnlyList<BrowsingHistoryRecord> Records { get; }
    void AddRecord(string url, string title = "");
    void ClearHistory();
    void Load();
    void Save();
}
