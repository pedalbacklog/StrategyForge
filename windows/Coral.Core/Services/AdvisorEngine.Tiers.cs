using Coral.Core.Generators;
using Coral.Core.Models;

namespace Coral.Core.Services;

/// <summary>
/// Port of the "tiered options" half of <c>StrategyForge/Services/AdvisorEngine.swift</c>
/// (<c>adviseTiers</c> and its helpers) — three cost/quality options for the
/// same task (Economy · Recommended · Max), each with its own cross-provider
/// mix. Wherever the Swift original calls <c>adviseWithAI</c> (the on-device
/// AI upgrade, not ported — see <c>AdvisorEngine.cs</c>'s class doc comment),
/// this substitutes the plain <see cref="AdvisorEngine.Advise"/> — exactly
/// the fallback Swift itself takes when AI is unavailable, so every branch
/// here is behavior Swift already exercises, not new logic.
/// </summary>
public static partial class AdvisorEngine
{
    /// <summary>One recommendation option at a cost/quality point.</summary>
    public sealed record Tier(string Id, string LabelKey, string NoteKey, Advice Advice);

    /// <summary>Three options for the same task: a cheaper/faster one, the
    /// balanced recommendation, and a max-quality one that spends more for
    /// better results. Same shape/loop/goal — they differ in model tier and
    /// team size, so <see cref="Advice.EstimatedCost"/> tells the tradeoff
    /// honestly.</summary>
    public static List<Tier> AdviseTiers(
        string task,
        IReadOnlySet<AIProvider>? connected = null,
        IReadOnlySet<AIProvider>? deprioritize = null,
        IReadOnlySet<AIProvider>? modelLocked = null)
    {
        connected ??= new HashSet<AIProvider> { AIProvider.Claude };
        var balanced = Advise(task, new HashSet<AIProvider>(connected));
        var saver = Variant(balanced, shift: -1, countDelta: -1, effort: Lower(balanced.Effort),
            id: "saver", labelKey: "advisor.tier.saver", noteKey: "advisor.tier.saver.note");
        var max = Variant(balanced, shift: +1, countDelta: +2, effort: Higher(balanced.Effort),
            id: "max", labelKey: "advisor.tier.max", noteKey: "advisor.tier.max.note");
        var mid = new Tier("balanced", "advisor.tier.balanced", "advisor.tier.balanced.note", balanced);

        // Drop a tier that collapses to the same model AND team as the balanced
        // one (e.g. already at Haiku -> no cheaper tier), so we never show
        // duplicates. Dedup on the CLAUDE-only shapes (before any cross-provider
        // reassignment) so the "no cheaper option" collapse still fires as it
        // would without providers in the mix.
        var tiers = new List<Tier> { saver, mid, max }
            .Where((t, i) => i == 1 || !SameShape(t.Advice, balanced))
            .ToList();

        // Cross-provider: reassign providers per tier with its matching bias, so
        // the Economy tier leans on fast/cheap models and Max on top capability
        // — each still explained by its own provider picks. No-op when <2
        // providers connected (ApplyingProviders/AssignProviders already no-op
        // in that case).
        if (connected.Count > 1)
        {
            tiers = tiers.Select(t =>
            {
                var advice = ApplyingProviders(t.Advice, connected, TierBiasFrom(t.Id), deprioritize, modelLocked);
                return t with { Advice = advice };
            }).ToList();
        }

        // Guarantee the Economy tier reads as genuinely cheaper: the
        // cross-provider re-apply can revert the orchestrator to the same top
        // Claude reasoner as the balanced tier, so if the two heads collapsed,
        // force Economy one Claude tier down (deterministic; only while on
        // Claude and not already at the floor).
        var saverIdx = tiers.FindIndex(t => t.Id == "saver");
        var balancedIdx = tiers.FindIndex(t => t.Id == "balanced");
        if (saverIdx >= 0 && balancedIdx >= 0
            && tiers[saverIdx].Advice.Model == tiers[balancedIdx].Advice.Model
            && tiers[saverIdx].Advice.Model != ClaudeModel.Haiku45)
        {
            tiers[saverIdx] = ForcingHeadDown(tiers[saverIdx]);
        }

        return tiers;
    }

    /// <summary>Lower the orchestrator (head) model by one Claude tier,
    /// re-estimating cost — used to keep the Economy tier's headline model
    /// distinct from Recommended.</summary>
    private static Tier ForcingHeadDown(Tier t)
    {
        var orchestrator = t.Advice.Strategy.Orchestrator;
        if (orchestrator is null || orchestrator.Provider != AIProvider.Claude) return t;
        var lowered = DownTier(orchestrator.Model);
        if (lowered == orchestrator.Model) return t;

        var newRoles = t.Advice.Strategy.Roles.Select(r => r.Clone()).ToList();
        var newOrchestrator = newRoles.First(r => r.IsOrchestrator);
        newOrchestrator.Model = lowered;
        var s = t.Advice.Strategy;
        var newStrategy = new Strategy(s.Name, s.Description, newRoles, s.OrchestrationNotes,
            new List<McpServer>(s.McpServers), new List<string>(s.Skills), s.EvalSuite,
            new List<ToolCheck>(s.ToolChecks), s.Id);

        var advice = new Advice(lowered, newStrategy, t.Advice.ShapeRationaleKey, t.Advice.LoopKind,
            t.Advice.Effort, t.Advice.DecisionPath, t.Advice.GoalSuggestion,
            CostEstimator.Estimate(newStrategy, t.Advice.Effort),
            t.Advice.AiRationale, t.Advice.UsedAI, t.Advice.ProviderPicks);
        return t with { Advice = advice };
    }

