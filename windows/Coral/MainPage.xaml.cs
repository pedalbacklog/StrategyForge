using Coral.Core.Services;
using Coral.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Coral;

/// <summary>
/// Minimal P0 chat page (windows/PORT-PLAN.md Fase 5): a prompt box, the
/// transcript, and the live activity panel, wired to <see cref="ChatViewModel"/>.
/// A <see cref="Page"/> rather than living directly in <see cref="MainWindow"/>:
/// WinUI 3's <c>Window</c> isn't a <c>FrameworkElement</c>, so x:Bind doesn't
/// work at a Window's root — the standard pattern is a Window that just hosts
/// a Page, with the actual bindings living on the Page. No repo picker yet —
/// defaults to the user's home directory; no settings UI (model/effort/
/// permission-mode are hardcoded sane defaults) — those are follow-up work,
/// not part of this minimal slice.
/// </summary>
public sealed partial class MainPage : Page
{
    public string RepoPath { get; }
    public ChatViewModel ViewModel { get; }

    public MainPage()
    {
        // Set everything an x:Bind in the XAML reads BEFORE InitializeComponent():
        // default (OneTime) x:Bind expressions evaluate during that call, so a
        // property assigned only afterward would bind to null/default and never
        // update (OneTime bindings don't re-evaluate).
        RepoPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ViewModel = new ChatViewModel(new RealProcessLauncher(), RepoPath);

        InitializeComponent();

        ViewModel.Messages.CollectionChanged += (_, _) => ScrollChatToEnd();
    }

    private async void OnSendClick(object sender, RoutedEventArgs e) => await ViewModel.SendAsync();

    private async void OnPromptBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await ViewModel.SendAsync();
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => ViewModel.CancelCurrentTurn();

    private void ScrollChatToEnd()
    {
        if (ViewModel.Messages.Count == 0) return;
        ChatList.ScrollIntoView(ViewModel.Messages[^1]);
    }
}
