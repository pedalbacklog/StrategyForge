using Microsoft.UI.Xaml;

namespace Coral;

/// <summary>Thin shell for Code Mode's git panel, same pattern as
/// <see cref="MainWindow"/>: WinUI 3's Window isn't a FrameworkElement, so
/// x:Bind can't live on it directly — the real content is
/// <see cref="CodeModePage"/>. A separate window (not embedded in
/// <see cref="MainPage"/>) so this new, not-yet-real-Windows-verified UI
/// can't destabilize the chat flow already confirmed working.</summary>
public sealed partial class CodeModeWindow : Window
{
    public CodeModeWindow(string repoPath)
    {
        InitializeComponent();
        Title = "Coral — Code Mode";
        Content = new CodeModePage(repoPath);
    }
}
