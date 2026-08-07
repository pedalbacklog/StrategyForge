using Microsoft.UI.Xaml;

namespace Coral;

/// <summary>Thin shell — WinUI 3's Window isn't a FrameworkElement, so it can't
/// host x:Bind directly; the real UI/bindings live in <see cref="MainPage"/>.</summary>
public sealed partial class MainWindow : Window
{
    /// <param name="repoPath">Null defaults to the user's home directory
    /// (<see cref="MainPage"/>'s own default). Opening a repo picked/cloned/
    /// created via <see cref="RepoPickerViewModel"/> opens a NEW MainWindow
    /// with this set, rather than mutating the current one's already
    /// OneTime-bound <c>RepoPath</c>/<c>ViewModel</c> — simpler and lower-risk
    /// than making those rebindable.</param>
    public MainWindow(string? repoPath = null)
    {
        InitializeComponent();
        Title = "Coral";
        Content = new MainPage(repoPath);
    }
}
