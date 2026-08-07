using System.Collections.ObjectModel;
using Coral.Core.Services;

namespace Coral.Core.ViewModels;

/// <summary>
/// Drives picking a repo to open: browse the signed-in user's GitHub repos,
/// clone one by URL, or create a brand-new one — resolving to a local
/// folder path the caller (the window/page hosting a chat or Code Mode
/// session) can point at. A fresh design — no dedicated Swift ViewModel
/// exists for this either, same reasoning as <see cref="GitPanelViewModel"/>
/// and <see cref="PullRequestViewModel"/> — following the shape this port
/// already established: a <c>Coral.Core</c> ViewModel with injected
/// <see cref="IProcessLauncher"/>, unit-testable with a fake.
///
/// SCOPE: only what <see cref="GitHubCLI"/>/<see cref="CodeGit"/> already
/// port — <see cref="LoadReposAsync"/> (browse), <see cref="CloneAsync"/>,
/// <see cref="CreateRepoAsync"/>. Deliberately NOT here: actually swapping
/// the ACTIVE repo of a running chat/Code Mode session — that's a caller
/// concern (e.g. opening a new window pointed at <see cref="ResultRepoPath"/>),
/// not this ViewModel's job.
/// </summary>
public sealed class RepoPickerViewModel : ObservableObject
{
    private readonly IProcessLauncher _launcher;
    private readonly Func<string, string?>? _resolveBinary;
    private readonly Action<string>? _createDirectory;
    private readonly Func<string, bool>? _pathExists;

    public ObservableCollection<RepoRef> Repos { get; } = new();

    private string _cloneUrl = "";
    public string CloneUrl { get => _cloneUrl; set => SetProperty(ref _cloneUrl, value); }

    private string _newRepoName = "";
    public string NewRepoName { get => _newRepoName; set => SetProperty(ref _newRepoName, value); }

    private bool _newRepoIsPrivate = true;
    public bool NewRepoIsPrivate { get => _newRepoIsPrivate; set => SetProperty(ref _newRepoIsPrivate, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    /// <summary>The local path of the repo just cloned/created, once
    /// successful — the signal a caller watches to know it's time to open
    /// a session there.</summary>
    private string? _resultRepoPath;
    public string? ResultRepoPath { get => _resultRepoPath; private set => SetProperty(ref _resultRepoPath, value); }

    /// <paramref name="createDirectory"/>/<paramref name="pathExists"/> are
    /// injectable so <see cref="CloneAsync"/>/<see cref="CreateRepoAsync"/>'s
    /// folder-dedup/success-check logic is testable without touching real
    /// disk — threaded straight through to <see cref="Services.CodeGit.CloneAsync"/>/
    /// <see cref="Services.GitHubCLI.CreateRepoAsync"/>, which already accept them.
    public RepoPickerViewModel(IProcessLauncher launcher, Func<string, string?>? resolveBinary = null,
        Action<string>? createDirectory = null, Func<string, bool>? pathExists = null)
    {
        _launcher = launcher;
        _resolveBinary = resolveBinary;
        _createDirectory = createDirectory;
        _pathExists = pathExists;
    }

    /// <summary>Load the signed-in user's GitHub repos. Empty (with a
    /// status message) when <c>gh</c> is missing/unauthenticated — the
    /// caller falls back to the manual clone URL field.</summary>
    public async Task LoadReposAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var repos = await GitHubCLI.ListReposAsync(_launcher, resolveBinary: _resolveBinary, ct: ct);
            Repos.Clear();
            foreach (var r in repos) Repos.Add(r);
            StatusMessage = repos.Count == 0
                ? "No repos found — is `gh` installed and signed in?"
                : null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Clone <see cref="CloneUrl"/> into <paramref name="parentDir"/>.
    /// No-op on a blank URL or while busy.</summary>
    public async Task CloneAsync(string parentDir, CancellationToken ct = default)
    {
        var url = CloneUrl.Trim();
        if (url.Length == 0 || IsBusy) return;

        IsBusy = true;
        try
        {
            var (ok, path, output) = await CodeGit.CloneAsync(_launcher, url, parentDir, _resolveBinary,
                _createDirectory, _pathExists, ct);
            if (ok)
            {
                ResultRepoPath = path;
                StatusMessage = null;
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

    /// <summary>Create a new repo on GitHub named <see cref="NewRepoName"/>
    /// and clone it into <paramref name="parentDir"/>. No-op on a blank
    /// name or while busy.</summary>
    public async Task CreateRepoAsync(string parentDir, CancellationToken ct = default)
    {
        var name = NewRepoName.Trim();
        if (name.Length == 0 || IsBusy) return;

        IsBusy = true;
        try
        {
            var (ok, path, output) = await GitHubCLI.CreateRepoAsync(_launcher, name, NewRepoIsPrivate, parentDir,
                _resolveBinary, _createDirectory, _pathExists, ct);
            if (ok)
            {
                ResultRepoPath = path;
                StatusMessage = null;
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
