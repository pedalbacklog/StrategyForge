using Coral.Core.Services;
using Coral.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Coral;

/// <summary>
/// Code Mode's git panel (Fase 7): changed files on the left with per-file
/// stage/revert, the selected file's diff on the right, a branch bar,
/// commit/push, a "Pull Request" flyout, and a collapsible terminal panel —
/// wired to <see cref="GitPanelViewModel"/>, <see cref="PullRequestViewModel"/>
/// (including its one-tap "Commit + PR", see <see cref="OnShipClick"/>), and
/// the chat's own <see cref="ChatViewModel"/> (for <c>CommandLog</c> and the
/// commit-message/PR-body drafting <see cref="OnShipClick"/> uses)
/// respectively (kept separate ViewModels on purpose — see each one's own
/// doc comment). Deliberately NOT here: Auto-PR's opt-in auto-fire-on-run-
/// finish (see <see cref="PullRequestViewModel"/>'s doc comment), repo
/// browse/create, and loading a file's raw (non-diff) contents — each is its
/// own unported piece.
/// </summary>
public sealed partial class CodeModePage : Page
{
    private readonly string _repoPath;

    public GitPanelViewModel ViewModel { get; }
    public PullRequestViewModel PullRequestViewModel { get; }
    public ChatViewModel ChatViewModel { get; }

    public CodeModePage(string repoPath, ChatViewModel chatViewModel)
    {
        // Set before InitializeComponent(): default (OneTime) x:Bind
        // expressions evaluate during that call — see MainPage.xaml.cs for
        // the same ordering requirement.
        _repoPath = repoPath;
        ViewModel = new GitPanelViewModel(new RealProcessLauncher(), repoPath);
        PullRequestViewModel = new PullRequestViewModel(new RealProcessLauncher(), repoPath);
        ChatViewModel = chatViewModel;

        InitializeComponent();
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

    private async void OnPrFlyoutOpened(object sender, object e)
    {
        if (ViewModel.Branch is { } branch) await PullRequestViewModel.RefreshAsync(branch);
    }

    private async void OnCreatePrClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Branch is { } branch) await PullRequestViewModel.CreateAsync(branch);
    }

    private async void OnMergePrClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Branch is { } branch) await PullRequestViewModel.MergeAsync(branch);
    }

    private async void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ListView)sender).SelectedItem is ChangedFile file) await ViewModel.SelectFileAsync(file.Path);
    }

    private async void OnToggleStageClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is ChangedFile file) await ViewModel.ToggleStageAsync(file);
    }

    private async void OnRevertClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is ChangedFile file) await ViewModel.RevertAsync(file.Path);
    }

    private async void OnCommitClick(object sender, RoutedEventArgs e) => await ViewModel.CommitAsync();

    private async void OnPushClick(object sender, RoutedEventArgs e) => await ViewModel.PushAsync();

    /// <summary>One-tap "Commit + PR" — commits (everything, or just what's
    /// staged), pushes, and opens/updates the branch's PR, via
    /// <see cref="PullRequestViewModel.ShipAsync"/>. Falls back to a drafted
    /// commit message/PR body (from the chat's last reply) when the git
    /// panel's commit box and the PR flyout's title/body are blank — same
    /// fallback CodeModeView.swift's commitAndPR/draftMessage/prBody use.
    /// Not the opt-in Auto-PR (auto-fires when a run finishes) — that's still
    /// deliberately unwired, see this type's doc comment.</summary>
    private async void OnShipClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Branch is not { } branch) return;
        var message = string.IsNullOrWhiteSpace(ViewModel.CommitMessage)
            ? ChatViewModel.DraftCommitMessage() : ViewModel.CommitMessage;
        if (string.IsNullOrWhiteSpace(message)) message = "Update";
        var title = string.IsNullOrWhiteSpace(PullRequestViewModel.Title) ? message : PullRequestViewModel.Title;
        var body = string.IsNullOrWhiteSpace(PullRequestViewModel.Body)
            ? ChatViewModel.DraftPrBody() : PullRequestViewModel.Body;

        await PullRequestViewModel.ShipAsync(_repoPath, branch, message, title, body,
            anyStaged: ViewModel.StagedFiles.Count > 0);
        await ViewModel.RefreshAsync();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

    private async void OnBranchSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ComboBox)sender).SelectedItem is string branch && branch != ViewModel.Branch)
        {
            await ViewModel.CheckoutAsync(branch);
        }
    }

    private async void OnCreateBranchClick(object sender, RoutedEventArgs e) =>
        await ViewModel.CreateBranchAsync(NewBranchBox.Text);

    /// <summary>Mirrors CodeModeView.swift's collapsible terminal — plain
    /// code-behind toggle rather than a bound bool, since it's pure UI state
    /// with no ViewModel consumer.</summary>
    private void OnToggleTerminalClick(object sender, RoutedEventArgs e) =>
        TerminalLog.Visibility = TerminalLog.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
}
