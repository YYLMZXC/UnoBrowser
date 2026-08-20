namespace UnoBrowser.UnoApp.Models;

/// <summary>
/// 浏览器标识平台。
/// </summary>
public enum UserAgentPlatform
{
    /// <summary>跟随设备默认 UA</summary>
    Auto = 0,
    /// <summary>桌面浏览器（Windows Chrome）</summary>
    Desktop = 1,
    /// <summary>安卓浏览器（Android Chrome，保留旧枚举值以兼容已保存设置）</summary>
    Mobile = 2,
    /// <summary>苹果手机浏览器（iPhone Chrome/Safari）</summary>
    IPhone = 3,
    /// <summary>Linux 桌面浏览器（Linux Chrome）</summary>
    Linux = 4,
    /// <summary>macOS 桌面浏览器（macOS Chrome）</summary>
    MacOS = 5
}

/// <summary>
/// 应用设置数据模型。
/// </summary>
public class AppSettings
{
    public UserAgentPlatform UserAgentPlatform { get; set; } = UserAgentPlatform.Auto;
}
