using Coral.Core.Generators;
using Coral.Core.Models;

namespace Coral.Core.Services;

/// <summary>
/// Port of <c>StrategyForge/Services/AdvisorEngine.swift</c> — the Advisor's
/// brain: a pure, deterministic decision tree that maps a plain-language
/// task to a recommended Claude model, team strategy, loop kind and effort
/// level, WITHOUT creating anything. Every branch taken is recorded as a
/// <see cref="DecisionStep"/> so a future UI can render the path visually.
/// No UI, no network, fully testable.
///
/// Matching is bilingual (EN + ES): the task is lowercased and diacritics
/// are folded ("rápido" → "rapido"), same as <see cref="StrategyGenerator"/>.
///
/// SCOPE for this pass: <see cref="Advise"/> only — the plain deterministic
/// recommendation. Deliberately NOT ported (same "real platform-redesign
/// reason" cut as <see cref="StrategyGenerator"/>'s doc comment explains):
/// <c>adviseWithAI</c> (the on-device Apple Intelligence upgrade — no
/// Windows equivalent) and <c>adviseTiers</c> (the Economy/Recommended/Max
/// three-tier UI, which wraps <c>adviseWithAI</c> and belongs with the UI
/// polish pass, not the engine port). Also not yet ported:
/// <c>AdvisorEngine+Providers.swift</c>'s <c>assignProviders</c> — now ported,
/// see <c>AdvisorEngine.Providers.cs</c> (same file-split as the Swift
/// original's extension file), including <c>aspirationalPicks</c>.
/// <c>adviseTiers</c>/<c>applyingProviders</c> are now ported too, see
/// <c>AdvisorEngine.Tiers.cs</c> — <c>applyingProviders</c> only needed an
/// already-built <see cref="Advice"/> plus the already-ported
/// <see cref="AssignProviders"/>, it never actually depended on
/// <c>adviseWithAI</c> itself. Still NOT ported: <c>adviseWithAI</c> (the
/// on-device AI upgrade) and <c>adviseCrossProvider</c> (which wraps it) —
/// <c>AdviseTiers</c> substitutes the plain <see cref="Advise"/> wherever
/// Swift's <c>adviseTiers</c> calls <c>adviseWithAI</c>, exactly the
/// fallback path Swift itself takes when AI is unavailable, so this is a
/// substitution with an already-proven-equivalent behavior, not a cut.
/// </summary>
public static partial class AdvisorEngine
{
    /// <summary>One node of the decision path: the question asked, the
    /// answer taken, and which signals fired (as localization keys, capped
    /// at 3).</summary>
    public sealed record DecisionStep(int Id, string QuestionKey, bool AnswerIsYes, string AnswerKey,
        IReadOnlyList<string> EvidenceKeys);

    /// <summary>The full recommendation. Equality is semantic (matches
    /// <c>Advice.swift</c>'s custom <c>==</c>): the library builders mint a
    /// fresh <see cref="Strategy.Id"/> on every call, so identity/Guid
    /// fields are deliberately excluded — two identical recommendations
    /// must still compare equal (determinism).</summary>
    public sealed class Advice : IEquatable<Advice>
    {
        public ClaudeModel Model { get; }
        public Strategy Strategy { get; }
        public string ShapeRationaleKey { get; }
        public LoopKind LoopKind { get; }
        public CostEffort Effort { get; }
        public IReadOnlyList<DecisionStep> DecisionPath { get; }
        /// <summary>A one-line verifiable goal drafted from the task text
        /// (best effort).</summary>
        public string GoalSuggestion { get; }
        public StrategyCost EstimatedCost { get; }
        /// <summary>Free-text rationale from an on-device model, when AI
        /// upgraded the shape. Always empty in this port — no Windows
        /// equivalent to Apple Intelligence, see this class's doc comment —
        /// kept only so <see cref="Tier"/>/<see cref="ApplyingProviders"/>
        /// read the same way the Swift original does.</summary>
        public string AiRationale { get; }
        /// <summary>Always false in this port for the same reason as
        /// <see cref="AiRationale"/>.</summary>
        public bool UsedAI { get; }
        /// <summary>Cross-provider assignments made on top of this advice.
        /// Empty on the Claude-only path; populated when &gt;1 provider is
        /// connected and <see cref="ApplyingProviders"/> mixed providers per
        /// role.</summary>
        public IReadOnlyList<ProviderPick> ProviderPicks { get; }

