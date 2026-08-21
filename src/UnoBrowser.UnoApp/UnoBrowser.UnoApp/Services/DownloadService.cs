using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnoBrowser.UnoApp.Models;

namespace UnoBrowser.UnoApp.Services;

public class DownloadService : IDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new();

    public DownloadService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
        // 设置合理的 User-Agent，避免被服务器拒绝
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        LogHelper.Info("[下载服务] 初始化完成");
    }

    public async Task StartDownloadAsync(
        DownloadRecord record,
        IProgress<(double Percent, long Received, long Total)>? onProgress = null,
        CancellationToken ct = default,
        string? cookies = null)
    {
        if (string.IsNullOrWhiteSpace(record.Url))
            throw new ArgumentException("下载 URL 不能为空", nameof(record));

        // 使用传入的 CancellationToken 包装一个内部 CancellationTokenSource
        var internalCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cancellations[record.Id] = internalCts;

        try
        {
            LogHelper.Info($"[下载服务] 开始下载: {record.FileName}, URL={record.Url}, Cookies={(string.IsNullOrEmpty(cookies) ? "无" : $"有({cookies.Length}字符)")}");

            record.State = DownloadState.Downloading;
            record.DownloadTime = DateTime.Now;

            // 构建请求，附加 Cookie 用于鉴权
            using var request = new HttpRequestMessage(HttpMethod.Get, record.Url);
            if (!string.IsNullOrWhiteSpace(cookies))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookies);
            }

            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, internalCts.Token);

            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            record.FileSize = totalBytes > 0 ? totalBytes : 0;

            // 记录响应 Content-Type
            if (response.Content.Headers.ContentType is not null)
                record.MimeType = response.Content.Headers.ContentType.MediaType ?? string.Empty;

            LogHelper.Info($"[下载服务] 响应 200 OK, 文件大小: {(totalBytes > 0 ? $"{totalBytes} 字节" : "未知")}" +
                           (string.IsNullOrEmpty(record.MimeType) ? string.Empty : $", 类型: {record.MimeType}"));

            // HTML 错误页/登录页检测：
            // 下载接口返回 text/html 且 URL 路径不是网页型扩展名时，多半是
            // 服务器返回了错误页、验证页或登录跳转，中止下载避免写入垃圾文件。
            if (IsHtmlErrorResponse(response, record.Url))
            {
                throw new InvalidOperationException(
                    "服务器返回了 HTML 页面而非文件（可能是登录/验证/错误页），请检查下载地址");
            }

            // 确定保存路径
            var downloadDir = GetDownloadDirectory();
            Directory.CreateDirectory(downloadDir);

            // 优先使用服务器 Content-Disposition 推荐的文件名（最准确的编码）
            var serverFileName = GetFileNameFromResponse(response, record.Url);
            record.FileName = serverFileName;
            LogHelper.Info($"[下载服务] 文件名确定: {record.FileName} (来源: 服务器 Content-Disposition)");

            // 处理重名文件
            var savePath = GetSavePath(downloadDir, record.FileName);
            record.LocalPath = savePath;

            // 流式下载写入文件
            using var contentStream = await response.Content.ReadAsStreamAsync(internalCts.Token);
            using var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 8192, useAsync: true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;
            var lastReportTime = DateTime.UtcNow;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, internalCts.Token)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, internalCts.Token);
                totalRead += bytesRead;

                // 限流: 每 200ms 只上报一次进度，避免 UI 刷新过于频繁
                var now = DateTime.UtcNow;
                if ((now - lastReportTime).TotalMilliseconds >= 200)
                {
                    var percent = totalBytes > 0 ? (double)totalRead / totalBytes * 100 : -1;
                    record.Progress = percent > 0 ? percent : 0;
                    onProgress?.Report((record.Progress, totalRead, totalBytes));
                    lastReportTime = now;
                }
            }

            // 完成
            record.State = DownloadState.Completed;
            record.CompletedTime = DateTime.Now;
            record.Progress = 100;
            onProgress?.Report((100, totalRead, totalRead));
            LogHelper.Info($"[下载服务] 下载完成: {record.FileName}, 大小={totalRead} 字节, 路径={savePath}");
        }
        catch (OperationCanceledException)
        {
            record.State = DownloadState.Cancelled;
            record.ErrorMessage = "下载已取消";
            LogHelper.Warn($"[下载服务] 下载取消: {record.FileName}");
            // 删除未完成的文件
            TryDeleteFile(record.LocalPath);
        }
        catch (Exception ex)
        {
            record.State = DownloadState.Failed;
            record.ErrorMessage = ex.Message;
            LogHelper.Error($"[下载服务] 下载失败: {record.FileName}", ex);
            // 删除未完成的文件
            TryDeleteFile(record.LocalPath);
        }
        finally
        {
            _cancellations.TryRemove(record.Id, out _);
            internalCts.Dispose();
        }
    }

    public void CancelDownload(string recordId)
    {
        if (_cancellations.TryGetValue(recordId, out var cts))
        {
            LogHelper.Info($"[下载服务] 请求取消下载: {recordId}");
            cts.Cancel();
        }
    }

    /// <summary>
    /// 获取下载文件保存目录（软件目录 Downloads 文件夹，见 <see cref="AppPaths"/>）。
    /// </summary>
    public string GetDownloadDirectory() => AppPaths.Downloads;

    private static string GetSavePath(string directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "download";

        var basePath = Path.Combine(directory, fileName);
        if (!File.Exists(basePath))
            return basePath;

        // 重名处理: file.txt -> file (1).txt
        var dirName = Path.GetDirectoryName(basePath) ?? directory;
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var counter = 1;

        string newPath;
        do
        {
            newPath = Path.Combine(dirName, $"{nameWithoutExt} ({counter}){ext}");
            counter++;
        } while (File.Exists(newPath));

        return newPath;
    }

    private static string GetFileNameFromResponse(HttpResponseMessage response, string url)
    {
        // 1) 优先从 Content-Disposition 头部获取（filename* 支持 RFC 5987 的 UTF-8 文件名）
        // 注意：FileNameStar 是原始编码值，需要 URL 解码；
        //       FileName 已由 ContentDispositionHeaderValue 自动解码，直接使用即可。
        var contentDisposition = response.Content.Headers.ContentDisposition;
        if (contentDisposition is not null)
        {
            var headerName = contentDisposition.FileNameStar;
            if (!string.IsNullOrWhiteSpace(headerName))
            {
                var fileName = CleanFileName(Uri.UnescapeDataString(headerName));
                if (!string.IsNullOrWhiteSpace(fileName))
                    return fileName;
            }
            // FileNameStar 不存在时使用 FileName（已被 .NET 自动解码）
            if (!string.IsNullOrWhiteSpace(contentDisposition.FileName))
            {
                var fileName = CleanFileName(contentDisposition.FileName);
                if (!string.IsNullOrWhiteSpace(fileName))
                    return fileName;
            }
        }

        // 2) 从 URL 中提取文件名（兼容带查询参数与无扩展名的下载接口）
        try
        {
            var uri = new Uri(url);
            var name = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                var fileName = CleanFileName(Uri.UnescapeDataString(name));
                if (!string.IsNullOrWhiteSpace(fileName))
                    return fileName;
            }
        }
        catch { }

        return "download";
    }

    /// <summary>
    /// 判断响应是否为"HTML 页面而非文件"。
    /// 当响应为 text/html 且 URL 路径不指向网页型扩展名时判定为错误页/登录页。
    /// </summary>
    private static bool IsHtmlErrorResponse(HttpResponseMessage response, string url)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrEmpty(mediaType)) return false;
        if (!mediaType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)) return false;

        // URL 本身指向 .html/.htm 页面（如静态站点的直接下载），则视为正常
        try
        {
            var path = new Uri(url).AbsolutePath.ToLowerInvariant();
            if (path.EndsWith(".html", StringComparison.Ordinal) ||
                path.EndsWith(".htm", StringComparison.Ordinal))
            {
                return false;
            }
        }
        catch
        {
            // URL 解析失败时按错误处理
        }
        return true;
    }

    /// <summary>
    /// 清理文件名中的非法字符并限制长度，避免写入失败或路径穿越。
    /// </summary>
    private static string CleanFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "download";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = new System.Collections.Generic.List<char>(fileName.Length);
        foreach (var c in fileName)
        {
            chars.Add(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        var cleaned = new string(chars.ToArray()).Trim();
        if (cleaned.Length == 0) return "download";
        if (cleaned.Length > 150) cleaned = cleaned[..150]; // 限制长度，避免路径过长

        return cleaned;
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[下载服务] 清理未完成文件失败: {path}, {ex.Message}");
        }
    }
}
