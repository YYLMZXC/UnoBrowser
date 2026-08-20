namespace UnoBrowser.UnoApp.Services;

/// <summary>
/// 服务定位器实例类 - 提供 GetService/GetRequiredService 便捷方法。
/// </summary>
public class ServiceLocatorInstance
{
    public T? GetService<T>() where T : class
        => CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetService<T>();

    public T GetRequiredService<T>() where T : class
        => CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default.GetRequiredService<T>();
}

/// <summary>
/// 全局静态服务引用 - 提供快速访问常用服务。
/// </summary>
public static class ServiceLocator
{
    public static IBrowserProvider BrowserProvider { get; set; } = null!;
    public static IDownloadHistoryService DownloadHistory { get; set; } = null!;
    public static IDownloadService DownloadService { get; set; } = null!;
    public static ISettingsService SettingsService { get; set; } = null!;
    internal static ServiceLocatorInstance ServiceLocatorObj { get; set; } = null!;
}
