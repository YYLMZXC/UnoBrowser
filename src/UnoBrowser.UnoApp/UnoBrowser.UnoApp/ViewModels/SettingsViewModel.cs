using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using UnoBrowser.UnoApp.Models;
using UnoBrowser.UnoApp.Services;

namespace UnoBrowser.UnoApp.ViewModels;

/// <summary>
/// 设置面板 ViewModel — 整合浏览器 UA 设置 + 下载管理。
/// </summary>
public class SettingsViewModel : INotifyPropertyChanged
{
    private readonly ISettingsService _settingsService;
    private readonly IBrowserProvider _browserProvider;
    private readonly DownloadListViewModel _downloadList;
    private readonly BrowsingHistoryViewModel _browsingHistoryVm;

    private int _selectedTabIndex;
    private int _selectedUaIndex;
    private bool _isVisible;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>UA 平台选项列表。</summary>
    public ObservableCollection<UaOption> UaOptions { get; } = new()
    {
        new UaOption { Platform = UserAgentPlatform.Auto, DisplayName = "跟随系统" },
        new UaOption { Platform = UserAgentPlatform.Desktop, DisplayName = "Windows (Chrome)" },
        new UaOption { Platform = UserAgentPlatform.Mobile, DisplayName = "安卓 (Android Chrome)" },
        new UaOption { Platform = UserAgentPlatform.IPhone, DisplayName = "iPhone (iOS Chrome)" },
        new UaOption { Platform = UserAgentPlatform.Linux, DisplayName = "Linux (Chrome)" },
        new UaOption { Platform = UserAgentPlatform.MacOS, DisplayName = "macOS (Chrome)" },
    };

    /// <summary>当前选中的标签页索引：0=浏览器设置，1=下载管理，2=历史记录。</summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            _selectedTabIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsBrowserSettingsTab));
            OnPropertyChanged(nameof(IsDownloadTab));
            OnPropertyChanged(nameof(IsHistoryTab));
        }
    }

    public bool IsBrowserSettingsTab => _selectedTabIndex == 0;
    public bool IsDownloadTab => _selectedTabIndex == 1;
    public bool IsHistoryTab => _selectedTabIndex == 2;

    // ===================== 历史记录 =====================

    /// <summary>
    /// 浏览历史记录 — 直接引用 BrowsingHistoryViewModel.History（单一数据源）。
    /// 不再维护独立副本，消除手动同步。
    /// </summary>
    public ObservableCollection<BrowsingHistoryRecord> History => _browsingHistoryVm.History;

    /// <summary>导航到历史 URL 后关闭面板的回调。</summary>
    public Action<string>? OnNavigateToHistoryUrl { get; set; }

    public IRelayCommand ClearHistoryCommand { get; }
    public IRelayCommand NavigateToHistoryCommand { get; }

    /// <summary>当前选中的 UA 平台索引。</summary>
    public int SelectedUaIndex
    {
        get => _selectedUaIndex;
        set
        {
            if (_selectedUaIndex != value)
            {
                _selectedUaIndex = value;
                OnPropertyChanged();
                ApplyUaSetting();
            }
        }
    }

    /// <summary>面板是否可见。</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set { _isVisible = value; OnPropertyChanged(); }
    }

    /// <summary>下载列表 ViewModel（数据绑定）。</summary>
    public DownloadListViewModel DownloadList => _downloadList;

    public IRelayCommand OpenDownloadFolderCommand { get; }
    public IRelayCommand ClearDownloadHistoryCommand { get; }
    public IRelayCommand SwitchToDownloadsCommand { get; }

    public SettingsViewModel(
        ISettingsService settingsService,
        IBrowserProvider browserProvider,
        DownloadListViewModel downloadList,
        BrowsingHistoryViewModel browsingHistoryVm)
    {
        _settingsService = settingsService;
        _browserProvider = browserProvider;
        _downloadList = downloadList;
        _browsingHistoryVm = browsingHistoryVm;

        OpenDownloadFolderCommand = new RelayCommand(() =>
        {
            LogHelper.Info("[设置] 打开下载文件夹");
            _downloadList.OpenDownloadFolder();
        });
        ClearDownloadHistoryCommand = new RelayCommand(() =>
        {
            LogHelper.Info("[设置] 清除下载历史");
            _downloadList.ClearHistory();
        });
        SwitchToDownloadsCommand = new RelayCommand(() =>
        {
            LogHelper.Info("[设置] 切换到下载标签");
            IsVisible = true;
            SelectedTabIndex = 1;
        });

        ClearHistoryCommand = new RelayCommand(() =>
        {
            LogHelper.Info("[设置] 清除浏览历史");
            _browsingHistoryVm.ClearHistoryCommand.Execute(null);
        });
        NavigateToHistoryCommand = new RelayCommand<string>(url =>
        {
            if (url is null) return;
            LogHelper.Info($"[设置] 历史记录点击导航: {url}");
            OnNavigateToHistoryUrl?.Invoke(url);
        });

        // 从已保存的设置初始化 UA
        var saved = _settingsService.Settings.UserAgentPlatform;
        _selectedUaIndex = UaOptions.IndexOf(UaOptions.First(o => o.Platform == saved));
        _browserProvider.SetUserAgent(saved);

        LogHelper.Info($"[设置] SettingsViewModel 初始化完成, UA={saved}");
    }

    /// <summary>UA 变更时应用并持久化。</summary>
    private void ApplyUaSetting()
    {
        if (_selectedUaIndex < 0 || _selectedUaIndex >= UaOptions.Count) return;
        var platform = UaOptions[_selectedUaIndex].Platform;
        LogHelper.Info($"[设置] UA 变更 -> {platform}");

        _settingsService.Settings.UserAgentPlatform = platform;
        _settingsService.Save();

        _browserProvider.SetUserAgent(platform);

        // UA 变更后刷新当前页面以生效
        _browserProvider.Reload();
    }

    /// <summary>下载开始时自动打开面板并切换到下载标签。</summary>
    public void ShowDownloads()
    {
        IsVisible = true;
        SelectedTabIndex = 1;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// UA 平台选项数据类。
/// </summary>
public class UaOption
{
    public UserAgentPlatform Platform { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    public override string ToString() => DisplayName;
}
