using Microsoft.UI.Xaml.Data;

namespace Coral.Converters;

/// <summary>Inverts a bool for XAML binding — e.g. disabling the input row
/// while <c>ViewModel.IsSending</c> is true (<c>IsEnabled="{x:Bind
/// converters:BoolNegationConverter...}"</c> isn't itself valid; used via a
/// resource instance, see MainPage.xaml).</summary>
public sealed class BoolNegationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        !(value is bool b && b);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
