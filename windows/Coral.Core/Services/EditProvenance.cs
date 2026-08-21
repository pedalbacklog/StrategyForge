using Coral.Core.Models;

namespace Coral.Core.Services;

/// <summary>
/// Port of <c>StrategyForge/Services/EditProvenance.swift</c> — who wrote a
/// change. Coral's whole point is putting different models/providers on
/// different roles in one team; this surfaces the payoff in a diff: each
/// changed file (and, via <see cref="LineAttributor"/>, each line) is tagged
/// with the agent that authored it, so a mixed-provider run is auditable at
/// a glance instead of an anonymous pile of edits.
/// </summary>
public sealed record EditProvenance(string? Agent, string Model, AIProvider Provider)
{
    /// <summary>Compact "Agent · Model" label; falls back to the
    /// orchestrator's name when there's no subagent, and drops the model
    /// when it's unknown.</summary>
    public string Label(string orchestratorName)
    {
        var who = string.IsNullOrEmpty(Agent) ? orchestratorName : Agent;
        return Model.Length == 0 ? who : $"{who} · {Model}";
    }
}

/// <summary>Resolves an edit (an active-subagent name at edit time) to the
/// responsible team member by matching it against the strategy's roles —
/// pure, so it's unit-tested directly.</summary>
public static class EditProvenanceResolver
{
    /// <summary>Attribute an edit made while <paramref name="subagent"/> was
    /// active to a concrete team member.
    /// <list type="bullet">
    /// <item>No active subagent -&gt; the orchestrator (with its configured model/provider).</item>
    /// <item>A subagent that matches a role -&gt; that role's pinned model/provider.</item>
    /// <item>A subagent with no matching role -&gt; credited by name, model unknown (best-effort).</item>
    /// </list></summary>
    public static EditProvenance Attribute(string? subagent, Strategy strategy)
    {
        var sub = subagent?.Trim();
        if (string.IsNullOrEmpty(sub))
        {
            return strategy.Orchestrator is { } orch
                ? new EditProvenance(null, orch.ModelDisplayName, orch.Provider)
                : new EditProvenance(null, "", AIProvider.Claude);
        }
        var match = strategy.SubagentRoles.FirstOrDefault(r => AgentNameMatcher.TitlesMatch(r.Name, sub))
            ?? strategy.SubagentRoles.FirstOrDefault(r => string.Equals(r.Name, sub, StringComparison.OrdinalIgnoreCase));
        return match is not null
            ? new EditProvenance(match.Name, match.ModelDisplayName, match.Provider)
            : new EditProvenance(sub, "", AIProvider.Claude);
    }
}

/// <summary>Per-line authorship via a snapshot diff: given a file's previous
/// lines (with their authors) and its new lines after an edit, credit the
/// lines this edit changed to <c>author</c> while carrying unchanged lines'
/// authorship forward. Pure LCS alignment, so it's unit-tested directly. The
/// first time a file is seen there's no prior snapshot, so every line is
/// credited to the first editor; later edits refine it line by line.</summary>
public static class LineAttributor
{
    /// <summary>A quadratic-LCS guard: past this many line-pairs we skip
    /// alignment and credit the whole new file to <c>author</c> (coarse but
    /// bounded — huge generated files won't stall the run).</summary>
    public const int MaxPairs = 400_000;

    public static IReadOnlyList<EditProvenance?> Attribute(
        IReadOnlyList<string> oldLines, IReadOnlyList<EditProvenance?> oldAuthors,
        IReadOnlyList<string> newLines, EditProvenance author)
    {
        var n = oldLines.Count;
        var m = newLines.Count;
        if (m == 0) return Array.Empty<EditProvenance?>();
        // No prior version, or a diff too large to align: credit everything to this edit.
        if (n == 0 || (long)n * m > MaxPairs) return Enumerable.Repeat(author, m).ToList();

        // dp[i][j] = LCS length of old[i...] and new[j...].
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                dp[i, j] = oldLines[i] == newLines[j]
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        // Backtrack: matched lines keep their prior author; inserted lines get `author`.
        var result = new EditProvenance?[m];
        for (var k = 0; k < m; k++) result[k] = author;
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (oldLines[x] == newLines[y])
            {
                result[y] = x < oldAuthors.Count ? oldAuthors[x] : null;
                x++; y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                x++; // line removed from old
            }
            else
            {
                result[y] = author; y++; // line inserted in new
            }
        }
        // Any remaining new lines (y..<m) are trailing inserts — already `author`.
        return result;
    }
}
