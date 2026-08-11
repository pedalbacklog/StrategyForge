using Coral.Core.Services;

namespace Coral.Core.ViewModels;

/// <summary>
/// Drives Code Mode's "one tap PR" flow: check the PR for the current
/// branch, open a new one, merge it. A fresh design, not a port — same
/// reasoning as <see cref="GitPanelViewModel"/> (no separate ViewModel type
/// exists in <c>CodeModeView.swift</c>, which keeps this as plain
/// <c>@State</c>) — and deliberately kept SEPARATE from
/// <see cref="GitPanelViewModel"/> rather than merged into it, the same way
/// <see cref="ConnectViewModel"/> stays separate from <see cref="ChatViewModel"/>
/// on <c>MainPage</c>: one ViewModel, one concern.
///
/// SCOPE: only what <see cref="GitHubCLI"/> already ports —
/// <see cref="RefreshAsync"/> (read the PR for a branch),
/// <see cref="CreateAsync"/> (open one), <see cref="MergeAsync"/> (merge
/// it), and <see cref="ShipAsync"/> — the one-tap "Commit + PR" engine
/// (commit → push → open-or-skip a PR), delegating the actual git+gh calls
/// to <see cref="ShipFlow"/> rather than duplicating them here, so
/// this ViewModel still doesn't take a dependency on
/// <see cref="GitPanelViewModel"/> (its caller passes in whatever git state
/// it needs — repo path, branch, staged-ness). <see cref="AutoPr"/> is the
/// opt-in toggle (persisted via <see cref="AppSettings"/>), but the actual
/// "fire <see cref="ShipAsync"/> when a chat turn finishes" wiring lives in
/// <c>CodeModePage.xaml.cs</c> — it needs to observe <c>ChatViewModel.
/// IsSending</c>, which this ViewModel deliberately has no dependency on
/// (same reasoning as not depending on <c>GitPanelViewModel</c>). Still
/// deliberately NOT here: repo browse/create (<c>GitHubCLI.ListReposAsync</c>/
/// <c>CreateRepoAsync</c> back a repo-picker flow, a different screen
/// entirely).
/// </summary>
public sealed class PullRequestViewModel : ObservableObject
{
    private readonly IProcessLauncher _launcher;
    private readonly string _repoPath;
    private readonly Func<string, string?>? _resolveBinary;

    private PRInfo? _info;
    public PRInfo? Info { get => _info; private set => SetProperty(ref _info, value); }

    private string _title = "";
    public string Title { get => _title; set => SetProperty(ref _title, value); }

    private string _body = "";
    public string Body { get => _body; set => SetProperty(ref _body, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    private readonly Action<bool> _saveAutoPr;
    private bool _autoPr;

    /// <summary>Opt-in: auto-ship (see <see cref="ShipAsync"/>, <c>auto:
    /// true</c>) after a chat turn that changed files finishes. Off by
    /// default; persisted immediately on every change via the injected
    /// save function (<see cref="AppSettings.AutoPr"/> in production).</summary>
    public bool AutoPr
    {
        get => _autoPr;
        set
        {
            if (SetProperty(ref _autoPr, value)) _saveAutoPr(value);
        }
    }

    /// <paramref name="loadAutoPr"/>/<paramref name="saveAutoPr"/> are
    /// injectable so the persisted toggle is unit-testable without touching
    /// the real settings file — production callers leave both at their
    /// defaults (<see cref="AppSettings.AutoPr"/>'s getter/setter).
    public PullRequestViewModel(IProcessLauncher launcher, string repoPath, Func<string, string?>? resolveBinary = null,
        Func<bool>? loadAutoPr = null, Action<bool>? saveAutoPr = null)
    {
        _launcher = launcher;
        _repoPath = repoPath;
        _resolveBinary = resolveBinary;
        _saveAutoPr = saveAutoPr ?? (v => AppSettings.AutoPr = v);
        _autoPr = (loadAutoPr ?? (() => AppSettings.AutoPr))();
    }

    /// <summary>Pure decision for Code Mode's Auto-PR trigger — matches
    /// <c>CodeModeView.swift</c>'s <c>onChange(of: vm.isRunning)</c> guard
    /// (<c>autoPR, isRepo, GitHubCLI.isInstalled, !changeStats.isEmpty</c>);
    /// <paramref name="hasRepo"/> is always true for this port's Code Mode
    /// (a window always has a repo path), kept as a parameter anyway to
    /// mirror the original 1:1 and stay testable if that ever changes.</summary>
    public static bool ShouldAutoShip(bool autoPr, bool hasRepo, bool ghInstalled, bool hasChanges) =>
        autoPr && hasRepo && ghInstalled && hasChanges;

    /// <summary>Reload the PR (if any) for <paramref name="branch"/>. The
    /// caller passes the branch in — this ViewModel doesn't own branch
    /// state, <see cref="GitPanelViewModel"/> does.</summary>
    public async Task RefreshAsync(string branch, CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            Info = await GitHubCLI.PrInfoAsync(_launcher, _repoPath, branch, _resolveBinary, ct);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Open a PR from the current branch of the repo using
    /// <see cref="Title"/>/<see cref="Body"/>. No-op on a blank title or
    /// while busy.</summary>
    public async Task CreateAsync(string branch, CancellationToken ct = default)
    {
        var title = Title.Trim();
        if (title.Length == 0 || IsBusy) return;

        IsBusy = true;
        try
        {
            var (ok, url, output) = await GitHubCLI.CreatePRAsync(_launcher, _repoPath, title, Body, _resolveBinary, ct);
            if (ok)
            {
                StatusMessage = url is null ? "PR created." : $"PR created: {url}";
                Title = "";
                Body = "";
                await RefreshAsync(branch, ct);
            }
            else
            {
                StatusMessage = $"Error: {output}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Commit, push, and open a PR for <paramref name="branch"/> —
    /// or, if one's already open, just report it as updated (the push alone
    /// brings gh's existing PR up to date). <paramref name="repoPath"/>/
    /// <paramref name="anyStaged"/> are passed in by the caller (ultimately
    /// <see cref="GitPanelViewModel"/>) rather than read from it directly —
    /// see this type's doc comment. On a freshly-created PR, clears
    /// <see cref="Title"/>/<see cref="Body"/> same as <see cref="CreateAsync"/>
    /// does.</summary>
    public async Task ShipAsync(string repoPath, string branch, string commitMessage, string prTitle,
        string prBody, bool anyStaged, bool auto = false, CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await ShipFlow.RunAsync(_launcher, repoPath, commitMessage, prTitle, prBody,
                auto, anyStaged, hadPR: Info != null, _resolveBinary, ct);
            if (!result.Ok)
            {
                StatusMessage = $"Error: {result.Error}";
                return;
            }
            StatusMessage = result.PrWasCreated ? "Pull request opened." : "Pull request updated.";
            if (result.PrWasCreated)
            {
                Title = "";
                Body = "";
            }
            await RefreshAsync(branch, ct);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Merge the PR for <paramref name="branch"/> (squash by
    /// default), then refresh to reflect MERGED.</summary>
    public async Task MergeAsync(string branch, bool squash = true, CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var (ok, output) = await GitHubCLI.MergePRAsync(_launcher, _repoPath, branch, squash, _resolveBinary, ct);
            if (ok)
            {
                StatusMessage = "Merged.";
                await RefreshAsync(branch, ct);
            }
            else
            {
                StatusMessage = $"Error: {output}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
