using Coral.Core.Generators;
using Coral.Core.Models;
using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of StrategyForgeTests/AdvisorEngineTests.swift: model
/// routing (EN + ES), loop-kind detection, the cheap-model team downgrade,
/// decision-path shape, and determinism. The engine is pure, so no fakes
/// are needed. Scoped to AdvisorEngine.Advise() (the deterministic path) —
/// see AdvisorEngine.cs's doc comment for what's deliberately not ported
/// this pass (adviseWithAI/adviseTiers/assignProviders).</summary>
public class AdvisorEngineTests
{
    // MARK: - Model routing

    [Fact]
    public void ShortSpanishSummaryGetsHaikuAndTurnBased()
    {
        var advice = AdvisorEngine.Advise("Resume este párrafo en dos líneas");
        Assert.Equal(ClaudeModel.Haiku45, advice.Model);
        Assert.Equal(LoopKind.TurnBased, advice.LoopKind);
        Assert.Equal(CostEffort.Low, advice.Effort);
    }

    [Fact]
    public void MultiFileMigrationGetsTopTierAndATeam()
    {
        var advice = AdvisorEngine.Advise(
            "Migración multi-archivo de bcrypt a argon2 en todo el repo, serán días de trabajo");
        // Deep + ambitious → the top tier (Fable, with Opus as the acceptable floor).
        Assert.True(advice.Model == ClaudeModel.Fable5 || advice.Model == ClaudeModel.Opus5);
        // A cross-repo migration warrants more than a solo agent.
        Assert.True(advice.Strategy.Roles.Count > 1);
        Assert.NotEmpty(advice.Strategy.SubagentRoles);
        Assert.Equal(CostEffort.High, advice.Effort);
    }

    // MARK: - Loop detection

    [Fact]
    public void UntilTestsPassIsGoalBasedWithVerifiableGoal()
    {
        var advice = AdvisorEngine.Advise("corre los tests hasta que pasen y arregla el lint");
        Assert.Equal(LoopKind.GoalBased, advice.LoopKind);
        // The drafted goal reuses the task's own finish line.
        Assert.Contains("hasta que", advice.GoalSuggestion.ToLowerInvariant());
        Assert.NotEmpty(advice.GoalSuggestion);
    }

    [Fact]
    public void EveryMorningIsTimeBased()
    {
        var advice = AdvisorEngine.Advise("cada mañana resume los issues nuevos");
        Assert.Equal(LoopKind.TimeBased, advice.LoopKind);
    }

    [Fact]
    public void OnFailureIsProactive()
    {
        var advice = AdvisorEngine.Advise("cuando falle el pipeline, investiga la causa y abre un informe");
        Assert.Equal(LoopKind.Proactive, advice.LoopKind);
    }

    // MARK: - Cheap-model team downgrade

    [Fact]
    public void FastModelDowngradesHeavyShapeWithExplicitStep()
    {
        // "explica" pushes the shape heuristic to research fan-out, but the task
        // is quick (Haiku), so the engine must scale down and say so in the path.
        var advice = AdvisorEngine.Advise("explica rápido cómo funciona este archivo, algo corto");
        Assert.Equal(ClaudeModel.Haiku45, advice.Model);
        Assert.Equal("advisor.rationale.downgraded", advice.ShapeRationaleKey);
        Assert.Contains(advice.DecisionPath, s => s.QuestionKey == "advisor.q.team" && !s.AnswerIsYes);
    }

    // MARK: - Non-delegable work

    [Fact]
    public void SerialDebuggingCollapsesTeamButKeepsStrongModel()
    {
        // "investiga" trips the fan-out heuristic and the depth gate (→ strong model),
        // but a root-cause hunt is one serial chain of judgments — nothing to hand
        // off. The engine must collapse to a lean setup, say so in the path, and
        // (unlike the cheap-model downgrade) keep a capable model leading.
        var advice = AdvisorEngine.Advise(
            "Investiga y depura por qué el checkout falla de forma intermitente en todo el repo; encuentra la causa raíz del fallo");
        Assert.Equal("advisor.rationale.nondelegable", advice.ShapeRationaleKey);
        Assert.True(advice.Strategy.Roles.Count <= 2); // executor + advisor, no fleet
        Assert.True(advice.Model == ClaudeModel.Fable5 || advice.Model == ClaudeModel.Opus5);
        Assert.Contains(advice.DecisionPath, s =>
            s.QuestionKey == "advisor.q.team" && !s.AnswerIsYes && s.EvidenceKeys.Contains("advisor.ev.serialDebug"));
    }

