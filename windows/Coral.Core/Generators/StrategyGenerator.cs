using System.Globalization;
using System.Text;
using Coral.Core.Models;

namespace Coral.Core.Generators;

/// <summary>
/// Port of <c>StrategyForge/Generators/StrategyGenerator.swift</c> — the
/// deterministic keyword classifier that maps a plain-language task to a
/// team shape + size, and builds a real <see cref="Strategy"/> from the
/// library templates. PORTED: <see cref="Classify"/> (the multi-axis
/// keyword reader), <see cref="ShapeFor"/> (the profile → shape map),
/// <see cref="BuildStrategy"/>, <see cref="HeuristicShape"/> (the two
/// composed). DELIBERATELY NOT PORTED, same "real platform-redesign
/// reason" cut this port has made elsewhere (see PORT-PLAN.md's Fase 6/7
/// deferred-scope sections): the Apple-on-device-model path
/// (<c>generate(from:)</c>'s AI branch, <c>isAIAvailable</c>, <c>aiStatus</c>,
/// <c>TaskRead</c>) — Windows has no bundled free/private on-device LLM
/// equivalent — and the semantic-embeddings paraphrase assist
/// (<c>SemanticClassifier</c>, also on-device NL embeddings) that
/// <c>heuristicShape</c> layers on top of the keyword read when it's not
/// confident. Both are enhancements on top of this deterministic core, not
/// load-bearing for it — dropping them means a paraphrased task might
/// classify a little less precisely than macOS, never incorrectly in a way
/// the keyword path wouldn't also risk.
/// </summary>
public static class StrategyGenerator
{
    /// <summary>What a coding task primarily asks for.</summary>
    public enum TaskIntent { Understand, Decide, Review, Triage, Change, Build, Harden, Quick, General }

    /// <summary>How much of the codebase a task spans.</summary>
    public enum TaskScope { Point, Module, Repo }

    /// <summary>A deterministic, multi-axis reading of a task. Pure and
    /// cheap; drives shape selection.</summary>
    public readonly record struct TaskProfile(
        TaskIntent Intent, TaskScope Scope, bool Breadth, bool Verifiable, bool Adversarial,
        bool SerialDebug, bool MultiDomain, bool NeedsScouting, bool WillChange, double Confidence)
    {
        /// <summary>A low-confidence read is one the UI shouldn't present as
        /// certain.</summary>
        public bool IsConfident => Confidence >= 0.5;
    }

