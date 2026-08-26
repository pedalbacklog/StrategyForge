using Coral.Core.Models;
using Microsoft.UI.Xaml.Data;

namespace Coral.Converters;

/// <summary>RoleKind → its display name, for the "Edit team" role rows
/// (see StrategyEditorPage.xaml) — x:Bind won't implicitly stringify an
/// enum onto TextBlock.Text, same reason DiffLineKindToGlyphConverter
/// exists for DiffLineKind.</summary>
public sealed class RoleKindToDisplayNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is RoleKind kind ? kind.DisplayName() : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