        public Advice(ClaudeModel model, Strategy strategy, string shapeRationaleKey, LoopKind loopKind,
            CostEffort effort, IReadOnlyList<DecisionStep> decisionPath, string goalSuggestion,
            StrategyCost estimatedCost, string aiRationale = "", bool usedAI = false,
            IReadOnlyList<ProviderPick>? providerPicks = null)
        {
            Model = model;
            Strategy = strategy;
            ShapeRationaleKey = shapeRationaleKey;
            LoopKind = loopKind;
            Effort = effort;
            DecisionPath = decisionPath;
            GoalSuggestion = goalSuggestion;
            EstimatedCost = estimatedCost;
            AiRationale = aiRationale;
            UsedAI = usedAI;
            ProviderPicks = providerPicks ?? Array.Empty<ProviderPick>();
        }

        private static string RoleSignature(AgentRole role) =>
            $"{role.Name}|{role.Model.ToRawValue()}|{role.Count}|{role.Provider.ToKey()}|{role.ProviderModelId ?? ""}";

        public bool Equals(Advice? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return Model == other.Model
                && Strategy.Name == other.Strategy.Name
                && Strategy.Roles.Select(RoleSignature).SequenceEqual(other.Strategy.Roles.Select(RoleSignature))
                && ShapeRationaleKey == other.ShapeRationaleKey
                && LoopKind == other.LoopKind
                && Effort == other.Effort
                && DecisionPath.Count == other.DecisionPath.Count
                && DecisionPath.Zip(other.DecisionPath, StepsEqual).All(x => x)
                && GoalSuggestion == other.GoalSuggestion
                && Math.Abs(EstimatedCost.PerRun - other.EstimatedCost.PerRun) < 0.000_001
                && ProviderPicks.SequenceEqual(other.ProviderPicks);
        }

        private static bool StepsEqual(DecisionStep a, DecisionStep b) =>
            a.Id == b.Id && a.QuestionKey == b.QuestionKey && a.AnswerIsYes == b.AnswerIsYes
            && a.AnswerKey == b.AnswerKey && a.EvidenceKeys.SequenceEqual(b.EvidenceKeys);

