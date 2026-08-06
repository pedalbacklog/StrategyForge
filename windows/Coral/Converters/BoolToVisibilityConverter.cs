using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Coral.Converters;

/// <summary>Bool → Visibility for XAML binding — e.g. showing the paste-code
/// row only while <c>ConnectViewModel.NeedsCode</c> is true (see MainPage.xaml).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
