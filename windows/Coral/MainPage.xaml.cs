using System.Collections.Specialized;
using System.ComponentModel;
using Coral.Core.Models;
using Coral.Core.Services;
using Coral.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Coral;

/// <summary>
/// Minimal P0 chat page (windows/PORT-PLAN.md Fase 5): a prompt box, the
/// transcript, and the live activity panel, wired to <see cref="ChatViewModel"/>.
/// A <see cref="Page"/> rather than living directly in <see cref="MainWindow"/>:
/// WinUI 3's <c>Window</c> isn't a <c>FrameworkElement</c>, so x:Bind doesn't
/// work at a Window's root — the standard pattern is a Window that just hosts
/// a Page, with the actual bindings living on the Page. Defaults to the
/// user's home directory unless opened with an explicit repo path (see
/// RepoPickerViewModel's "Open Repo" flyout); no settings UI (model/effort/
/// permission-mode are hardcoded sane defaults) — that's follow-up work,
/// not part of this minimal slice.
/// </summary>
public sealed partial class MainPage : Page
{
    public string RepoPath { get; }
    public ChatViewModel ViewModel { get; }

    /// <summary>Drives the "Connect Claude" flyout (Fase 6) — install-if-missing
    /// + headless sign-in against the real CLI, streamed into the flyout's log/
    /// status/paste-code UI.</summary>
    public ConnectViewModel ConnectViewModel { get; }

    /// <summary>Same flow as <see cref="ConnectViewModel"/>, for Codex (OpenAI).
    /// Cross-provider role reassignment (<c>AdvisorEngine.AssignProviders</c>)
    /// only has something to assign once a second provider is actually
    /// connected — this button is what makes that reachable from the UI.</summary>
    public ConnectViewModel ConnectCodexViewModel { get; }

    /// <summary>Same flow as <see cref="ConnectViewModel"/>, for Gemini (Google).</summary>
    public ConnectViewModel ConnectGeminiViewModel { get; }

    /// <summary>Drives the "Open Repo" flyout — browse/clone/create a GitHub
    /// repo. Opening one hands off to a NEW <see cref="MainWindow"/> rather
    /// than swapping this page's own (OneTime-bound) repo in place.</summary>
    public RepoPickerViewModel RepoPickerViewModel { get; }

    /// <summary>Drives the "Strategy" flyout (P0 item 2, Phase 1) — pick one
    /// of the 15 built-in templates and generate its files into
    /// <see cref="RepoPath"/>. See its own doc comment for why nothing is
    /// written here automatically on open.</summary>
    public StrategyPickerViewModel StrategyPickerViewModel { get; }

    /// <summary>Drives the same flyout's "Suggest a team" section (P0 item
    /// 2, Phase 3 — the heuristic half only, see its own doc comment).</summary>
    public AdvisorViewModel AdvisorViewModel { get; }

    private ScrollViewer? _chatScrollViewer;

    public MainPage(string? repoPath = null)
    {
        // Set everything an x:Bind in the XAML reads BEFORE InitializeComponent():
        // default (OneTime) x:Bind expressions evaluate during that call, so a
        // property assigned only afterward would bind to null/default and never
        // update (OneTime bindings don't re-evaluate).
        RepoPath = repoPath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ViewModel = new ChatViewModel(new RealProcessLauncher(), RepoPath);
        ConnectViewModel = new ConnectViewModel(new RealProcessLauncher(), new Win32PseudoConsoleLauncher(),
            AIProvider.Claude);
        ConnectCodexViewModel = new ConnectViewModel(new RealProcessLauncher(), new Win32PseudoConsoleLauncher(),
            AIProvider.Openai);
        ConnectGeminiViewModel = new ConnectViewModel(new RealProcessLauncher(), new Win32PseudoConsoleLauncher(),
            AIProvider.Gemini);
        RepoPickerViewModel = new RepoPickerViewModel(new RealProcessLauncher());
        StrategyPickerViewModel = new StrategyPickerViewModel(RepoPath);
        AdvisorViewModel = new AdvisorViewModel();

        InitializeComponent();

        ViewModel.Messages.CollectionChanged += OnMessagesChanged;
        RepoPickerViewModel.PropertyChanged += OnRepoPickerPropertyChanged;
    }

