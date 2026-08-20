using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UnoBrowser.UnoApp.Models;
using UnoBrowser.UnoApp.Services;

namespace UnoBrowser.UnoApp.ViewModels;

public partial class DownloadListViewModel : ViewModelBase
{
    private readonly IDownloadHistoryService _historyService;
    private readonly IDownloadService _downloadService;
    private readonly IBrowserProvider? _browserProvider;

    public ObservableCollection<DownloadRecord> Records { get; } = [];

    [ObservableProperty]
    public partial DownloadRecord? SelectedRecord { get; set; }

    /// <summary>是否有下载记录（用于空状态提示）。</summary>
    public bool HasRecords => Records.Count > 0;

    public DownloadListViewModel(
        IDownloadHistoryService historyService,
        IDownloadService downloadService,
        IBrowserProvider? browserProvider = null)
    {
        LogHelper.Info("[下载列表] 构造函数 - 初始化");
        _historyService = historyService;
        _downloadService = downloadService;
        _browserProvider = browserProvider;
        _historyService.HistoryChanged += OnHistoryChanged;
        Refresh();
    }

    private void OnHistoryChanged()
    {
        LogHelper.Info("[下载列表] 历史记录变更，刷新列表");
        SyncRecords();
    }

    /// <summary>
    /// 智能同步：新记录追加，已有记录保留（以维护正在下载的记录引用）。
    /// </summary>
    private void SyncRecords()
    {
        // 移除已不在历史中的记录
        var historyIds = new HashSet<string>(_historyService.Records.Select(r => r.Id));
        for (int i = Records.Count - 1; i >= 0; i--)
        {
            if (!historyIds.Contains(Records[i].Id))
                Records.RemoveAt(i);
        }

        // 追加新记录（未在列表中出现的）
        foreach (var r in _historyService.Records)
        {
            if (!Records.Any(rec => rec.Id == r.Id))
                Records.Add(r);
        }

        OnPropertyChanged(nameof(HasRecords));
    }

    private void Refresh()
    {
        Records.Clear();
        foreach (var r in _historyService.Records)
            Records.Add(r);
        OnPropertyChanged(nameof(HasRecords));
        LogHelper.Info($"[下载列表] 刷新完成，共 {Records.Count} 条记录");
    }

    /// <summary>
    /// 开始下载文件（浏览器式入口）。自动从 WebView 获取 Cookie 以支持鉴权下载。
    /// 文件名缺省时从 URL 推断，支持任意文件格式。
    /// </summary>
    public async void StartDownload(string url, string? fileName = null)
    {
        var record = new DownloadRecord
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            FileName = string.IsNullOrWhiteSpace(fileName)
                ? BrowserProvider.GetFileNameFromUrl(url)
                : fileName,
            Url = url,
            State = DownloadState.Pending,
            DownloadTime = DateTime.Now
        };

        _historyService.AddRecord(record);
        Records.Add(record);
        OnPropertyChanged(nameof(HasRecords));
        LogHelper.Info($"[下载列表] 新增下载任务: {record.FileName}, URL={url}");

