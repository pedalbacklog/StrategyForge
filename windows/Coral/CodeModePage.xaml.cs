using System.ComponentModel;
using Coral.Core.Services;
using Coral.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

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
/// doc comment). Auto-PR's toggle lives on <see cref="PullRequestViewModel"/>
/// (<c>AutoPr</c>, persisted via <c>AppSettings</c>); the auto-fire-on-run-
/// finish wiring lives here (<see cref="OnChatViewModelPropertyChanged"/>),
/// observing <c>ChatViewModel.IsSending</c> from outside rather than having
/// <c>ChatViewModel</c> itself know about Code Mode. Deliberately NOT here:
/// repo browse/create, and loading a file's raw (non-diff) contents — each
/// is its own unported piece.
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

        // Not unsubscribed on close — same precedent MainPage already set
        // for its own RepoPickerViewModel subscription. Harmless even if it
        // piles up across repeated Code Mode opens for the same chat:
        // ShipAsync's own IsBusy guard (set synchronously before any await)
        // turns a redundant same-tick fire into a no-op, not a duplicate ship.
        ChatViewModel.PropertyChanged += OnChatViewModelPropertyChanged;
    }

    /// <summary>Port of <c>CodeModeView.swift</c>'s
    /// <c>.onChange(of: vm.isRunning)</c>: once a turn finishes, ship
    /// automatically if Auto-PR is on and there's something to ship. Doesn't
    /// call <see cref="PullRequestViewModel.ShouldAutoShip"/> directly —
    /// that static method and this page's own <c>PullRequestViewModel</c>
    /// property share a name, and C# won't let a static member be reached
    /// through what resolves to an instance reference — so the same
    /// four-part check is inlined here instead; the static method stays as
    /// the tested, documented spec for it.</summary>
    private async void OnChatViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Coral.Core.ViewModels.ChatViewModel.IsSending) || ChatViewModel.IsSending) return;
        if (!PullRequestViewModel.AutoPr) return;
        if (ViewModel.Branch is not { } branch) return;

        await ViewModel.RefreshAsync();
        if (!GitHubCLI.IsInstalled() || ViewModel.ChangedFiles.Count == 0) return;

        var message = ChatViewModel.DraftCommitMessage();
        if (string.IsNullOrWhiteSpace(message)) message = "Update";
        var body = ChatViewModel.DraftPrBody();
        await PullRequestViewModel.ShipAsync(_repoPath, branch, message, message, body,
            anyStaged: ViewModel.StagedFiles.Count > 0, auto: true);
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

    private async void OnPrFlyoutOpened(object sender, object e)
    {
        if (ViewModel.Branch is { } branch) await PullRequestViewModel.RefreshAsync(branch);
    }

    private bool _prFlyoutClosingAllowed;

    /// <summary>Blocks the PR flyout's light-dismiss unconditionally — real
    /// bug caught on real Windows: switching windows mid-edit (e.g. to check
    /// something else while drafting a title/description) silently closed
    /// it, though the typed text did survive since the ViewModel keeps it
    /// regardless of the popup's visibility. Unlike "Connect Claude"'s
    /// Flyout, there's no IsConnecting-style busy window here to key the
    /// block off, so it's unconditional — paired with an explicit ✕ button
    /// (<see cref="OnClosePrFlyoutClick"/>), the only way to actually close
    /// it, which sets <see cref="_prFlyoutClosingAllowed"/> right before
    /// calling <c>Hide()</c> so this handler lets that one through.</summary>
    private void OnPrFlyoutClosing(FlyoutBase sender, FlyoutBaseClosingEventArgs e)
    {
        if (_prFlyoutClosingAllowed) { _prFlyoutClosingAllowed = false; return; }
        e.Cancel = true;
    }

    private void OnClosePrFlyoutClick(object sender, RoutedEventArgs e)
    {
        _prFlyoutClosingAllowed = true;
        PrFlyout.Hide();
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

    private async void OnCreateBranchClick(object sender, RoutedEventArgs e) => await CreateBranchFromFlyoutAsync();

    private async void OnNewBranchBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await CreateBranchFromFlyoutAsync();
    }

    /// <summary>Shared by the "Create" button and Enter in the branch-name
    /// box. Closes the flyout and clears the box on success — otherwise
    /// (real bug caught on real Windows) nothing told the user a branch had
    /// been created, and the flyout just sat there open. Left open on
    /// failure so the error in <see cref="GitPanelViewModel.StatusMessage"/>
    /// stays visible and the name can be retried.</summary>
    private async Task CreateBranchFromFlyoutAsync()
    {
        var name = NewBranchBox.Text;
        if (await ViewModel.CreateBranchAsync(name))
        {
            NewBranchBox.Text = "";
            NewBranchFlyout.Hide();
        }
    }

    /// <summary>Mirrors CodeModeView.swift's collapsible terminal — plain
    /// code-behind toggle rather than a bound bool, since it's pure UI state
    /// with no ViewModel consumer.</summary>
    private void OnToggleTerminalClick(object sender, RoutedEventArgs e) =>
        TerminalLog.Visibility = TerminalLog.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
}
