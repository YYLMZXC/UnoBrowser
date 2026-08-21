using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using UnoBrowser.UnoApp.Services;
using UnoBrowser.UnoApp.ViewModels;
using UnoBrowser.UnoApp.Views;
using Uno.Resizetizer;

namespace UnoBrowser.UnoApp;

public partial class App : Application
{
    private Window? _mainWindow;

    public App()
    {
        this.InitializeComponent();

        // 启动基础信息（软件目录、日志目录、平台、版本）—— 排查问题第一步看这里
        LogHelper.Info($"[应用] ============ 应用启动 ============");
        LogHelper.Info($"[应用] 软件目录: {AppPaths.Root}");
        LogHelper.Info($"[应用] 日志目录: {LogHelper.GetLogDirectory()}");
        LogHelper.Info($"[应用] 平台: {GetPlatformInfo()}");
        LogHelper.Info($"[应用] 版本: {GetVersionInfo()}");

        // 全局未处理异常日志：任何未捕获异常都记录到 Bugs 日志，便于崩溃定位
        RegisterGlobalExceptionLogging();

        LogHelper.Info("[应用] 构造函数 - 正在配置服务");
        ConfigureServices();
    }

    /// <summary>注册全局未处理异常日志，确保任何崩溃都记录到 Bugs 目录。</summary>
    private static void RegisterGlobalExceptionLogging()
    {
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                LogHelper.Error($"[应用] 未处理的域异常 (IsTerminating={e.IsTerminating})", ex);
            };

            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                LogHelper.Error($"[应用] 未观察的任务异常 (inner={e.Exception?.InnerExceptions?.Count ?? 0})", e.Exception);
                e.SetObserved(); // 标记已处理，防止进程被终止
            };

            LogHelper.Info("[应用] 全局异常日志已注册");
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[应用] 注册全局异常日志失败: {ex.Message}");
        }
    }

    private static string GetPlatformInfo()
    {
#if ANDROID
        return $"Android {Android.OS.Build.VERSION.Release} (API {(int)Android.OS.Build.VERSION.SdkInt})";
#elif __IOS__
        return $"iOS {UIKit.UIDevice.CurrentDevice.SystemVersion}";
#elif MACCATALYST
        return $"macOS {Environment.OSVersion.VersionString}";
#elif WINDOWS
        return $"Windows {Environment.OSVersion.VersionString}";
#else
        return Environment.OSVersion.VersionString;
#endif
    }

    private static string GetVersionInfo()
    {
        try
        {
            var asmVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";
#if ANDROID
            var context = Android.App.Application.Context;
            var pkg = context?.PackageManager?.GetPackageInfo(context.PackageName!, 0);
            if (pkg is not null)
            {
                return $"{pkg.VersionName} ({asmVersion})";
            }
#endif
            return asmVersion;
        }
        catch
        {
            return "unknown";
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        LogHelper.Info("[应用] OnLaunched - 正在创建窗口");
        _mainWindow = new Window();

        var mainPage = new MainPage();
        _mainWindow.Content = mainPage;
        _mainWindow.Title = "Uno浏览器";
        _mainWindow.SetWindowIcon();

        _mainWindow.Activate();
        LogHelper.Info("[应用] 窗口已激活");

        // Win32 Skia 后端：通过原生 API 设置窗口图标
        if (OperatingSystem.IsWindows())
        {
            _mainWindow.DispatcherQueue.TryEnqueue(SetNativeWindowIcon);
        }

        // 监听应用恢复事件（Android 从后台切回 / iOS 进入前台）
        // 用于在用户从 QQ 等外部 App 授权返回时刷新页面检测登录状态
        this.Resuming += (s, e) =>
        {
            LogHelper.Info("[应用] 检测到应用恢复事件，通知浏览器刷新");
            ServiceLocator.BrowserProvider?.HandleAppResumed();
        };
    }

    private static void ConfigureServices()
    {
        var services = new ServiceCollection();

        // Core services
        var downloadHistory = new DownloadHistoryService();
        downloadHistory.Load();
        services.AddSingleton<IDownloadHistoryService>(downloadHistory);

        var browsingHistory = new BrowsingHistoryService();
        browsingHistory.Load();
        services.AddSingleton<IBrowsingHistoryService>(browsingHistory);

        var downloadService = new DownloadService();
        services.AddSingleton<IDownloadService>(downloadService);

        var browserProvider = new BrowserProvider();
        services.AddSingleton<IBrowserProvider>(browserProvider);

        var settingsService = new SettingsService();
        settingsService.Load();
        services.AddSingleton<ISettingsService>(settingsService);

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DownloadListViewModel>();

        var provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        // Populate static ServiceLocator
        ServiceLocator.BrowserProvider = browserProvider;
        ServiceLocator.DownloadHistory = downloadHistory;
        ServiceLocator.DownloadService = downloadService;
        ServiceLocator.SettingsService = settingsService;
        ServiceLocator.ServiceLocatorObj = new ServiceLocatorInstance();

        LogHelper.Info("[应用] 服务配置完成");
    }

    /// <summary>
    /// 通过 Win32 API 设置原生窗口图标（Skia/Win32 后端需要）
    /// </summary>
    private static void SetNativeWindowIcon()
    {
        var iconPath = Path.Combine(System.AppContext.BaseDirectory, "icon.ico");
        if (!File.Exists(iconPath))
        {
            LogHelper.Warn($"[窗口图标] icon.ico 未找到: {iconPath}");
            return;
        }

        // 从文件加载图标
        var hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_SHARED);
        if (hIcon == IntPtr.Zero)
        {
            LogHelper.Warn($"[窗口图标] 加载 icon.ico 失败: {iconPath}");
            return;
        }

        // 通过窗口标题查找窗口句柄
        var hwnd = FindWindow(null, "Uno浏览器");
        if (hwnd == IntPtr.Zero)
        {
            LogHelper.Warn("[窗口图标] 未能找到原生窗口句柄");
            return;
        }

        // WM_SETICON: ICON_SMALL (0) + ICON_BIG (1)
        SendMessage(hwnd, 0x0080, (IntPtr)0, hIcon); // ICON_SMALL - 任务栏
        SendMessage(hwnd, 0x0080, (IntPtr)1, hIcon); // ICON_BIG   - 标题栏/Alt+Tab

        LogHelper.Info("[窗口图标] 原生窗口图标已设置");
    }

    // --- Win32 P/Invoke ---

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;
    private const uint LR_SHARED = 0x00008000;

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr LoadImage(
        IntPtr hinst,
        string lpszName,
        uint uType,
        int cxDesired,
        int cyDesired,
        uint fuLoad);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
}