        await DownloadAsync(record);
    }

    /// <summary>
    /// 执行下载任务（新任务与重试共用）。
    /// 进度通过 Progress 组件封送到 UI 线程，实现进度条实时刷新。
    /// </summary>
    private async Task DownloadAsync(DownloadRecord record)
    {
        // 从浏览器获取 Cookie 用于鉴权
        string? cookies = null;
        if (_browserProvider is not null)
        {
            try
            {
                cookies = await _browserProvider.GetCookiesAsync(record.Url);
                LogHelper.Info($"[下载列表] 获取到 Cookie: {(string.IsNullOrEmpty(cookies) ? "无" : $"{cookies.Length} 字符")}");
            }
            catch (Exception ex)
            {
                LogHelper.Warn($"[下载列表] 获取 Cookie 失败: {ex.Message}");
            }
        }

        try
        {
            var progress = new Progress<(double Percent, long Received, long Total)>(p =>
            {
                record.Progress = p.Percent;
            });

            await _downloadService.StartDownloadAsync(
                record,
                onProgress: progress,
                ct: CancellationToken.None,
                cookies: cookies);

            // 下载完成后刷新历史记录（含最终文件名/路径/状态）
            _historyService.UpdateRecord(record);
            LogHelper.Info($"[下载列表] 下载完成: {record.FileName}, 状态={record.State}");
        }
        catch (Exception ex)
        {
            LogHelper.Error($"[下载列表] 下载异常: {record.FileName}", ex);
            _historyService.UpdateRecord(record);
        }
    }

    /// <summary>取消下载任务（下载中/等待中的记录）。</summary>
    [RelayCommand]
    private void CancelDownload(DownloadRecord? record)
    {
        record ??= SelectedRecord;
        if (record is null) return;
        if (record.CanCancel)
        {
            LogHelper.Info($"[下载列表] 取消下载: {record.FileName}");
            _downloadService.CancelDownload(record.Id);
        }
    }

    /// <summary>重试下载任务（失败/已取消的记录）。</summary>
    [RelayCommand]
    private async Task RetryDownloadAsync(DownloadRecord? record)
    {
        record ??= SelectedRecord;
        if (record is null) return;
        if (!record.CanRetry)
        {
            LogHelper.Warn($"[下载列表] 当前状态不可重试: {record.State}");
            return;
        }

        LogHelper.Info($"[下载列表] 重试下载: {record.FileName}");
        record.State = DownloadState.Pending;
        record.Progress = 0;
        record.ErrorMessage = null;
        record.CompletedTime = null;
        record.LocalPath = string.Empty;
        record.FileSize = 0;
        record.MimeType = string.Empty;
        _historyService.UpdateRecord(record);

        await DownloadAsync(record);
    }

    /// <summary>打开已下载的文件（Completed 记录）。</summary>
    [RelayCommand]
    private void OpenFile(DownloadRecord? record)
    {
        record ??= SelectedRecord;
        if (record is null) return;
        if (!record.CanOpen) return;

        var localPath = record.LocalPath;
        if (string.IsNullOrWhiteSpace(localPath) || !File.Exists(localPath))
        {
            LogHelper.Warn($"[下载列表] 打开文件失败，文件不存在: {localPath}");
            return;
        }

        LogHelper.Info($"[下载列表] 打开文件: {localPath}");
#if ANDROID
        OpenFileOnAndroid(localPath);
#else
        try
        {
            Process.Start(new ProcessStartInfo(localPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogHelper.Error($"[下载列表] 打开文件失败: {ex.Message}", ex);
        }
#endif
    }

    /// <summary>打开下载目录命令（供下载面板 Header 按钮绑定）。</summary>
    [RelayCommand]
    private void OpenDownloadsFolder() => OpenDownloadFolder();

    /// <summary>打开下载目录（供 SettingsViewModel 调用）。</summary>
    public void OpenDownloadFolder()
    {
        var dir = _downloadService.GetDownloadDirectory();
        LogHelper.Info($"[下载列表] 打开下载文件夹: {dir}");
        try
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

#if ANDROID
            OpenFolderOnAndroid(dir);
#else
            OpenFolderInExplorer(dir);
#endif
        }
        catch (Exception ex)
        {
            LogHelper.Error($"[下载列表] 打开下载文件夹失败: {ex.Message}", ex);
        }
    }

    /// <summary>清除所有下载历史记录。</summary>
    [RelayCommand]
    private void DeleteAllRecords()
    {
        LogHelper.Info("[下载列表] 删除所有记录");
        _historyService.ClearHistory();
        Records.Clear();
        OnPropertyChanged(nameof(HasRecords));
    }

    /// <summary>清除所有下载历史记录（供 SettingsViewModel 调用）。</summary>
    public void ClearHistory()
    {
        DeleteAllRecords();
    }

    [RelayCommand]
    private void OpenFolder(DownloadRecord? record)
    {
        record ??= SelectedRecord;
        if (record == null)
        {
            LogHelper.Warn("[下载列表] OpenFolder - 未选中任何记录");
            return;
        }

        var localPath = record.LocalPath;

#if ANDROID
        // Android: 使用 Intent 打开文件
        OpenFileOnAndroid(localPath);
#else
        if (File.Exists(localPath))
        {
            LogHelper.Info($"[下载列表] 打开文件所在位置: {localPath}");
            RevealFileInFolder(localPath);
        }
        else if (!string.IsNullOrWhiteSpace(localPath))
        {
            try
            {
                var folderPath = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(folderPath) && Directory.Exists(folderPath))
                {
                    LogHelper.Info($"[下载列表] 文件不存在，打开父目录: {folderPath}");
                    OpenFolderInExplorer(folderPath);
                }
                else
                {
                    LogHelper.Warn($"[下载列表] 文件路径无效或目录不存在: {localPath}");
                }
            }
            catch (Exception ex)
            {
                LogHelper.Error($"[下载列表] 打开文件夹失败: {ex.Message}", ex);
            }
        }
#endif
    }

    [RelayCommand]
    private void DeleteRecord(DownloadRecord? record)
    {
        record ??= SelectedRecord;
        if (record == null)
        {
            LogHelper.Warn("[下载列表] 删除记录 - 未选中任何记录");
            return;
        }

        // 如果正在下载中，先取消
        if (record.State == DownloadState.Downloading)
            _downloadService.CancelDownload(record.Id);

        LogHelper.Info($"[下载列表] 删除记录: Id={record.Id}, Name={record.FileName}");
        _historyService.RemoveRecord(record);
        Records.Remove(record);
        if (SelectedRecord == record) SelectedRecord = null;
        OnPropertyChanged(nameof(HasRecords));
    }

