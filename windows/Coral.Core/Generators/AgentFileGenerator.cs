using System.Text;
using Coral.Core.Models;

namespace Coral.Core.Generators;

/// <summary>
/// Port of <c>StrategyForge/Generators/AgentFileGenerator.swift</c>. Pure,
/// testable generation of Claude Code subagent files
/// (<c>.claude/agents/&lt;name&gt;.md</c>) from a Strategy. No disk access — the
/// orchestrator is skipped entirely, since its model is a launch setting.
/// </summary>
public static class AgentFileGenerator
{
    /// <summary>Directory (relative to repo root) where subagent files live.</summary>
    public const string AgentsDirectory = ".claude/agents";

    /// <summary>Frontmatter marker identifying an agent file Coral generated, so a
    /// re-write can safely prune ones written for now-removed/renamed roles WITHOUT
    /// ever touching hand-written agent files.</summary>
    public const string ManagedSignature = "coral: managed";
    /// <summary>Legacy signature from the app's former name — still recognized when
    /// pruning so agent files written by an earlier version are managed too.</summary>
    public const string LegacyManagedSignature = "strategyforge: managed";

    /// <summary>Generate one <see cref="GeneratedFile"/> per subagent instance,
    /// expanding <c>Count</c>. A role with count 1 produces <c>&lt;name&gt;.md</c>;
    /// count &gt; 1 produces <c>&lt;name&gt;-1.md … &lt;name&gt;-N.md</c>.</summary>
    public static List<GeneratedFile> Generate(Strategy strategy)
    {
        var files = new List<GeneratedFile>();
        foreach (var role in strategy.SubagentRoles)
        {
            // Defense in depth: the (not yet built) editor blocks invalid names via
            // Validate(), but strategies also arrive from imports and sync. A name
            // with "/", ".." or newlines would escape .claude/agents/ or inject
            // frontmatter — never build a path from it.
            if (!Strategy.IsValidRoleName(role.Name)) continue;
            var count = Math.Max(role.Count, 1);
            for (var index = 1; index <= count; index++)
            {
                var instanceName = count == 1 ? role.Name : $"{role.Name}-{index}";
                files.Add(new GeneratedFile($"{AgentsDirectory}/{instanceName}.md", Markdown(role, instanceName)));
            }
        }
        return files;
    }

    /// <summary>Render a single subagent file's contents.</summary>
    public static string Markdown(AgentRole role, string instanceName)
    {
        var frontmatter = new StringBuilder();
        frontmatter.Append("---\n");
        frontmatter.Append($"name: {instanceName}\n");
        frontmatter.Append($"description: {EscapedScalar(role.Description)}\n");

        // Omit `tools` entirely when empty so the subagent inherits all tools.
        // Strip newlines from each entry: a tool name with "\n" (or a raw "\r" —
        // YAML treats CR as a line break too) would break out of the scalar and
        // inject its own frontmatter lines.
        var tools = role.Tools
            .Select(t => t.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim())
            .Where(t => t.Length > 0)
            .ToList();
        if (tools.Count > 0)
        {
            frontmatter.Append($"tools: {string.Join(", ", tools)}\n");
        }
        // Workers pin their model in frontmatter (unlike the orchestrator).
        frontmatter.Append($"model: {role.Model.ToRawValue()}\n");
        frontmatter.Append(ManagedSignature).Append('\n');
        frontmatter.Append("---\n");

        var body = role.SystemPrompt.Trim();
        if (role.MemoryEnabled)
        {
            body += "\n\n" + MemoryInstructions(role.MemoryPath(instanceName));
        }
        return frontmatter.ToString() + "\n" + body + "\n";
    }

    /// <summary>The "## Memory" block appended to a memory-enabled subagent's instructions.</summary>
    public static string MemoryInstructions(string memoryPath) =>
        "## Memory\n" +
        "\n" +
        $"You keep a persistent memory file at `{memoryPath}`. At the START of every task,\n" +
        "read it to recall what earlier runs learned. At the END, append any DURABLE\n" +
        "learnings — decisions made, conventions in this codebase, dead ends to avoid — in\n" +
        "a concise line or two. Don't repeat what's already there, and prune anything now\n" +
        "wrong. This file carries across runs, so keep it short and high-signal.\n" +
        "\n" +
        "Close the loop: whenever a reviewer or verifier REJECTS your work, write the reason\n" +
        "here as a rule to follow next time — so the same mistake never costs twice.";

    /// <summary>Seed memory files for roles with persistent memory ON. Written ONCE
    /// and then owned by the agent — callers must never overwrite an existing one,
    /// so a re-generate never clobbers accumulated notes. One per expanded instance.</summary>
    public static List<GeneratedFile> MemorySeedFiles(Strategy strategy)
    {
        var files = new List<GeneratedFile>();
        foreach (var role in strategy.SubagentRoles.Where(r => r.MemoryEnabled))
        {
            if (!Strategy.IsValidRoleName(role.Name)) continue;
            var count = Math.Max(role.Count, 1);
            for (var index = 1; index <= count; index++)
            {
                var instanceName = count == 1 ? role.Name : $"{role.Name}-{index}";
                files.Add(new GeneratedFile(role.MemoryPath(instanceName), MemorySeed(instanceName)));
            }
        }
        return files;
    }

    private static string MemorySeed(string instanceName) =>
        $"# Memory — {instanceName}\n" +
        "\n" +
        "Durable notes this agent keeps across runs. It reads this first and appends\n" +
        "concise, lasting learnings last (decisions, conventions found, dead ends). Keep it\n" +
        "short and high-signal; prune anything stale.";

    /// <summary>YAML-safe rendering of a single-line scalar. <c>description</c> can
    /// contain colons and other YAML-significant characters, so quote it when needed.</summary>
    private static string EscapedScalar(string value)
    {
        // \r\n and bare \r count as line breaks in YAML — flatten them all, or an
        // imported description with raw CRs injects its own frontmatter lines.
        var flat = value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        string[] reservedPrefixes = { "'", "\"", ">", "|", "-", "[", "{", "*", "&", "!", "%", "@", "`", "?", "~" };
        var needsQuoting = flat.Contains(':') || flat.Contains('#')
            || reservedPrefixes.Any(p => flat.StartsWith(p, StringComparison.Ordinal));
        if (!needsQuoting) return flat;
        var escaped = flat.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }
}
