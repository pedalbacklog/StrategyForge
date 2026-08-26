using Coral.Core.Generators;
using Coral.Core.Models;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of StrategyForgeTests/GeneratorTests.swift's CostEstimatorTests.</summary>
public class CostEstimatorTests
{
    [Fact]
    public void CheaperModelCostsLess()
    {
        // Same single-agent shape, cheaper model -> strictly cheaper: Solo Economy
        // runs Sonnet, Solo runs Opus.
        var economy = CostEstimator.Estimate(StrategyLibrary.SoloEconomy());
        var solo = CostEstimator.Estimate(StrategyLibrary.Solo());
        Assert.True(economy.PerRun < solo.PerRun);
        Assert.True(economy.PerRun > 0);
    }

    [Fact]
    public void MoreInstancesCostMore()
    {
        var s = StrategyLibrary.OrchestratorWorkers();
        var basePerRun = CostEstimator.Estimate(s).PerRun;
        var i = s.Roles.FindIndex(r => !r.IsOrchestrator);
        if (i >= 0) s.Roles[i].Count += 3;
        Assert.True(CostEstimator.Estimate(s).PerRun > basePerRun);
    }

    [Fact]
    public void SoloLeadCostsMoreThanTheSameLeadDelegating()
    {
        // Same lead (Opus), with vs without a team to delegate to. The added
        // worker uses a different model (Haiku) so byModel[opus5] is purely the
        // Opus lead's share.
        var solo = StrategyLibrary.Solo(); // single Opus agent, no team
        var soloLead = CostEstimator.Estimate(solo).ByModel.GetValueOrDefault(ClaudeModel.Opus5.ToRawValue());

        var team = solo;
        team.Roles.Add(new AgentRole("worker", RoleKind.Worker, ClaudeModel.Haiku45, "implement", "do the work"));
        var delegatingLead = CostEstimator.Estimate(team).ByModel.GetValueOrDefault(ClaudeModel.Opus5.ToRawValue());

        Assert.True(soloLead > 0);
        Assert.True(delegatingLead < soloLead);
    }

    [Fact]
    public void BreakdownCoversUsedModels()
    {
        var cost = CostEstimator.Estimate(StrategyLibrary.OrchestratorWorkers());
        // Fan-out uses Fable (orchestrator) + Sonnet (workers).
        Assert.True(cost.ByModel.ContainsKey(ClaudeModel.Fable5.ToRawValue()));
        Assert.True(cost.ByModel.ContainsKey(ClaudeModel.Sonnet5.ToRawValue()));
    }

    [Fact]
    public void EffortScalesCostAndMediumIsBaseline()
    {
        var s = StrategyLibrary.OrchestratorWorkers();
        var low = CostEstimator.Estimate(s, CostEffort.Low).PerRun;
        var medium = CostEstimator.Estimate(s, CostEffort.Medium).PerRun;
        var high = CostEstimator.Estimate(s, CostEffort.High).PerRun;
        Assert.True(low < medium);
        Assert.True(high > medium);
        // The no-effort overload must equal the medium baseline.
        Assert.Equal(medium, CostEstimator.Estimate(s).PerRun);
    }

    [Fact]
    public void EffectiveCostBreakdownSumsToTotal()
    {
        // The effective-cost model (tokenizer overhead + cache-read discount) must
        // stay internally consistent: the per-model breakdown sums to PerRun.
        var cost = CostEstimator.Estimate(StrategyLibrary.DomainSpecialists());
        var sum = cost.ByModel.Values.Sum();
        Assert.True(cost.PerRun > 0);
        Assert.True(Math.Abs(sum - cost.PerRun) < 0.0001);
    }
}
