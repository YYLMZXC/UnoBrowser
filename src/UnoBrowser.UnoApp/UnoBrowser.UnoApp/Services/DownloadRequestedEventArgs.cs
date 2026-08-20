using System;

namespace UnoBrowser.UnoApp.Services;

/// <summary>
/// 下载请求事件参数，携带下载地址与可选的服务器真实文件名。
/// 文件名优先级：服务器 Content-Disposition / ResultFilePath > URL 提取 > 兜底默认名。
/// </summary>
public class DownloadRequestedEventArgs : EventArgs
{
    public DownloadRequestedEventArgs(string url, string? fileName = null)
    {
        Url = url;
        FileName = fileName;
    }

    /// <summary>下载地址。</summary>
    public string Url { get; }

    /// <summary>服务器提供的文件名（可能为 null，此时由上层从 URL 推断）。</summary>
    public string? FileName { get; }
}
