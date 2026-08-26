using Coral.Core.Models;

namespace Coral.Core.Services;

/// <summary>
/// Port of <c>StrategyForge/Services/AdvisorEngine+Providers.swift</c> — the
/// cross-provider brain: given a team, reassign the best (provider, model) to
/// each role, but only among CONNECTED providers. Pure and deterministic:
/// same inputs always produce the same picks.
///
/// Two ideas drive it: (1) a capability profile per model (reasoning/coding/
/// breadth/speed), each role kind maxing the axis it cares about; (2)
/// diversity by design — the reviewer is pushed onto a different model
/// family than the coder, so a second opinion has a different failure
/// profile.
///
/// NOT ported in this pass (see <c>AdvisorEngine.cs</c>'s class doc comment):
/// <c>aspirationalPicks</c>, <c>adviseCrossProvider</c>, <c>applyingProviders</c>.
/// </summary>
public static partial class AdvisorEngine
{
    // MARK: - Capability profile

    /// <summary>The axes a model can be strong on. A role picks the one that matters to it.</summary>
    public enum Axis { Reasoning, Coding, Breadth, Speed }

    /// <summary>How aggressively to trade cost for quality.</summary>
    public enum TierBias { Saver, Balanced, Max }

    /// <summary>Map a tier id ("saver" | "balanced" | "max", see
    /// <see cref="Tier.Id"/>) to its bias — used by <see cref="AdviseTiers"/>
    /// so each tier's cross-provider reassignment leans the same direction
    /// as its cost/quality intent.</summary>
    public static TierBias TierBiasFrom(string tierId) => tierId switch
    {
        "saver" => TierBias.Saver,
        "max" => TierBias.Max,
        _ => TierBias.Balanced,
    };