        public override bool Equals(object? obj) => Equals(obj as Advice);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Model);
            hash.Add(Strategy.Name);
            foreach (var role in Strategy.Roles) hash.Add(RoleSignature(role));
            hash.Add(ShapeRationaleKey);
            hash.Add(LoopKind);
            hash.Add(Effort);
            hash.Add(DecisionPath.Count);
            hash.Add(GoalSuggestion);
            foreach (var pick in ProviderPicks) hash.Add(pick);
            return hash.ToHashCode();
        }
    }

    // MARK: - Signal tables (keywords pre-normalized: lowercase, no diacritics)

    /// <summary>A named group of keywords; if any keyword occurs in the
    /// normalized task the group "fires" and contributes its evidence key
    /// to the decision step.</summary>
    private sealed record SignalGroup(string EvidenceKey, string[] Keywords);

    /// <summary>Q1 — does the task need more than a quick answer?</summary>
    private static readonly SignalGroup[] DepthGroups =
    {
        // Stems on purpose: "implement" covers implementa(r), "investiga" covers
        // investigate/investigar, "disen" covers diseña(r) once diacritics fold.
        new("advisor.ev.multistep", new[]
        {
            "migrate", "migrar", "refactor", "audit", "build", "construir",
            "implement", "investiga", "design", "disen",
        }),
        new("advisor.ev.scope", new[]
        {
            "repo", "test", "multiple files", "varios archivos", "muchos archivos",
            "all files", "todos los archivos", "en todo", "dias", "days", "hours", "horas",
        }),
        // Explicit breadth ("from several fronts", "exhaustive", "in parallel", a
        // "prioritized backlog") is real depth: it warrants a strong model + a team,
        // so it must NOT collapse to the cheap-model executor+advisor downgrade.
        new("advisor.ev.breadth", new[]
        {
            "exhaustiv", "varios frentes", "desde varios", "en varios", "en paralelo",
            "in parallel", "several fronts", "multiple angles", "many areas", "backlog",
            "varias areas", "auditoria",
        }),
    };

    /// <summary>Q2 — is maximum speed / minimal tokens the priority?
    /// (quick-answer branch)</summary>
    private static readonly SignalGroup[] SpeedGroups =
    {
        new("advisor.ev.speedWords", new[]
        {
            "summar", "resum", "translat", "traduc", "list", "short", "corto",
            "quick", "rapid", "classif", "clasific",
        }),
    };

    /// <summary>Q3 — is this the hardest, most ambitious work? (deep-task
    /// branch)</summary>
    private static readonly SignalGroup[] HardestGroups =
    {
        new("advisor.ev.ambition", new[]
        {
            "architecture", "arquitectura", "multi-day", "varios dias", "deep research",
            "investigacion profunda", "end to end", "end-to-end", "de punta a punta",
        }),
        new("advisor.ev.migration", new[] { "migration", "migracion" }),
        new("advisor.ev.orchestration", new[]
        {
            "orchestrat", "orquestar", "orquesta", "many agents", "muchos agentes",
        }),
    };

    /// <summary>Work with no delegable components: a serial root-cause
    /// investigation where the accumulated context IS the work. Keywords
    /// are high-precision on purpose (diagnosis, not just "bug"), to avoid
    /// collapsing genuinely splittable work.</summary>
    private static readonly SignalGroup[] NonDelegableGroups =
    {
        new("advisor.ev.serialDebug", new[]
        {
            "root cause", "root-cause", "causa raiz", "raiz del",
            "why is", "why does", "why the", "why it", "why are",
            "por que falla", "por que se", "por que da", "por que no funciona",
            "debug", "depura", "trace through", "step through", "stack trace",
            "flaky", "intermitente", "intermittent", "bisect",
        }),
    };

    /// <summary>Loop-kind signals, checked in priority order: goal → time →
    /// event.</summary>
    private static readonly SignalGroup GoalLoopGroup = new("advisor.ev.goalWords", new[]
    {
        "hasta que", "until", "tests pass", "pasen", "lint", "bug fix", "arregl",
        "fixed", "score", "quede limpio",
    });

    private static readonly SignalGroup TimeLoopGroup = new("advisor.ev.timeWords", new[]
    {
        "daily", "diario", "diaria", " cada ", "every morning", "every day",
        "every week", "each morning", "semanal", "weekly", "monitor", "vigila",
    });

    private static readonly SignalGroup EventLoopGroup = new("advisor.ev.eventWords", new[]
    {
        " pr ", "pull request", "webhook", "on failure", "cuando falle",
        "cuando falla", "si falla", "when it fails", "issue triage",
    });

    /// <summary>Verifiable finish lines for the goal draft: (task keyword,
    /// English goal).</summary>
    private static readonly (string Keyword, string Goal)[] VerifiableMarkers =
    {
        ("test", "the tests pass"), ("lint", "the lint is clean"),
        ("build", "the build succeeds"), ("compil", "the build succeeds"),
        ("deploy", "the deploy is verified"), ("bug", "the bug is fixed"),
        ("score", "the target score is reached"),
    };

    // MARK: - Public API

    /// <summary>Run the decision tree on a task. Pure and deterministic:
    /// the same input (task + connected set) always produces the same
    /// (semantically equal) <see cref="Advice"/>. <paramref name="connected"/>
    /// lets the SHAPE itself be provider-aware; it defaults to Claude-only
    /// so existing callers keep today's behavior.</summary>
    public static Advice Advise(string task, ISet<AIProvider>? connected = null)
    {
        connected ??= new HashSet<AIProvider> { AIProvider.Claude };
        var trimmed = task.Trim();
        var text = Normalize(trimmed);

        var steps = new List<DecisionStep>();
        void Record(string question, bool yes, string answer, IReadOnlyList<string> evidence) =>
            steps.Add(new DecisionStep(steps.Count, question, yes, answer, evidence.Take(3).ToList()));

        // Q1 — does it need more than a quick answer?
        var depthEvidence = Fired(DepthGroups, text);
        if (trimmed.Length > 140) depthEvidence.Add("advisor.ev.long");
        var needsDepth = depthEvidence.Count > 0;
        Record("advisor.q.depth", needsDepth, needsDepth ? "advisor.a.depth.yes" : "advisor.a.depth.no", depthEvidence);

        // Q2 / Q3 — pick the model on the branch taken.
        ClaudeModel model;
        if (needsDepth)
        {
            var hardestEvidence = Fired(HardestGroups, text);
            // "complex/complejo" only counts when paired with an explicitly large scope.
            if (Contains(text, "complex", "complejo", "compleja")
                && Contains(text, "entire", "whole", "large", "end to end", "todo el", "toda la", "gran ", "grande"))
            {
                hardestEvidence.Add("advisor.ev.complexScope");
            }
            var hardest = hardestEvidence.Count > 0;
            model = hardest ? ClaudeModel.Fable5 : ClaudeModel.Opus5;
            Record("advisor.q.hardest", hardest, hardest ? "advisor.a.hardest.yes" : "advisor.a.hardest.no", hardestEvidence);
        }
        else
        {
            var speedEvidence = Fired(SpeedGroups, text);
            var speedy = speedEvidence.Count > 0;
            model = speedy ? ClaudeModel.Haiku45 : ClaudeModel.Sonnet5;
            Record("advisor.q.speed", speedy, speedy ? "advisor.a.speed.yes" : "advisor.a.speed.no", speedEvidence);
        }

        // Strategy — reuse the generator's shape heuristic, then adjust: a cheap,
        // fast model doesn't need a fleet, so heavy shapes collapse to solo /
        // executor+advisor. The adjustment is an explicit step in the path.
        var (shape, teamSize) = StrategyGenerator.HeuristicShape(trimmed, connected);
        var rationaleKey = $"advisor.rationale.{ShapeKeySuffix(shape)}";
        var isCheapModel = model == ClaudeModel.Haiku45 || model == ClaudeModel.Sonnet5;
        var isHeavyShape = shape != StrategyShape.Solo && shape != StrategyShape.ExecutorAdvisor;
        // Fan-out shapes are PARALLEL LABOR (many near-identical agents); structural
        // shapes (planner→reviewer, debate, sparring) earn their keep from the SHAPE,
        // not the fleet size. So a cheap model only collapses a fan-out — the
        // structural teams survive.
        var isFanoutShape = shape == StrategyShape.OrchestratorWorkers
            || shape == StrategyShape.DomainSpecialists || shape == StrategyShape.ResearchFanout;
        // A serial root-cause hunt has nothing to hand off — the accumulated context
        // is the work. Unlike the cheap-model downgrade, the model tier is kept:
        // debugging is judgment-heavy and still wants a capable lead.
        var nonDelegableEvidence = Fired(NonDelegableGroups, text);
        var isNonDelegable = nonDelegableEvidence.Count > 0;
        if (isHeavyShape)
        {
            if (isNonDelegable)
            {
                shape = StrategyShape.ExecutorAdvisor;
                teamSize = 1;
                rationaleKey = "advisor.rationale.nondelegable";
                Record("advisor.q.team", false, "advisor.a.team.no", nonDelegableEvidence);
            }
            else if (isCheapModel && isFanoutShape)
            {
                shape = model == ClaudeModel.Haiku45 ? StrategyShape.Solo : StrategyShape.ExecutorAdvisor;
                teamSize = 1;
                rationaleKey = "advisor.rationale.downgraded";
                Record("advisor.q.team", false, "advisor.a.team.no", new List<string> { "advisor.ev.cheapModel" });
            }
            else
            {
                Record("advisor.q.team", true, "advisor.a.team.yes", Array.Empty<string>());
            }
        }
        var strategy = StrategyGenerator.BuildStrategy(shape, teamSize);
        // The orchestrator is the main session — it runs the recommended model, so
        // the launch command and the cost estimate reflect the advice.
        if (strategy.Orchestrator is { } orchestrator) orchestrator.Model = model;

        // Loop kind — verifiable goal beats schedule beats external trigger.
        LoopKind loopKind;
        List<string> loopEvidence;
        if (Contains(text, GoalLoopGroup.Keywords)) { loopKind = LoopKind.GoalBased; loopEvidence = new List<string> { GoalLoopGroup.EvidenceKey }; }
        else if (Contains(text, TimeLoopGroup.Keywords)) { loopKind = LoopKind.TimeBased; loopEvidence = new List<string> { TimeLoopGroup.EvidenceKey }; }
        else if (Contains(text, EventLoopGroup.Keywords)) { loopKind = LoopKind.Proactive; loopEvidence = new List<string> { EventLoopGroup.EvidenceKey }; }
        else { loopKind = LoopKind.TurnBased; loopEvidence = new List<string>(); }
        Record("advisor.q.loop", loopKind != LoopKind.TurnBased, $"advisor.a.loop.{LoopKindRawValue(loopKind)}", loopEvidence);

        // Effort — model tier plus scope. Medium is the estimator's baseline.
        var effort = model switch
        {
            ClaudeModel.Haiku45 => CostEffort.Low,
            ClaudeModel.Sonnet5 => CostEffort.Medium,
            ClaudeModel.Opus5 or ClaudeModel.Opus48 => (trimmed.Length > 280 || depthEvidence.Count >= 2) ? CostEffort.High : CostEffort.Medium,
            ClaudeModel.Fable5 => CostEffort.High,
            _ => CostEffort.Medium,
        };

        return new Advice(model, strategy, rationaleKey, loopKind, effort, steps,
            DraftGoal(trimmed, text), CostEstimator.Estimate(strategy, effort));
    }

    // MARK: - Helpers

    /// <summary>Lowercase, fold diacritics, strip light punctuation,
    /// collapse whitespace, and pad with spaces so word-boundary keywords
    /// like " cada " can match.</summary>
    private static string Normalize(string s)
    {
        var folded = Fold(s);
        var stripped = new string(folded.Select(c => ",.;:!?¡¿()[]\"'".Contains(c) ? ' ' : c).ToArray());
        var collapsed = string.Join(" ", stripped.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return " " + collapsed + " ";
    }

    private static string Fold(string s)
    {
        var lower = s.ToLowerInvariant();
        var decomposed = lower.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(c);
        }
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    /// <summary>Evidence keys of every group with at least one keyword hit,
    /// in table order.</summary>
    private static List<string> Fired(IEnumerable<SignalGroup> groups, string text) =>
        groups.Where(g => g.Keywords.Any(text.Contains)).Select(g => g.EvidenceKey).ToList();

    private static bool Contains(string text, params string[] keywords) => keywords.Any(text.Contains);

    /// <summary>Stable localization-key suffix per shape (mirrors
    /// <c>AppModel.strategyKey</c>).</summary>
    private static string ShapeKeySuffix(StrategyShape shape) => shape switch
    {
        StrategyShape.Solo => "solo",
        StrategyShape.ExecutorAdvisor => "execadv",
        StrategyShape.PlannerReviewer => "planner",
        StrategyShape.OrchestratorWorkers => "fanout",
        StrategyShape.ResearchFanout => "research",
        StrategyShape.DebateConsensus => "debate",
        StrategyShape.DomainSpecialists => "domain",
        StrategyShape.Sparring => "sparring",
        StrategyShape.ScoutAct => "scout",
        StrategyShape.TriageRouter => "triage",
        StrategyShape.RootCauseDebugging => "rootcause",
        StrategyShape.Pipeline => "pipeline",
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
    };

    private static string LoopKindRawValue(LoopKind kind) => kind switch
    {
        LoopKind.TurnBased => "turnBased",
        LoopKind.GoalBased => "goalBased",
        LoopKind.TimeBased => "timeBased",
        LoopKind.Proactive => "proactive",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Draft a one-line verifiable goal from the task's own words
    /// (best effort).</summary>
    private static string DraftGoal(string task, string normalized)
    {
        var firstLine = task.Split('\n', '\r').FirstOrDefault() ?? task;
        // The task already states its own finish line ("hasta que…", "until…"):
        // reuse it verbatim — it IS the goal.
        if (normalized.Contains("hasta que") || normalized.Contains("until"))
        {
            return Cap(firstLine, 140).Trim();
        }
        var snippet = Cap(firstLine, 80).Trim();
        // A verifiable artifact is mentioned: attach an "until it passes" clause.
        foreach (var (keyword, goal) in VerifiableMarkers)
        {
            if (normalized.Contains(keyword)) return $"{snippet} — until {goal}";
        }
        // Generic fallback: the task plus a completion criterion.
        return $"{snippet} — until the result is verified and complete";
    }

    private static string Cap(string s, int limit) => s.Length <= limit ? s : s[..limit];
}
