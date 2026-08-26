using Coral.Core.Models;
using Coral.Core.Services;

namespace Coral.Core.ViewModels;

/// <summary>
/// Drives the "Run for real" window — <see cref="TeamRunEngine"/>'s UI/
/// ViewModel half, same split as every other engine/ViewModel pair in this
/// port (<c>StrategyPickerViewModel</c>/<c>StrategyWriter</c>,
/// <c>ConnectViewModel</c>/<c>ProviderInstaller</c>). Isolates the CURRENT
/// team in its own worktree, runs it for real, and shows the resulting
/// diff — the user decides whether to Apply (merge into the real repo) or
/// Discard. Nothing here ever touches the user's own working tree until
/// Apply is pressed.
/// </summary>
public sealed class TeamRunViewModel : ObservableObject
{
    private readonly string _repoPath;
    private readonly Strategy _strategy;
    private readonly IOneShotRunner _runner;
    private readonly IProcessLauncher _gitLauncher;
    private readonly string _binary;
    private readonly Func<string, string?>? _resolveGitBinary;

    private string _task = "";
    public string Task { get => _task; set => SetProperty(ref _task, value); }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    private TeamRunOutcome? _outcome;
    public TeamRunOutcome? Outcome
    {
        get => _outcome;
        private set
        {
            if (SetProperty(ref _outcome, value))
            {
                OnPropertyChanged(nameof(DiffLines));
                OnPropertyChanged(nameof(HasResult));
                OnPropertyChanged(nameof(CanApply));
                OnPropertyChanged(nameof(AuthorshipLines));
                OnPropertyChanged(nameof(HasAuthorship));
            }
        }
    }

    /// <summary>The result diff, parsed the same way Code Mode's git panel
    /// parses one — reuses <see cref="CodeGit.Parse"/> so the "Run for real"
    /// window can bind the exact same <c>ListView</c>/converter shape.</summary>
    public IReadOnlyList<DiffLine> DiffLines => Outcome is null ? Array.Empty<DiffLine>() : CodeGit.Parse(Outcome.Diff);

    public bool HasResult => Outcome is { State: TeamRunState.Done or TeamRunState.Failed };

    public bool CanApply => Outcome is { State: TeamRunState.Done, ProducedChanges: true };

    /// <summary>Per-file "who wrote this" lines — only populated for a
    /// cross-provider run (a Claude-only team has no per-line provenance to
    /// show; Claude Code's own Agent tool did the delegating).</summary>
    public IReadOnlyList<string> AuthorshipLines => Outcome is null
        ? Array.Empty<string>()
        : Outcome.Authorship.Select(f => $"{f.File}: {string.Join(", ", f.Authors.Select(a => a.Label(_strategy.Orchestrator?.Name ?? "orchestrator")))}").ToList();

    /// <summary>Bool-typed companion to <see cref="AuthorshipLines"/> — same
    /// reasoning as <c>AdvisorViewModel.HasAdvice</c>: <c>BoolToVisibilityConverter</c>
    /// only handles <c>bool</c>, not a collection count.</summary>
    public bool HasAuthorship => AuthorshipLines.Count > 0;

    public TeamRunViewModel(string repoPath, Strategy strategy, IOneShotRunner runner, IProcessLauncher gitLauncher,
        string binary = "claude", Func<string, string?>? resolveGitBinary = null)
    {
        _repoPath = repoPath;
        _strategy = strategy;
        _runner = runner;
        _gitLauncher = gitLauncher;
        _binary = binary;
        _resolveGitBinary = resolveGitBinary;
    }

    /// <summary>Isolate the team in a fresh worktree and run it for real.
    /// No-op while already running or with a blank task.</summary>
    public async Task RunAsync()
    {
        var trimmed = Task.Trim();
        if (IsRunning || trimmed.Length == 0) return;

        IsRunning = true;
        StatusMessage = "Running…";
        Outcome = null;
        try
        {
            Outcome = await TeamRunEngine.RunAsync(trimmed, _repoPath, _strategy, _runner, _gitLauncher, _binary, _resolveGitBinary);
            StatusMessage = Outcome.State switch
            {
                TeamRunState.Done when Outcome.ProducedChanges => "Done — review the diff below.",
                TeamRunState.Done => "Done — no changes were made.",
                TeamRunState.Failed => $"Error: {Outcome.Error}",
                _ => null,
            };
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Merge the run's branch into the real repo, then tear down
    /// its worktree.</summary>
    public async Task ApplyAsync()
    {
        if (Outcome is not { State: TeamRunState.Done } outcome || IsRunning) return;
        IsRunning = true;
        try
        {
            var (ok, output) = await TeamRunEngine.ApplyAsync(_gitLauncher, _repoPath, outcome, _resolveGitBinary);
            StatusMessage = ok ? "Applied to your repo." : $"Couldn't merge: {output}";
            if (ok) Outcome = null;
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Throw the run away without merging — the user's repo is
    /// never touched.</summary>
    public async Task DiscardAsync()
    {
        if (Outcome is not { } outcome || IsRunning) return;
        IsRunning = true;
        try
        {
            await TeamRunEngine.DiscardAsync(_gitLauncher, _repoPath, outcome, _resolveGitBinary);
            StatusMessage = "Discarded.";
            Outcome = null;
        }
        finally
        {
            IsRunning = false;
        }
    }
}