#if ANDROID
    private static void OpenFileOnAndroid(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                LogHelper.Warn("[下载列表] Android 打开文件失败: 路径为空");
                return;
            }

            if (File.Exists(filePath))
            {
                var file = new Java.IO.File(filePath);
                var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                    Android.App.Application.Context,
                    Android.App.Application.Context.PackageName + ".provider",
                    file);

                var intent = new Android.Content.Intent(Android.Content.Intent.ActionView);
                intent.SetDataAndType(uri, GetMimeType(filePath));
                intent.AddFlags(Android.Content.ActivityFlags.GrantReadUriPermission);
                intent.AddFlags(Android.Content.ActivityFlags.NewTask);

                Android.App.Application.Context.StartActivity(intent);
                LogHelper.Info($"[下载列表] Android 打开文件: {filePath}");
            }
            else
            {
                LogHelper.Warn($"[下载列表] Android 文件不存在: {filePath}");
            }
        }
        catch (Exception ex)
        {
            LogHelper.Error($"[下载列表] Android 打开文件异常: {ex.Message}", ex);
        }
    }

    /// <summary>在 Android 上打开文件夹（使用文件管理器 Intent）。</summary>
    private static void OpenFolderOnAndroid(string folderPath)
    {
        try
        {
            var dir = new Java.IO.File(folderPath);
            if (!dir.Exists())
            {
                dir.Mkdirs();
            }

            var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(
                Android.App.Application.Context,
                Android.App.Application.Context.PackageName + ".provider",
                dir);

            var intent = new Android.Content.Intent(Android.Content.Intent.ActionView);
            intent.SetDataAndType(uri, Android.Provider.DocumentsContract.Document.MimeTypeDir);
            intent.AddFlags(Android.Content.ActivityFlags.GrantReadUriPermission);
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);

            Android.App.Application.Context.StartActivity(intent);
            LogHelper.Info($"[下载列表] Android 打开文件夹: {folderPath}");
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[下载列表] Android 用文件管理器打开文件夹失败，尝试备用方式: {ex.Message}");
            try
            {
                // 备用：通过 SAW Intent 让用户选择文件管理器
                var intent = new Android.Content.Intent(Android.Content.Intent.ActionOpenDocumentTree);
                intent.AddFlags(Android.Content.ActivityFlags.NewTask);
                Android.App.Application.Context.StartActivity(intent);
            }
            catch { /* 放弃 */ }
        }
    }

    private static string GetMimeType(string filePath)
    {
        var ext = Path.GetExtension(filePath)?.ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".txt" => "text/plain",
            ".html" or ".htm" => "text/html",
            ".zip" => "application/zip",
            ".apk" => "application/vnd.android.package-archive",
            _ => "*/*"
        };
    }
#else
    private static void RevealFileInFolder(string filePath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
            LogHelper.Info($"[下载列表] 资源管理器定位文件(Windows): {filePath}");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            System.Diagnostics.Process.Start("open", $"-R \"{filePath}\"");
            LogHelper.Info($"[下载列表] Finder 定位文件(macOS): {filePath}");
        }
        else
        {
            var folder = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(folder))
                OpenFolderInExplorer(folder);
        }
    }

    private static void OpenFolderInExplorer(string folderPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            System.Diagnostics.Process.Start("explorer.exe", $"\"{folderPath}\"");
            LogHelper.Info($"[下载列表] 资源管理器打开目录(Windows): {folderPath}");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            System.Diagnostics.Process.Start("open", $"\"{folderPath}\"");
            LogHelper.Info($"[下载列表] Finder 打开目录(macOS): {folderPath}");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            System.Diagnostics.Process.Start("xdg-open", $"\"{folderPath}\"");
            LogHelper.Info($"[下载列表] 文件管理器打开目录(Linux): {folderPath}");
        }
        else
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folderPath)
            { UseShellExecute = true });
            LogHelper.Info($"[下载列表] 打开目录(其他平台): {folderPath}");
        }
    }
#endif
}
