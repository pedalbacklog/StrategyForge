using Coral.Core.Generators;
using Coral.Core.Models;
using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of StrategyForgeTests/AdvisorProvidersTests.swift: catalog
/// integrity, role-to-axis routing, "diversity by design" for reviewers, the
/// tier bias, the Claude-only no-op guarantee, and determinism.</summary>
public class AdvisorProvidersTests
{
    private static AgentRole Role(string name, RoleKind kind, bool orchestrator = false,
        int count = 1, ClaudeModel model = ClaudeModel.Sonnet5) =>
        new(name, kind, model, "", "", count: count, isOrchestrator: orchestrator);

    private static Strategy MakeStrategy(List<AgentRole> roles) => new("Test", "", roles, "");

    private static readonly IReadOnlySet<AIProvider> All =
        new HashSet<AIProvider> { AIProvider.Claude, AIProvider.Openai, AIProvider.Gemini };

    // MARK: - Catalog integrity

    [Fact]
    public void EveryNonClaudeProfileIdExistsInItsProvider()
    {
        var apiOnly = new HashSet<string> { "gpt-5-codex" };
        foreach (var profile in AdvisorEngine.ModelProfiles.Where(p => p.Provider != AIProvider.Claude))
        {
            var ids = ModelCatalog.Models(profile.Provider).Select(m => m.Id).ToList();
            Assert.True(ids.Contains(profile.ModelId) || apiOnly.Contains(profile.ModelId),
                $"{profile.ModelId} is not a real {profile.Provider.DisplayName()} model id");
        }
    }

    [Fact]
    public void EveryClaudeModelHasAProfile()
    {
        foreach (var m in Enum.GetValues<ClaudeModel>())
        {
            Assert.Contains(AdvisorEngine.ModelProfiles, p => p.Provider == AIProvider.Claude && p.ModelId == m.ToRawValue());
        }
    }

    // MARK: - Claude-only is a no-op

