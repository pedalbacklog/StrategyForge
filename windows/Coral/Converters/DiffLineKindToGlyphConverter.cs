using Coral.Core.Services;
using Microsoft.UI.Xaml.Data;

namespace Coral.Converters;

/// <summary>DiffLineKind → a short glyph for the diff viewer's gutter
/// column (see CodeModePage.xaml).</summary>
public sealed class DiffLineKindToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is DiffLineKind kind
            ? kind switch
            {
                DiffLineKind.Add => "+",
                DiffLineKind.Del => "-",
                DiffLineKind.Hunk => "@@",
                _ => " ",
            }
            : " ";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
