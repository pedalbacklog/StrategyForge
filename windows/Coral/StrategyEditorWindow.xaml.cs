using Coral.Core.Models;
using Coral.Core.ViewModels;
using Microsoft.UI.Xaml;

namespace Coral;

/// <summary>Thin shell for the "Edit team" page, same pattern as
/// <see cref="CodeModeWindow"/>: a separate window (not embedded in
/// <see cref="MainPage"/>'s flyout) since a real role editor needs more
/// room than a Flyout comfortably gives, and keeping it separate means
/// this new, not-yet-real-Windows-verified UI can't destabilize the chat/
/// Strategy flyout flow already confirmed working.</summary>
public sealed partial class StrategyEditorWindow : Window
{
    /// <param name="strategy">The strategy to edit — the caller
    /// (<c>MainPage</c>) passes <c>StrategyPickerViewModel.SelectedStrategy</c>,
    /// so there's always something to edit before this window can open.</param>
    /// <param name="strategyPickerViewModel">The SAME instance driving
    /// <see cref="MainPage"/>'s "Strategy" flyout — "Save" here writes
    /// through it, the exact same path picking/suggesting a template
    /// already uses, so the header/status text stay in sync.</param>
    /// <param name="chatViewModel">The SAME instance driving <see cref="MainPage"/>'s
    /// chat — "Save" updates its <c>Model</c>, same as picking a template.</param>
    public StrategyEditorWindow(Strategy strategy, StrategyPickerViewModel strategyPickerViewModel,
        ChatViewModel chatViewModel)
    {
        InitializeComponent();
        Title = "Coral — Edit Team";
        Content = new StrategyEditorPage(strategy, strategyPickerViewModel, chatViewModel, this);
    }
}
