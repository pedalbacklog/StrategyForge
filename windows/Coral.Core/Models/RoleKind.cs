namespace Coral.Core.Models;

/// <summary>
/// Port of <c>StrategyForge/Models/AgentRole.swift</c>'s <c>RoleKind</c>. The kind
/// of role a subagent plays — drives sensible defaults (e.g. read-only tools for
/// reviewers/advisors/researchers). The macOS app's per-kind icon/tint are UI
/// concerns for the (not-yet-built) WinUI views, not ported here.
/// </summary>
public enum RoleKind
{
    Orchestrator,
    Worker,
    Advisor,
    Reviewer,
    Planner,
    Researcher,
    Specialist,
}

public static class RoleKindExtensions
{
    /// <summary>Short label for the role badge.</summary>
    public static string DisplayName(this RoleKind kind) => kind switch
    {
        RoleKind.Orchestrator => "Orchestrator",
        RoleKind.Worker => "Worker",
        RoleKind.Advisor => "Advisor",
        RoleKind.Reviewer => "Reviewer",
        RoleKind.Planner => "Planner",
        RoleKind.Researcher => "Researcher",
        RoleKind.Specialist => "Specialist",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Roles that must not modify the repository default to read-only tools.</summary>
    public static bool IsReadOnlyByDefault(this RoleKind kind) => kind switch
    {
        RoleKind.Advisor or RoleKind.Reviewer or RoleKind.Researcher => true,
        _ => false,
    };
}