    /// <summary>Build a cost/quality variant of an advice: shift EVERY agent's
    /// model up/down a tier (so the whole team visibly changes), nudge the
    /// largest fan-out role's count, and re-estimate the cost.</summary>
    private static Tier Variant(Advice baseAdvice, int shift, int countDelta, CostEffort effort,
        string id, string labelKey, string noteKey)
    {
        var newRoles = baseAdvice.Strategy.Roles.Select(r => r.Clone()).ToList();
        foreach (var role in newRoles) role.Model = ShiftModel(role.Model, shift);

        var subIdx = newRoles.Where(r => !r.IsOrchestrator).ToList();
        if (subIdx.Count > 0)
        {
            var target = subIdx.OrderByDescending(r => r.Count).First();
            target.Count = Math.Max(1, Math.Min(6, target.Count + countDelta));
        }

        var s = baseAdvice.Strategy;
        var newStrategy = new Strategy(s.Name, s.Description, newRoles, s.OrchestrationNotes,
            new List<McpServer>(s.McpServers), new List<string>(s.Skills), s.EvalSuite,
            new List<ToolCheck>(s.ToolChecks), s.Id);

        var headModel = newRoles.FirstOrDefault(r => r.IsOrchestrator)?.Model ?? ShiftModel(baseAdvice.Model, shift);
        var advice = new Advice(headModel, newStrategy, baseAdvice.ShapeRationaleKey, baseAdvice.LoopKind,
            effort, baseAdvice.DecisionPath, baseAdvice.GoalSuggestion,
            CostEstimator.Estimate(newStrategy, effort), baseAdvice.AiRationale, baseAdvice.UsedAI);
        return new Tier(id, labelKey, noteKey, advice);
    }

    private static ClaudeModel ShiftModel(ClaudeModel model, int by) =>
        by < 0 ? DownTier(model) : (by > 0 ? UpTier(model) : model);

    /// <summary>True when two advices land on the same model AND the same
    /// role name/count list — used to dedup a tier that collapsed onto
    /// another.</summary>
    private static bool SameShape(Advice a, Advice b) =>
        a.Model == b.Model && a.Strategy.Roles.Select(r => $"{r.Name}|{r.Count}")
            .SequenceEqual(b.Strategy.Roles.Select(r => $"{r.Name}|{r.Count}"));

    private static ClaudeModel DownTier(ClaudeModel m) => m switch
    {
        ClaudeModel.Fable5 => ClaudeModel.Opus5,
        ClaudeModel.Opus5 or ClaudeModel.Opus48 => ClaudeModel.Sonnet5,
        ClaudeModel.Sonnet5 => ClaudeModel.Haiku45,
        ClaudeModel.Haiku45 => ClaudeModel.Haiku45,
        _ => m,
    };

    private static ClaudeModel UpTier(ClaudeModel m) => m switch
    {
        ClaudeModel.Haiku45 => ClaudeModel.Sonnet5,
        ClaudeModel.Sonnet5 => ClaudeModel.Opus5,
        ClaudeModel.Opus5 or ClaudeModel.Opus48 => ClaudeModel.Fable5,
        ClaudeModel.Fable5 => ClaudeModel.Fable5,
        _ => m,
    };

    private static CostEffort Lower(CostEffort e) => e switch
    {
        CostEffort.High => CostEffort.Medium,
        CostEffort.Medium => CostEffort.Low,
        CostEffort.Low => CostEffort.Low,
        _ => e,
    };

    private static CostEffort Higher(CostEffort e) => e switch
    {
        CostEffort.Low => CostEffort.Medium,
        CostEffort.Medium => CostEffort.High,
        CostEffort.High => CostEffort.High,
        _ => e,
    };

    /// <summary>Reassign providers on top of an already-built advice (used by
    /// <see cref="AdviseTiers"/>, where each tier gets its own bias). Unlike
    /// Swift's original, this never needs to await anything: it only wraps
    /// the already-ported, synchronous <see cref="AssignProviders"/> — it
    /// never depended on <c>adviseWithAI</c> itself, only <c>adviseCrossProvider</c>
    /// (not ported) does.</summary>
    public static Advice ApplyingProviders(
        Advice advice,
        IReadOnlySet<AIProvider> connected,
        TierBias bias,
        IReadOnlySet<AIProvider>? deprioritize = null,
        IReadOnlySet<AIProvider>? modelLocked = null)
    {
        var (s, picks) = AssignProviders(advice.Strategy, connected, bias, deprioritize, modelLocked);
        // The launch command's headline model tracks the orchestrator only
        // while it stays on Claude (the session model is a Claude Code concept).
        var head = s.Orchestrator?.Provider == AIProvider.Claude
            ? (s.Orchestrator?.Model ?? advice.Model)
            : advice.Model;
        return new Advice(head, s, advice.ShapeRationaleKey, advice.LoopKind, advice.Effort,
            advice.DecisionPath, advice.GoalSuggestion, CostEstimator.Estimate(s, advice.Effort),
            advice.AiRationale, advice.UsedAI, picks);
    }
}
