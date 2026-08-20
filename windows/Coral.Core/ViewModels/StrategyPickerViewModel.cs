using Coral.Core.Generators;
using Coral.Core.Models;

namespace Coral.Core.ViewModels;

/// <summary>
/// Drives the "Strategy" flyout (windows/PORT-PLAN.md P0 item 2, Phase 1):
/// pick one of <see cref="StrategyLibrary.All"/>'s 15 built-in templates and
/// write it to the repo via <see cref="StrategyWriter"/> — the same
/// generator Fase 2 already ported and tested, just never wired to the
/// chat UI until now. Deliberately minimal: no editing (<c>Strategy.Validate()</c>/
/// <c>AutoFixed()</c> aren't surfaced here — there's nothing to validate
/// when every template is used as-is), no Advisor (a separate, later
/// phase — see PORT-PLAN.md). <see cref="ChatViewModel.Model"/> is updated
/// by the caller (<c>MainPage.xaml.cs</c>), not here — this ViewModel has
/// no dependency on <see cref="ChatViewModel"/>, same one-ViewModel-one-concern
/// separation already established between <c>GitPanelViewModel</c> and
/// <c>PullRequestViewModel</c>.
///
/// Deliberately does NOT write a default strategy on startup: unlike
/// picking a template (an explicit action), silently writing files into
/// whatever <c>RepoPath</c> happens to be — which defaults to the user's
/// whole home directory when no repo is open — would be a surprising side
/// effect for a feature meant to add zero required action. Chat behaves
/// exactly as it did before this feature existed until the user opens this
/// flyout and picks something.
/// </summary>
public sealed class StrategyPickerViewModel : ObservableObject
{
    private readonly string _repoPath;
    private readonly string _binary;

    public List<Strategy> Templates { get; } = StrategyLibrary.All;

    private Strategy? _selectedStrategy;
    public Strategy? SelectedStrategy { get => _selectedStrategy; private set => SetProperty(ref _selectedStrategy, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public StrategyPickerViewModel(string repoPath, string binary = "claude")
    {
        _repoPath = repoPath;
        _binary = binary;
    }

    /// <summary>Write <paramref name="strategy"/>'s files to the repo.
    /// <see cref="StrategyWriter.Write"/> is synchronous disk I/O, so this
    /// runs it on a background thread — same reasoning as every other
    /// ViewModel here that wraps sync work in <c>Task.Run</c>. Returns
    /// whether it succeeded, so the caller knows whether it's safe to also
    /// update <c>ChatViewModel.Model</c>.</summary>
    public async Task<bool> SelectAsync(Strategy strategy)
    {
        // Real bug caught on real Windows: this early return used to be
        // silent — no StatusMessage, no visible change at all — which made
        // "Use this"/picking a template while already busy indistinguishable
        // from the button simply not being wired up. Surfacing it here turns
        // a confusing no-op into a debuggable one.
        if (IsBusy) { StatusMessage = "Busy — try again in a moment."; return false; }
        IsBusy = true;
        try
        {
            await Task.Run(() => new StrategyWriter(_repoPath) { Binary = _binary }.Write(strategy));
            SelectedStrategy = strategy;
            StatusMessage = $"Now running as \"{strategy.Name}\".";
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
