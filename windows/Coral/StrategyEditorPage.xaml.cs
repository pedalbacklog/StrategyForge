using Coral.Core.Models;
using Coral.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Coral;

/// <summary>Code-behind for the "Edit team" page — see
/// <see cref="StrategyEditorViewModel"/>'s doc comment for scope. Most of
/// the per-role controls (model, instance count, tools) are wired here in
/// code-behind rather than via x:Bind, since <see cref="AgentRole"/> isn't
/// an observable type and a couple of the fields (an enum into a fixed
/// ComboBoxItem list, an int into a text box) don't have a clean x:Bind
/// path without a converter that isn't worth it for this small a surface —
/// see the XAML file's own comments at each control.</summary>
public sealed partial class StrategyEditorPage : Page
{
    public StrategyEditorViewModel ViewModel { get; }
    private readonly StrategyPickerViewModel _strategyPickerViewModel;
    private readonly ChatViewModel _chatViewModel;
    private readonly Window _window;

    public StrategyEditorPage(Strategy strategy, StrategyPickerViewModel strategyPickerViewModel,
        ChatViewModel chatViewModel, Window window)
    {
        // Set before InitializeComponent(): default (OneTime) x:Bind
        // expressions evaluate during that call — see MainPage.xaml.cs for
        // the same ordering requirement.
        ViewModel = new StrategyEditorViewModel(strategy);
        _strategyPickerViewModel = strategyPickerViewModel;
        _chatViewModel = chatViewModel;
        // A Page has no built-in way to reach its own hosting Window in
        // WinUI 3 (no Window.Current, unlike UWP) — the caller
        // (StrategyEditorWindow) passes itself in so "Cancel" can close it.
        _window = window;

        InitializeComponent();
    }

    private void OnRoleFieldChanged(object sender, RoutedEventArgs e) => ViewModel.Revalidate();

    private static AgentRole? RoleOf(object sender) => (sender as FrameworkElement)?.DataContext as AgentRole;

    private void OnModelComboLoaded(object sender, RoutedEventArgs e)
    {
        if (RoleOf(sender) is not { } role) return;
        var combo = (ComboBox)sender;
        combo.SelectedIndex = role.Model switch
        {
            ClaudeModel.Opus5 => 0,
            ClaudeModel.Fable5 => 1,
            ClaudeModel.Sonnet5 => 2,
            ClaudeModel.Haiku45 => 3,
            _ => 2, // legacy Opus48 or anything unrecognized — nearest selectable default
        };
    }

    private void OnModelComboSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RoleOf(sender) is not { } role) return;
        if (((ComboBox)sender).SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        role.Model = tag switch
        {
            "Opus5" => ClaudeModel.Opus5,
            "Fable5" => ClaudeModel.Fable5,
            "Sonnet5" => ClaudeModel.Sonnet5,
            "Haiku45" => ClaudeModel.Haiku45,
            _ => role.Model,
        };
        ViewModel.Revalidate();
    }

    private void OnCountBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (RoleOf(sender) is not { } role) return;
        ((TextBox)sender).Text = role.Count.ToString();
    }

    private void OnCountBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        // Real bug caught on real Windows: leaving this parse-failure case
        // untouched (role.Count kept its last valid value) meant a blank or
        // non-numeric box never actually became an invalid model state, so
        // Validate()/IsValid/Save's IsEnabled had nothing to catch — the box
        // LOOKED broken but the underlying Strategy quietly stayed valid.
        // Writing 0 on a parse failure makes Validate()'s own "count below 1"
        // rule catch it for real, the same way any other bad edit does.
        if (RoleOf(sender) is not { } role) return;
        role.Count = int.TryParse(((TextBox)sender).Text, out var count) ? count : 0;
        ViewModel.Revalidate();
    }

    private void OnToolsBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (RoleOf(sender) is not { } role) return;
        ((TextBox)sender).Text = string.Join(", ", role.Tools);
    }

    private void OnToolsBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (RoleOf(sender) is not { } role) return;
        role.Tools = ((TextBox)sender).Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        ViewModel.Revalidate();
    }

    private void OnAutoFixClick(object sender, RoutedEventArgs e) => ViewModel.AutoFix();

    private void OnCancelClick(object sender, RoutedEventArgs e) => _window.Close();

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        ViewModel.StatusMessage = null;
        if (!await _strategyPickerViewModel.SelectAsync(ViewModel.Strategy)) return;
        if (ViewModel.Strategy.Orchestrator is { } orchestrator) _chatViewModel.Model = orchestrator.Model.ToRawValue();
        ViewModel.StatusMessage = "Saved.";
    }
}
