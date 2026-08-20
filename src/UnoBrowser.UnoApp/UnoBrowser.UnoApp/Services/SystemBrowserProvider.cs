using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UnoBrowser.UnoApp.Services;

/// <summary>
/// 系统默认浏览器打开 URL - 作为 WebView2 不可用时的回退方案。
/// </summary>
public static class SystemBrowserProvider
{
    public static void OpenUrl(string url)
    {
        LogHelper.Info($"[系统浏览器] 尝试打开 URL: {url}");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            Process.Start(psi);
            LogHelper.Info("[系统浏览器] URL 已通过默认浏览器打开");
        }
        catch (Exception ex)
        {
            LogHelper.Warn($"[系统浏览器] 首次打开失败: {ex.Message}，尝试备用方案");
            // 如果打开失败，尝试另一种方式
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Process.Start(new ProcessStartInfo("cmd", $"/c start {url}")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                    LogHelper.Info("[系统浏览器] 通过 cmd 打开 URL (Windows备用方案)");
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", url);
                    LogHelper.Info("[系统浏览器] 通过 open 命令打开 URL (macOS备用方案)");
                }
                else
                {
                    Process.Start("xdg-open", url);
                    LogHelper.Info("[系统浏览器] 通过 xdg-open 打开 URL (Linux备用方案)");
                }
            }
            catch (Exception ex2)
            {
                LogHelper.Error($"[系统浏览器] 所有尝试均失败，URL={url}: {ex2.Message}", ex2);
            }
        }
    }
}
