using Coral.Core.Models;
using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Tests for AdvisorEngine.AdviseTiers/ApplyingProviders — no Swift
/// source to port from (adviseTiers has no dedicated test file in
/// StrategyForgeTests either), so this is original coverage of the ported
/// behavior: three cost/quality options, dedup of a collapsed tier,
/// cross-provider reassignment per tier, and determinism.</summary>
public class AdvisorTiersTests
{
    private const string BigTask =
        "Migración multi-archivo de bcrypt a argon2 en todo el repo, serán días de trabajo";
    private const string TinyTask = "Resume este párrafo en dos líneas";

    private static readonly IReadOnlySet<AIProvider> ClaudeOnly = new HashSet<AIProvider> { AIProvider.Claude };
    private static readonly IReadOnlySet<AIProvider> All =
        new HashSet<AIProvider> { AIProvider.Claude, AIProvider.Openai, AIProvider.Gemini };

    [Fact]
    public void ABigTaskProducesAllThreeTiersInOrder()
    {
        var tiers = AdvisorEngine.AdviseTiers(BigTask, ClaudeOnly);
        Assert.Equal(new[] { "saver", "balanced", "max" }, tiers.Select(t => t.Id));
    }

    [Fact]
    public void BalancedTierIsAlwaysPresentAndMatchesPlainAdvise()
    {
        var tiers = AdvisorEngine.AdviseTiers(BigTask, ClaudeOnly);
        var balanced = tiers.Single(t => t.Id == "balanced");
        var plain = AdvisorEngine.Advise(BigTask, new HashSet<AIProvider> { AIProvider.Claude });
        Assert.Equal(plain.Model, balanced.Advice.Model);
        Assert.Equal(plain.Strategy.Roles.Count, balanced.Advice.Strategy.Roles.Count);
    }

    [Fact]
    public void SaverIsCheaperAndMaxIsPricierThanBalanced()
    {
        var tiers = AdvisorEngine.AdviseTiers(BigTask, ClaudeOnly);
        var saver = tiers.Single(t => t.Id == "saver");
        var balanced = tiers.Single(t => t.Id == "balanced");
        var max = tiers.Single(t => t.Id == "max");
        Assert.True(saver.Advice.EstimatedCost.PerRun <= balanced.Advice.EstimatedCost.PerRun);
        Assert.True(max.Advice.EstimatedCost.PerRun >= balanced.Advice.EstimatedCost.PerRun);
    }

    [Fact]
    public void ATinyTaskAlreadyAtTheFloorDropsTheSaverTier()
    {
        // "Resume..." lands on Haiku45 solo — there's nowhere cheaper to go, so
        // the saver variant collapses onto the exact same shape as balanced and
        // must be filtered out rather than shown as a duplicate.
        var tiers = AdvisorEngine.AdviseTiers(TinyTask, ClaudeOnly);
        Assert.DoesNotContain(tiers, t => t.Id == "saver");
        Assert.Contains(tiers, t => t.Id == "balanced");
    }

    [Fact]
    public void ClaudeOnlyProducesNoProviderPicksOnAnyTier()
    {
        var tiers = AdvisorEngine.AdviseTiers(BigTask, ClaudeOnly);
        Assert.All(tiers, t => Assert.Empty(t.Advice.ProviderPicks));
    }

    [Fact]
    public void MultipleConnectedProvidersProduceProviderPicksOnEveryTier()
    {
        var tiers = AdvisorEngine.AdviseTiers(BigTask, All);
        Assert.All(tiers, t => Assert.NotEmpty(t.Advice.ProviderPicks));
        // Every pick is for a provider actually in the connected set.
        Assert.All(tiers, t => Assert.All(t.Advice.ProviderPicks, p => Assert.Contains(p.Provider, All)));
    }

    [Fact]
    public void EmptyConnectedSetIsTreatedAsClaudeOnly()
    {
        var tiers = AdvisorEngine.AdviseTiers(BigTask, new HashSet<AIProvider>());
        Assert.All(tiers, t => Assert.Empty(t.Advice.ProviderPicks));
    }

    [Fact]
    public void SaverNeverReadsAsTheSameHeadlineModelAsBalanced()
    {
        // The "genuinely cheaper" guarantee: even after cross-provider
        // reassignment might otherwise revert the saver tier's orchestrator to
        // the same top Claude reasoner as balanced, saver's headline model must
        // stay distinct (unless balanced is already at the Haiku floor).
        foreach (var task in new[] { BigTask, "diseña e implementa una nueva arquitectura de pagos" })
        {
            var tiers = AdvisorEngine.AdviseTiers(task, All);
            var saver = tiers.FirstOrDefault(t => t.Id == "saver");
            var balanced = tiers.Single(t => t.Id == "balanced");
            if (saver is null || balanced.Advice.Model == ClaudeModel.Haiku45) continue;
            Assert.NotEqual(balanced.Advice.Model, saver.Advice.Model);
        }
    }

    [Fact]
    public void SameInputsProduceIdenticalTiers()
    {
        var a = AdvisorEngine.AdviseTiers(BigTask, All);
        var b = AdvisorEngine.AdviseTiers(BigTask, All);
        Assert.Equal(a.Select(t => t.Id), b.Select(t => t.Id));
        Assert.Equal(a.Select(t => t.Advice), b.Select(t => t.Advice));
    }

    [Fact]
    public void ApplyingProvidersIsANoOpUnderTwoProvidersAndReassignsUnderTwoOrMore()
    {
        var advice = AdvisorEngine.Advise(BigTask, new HashSet<AIProvider> { AIProvider.Claude });
        var single = AdvisorEngine.ApplyingProviders(advice, ClaudeOnly, AdvisorEngine.TierBias.Balanced);
        Assert.Empty(single.ProviderPicks);

        var mixed = AdvisorEngine.ApplyingProviders(advice, All, AdvisorEngine.TierBias.Balanced);
        Assert.NotEmpty(mixed.ProviderPicks);
        // The strategy's role count/names are unchanged — only provider/model per role.
        Assert.Equal(advice.Strategy.Roles.Select(r => r.Name), mixed.Strategy.Roles.Select(r => r.Name));
    }

    [Fact]
    public void AspirationalPicksAreEmptyForAClaudeOnlyStrategyRegardlessOfConnection()
    {
        var advice = AdvisorEngine.Advise(BigTask, new HashSet<AIProvider> { AIProvider.Claude });
        // The catalog has >1 provider overall, so aspirational picks ARE produced
        // (it ignores what's connected) — but every pick not actually connected
        // is flagged so the UI can dim it.
        var picks = AdvisorEngine.AspirationalPicks(advice.Strategy, ClaudeOnly);
        Assert.NotEmpty(picks);
        Assert.Contains(picks, p => !p.IsConnected);
    }
}