    /// <summary>Lowercase + fold diacritics (Swift's <c>.folding(options:
    /// .diacriticInsensitive, ...)</c>) via Unicode NFD decomposition and
    /// stripping combining marks — the standard .NET equivalent.</summary>
    private static string Fold(string s)
    {
        var lower = s.ToLowerInvariant();
        var decomposed = lower.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Read a task into a <see cref="TaskProfile"/>. Bilingual
    /// (EN + ES): the text is lowercased and diacritics folded, so every
    /// keyword below is stored accent-free.</summary>
    public static TaskProfile Classify(string task)
    {
        var t = " " + Fold(task) + " ";
        bool Any(params string[] words) => words.Any(t.Contains);
        int Hits(params string[] words) => words.Count(t.Contains);

        var serialDebug = Any("root cause", "root-cause", "causa raiz", "raiz del",
            "why is", "why does", "why the", "why it", "why are", "por que falla",
            "por que se", "por que da", "por que no funciona", "debug", "depura",
            "trace through", "step through", "stack trace", "flaky", "intermitente",
            "intermittent", "bisect");
        var adversarial = Any("adversarial", "attack", "atacar", "break ", "romper",
            "exploit", "harden", "endurec", "red team", "red-team", "pentest",
            "penetration", "sparring", "fuzz");
        var breadth = Any("exhaustiv", "varios frentes", "varias areas", "desde varios",
            "en varios", "en paralelo", "in parallel", "several fronts", "multiple angles",
            "many areas", "backlog", "cada modulo", "every module", "every component",
            "batch", "todo el codigo", "entire codebase", "whole codebase");
        var verifiable = Any("test", "lint", "compil", "build succeeds", "deploy",
            "hasta que", "until", "score", "pasen", "que pase", "green ");
        var needsScouting = Any("unfamiliar", "legacy", "explore", "explora", "investiga",
            "research", "how does", "como funciona", "map the", "no conozco", "desconocid",
            "understand", "entender");
        var repo = Any("codebase", "code base", "repo", "across all", "all files",
            "every file", "entire", "whole ", "todo el", "toda la", "project-wide",
            "monorepo", "microservice");
        var module = Any("module", "modulo", "component", "componente", "service",
            "servicio", "file", "archivo", "class ", "clase", "function", "funcion",
            "endpoint");
        var scope = repo ? TaskScope.Repo : (module ? TaskScope.Module : TaskScope.Point);
        var multiDomain = Hits("frontend", "backend", "database", "base de datos",
            "security", "seguridad", "infra", "api", "mobile", "ui ", "devops") >= 2;
        // Does the task ultimately CHANGE the code (vs. pure understanding)? Separates
        // a research fan-out from the full build pipeline.
        var willChange = Any("rewrite", "reescrib", "refactor", "migrate", "migrar",
            "implement", "implementa", "build", "construir", "add ", "create", "crea",
            "feature", "funcionalidad", "change", "cambia", " port ", "upgrade");

        // Intent — first matching group wins; the order resolves ambiguous tasks
        // (e.g. "explain then refactor" reads as understand, not change). NOTE:
        // "unfamiliar"/"legacy" are scouting SIGNALS, not an understand intent — a
        // "change this unfamiliar module" is a change that needs scouting, not research.
        TaskIntent intent;
        if (Any("understand", "explain", "explica", "explora", "explore", "investiga",
                "investigar", "research", "how does", "como funciona",
                "entender", "comprender", "study"))
        {
            intent = TaskIntent.Understand;
        }
        else if (Any("architecture", "arquitectura", "design", "disen", "trade-off",
                "tradeoff", "decide", "decidir", "compare", "comparar", "option",
                "opcion", "choose", "elegir", "consensus", "consenso", "evaluate", "evalua"))
        {
            intent = TaskIntent.Decide;
        }
        else if (Any("review", "revis", "audit", "auditar", "auditoria", "verify",
                "verificar", "qa", "code review", "inspecc", "coverage", "cobertura",
                "production", "critical", "critico", "produccion", "safe"))
        {
            intent = TaskIntent.Review;
        }
        else if (Any("triage", "triar", "router", "route ", "routing", "classify",
                "clasific", "categor", "dispatch", "sort into", "prioriti", "prioriza"))
        {
            intent = TaskIntent.Triage;
        }
        else if (adversarial)
        {
            intent = TaskIntent.Harden;
        }
        else if (Any("refactor", "migrate", "migrar", "migracion", "rename", "renombr",
                "bulk", "masivo", "convert", " port ", "upgrade", "actualiza", "cleanup",
                "fix", "arregl", "replace", "reemplaza", "across", "todos los", "en todo",
                "change", "cambia", "modif", "tweak", "adjust", "edit "))
        {
            intent = TaskIntent.Change;
        }
        else if (Any("implement", "implementa", "add ", "anad", "create", "crea",
                "build", "construir", "feature", "funcionalidad", "nuevo", "nueva",
                "write", "escribe", "generate", "genera"))
        {
            intent = TaskIntent.Build;
        }
        else if (Any("experiment", "experimenta", "quick", "rapid", "small", "pequen",
                "play", "prueba", "try "))
        {
            intent = TaskIntent.Quick;
        }
        else
        {
            intent = TaskIntent.General;
        }

        // Confidence: how decisively the task read. A clear intent (or a decisive flag
        // like a debug hunt / adversarial ask) plus supporting axes reads high; a bare
        // `general` task with no axes reads low (the UI can then offer to clarify).
        var axes = 0;
        foreach (var on in new[] { breadth, verifiable, adversarial, serialDebug, multiDomain,
                     needsScouting, scope != TaskScope.Point })
        {
            if (on) axes++;
        }
        var decisive = intent != TaskIntent.General || serialDebug || adversarial;
        var confidence = Math.Min(1.0, (decisive ? 0.6 : 0.35) + 0.1 * axes);

        return new TaskProfile(intent, scope, breadth, verifiable, adversarial,
            serialDebug, multiDomain, needsScouting, willChange, confidence);
    }

    /// <summary>Map a task profile (+ what's connected) to a concrete shape
    /// + team size. Pure and total — every profile resolves to exactly one
    /// shape.</summary>
    public static (StrategyShape Shape, int TeamSize) ShapeFor(TaskProfile p, ISet<AIProvider>? connected = null)
    {
        connected ??= new HashSet<AIProvider>();
        var multi = connected.Count > 1;
        var size = p.Breadth ? 4 : 3;
        // A broad, splittable task: distinct specialists when several providers can
        // each take a seat, else identical workers.
        (StrategyShape, int) Fanout() => multi ? (StrategyShape.DomainSpecialists, size) : (StrategyShape.OrchestratorWorkers, size);

        // 1. Serial root-cause hunt — non-delegable (Advise() also enforces this).
        if (p.SerialDebug) return (StrategyShape.RootCauseDebugging, 1);
        // 2. Adversarial hardening.
        if (p.Intent == TaskIntent.Harden) return (StrategyShape.Sparring, 1);
        // 3. Classify / route incoming work.
        if (p.Intent == TaskIntent.Triage) return (StrategyShape.TriageRouter, 1);
        // 4. Understand / explore.
        if (p.Intent == TaskIntent.Understand)
        {
            // Large, unfamiliar, broad understanding that LEADS TO a change → the
            // full scout → plan → build → review pipeline; pure research → a
            // research fan-out.
            if (p.Scope == TaskScope.Repo && p.Breadth && p.WillChange) return (StrategyShape.Pipeline, size);
            return (StrategyShape.ResearchFanout, size);
        }
        // 5. Decide / design.
        if (p.Intent == TaskIntent.Decide)
        {
            return (p.MultiDomain || p.Breadth) ? (StrategyShape.DomainSpecialists, size) : (StrategyShape.DebateConsensus, 1);
        }
        // 6. Review / audit — broad, multi-domain, or repo-wide → a specialist per angle.
        if (p.Intent == TaskIntent.Review)
        {
            return (p.Breadth || p.MultiDomain || p.Scope == TaskScope.Repo)
                ? (StrategyShape.DomainSpecialists, size)
                : (StrategyShape.PlannerReviewer, 1);
        }
        // 7. Change (refactor / migrate / bulk edit).
        if (p.Intent == TaskIntent.Change)
        {
            if (p.Scope == TaskScope.Repo || p.Breadth) return Fanout();
            if (p.MultiDomain) return (StrategyShape.DomainSpecialists, size);
            if (p.NeedsScouting) return (StrategyShape.ScoutAct, 1); // localized change in an unfamiliar area
            return (StrategyShape.ExecutorAdvisor, 1);
        }
        // 8. Build a feature.
        if (p.Intent == TaskIntent.Build)
        {
            if (p.Scope == TaskScope.Repo && p.Breadth) return Fanout();
            if (p.MultiDomain) return (StrategyShape.DomainSpecialists, size);
            if (p.Verifiable) return (StrategyShape.PlannerReviewer, 1);
            if (p.NeedsScouting) return (StrategyShape.ScoutAct, 1);
            return (StrategyShape.ExecutorAdvisor, 1);
        }
        // 9. Quick / trivial.
        if (p.Intent == TaskIntent.Quick) return (StrategyShape.Solo, 1);
        // 10. Any remaining breadth signal → a small fan-out.
        if (p.Breadth) return Fanout();
        // 11. Default: a lean executor + advisor.
        return (StrategyShape.ExecutorAdvisor, 1);
    }

    /// <summary>The deterministic classifier <see cref="Generators.AdvisorEngine"/>
    /// relies on. Pure and cheap (substring scans on normalized text);
    /// <paramref name="connected"/> defaults to empty so existing callers
    /// keep single-provider behavior. Unlike Swift's <c>heuristicShape</c>,
    /// does NOT layer a semantic-embeddings paraphrase assist on top of a
    /// low-confidence keyword read — see this type's doc comment.</summary>
    public static (StrategyShape Shape, int TeamSize) HeuristicShape(string task, ISet<AIProvider>? connected = null) =>
        ShapeFor(Classify(task), connected);

    /// <summary>Build a real, legal <see cref="Strategy"/> from a shape +
    /// size.</summary>
    public static Strategy BuildStrategy(StrategyShape shape, int teamSize)
    {
        var s = shape switch
        {
            StrategyShape.Solo => StrategyLibrary.Solo(),
            StrategyShape.ExecutorAdvisor => StrategyLibrary.ExecutorAdvisor(),
            StrategyShape.PlannerReviewer => StrategyLibrary.PlannerImplementersReviewer(),
            StrategyShape.OrchestratorWorkers => StrategyLibrary.OrchestratorWorkers(),
            StrategyShape.ResearchFanout => StrategyLibrary.ResearchFanout(),
            StrategyShape.DebateConsensus => StrategyLibrary.DebateConsensus(),
            StrategyShape.DomainSpecialists => StrategyLibrary.DomainSpecialists(),
            StrategyShape.Sparring => StrategyLibrary.Sparring(),
            StrategyShape.ScoutAct => StrategyLibrary.ScoutAct(),
            StrategyShape.TriageRouter => StrategyLibrary.TriageRouter(),
            StrategyShape.RootCauseDebugging => StrategyLibrary.RootCauseDebugging(),
            StrategyShape.Pipeline => StrategyLibrary.ExplorePlanBuildReview(),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, null),
        };

        // Scale the primary fan-out role (the one that already has count > 1).
        var size = Math.Max(1, Math.Min(teamSize, 6));
        var subagentIndices = Enumerable.Range(0, s.Roles.Count).Where(i => !s.Roles[i].IsOrchestrator).ToList();
        if (subagentIndices.Count > 0)
        {
            var idx = subagentIndices.OrderByDescending(i => s.Roles[i].Count).First();
            if (s.Roles[idx].Count > 1) s.Roles[idx].Count = size;
        }
        // Guarantee a legal topology no matter what the model returned.
        return s.AutoFixed();
    }
}
