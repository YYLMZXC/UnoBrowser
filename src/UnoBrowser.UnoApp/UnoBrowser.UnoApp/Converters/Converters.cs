using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using UnoBrowser.UnoApp.Models;

namespace UnoBrowser.UnoApp.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b)
            return b ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is Visibility v)
            return v == Visibility.Visible;
        return false;
    }
}

/// <summary>
/// 布尔反转可见性转换器 — true 时隐藏，false 时显示。
/// </summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b)
            return b ? Visibility.Collapsed : Visibility.Visible;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is Visibility v)
            return v != Visibility.Visible;
        return false;
    }
}

public class IsNotNullConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is not null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 下载状态文本转换器 - 将 DownloadState 转为中文显示文本。
/// </summary>
public class DownloadStateToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is DownloadState state)
        {
            return state switch
            {
                DownloadState.Pending => "等待下载",
                DownloadState.Downloading => "下载中",
                DownloadState.Completed => "已完成",
                DownloadState.Failed => "下载失败",
                DownloadState.Cancelled => "已取消",
                _ => "未知"
            };
        }
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

/// <summary>
/// 进度百分比文本转换器 - 将 double 进度值转为 "85.0%" 格式，替代 Uno 不支持的 StringFormat。
/// </summary>
public class ProgressToPercentTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double d)
            return $"{d:F1}%";
        return "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
