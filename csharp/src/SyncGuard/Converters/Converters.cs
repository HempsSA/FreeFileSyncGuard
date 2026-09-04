using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SyncGuard.Converters;

/// <summary>Status → dot color (ports STATUS_COLORS).</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var color = (value as string)?.ToUpperInvariant() switch
        {
            "OK" => "#3FB950",
            "WARN" => "#D29922",
            "ERROR" or "ABORTED" => "#F85149",
            "RUNNING" => "#388BFD",
            _ => "#8B949E",
        };
        return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)!);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Log level → text color for the activity log.</summary>
public sealed class LevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var color = (value as string)?.ToUpperInvariant() switch
        {
            "OK" => "#3FB950",
            "WARN" => "#D29922",
            "ERROR" => "#F85149",
            _ => "#8B949E",
        };
        return new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color)!);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : value;
}
