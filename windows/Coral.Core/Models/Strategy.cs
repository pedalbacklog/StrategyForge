using Coral.Core;

namespace Coral.Core.Models;

/// <summary>
/// Port of <c>StrategyForge/Models/Strategy.swift</c>. A multi-agent topology,
/// fully editable (model and count per role).
///
/// <c>EvalSuite</c>/<c>ToolChecks</c> (Swift's optional eval suite and deterministic
/// tool checks) and <c>AutoFixed()</c> are not ported yet — nothing in Coral.Core
/// references them today; add them alongside whatever UI/feature needs them.
/// </summary>
public sealed class Strategy
{
    public Guid Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public List<AgentRole> Roles { get; set; }
    /// <summary>Prose that goes into CLAUDE.md explaining how the orchestrator
    /// should delegate and the limitations (single level of delegation, no lateral
    /// worker-to-worker communication).</summary>
    public string OrchestrationNotes { get; set; }
    /// <summary>External MCP tool servers this strategy makes available.</summary>
    public List<McpServer> McpServers { get; set; }
    /// <summary>Attached skill slugs (folders under .claude/skills).</summary>
    public List<string> Skills { get; set; }

    public Strategy(
        string name,
        string description,
        List<AgentRole> roles,
        string orchestrationNotes,
        List<McpServer>? mcpServers = null,
        List<string>? skills = null,
        Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Name = name;
        Description = description;
        Roles = roles;
        OrchestrationNotes = orchestrationNotes;
        McpServers = mcpServers ?? new List<McpServer>();
        Skills = skills ?? new List<string>();
    }

    /// <summary>The single orchestrator role, if the strategy is well-formed.</summary>
    public AgentRole? Orchestrator => Roles.FirstOrDefault(r => r.IsOrchestrator);

    /// <summary>All non-orchestrator roles — the ones that become subagent files.</summary>
    public List<AgentRole> SubagentRoles => Roles.Where(r => !r.IsOrchestrator).ToList();

    // MARK: - Validation

    public enum Severity { Error, Warning }

    /// <summary>A single validation problem, surfaced inline in the (not yet built) editor.</summary>
    public sealed record ValidationIssue(Severity Severity, string Message, Guid? RoleId = null);