    [Fact]
    public void SingleProviderMakesNoChangesAndNoPicks()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true),
            Role("worker", RoleKind.Worker, count: 3),
        });
        var (outp, picks) = AdvisorEngine.AssignProviders(s, new HashSet<AIProvider> { AIProvider.Claude });
        Assert.Empty(picks);
        Assert.All(outp.Roles, r => Assert.Equal(AIProvider.Claude, r.Provider));
        Assert.Equal(s.Roles.Select(r => r.Name), outp.Roles.Select(r => r.Name));
    }

    [Fact]
    public void EmptyConnectedSetIsTreatedAsClaudeOnly()
    {
        var s = MakeStrategy(new List<AgentRole> { Role("lead", RoleKind.Orchestrator, orchestrator: true) });
        var (_, picks) = AdvisorEngine.AssignProviders(s, new HashSet<AIProvider>());
        Assert.Empty(picks);
    }

    // MARK: - Role -> axis routing

    [Fact]
    public void ImplementerGoesToTheBestCoder()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true),
            Role("impl", RoleKind.Worker, count: 2),
        });
        var (outp, _) = AdvisorEngine.AssignProviders(s, All);
        var impl = outp.Roles.First(r => r.Name == "impl");
        Assert.Equal(AIProvider.Openai, impl.Provider);
        Assert.Equal("gpt-5-codex", impl.ProviderModelId);
    }

    [Fact]
    public void ResearcherGoesToWidestContext()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true),
            Role("scout", RoleKind.Researcher, count: 4),
        });
        var (outp, _) = AdvisorEngine.AssignProviders(s, All);
        Assert.Equal(AIProvider.Gemini, outp.Roles.First(r => r.Name == "scout").Provider);
    }

    [Fact]
    public void OrchestratorGoesToStrongestReasoner()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true),
            Role("impl", RoleKind.Worker),
        });
        var (outp, _) = AdvisorEngine.AssignProviders(s, All);
        Assert.Equal(AIProvider.Claude, outp.Orchestrator?.Provider);
    }

    // MARK: - Diversity by design

    [Fact]
    public void ReviewerIsPushedOffTheCoderFamily()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true),
            Role("impl", RoleKind.Worker, count: 2),
            Role("review", RoleKind.Reviewer),
        });
        var (outp, picks) = AdvisorEngine.AssignProviders(s, All);
        var impl = outp.Roles.First(r => r.Name == "impl");
        var review = outp.Roles.First(r => r.Name == "review");
        Assert.Equal(AIProvider.Openai, impl.Provider);
        Assert.NotEqual(impl.Provider, review.Provider);
        Assert.Contains(picks, p => p.RoleName == "review" && p.ReasonKey == "advisor.provider.reason.diversity");
    }

    [Fact]
    public void ReviewerWithOnlyTwoProvidersStillCrossesFamily()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true),
            Role("impl", RoleKind.Worker),
            Role("review", RoleKind.Reviewer),
        });
        var (outp, _) = AdvisorEngine.AssignProviders(s, new HashSet<AIProvider> { AIProvider.Claude, AIProvider.Openai });
        Assert.Equal(AIProvider.Openai, outp.Roles.First(r => r.Name == "impl").Provider);
        Assert.Equal(AIProvider.Claude, outp.Roles.First(r => r.Name == "review").Provider);
    }

    // MARK: - Tier bias

    [Fact]
    public void SaverBiasPrefersFasterCheaperWorkers()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true, model: ClaudeModel.Opus48),
            Role("impl", RoleKind.Worker, count: 5),
        });
        var (saver, _) = AdvisorEngine.AssignProviders(s, All, bias: AdvisorEngine.TierBias.Saver);
        var (maxT, _) = AdvisorEngine.AssignProviders(s, All, bias: AdvisorEngine.TierBias.Max);
        var saverImpl = saver.Roles.First(r => r.Name == "impl");
        var maxImpl = maxT.Roles.First(r => r.Name == "impl");
        Assert.Equal("gpt-5-codex", maxImpl.ProviderModelId);
        Assert.True(saverImpl.Provider != AIProvider.Claude || saverImpl.Model == ClaudeModel.Haiku45 || saverImpl.Model == ClaudeModel.Sonnet5);
    }

    // MARK: - Determinism

    [Fact]
    public void SameInputsProduceIdenticalAssignment()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true),
            Role("impl", RoleKind.Worker, count: 2),
            Role("scout", RoleKind.Researcher, count: 3),
            Role("review", RoleKind.Reviewer),
        });
        var (a, pa) = AdvisorEngine.AssignProviders(s, All, bias: AdvisorEngine.TierBias.Balanced);
        var (b, pb) = AdvisorEngine.AssignProviders(s, All, bias: AdvisorEngine.TierBias.Balanced);
        static string Key(AgentRole r) => $"{r.Name}|{r.Provider.ToKey()}|{r.ProviderModelId ?? r.Model.ToRawValue()}";
        Assert.Equal(a.Roles.Select(Key), b.Roles.Select(Key));
        Assert.Equal(pa, pb);
    }

    // MARK: - Picks are in role order

    [Fact]
    public void PicksAreOrderedByRoleIndex()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true),
            Role("impl", RoleKind.Worker),
            Role("review", RoleKind.Reviewer),
        });
        var (_, picks) = AdvisorEngine.AssignProviders(s, All);
        Assert.Equal(new[] { "lead", "impl", "review" }, picks.Select(p => p.RoleName));
    }

    // MARK: - Coverage: connected providers actually get used

    [Fact]
    public void ConnectedGeminiLandsOnTheReviewSeat()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true),
            Role("impl", RoleKind.Worker, count: 2),
            Role("review", RoleKind.Reviewer),
        });
        var (outp, _) = AdvisorEngine.AssignProviders(s, All);
        Assert.Equal(AIProvider.Gemini, outp.Roles.First(r => r.Name == "review").Provider);
        Assert.Equal(All, outp.Roles.Select(r => r.Provider).ToHashSet());
    }

    [Fact]
    public void EveryConnectedProviderIsRepresentedOnADiverseTeam()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true),
            Role("impl", RoleKind.Worker, count: 2),
            Role("scout", RoleKind.Researcher, count: 3),
            Role("review", RoleKind.Reviewer),
        });
        var (outp, _) = AdvisorEngine.AssignProviders(s, All);
        Assert.Equal(All, outp.Roles.Select(r => r.Provider).ToHashSet());
    }

    [Fact]
    public void CoverageNeverPlantsAWeakCoderOnAPureCodingTeam()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true),
            Role("impl", RoleKind.Worker, count: 3),
        });
        var (outp, _) = AdvisorEngine.AssignProviders(s, All);
        Assert.DoesNotContain(outp.Roles, r => r.Provider == AIProvider.Gemini);
        Assert.Equal(AIProvider.Openai, outp.Roles.First(r => r.Name == "impl").Provider);
    }

    [Fact]
    public void CoverageIsANoOpWhenEveryProviderAlreadyUsed()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true),
            Role("impl", RoleKind.Worker, count: 2),
            Role("scout", RoleKind.Researcher, count: 3),
        });
        var (a, _) = AdvisorEngine.AssignProviders(s, All);
        var (b, _) = AdvisorEngine.AssignProviders(s, All);
        Assert.Equal(a.Roles.Select(r => r.Provider), b.Roles.Select(r => r.Provider));
        Assert.Equal(All, a.Roles.Select(r => r.Provider).ToHashSet());
    }

    // MARK: - Usage-aware assignment

    [Fact]
    public void DeprioritizedProviderLosesRolesToAComparableRival()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true),
            Role("impl", RoleKind.Worker, count: 2),
        });
        Assert.Equal(AIProvider.Openai, AdvisorEngine.AssignProviders(s, All).Strategy.Roles.First(r => r.Name == "impl").Provider);
        var (capped, _) = AdvisorEngine.AssignProviders(s, All, deprioritize: new HashSet<AIProvider> { AIProvider.Openai });
        Assert.NotEqual(AIProvider.Openai, capped.Roles.First(r => r.Name == "impl").Provider);
    }

    [Fact]
    public void DeprioritizedProviderIsStillUsedWhenItIsTheBestFitAnyway()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true),
            Role("impl", RoleKind.Worker),
        });
        var (outp, _) = AdvisorEngine.AssignProviders(s, new HashSet<AIProvider> { AIProvider.Claude, AIProvider.Openai },
            deprioritize: new HashSet<AIProvider> { AIProvider.Claude, AIProvider.Openai });
        Assert.Equal(AIProvider.Openai, outp.Roles.First(r => r.Name == "impl").Provider);
    }

    // MARK: - Orchestrator never on Gemini

    [Fact]
    public void OrchestratorNeverLandsOnGemini()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true, model: ClaudeModel.Haiku45),
            Role("impl", RoleKind.Worker, count: 2),
        });
        var (outp, _) = AdvisorEngine.AssignProviders(s, All);
        Assert.NotEqual(AIProvider.Gemini, outp.Orchestrator?.Provider);
    }

    [Fact]
    public void GeminiStillUsedAsAWorkerOrReviewer()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true, model: ClaudeModel.Opus48),
            Role("impl", RoleKind.Worker, count: 2),
            Role("scout", RoleKind.Researcher, count: 2),
            Role("review", RoleKind.Reviewer),
        });
        var (outp, _) = AdvisorEngine.AssignProviders(s, All);
        Assert.Contains(outp.Roles, r => r.Provider == AIProvider.Gemini);
    }

    // MARK: - Model-locked provider

    [Fact]
    public void LockedOpenAINeverAssignsASpecificModel()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true, model: ClaudeModel.Opus48),
            Role("impl", RoleKind.Worker, count: 2),
            Role("review", RoleKind.Reviewer),
        });
        var (_, picks) = AdvisorEngine.AssignProviders(s, All, modelLocked: new HashSet<AIProvider> { AIProvider.Openai });
        Assert.DoesNotContain(picks, p => p.Provider == AIProvider.Openai && p.ModelDisplayName.Contains("GPT-5"));
        var openAiPick = picks.FirstOrDefault(p => p.Provider == AIProvider.Openai);
        if (openAiPick is not null) Assert.Equal("ChatGPT · Codex", openAiPick.ModelDisplayName);
    }

    [Fact]
    public void UnlockedOpenAIStillGetsItsBestCoder()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true, model: ClaudeModel.Opus48),
            Role("impl", RoleKind.Worker, count: 2),
        });
        var (outp, _) = AdvisorEngine.AssignProviders(s, All);
        Assert.Equal("gpt-5-codex", outp.Roles.First(r => r.Name == "impl").ProviderModelId);
    }

    // MARK: - Cost band preserved

    [Fact]
    public void EconomySoloStaysCheapAcrossProviders()
    {
        var (outp, _) = AdvisorEngine.AssignProviders(StrategyLibrary.SoloEconomy(), All);
        Assert.Equal(StrategyCost.Tier.Low, CostEstimator.Estimate(outp).CostTier);
        var lead = outp.Roles[0];
        var id = lead.Provider == AIProvider.Claude ? lead.Model.ToRawValue() : (lead.ProviderModelId ?? lead.Model.ToRawValue());
        Assert.True(AdvisorEngine.CostBand(id) == 0, $"economy seat was upgraded to {id}");
    }

    [Fact]
    public void FrontierSoloStaysFrontier()
    {
        var (outp, _) = AdvisorEngine.AssignProviders(StrategyLibrary.Solo(), All);
        Assert.Equal(AIProvider.Claude, outp.Roles[0].Provider);
        Assert.Equal(2, AdvisorEngine.CostBand(outp.Roles[0].Model.ToRawValue()));
    }

    [Fact]
    public void ASwapNeverRaisesARolesCostBand()
    {
        foreach (var template in StrategyLibrary.All)
        {
            var (outp, _) = AdvisorEngine.AssignProviders(template, All);
            foreach (var (before, after) in template.Roles.Zip(outp.Roles))
            {
                var beforeBand = AdvisorEngine.CostBand(before.Model.ToRawValue());
                var afterId = after.Provider == AIProvider.Claude ? after.Model.ToRawValue() : (after.ProviderModelId ?? after.Model.ToRawValue());
                var afterBand = AdvisorEngine.CostBand(afterId);
                Assert.True(afterBand <= beforeBand, $"{template.Name}/{before.Name}: band {beforeBand} -> {afterBand}");
            }
        }
    }

    [Fact]
    public void EmptyDeprioritizeMatchesTheDefault()
    {
        var s = MakeStrategy(new List<AgentRole>
        {
            Role("lead", RoleKind.Orchestrator, orchestrator: true),
            Role("impl", RoleKind.Worker, count: 2),
            Role("review", RoleKind.Reviewer),
        });
        var a = AdvisorEngine.AssignProviders(s, All).Strategy.Roles.Select(r => r.Provider);
        var b = AdvisorEngine.AssignProviders(s, All, deprioritize: new HashSet<AIProvider>()).Strategy.Roles.Select(r => r.Provider);
        Assert.Equal(a, b);
    }
}
