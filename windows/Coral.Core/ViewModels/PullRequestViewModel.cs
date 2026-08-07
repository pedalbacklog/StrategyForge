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
/// it). Deliberately NOT here: Auto-PR (auto-commit + push + open/update a
/// PR when a run finishes — a policy decision layered on top of these
/// primitives, not a primitive itself) and repo browse/create
/// (<c>GitHubCLI.ListReposAsync</c>/<c>CreateRepoAsync</c> back a repo-picker
/// flow, a different screen entirely).
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

    public PullRequestViewModel(IProcessLauncher launcher, string repoPath, Func<string, string?>? resolveBinary = null)
    {
        _launcher = launcher;
        _repoPath = repoPath;
        _resolveBinary = resolveBinary;
    }

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
