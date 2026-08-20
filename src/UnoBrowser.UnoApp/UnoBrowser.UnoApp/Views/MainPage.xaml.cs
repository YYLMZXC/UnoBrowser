using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using UnoBrowser.UnoApp.Services;
using UnoBrowser.UnoApp.ViewModels;

namespace UnoBrowser.UnoApp.Views;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }
    private readonly IBrowserProvider _browserProvider;

    // 底部导航按钮高亮画刷
    private readonly SolidColorBrush _tabSelectedBg = new(Microsoft.UI.Colors.DodgerBlue);
    private readonly SolidColorBrush _tabUnselectedBg = new(Microsoft.UI.Colors.Transparent);
    private readonly SolidColorBrush _tabSelectedFg = new(Microsoft.UI.Colors.White);
    private readonly SolidColorBrush _tabUnselectedFg;

    public MainPage()
    {
        InitializeComponent();

        LogHelper.Info("[主页] 正在构造 MainPage");

        _browserProvider = ServiceLocator.BrowserProvider;

        // 注册 Browser WebView2 控件
        var browserControl = _browserProvider.CreateBrowserControl();
        if (browserControl is FrameworkElement fe)
        {
            BrowserHost.Children.Add(fe);
            LogHelper.Info("[主页] WebView2 控件已添加到 BrowserHost Grid");
        }

        // 获取 ViewModel（DI 创建，含 SettingsViewModel）
        ViewModel = ServiceLocator.ServiceLocatorObj.GetRequiredService<MainViewModel>();
        DataContext = ViewModel;

        // 底部导航按钮高亮画刷（未选中前景使用主题色）
        var mediumColor = (Windows.UI.Color)Application.Current.Resources["SystemBaseMediumColor"];
        _tabUnselectedFg = new SolidColorBrush(mediumColor);

        // 监听底部导航标签变化，刷新按钮高亮
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateBottomTabs(ViewModel.CurrentBottomTab);

        // 绑定浏览历史到设置面板
        if (ViewModel.Settings is not null)
        {
            ViewModel.Settings.History = ViewModel.History;
            ViewModel.Settings.OnNavigateToHistoryUrl = url =>
            {
                ViewModel.NavigateTo(url);
                ViewModel.IsSettingsVisible = false;
            };
        }

        // 监听 ViewModel 的 IsSettingsVisible 变化，控制设置面板可见性
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSettingsVisible))
            {
                UpdateSettingsOverlayVisibility();
            }
        };

        // 设置面板的关闭事件
        SettingsPanelControl.CloseRequested += (_, _) =>
        {
            LogHelper.Info("[主页] 设置面板关闭请求");
            ViewModel.CloseSettingsCommand.Execute(null);
        };

        Loaded += OnLoaded;
    }

    private void UpdateSettingsOverlayVisibility()
    {
        SettingsOverlay.Visibility = ViewModel.IsSettingsVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LogHelper.Info("[主页] MainPage.Loaded 事件");
        _browserProvider.Initialize("https://test.suancaixianyu.cn/");
    }

    private void AddressBar_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            LogHelper.Info($"[主页] 地址栏回车: {AddressBar.Text}");
            ViewModel.NavigateToCustomUrl(AddressBar.Text);
        }
    }

    private void GoButton_Click(object sender, RoutedEventArgs e)
    {
        LogHelper.Info($"[主页] 跳转按钮点击: {AddressBar.Text}");
        ViewModel.NavigateToCustomUrl(AddressBar.Text);
    }

    private void NarrowAddressBar_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            LogHelper.Info($"[主页] 窄屏地址栏回车: {NarrowAddressBar.Text}");
            ViewModel.NavigateToCustomUrl(NarrowAddressBar.Text);
        }
    }

    private void NarrowGoButton_Click(object sender, RoutedEventArgs e)
    {
        LogHelper.Info($"[主页] 窄屏跳转按钮点击: {NarrowAddressBar.Text}");
        ViewModel.NavigateToCustomUrl(NarrowAddressBar.Text);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentBottomTab) && sender is MainViewModel vm)
        {
            UpdateBottomTabs(vm.CurrentBottomTab);
        }
    }

    /// <summary>根据当前底部导航标签刷新 3 个底栏按钮的高亮状态。</summary>
    private void UpdateBottomTabs(MainViewModel.BottomTab tab)
    {
        BottomTabHome.Background = tab == MainViewModel.BottomTab.Home ? _tabSelectedBg : _tabUnselectedBg;
        BottomTabHome.Foreground = tab == MainViewModel.BottomTab.Home ? _tabSelectedFg : _tabUnselectedFg;

        BottomTabSCKey.Background = tab == MainViewModel.BottomTab.SCKey ? _tabSelectedBg : _tabUnselectedBg;
        BottomTabSCKey.Foreground = tab == MainViewModel.BottomTab.SCKey ? _tabSelectedFg : _tabUnselectedFg;

        BottomTabSCWZ.Background = tab == MainViewModel.BottomTab.SCWZ ? _tabSelectedBg : _tabUnselectedBg;
        BottomTabSCWZ.Foreground = tab == MainViewModel.BottomTab.SCWZ ? _tabSelectedFg : _tabUnselectedFg;
    }
}
