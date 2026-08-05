using Coral.Core;
using Coral.Core.Models;

namespace Coral.Tests;

/// <summary>
/// Minimal, faithful stand-ins for the specific <c>StrategyLibrary.swift</c>
/// templates the ported generator/validation tests need (field values — model,
/// counts, tools — matched against the Swift source; prose shortened since no
/// test asserts on exact wording). The full 13-template library isn't ported to
/// Coral.Core yet — see windows/PORT-PLAN.md Fase 2.
/// </summary>
internal static class TestStrategies
{
    private static AgentRole Orchestrator(string name, ClaudeModel model, string description, string systemPrompt) =>
        new(name: name, role: RoleKind.Orchestrator, model: model, systemPrompt: systemPrompt,
            description: description, tools: new List<string>(), count: 1, isOrchestrator: true);

    /// <summary>Mirrors StrategyLibrary.orchestratorWorkers(): 1 orchestrator (Fable 5) + a
    /// "worker" role (Sonnet 5, count 3, inherits all tools).</summary>
    public static Strategy OrchestratorWorkers() => new(
        name: "Orchestrator + Workers (Fan-out)",
        description: "One orchestrator plans and splits the task into parallel slices; "
            + "N identical workers each take a slice and report back.",
        roles: new List<AgentRole>
        {
            Orchestrator("orchestrator", ClaudeModel.Fable5,
                "Main session. Plans the work, splits it into independent slices, and delegates each to a worker.",
                "You are the orchestrator of a fan-out team. Delegate slices to worker subagents."),
            new AgentRole(name: "worker", role: RoleKind.Worker, model: ClaudeModel.Sonnet5,
                systemPrompt: "You are a worker. You receive one self-contained slice of a larger task "
                    + "from the orchestrator. Implement exactly that slice.",
                description: "Use to implement one independent slice of a parallelizable task.",
                tools: new List<string>(), count: 3, isOrchestrator: false),
        },
        orchestrationNotes: "The orchestrator decomposes the task and delegates independent slices to "
            + "worker subagents. Single level of delegation: workers do NOT talk to each other.");

    /// <summary>Mirrors StrategyLibrary.executorAdvisor(): executor (Sonnet 5) + a read-only advisor.</summary>
    public static Strategy ExecutorAdvisor() => new(
        name: "Executor + Advisor",
        description: "The executor does the work every turn and consults a read-only advisor.",
        roles: new List<AgentRole>
        {
            Orchestrator("executor", ClaudeModel.Sonnet5,
                "Main session. Implements the work each turn and consults the advisor when stuck.",
                "You are the executor and the main session. You do the actual work each turn."),
            new AgentRole(name: "advisor", role: RoleKind.Advisor, model: ClaudeModel.Sonnet5,
                systemPrompt: "You are a high-level advisor consulted on demand. You do NOT write or edit code.",
                description: "Use on demand for high-level design advice. Advice only.",
                tools: new List<string>(Constants.ReadOnlyTools), count: 1, isOrchestrator: false),
        },
        orchestrationNotes: "The executor does the work directly and consults the advisor for high-level "
            + "guidance on demand.");

    /// <summary>Mirrors StrategyLibrary.researchFanout(): orchestrator (Fable 5) + a read-only
    /// "researcher" role (Haiku 4.5, count 3).</summary>
    public static Strategy ResearchFanout() => new(
        name: "Research Fan-out",
        description: "The orchestrator dispatches N read-only researchers to explore the codebase in parallel.",
        roles: new List<AgentRole>
        {
            Orchestrator("orchestrator", ClaudeModel.Fable5,
                "Main session. Splits a research question into areas and dispatches a researcher per area.",
                "You are the orchestrator of a research fan-out."),
            new AgentRole(name: "researcher", role: RoleKind.Researcher, model: ClaudeModel.Haiku45,
                systemPrompt: "You are a read-only researcher. You are given one area to explore.",
                description: "Use to explore one area of the codebase and return a summary. Read-only.",
                tools: new List<string>(Constants.ReadOnlyTools), count: 3, isOrchestrator: false),
        },
        orchestrationNotes: "The orchestrator partitions a research question into areas and dispatches a "
            + "read-only researcher per area in parallel, then synthesizes.");

    /// <summary>Mirrors StrategyLibrary.plannerImplementersReviewer(): orchestrator (Fable 5) +
    /// 2 implementers (Sonnet 5) + a reviewer (Haiku 4.5).</summary>
    public static Strategy PlannerImplementersReviewer() => new(
        name: "Planner → Implementers → Reviewer",
        description: "The orchestrator plans, delegates to N implementers, then invokes a read-only reviewer.",
        roles: new List<AgentRole>
        {
            Orchestrator("orchestrator", ClaudeModel.Fable5,
                "Main session. Plans the work, delegates to implementers, then routes to the reviewer.",
                "You are the orchestrator. Produce a plan, delegate units, then route to review."),
            new AgentRole(name: "implementer", role: RoleKind.Worker, model: ClaudeModel.Sonnet5,
                systemPrompt: "You are an implementer. You receive one unit of a plan and implement it fully.",
                description: "Use to implement one planned unit of work.",
                tools: new List<string>(), count: 2, isOrchestrator: false),
            new AgentRole(name: "reviewer", role: RoleKind.Reviewer, model: ClaudeModel.Haiku45,
                systemPrompt: "You are a read-only reviewer. You review the implementers' changes.",
                description: "Use to review the implementation for bugs and regressions.",
                tools: new List<string>(Constants.ReadOnlyTools), count: 1, isOrchestrator: false),
        },
        orchestrationNotes: "The orchestrator plans, delegates implementation to implementers, then routes "
            + "the result to the reviewer before verifying.");

    /// <summary>Mirrors StrategyLibrary.debateConsensus(): moderator (Opus 5) + 3 debaters (Sonnet 5).</summary>
    public static Strategy DebateConsensus() => new(
        name: "Debate / Consensus (mediated)",
        description: "A moderator collects arguments from N agents holding different positions.",
        roles: new List<AgentRole>
        {
            Orchestrator("moderator", ClaudeModel.Opus5,
                "Main session. Poses the question to each debater and synthesizes a consensus.",
                "You are the moderator of a mediated debate."),
            new AgentRole(name: "debater", role: RoleKind.Advisor, model: ClaudeModel.Sonnet5,
                systemPrompt: "You are a debater. Argue your assigned position with clear reasoning.",
                description: "Use to argue a distinct position on a decision. Advice only.",
                tools: new List<string>(Constants.ReadOnlyTools), count: 3, isOrchestrator: false),
        },
        orchestrationNotes: "The moderator is the ONLY channel between debaters — they never talk to each "
            + "other directly.");

    /// <summary>Mirrors StrategyLibrary.solo(): a single orchestrator (Opus 5), no subagents.</summary>
    public static Strategy Solo() => new(
        name: "Solo (baseline)",
        description: "A single agent with no subagents — a baseline to compare cost and quality against.",
        roles: new List<AgentRole>
        {
            Orchestrator("solo", ClaudeModel.Opus5,
                "Main session. Does all the work directly with no subagents.",
                "You are working solo. There are no subagents to delegate to."),
        },
        orchestrationNotes: "Baseline: a single agent does everything directly. No subagents are generated.");
}