    /// <summary>A model's capability fingerprint, scored 1-5 per axis. The
    /// numbers are a deliberate, editable starting point, mirrored verbatim
    /// from the Swift catalog.</summary>
    public sealed record ModelProfile(AIProvider Provider, string ModelId, string DisplayName,
        int Reasoning, int Coding, int Breadth, int Speed)
    {
        public int Value(Axis axis) => axis switch
        {
            Axis.Reasoning => Reasoning,
            Axis.Coding => Coding,
            Axis.Breadth => Breadth,
            Axis.Speed => Speed,
            _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, null),
        };
    }

    /// <summary>The catalog. Non-Claude <see cref="ModelProfile.ModelId"/>s must stay
    /// in sync with <see cref="ModelCatalog"/> (guarded by
    /// AdvisorProvidersTests.EveryNonClaudeProfileIdExistsInItsProvider), since
    /// that's what the CLI actually receives.</summary>
    public static readonly IReadOnlyList<ModelProfile> ModelProfiles = new List<ModelProfile>
    {
        // Claude
        new(AIProvider.Claude, ClaudeModel.Opus5.ToRawValue(), ClaudeModel.Opus5.DisplayName(), 5, 4, 4, 2),
        new(AIProvider.Claude, ClaudeModel.Opus48.ToRawValue(), ClaudeModel.Opus48.DisplayName(), 5, 4, 4, 2), // legacy, still selectable
        new(AIProvider.Claude, ClaudeModel.Fable5.ToRawValue(), ClaudeModel.Fable5.DisplayName(), 5, 4, 4, 1),
        new(AIProvider.Claude, ClaudeModel.Sonnet5.ToRawValue(), ClaudeModel.Sonnet5.DisplayName(), 4, 4, 3, 3),
        new(AIProvider.Claude, ClaudeModel.Haiku45.ToRawValue(), ClaudeModel.Haiku45.DisplayName(), 2, 2, 2, 5),
        // OpenAI / Codex. NOTE: gpt-5-codex is API-only (errors on a ChatGPT
        // subscription login), so it's intentionally NOT in ModelCatalog's
        // Openai list — but stays profiled here as the strongest OpenAI coder
        // for API-key users. The catalog-integrity test allows this known
        // API-only id.
        new(AIProvider.Openai, "gpt-5-codex", "GPT-5 Codex", 4, 5, 3, 3),
        new(AIProvider.Openai, "gpt-5", "GPT-5", 4, 4, 3, 3),
        new(AIProvider.Openai, "gpt-5-mini", "GPT-5 mini", 2, 3, 2, 5),
        // Gemini
        new(AIProvider.Gemini, "gemini-2.5-pro", "Gemini 2.5 Pro", 4, 3, 5, 3),
        new(AIProvider.Gemini, "gemini-2.5-flash", "Gemini 2.5 Flash", 3, 2, 5, 5),
    };

    // MARK: - Explained pick

    /// <summary>One role's cross-provider assignment, for a future "why" panel.</summary>
    /// <param name="IsConnected">False when this pick is part of an aspirational
    /// (display-only) mix and the provider isn't connected yet. Always true for
    /// anything <see cref="AssignProviders"/> itself returns — a run can never
    /// target a provider the user hasn't connected.</param>
    public sealed record ProviderPick(string RoleName, RoleKind RoleKind, AIProvider Provider,
        string ModelDisplayName, string ReasonKey, bool IsConnected = true);

    // MARK: - Role -> axis

    /// <summary>The axis a role kind optimizes for. Reviewers/advisors nominally
    /// want reasoning, but they're resolved separately (diversity beats raw score).</summary>
    public static Axis PrimaryAxis(RoleKind kind) => kind switch
    {
        RoleKind.Orchestrator or RoleKind.Planner => Axis.Reasoning,
        RoleKind.Worker or RoleKind.Specialist => Axis.Coding,
        RoleKind.Researcher => Axis.Breadth,
        RoleKind.Reviewer or RoleKind.Advisor => Axis.Reasoning,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static bool IsReviewRole(RoleKind kind) => kind is RoleKind.Reviewer or RoleKind.Advisor;

    // MARK: - Cost band (keep a swap from inflating a role's price)

    /// <summary>Rough cost band of a model: 0 = economy, 1 = mid, 2 = frontier. A
    /// cross-provider swap must keep a role's COST intent — a deliberately cheap
    /// seat should map to another low-cost model, never be silently upgraded.</summary>
    public static int CostBand(string modelId)
    {
        if (modelId == ClaudeModel.Haiku45.ToRawValue() || modelId == "gpt-5-mini" || modelId == "gemini-2.5-flash")
            return 0;
        if (modelId == ClaudeModel.Sonnet5.ToRawValue() || modelId == "gpt-5" || modelId == "gpt-5-codex" || modelId == "gemini-2.5-pro")
            return 1;
        return 2; // opus48, fable5, and any unknown id -> treat as frontier
    }

    /// <summary>The model id a role currently targets (Claude -> Model, others -> ProviderModelId).</summary>
    private static string CurrentModelId(AgentRole role) =>
        role.Provider == AIProvider.Claude ? role.Model.ToRawValue() : (role.ProviderModelId ?? role.Model.ToRawValue());

    /// <summary>Candidates no more expensive than <paramref name="band"/>. Falls
    /// back to the full list if none qualify, so assignment always finds a model.</summary>
    private static List<ModelProfile> Capped(List<ModelProfile> candidates, int band)
    {
        var within = candidates.Where(c => CostBand(c.ModelId) <= band).ToList();
        return within.Count == 0 ? candidates : within;
    }

    private static string ReasonKey(Axis axis) => axis switch
    {
        Axis.Reasoning => "advisor.provider.reason.reasoning",
        Axis.Coding => "advisor.provider.reason.coding",
        Axis.Breadth => "advisor.provider.reason.breadth",
        Axis.Speed => "advisor.provider.reason.speed",
        _ => throw new ArgumentOutOfRangeException(nameof(axis), axis, null),
    };

    // MARK: - Assignment (pure)

    /// <summary>Reassign the best connected (provider, model) to each role in
    /// <paramref name="strategy"/>. Returns a NEW strategy (the input is never
    /// mutated) plus an ordered, explained list of picks.
    ///
    /// No-op (returns the strategy unchanged, no picks) unless at least TWO
    /// distinct providers are connected — so a Claude-only user sees exactly
    /// today's behavior.</summary>
    public static (Strategy Strategy, List<ProviderPick> Picks) AssignProviders(
        Strategy strategy,
        IReadOnlySet<AIProvider> connected,
        TierBias bias = TierBias.Balanced,
        IReadOnlySet<AIProvider>? deprioritize = null,
        IReadOnlySet<AIProvider>? modelLocked = null)
    {
        var deprioritizeSet = deprioritize ?? new HashSet<AIProvider>();
        var modelLockedSet = modelLocked ?? new HashSet<AIProvider>();
        var connectedSet = connected.Count == 0
            ? new HashSet<AIProvider> { AIProvider.Claude }
            : new HashSet<AIProvider>(connected);
        var available = CollapsingLocked(
            ModelProfiles.Where(p => connectedSet.Contains(p.Provider)).ToList(), modelLockedSet);
        if (available.Select(p => p.Provider).Distinct().Count() <= 1) return (strategy, new List<ProviderPick>());
        return AssignCore(strategy, available, bias, connectedSet, deprioritizeSet);
    }

    /// <summary>The IDEAL cross-provider mix computed against ALL providers
    /// (ignoring what's connected) — for DISPLAY ONLY. Each pick is flagged
    /// <see cref="ProviderPick.IsConnected"/> so a card can dim the
    /// not-yet-connected ones and offer a one-tap connect. This never
    /// mutates the strategy that actually runs (it returns picks, not a
    /// strategy), so a run can never target a provider the user hasn't
    /// connected.</summary>
    public static List<ProviderPick> AspirationalPicks(
        Strategy strategy,
        IReadOnlySet<AIProvider> connected,
        TierBias bias = TierBias.Balanced,
        IReadOnlySet<AIProvider>? modelLocked = null)
    {
        var available = CollapsingLocked(ModelProfiles.ToList(), modelLocked ?? new HashSet<AIProvider>());
        if (available.Select(p => p.Provider).Distinct().Count() <= 1) return new List<ProviderPick>();
        return AssignCore(strategy, available, bias, connected, new HashSet<AIProvider>()).Picks;
    }

    /// <summary>Collapse each <paramref name="locked"/> provider — one whose CLI
    /// can't select a model (an OpenAI/Codex ChatGPT-account login rejects
    /// <c>--model</c>) — down to a SINGLE "account default" profile, so the
    /// advisor never assigns a specific model the login can't actually run.</summary>
    private static List<ModelProfile> CollapsingLocked(List<ModelProfile> profiles, IReadOnlySet<AIProvider> locked)
    {
        if (locked.Count == 0) return profiles;
        var result = profiles.Where(p => !locked.Contains(p.Provider)).ToList();
        foreach (var provider in locked)
        {
            var own = profiles.Where(p => p.Provider == provider).ToList();
            if (own.Count == 0) continue;
            // Keep the best coder's caps so the provider still competes for the
            // role it fits, just under one honest name. Ties keep catalog order
            // (mirrors Swift's max(by:), which keeps the first max on a tie).
            var best = own.OrderByDescending(p => p.Coding).First();
            result.Add(best with { DisplayName = DefaultModelName(provider) });
        }
        return result;
    }

    /// <summary>The honest display name for a provider's account-default model (no <c>--model</c>).</summary>
    private static string DefaultModelName(AIProvider p) => p switch
    {
        AIProvider.Openai => "ChatGPT · Codex",
        AIProvider.Gemini => "Gemini",
        AIProvider.Claude => ClaudeModel.Opus5.DisplayName(),
        _ => throw new ArgumentOutOfRangeException(nameof(p), p, null),
    };

    /// <summary>Shared assignment core: pick the best (provider, model) per role
    /// from <paramref name="available"/>. Clones every role first, so the input
    /// <paramref name="strategy"/> is never mutated (matches Swift's struct
    /// value semantics — see <c>Strategy.AutoFixed()</c> for the same pattern).</summary>
    private static (Strategy Strategy, List<ProviderPick> Picks) AssignCore(
        Strategy strategy,
        List<ModelProfile> available,
        TierBias bias,
        IReadOnlySet<AIProvider> connected,
        IReadOnlySet<AIProvider> deprioritize)
    {
        var newRoles = strategy.Roles.Select(r => r.Clone()).ToList();
        var s = new Strategy(strategy.Name, strategy.Description, newRoles, strategy.OrchestrationNotes,
            new List<McpServer>(strategy.McpServers), new List<string>(strategy.Skills), strategy.EvalSuite,
            new List<ToolCheck>(strategy.ToolChecks), strategy.Id);

        var ordered = new List<(int Index, ProviderPick Pick)>();
        AIProvider? coderProvider = null;

        ProviderPick Pick(string name, RoleKind kind, ModelProfile profile, string reason) =>
            new(name, kind, profile.Provider, profile.DisplayName, reason, connected.Contains(profile.Provider));

        // Pass 1 - every non-review role by its primary axis, without exceeding
        // the role's original cost band (so an economy seat stays economy).
        for (var i = 0; i < s.Roles.Count; i++)
        {
            if (IsReviewRole(s.Roles[i].Role)) continue;
            var axis = PrimaryAxis(s.Roles[i].Role);
            var band = CostBand(CurrentModelId(s.Roles[i]));
            var pool = Capped(available, band);
            // Keep the ORCHESTRATOR off Gemini — it's the meta run's planner +
            // synthesizer and stalls on that in practice (see Swift's comment
            // on the same rule). Falls back to the full pool only if nothing's left.
            if (s.Roles[i].IsOrchestrator)
            {
                var nonGemini = pool.Where(p => p.Provider != AIProvider.Gemini).ToList();
                if (nonGemini.Count > 0) pool = nonGemini;
            }
            var profile = Best(pool, axis, bias, deprioritize);
            if (profile is null) continue;
            Apply(profile, s.Roles[i]);
            ordered.Add((i, Pick(s.Roles[i].Name, s.Roles[i].Role, profile, ReasonKey(axis))));
            if (s.Roles[i].Role is RoleKind.Worker or RoleKind.Specialist && coderProvider is null)
                coderProvider = profile.Provider;
        }
        // No dedicated coder -> the orchestrator is who "writes", so use its family.
        coderProvider ??= s.Roles.FirstOrDefault(r => r.IsOrchestrator)?.Provider;

        // Pass 2 - review roles: prefer a DIFFERENT family than the coder (diversity).
        for (var i = 0; i < s.Roles.Count; i++)
        {
            if (!IsReviewRole(s.Roles[i].Role)) continue;
            var band = CostBand(CurrentModelId(s.Roles[i]));
            var crossFamily = available.Where(p => p.Provider != coderProvider).ToList();
            var pool = Capped(crossFamily.Count == 0 ? available : crossFamily, band);
            var profile = Best(pool, Axis.Reasoning, bias, deprioritize);
            if (profile is null) continue;
            Apply(profile, s.Roles[i]);
            var reason = crossFamily.Count == 0 ? "advisor.provider.reason.onlyOne" : "advisor.provider.reason.diversity";
            ordered.Add((i, Pick(s.Roles[i].Name, s.Roles[i].Role, profile, reason)));
        }

        // Pass 3 - coverage: make sure every CONNECTED provider is actually used
        // at least once.
        EnsureCoverage(s, ordered, available, connected, bias, deprioritize);

        var picks = ordered.OrderBy(o => o.Index).Select(o => o.Pick).ToList();
        return (s, picks);
    }

    /// <summary>The profile matching a role's CURRENT (provider, model), used to
    /// measure the capability lost by a coverage swap.</summary>
    private static ModelProfile? CurrentProfile(AgentRole role, List<ModelProfile> available) =>
        available.FirstOrDefault(p => p.Provider == role.Provider &&
            (role.Provider == AIProvider.Claude ? p.ModelId == role.Model.ToRawValue() : p.ModelId == role.ProviderModelId));

    /// <summary>Give each connected-but-unused provider one role it genuinely
    /// fits (loss &lt;= 1 on that role's primary axis). Deterministic (providers
    /// in catalog order; ties by role index). Never touches the orchestrator.</summary>
    private static void EnsureCoverage(
        Strategy s,
        List<(int Index, ProviderPick Pick)> ordered,
        List<ModelProfile> available,
        IReadOnlySet<AIProvider> connected,
        TierBias bias,
        IReadOnlySet<AIProvider> deprioritize)
    {
        // Don't force a near-capped provider onto a role just for coverage.
        var usable = new HashSet<AIProvider>(available.Select(p => p.Provider));
        usable.IntersectWith(connected);
        usable.ExceptWith(deprioritize);

        foreach (var provider in Enum.GetValues<AIProvider>())
        {
            if (!usable.Contains(provider)) continue;
            if (s.Roles.Any(r => r.Provider == provider)) continue;

            (int Index, ModelProfile Profile, int Fit)? choice = null;
            for (var i = 0; i < s.Roles.Count; i++)
            {
                if (s.Roles[i].IsOrchestrator) continue;
                var axis = PrimaryAxis(s.Roles[i].Role);
                var band = CostBand(CurrentModelId(s.Roles[i]));
                var pool = Capped(available.Where(p => p.Provider == provider).ToList(), band);
                var cand = Best(pool, axis, bias);
                if (cand is null) continue;
                var currentVal = CurrentProfile(s.Roles[i], available)?.Value(axis) ?? cand.Value(axis);
                if (currentVal - cand.Value(axis) > 1) continue; // loss <= 1
                var fit = cand.Value(axis);
                if (choice is null || fit > choice.Value.Fit) choice = (i, cand, fit);
            }
            if (choice is null) continue;

            var (index, chosenProfile, _) = choice.Value;
            Apply(chosenProfile, s.Roles[index]);
            var newPick = new ProviderPick(s.Roles[index].Name, s.Roles[index].Role,
                chosenProfile.Provider, chosenProfile.DisplayName, "advisor.provider.reason.diversity",
                connected.Contains(chosenProfile.Provider));
            var existingIdx = ordered.FindIndex(o => o.Index == index);
            if (existingIdx >= 0) ordered[existingIdx] = (index, newPick);
            else ordered.Add((index, newPick));
        }
    }

    // MARK: - Scoring (deterministic)

    /// <summary>The best profile for an axis under a bias, with a stable,
    /// deterministic tie-break (score desc, then provider order asc, then model
    /// id asc) so the same inputs never reorder.</summary>
    private static ModelProfile? Best(List<ModelProfile> candidates, Axis axis, TierBias bias,
        IReadOnlySet<AIProvider>? deprioritize = null)
    {
        var dep = deprioritize ?? new HashSet<AIProvider>();
        return candidates
            .OrderByDescending(p => Score(p, axis, bias, dep))
            .ThenBy(p => ProviderOrder(p.Provider))
            .ThenBy(p => p.ModelId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>Weighted score: the priority axis dominates (x100); the bias
    /// nudges the tie-break toward cheap/fast (saver) or top capability (max). A
    /// provider in <paramref name="deprioritize"/> takes a fixed penalty of just
    /// over one axis point, so it loses to a comparable rival but is still used
    /// when it's the only fit.</summary>
    private static int Score(ModelProfile p, Axis axis, TierBias bias, IReadOnlySet<AIProvider> deprioritize)
    {
        var baseScore = p.Value(axis) * 100;
        var penalty = deprioritize.Contains(p.Provider) ? 120 : 0;
        var bonus = bias switch
        {
            TierBias.Saver => p.Speed * 10,
            TierBias.Balanced => p.Speed * 2 + p.Reasoning * 2,
            TierBias.Max => (p.Reasoning + p.Coding) * 10,
            _ => throw new ArgumentOutOfRangeException(nameof(bias), bias, null),
        };
        return baseScore + bonus - penalty;
    }

    private static int ProviderOrder(AIProvider p) => Array.IndexOf(Enum.GetValues<AIProvider>(), p);

    /// <summary>Write a profile onto a role, honoring the Claude vs non-Claude
    /// split (Claude -> Model; others -> ProviderModelId).</summary>
    private static void Apply(ModelProfile p, AgentRole role)
    {
        role.Provider = p.Provider;
        if (p.Provider == AIProvider.Claude)
        {
            role.Model = ClaudeModelExtensions.FromRawValue(p.ModelId) ?? role.Model;
            role.ProviderModelId = null;
        }
        else
        {
            role.ProviderModelId = p.ModelId;
        }
    }
}
