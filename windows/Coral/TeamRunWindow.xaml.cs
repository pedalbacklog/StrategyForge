using Coral.Core.Models;
using Microsoft.UI.Xaml;

namespace Coral;

/// <summary>Thin shell for the "Run for real" page, same pattern as
/// <see cref="StrategyEditorWindow"/>/<see cref="CodeModeWindow"/>: a
/// separate window so this new, not-yet-real-Windows-verified UI can't
/// destabilize the chat/Strategy flyout flow already confirmed working.</summary>
public sealed partial class TeamRunWindow : Window
{
    /// <param name="repoPath">The real repo the run will isolate itself
    /// from (and, on Apply, merge back into) — the SAME path driving
    /// <see cref="MainPage"/>'s chat.</param>
    /// <param name="strategy">The team to run — the caller passes
    /// <c>StrategyPickerViewModel.SelectedStrategy</c>, so there's always a
    /// team to run before this window can open.</param>
    /// <param name="binary">The configured Claude binary — matches
    /// <see cref="StrategyEditorWindow"/>'s "Binary" plumbing for
    /// <c>StrategyWriter</c>.</param>
    public TeamRunWindow(string repoPath, Strategy strategy, string binary = "claude")
    {
        InitializeComponent();
        Title = "Coral — Run for Real";
        Content = new TeamRunPage(repoPath, strategy, binary);
    }
}
