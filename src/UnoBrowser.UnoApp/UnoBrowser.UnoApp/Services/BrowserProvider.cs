using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using UnoBrowser.UnoApp.Models;

// 桌面端 WebView2 持久化缓存支持
#if __SKIA__
extern alias WpfWebView;
#endif

// Android 平台：自定义 URL scheme 拦截
#if __ANDROID__
using Android.Content;
#endif

namespace UnoBrowser.UnoApp.Services;

/// <summary>
/// 基于 Uno Platform 跨平台 WebView2 的浏览器实现。
/// Uno 将 WebView2 映射为各平台原生浏览器：
/// - Windows: Edge WebView2
/// - Android: Android WebView
/// - iOS: WKWebView
/// - Desktop Skia: Uno 模拟实现
///
/// 正确生命周期：
/// 1. 创建 WebView2 控件对象，加入可视化树
/// 2. 调用 EnsureCoreWebView2Async() 等待内核初始化完成
/// 3. 内核就绪后才允许导航（Source = / CoreWebView2.Navigate）
///
/// 注意：Loaded 事件仅代表 UI 控件入树，不代表 CoreWebView2 内核就绪。
/// </summary>
public class BrowserProvider : IBrowserProvider
{
    private WebView2? _webView;
    private string _currentUrl = string.Empty;
    private string _currentTitle = string.Empty;
    private bool _isLoading;
    private bool _canGoBack;
    private bool _canGoForward;
    private string? _pendingNavigateUrl;
    private bool _isReady;
    private UserAgentPlatform _userAgentPlatform = UserAgentPlatform.Auto;

    // 幂等标志：保证事件处理器只注册一次。
    // 背景：CoreWebView2Initialized 事件与 EnsureCoreWebView2Async 完成后是两条初始化路径，
    // 都会触发注册；若无幂等保护，DownloadStarting / NewWindowRequested / WebMessageReceived
    // 等事件会被重复订阅，导致下载请求被触发多次等业务 bug。
    private bool _isNewWindowHandlerRegistered;
    private bool _isDesktopDownloadHandlerRegistered;
    private bool _isMobileDownloadHandlerRegistered;
    private bool _isWebMessageHandlerRegistered;
    private bool _isCoreHttpStatusLogRegistered;

    /// <summary>已成功应用的 UA 平台，避免重复设置（性能冗余）。</summary>
    private UserAgentPlatform? _appliedUserAgentPlatform;

    /// <summary>幂等标志：Client-Hints 请求头剥离只注册一次。</summary>
    private bool _isClientHintsFilterRegistered;

    /// <summary>已注册为"每个新文档自动注入"的 UA 伪装脚本（幂等去重）。</summary>
    private string? _injectedDocumentScript;

    /// <summary>CoreWebView2 内核初始化超时时间。</summary>
    private static readonly TimeSpan CoreInitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>上一个有效的 HTTP/HTTPS 页面 URL，用于 scheme 跳转后恢复黑屏。</summary>
    private string _lastKnownGoodUrl = string.Empty;

    /// <summary>是否正在从 scheme 跳转恢复（防止恢复期间的递归处理）。</summary>
    private bool _isRestoringFromScheme;

    // 移动端平台标志：Android WebView / iOS WKWebView 行为与桌面 Edge WebView2 不同
    // 两者都是 Skia 渲染层上的原生覆盖视图，需特殊处理布局和导航时序
    private static readonly bool IsIOSPlatform = OperatingSystem.IsIOS();
    private static readonly bool IsAndroidPlatform = OperatingSystem.IsAndroid();
    private static readonly bool IsMobilePlatform = IsIOSPlatform || IsAndroidPlatform;

    public event EventHandler<string>? AddressChanged;
    public event EventHandler<string>? TitleChanged;
    public event EventHandler<bool>? LoadingStateChanged;
    public event EventHandler<DownloadRequestedEventArgs>? DownloadRequested;
    public event EventHandler? NavigationHistoryChanged;

    public string CurrentUrl => _currentUrl;
    public string CurrentTitle => _currentTitle;
    public bool IsLoading => _isLoading;
    public bool CanGoBack => _canGoBack;
    public bool CanGoForward => _canGoForward;

    public object CreateBrowserControl()
    {
        LogHelper.Info("[浏览器] CreateBrowserControl - 正在创建 WebView2 控件");
        _webView = new WebView2();
        _isReady = false;

        // 确保 WebView2 拉伸填满父容器
        _webView.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
        _webView.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch;

        LogHelper.Info($"[浏览器] WebView2 类型: {_webView.GetType().FullName}, 程序集: {_webView.GetType().Assembly.GetName().FullName}");

        // Loaded 仅用于触发内核初始化，不直接执行业务导航
        _webView.Loaded += OnWebViewLoaded;

        // iOS: 监听尺寸变化，确保 WKWebView 获得正确的 frame
        _webView.SizeChanged += (sender, e) =>
        {
            var wv = sender as WebView2;
            LogHelper.Info($"[浏览器] SizeChanged: ActualWidth={wv?.ActualWidth}, ActualHeight={wv?.ActualHeight}, Width={wv?.Width}, Height={wv?.Height}, DesiredSize={wv?.DesiredSize}");
        };

        // CoreWebView2Initialized 事件
        _webView.CoreWebView2Initialized += (sender, e) =>
        {
            if (e.Exception is not null)
            {
                LogHelper.Error($"[浏览器] CoreWebView2Initialized 初始化失败: {e.Exception.GetType().Name}: {e.Exception.Message}", e.Exception);
                return;
            }
            LogHelper.Info("[浏览器] CoreWebView2Initialized 成功 - 运行时已完全就绪");
            LogHelper.Info($"[浏览器] CoreWebView2 类型: {sender.CoreWebView2?.GetType().FullName ?? "null"}");
            RegisterNewWindowHandler();
            RegisterDownloadHandler();
            ApplyUserAgent();
        };

		_webView.NavigationStarting += (sender, args) =>
		{
			var url = args.Uri?.ToString() ?? string.Empty;

#if __ANDROID__
			// 拦截非 http/https 自定义 URL scheme（如 wtloginmqq://、mqq:// 等）
			// Android WebView 默认丢弃这类导航，需通过 Intent 跳转到对应 App
			if (!string.IsNullOrEmpty(url) && TryHandleCustomScheme(url))
			{
				args.Cancel = true;
				// 自定义 scheme 跳转（如 QQ 登录 wtloginmqq://）后，Android WebView
				// 通常会因为导航被中途取消而变为空白页。
				// 延迟恢复到上一个有效的 HTTP 页面，防止黑屏。
				if (!_isRestoringFromScheme)
				{
					_ = RestoreAfterSchemeRedirectAsync();
				}
				return;
			}
#endif

			// 记录正常的 HTTP/HTTPS 导航 URL，用于 scheme 跳转后页面恢复
			if (!string.IsNullOrEmpty(url) &&
				(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
				 url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
			{
				_lastKnownGoodUrl = url;
			}

			_isLoading = true;
			_currentUrl = url;
			LogHelper.Info($"[浏览器] 导航开始 -> {_currentUrl}");
			AddressChanged?.Invoke(this, _currentUrl);
			LoadingStateChanged?.Invoke(this, true);
		};

        _webView.NavigationCompleted += (sender, args) =>
        {
            _isLoading = false;

            // 注意：WebView2 导航成功时 WebErrorStatus 恒为 Unknown（正常行为），
            // 不能拿 WebErrorStatus 判断成败，仅在失败时输出它做诊断。
            if (args.IsSuccess)
            {
                LogHelper.Info("[浏览器] 导航完成 成功");
            }
            else
            {
                LogHelper.Warn($"[浏览器] 导航完成 失败, WebErrorStatus={args.WebErrorStatus}");
            }

            try
            {
                if (sender.CoreWebView2 is not null)
                {
                    _currentTitle = sender.CoreWebView2.DocumentTitle ?? string.Empty;
                    _canGoBack = sender.CoreWebView2.CanGoBack;
                    _canGoForward = sender.CoreWebView2.CanGoForward;
                    NavigationHistoryChanged?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                LogHelper.Error("[浏览器] 读取文档标题失败", ex);
            }
            TitleChanged?.Invoke(this, _currentTitle);
            LoadingStateChanged?.Invoke(this, false);

            // 页面加载完成后注入资源失败钩子，上报子资源（图片/脚本等）加载失败
            _ = InjectResourceErrorHookAsync();
        };

        // 诊断：CoreWebView2 属性访问
        try
        {
            var cv = _webView.CoreWebView2;
            LogHelper.Info($"[浏览器] CoreWebView2 属性(初始化前): {(cv is null ? "null" : cv.GetType().FullName)}");
        }
        catch (Exception ex)
        {
            LogHelper.Info($"[浏览器] CoreWebView2 属性(初始化前)访问异常: {ex.GetType().Name}: {ex.Message}");
        }

        LogHelper.Info($"[浏览器] CreateBrowserControl 完成, _isReady={_isReady}");
        return _webView;
    }

    private void OnWebViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_webView is null) return;
        _webView.Loaded -= OnWebViewLoaded;

        LogHelper.Info("[浏览器] WebView2.Loaded - 控件已挂入可视化树，开始初始化内核");
        _ = InitializeCoreWebView2Async();
    }

