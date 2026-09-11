using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WhisperSubtitler.Models;

namespace WhisperSubtitler.Converters;

/// <summary>bool -> Visibility；ConverterParameter="invert" 反转。</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool flag = value is bool b && b;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>非空字符串 -> Visible；ConverterParameter="invert" 反转。</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool hasText = value is string s && !string.IsNullOrWhiteSpace(s);
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase))
            hasText = !hasText;
        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>bool -> 圆点颜色（true=绿色就绪，false=灰色）。</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool flag = value is bool b && b;
        Color color = flag
            ? Color.FromArgb(255, 16, 137, 62)     // green
            : Color.FromArgb(255, 160, 160, 160);  // gray
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>JobStatus -> 状态圆点颜色。</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        Color color = value is JobStatus status
            ? status switch
            {
                JobStatus.Running => Color.FromArgb(255, 0, 120, 212),   // accent blue
                JobStatus.Completed => Color.FromArgb(255, 16, 137, 62), // green
                JobStatus.Failed => Color.FromArgb(255, 196, 43, 28),    // red
                JobStatus.Canceled => Color.FromArgb(255, 140, 140, 140),
                _ => Color.FromArgb(255, 160, 160, 160)                  // pending gray
            }
            : Colors.Gray;
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
