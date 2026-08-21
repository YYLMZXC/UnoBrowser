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

        ClearHistoryCommand = new RelayCommand(() =>
        {
            LogHelper.Info("[浏览历史] 清除所有记录");
            _browsingHistory.ClearHistory();
            History.Clear();
        });

        LogHelper.Info("[浏览历史] BrowsingHistoryViewModel 初始化完成");
    }

    /// <summary>
    /// 从持久化存储加载初始数据。必须在构造后显式调用一次。
    /// 订阅已在构造函数中完成，因此 Load 产生的变更能被正确捕获。
    /// </summary>
    public void Load()
    {
        _browsingHistory.Load();
        SyncToUI();
    }

    /// <summary>添加 URL 到浏览历史（去重，最新的在最前面）。</summary>
    public void AddRecord(string url, string title = "")
    {
        _browsingHistory.AddRecord(url, title);
    }

    /// <summary>用最新标题更新浏览历史中的对应记录。</summary>
    public void UpdateTitle(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title)) return;
        _browsingHistory.AddRecord(url, title);
    }

    /// <summary>Service 层 HistoryChanged 事件处理器 — 自动同步 UI 集合。</summary>
    private void OnHistoryChanged()
    {
        LogHelper.Info("[浏览历史] HistoryChanged 触发，同步 UI");
        SyncToUI();
    }

    /// <summary>将 Service 层记录同步到 UI ObservableCollection。</summary>
    private void SyncToUI()
    {
        History.Clear();
        foreach (var r in _browsingHistory.Records)
            History.Add(r);
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
