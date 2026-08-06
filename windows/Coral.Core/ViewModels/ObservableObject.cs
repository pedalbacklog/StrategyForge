using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Coral.Core.ViewModels;

/// <summary>Minimal hand-rolled <see cref="INotifyPropertyChanged"/> base —
/// deliberately not a dependency on CommunityToolkit.Mvvm or similar: the
/// surface this port needs (a settable-property helper) is a few lines, and
/// keeping ViewModels dependency-free means they stay plain <c>Coral.Core</c>
/// (net8.0, no WinUI reference) and are unit-testable on any platform, same
/// as everything else ported so far.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