    /// <summary>All validation issues for the current state. No <see cref="Errors"/>
    /// means the strategy can be generated safely.</summary>
    public List<ValidationIssue> Validate()
    {
        var issues = new List<ValidationIssue>();

        // Exactly one orchestrator.
        var orchestrators = Roles.Where(r => r.IsOrchestrator).ToList();
        if (orchestrators.Count == 0)
        {
            issues.Add(new ValidationIssue(Severity.Error,
                "A strategy must have exactly one orchestrator; none is marked."));
        }
        else if (orchestrators.Count > 1)
        {
            issues.Add(new ValidationIssue(Severity.Error,
                $"A strategy must have exactly one orchestrator; {orchestrators.Count} are marked."));
        }

        // The orchestrator must have a count of 1 — its model is a launch setting,
        // never frontmatter, so a count other than 1 slipped in is a bug to flag.
        foreach (var orch in orchestrators.Where(o => o.Count != 1))
        {
            issues.Add(new ValidationIssue(Severity.Error,
                "The orchestrator must have a count of 1.", orch.Id));
        }

        // Unique, non-empty slug names. Names become file paths under
        // .claude/agents/ and YAML `name:` scalars, so anything beyond
        // [A-Za-z0-9_-] must be blocked — an imported strategy could otherwise
        // write outside the repo or inject frontmatter.
        var seen = new Dictionary<string, int>();
        foreach (var role in Roles)
        {
            var trimmed = role.Name.Trim();
            if (trimmed.Length == 0)
            {
                issues.Add(new ValidationIssue(Severity.Error, "A role has an empty name.", role.Id));
            }
            else if (!IsValidRoleName(role.Name))
            {
                issues.Add(new ValidationIssue(Severity.Error,
                    $"Role name “{role.Name}” is invalid; use only letters, digits, “-” or “_” (e.g. “code-reviewer”).",
                    role.Id));
            }
            seen[role.Name] = seen.GetValueOrDefault(role.Name) + 1;
        }
        foreach (var name in seen.Where(kv => kv.Value > 1).Select(kv => kv.Key))
        {
            issues.Add(new ValidationIssue(Severity.Error,
                $"Duplicate role name “{name}”. Subagent names must be unique."));
        }

        // Expanded-name collisions: a role "worker" with count 3 writes worker-1…worker-3,
        // and a SEPARATE literal role "worker-1" writes the SAME file — different role
        // names, same generated .md, so one agent is silently lost.
        var expanded = new Dictionary<string, int>();
        foreach (var role in Roles.Where(r => !r.IsOrchestrator))
        {
            var count = Math.Max(role.Count, 1);
            if (count == 1)
            {
                expanded[role.Name] = expanded.GetValueOrDefault(role.Name) + 1;
            }
            else
            {
                for (var i = 1; i <= count; i++)
                {
                    var key = $"{role.Name}-{i}";
                    expanded[key] = expanded.GetValueOrDefault(key) + 1;
                }
            }
        }
        foreach (var name in expanded.Where(kv => kv.Value > 1).Select(kv => kv.Key))
        {
            issues.Add(new ValidationIssue(Severity.Error,
                $"Two subagents both generate “{name}.md” — a numbered role collides with another name. Rename one."));
        }

        // Counts must be positive.
        foreach (var role in Roles.Where(r => r.Count < 1))
        {
            issues.Add(new ValidationIssue(Severity.Error, $"Role “{role.Name}” has a count below 1.", role.Id));
        }

        // Unknown tool names (typically from an import): warn, since Claude Code
        // silently ignores tools it doesn't recognize. Scoped patterns (Bash(…), an
        // mcp__ server tool) are legitimate and not flagged.
        var known = new HashSet<string>(Constants.AvailableTools);
        foreach (var role in Roles)
        {
            var unknown = role.Tools
                .Select(t => t.Trim())
                .Where(name => name.Length > 0 && !known.Contains(name) && !name.Contains('(') && !name.StartsWith("mcp__", StringComparison.Ordinal))
                .ToList();
            if (unknown.Count > 0)
            {
                issues.Add(new ValidationIssue(Severity.Warning,
                    $"“{role.Name}” lists unrecognized tool(s): {string.Join(", ", unknown)}. Claude Code may ignore them.",
                    role.Id));
            }
        }

        // Reviewers/advisors/researchers should not hold write tools.
        var writeTools = new HashSet<string> { "Write", "Edit", "Bash" };
        foreach (var role in Roles.Where(r => r.Role.IsReadOnlyByDefault() && !r.IsOrchestrator))
        {
            var offending = role.Tools.Where(writeTools.Contains).OrderBy(t => t, StringComparer.Ordinal).ToList();
            if (offending.Count > 0)
            {
                issues.Add(new ValidationIssue(Severity.Warning,
                    $"“{role.Name}” is a {role.Role.DisplayName().ToLowerInvariant()} but can {string.Join(", ", offending)}. Consider read-only tools.",
                    role.Id));
            }
        }

        // Workers should not hold the delegation tool — Claude Code allows only a
        // single level of delegation.
        foreach (var role in SubagentRoles.Where(r => r.Tools.Contains("Agent")))
        {
            issues.Add(new ValidationIssue(Severity.Warning,
                $"“{role.Name}” is granted the Agent tool, but subagents cannot delegate further (single level of delegation).",
                role.Id));
        }

        return issues;
    }

    /// <summary>Only the blocking issues.</summary>
    public List<ValidationIssue> Errors => Validate().Where(i => i.Severity == Severity.Error).ToList();

    /// <summary>True when there are no blocking errors.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>Safe slug for a subagent name: used verbatim as a file name and a
    /// YAML scalar, so restricted to ASCII letters, digits, "-" and "_".</summary>
    public static bool IsValidRoleName(string name)
    {
        if (name.Length == 0) return false;
        return name.All(c => c < 128 && (char.IsLetterOrDigit(c) || c == '-' || c == '_'));
    }
}
