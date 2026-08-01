using System.Globalization;
using System.Windows;
using System.Windows.Data;
using WebViewHub.Models;

namespace WebViewHub.Helpers;

/// <summary>
/// Returns Visible when the bound UserAgentMode is Custom — used to show
/// the "Custom user agent" textbox only when needed.
/// </summary>
public class UserAgentIsCustomToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is UserAgentMode m && m == UserAgentMode.Custom ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
