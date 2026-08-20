using Microsoft.UI.Xaml.Controls;
using UnoBrowser.UnoApp.Services;

namespace UnoBrowser.UnoApp.Views;

public partial class DownloadListPanel : UserControl
{
    public DownloadListPanel()
    {
        InitializeComponent();
        LogHelper.Info("[下载列表面板] 已构造");
    }
}
