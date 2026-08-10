using System.Collections.ObjectModel;
using Coral.Core.Services;

namespace Coral.Core.ViewModels;

/// <summary>
/// Drives Code Mode's git panel: the changed-files list, the selected file's
/// diff, staging, revert, commit, push, and branch switching. A fresh design
/// rather than a mechanical port — <c>CodeModeView.swift</c> keeps this state
/// as plain <c>@State</c> on the View itself (SwiftUI's norm), not a separate
/// ViewModel class, so there's no 1:1 Swift type to translate; this follows
/// the same shape <see cref="ChatViewModel"/> and <see cref="ConnectViewModel"/>
/// already established for this port instead (a ViewModel in <c>Coral.Core</c>,
/// no WinUI dependency, an injected <see cref="IProcessLauncher"/> so it's
/// unit-testable with a fake).
///
/// SCOPE: only what <see cref="CodeGit"/> ports — changed files, per-file
/// diff, stage/unstage/revert, commit/commit-staged, push, branch
/// create/checkout. The GitHub PR integration is deliberately a SEPARATE
/// ViewModel (<see cref="PullRequestViewModel"/>) even though
/// <see cref="GitHubCLI"/> is ported now — keeps this one focused on git
/// alone, the same separation <c>ConnectViewModel</c> keeps from
/// <c>ChatViewModel</c> on <c>MainPage</c>. Still not wired anywhere: Auto-PR,
/// the terminal panel, and file content loading for the "file" (non-diff)
/// view mode (`CodeModeView.swift`'s <c>fileText</c>) — this ViewModel is the
/// git panel specifically, not the whole Code Mode workspace.
/// </summary>
public sealed class GitPanelViewModel : ObservableObject
{
    private readonly IProcessLauncher _launcher;
    private readonly string _repoPath;
    private readonly Func<string, string?>? _resolveBinary;
    private readonly HashSet<string> _stagedFiles = new();

    public ObservableCollection<ChangedFile> ChangedFiles { get; } = new();
    public ObservableCollection<string> Branches { get; } = new();

    /// <summary>Absolute paths of currently-staged files — matches
    /// <see cref="ChangedFile.Path"/> (repo-relative) joined with the repo
    /// root, the same convention <c>CodeGit.swift</c>'s <c>stagedFiles</c>
    /// uses to match an agent's absolute edited-file paths.</summary>
    public IReadOnlySet<string> StagedFiles => _stagedFiles;

    private string? _selectedFile;
    public string? SelectedFile { get => _selectedFile; private set => SetProperty(ref _selectedFile, value); }

    private IReadOnlyList<DiffLine>? _diffLines;
    public IReadOnlyList<DiffLine>? DiffLines { get => _diffLines; private set => SetProperty(ref _diffLines, value); }

    private string? _branch;
    public string? Branch { get => _branch; private set => SetProperty(ref _branch, value); }

