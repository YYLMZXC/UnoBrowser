using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace UnoBrowser.UnoApp.Models;

public class DownloadRecord : INotifyPropertyChanged
{
    private string _id = string.Empty;
    private string _fileName = string.Empty;
    private string _url = string.Empty;
    private string _localPath = string.Empty;
    private long _fileSize;
    private DateTime _downloadTime;
    private DateTime? _completedTime;
    private DownloadState _state;
    private string? _errorMessage;
    private double _progress;
    private string _mimeType = string.Empty;

    public string Id
    {
        get => _id;
        set { _id = value; OnPropertyChanged(); }
    }

    public string FileName
    {
        get => _fileName;
        set { _fileName = value; OnPropertyChanged(); }
    }

    public string Url
    {
        get => _url;
        set { _url = value; OnPropertyChanged(); }
    }

    public string LocalPath
    {
        get => _localPath;
        set { _localPath = value; OnPropertyChanged(); }
    }

    public long FileSize
    {
        get => _fileSize;
        set { _fileSize = value; OnPropertyChanged(); OnPropertyChanged(nameof(FileSizeDisplay)); }
    }

    /// <summary>
    /// 文件大小显示文本（供 UI 绑定，不参与序列化）。
    /// </summary>
    [JsonIgnore]
    public string FileSizeDisplay =>
        _fileSize <= 0 ? "" :
        _fileSize < 1024 ? $"{_fileSize} B" :
        _fileSize < 1024 * 1024 ? $"{_fileSize / 1024.0:F1} KB" :
        _fileSize < 1024 * 1024 * 1024 ? $"{_fileSize / (1024.0 * 1024):F1} MB" :
        $"{_fileSize / (1024.0 * 1024 * 1024):F2} GB";

    public DateTime DownloadTime
    {
        get => _downloadTime;
        set { _downloadTime = value; OnPropertyChanged(); }
    }

    public DateTime? CompletedTime
    {
        get => _completedTime;
        set { _completedTime = value; OnPropertyChanged(); }
    }

    public DownloadState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanRetry));
            OnPropertyChanged(nameof(CanOpen));
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set { _errorMessage = value; OnPropertyChanged(); }
    }

    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); }
    }

    /// <summary>响应 Content-Type（如 application/zip），供 UI 展示与打开方式判断。</summary>
    public string MimeType
    {
        get => _mimeType;
        set { _mimeType = value; OnPropertyChanged(); }
    }

    /// <summary>状态显示文本。</summary>
    [JsonIgnore]
    public string StateText => State switch
    {
        DownloadState.Pending => "等待中",
        DownloadState.Downloading => "下载中",
        DownloadState.Completed => "已完成",
        DownloadState.Failed => "失败",
        DownloadState.Cancelled => "已取消",
        _ => State.ToString()
    };

    /// <summary>是否可取消（下载中/等待中）。</summary>
    [JsonIgnore]
    public bool CanCancel => State == DownloadState.Downloading || State == DownloadState.Pending;

    /// <summary>是否可重试（失败/已取消）。</summary>
    [JsonIgnore]
    public bool CanRetry => State == DownloadState.Failed || State == DownloadState.Cancelled;

    /// <summary>是否可打开文件（已完成）。</summary>
    [JsonIgnore]
    public bool CanOpen => State == DownloadState.Completed;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public override string ToString() => $"[{State}] {FileName}";
}

public enum DownloadState
{
    Pending,
    Downloading,
    Completed,
    Failed,
    Cancelled
}
