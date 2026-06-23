using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinDeploy.App;

/// <summary>int count → Visible when 0, else Collapsed. Used to show "empty" placeholders.</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is int n && n == 0) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