    /// <summary>Scroll on every new message AND on every streamed delta into the
    /// current one — a growing message mutates its own Text in place (see
    /// ChatViewModel.AddAssistantMessage), which doesn't raise
    /// CollectionChanged, only that item's own PropertyChanged.</summary>
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (ChatMessage added in e.NewItems) added.PropertyChanged += OnMessagePropertyChanged;
        }
        if (e.OldItems is not null)
        {
            foreach (ChatMessage removed in e.OldItems) removed.PropertyChanged -= OnMessagePropertyChanged;
        }
        ScrollChatToEnd();
    }

    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e) => ScrollChatToEnd();

    private void OnChatListLoaded(object sender, RoutedEventArgs e) => _chatScrollViewer ??= FindScrollViewer(ChatList);

    private async void OnSendClick(object sender, RoutedEventArgs e) => await ViewModel.SendAsync();

    private async void OnPromptBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await ViewModel.SendAsync();
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => ViewModel.CancelCurrentTurn();

    private async void OnConnectFlyoutOpened(object sender, object e) => await ConnectViewModel.ConnectAsync();

    /// <summary>Stop the Flyout from light-dismissing (its default: any
    /// outside click or window-focus change closes it) while a connect is in
    /// flight — otherwise the sign-in browser window stealing focus hides the
    /// "paste the code" box the instant it appears, often before the user can
    /// even see it. The macOS original doesn't need this: a modal `.sheet`
    /// never auto-dismisses on focus loss in the first place.</summary>
    private void OnConnectFlyoutClosing(FlyoutBase sender, FlyoutBaseClosingEventArgs e)
    {
        if (ConnectViewModel.IsConnecting) e.Cancel = true;
    }

    private async void OnSubmitCodeClick(object sender, RoutedEventArgs e) => await ConnectViewModel.SubmitCodeAsync();

    private async void OnConnectCodexFlyoutOpened(object sender, object e) => await ConnectCodexViewModel.ConnectAsync();

    private void OnConnectCodexFlyoutClosing(FlyoutBase sender, FlyoutBaseClosingEventArgs e)
    {
        if (ConnectCodexViewModel.IsConnecting) e.Cancel = true;
    }

    private async void OnSubmitCodexCodeClick(object sender, RoutedEventArgs e) => await ConnectCodexViewModel.SubmitCodeAsync();

    private async void OnConnectGeminiFlyoutOpened(object sender, object e) => await ConnectGeminiViewModel.ConnectAsync();

    private void OnConnectGeminiFlyoutClosing(FlyoutBase sender, FlyoutBaseClosingEventArgs e)
    {
        if (ConnectGeminiViewModel.IsConnecting) e.Cancel = true;
    }

    private async void OnSubmitGeminiCodeClick(object sender, RoutedEventArgs e) => await ConnectGeminiViewModel.SubmitCodeAsync();

    private void OnCodeModeClick(object sender, RoutedEventArgs e) => new CodeModeWindow(RepoPath, ViewModel).Activate();

    /// <summary>Picking a template writes its files (via
    /// <see cref="StrategyPickerViewModel.SelectAsync"/>) and, on success,
    /// updates <see cref="ChatViewModel.Model"/> to the orchestrator's
    /// suggested model — so the very next turn runs against the newly
    /// generated team, no restart needed. Every template has exactly one
    /// orchestrator (<c>Strategy.Validate()</c> would flag anything else),
    /// so the null-conditional below is defensive, not an expected path.</summary>
    private async void OnStrategySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ListView)sender).SelectedItem is not Strategy strategy) return;
        if (await StrategyPickerViewModel.SelectAsync(strategy) && strategy.Orchestrator is { } orchestrator)
        {
            ViewModel.Model = orchestrator.Model.ToRawValue();
        }
    }

    private bool _strategyFlyoutClosingAllowed;

    /// <summary>Blocks the Strategy flyout's light-dismiss unconditionally —
    /// real bug caught on real Windows: the "Use this" row appearing after
    /// "Suggest" grows this flyout's content, and the resulting reposition
    /// (it stays anchored under the "Strategy" button) was enough to
    /// light-dismiss it before the recommendation was ever visible — looked
    /// like a flash to an identical flyout that then closed itself. Same
    /// fix as the Pull Request flyout: unconditional block, paired with an
    /// explicit ✕ button (<see cref="OnCloseStrategyFlyoutClick"/>) as the
    /// only way to actually close it.</summary>
    private void OnStrategyFlyoutClosing(FlyoutBase sender, FlyoutBaseClosingEventArgs e)
    {
        if (_strategyFlyoutClosingAllowed) { _strategyFlyoutClosingAllowed = false; return; }
        e.Cancel = true;
    }

    private void OnCloseStrategyFlyoutClick(object sender, RoutedEventArgs e)
    {
        _strategyFlyoutClosingAllowed = true;
        StrategyFlyout.Hide();
    }

    /// <summary>Opens the "Edit team" window (P0 item 2, Phase 2) for
    /// whatever strategy is currently active. The button that triggers
    /// this is disabled while <see cref="Coral.Core.ViewModels.StrategyPickerViewModel.HasSelectedStrategy"/>
    /// is false, so the null-conditional below is defensive, not an
    /// expected path.</summary>
    private void OnEditTeamClick(object sender, RoutedEventArgs e)
    {
        if (StrategyPickerViewModel.SelectedStrategy is not { } strategy) return;
        new StrategyEditorWindow(strategy, StrategyPickerViewModel, ViewModel).Activate();
    }

    private void OnSuggestTeamClick(object sender, RoutedEventArgs e)
    {
        AdvisorViewModel.ChosenTeamName = StrategyPickerViewModel.SelectedStrategy?.Name;
        AdvisorViewModel.Suggest();
    }

    private void OnAdvisorTaskBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        AdvisorViewModel.ChosenTeamName = StrategyPickerViewModel.SelectedStrategy?.Name;
        AdvisorViewModel.Suggest();
    }

    /// <summary>Switches the displayed tier (Economy/Recommended/Max) — the
    /// clicked chip's tier id travels in its own <c>Tag</c>, set from
    /// <c>TierChip.Id</c> in the XAML template.</summary>
    private void OnTierChipClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tierId) AdvisorViewModel.SelectTier(tierId);
    }

    /// <summary>Applies the Advisor's currently SELECTED tier the exact same
    /// way manually picking a template does — writes the strategy, then
    /// (on success) updates <see cref="ChatViewModel.Model"/>. A no-op if
    /// "Suggest" hasn't produced anything yet (button is disabled in that
    /// state, this is defensive).</summary>
    private async void OnUseSuggestedTeamClick(object sender, RoutedEventArgs e)
    {
        if (AdvisorViewModel.SelectedTier is not { } tier) return;
        if (await StrategyPickerViewModel.SelectAsync(tier.Advice.Strategy))
        {
            ViewModel.Model = tier.Advice.Model.ToRawValue();
        }
    }

    private async void OnOpenRepoFlyoutOpened(object sender, object e) => await RepoPickerViewModel.LoadReposAsync();

    private async void OnRepoSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ListView)sender).SelectedItem is not RepoRef repo) return;
        await RepoPickerViewModel.OpenOrCloneAsync(repo, DefaultReposParentDir());
    }

    private async void OnCloneRepoClick(object sender, RoutedEventArgs e) =>
        await RepoPickerViewModel.CloneAsync(DefaultReposParentDir());

    private async void OnCreateRepoClick(object sender, RoutedEventArgs e) =>
        await RepoPickerViewModel.CreateRepoAsync(DefaultReposParentDir());

    /// <summary>Where a cloned/newly-created repo lands. No folder picker in
    /// this first pass — the user's home directory, same default
    /// <see cref="RepoPath"/> itself falls back to.</summary>
    private static string DefaultReposParentDir() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Once a clone/create succeeds, open a NEW window pointed at
    /// it — see the doc comment on <see cref="RepoPickerViewModel"/>.</summary>
    private void OnRepoPickerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RepoPickerViewModel.ResultRepoPath)) return;
        if (RepoPickerViewModel.ResultRepoPath is not { } path) return;
        new MainWindow(path).Activate();
    }

    /// <summary>Scroll all the way to the bottom of the ListView's real
    /// scrollable content. Deliberately NOT ChatList.ScrollIntoView(lastItem):
    /// that only guarantees the item is visible, which for one tall item that's
    /// still growing (mid-stream) can mean "just its top edge," not "follow the
    /// bottom as it grows" — going straight to the ScrollViewer is the reliable
    /// way to pin to the end.</summary>
    private void ScrollChatToEnd()
    {
        if (_chatScrollViewer is null || ViewModel.Messages.Count == 0) return;
        _chatScrollViewer.ChangeView(null, _chatScrollViewer.ScrollableHeight, null, disableAnimation: true);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scrollViewer) return scrollViewer;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }
}