    private async Task InitializeCoreWebView2Async()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LogHelper.Info($"[浏览器] 正在调用 EnsureCoreWebView2Async... (iOS={IsIOSPlatform}, Android={IsAndroidPlatform})");

            // 初始化超时保护：内核长时间无响应时避免页面永久空白且无日志
            var initTask = InitializeWebViewCoreAsync();
            var completedTask = await Task.WhenAny(initTask, Task.Delay(CoreInitTimeout));
            if (completedTask != initTask)
            {
                sw.Stop();
                LogHelper.Error($"[浏览器] CoreWebView2 初始化超时（{CoreInitTimeout.TotalSeconds} 秒），放弃后续初始化。请检查 WebView2 Runtime 是否安装或可用。");
                return;
            }
            await initTask; // 初始化内部若抛异常，在此传播到下方 catch

            sw.Stop();
            LogHelper.Info($"[浏览器] EnsureCoreWebView2Async 完成，耗时 {sw.ElapsedMilliseconds}ms");

            // 内核就绪：先置位，供后续注册/UA 方法判断（此时 CoreWebView2 才是真实内核而非占位对象）
            _isReady = true;

            ApplyPerformanceSettings();
            RegisterCoreHttpStatusLog();
            RegisterNewWindowHandler();
            RegisterDownloadHandler();
            ApplyUserAgent();

            if (IsMobilePlatform)
            {
                var platform = IsAndroidPlatform ? "Android" : "iOS";
                LogHelper.Info($"[浏览器] {platform} 原生 WebView 初始化完成: ActualWidth={_webView.ActualWidth}, ActualHeight={_webView.ActualHeight}");
            }

            try
            {
                var cv = _webView.CoreWebView2;
                LogHelper.Info($"[浏览器] CoreWebView2 初始化后状态: {(cv is null ? "null" : $"类型={cv.GetType().FullName}, 可以导航")}");
                if (cv is not null)
                {
                    LogHelper.Info($"[浏览器] CoreWebView2.Settings={(cv.Settings is null ? "null" : "正常")}");
                }
            }
            catch (Exception ex2)
            {
                LogHelper.Error($"[浏览器] 初始化后检查 CoreWebView2 失败: {ex2.Message}", ex2);
            }

