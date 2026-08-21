using System;
using System.Collections.Generic;

namespace UnoBrowser.UnoApp.Services;

public interface IBrowsingHistoryService
{
    event Action? HistoryChanged;
    IReadOnlyList<string> Records { get; }
    void AddRecord(string url);
    void ClearHistory();
    void Load();
    void Save();
}
