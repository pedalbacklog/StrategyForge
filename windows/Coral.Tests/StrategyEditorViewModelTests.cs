using Coral.Core.Models;
using Coral.Core.ViewModels;
using Xunit;

namespace Coral.Tests;

public class StrategyEditorViewModelTests
{
    [Fact]
    public void ConstructorRunsAnInitialValidation()
    {
        var vm = new StrategyEditorViewModel(StrategyLibrary.OrchestratorWorkers());

        Assert.True(vm.IsValid);
        Assert.Empty(vm.IssueLines);
    }

    [Fact]
    public void RevalidatePicksUpAnEditMadeDirectlyOnARole()
    {
        var strategy = StrategyLibrary.OrchestratorWorkers();
        var vm = new StrategyEditorViewModel(strategy);
        // Duplicate the orchestrator's name onto a subagent — a real
        // validation error (Strategy.Validate() requires unique role names).
        strategy.Roles[0].Name = strategy.Roles[1].Name;

        vm.Revalidate();

        Assert.False(vm.IsValid);
        Assert.Contains(vm.IssueLines, line => line.Contains("Duplicate role name"));
    }

    [Fact]
    public void AutoFixSwapsInAFixedStrategyAndRevalidates()
    {
        var strategy = StrategyLibrary.OrchestratorWorkers();
        // A reviewer holding a write tool — HasAutoFixableIssues should
        // pick this up (AutoFixed() strips write tools from read-only roles).
        strategy.Roles.Add(new AgentRole("reviewer", RoleKind.Reviewer, ClaudeModel.Sonnet5,
            "Reviews changes.", "Reviews the implementer's work.", tools: new List<string> { "Write" }));
        var vm = new StrategyEditorViewModel(strategy);
        vm.Revalidate();
        Assert.True(vm.HasAutoFixableIssues);

        vm.AutoFix();

        Assert.False(vm.HasAutoFixableIssues);
        Assert.DoesNotContain(vm.Strategy.Roles.Single(r => r.Name == "reviewer").Tools, t => t == "Write");
    }

    [Fact]
    public void IssueLinesPrefixesErrorsAndWarningsDifferently()
    {
        var strategy = StrategyLibrary.OrchestratorWorkers();
        strategy.Roles[0].Name = ""; // an error: empty role name
        var vm = new StrategyEditorViewModel(strategy);

        Assert.Contains(vm.IssueLines, line => line.StartsWith("❌"));
    }
}
