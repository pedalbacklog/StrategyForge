using Coral.Core.Models;
using Coral.Core.ViewModels;
using Xunit;

namespace Coral.Tests;

public class AdvisorViewModelTests
{
    [Fact]
    public void SuggestIsANoOpOnBlankTask()
    {
        var vm = new AdvisorViewModel { Task = "   " };

        vm.Suggest();

        Assert.Null(vm.SelectedTier);
        Assert.Equal("", vm.SummaryText);
        Assert.False(vm.HasAdvice);
        Assert.Empty(vm.TierChips);
    }

    [Fact]
    public void SuggestPopulatesTiersAndASummaryForARealTask()
    {
        var vm = new AdvisorViewModel { Task = "Resume este párrafo en dos líneas" };

        vm.Suggest();

        Assert.NotNull(vm.SelectedTier);
        Assert.True(vm.HasAdvice);
        Assert.NotEmpty(vm.SummaryText);
        Assert.NotEmpty(vm.TierChips);
        Assert.Contains(vm.SelectedTier!.Advice.Strategy.Name, vm.SummaryText);
    }

    [Fact]
    public void SuggestDefaultsToTheBalancedTier()
    {
        var vm = new AdvisorViewModel { Task = "Migración multi-archivo de bcrypt a argon2 en todo el repo" };

        vm.Suggest();

        Assert.Equal("balanced", vm.SelectedTierId);
        Assert.Contains(vm.TierChips, c => c.Id == "balanced" && c.IsSelected);
    }

    [Fact]
    public void SelectTierSwitchesTheDisplayedAdviceWithoutRecomputing()
    {
        var vm = new AdvisorViewModel { Task = "Migración multi-archivo de bcrypt a argon2 en todo el repo" };
        vm.Suggest();
        var chips = vm.TierChips;
        var other = chips.First(c => c.Id != "balanced");

        vm.SelectTier(other.Id);

        Assert.Equal(other.Id, vm.SelectedTierId);
        Assert.Equal(other.Id, vm.SelectedTier!.Id);
        Assert.Contains(vm.TierChips, c => c.Id == other.Id && c.IsSelected);
    }

    [Fact]
    public void DecisionLinesEndWithTheResolvedModelAsTheResult()
    {
        var vm = new AdvisorViewModel { Task = "Resume este párrafo en dos líneas" };
        vm.Suggest();

        Assert.NotEmpty(vm.DecisionLines);
        Assert.Equal($"Result: {vm.SelectedTier!.Advice.Model.DisplayName()}", vm.DecisionLines[^1]);
    }

    [Fact]
    public void ApplyButtonLabelReflectsWhetherATeamIsAlreadyChosen()
    {
        var vm = new AdvisorViewModel { Task = "Resume este párrafo en dos líneas" };
        Assert.Equal("Apply team", vm.ApplyButtonLabel);

        vm.ChosenTeamName = "Solo";
        Assert.Equal("Switch to this", vm.ApplyButtonLabel);
    }

    [Fact]
    public void ATurnBasedTaskHasNoLoopHint()
    {
        var vm = new AdvisorViewModel { Task = "Resume este párrafo en dos líneas" };
        vm.Suggest();

        Assert.False(vm.ShowLoopHint);
        Assert.Equal("", vm.LoopHintText);
    }

    [Fact]
    public void AGoalBasedTaskShowsALoopHint()
    {
        var vm = new AdvisorViewModel { Task = "corre los tests hasta que pasen y arregla el lint" };
        vm.Suggest();

        Assert.True(vm.ShowLoopHint);
        Assert.NotEmpty(vm.LoopHintText);
    }

    [Fact]
    public void ChosenTeamHintTextIsEmptyUntilATeamIsChosen()
    {
        var vm = new AdvisorViewModel();
        Assert.False(vm.HasChosenTeam);
        Assert.Equal("", vm.ChosenTeamHintText);

        vm.ChosenTeamName = "Solo";
        Assert.True(vm.HasChosenTeam);
        Assert.Contains("Solo", vm.ChosenTeamHintText);
    }

    [Fact]
    public void ProviderMixLinesAreEmptyWithoutASuggestion()
    {
        var vm = new AdvisorViewModel();
        Assert.Empty(vm.ProviderMixLines);
        Assert.False(vm.HasLockedProviderInMix);
    }

    [Fact]
    public void ProviderMixLinesShowTheAspirationalMixOnAClaudeOnlySetup()
    {
        // With no other providers connected, the mix falls back to the
        // aspirational (display-only) ideal, so it's still visible — just
        // flagged as not connected.
        var vm = new AdvisorViewModel { Task = "Migración multi-archivo de bcrypt a argon2 en todo el repo" };
        vm.Suggest();

        Assert.NotEmpty(vm.ProviderMixLines);
        Assert.True(vm.HasLockedProviderInMix);
        Assert.Contains(vm.ProviderMixLines, l => l.Contains("not connected"));
    }
}
