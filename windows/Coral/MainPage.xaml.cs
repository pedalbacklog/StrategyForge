using System.Collections.Specialized;
using System.ComponentModel;
using Coral.Core.Models;
using Coral.Core.Services;
using Coral.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    /// status/paste-code UI. Claude only for now, matching ChatViewModel's own
    /// single-provider scope.</summary>
    public ConnectViewModel ConnectViewModel { get; }

    /// <summary>Drives the "Open Repo" flyout — browse/clone/create a GitHub
    /// repo. Opening one hands off to a NEW <see cref="MainWindow"/> rather
    /// than swapping this page's own (OneTime-bound) repo in place.</summary>
    public RepoPickerViewModel RepoPickerViewModel { get; }

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
        RepoPickerViewModel = new RepoPickerViewModel(new RealProcessLauncher());

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

    private void OnConnectFlyoutClosed(object sender, object e) => ConnectViewModel.CancelConnect();

    private async void OnSubmitCodeClick(object sender, RoutedEventArgs e) => await ConnectViewModel.SubmitCodeAsync();

    private void OnCodeModeClick(object sender, RoutedEventArgs e) => new CodeModeWindow(RepoPath).Activate();

    private async void OnOpenRepoFlyoutOpened(object sender, object e) => await RepoPickerViewModel.LoadReposAsync();

    private async void OnRepoSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (((ListView)sender).SelectedItem is not RepoRef repo) return;
        RepoPickerViewModel.CloneUrl = repo.Url;
        await RepoPickerViewModel.CloneAsync(DefaultReposParentDir());
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