    [Fact]
    public void DelegableMultiFileWorkStillGetsATeam()
    {
        // Guard against over-firing: a genuinely splittable migration must NOT be
        // mistaken for non-delegable work.
        var advice = AdvisorEngine.Advise(
            "Migración multi-archivo de bcrypt a argon2 en todo el repo, serán días de trabajo");
        Assert.NotEqual("advisor.rationale.nondelegable", advice.ShapeRationaleKey);
        Assert.NotEmpty(advice.Strategy.SubagentRoles);
    }

    // MARK: - Variety: structural teams survive on a mid/cheap model

    [Fact]
    public void DesignDebateTeamSurvivesOnAMidModel()
    {
        // A "decide / compare trade-offs" task lands on a mid model (Sonnet), which is
        // "cheap". Structural shapes (debate) must survive, since their value is the
        // SHAPE, not the fleet size.
        var advice = AdvisorEngine.Advise("help me decide between Postgres and MySQL and compare the trade-offs");
        Assert.Equal(ClaudeModel.Sonnet5, advice.Model); // a "cheap" mid model
        Assert.NotEqual("advisor.rationale.downgraded", advice.ShapeRationaleKey);
        Assert.True(advice.Strategy.SubagentRoles.Count >= 1); // a real team, not a lone agent
    }

    [Fact]
    public void AdversarialSparringTeamSurvivesOnAMidModel()
    {
        var advice = AdvisorEngine.Advise("try to break and attack the auth to harden it");
        Assert.NotEqual("advisor.rationale.downgraded", advice.ShapeRationaleKey);
        Assert.True(advice.Strategy.SubagentRoles.Count >= 1);
    }

    // MARK: - Provider-aware SHAPE selection

    [Fact]
    public void BroadTaskPrefersSpecialistsWhenSeveralProvidersConnected()
    {
        // Single provider → identical worker fan-out (today's behavior).
        var solo = StrategyGenerator.HeuristicShape("process every module exhaustively in parallel");
        Assert.Equal(StrategyShape.OrchestratorWorkers, solo.Shape);
        // Several providers → a heterogeneous team of specialists, so each provider
        // has a distinct seat.
        var mixed = StrategyGenerator.HeuristicShape("process every module exhaustively in parallel",
            new HashSet<AIProvider> { AIProvider.Claude, AIProvider.Openai, AIProvider.Gemini });
        Assert.Equal(StrategyShape.DomainSpecialists, mixed.Shape);
    }

    [Fact]
    public void AdviseWithClaudeOnlyMatchesTheNoArgDefault()
    {
        // The default connected set is Claude-only, so passing it explicitly is a no-op.
        const string task = "process every module exhaustively in parallel";
        Assert.Equal(AdvisorEngine.Advise(task), AdvisorEngine.Advise(task, new HashSet<AIProvider> { AIProvider.Claude }));
    }

    // MARK: - Decision path shape

    [Theory]
    [InlineData("Resume este párrafo")]
    [InlineData("Migrate the whole repo to argon2 over several days")]
    [InlineData("corre los tests hasta que pasen")]
    [InlineData("")]
    public void PathIsNonEmptyAndStartsWithDepthQuestion(string task)
    {
        var advice = AdvisorEngine.Advise(task);
        Assert.NotEmpty(advice.DecisionPath);
        Assert.Equal("advisor.q.depth", advice.DecisionPath[0].QuestionKey);
        // Step ids are sequential, so the UI can rely on stable ordering.
        Assert.Equal(Enumerable.Range(0, advice.DecisionPath.Count), advice.DecisionPath.Select(s => s.Id));
        // Evidence is capped at 3 per step.
        Assert.All(advice.DecisionPath, s => Assert.True(s.EvidenceKeys.Count <= 3));
    }

    [Fact]
    public void OrchestratorRunsTheRecommendedModel()
    {
        var advice = AdvisorEngine.Advise("Resume este párrafo en dos líneas");
        Assert.Equal(advice.Model, advice.Strategy.Orchestrator?.Model);
    }

    // MARK: - Determinism

    [Fact]
    public void SameInputProducesEqualAdvice()
    {
        const string task = "Migración multi-archivo de bcrypt a argon2 en todo el repo, serán días de trabajo";
        var first = AdvisorEngine.Advise(task);
        var second = AdvisorEngine.Advise(task);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }
}
