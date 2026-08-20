using System;
using System.Runtime.InteropServices;
using System.Text;
using UnoBrowser.UnoApp.Services;
using Uno.UI.Hosting;

namespace UnoBrowser.UnoApp;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 桌面端分配控制台窗口方便调试查看日志
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            AllocConsole();
            Console.OutputEncoding = Encoding.UTF8;
        }
        LogHelper.Info("[应用] 启动中...");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            LogHelper.Error($"[应用] 未处理的异常: {e.ExceptionObject}");
        };

        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseX11()
            .UseLinuxFrameBuffer()
            .UseMacOS()
            .UseWin32()
            .Build();

        host.Run();
    }

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();
}
