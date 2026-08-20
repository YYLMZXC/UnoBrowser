using System;
using System.IO;

namespace UnoBrowser.UnoApp.Services;

/// <summary>
/// 日志系统：同时输出到 Console 窗口、Debug 输出和日志文件。
/// 日志文件直接保存在软件目录 Bugs 文件夹下，按日期命名：
///   {软件目录}/Bugs/app_yyyy-MM-dd.log
/// 软件目录位置见 <see cref="AppPaths"/>。
/// </summary>
public static class LogHelper
{
    private static readonly string LogDirectory;
    private static readonly object LockObj = new();

    static LogHelper()
    {
        LogDirectory = AppPaths.Bugs;
    }

    /// <summary>获取日志目录路径（供外部使用，如打开日志文件夹）。</summary>
    public static string GetLogDirectory() => LogDirectory;

    public static void Debug(string message)
    {
        Write("[DEBUG]", message);
    }

    public static void Info(string message)
    {
        Write("[INFO]", message);
    }

    public static void Warn(string message)
    {
        Write("[WARN]", message);
    }

    public static void Error(string message, Exception? ex = null)
    {
        var text = ex is not null ? $"{message} | Exception: {ex}" : message;
        Write("[ERROR]", text);
    }

    private static void Write(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var line = $"{timestamp} {level} {message}";

        Console.WriteLine(line);
        System.Diagnostics.Debug.WriteLine(line);

        try
        {
            lock (LockObj)
            {
                File.AppendAllText(GetLogFilePath(), line + Environment.NewLine);
            }
        }
        catch
        {
            // 写文件失败不抛异常
        }
    }

    private static string GetLogFilePath()
    {
        var date = DateTime.Now.ToString("yyyy-MM-dd");
        return Path.Combine(LogDirectory, $"app_{date}.log");
    }
}
