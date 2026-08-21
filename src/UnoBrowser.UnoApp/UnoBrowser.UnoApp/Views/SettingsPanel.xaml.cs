using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using UnoBrowser.UnoApp.Models;
using UnoBrowser.UnoApp.Services;
using UnoBrowser.UnoApp.ViewModels;

namespace UnoBrowser.UnoApp.Views;

public partial class SettingsPanel : UserControl
{
    /// <summary>外部订阅此事件以响应关闭操作。</summary>
    public event EventHandler? CloseRequested;

    private readonly SolidColorBrush _tabSelectedBg;
    private readonly SolidColorBrush _tabUnselectedBg;
    private readonly SolidColorBrush _tabSelectedFg;
    private readonly SolidColorBrush _tabUnselectedFg;

    /// <summary>记录当前选中的历史记录 URL（用于多选模式）。</summary>
    private readonly HashSet<string> _selectedHistoryUrls = new();

    public SettingsPanel()
    {
        InitializeComponent();
        LogHelper.Info("[设置面板] 已构造");

        _tabSelectedBg = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
        _tabUnselectedBg = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _tabSelectedFg = new SolidColorBrush(Microsoft.UI.Colors.White);
        var mediumColor = (Windows.UI.Color)Application.Current.Resources["SystemBaseMediumColor"];
        _tabUnselectedBg = new SolidColorBrush(mediumColor);

        // 监听 DataContext 变化以绑定 ViewModel 的 SelectedTabIndex
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private SettingsViewModel? _currentVm;

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        // 解除旧 ViewModel 的绑定
        if (_currentVm is not null)
            _currentVm.PropertyChanged -= OnViewModelPropertyChanged;

        _currentVm = DataContext as SettingsViewModel;

        // 绑定新 ViewModel
        if (_currentVm is not null)
        {
            _currentVm.PropertyChanged += OnViewModelPropertyChanged;
            // 同步当前标签状态
            SetActiveTab(_currentVm.SelectedTabIndex);
            BindHistory();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // DataContextChanged 在 Uno XAML 绑定场景下可能不触发，Loaded 作为兜底
        if (_currentVm is null)
            _currentVm = DataContext as SettingsViewModel;
        if (_currentVm is not null)
            BindHistory();
    }

    private void BindHistory()
    {
        if (_currentVm is null) return;
        LogHelper.Info($"[设置面板] BindHistory: History.Count={_currentVm.History.Count}");
        // 先置空再赋值，确保 ListView 重新绑定到当前集合
        HistoryListBox.ItemsSource = null;
        HistoryListBox.ItemsSource = _currentVm.History;

        // 打印每条记录用于调试
        for (int i = 0; i < _currentVm.History.Count; i++)
        {
            var r = _currentVm.History[i];
            LogHelper.Info($"[设置面板]   [{i}] Url={r.Url}, Title={r.Title}, Time={r.Time:yyyy-MM-dd HH:mm:ss}");
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.SelectedTabIndex) &&
            sender is SettingsViewModel vm)
        {
            SetActiveTab(vm.SelectedTabIndex);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        LogHelper.Info("[设置面板] 关闭按钮点击");
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void BrowserSettingsTab_Click(object sender, RoutedEventArgs e)
    {
        LogHelper.Info("[设置面板] 切换到浏览器设置标签");
        if (DataContext is SettingsViewModel vm)
            vm.SelectedTabIndex = 0;
    }

    private void DownloadTab_Click(object sender, RoutedEventArgs e)
    {
        LogHelper.Info("[设置面板] 切换到下载管理标签");
        if (DataContext is SettingsViewModel vm)
            vm.SelectedTabIndex = 1;
    }

    private void HistoryTab_Click(object sender, RoutedEventArgs e)
    {
        LogHelper.Info("[设置面板] 切换到历史记录标签");
        if (DataContext is SettingsViewModel vm)
            vm.SelectedTabIndex = 2;
    }

    /// <summary>ListView 选择变化事件（多选模式下记录选中项）。</summary>
    private void HistoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentVm is null) return;

        // 多选模式：记录选中/取消选中的 URL
        if (_currentVm.IsHistoryMultiSelectMode)
        {
            // 移除取消选中的
            foreach (var item in e.RemovedItems)
            {
                if (item is BrowsingHistoryRecord record)
                {
                    _selectedHistoryUrls.Remove(record.Url);
                    LogHelper.Info($"[设置面板] 多选取消: {record.Url}");
                }
            }
            // 添加新选中的
            foreach (var item in e.AddedItems)
            {
                if (item is BrowsingHistoryRecord record)
                {
                    _selectedHistoryUrls.Add(record.Url);
                    LogHelper.Info($"[设置面板] 多选选中: {record.Url}");
                }
            }
            LogHelper.Info($"[设置面板] 当前选中 {_selectedHistoryUrls.Count} 条");

            // 将选中项传给删除命令
            var selectedRecords = _currentVm.History
                .Where(r => _selectedHistoryUrls.Contains(r.Url))
                .ToList();
            _currentVm.DeleteSelectedHistoryCommand.Execute(selectedRecords);
            _selectedHistoryUrls.Clear();
        }
        else
        {
            // 单选模式：直接导航
            if (HistoryListBox.SelectedItem is BrowsingHistoryRecord record)
            {
                HistoryListBox.SelectedIndex = -1; // 取消选中，允许重复点击
                _currentVm.NavigateToHistoryCommand.Execute(record.Url);
            }
        }
    }

    /// <summary>单条历史记录删除按钮点击。</summary>
    private void DeleteSingleHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string url && DataContext is SettingsViewModel vm)
        {
            LogHelper.Info($"[设置面板] 单条删除: {url}");
            // 通过 NavigateToHistoryCommand 同级的 BrowsingHistoryViewModel 删除
            vm.DeleteSelectedHistoryCommand.Execute(
                vm.History.Where(r => r.Url == url).ToList());
        }
    }

    private void SetActiveTab(int index)
    {
        BrowserSettingsPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        DownloadSettingsPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        HistorySettingsPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        BrowserSettingsTab.Background = index == 0 ? _tabSelectedBg : _tabUnselectedBg;
        BrowserSettingsTab.Foreground = index == 0 ? _tabSelectedFg : _tabUnselectedFg;

        DownloadTab.Background = index == 1 ? _tabSelectedBg : _tabUnselectedBg;
        DownloadTab.Foreground = index == 1 ? _tabSelectedFg : _tabUnselectedFg;

        HistoryTab.Background = index == 2 ? _tabSelectedBg : _tabUnselectedBg;
        HistoryTab.Foreground = index == 2 ? _tabSelectedFg : _tabUnselectedFg;
    }
}
