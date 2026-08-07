using Coral.Core.ViewModels;
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
    /// <param name="chatViewModel">The SAME instance driving <see cref="MainPage"/>'s
    /// chat — matches CodeModeView.swift, which takes the chat's own ChatViewModel
    /// rather than a new one, so the terminal panel shows commands from that
    /// live session.</param>
    public CodeModeWindow(string repoPath, ChatViewModel chatViewModel)
    {
        InitializeComponent();
        Title = "Coral — Code Mode";
        Content = new CodeModePage(repoPath, chatViewModel);
    }
}
