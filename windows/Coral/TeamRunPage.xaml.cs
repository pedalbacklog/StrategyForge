using Coral.Core.Models;
using Coral.Core.Services;
using Coral.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Coral;

/// <summary>Code-behind for the "Run for real" page — see
/// <see cref="TeamRunViewModel"/>'s doc comment for scope.</summary>
public sealed partial class TeamRunPage : Page
{
    public TeamRunViewModel ViewModel { get; }

    public TeamRunPage(string repoPath, Strategy strategy, string binary)
    {
        // Set before InitializeComponent(): default (OneTime) x:Bind
        // expressions evaluate during that call — see MainPage.xaml.cs for
        // the same ordering requirement.
        ViewModel = new TeamRunViewModel(repoPath, strategy,
            new ProviderOneShotRunner(new RealProcessLauncher(), new Win32PseudoConsoleLauncher()),
            new RealProcessLauncher(), binary);

        InitializeComponent();
    }

    private async void OnRunClick(object sender, RoutedEventArgs e) => await ViewModel.RunAsync();

    private async void OnTaskBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await ViewModel.RunAsync();
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e) => await ViewModel.ApplyAsync();

    private async void OnDiscardClick(object sender, RoutedEventArgs e) => await ViewModel.DiscardAsync();
}
