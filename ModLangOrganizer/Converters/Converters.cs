using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ModLangOrganizer.Models;

namespace ModLangOrganizer.Converters;

/// <summary>bool → Visibility</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool invert = parameter?.ToString() == "Invert";
        bool val = value is bool b && b;
        if (invert) val = !val;
        return val ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool反転</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}

/// <summary>ModStatus → 色</summary>
public sealed class StatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is ModStatus status ? status switch
        {
            ModStatus.Success => new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),    // Green
            ModStatus.Warning => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),    // Amber
            ModStatus.Failed => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),     // Red
            ModStatus.Skipped => new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),    // Gray
            ModStatus.Processing => new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), // Blue
            ModStatus.Scanning => new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)),   // Purple
            _ => new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF))                     // GrayLight
        } : new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>LogLevel → 色</summary>
public sealed class LogLevelToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is LogLevel level ? level switch
        {
            LogLevel.Info => new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),     // Light Gray
            LogLevel.Warning => new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),  // Yellow
            LogLevel.Error => new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)),    // Red
            _ => new SolidColorBrush(Colors.Gray)
        } : new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>JarIntegrity → 色</summary>
public sealed class IntegrityToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is JarIntegrity integrity ? integrity switch
        {
            JarIntegrity.OK => new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),
            JarIntegrity.Corrupted => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
            _ => new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))
        } : new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>SnapshotState → 色</summary>
public sealed class SnapshotStateToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is SnapshotState state ? state switch
        {
            SnapshotState.Current => new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)),
            SnapshotState.Stale => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
            _ => new SolidColorBrush(Colors.Gray)
        } : new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>CancelGranularity → bool (RadioButton用)</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString() == parameter?.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string str)
            return Enum.Parse(targetType, str);
        return Binding.DoNothing;
    }
}
