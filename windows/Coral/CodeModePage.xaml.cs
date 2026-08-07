using Coral.Core.Services;
using Coral.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Coral;

/// <summary>
/// Code Mode's git panel (Fase 7): changed files on the left with per-file
/// stage/revert, the selected file's diff on the right, a branch bar,
/// commit/push, and a "Pull Request" flyout — wired to
/// <see cref="GitPanelViewModel"/> and <see cref="PullRequestViewModel"/>
/// respectively (kept separate ViewModels on purpose — see each one's own
/// doc comment). Deliberately NOT here: Auto-PR, the terminal panel, repo
/// browse/create, and loading a file's raw (non-diff) contents — each is
/// its own unported piece.
/// </summary>
public sealed partial class CodeModePage : Page
{
    public GitPanelViewModel ViewModel { get; }
    public PullRequestViewModel PullRequestViewModel { get; }

    public CodeModePage(string repoPath)
    {
        // Set before InitializeComponent(): default (OneTime) x:Bind
        // expressions evaluate during that call — see MainPage.xaml.cs for
        // the same ordering requirement.
        ViewModel = new GitPanelViewModel(new RealProcessLauncher(), repoPath);
        PullRequestViewModel = new PullRequestViewModel(new RealProcessLauncher(), repoPath);

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
}
