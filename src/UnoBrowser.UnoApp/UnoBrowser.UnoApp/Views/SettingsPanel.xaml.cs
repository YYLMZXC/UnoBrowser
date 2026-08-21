using System.ComponentModel;
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

    public SettingsPanel()
    {
        InitializeComponent();
        LogHelper.Info("[设置面板] 已构造");

        _tabSelectedBg = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
        _tabUnselectedBg = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _tabSelectedFg = new SolidColorBrush(Microsoft.UI.Colors.White);
        var mediumColor = (Windows.UI.Color)Application.Current.Resources["SystemBaseMediumColor"];
        _tabUnselectedFg = new SolidColorBrush(mediumColor);

        // 监听 DataContext 变化以绑定 ViewModel 的 SelectedTabIndex
        DataContextChanged += OnDataContextChanged;
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
            // 强制刷新历史列表绑定
            HistoryListBox.ItemsSource = _currentVm.History;
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

    private void HistoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryListBox.SelectedItem is BrowsingHistoryRecord record && DataContext is SettingsViewModel vm)
        {
            HistoryListBox.SelectedIndex = -1; // 取消选中，允许重复点击
            vm.NavigateToHistoryCommand.Execute(record.Url);
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
