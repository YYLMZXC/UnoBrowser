using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Views;
using AndroidX.Core.View;

namespace UnoBrowser.UnoApp.Droid;

[Activity(
    MainLauncher = true,
    ConfigurationChanges = global::Uno.UI.ActivityHelper.AllConfigChanges,
    WindowSoftInputMode = SoftInput.AdjustNothing | SoftInput.StateHidden
)]
public class MainActivity : Microsoft.UI.Xaml.ApplicationActivity
{
    /// <summary>
    /// 刘海屏安全区顶部高度 (pixels)，由 WindowInsets 回调更新。
    /// </summary>
    public static float SafeAreaTopPixels { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Android WebView 空白页排查：启用远程调试
        // 可通过 Chrome 地址栏 chrome://inspect 连接到设备实时查看 WebView 状态
#if DEBUG
        Android.Webkit.WebView.SetWebContentsDebuggingEnabled(true);
        Android.Util.Log.Info("UnoBrowser", "WebView 远程调试已启用");
#endif

        global::AndroidX.Core.SplashScreen.SplashScreen.InstallSplashScreen(this);

        // 启用无缝全屏渲染（edge-to-edge），让内容渲染到状态栏/导航栏后方
        // Uno Platform 的 SafeArea.Insets 会自动根据 WindowInsets 添加安全区偏移
        EnableEdgeToEdge();

        base.OnCreate(savedInstanceState);

        // 刘海屏 / 挖孔屏 安全区适配：通过 WindowInsets 获取真实顶部安全高度
        SetupSafeAreaTracking();
    }

    /// <summary>
    /// 启用 edge-to-edge 全屏渲染，配合 windowLayoutInDisplayCutoutMode=shortEdges
    /// 实现刘海屏/挖孔屏/状态栏区域的正确适配。
    /// </summary>
    private void EnableEdgeToEdge()
    {
        try
        {
            var decorView = Window?.DecorView;
            if (decorView == null) return;

            // 核心：告知系统应用自行处理 WindowInsets，不自动为系统栏预留空间
            WindowCompat.SetDecorFitsSystemWindows(Window!, false);

            // 状态栏透明，使内容可以渲染到状态栏后方
            Window?.SetStatusBarColor(Color.Transparent);

            // 导航栏透明（可选，Android 8.0+）
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                Window?.SetNavigationBarColor(Color.Transparent);
            }

            Android.Util.Log.Info("UnoBrowser", "Edge-to-edge 全屏渲染已启用");
        }
        catch (System.Exception ex)
        {
            Android.Util.Log.Warn("UnoBrowser", $"Edge-to-edge 启用失败: {ex.Message}");
        }
    }

    private void SetupSafeAreaTracking()
    {
        try
        {
            var decorView = Window?.DecorView;
            if (decorView == null) return;

            // 立即同步读取当前 WindowInsets（可能已经就绪）
            var currentInsets = ViewCompat.GetRootWindowInsets(decorView);
            if (currentInsets != null)
            {
                UpdateSafeArea(currentInsets);
            }

            // 注册监听器处理后续 insets 变化
            ViewCompat.SetOnApplyWindowInsetsListener(decorView, new InsetsListener());
        }
        catch (System.Exception ex)
        {
            Android.Util.Log.Warn("UnoBrowser", $"SafeArea 监听设置失败: {ex.Message}");
        }
    }

    private static void UpdateSafeArea(WindowInsetsCompat insets)
    {
        var statusBars = insets.GetInsets(WindowInsetsCompat.Type.StatusBars());
        var cutout = insets.GetInsets(WindowInsetsCompat.Type.DisplayCutout());
        var navigationBars = insets.GetInsets(WindowInsetsCompat.Type.NavigationBars());
        var systemBars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());

        // 安全区顶部 = max(状态栏, 刘海/挖孔, 系统栏 顶部)
        SafeAreaTopPixels = Math.Max(Math.Max(statusBars.Top, cutout.Top), systemBars.Top);
        Android.Util.Log.Info("UnoBrowser",
            $"SafeArea: statusBars={statusBars.Top}, cutout={cutout.Top}, " +
            $"navBars={navigationBars.Bottom}, finalTop={SafeAreaTopPixels}px");
    }

    private class InsetsListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat? OnApplyWindowInsets(View? v, WindowInsetsCompat? insets)
        {
            if (insets != null)
                UpdateSafeArea(insets);
            return insets;
        }
    }
}
