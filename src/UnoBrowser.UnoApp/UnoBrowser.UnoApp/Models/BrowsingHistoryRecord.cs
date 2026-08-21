using System;
using System.Text.Json.Serialization;

namespace UnoBrowser.UnoApp.Models;

/// <summary>
/// 浏览历史记录条目。
/// </summary>
public class BrowsingHistoryRecord
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    /// <summary>访问时间。</summary>
    public DateTime Time { get; set; } = DateTime.Now;

    /// <summary>时间显示文本（供 UI 绑定）。</summary>
    [JsonIgnore]
    public string TimeDisplay => Time.ToString("MM-dd HH:mm");
}
