using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace Coral.Converters;

/// <summary>Bool → Button <see cref="Style"/>, so a selected tier chip
/// (Advisor's Economy/Recommended/Max row) reads as filled/accented and an
/// unselected one stays a plain button — no other control in this port needs
/// per-state styling, so this stays a single-purpose converter rather than a
/// general style-picker.</summary>
public sealed class BoolToAccentButtonStyleConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language) =>
        value is bool b && b ? Application.Current.Resources["AccentButtonStyle"] as Style : null;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
