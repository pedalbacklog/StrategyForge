namespace Coral.Core.Models;

/// <summary>
/// Port of <c>StrategyForge/Generators/StrategyGenerator.swift</c>'s
/// <c>StrategyShape</c> — the team shape a task classifies into. Mirrors
/// the library's templates (see <see cref="StrategyGenerator.BuildStrategy"/>).
/// </summary>
public enum StrategyShape
{
    Solo,
    ExecutorAdvisor,
    PlannerReviewer,
    OrchestratorWorkers,
    ResearchFanout,
    DebateConsensus,
    DomainSpecialists,
    Sparring,
    /// <summary>Cheap read-only scout maps the terrain, then an implementer
    /// acts on a brief.</summary>
    ScoutAct,
    /// <summary>A cheap triager classifies/routes incoming work to the
    /// right handler.</summary>
    TriageRouter,
    /// <summary>Serial root-cause hunt: reproduce → locate → fix → verify
    /// (non-delegable).</summary>
    RootCauseDebugging,
    /// <summary>Full pipeline for large, unfamiliar, high-stakes work:
    /// scout → plan → implement → review.</summary>
    Pipeline,
}
