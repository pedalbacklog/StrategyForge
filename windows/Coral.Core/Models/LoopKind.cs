namespace Coral.Core.Models;

/// <summary>
/// The recurrence shape a task implies — turn-based (one-shot chat, the
/// default), goal-based (repeat until a verifiable finish line), time-based
/// (on a schedule), or proactive (triggered by an external event like a
/// webhook). Used ONLY as a label on <see cref="AdvisorEngine"/>'s
/// recommendation in this port — a standalone value type with the four
/// case names <c>AdvisorEngine.swift</c> references (<c>.turnBased</c>/
/// <c>.goalBased</c>/<c>.timeBased</c>/<c>.proactive</c>), NOT derived from
/// or dependent on <c>Models/LoopPlan.swift</c>, which CLAUDE.md marks
/// off-limits for autonomous changes (loop scheduling/generation needs
/// human review of the diff, not just green tests — see windows/PORT-PLAN.md
/// §6/§9). Porting the actual Loops feature (Fase 8: turn/goal/time/proactive
/// scheduling, `LoopScheduler`, `LoopRunner`, `LoopFileGenerator`) is
/// separate, out-of-scope work this type does not touch or unlock.
/// </summary>
public enum LoopKind
{
    TurnBased,
    GoalBased,
    TimeBased,
    Proactive,
}