            if (_pendingNavigateUrl is not null)
            {
                var url = _pendingNavigateUrl;
                _pendingNavigateUrl = null;
                LogHelper.Info($"[浏览器] 执行挂起的导航 -> {url}");
                DoNavigate(url);
            }
            else
            {
                LogHelper.Info("[浏览器] 内核就绪，无挂起的导航");
            }
        }
        catch (Exception ex)
        {
            LogHelper.Error($"[浏览器] CoreWebView2 初始化失败: {ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 执行 WebView2 内核初始化（不同平台路径不同）。
    /// 抽离为独立方法以便在 InitializeCoreWebView2Async 中统一加超时控制。
    /// </summary>
    private async Task InitializeWebViewCoreAsync()
    {
#if __SKIA__
        if (OperatingSystem.IsWindows())
        {
            await InitializeWithUserDataFolderAsync();
            return;
        }
#endif
        // 注：EnsureCoreWebView2Async 在不同平台返回类型不同（桌面为 Task，移动端为 IAsyncAction），
        // 统一用 await 兼容
        await _webView!.EnsureCoreWebView2Async();
    }

#if __SKIA__
    private async Task InitializeWithUserDataFolderAsync()
    {
        try
        {
            var userDataFolder = AppPaths.WebView2;
            System.IO.Directory.CreateDirectory(userDataFolder);

            LogHelper.Info($"[浏览器] 正在创建 CoreWebView2Environment (userDataFolder={userDataFolder})");
            var env = await WpfWebView::Microsoft.Web.WebView2.Core.CoreWebView2Environment
                .CreateAsync(userDataFolder: userDataFolder);
            await _webView!.EnsureCoreWebView2Async(env);
            LogHelper.Info($"[浏览器] 使用持久化缓存目录初始化成功: {userDataFolder}");
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[浏览器] 创建自定义 CoreWebView2Environment 失败: {ex.Message}，回退默认初始化");
            await _webView!.EnsureCoreWebView2Async();
        }
    }
#endif

    private void ApplyPerformanceSettings()
    {
        if (_webView?.CoreWebView2?.Settings is null) return;

        try
        {
            var settings = _webView.CoreWebView2.Settings;
            settings.IsWebMessageEnabled = true;
            LogHelper.Info("[浏览器] 性能设置已应用");
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[浏览器] 应用性能设置失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 注册 CoreWebView2 级导航完成监听，仅用于记录主文档 HTTP 状态码异常（4xx/5xx）。
    /// 说明：控件级 NavigationCompleted 参数（WebViewNavigationCompletedEventArgs）不暴露
    /// HTTP 状态码，而 Core 级参数（CoreWebView2NavigationCompletedEventArgs）有 HttpStatusCode。
    /// WebView2 中 404/500 等错误页 IsSuccess 仍为 true，必须借助状态码才能发现。
    /// </summary>
    private void RegisterCoreHttpStatusLog()
    {
        if (!_isReady) return;
        if (_isCoreHttpStatusLogRegistered) return; // 幂等：只注册一次
        if (_webView?.CoreWebView2 is null) return;

        try
        {
            _webView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                var code = args.HttpStatusCode;
                if (code >= 400)
                {
                    LogHelper.Warn($"[浏览器] 主文档 HTTP 状态码异常: {code}");
                }
            };
            _isCoreHttpStatusLogRegistered = true;
            LogHelper.Info("[浏览器] Core 级 HTTP 状态码监听已注册");
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[浏览器] 注册 HTTP 状态码监听失败: {ex.Message}");
        }
    }

    private bool _isHandlingNewWindow; // 防止 NewWindowRequested 重入
    private void RegisterNewWindowHandler()
    {
        // 内核未就绪时 CoreWebView2 可能是占位对象，注册无效；幂等防止重复注册
        if (!_isReady) return;
        if (_isNewWindowHandlerRegistered) return;
        if (_webView?.CoreWebView2 is null) return;

        try
        {
            _webView.CoreWebView2.NewWindowRequested += (sender, args) =>
            {
                if (_isHandlingNewWindow) return; // 防止重入
                _isHandlingNewWindow = true;

                try
                {
                    var newUri = args.Uri;
                    LogHelper.Info($"[浏览器] NewWindowRequested -> {newUri}，拦截并在当前窗口打开");
                    args.Handled = true;

                    // 浏览器式下载判断：URL 带扩展名且不是网页型 → 直接触发下载（无论什么格式）；
                    // 网页型/无扩展名 → 在当前窗口导航，若服务端返回附件（Content-Disposition: attachment）
                    // 或不可渲染类型，CoreWebView2.DownloadStarting 会兜底触发统一下载管线。
                    if (IsLikelyDownloadUrl(newUri))
                    {
                        LogHelper.Info($"[浏览器] NewWindowRequested 检测到下载链接，触发下载: {newUri}");
                        DownloadRequested?.Invoke(this, new DownloadRequestedEventArgs(newUri));
                    }
                    else
                    {
                        Navigate(newUri);
                    }
                }
                finally
                {
                    _isHandlingNewWindow = false;
                }
            };
            _isNewWindowHandlerRegistered = true;
            LogHelper.Info("[浏览器] NewWindowRequested 事件已注册");
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[浏览器] 注册 NewWindowRequested 失败: {ex.Message}");
        }
    }

    public void Initialize(string startUrl)
    {
        LogHelper.Info($"[浏览器] Initialize(startUrl={startUrl}) _isReady={_isReady}");
        _pendingNavigateUrl = startUrl;
        _lastKnownGoodUrl = startUrl;
        if (_webView is not null && _isReady)
        {
            _pendingNavigateUrl = null;
            DoNavigate(startUrl);
        }
        else
        {
            LogHelper.Info("[浏览器] 初始化延迟 - 等待 CoreWebView2 内核就绪");
        }
    }

    public void Navigate(string url)
    {
        LogHelper.Info($"[浏览器] Navigate(url={url}) _isReady={_isReady}");
        _currentUrl = url;
        if (_webView is not null && _isReady)
        {
            DoNavigate(url);
        }
        else
        {
            LogHelper.Info("[浏览器] 导航延迟 - 等待 CoreWebView2 内核就绪");
            _pendingNavigateUrl = url;
        }
    }

    public void Reload()
    {
        LogHelper.Info("[浏览器] 请求刷新页面");
        _webView?.Reload();
        LogHelper.Info("[浏览器] Reload 已调用");
    }

    public void GoBack()
    {
        LogHelper.Info("[浏览器] 请求后退");
        if (_webView?.CoreWebView2 is not null && _canGoBack)
        {
            _webView.CoreWebView2.GoBack();
        }
    }

    public void GoForward()
    {
        LogHelper.Info("[浏览器] 请求前进");
        if (_webView?.CoreWebView2 is not null && _canGoForward)
        {
            _webView.CoreWebView2.GoForward();
        }
    }

    private void DoNavigate(string url)
    {
        if (_webView is null) return;

        LogHelper.Info($"[浏览器] DoNavigate -> {url} (iOS={IsIOSPlatform}, Android={IsAndroidPlatform})");

        // 移动端：尺寸检查
        if (IsMobilePlatform && (_webView.ActualWidth <= 0 || _webView.ActualHeight <= 0))
        {
            var platform = IsAndroidPlatform ? "Android" : "iOS";
            LogHelper.Warn($"[浏览器] {platform}: WebView 尺寸为 {_webView.ActualWidth}x{_webView.ActualHeight}，延迟导航");
            _pendingNavigateUrl = url;
            _ = DeferredMobileNavigateAsync(url);
            return;
        }

        try
        {
            if (IsMobilePlatform)
            {
                LogHelper.Info("[浏览器] 移动端: 使用 Source 属性导航");
                // 注意：Uno WebView2 的 Source setter 在 Android 上会触发原生 WebView.loadUrl。
                // 这里不能再额外调用 CoreWebView2.Navigate，否则同一个 URL 会被 loadUrl 两次，
                // 导致网页重复加载（页面闪烁、资源重复请求、状态被重置）。
                // 若 Source 导航确实失败，由 VerifyMobileNavigationAsync 在 1 秒后兜底重试。
                _webView.Source = new Uri(url);
            }
            else if (_webView.CoreWebView2 is not null)
            {
                LogHelper.Info("[浏览器] 桌面: CoreWebView2 可用，使用 CoreWebView2.Navigate()");
                _webView.CoreWebView2.Navigate(url);
            }
            else
            {
                LogHelper.Info("[浏览器] CoreWebView2 为空，使用 Source 属性");
                _webView.Source = new Uri(url);
            }

            // 桌面端使用 CoreWebView2.Navigate() 时，控件 Source 属性不会立即同步，
            // 此时读取会得到空值，容易误导排查。仅在移动端（刚设置过 Source）记录。
            if (IsMobilePlatform)
            {
                LogHelper.Info($"[浏览器] 导航后 Source={_webView.Source?.ToString() ?? "null"}");
            }

            if (IsMobilePlatform)
            {
                _ = VerifyMobileNavigationAsync(url);
            }
        }
        catch (Exception ex)
        {
            LogHelper.Error($"[浏览器] 导航失败 URL={url}: {ex.GetType().Name}: {ex.Message}", ex);
            LogHelper.Info("[浏览器] 回退到系统浏览器");
            SystemBrowserProvider.OpenUrl(url);
        }
    }

    private async Task DeferredMobileNavigateAsync(string url)
    {
        var platform = IsAndroidPlatform ? "Android" : "iOS";
        var maxWait = 3000;
        var elapsed = 0;

        while (elapsed < maxWait && _webView is not null)
        {
            await Task.Delay(200);
            elapsed += 200;

            if (_webView.ActualWidth > 0 && _webView.ActualHeight > 0)
            {
                LogHelper.Info($"[浏览器] {platform}: 尺寸就绪 ({elapsed}ms, {_webView.ActualWidth}x{_webView.ActualHeight})，执行延迟导航");
                _pendingNavigateUrl = null;
                DoNavigate(url);
                return;
            }
        }

        LogHelper.Warn($"[浏览器] {platform}: 延迟导航超时 ({maxWait}ms)，强制尝试");
        _pendingNavigateUrl = null;
        try { _webView!.Source = new Uri(url); } catch { }
    }

    private async Task VerifyMobileNavigationAsync(string url)
    {
        try
        {
            await Task.Delay(1000);
            if (_webView is null) return;

            var platform = IsAndroidPlatform ? "Android" : "iOS";
            var currentSource = _webView.Source?.ToString();
            LogHelper.Info($"[浏览器] {platform} 导航验证: 期望={url}, 实际Source={currentSource ?? "null"}, IsLoading={_isLoading}");

            if (currentSource != url && !_isLoading)
            {
                LogHelper.Warn($"[浏览器] {platform}: 导航似乎未生效，重试导航");
#if __ANDROID__
                // Android 兜底：使用原生 Navigate 重试（避免再次设置 Source 造成重复导航）。
                // 仅在 Source 导航确实未生效时才走到这里。
                try
                {
                    if (_webView.CoreWebView2 is not null)
                    {
                        _webView.CoreWebView2.Navigate(url);
                        return;
                    }
                }
                catch (Exception ex2)
                {
                    LogHelper.Warn($"[浏览器] Android 导航重试(Navigate)失败: {ex2.Message}");
                }
#endif
                _webView.Source = new Uri(url);
            }
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[浏览器] 移动端导航验证异常: {ex.Message}");
        }
    }

    // =============================================================================
    // 用户代理（User-Agent）设置
    // =============================================================================

    public void SetUserAgent(UserAgentPlatform platform)
    {
        LogHelper.Info($"[浏览器] SetUserAgent: {platform}");
        _userAgentPlatform = platform;
        ApplyUserAgent();
    }

    /// <summary>
    /// 将 UA 设置应用到 WebView。
    /// 桌面端: 直接设置 CoreWebView2.Settings.UserAgent，并剥离 Client-Hints 请求头（Sec-CH-UA-*）。
    /// 移动端: 注入 JS 覆盖 navigator.userAgent / navigator.userAgentData（Uno WebView2 可能不暴露原生 UA 设置）。
    /// 说明：只改 User-Agent 字符串不够——Chromium 内核还会通过 Client-Hints 请求头
    /// （Sec-CH-UA-Platform / Sec-CH-UA-Mobile 等）和 navigator.userAgentData 上报真实平台，
    /// 网站据此即可识破伪装，因此必须一并覆盖。
    /// </summary>
    private void ApplyUserAgent()
    {
        if (_webView is null) return;

        // 内核未就绪时 CoreWebView2 可能是占位对象，设置无效；等就绪后统一应用一次
        if (!_isReady) return;

        // 已为当前平台成功应用过 UA，跳过避免重复设置（性能冗余）
        if (_appliedUserAgentPlatform == _userAgentPlatform) return;

        var ua = GetUserAgentString(_userAgentPlatform);
        LogHelper.Info($"[浏览器] ApplyUserAgent 平台={_userAgentPlatform}, UA={(ua is null ? "跟随系统" : ua)}");

        // 桌面端：直接设置 WebView2 的 UA
        if (!IsMobilePlatform && _webView.CoreWebView2?.Settings is not null)
        {
            // 已为当前平台应用过（无论原生设置是否成功，请求头改写都已注册）
            _appliedUserAgentPlatform = _userAgentPlatform;

            // 原生 UA 设置（Uno 的 CoreWebView2Settings.UserAgent 标记为 not implemented，
            // 运行时可能抛 NotImplementedException 或静默无效，因此独立 try-catch 降级）
            try
            {
                _webView.CoreWebView2.Settings.UserAgent = ua;
                LogHelper.Info("[浏览器] UA 已通过 CoreWebView2.Settings 设置");
            }
            catch (Exception ex)
            {
                LogHelper.Warn($"[浏览器] CoreWebView2.Settings.UserAgent 设置失败（Uno 未实现，改用请求头改写）: {ex.Message}");
            }

            // 跟随系统：UA 置空恢复默认，同时解除 JS 层伪装覆盖
            if (ua is null)
            {
                _ = RestoreUserAgentJsAsync();
                return;
            }

            // 无条件注册请求头改写（不依赖 Settings.UserAgent 是否生效）：
            // 1) 改写 User-Agent 请求头 —— 微软文档明确请求级 User-Agent 头会覆盖 Settings 值
            //    （"This property may be overridden if the User-Agent header is set in a request"），
            //    这是 Uno 未实现原生 UA 设置时的可靠兜底；
            // 2) 剥离 Sec-CH-UA-* Client-Hints 头，防止服务端识破真实平台。
            ApplyClientHintsRequestHeaderOverride();

            // JS 层兜底覆盖 navigator.userAgentData 等（WebView2 设置自定义 UA 后并不保证
            // 清除 UA Client Hints，微软文档: "may clear...subject to change"）
            _ = InjectUserAgentAsync(ua, _userAgentPlatform);
            return;
        }

        // 移动端 / 桌面端 UA 设置失败时的兜底：JS 覆盖 navigator.userAgent / userAgentData
        if (ua is not null && _webView.CoreWebView2 is not null)
        {
            _appliedUserAgentPlatform = _userAgentPlatform; // 标记已应用，避免重复注入
            _ = InjectUserAgentAsync(ua, _userAgentPlatform);
        }
    }

    /// <summary>
    /// 注入 UA / Client-Hints 的 JS 覆盖。同时注册：
    /// 1. document-created 脚本 —— 每个新文档加载时自动注入（避免导航后覆盖失效；移动端不支持则忽略）。
    /// 2. 导航完成后重注入 —— 跟随系统（Auto）时注入还原脚本，恢复真实 UA 信息。
    /// </summary>
    private async Task InjectUserAgentAsync(string ua, UserAgentPlatform platform)
    {
        try
        {
            var js = BuildUserAgentSpoofScript(ua, platform);

            if (_webView?.CoreWebView2 is not null)
            {
                // 注入当前文档
                await _webView.CoreWebView2.ExecuteScriptAsync(js);

                // 每个新文档自动注入（可选优化；Uno 移动端未实现此 API 时忽略，不影响整体覆盖）
                try
                {
                    if (_injectedDocumentScript != js)
                    {
                        await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(js);
                        _injectedDocumentScript = js;
                    }
                }
                catch (Exception ex)
                {
                    LogHelper.Warn($"[浏览器] 注册 document-created 脚本失败（降级为导航后重注入）: {ex.Message}");
                }

                // 导航后重注入：处理切换回 Auto 时的还原
                HookUserAgentReinject();

                LogHelper.Info("[浏览器] UA / Client-Hints JS 覆盖已注入");
            }
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[浏览器] UA JS 注入失败: {ex.Message}");
        }
    }

    /// <summary>解除 JS 层 UA 伪装（切回 Auto 时调用），恢复真实 UA 信息。</summary>
    private async Task RestoreUserAgentJsAsync()
    {
        try
        {
            if (_webView?.CoreWebView2 is null) return;

            // 当前文档还原
            await _webView.CoreWebView2.ExecuteScriptAsync(BuildUserAgentRestoreScript());

            // 导航后持续还原（覆盖已注册的 document-created 伪装脚本）
            HookUserAgentReinject();

            LogHelper.Info("[浏览器] UA 伪装已还原为系统默认");
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[浏览器] 还原 UA 失败: {ex.Message}");
        }
    }

    private bool _isUserAgentReinjectHooked;

    /// <summary>注册导航完成后重注入 UA 脚本的钩子（幂等）。</summary>
    private void HookUserAgentReinject()
    {
        if (_isUserAgentReinjectHooked) return;
        if (_webView?.CoreWebView2 is null) return;

        try
        {
            _webView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _ = ReapplyUserAgentJsAsync();
            };
            _isUserAgentReinjectHooked = true;
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[浏览器] 注册 UA 导航重注入失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 每次导航完成后按当前平台重注入 JS 覆盖；Auto 时注入还原脚本。
    /// </summary>
    private async Task ReapplyUserAgentJsAsync()
    {
        try
        {
            if (_webView?.CoreWebView2 is null) return;

            if (_userAgentPlatform == UserAgentPlatform.Auto)
            {
                await _webView.CoreWebView2.ExecuteScriptAsync(BuildUserAgentRestoreScript());
                return;
            }

            var ua = GetUserAgentString(_userAgentPlatform);
            if (ua is not null)
            {
                await _webView.CoreWebView2.ExecuteScriptAsync(BuildUserAgentSpoofScript(ua, _userAgentPlatform));
            }
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[浏览器] 导航后重注入 UA 脚本失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 生成覆盖 UA 及 Client-Hints 的 JS 脚本：
    /// navigator.userAgent / appVersion / platform / userAgentData（brands、mobile、platform、getHighEntropyValues）。
    /// </summary>
    private static string BuildUserAgentSpoofScript(string ua, UserAgentPlatform platform)
    {
        var escapedUa = ua.Replace("'", "\\'");
        var profile = GetClientHintsProfile(platform);
        var isMobile = profile.Mobile;
        var chPlatform = profile.ChPlatform;
        var navPlatform = profile.NavPlatform;
        var arch = profile.Arch;
        var model = profile.Model;
        var platformVersion = profile.PlatformVersion;
        var mobileFlag = isMobile ? "true" : "false";

        return $$"""
        (function() {
            try {
                var ua = '{{escapedUa}}';
                Object.defineProperty(navigator, 'userAgent', { get: function() { return ua; } });
                Object.defineProperty(navigator, 'appVersion', { get: function() { return ua; } });
                Object.defineProperty(navigator, 'platform', { get: function() { return '{{navPlatform}}'; } });

                // Client-Hints: navigator.userAgentData
                var brands = [
                    { brand: 'Not_A Brand', version: '8' },
                    { brand: 'Chromium', version: '120' },
                    { brand: 'Google Chrome', version: '120' }
                ];
                var fullVersionList = [
                    { brand: 'Not_A Brand', version: '8.0.0.0' },
                    { brand: 'Chromium', version: '120.0.6099.109' },
                    { brand: 'Google Chrome', version: '120.0.6099.109' }
                ];
                var uaData = {
                    brands: brands,
                    mobile: {{mobileFlag}},
                    platform: '{{chPlatform}}',
                    getHighEntropyValues: function(hints) {
                        return Promise.resolve({
                            architecture: '{{arch}}',
                            bitness: '64',
                            brands: brands,
                            fullVersionList: fullVersionList,
                            mobile: {{mobileFlag}},
                            model: '{{model}}',
                            platform: '{{chPlatform}}',
                            platformVersion: '{{platformVersion}}',
                            uaFullVersion: '120.0.6099.109',
                            wow64: false
                        });
                    }
                };
                Object.defineProperty(navigator, 'userAgentData', {
                    get: function() { return uaData; }
                });
            } catch(e) {}
        })();
        """;
    }

    /// <summary>
    /// 各伪装平台的 Client-Hints 参数表，保证 navigator.userAgentData / navigator.platform
    /// 与 UA 字符串、Sec-CH-UA-* 头一致，避免网站从 JS 侧识破平台伪装。
    /// </summary>
    private static (bool Mobile, string ChPlatform, string NavPlatform, string Arch, string Model, string PlatformVersion) GetClientHintsProfile(UserAgentPlatform platform)
    {
        return platform switch
        {
            UserAgentPlatform.Mobile => (true, "Android", "Linux armv8l", "arm", "K", "10.0.0"),
            UserAgentPlatform.IPhone => (true, "iPhone", "iPhone", "arm", "iPhone", "16.0.0"),
            UserAgentPlatform.Linux => (false, "Linux", "Linux x86_64", "x86_64", "", "5.15.0"),
            UserAgentPlatform.MacOS => (false, "macOS", "MacIntel", "x86", "", "15.0.0"),
            _ => (false, "Windows", "Win32", "x86", "", "15.0.0") // Desktop 等桌面默认
        };
    }

    /// <summary>
    /// 生成还原脚本：清除 UA / Client-Hints 的 JS 覆盖，恢复系统真实值（切回 Auto 时使用）。
    /// 通过从 Navigator.prototype 恢复原始 descriptor，解除实例上的覆盖。
    /// </summary>
    private static string BuildUserAgentRestoreScript()
    {
        return """
        (function() {
            try {
                var nav = window.navigator;
                var restore = function(key) {
                    try {
                        var desc = Object.getOwnPropertyDescriptor(Navigator.prototype, key);
                        if (desc) { Object.defineProperty(nav, key, desc); }
                    } catch(e) {}
                };
                restore('userAgent');
                restore('appVersion');
                restore('platform');
                restore('userAgentData');
            } catch(e) {}
        })();
        """;
    }

    /// <summary>
    /// 桌面端：拦截所有请求并改写 UA 相关请求头，双保险：
    /// 1) 改写 User-Agent 请求头为伪装值 —— 微软文档明确"请求中显式设置的 User-Agent 头
    ///    会覆盖 Settings.UserAgent"（"This property may be overridden if the User-Agent
    ///    header is set in a request"），因此在 Uno 未实现原生 UA 设置时，这是 HTTP 层
    ///    真正改变浏览器标识的唯一可靠途径；
    /// 2) 剥离 UA Client-Hints 头（Sec-CH-UA-*）—— 仅设置 Settings.UserAgent 时 WebView2
    ///    仍可能基于真实系统发送这些头（微软文档："may clear...subject to change"，不保证清除），
    ///    网站据此即可识破 UA 伪装。
    /// 处理完成后服务端只能依赖 User-Agent 字符串判断，伪装即完整生效。
    /// </summary>
    private void ApplyClientHintsRequestHeaderOverride()
    {
        if (!_isReady) return;
        if (_isClientHintsFilterRegistered) return; // 幂等：只注册一次
        if (_webView?.CoreWebView2 is null) return;
        if (IsMobilePlatform) return; // 移动端原生 WebView 不发送 Sec-CH-UA-*，无需处理

        try
        {
            _webView.CoreWebView2.WebResourceRequested += (_, args) =>
            {
                try
                {
                    var headers = args.Request.Headers;

                    // 改写 User-Agent 请求头（跟随系统 Auto 时 ua 为 null，跳过保持真实）
                    var ua = GetUserAgentString(_userAgentPlatform);
                    if (ua is not null)
                    {
                        headers.SetHeader("User-Agent", ua);
                    }

                    // 移除所有 UA 相关 Client-Hints 头，避免真实平台泄露
                    headers.RemoveHeader("Sec-CH-UA");
                    headers.RemoveHeader("Sec-CH-UA-Arch");
                    headers.RemoveHeader("Sec-CH-UA-Bitness");
                    headers.RemoveHeader("Sec-CH-UA-Full-Version");
                    headers.RemoveHeader("Sec-CH-UA-Full-Version-List");
                    headers.RemoveHeader("Sec-CH-UA-Mobile");
                    headers.RemoveHeader("Sec-CH-UA-Model");
                    headers.RemoveHeader("Sec-CH-UA-Platform");
                    headers.RemoveHeader("Sec-CH-UA-Platform-Version");
                    headers.RemoveHeader("Sec-CH-UA-WOW64");
                }
                catch
                {
                    // 个别头可能为只读，忽略该次请求
                }
            };
            _webView.CoreWebView2.AddWebResourceRequestedFilter("*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
            _isClientHintsFilterRegistered = true;
            LogHelper.Info("[浏览器] UA 请求头改写已注册 (User-Agent + Sec-CH-UA-* 剥离)");
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[浏览器] 注册 UA 请求头改写失败: {ex.Message}");
        }
    }

    private static string? GetUserAgentString(UserAgentPlatform platform)
    {
        return platform switch
        {
            UserAgentPlatform.Desktop => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            UserAgentPlatform.Mobile => "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36",
            UserAgentPlatform.IPhone => "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/120.0.6099.109 Mobile/15E148 Safari/604.1",
            UserAgentPlatform.Linux => "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            UserAgentPlatform.MacOS => "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            _ => null // Auto — 不覆盖
        };
    }

    // =============================================================================
    // Cookie 获取（用于下载鉴权）
    // =============================================================================

    public async Task<string> GetCookiesAsync(string url)
    {
        try
        {
            // 桌面端: CoreWebView2.CookieManager
            if (!IsMobilePlatform && _webView?.CoreWebView2?.CookieManager is not null)
            {
                var cookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync(url);
                var cookieStrings = new System.Collections.Generic.List<string>();
                foreach (var c in cookies)
                {
                    cookieStrings.Add($"{c.Name}={c.Value}");
                }
                var result = string.Join("; ", cookieStrings);
                LogHelper.Info($"[浏览器] 获取了 {cookieStrings.Count} 个 Cookie");
                return result;
            }

            // 移动端：通过 JS 获取 document.cookie
            if (_webView?.CoreWebView2 is not null)
            {
                var cookieStr = await _webView.CoreWebView2.ExecuteScriptAsync("document.cookie");
                if (cookieStr is not null)
                {
                    var trimmed = cookieStr.Trim('"');
                    LogHelper.Info($"[浏览器] JS document.cookie 获取成功，长度={trimmed.Length}");
                    return trimmed;
                }
            }
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[浏览器] 获取 Cookie 失败: {ex.Message}");
        }
        return string.Empty;
    }

    // =============================================================================
    // 下载处理（跨平台统一管线）
    // =============================================================================

    /// <summary>
    /// 注册下载事件处理（跨平台统一下载管线）。
    ///
    /// 策略（浏览器式行为，不依赖白名单扩展名）:
    /// - 桌面端 (WebView2): 拦截 CoreWebView2.DownloadStarting —— 该事件覆盖服务端
    ///   通过 Content-Disposition: attachment / 不可渲染类型发起的任何下载，取消原生
    ///   下载对话框，统一走自定义下载管线；同时提取 ResultFilePath 中的真实文件名。
    /// - Android: 优先注册原生 WebView.DownloadListener（系统级判定，与浏览器一致），
    ///   再叠加 JS 注入 + NavigationStarting 黑名单兜底。
    /// - iOS: JS 注入 + NavigationStarting 黑名单兜底。
    ///   1) 页面加载后注入 JS：拦截任意非网页扩展名链接（任何格式）与带 download 属性的链接
    ///   2) NavigationStarting 中检测下载型 URL（黑名单扩展名 + 下载接口关键词）作为兜底
    ///   3) WebMessageReceived 接收 JS 通知
    /// </summary>
    private void RegisterDownloadHandler()
    {
        if (_webView is null) return;

        try
        {
            // === 桌面端: 拦截 DownloadStarting，取消原生对话框，统一走自定义下载 ===
            if (!IsMobilePlatform)
            {
                RegisterDesktopDownloadHandler();
            }

            // === 移动端: JS 注入 + NavigationStarting + Android 原生 DownloadListener ===
            if (IsMobilePlatform)
            {
                RegisterMobileDownloadHandler();
#if __ANDROID__
                RegisterAndroidDownloadListener();
#endif
            }

            // === 所有平台: WebMessageReceived（JS 通知下载） ===
            RegisterWebMessageHandler();
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[浏览器] 注册下载处理失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 桌面端：拦截 WebView2 原生下载，取消默认对话框，统一走自定义下载管线。
    /// </summary>
    private void RegisterDesktopDownloadHandler()
    {
        // 内核未就绪时 CoreWebView2 可能是占位对象，注册无效；幂等防止重复注册
        if (!_isReady) return;
        if (_isDesktopDownloadHandlerRegistered) return;
        if (_webView?.CoreWebView2 is null) return;

        try
        {
            _webView.CoreWebView2.DownloadStarting += (sender, args) =>
            {
                var dlUrl = args.DownloadOperation.Uri;
                var filePath = args.DownloadOperation.ResultFilePath ?? string.Empty;
                LogHelper.Info($"[浏览器] 桌面端 DownloadStarting 拦截 -> URL={dlUrl}, Path={filePath}");

                // 取消 WebView2 原生下载对话框
                args.Cancel = true;

                // 走统一的自定义下载管线。
                // ResultFilePath 通常来自服务器 Content-Disposition，包含真实文件名，
                // 作为下载文件名提示传递给上层。
                var fileName = GetFileNameFromPath(filePath);
                DownloadRequested?.Invoke(this, new DownloadRequestedEventArgs(dlUrl, fileName));
            };
            _isDesktopDownloadHandlerRegistered = true;
            LogHelper.Info("[浏览器] 桌面端 DownloadStarting 拦截已注册 — 下载将走统一管线");
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[浏览器] 注册 DownloadStarting 失败，回退原生下载: {ex.Message}");
        }
    }

    /// <summary>
    /// 移动端：JS 注入拦截 + NavigationStarting 检测。
    /// </summary>
    private void RegisterMobileDownloadHandler()
    {
        if (!_isReady) return;
        if (_isMobileDownloadHandlerRegistered) return; // 幂等：只注册一次
        _isMobileDownloadHandlerRegistered = true;

        // 每次页面导航完成后注入下载拦截 JS
        _webView!.NavigationCompleted += (sender, args) =>
        {
            _ = InjectDownloadInterceptorAsync();
        };

        // NavigationStarting 中检测下载型 URL（兜底）
        _webView.NavigationStarting += (sender, args) =>
        {
            var url = args.Uri?.ToString();
            if (string.IsNullOrWhiteSpace(url)) return;

            if (IsLikelyDownloadUrl(url))
            {
                args.Cancel = true;
                LogHelper.Info($"[浏览器] 移动端 NavigationStarting 检测到下载链接，拦截 -> {url}");
                DownloadRequested?.Invoke(this, new DownloadRequestedEventArgs(url));
            }
        };

        LogHelper.Info("[浏览器] 移动端下载拦截已注册 (JS注入 + NavigationStarting 检测)");
    }

#if __ANDROID__
    /// <summary>
    /// Android：注册原生 WebView.DownloadListener。
    /// 这是与系统浏览器完全一致的下载判定机制：WebView 在遇到
    /// Content-Disposition: attachment 或不可渲染的 Content-Type 时
    /// 会触发 onDownloadStart，从而覆盖 JS/URL 扩展名方案无法捕获的场景
    /// （如无扩展名的 /download?id=123 动态下载接口、blob 生成的文件等）。
    /// 通过递归查找 Uno WebView2 控件树中的原生 Android.Webkit.WebView 完成注册。
    /// </summary>
    private void RegisterAndroidDownloadListener()
    {
        try
        {
            if (_webView is null) return;
            if (_isAndroidDownloadListenerRegistered) return;

            var native = FindNativeWebView(_webView);
            if (native is not null)
            {
                native.SetDownloadListener(new AndroidDownloadListener(OnAndroidDownloadStart));
                _isAndroidDownloadListenerRegistered = true;
                LogHelper.Info("[浏览器] Android 原生 DownloadListener 已注册");
            }
            else
            {
                LogHelper.Warn("[浏览器] 未找到原生 Android WebView，跳过 DownloadListener 注册");
            }
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[浏览器] 注册 Android DownloadListener 失败: {ex.Message}");
        }
    }

    private bool _isAndroidDownloadListenerRegistered;

    /// <summary>递归查找视图树中的原生 Android.Webkit.WebView。</summary>
    private static Android.Webkit.WebView? FindNativeWebView(object root)
    {
        if (root is Android.Webkit.WebView wv) return wv;
        if (root is Android.Views.ViewGroup vg)
        {
            for (int i = 0; i < vg.ChildCount; i++)
            {
                var child = vg.GetChildAt(i);
                if (child is null) continue;
                var nested = FindNativeWebView(child);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    /// <summary>Android DownloadListener 触发时的回调（发生在 UI 线程）。</summary>
    private void OnAndroidDownloadStart(string url, string userAgent, string contentDisposition, string mimetype, long contentLength)
    {
        try
        {
            LogHelper.Info($"[浏览器] Android DownloadListener 拦截下载: {url} (type={mimetype}, length={contentLength})");
            var fileName = ParseFileNameFromContentDisposition(contentDisposition) ?? GetFileNameFromUrl(url);
            DownloadRequested?.Invoke(this, new DownloadRequestedEventArgs(url, fileName));
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[浏览器] Android DownloadListener 处理下载请求失败: {ex.Message}");
        }
    }

    /// <summary>从 Content-Disposition 响应头解析文件名（支持 filename / filename*）。</summary>
    private static string? ParseFileNameFromContentDisposition(string contentDisposition)
    {
        if (string.IsNullOrWhiteSpace(contentDisposition)) return null;
        try
        {
            var cd = System.Net.Http.Headers.ContentDispositionHeaderValue.Parse(contentDisposition);
            var name = cd.FileNameStar ?? cd.FileName;
            if (!string.IsNullOrWhiteSpace(name))
            {
                name = name.Trim('"');
                name = Uri.UnescapeDataString(name);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        catch
        {
            // 解析失败时返回 null，由调用方从 URL 提取
        }
        return null;
    }

    /// <summary>Android 原生下载监听器适配器。</summary>
    private sealed class AndroidDownloadListener(Action<string, string, string, string, long> onDownloadStart)
        : Java.Lang.Object, Android.Webkit.IDownloadListener
    {
        public void OnDownloadStart(string? url, string? userAgent, string? contentDisposition, string? mimetype, long contentLength)
        {
            onDownloadStart(
                url ?? string.Empty,
                userAgent ?? string.Empty,
                contentDisposition ?? string.Empty,
                mimetype ?? string.Empty,
                contentLength);
        }
    }
#endif

    /// <summary>
    /// 所有平台：监听 WebMessage，处理 JS postMessage 通知的下载请求。
    /// </summary>
    private void RegisterWebMessageHandler()
    {
        if (!_isReady) return;
        if (_isWebMessageHandlerRegistered) return; // 幂等：只注册一次
        if (_webView is null) return;

        _webView.WebMessageReceived += (_, args) =>
        {
            var raw = args.WebMessageAsJson;
            if (string.IsNullOrWhiteSpace(raw)) return;

            var message = raw.Trim('"');
            if (message is null) return;

            if (message.StartsWith("download:", StringComparison.Ordinal))
            {
                // 格式: download:<url>\u0001<fileName>（fileName 可选，来自 <a download="..."> 属性）
                var payload = message["download:".Length..];
                string? fileName = null;
                var sepIndex = payload.IndexOf('\u0001');
                if (sepIndex >= 0)
                {
                    fileName = payload[(sepIndex + 1)..];
                    payload = payload[..sepIndex];
                }
                LogHelper.Info($"[浏览器] JS postMessage 通知下载 -> {payload}" + (fileName is null ? string.Empty : $" (文件名: {fileName})"));
                DownloadRequested?.Invoke(this, new DownloadRequestedEventArgs(payload, fileName));
            }
            else if (message.StartsWith("log:", StringComparison.Ordinal))
            {
                // 页面内资源失败日志（低优先级诊断）
                var logMsg = message["log:".Length..];
                if (logMsg.StartsWith("resource-error:", StringComparison.Ordinal))
                {
                    LogHelper.Warn($"[浏览器] 资源加载失败: {logMsg["resource-error:".Length..]}");
                }
                else if (logMsg.StartsWith("js-error:", StringComparison.Ordinal))
                {
                    LogHelper.Warn($"[浏览器] 页面 JS 错误: {logMsg["js-error:".Length..]}");
                }
            }
        };

        _isWebMessageHandlerRegistered = true;
        LogHelper.Info("[浏览器] WebMessageReceived 已注册");
    }

    /// <summary>
    /// 在当前页面注入下载拦截 JavaScript。
    /// 拦截已知下载扩展名的 &lt;a&gt; 标签点击，通过 postMessage 通知 C# 层。
    /// 使用跨平台兼容的 postMessage 封装，兼容 Uno 各平台 WebView2 桥接。
    /// </summary>
    private async Task InjectDownloadInterceptorAsync()
    {
        if (_webView?.CoreWebView2 is null) return;

        try
        {
            await _webView.CoreWebView2.ExecuteScriptAsync(DownloadInterceptorJs);
            LogHelper.Info("[浏览器] 下载拦截 JS 注入完成");
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[浏览器] JS 注入失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 下载拦截 JavaScript（浏览器式行为）。
    /// 策略：不依赖"白名单扩展名"，而是采用黑名单判断——
    ///   1) 带 download 属性的链接 → 无条件拦截（浏览器语义：download 属性 = 强制下载）；
    ///   2) URL 有扩展名且不是网页型扩展名 → 拦截（任意格式，包括 .scmod2/.kra/.part 等特殊格式）；
    ///   3) 网页型扩展名（.html/.php/.aspx 等）或无扩展名 → 不拦截，交给导航层兜底判断。
    /// 跨平台兼容：优先使用 window.chrome.webview.postMessage（Uno 在桌面端 polyfill），
    /// 同时存在 window.external.notify 作为移动端兜底。
    /// </summary>
    private const string DownloadInterceptorJs = @"
(function() {
    if (window.__scDownloadInterceptorInstalled) return;
    window.__scDownloadInterceptorInstalled = true;

    // 黑名单：网页型扩展名，命中这些的链接一律按网页导航处理，不触发下载
    var webPageExtensions = [
        '.html','.htm','.xhtml','.php','.php3','.php5','.phtml',
        '.aspx','.asp','.jsp','.jspx','.cgi','.ashx','.shtml',
        '.do','.action','.svc','.asax','.axd'
    ];

    // 下载型 URL 关键词（用于无扩展名的下载接口路径，如 /download?id=123）
    var downloadUrlKeywords = /(download|getfile|attachment|downfile|get_file|export|saveas|file_down|downdata|receivefile|fetch\b)/i;

    function getPathPart(url) {
        try { return url.split('?')[0].split('#')[0]; } catch(e) { return url; }
    }

    // 浏览器式下载判断：
    // - 带 download 属性 → 下载（调用方单独处理）
    // - 有扩展名且非网页型 → 下载
    // - 无扩展名 → 启发式关键词
    function isDownloadLink(url) {
        if (!url) return false;
        var path = getPathPart(url).toLowerCase();
        for (var i = 0; i < webPageExtensions.length; i++) {
            if (path.endsWith(webPageExtensions[i])) return false;
        }
        var lastSlash = path.lastIndexOf('/');
        var fileNamePart = lastSlash >= 0 ? path.substring(lastSlash + 1) : path;
        if (fileNamePart.indexOf('.') >= 0) return true; // 有扩展名 → 任意格式都下载
        return downloadUrlKeywords.test(path);           // 无扩展名 → 启发式
    }

    // 从链接提取下载文件名（&lt;a download&gt; 属性优先）
    function getDownloadName(link) {
        try {
            if (link.download) {
                var n = link.download.trim();
                if (n) return n;
            }
        } catch(e) {}
        return '';
    }

    // 跨平台 postMessage 封装。消息格式: download:<url>\u0001<fileName>
    function notifyDownload(url, name) {
        var fullMsg = 'download:' + url + '\u0001' + (name || '');
        try {
            // Uno 桌面端: chrome.webview.postMessage (主要)
            if (window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === 'function') {
                window.chrome.webview.postMessage(fullMsg);
                return;
            }
        } catch(e) {}
        try {
            // Uno 移动端 / 旧版本兼容: external.notify
            if (window.external && typeof window.external.notify === 'function') {
                window.external.notify(fullMsg);
                return;
            }
        } catch(e) {}
        try {
            // Android WebView JavaScriptInterface 兼容
            if (window.__scBridge) {
                window.__scBridge.postMessage(fullMsg);
                return;
            }
        } catch(e) {}
    }

    // 全局点击委托（捕获阶段）：
    // - 文档级委托天然覆盖动态添加的链接，无需 MutationObserver
    // - 带 download 属性 → 无条件拦截（浏览器语义）
    // - 非网页型扩展名（任意格式）→ 拦截
    document.addEventListener('click', function(e) {
        var target = e.target;
        while (target && target !== document) {
            if (target.tagName === 'A' && target.href) {
                var isForcedDownload = target.hasAttribute && target.hasAttribute('download');
                if (isForcedDownload || isDownloadLink(target.href)) {
                    e.preventDefault();
                    e.stopPropagation();
                    notifyDownload(target.href, getDownloadName(target));
                    return false;
                }
                return;
            }
            target = target.parentElement;
        }
    }, true);
})();
";

    /// <summary>
    /// 在当前页面注入资源加载失败监听 JS：
    /// - 子资源（图片/脚本/样式等）加载失败 -> postMessage("log:resource-error:...")
    /// - 页面脚本运行时错误 -> postMessage("log:js-error:...")
    /// C# 侧在 RegisterWebMessageHandler 中解析并记录日志。
    /// </summary>
    private async Task InjectResourceErrorHookAsync()
    {
        if (_webView?.CoreWebView2 is null) return;

        try
        {
            await _webView.CoreWebView2.ExecuteScriptAsync(ResourceErrorHookJs);
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[浏览器] 资源失败钩子 JS 注入失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 资源加载失败监听 JavaScript（低优先级诊断增强）。
    /// 跨平台兼容：优先 window.chrome.webview.postMessage，移动端回退 window.external.notify。
    /// </summary>
    private const string ResourceErrorHookJs = @"
(function() {
    if (window.__scResourceErrorHookInstalled) return;
    window.__scResourceErrorHookInstalled = true;

    function postLog(msg) {
        try {
            if (window.chrome && window.chrome.webview && typeof window.chrome.webview.postMessage === 'function') {
                window.chrome.webview.postMessage(msg);
                return;
            }
        } catch(e) {}
        try {
            if (window.external && typeof window.external.notify === 'function') {
                window.external.notify(msg);
                return;
            }
        } catch(e) {}
    }

    // 子资源加载失败（img/script/link/audio/video 等）
    document.addEventListener('error', function(e) {
        var el = e.target;
        var src = el && (el.src || el.href);
        if (src) postLog('log:resource-error:' + src);
    }, true);

    // 页面脚本运行时错误
    window.addEventListener('error', function(e) {
        var msg = 'log:js-error:' + (e.message || 'unknown') + ' @ ' + (e.filename || '') + ':' + (e.lineno || 0);
        postLog(msg);
    });
})();
";

    /// <summary>
    /// 浏览器式下载 URL 判断（黑名单策略，替代原先的白名单扩展名）。
    /// 规则：
    ///   1. 网页型扩展名（.html/.php/.aspx 等）→ 不是下载；
    ///   2. URL 有扩展名（无论是什么格式，包括 .scmod2/.kra/.part 等特殊格式）→ 下载；
    ///   3. 无扩展名 → 按下载接口关键词启发式判断（如 /download?id=123）。
    /// 作为 JS 注入拦截的兜底方案。
    /// </summary>
    private static readonly string[] WebPageExtensions = new[]
    {
        ".html", ".htm", ".xhtml", ".php", ".php3", ".php5", ".phtml",
        ".aspx", ".asp", ".jsp", ".jspx", ".cgi", ".ashx", ".shtml",
        ".do", ".action", ".svc", ".asax", ".axd"
    };

    private static readonly string[] DownloadUrlKeywords = new[]
    {
        "download", "getfile", "attachment", "downfile", "get_file",
        "export", "saveas", "file_down", "downdata", "receivefile"
    };

    private static bool IsLikelyDownloadUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        Uri? uri;
        try { uri = new Uri(url); }
        catch { return false; }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        var lower = path.ToLowerInvariant();

        // 网页型扩展名 → 不是下载
        foreach (var ext in WebPageExtensions)
        {
            if (lower.EndsWith(ext, StringComparison.Ordinal))
            {
                LogHelper.Info($"[浏览器] IsLikelyDownloadUrl -> {url} (网页型扩展名 {ext}，判定为导航)");
                return false;
            }
        }

        // 有扩展名（任意格式）→ 下载
        var fileName = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(fileName) && fileName.IndexOf('.') >= 0)
        {
            LogHelper.Info($"[浏览器] IsLikelyDownloadUrl -> {url} (非网页扩展名，判定为下载)");
            return true;
        }

        // 无扩展名 → 下载接口关键词启发式
        foreach (var keyword in DownloadUrlKeywords)
        {
            if (lower.Contains(keyword, StringComparison.Ordinal))
            {
                LogHelper.Info($"[浏览器] IsLikelyDownloadUrl -> {url} (命中下载关键词 {keyword})");
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 从完整文件路径中提取文件名；空路径时返回 null。
    /// 用于桌面端 DownloadStarting 的 ResultFilePath（通常源自服务器 Content-Disposition）。
    /// </summary>
    private static string? GetFileNameFromPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        try
        {
            var name = Path.GetFileName(filePath);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从 URL 中提取文件名，用于无服务器文件名提示时的兜底。
    /// 兼容带查询参数 / 无扩展名的下载接口 URL。
    /// </summary>
    public static string GetFileNameFromUrl(string url, string fallback = "download")
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            try
            {
                var name = Path.GetFileName(new Uri(url).AbsolutePath);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch
            {
                // 忽略解析失败，使用兜底名
            }
        }
        return fallback;
    }

#if __ANDROID__
    /// <summary>
    /// Android：判断 URL 是否属于标准 Web scheme（http/https/file/about/data/javascript/blob），
    /// 对于自定义 URL scheme（如 wtloginmqq://、mqq:// 等），通过 Android Intent 跳转到对应 App。
    /// 返回 true 表示已通过 Intent 处理，调用方应取消 WebView 导航。
    /// </summary>
    private static bool TryHandleCustomScheme(string url)
    {
        // 标准 Web 协议由 WebView 自行处理
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 自定义 scheme：通过 Android Intent 跳转到对应 App（QQ、微信等）
        try
        {
            var intent = new Intent(Intent.ActionView);
            intent.SetData(Android.Net.Uri.Parse(url));
            intent.AddFlags(ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
            LogHelper.Info($"[浏览器] 已通过 Intent 打开自定义 scheme: {url}");
            return true;
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[浏览器] 无法处理自定义 scheme {url}: {ex.Message}");
            // 如果系统没有能处理该 scheme 的 App（如未安装 QQ），返回 false
            // 让 WebView 尝试处理（虽然大概率会静默失败，但不影响其他逻辑）
            return false;
        }
    }

    /// <summary>
    /// 自定义 scheme 跳转（如 QQ 登录的 wtloginmqq://）后，
    /// Android WebView 常因导航被中途取消而变为空白页。
    /// 此方法在短暂延迟后重新加载上一个有效页面，防止黑屏。
    /// </summary>
    private async Task RestoreAfterSchemeRedirectAsync()
    {
        _isRestoringFromScheme = true;
        try
        {
            // 短暂延迟，让 WebView 完成内部状态回滚
            await Task.Delay(400);

            if (_webView is null || !_isReady) return;

            // 优先尝试直接刷新当前页面（保持页面状态，不产生额外历史记录）
            if (!string.IsNullOrEmpty(_currentUrl) &&
                (_currentUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 _currentUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                LogHelper.Info($"[浏览器] scheme跳转后刷新当前页面 -> {_currentUrl}");
                _webView.Reload();
            }
            else if (!string.IsNullOrEmpty(_lastKnownGoodUrl))
            {
                // 当前 URL 不可用（如 about:blank），恢复到上一个有效页面
                LogHelper.Info($"[浏览器] scheme跳转后恢复到上一个页面 -> {_lastKnownGoodUrl}");
                DoNavigate(_lastKnownGoodUrl);
            }
            else
            {
                LogHelper.Warn("[浏览器] scheme跳转后无可恢复的有效页面");
            }
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[浏览器] scheme跳转后恢复页面失败: {ex.Message}");
        }
        finally
        {
            _isRestoringFromScheme = false;
        }
    }
#endif

    /// <summary>
    /// 当应用从后台恢复时调用（如用户从 QQ 授权返回）。
    /// 刷新当前页面，使登录页面能够检测到最新的登录状态。
    /// </summary>
    public void HandleAppResumed()
    {
        if (_webView is null || !_isReady) return;

        LogHelper.Info("[浏览器] 应用从后台恢复，刷新页面以检测登录状态");
        Reload();
    }
}