    private string _commitMessage = "";
    public string CommitMessage { get => _commitMessage; set => SetProperty(ref _commitMessage, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }

    private string? _statusMessage;
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    public GitPanelViewModel(IProcessLauncher launcher, string repoPath, Func<string, string?>? resolveBinary = null)
    {
        _launcher = launcher;
        _repoPath = repoPath;
        _resolveBinary = resolveBinary;
    }

    public bool IsStaged(ChangedFile file) => _stagedFiles.Contains(Path.Combine(_repoPath, file.Path));

    /// <summary>Reload changed files, staged files, current branch, and the
    /// branch list; re-selects the first changed file if the previous
    /// selection no longer exists (matches <c>onChange(of: vm.editedFiles)</c>
    /// re-selecting in the Swift original).</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var files = await CodeGit.ChangedFilesAsync(_launcher, _repoPath, _resolveBinary, ct: ct);
            ChangedFiles.Clear();
            foreach (var f in files) ChangedFiles.Add(f);

            var staged = await CodeGit.StagedFilesAsync(_launcher, _repoPath, _resolveBinary, ct);
            _stagedFiles.Clear();
            foreach (var s in staged) _stagedFiles.Add(s);
            OnPropertyChanged(nameof(StagedFiles));

            // Branches populated BEFORE Branch is (re)assigned: the ComboBox's
            // x:Bind SelectedItem is a OneWay push sourced from Branch — if
            // Branch changes while Branches doesn't contain that value yet,
            // WinUI3 can't find a matching item to highlight and falls back to
            // auto-selecting whatever ends up at index 0 once items are added,
            // which looked like the wrong branch was "selected" (a real bug
            // caught on real Windows, not visible from Coral.Core's own tests
            // — no WinUI dependency here to observe it).
            var branches = await CodeGit.BranchesAsync(_launcher, _repoPath, _resolveBinary, ct);
            Branches.Clear();
            foreach (var b in branches) Branches.Add(b);

            Branch = await CodeGit.CurrentBranchAsync(_launcher, _repoPath, _resolveBinary, ct);

            if (SelectedFile is null || !ChangedFiles.Any(f => f.Path == SelectedFile))
            {
                await SelectFileAsync(ChangedFiles.Count > 0 ? ChangedFiles[0].Path : null, ct);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Select a file and load its diff.</summary>
    public async Task SelectFileAsync(string? path, CancellationToken ct = default)
    {
        SelectedFile = path;
        DiffLines = path is null ? null : await CodeGit.DiffAsync(_launcher, _repoPath, path, _resolveBinary, ct);
    }

    /// <summary>Stage <paramref name="file"/> if it's unstaged, or unstage it
    /// if it's already staged.</summary>
    public async Task ToggleStageAsync(ChangedFile file, CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var absolute = Path.Combine(_repoPath, file.Path);
            var wasStaged = _stagedFiles.Contains(absolute);
            var ok = wasStaged
                ? await CodeGit.UnstageAsync(_launcher, _repoPath, file.Path, _resolveBinary, ct)
                : await CodeGit.StageAsync(_launcher, _repoPath, file.Path, _resolveBinary, ct);
            if (ok)
            {
                if (wasStaged) _stagedFiles.Remove(absolute); else _stagedFiles.Add(absolute);
                OnPropertyChanged(nameof(StagedFiles));
            }
            else
            {
                StatusMessage = $"Error: couldn't update staging for {file.Path}";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Discard an agent's changes to one file, then refresh.</summary>
    public async Task RevertAsync(string repoRelativePath, CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var ok = await CodeGit.RevertAsync(_launcher, _repoPath, repoRelativePath, _resolveBinary, ct);
            if (ok) await RefreshAsync(ct);
            else StatusMessage = $"Error: couldn't revert {repoRelativePath}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Commit — everything (<paramref name="stagedOnly"/> false) or
    /// only what's staged. No-op on a blank message or while busy.</summary>
    public async Task CommitAsync(bool stagedOnly = false, CancellationToken ct = default)
    {
        var message = CommitMessage.Trim();
        if (message.Length == 0 || IsBusy) return;

        IsBusy = true;
        try
        {
            var (ok, output) = stagedOnly
                ? await CodeGit.CommitStagedAsync(_launcher, _repoPath, message, _resolveBinary, ct)
                : await CodeGit.CommitAsync(_launcher, _repoPath, message, _resolveBinary, ct);
            if (ok)
            {
                StatusMessage = "Committed.";
                CommitMessage = "";
                await RefreshAsync(ct);
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

    /// <summary>Push the current branch to origin.</summary>
    public async Task PushAsync(CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var (ok, output) = await CodeGit.PushAsync(_launcher, _repoPath, _resolveBinary, ct);
            StatusMessage = ok ? "Pushed." : $"Error: {output}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Create a branch off HEAD and switch to it, then refresh.
    /// Returns whether it succeeded, so a caller (e.g. the "New" branch
    /// flyout) can react — close itself, clear its textbox — only on
    /// success, matching the feedback <see cref="CommitAsync"/>/
    /// <see cref="PushAsync"/> already give.</summary>
    public async Task<bool> CreateBranchAsync(string name, CancellationToken ct = default)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0 || IsBusy) return false;

        IsBusy = true;
        try
        {
            var (ok, output) = await CodeGit.CreateBranchAsync(_launcher, _repoPath, trimmed, _resolveBinary, ct);
            if (ok)
            {
                StatusMessage = $"Created branch '{trimmed}'.";
                await RefreshAsync(ct);
            }
            else
            {
                StatusMessage = $"Error: {output}";
            }
            return ok;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Switch to an existing branch, then refresh.</summary>
    public async Task CheckoutAsync(string branch, CancellationToken ct = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var (ok, output) = await CodeGit.CheckoutAsync(_launcher, _repoPath, branch, _resolveBinary, ct);
            if (ok)
            {
                StatusMessage = $"Switched to '{branch}'.";
                await RefreshAsync(ct);
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
