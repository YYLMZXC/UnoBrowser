using System;
using System.Threading.Tasks;
using UnoBrowser.UnoApp.Models;

namespace UnoBrowser.UnoApp.Services;

public interface IBrowserProvider
{
    event EventHandler<string>? AddressChanged;
    event EventHandler<string>? TitleChanged;
    event EventHandler<bool>? LoadingStateChanged;
    event EventHandler<DownloadRequestedEventArgs>? DownloadRequested;
    event EventHandler? NavigationHistoryChanged;

    string CurrentUrl { get; }
    string CurrentTitle { get; }
    bool IsLoading { get; }
    bool CanGoBack { get; }
    bool CanGoForward { get; }

    object CreateBrowserControl();
    void Initialize(string startUrl);
    void Navigate(string url);
    void Reload();
    void GoBack();
    void GoForward();

    /// <summary>设置浏览器用户代理标识平台。</summary>
    void SetUserAgent(UserAgentPlatform platform);

    /// <summary>获取当前 WebView 的 Cookie 字符串，用于下载鉴权。</summary>
    Task<string> GetCookiesAsync(string url);

    /// <summary>当应用从后台恢复时调用，刷新页面以检测登录状态。</summary>
    void HandleAppResumed();
}
