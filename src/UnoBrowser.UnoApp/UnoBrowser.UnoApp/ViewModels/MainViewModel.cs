using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using UnoBrowser.UnoApp.Services;

namespace UnoBrowser.UnoApp.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly IBrowserProvider _browser;
    private readonly ISettingsService _settingsService;

    // ===================== 浏览器相关 =====================

    private string _currentUrl = "https://test.suancaixianyu.cn/";
    private string _statusText = "就绪";
    private bool _isLoading;
    private string _windowTitle = "Uno浏览器";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CurrentUrl
    {
        get => _currentUrl;
        set { _currentUrl = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    private bool _canGoBack;
    public bool CanGoBack
    {
        get => _canGoBack;
        set { _canGoBack = value; OnPropertyChanged(); }
    }

    private bool _canGoForward;
    public bool CanGoForward
    {
        get => _canGoForward;
        set { _canGoForward = value; OnPropertyChanged(); }
    }

    public string WindowTitle
    {
        get => _windowTitle;
        set { _windowTitle = value; OnPropertyChanged(); }
    }

    // ===================== 历史记录 =====================

    public ObservableCollection<string> History { get; } = new();

    // ===================== 设置面板 =====================

    private SettingsViewModel? _settings;
    public SettingsViewModel? Settings
    {
        get => _settings;
        set { _settings = value; OnPropertyChanged(); }
    }

    /// <summary>设置面板是否可见（绑定 MainPage 中设置面板的 Visibility）。</summary>
    private bool _isSettingsVisible;
    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        set { _isSettingsVisible = value; OnPropertyChanged(); }
    }

    /// <summary>旧版兼容属性：下载列表是否可见。</summary>
    private bool _isDownloadListVisible;
    public bool IsDownloadListVisible
    {
        get => _isDownloadListVisible;
        set { _isDownloadListVisible = value; OnPropertyChanged(); }
    }

    // ===================== 底部导航标签 =====================

    /// <summary>底部快捷导航标签。</summary>
    public enum BottomTab
    {
        None,
        Home,
        SCKey,
        SCWZ,
    }

    private BottomTab _currentBottomTab;
    /// <summary>当前所在的底部导航标签（决定底栏按钮是否高亮显示）。</summary>
    public BottomTab CurrentBottomTab
    {
        get => _currentBottomTab;
        set { _currentBottomTab = value; OnPropertyChanged(); }
    }

    // ===================== 下载列表（保留兼容） =====================

    public DownloadListViewModel DownloadList { get; }

    // ===================== 命令 =====================

    public IRelayCommand NavigateHomeCommand { get; }
    public IRelayCommand NavigateSCKeyCommand { get; }
    public IRelayCommand NavigateSCWZCommand { get; }
    public IRelayCommand GoHomeCommand { get; }
    public IRelayCommand NavigateBackCommand { get; }
    public IRelayCommand NavigateForwardCommand { get; }
    public IRelayCommand ReloadCommand { get; }
    public IRelayCommand OpenSettingsCommand { get; }
    public IRelayCommand CloseSettingsCommand { get; }

    // ===================== 构造函数 =====================

    public MainViewModel(IBrowserProvider browser, ISettingsService settingsService)
    {
        _browser = browser;
        _settingsService = settingsService;

        // 创建 DownloadListViewModel 并传入下载管线（包含 Cookie 获取）
        DownloadList = new DownloadListViewModel(
            ServiceLocator.ServiceLocatorObj.GetRequiredService<IDownloadHistoryService>(),
            ServiceLocator.ServiceLocatorObj.GetRequiredService<IDownloadService>(),
            browser);

        // 创建 SettingsViewModel
        Settings = new SettingsViewModel(_settingsService, _browser, DownloadList);

        // 命令绑定
        NavigateHomeCommand = new RelayCommand(() => NavigateTo("https://www.scbbs.top/"));
        NavigateSCKeyCommand = new RelayCommand(() => NavigateTo("https://www.sckey.net/"));
        NavigateSCWZCommand = new RelayCommand(() => NavigateTo("https://www.scwz.top/"));
        GoHomeCommand = new RelayCommand(NavigateToHome);
        NavigateBackCommand = new RelayCommand(() => _browser.GoBack(), () => _browser.CanGoBack);
        NavigateForwardCommand = new RelayCommand(() => _browser.GoForward(), () => _browser.CanGoForward);
        ReloadCommand = new RelayCommand(() => _browser.Reload());
        OpenSettingsCommand = new RelayCommand(() =>
        {
            LogHelper.Info("[主页] 打开设置面板");
            IsSettingsVisible = true;
        });
        CloseSettingsCommand = new RelayCommand(() =>
        {
            LogHelper.Info("[主页] 关闭设置面板");
            IsSettingsVisible = false;
        });

        // 订阅浏览器事件
        _browser.AddressChanged += (_, url) =>
        {
            LogHelper.Info($"[主页] 地址变化 -> {url}");
            CurrentUrl = url;
            UpdateCurrentTab(url);
            if (!string.IsNullOrWhiteSpace(url))
            {
                History.Insert(0, url);
                if (History.Count > 100) History.RemoveAt(100);
            }
        };
        _browser.TitleChanged += (_, title) =>
        {
            var newTitle = string.IsNullOrWhiteSpace(title) ? "Uno浏览器" : $"Uno浏览器 - {title}";
            if (newTitle != _windowTitle)
            {
                LogHelper.Info($"[主页] 页面标题变化 -> {title}");
                WindowTitle = newTitle;
            }
        };
        _browser.LoadingStateChanged += (_, loading) =>
        {
            if (loading != _isLoading)
            {
                LogHelper.Info(loading ? "[主页] 页面开始加载" : "[主页] 页面加载完成");
            }
            IsLoading = loading;
            StatusText = loading ? "加载中..." : "就绪";
        };
        _browser.NavigationHistoryChanged += (_, _) =>
        {
            CanGoBack = _browser.CanGoBack;
            CanGoForward = _browser.CanGoForward;
            // 刷新命令的 CanExecute 状态
            NavigateBackCommand.NotifyCanExecuteChanged();
            NavigateForwardCommand.NotifyCanExecuteChanged();
            LogHelper.Info($"[主页] 导航历史变化 (canGoBack={CanGoBack}, canGoForward={CanGoForward})");
        };

        // 下载请求事件：自动弹出设置面板并触发下载
        _browser.DownloadRequested += OnDownloadRequested;

        // 加载持久化设置
        _settingsService.Load();

        LogHelper.Info("[主页] MainViewModel 初始化完成");
    }

    // ===================== 导航方法 =====================

    public void NavigateTo(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        LogHelper.Info($"[主页] NavigateTo -> {url}");

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        CurrentUrl = url;
        UpdateCurrentTab(url);
        _browser.Navigate(url);
    }

    public void NavigateToCustomUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        LogHelper.Info($"[主页] NavigateToCustomUrl -> {url}");
        NavigateTo(url);
    }

    public void NavigateToHome()
    {
        LogHelper.Info("[主页] NavigateToHome");
        _browser.Initialize("https://test.suancaixianyu.cn/");
        CurrentUrl = "https://test.suancaixianyu.cn/";
        UpdateCurrentTab(CurrentUrl);
    }

    /// <summary>根据当前 URL 更新底部导航高亮标签。</summary>
    private void UpdateCurrentTab(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            CurrentBottomTab = BottomTab.None;
            return;
        }

        if (url.Contains("scbbs.top", StringComparison.OrdinalIgnoreCase))
            CurrentBottomTab = BottomTab.Home;
        else if (url.Contains("sckey.net", StringComparison.OrdinalIgnoreCase))
            CurrentBottomTab = BottomTab.SCKey;
        else if (url.Contains("scwz.top", StringComparison.OrdinalIgnoreCase))
            CurrentBottomTab = BottomTab.SCWZ;
        else
            CurrentBottomTab = BottomTab.None;
    }

    // ===================== 下载处理 =====================

    /// <summary>
    /// 当 BrowserProvider 检测到可下载文件时触发。
    /// 自动弹出设置面板并切换到下载标签，启动下载。
    /// 文件名优先级：服务器 Content-Disposition / ResultFilePath > URL 提取 > 兜底默认名。
    /// </summary>
    private void OnDownloadRequested(object? sender, DownloadRequestedEventArgs e)
    {
        LogHelper.Info($"[主页] OnDownloadRequested -> {e.Url}" +
                       (string.IsNullOrEmpty(e.FileName) ? string.Empty : $", 文件名={e.FileName}"));

        // 自动显示设置面板（下载标签页）
        Settings?.ShowDownloads();
        IsSettingsVisible = true;

        // 启动下载（文件名缺省时由下载管理从 URL 推断）
        DownloadList.StartDownload(e.Url, e.FileName);
    }

    // ===================== INPC =====================

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
