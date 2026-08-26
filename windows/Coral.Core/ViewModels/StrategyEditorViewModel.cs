using Coral.Core.Models;

namespace Coral.Core.ViewModels;

/// <summary>
/// Drives the "Edit team" window (P0 item 2, Phase 2 — a deliberately
/// minimal slice, see below). Wraps one <see cref="Strategy"/> in place:
/// per-role name/model/instance-count/tools, live validation
/// (<see cref="Models.Strategy.Validate"/>), and a one-click "Fix All"
/// (<see cref="Models.Strategy.AutoFixed"/>).
///
/// SCOPE, agreed with the founder before writing this: only the fields
/// listed above. Deliberately NOT ported from <c>StrategyEditorView.swift</c>
/// (589 lines) in this pass: the repo picker embedded in the editor (this
/// port already opens a repo before any editing happens), the animated
/// topology diagram, the cost popover, the MCP server list editor, cross-
/// provider role assignment (no second provider is connectable in this
/// port yet), memory-toggle/system-prompt/description editing, and the
/// "download brief"/"copy starter prompt"/"generate in Terminal"/"generate
/// + commit" action variants — none of those block "tweak the roles I
/// already have and save," which is the actual gap Fase 1/3 left open.
///
/// Persistence is deliberately minimal too: editing applies to the
/// in-memory <see cref="Strategy"/> for as long as this window is open,
/// written to disk via the SAME <c>StrategyPickerViewModel.SelectAsync</c>
/// path picking/suggesting a template already uses (the caller,
/// <c>StrategyEditorPage</c>, owns that call) — there is no "save as a new
/// named custom template" library here yet, same as the original plan
/// flagged as deliberately out of scope for this phase.
/// </summary>
public sealed class StrategyEditorViewModel : ObservableObject
{
    private Strategy _strategy;

    /// <summary>The strategy being edited — mutated in place by the roles
    /// list's own controls, and swapped wholesale by <see cref="AutoFix"/>
    /// (which returns a new, fixed copy rather than mutating in place).</summary>
    public Strategy Strategy
    {
        get => _strategy;
        private set
        {
            _strategy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Roles));
        }
    }

    /// <summary>Exposed separately from <see cref="Strategy"/> so the roles
    /// <c>ItemsControl</c> only re-binds (and re-creates its row controls)
    /// when the role LIST itself changes — e.g. after <see cref="AutoFix"/> —
    /// not on every unrelated <see cref="Strategy"/> notification.</summary>
    public IReadOnlyList<AgentRole> Roles => _strategy.Roles;

    private IReadOnlyList<Strategy.ValidationIssue> _issues = Array.Empty<Strategy.ValidationIssue>();
    public IReadOnlyList<Strategy.ValidationIssue> Issues
    {
        get => _issues;
        private set
        {
            if (SetProperty(ref _issues, value))
            {
                OnPropertyChanged(nameof(IssueLines));
                OnPropertyChanged(nameof(IsValid));
                OnPropertyChanged(nameof(HasAutoFixableIssues));
            }
        }
    }

    /// <summary>Pre-formatted issue lines ("⚠ ..."/"❌ ...") — kept as
    /// plain strings (not the record type itself) so the roles-issues
    /// <c>ItemsControl</c>'s DataTemplate can x:Bind against a plain
    /// <c>string</c> instead of needing a XAML type reference to
    /// <see cref="Models.Strategy.ValidationIssue"/>, a nested record type.</summary>
    public IReadOnlyList<string> IssueLines =>
        Issues.Select(i => (i.Severity == Strategy.Severity.Error ? "❌ " : "⚠ ") + i.Message).ToList();

    public bool IsValid => Issues.All(i => i.Severity != Strategy.Severity.Error);
    public bool HasAutoFixableIssues => _strategy.HasAutoFixableIssues;

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public StrategyEditorViewModel(Strategy strategy)
    {
        _strategy = strategy;
        Revalidate();
    }

    /// <summary>Re-run <see cref="Models.Strategy.Validate"/> — called after
    /// every field edit (role controls call this directly from their
    /// change-event handlers in code-behind, since <see cref="AgentRole"/>
    /// isn't itself an observable type; see this type's own doc comment).</summary>
    public void Revalidate() => Issues = _strategy.Validate();

    /// <summary>Apply <see cref="Models.Strategy.AutoFixed"/> — swaps in a
    /// new, fixed <see cref="Strategy"/> (roles are cloned, not mutated in
    /// place, matching <c>AutoFixed()</c>'s own contract) and revalidates.</summary>
    public void AutoFix()
    {
        Strategy = _strategy.AutoFixed();
        Revalidate();
    }
}
