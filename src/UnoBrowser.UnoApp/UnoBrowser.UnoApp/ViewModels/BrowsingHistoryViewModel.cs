using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using UnoBrowser.UnoApp.Models;
using UnoBrowser.UnoApp.Services;

namespace UnoBrowser.UnoApp.ViewModels;

/// <summary>
/// 浏览历史 ViewModel — 历史记录的单一数据源（Single Source of Truth）。
/// 所有对浏览历史的读写都通过此类，消除 MainViewModel / SettingsViewModel 之间的手动同步。
/// </summary>
public class BrowsingHistoryViewModel : INotifyPropertyChanged
{
    private readonly IBrowsingHistoryService _browsingHistory;

    /// <summary>浏览历史集合（供 UI 绑定，MainViewModel 和 SettingsViewModel 共享同一实例）。</summary>
    public ObservableCollection<BrowsingHistoryRecord> History { get; } = new();

    /// <summary>导航到历史 URL 的回调（由 MainPage 注入，关闭设置面板并跳转）。</summary>
    public Action<string>? OnNavigateRequested { get; set; }

    public IRelayCommand ClearHistoryCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public BrowsingHistoryViewModel(IBrowsingHistoryService browsingHistory)
    {
        _browsingHistory = browsingHistory;

        // 订阅 Service 层变更，自动同步 UI 集合
        _browsingHistory.HistoryChanged += OnHistoryChanged;
        LogHelper.Info($"[浏览历史] 已订阅 HistoryChanged 事件，当前已有 {_browsingHistory.Records.Count} 条记录");

        ClearHistoryCommand = new RelayCommand(() =>
        {
            LogHelper.Info("[浏览历史] 清除所有记录");
            _browsingHistory.ClearHistory();
            History.Clear();
            LogHelper.Info($"[浏览历史] ClearHistory 完成，UI 集合 Count={History.Count}");
        });

        LogHelper.Info("[浏览历史] BrowsingHistoryViewModel 初始化完成");
    }

    /// <summary>
    /// 从持久化存储加载初始数据。必须在构造后显式调用一次。
    /// </summary>
    public void Load()
    {
        LogHelper.Info("[浏览历史] BrowsingHistoryViewModel.Load() 开始");
        _browsingHistory.Load();
        LogHelper.Info($"[浏览历史] Load() 完成，Service 层 Records.Count={_browsingHistory.Records.Count}");
        SyncToUI();
        LogHelper.Info($"[浏览历史] SyncToUI 完成，UI History.Count={History.Count}");
    }

    /// <summary>添加 URL 到浏览历史（去重，最新的在最前面）。</summary>
    public void AddRecord(string url, string title = "")
    {
        LogHelper.Info($"[浏览历史] ViewModel.AddRecord -> Url={url}, Title={title}");
        _browsingHistory.AddRecord(url, title);
        LogHelper.Info($"[浏览历史] ViewModel.AddRecord 完成，Service Records.Count={_browsingHistory.Records.Count}, UI History.Count={History.Count}");
    }

    /// <summary>用最新标题更新浏览历史中的对应记录。</summary>
    public void UpdateTitle(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title)) return;
        LogHelper.Info($"[浏览历史] ViewModel.UpdateTitle -> Url={url}, Title={title}");
        _browsingHistory.AddRecord(url, title);
    }

    /// <summary>删除单条历史记录。</summary>
    public void RemoveRecord(string url)
    {
        LogHelper.Info($"[浏览历史] ViewModel.RemoveRecord -> {url}");
        _browsingHistory.RemoveRecord(url);
        LogHelper.Info($"[浏览历史] ViewModel.RemoveRecord 完成，UI History.Count={History.Count}");
    }

    /// <summary>批量删除历史记录。</summary>
    public void RemoveRecords(IEnumerable<string> urls)
    {
        LogHelper.Info("[浏览历史] ViewModel.RemoveRecords 开始");
        _browsingHistory.RemoveRecords(urls);
        LogHelper.Info($"[浏览历史] ViewModel.RemoveRecords 完成，UI History.Count={History.Count}");
    }

    /// <summary>Service 层 HistoryChanged 事件处理器 — 自动同步 UI 集合。</summary>
    private void OnHistoryChanged()
    {
        LogHelper.Info($"[浏览历史] HistoryChanged 触发，准备同步 UI（当前 UI Count={History.Count}, Service Count={_browsingHistory.Records.Count}）");
        SyncToUI();
    }

    /// <summary>将 Service 层记录同步到 UI ObservableCollection。</summary>
    private void SyncToUI()
    {
        LogHelper.Info($"[浏览历史] SyncToUI 开始 -> 清空 {History.Count} 条，准备加载 {_browsingHistory.Records.Count} 条");
        History.Clear();
        foreach (var r in _browsingHistory.Records)
        {
            History.Add(r);
            LogHelper.Info($"[浏览历史]   SyncToUI 添加: Url={r.Url}, Title={r.Title}, Time={r.Time:yyyy-MM-dd HH:mm:ss}");
        }
        LogHelper.Info($"[浏览历史] SyncToUI 完成，UI History.Count={History.Count}");
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
