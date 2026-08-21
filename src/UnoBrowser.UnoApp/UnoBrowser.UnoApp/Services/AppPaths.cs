using System;
using System.IO;

namespace UnoBrowser.UnoApp.Services;

/// <summary>
/// 软件数据目录统一管理。
/// 所有数据都存放在"软件目录"下，各功能各自建立独立文件夹：
///   {Root}/
///     config/            —— 配置文件 (settings.json)
///     Bugs/              —— 日志文件
///     Downloads/         —— 下载的文件
///     DownloadHistory/   —— 下载历史记录
///     WebView2/          —— WebView2 浏览器数据
/// 软件目录：
///   - Android: 应用专属外部存储（用户可通过文件管理器访问），不可用时回退内部存储
///   - 桌面:    程序所在目录（便携式，数据随 exe 走）；程序目录不可写时回退 %LocalAppData%/UnoBrowser
/// </summary>
public static class AppPaths
{
    /// <summary>软件目录根路径。Android 为 files 根，桌面为程序所在目录（或 LocalAppData/UnoBrowser 回退）。</summary>
    public static string Root { get; }

    /// <summary>配置文件目录（config）。</summary>
    public static string Config { get; }

    /// <summary>日志目录（Bugs）。</summary>
    public static string Bugs { get; }

    /// <summary>下载文件目录（Downloads）。</summary>
    public static string Downloads { get; }

    /// <summary>下载历史目录（DownloadHistory）。</summary>
    public static string DownloadHistory { get; }

    /// <summary>浏览历史目录（BrowsingHistory）。</summary>
    public static string BrowsingHistory { get; }

    /// <summary>WebView2 浏览器数据目录（WebView2）。</summary>
    public static string WebView2 { get; }

    static AppPaths()
    {
        Root = GetRootDirectory();
        Config = CreateDirectory(Path.Combine(Root, "config"));
        Bugs = CreateDirectory(Path.Combine(Root, "Bugs"));
        Downloads = CreateDirectory(Path.Combine(Root, "Downloads"));
        DownloadHistory = CreateDirectory(Path.Combine(Root, "DownloadHistory"));
        BrowsingHistory = CreateDirectory(Path.Combine(Root, "BrowsingHistory"));
        WebView2 = CreateDirectory(Path.Combine(Root, "WebView2"));
    }

    private static string GetRootDirectory()
    {
#if ANDROID
        return GetAndroidStoragePath() ??
               Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
#else
        // 桌面端：优先使用程序所在目录（便携式数据目录），
        // 程序目录不可写时（如安装到 Program Files）回退到 %LocalAppData%/UnoBrowser。
        var appDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(appDir))
        {
            try
            {
                // 探测可写性：尝试创建 config 子目录
                Directory.CreateDirectory(Path.Combine(appDir, "config"));
                return appDir;
            }
            catch
            {
                // 程序目录不可写，回退到 LocalAppData
            }
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnoBrowser");
#endif
    }

#if ANDROID
    /// <summary>
    /// 获取 Android 应用专属外部存储目录（用户可通过文件管理器访问）。
    /// 返回 null 时回退到内部存储。
    /// </summary>
    private static string? GetAndroidStoragePath()
    {
        try
        {
            var context = Android.App.Application.Context;
            var externalDir = context?.GetExternalFilesDir(null);
            if (externalDir?.AbsolutePath is { } path && !string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }
        catch
        {
            // 获取外部存储失败，回退到内部存储
        }
        return null;
    }
#endif

    private static string CreateDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // 创建失败不影响根路径使用
        }
        return path;
    }
}
