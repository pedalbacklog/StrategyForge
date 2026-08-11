using Coral.Core.Models;
using Coral.Core.Services;

namespace Coral.Core.ViewModels;

/// <summary>
/// Drives the "Suggest a team" section of the Strategy flyout — wraps
/// <see cref="AdvisorEngine.Advise"/>, a pure/deterministic/offline
/// heuristic (see its own doc comment: no LLM call, no network, no cost).
/// Kept separate from <see cref="StrategyPickerViewModel"/> on purpose,
/// same one-ViewModel-one-concern separation as <c>GitPanelViewModel</c>/
/// <c>PullRequestViewModel</c>: this ViewModel only produces a
/// recommendation, it never writes files — applying one is the caller's
/// job (<c>MainPage.xaml.cs</c>), via the exact same
/// <see cref="StrategyPickerViewModel.SelectAsync"/> path a manually
/// picked template already uses.
/// </summary>
public sealed class AdvisorViewModel : ObservableObject
{
    private string _task = "";
    public string Task { get => _task; set => SetProperty(ref _task, value); }

    private AdvisorEngine.Advice? _advice;
    public AdvisorEngine.Advice? Advice
    {
        get => _advice;
        private set
        {
            if (SetProperty(ref _advice, value))
            {
                OnPropertyChanged(nameof(SummaryText));
                OnPropertyChanged(nameof(HasAdvice));
            }
        }
    }

    /// <summary>A one-line, binding-friendly summary of <see cref="Advice"/>
    /// — empty while there's nothing to show yet.</summary>
    public string SummaryText => Advice is null
        ? ""
        : $"{Advice.Strategy.Name} · {Advice.Model.DisplayName()} · {Advice.Effort} effort";

    /// <summary>Bool-typed companion to <see cref="Advice"/> — the port's
    /// existing <c>BoolToVisibilityConverter</c> only handles <c>bool</c>,
    /// not a nullable reference type, so XAML keys the "Use this" row's
    /// <c>Visibility</c> off this instead of <c>Advice</c> directly.</summary>
    public bool HasAdvice => Advice is not null;

    /// <summary>Run the heuristic on <see cref="Task"/>. Synchronous and
    /// instant (no I/O, no subprocess) — unlike every other "action" method
    /// in this port, there's nothing to await.</summary>
    public void Suggest()
    {
        var trimmed = Task.Trim();
        Advice = trimmed.Length == 0 ? null : AdvisorEngine.Advise(trimmed);
    }
}
