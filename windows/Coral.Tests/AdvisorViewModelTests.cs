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

        Assert.Null(vm.Advice);
        Assert.Equal("", vm.SummaryText);
        Assert.False(vm.HasAdvice);
    }

    [Fact]
    public void SuggestPopulatesAdviceAndASummaryForARealTask()
    {
        var vm = new AdvisorViewModel { Task = "Resume este párrafo en dos líneas" };

        vm.Suggest();

        Assert.NotNull(vm.Advice);
        Assert.True(vm.HasAdvice);
        Assert.NotEmpty(vm.SummaryText);
        Assert.Contains(vm.Advice!.Strategy.Name, vm.SummaryText);
    }
}
