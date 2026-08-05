namespace Coral.Core;

/// <summary>
/// Port of the parts of <c>StrategyForge/Constants.swift</c> that Coral.Core's
/// generators and validation need today. Extend as more of Constants.swift's
/// scope (model catalog, cost pricing) gets ported.
/// </summary>
public static class Constants
{
    /// <summary>The tools a Claude Code subagent can be granted in its <c>tools</c>
    /// frontmatter. If a subagent omits <c>tools</c>, it inherits all of them.
    /// <c>Agent</c> is listed but should generally NOT be granted to subagents:
    /// Claude Code allows only a single level of delegation.</summary>
    public static readonly IReadOnlyList<string> AvailableTools = new[]
    {
        "Read", "Write", "Edit", "Grep", "Glob", "Bash", "WebFetch", "WebSearch", "Agent",
    };

    /// <summary>Tools that only read — the safe default set for reviewers, advisors
    /// and researchers, which must not modify the repository.</summary>
    public static readonly IReadOnlyList<string> ReadOnlyTools = new[] { "Read", "Grep", "Glob" };
}
