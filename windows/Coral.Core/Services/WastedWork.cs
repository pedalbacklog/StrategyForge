using System.Linq;
using Coral.Core.ViewModels;

namespace Coral.Core.Services;

/// <summary>One piece of work that was done more than once across the run.
/// Port of <c>WastedWork.swift</c>'s <c>DuplicatedWork</c>.</summary>
/// <param name="Title">The tool ("Read", "Bash", "WebFetch", …).</param>
/// <param name="Detail">What it acted on (file, command, url, query).</param>
/// <param name="Count">Total times it happened.</param>
/// <param name="Agents">Distinct agents that did it ("orchestrator" for the lead).</param>
public sealed record DuplicatedWork(string Title, string Detail, int Count, IReadOnlyList<string> Agents)
{
    public string Id => Title + "" + Detail;

    /// <summary>Redone across DIFFERENT agents (the collaboration failure), vs
    /// one agent looping.</summary>
    public bool CrossAgent => Agents.Count > 1;
}

/// <summary>
/// "Your two agents are not collaborating — the second is redoing the
/// first's work." Duplicated effort is invisible in the final answer and
/// only shows in the TRAJECTORY: the same tool called with the same target
/// by more than one agent means the team paid for that step twice. This
/// pure detector walks the activity timeline (<see cref="ActivityStep"/>,
/// already produced by every real chat turn) and surfaces those repeats.
/// Port of <c>WastedWork.swift</c> — no UI reads this yet (surfacing it is a
/// product decision, same reasoning as <c>DiagnosticsLog</c>/
/// <c>ClaudeUsageStore</c>), but it operates on data this port's chat
/// feature already produces, not a speculative/unbuilt one.
/// </summary>
public static class WastedWork
{
    /// <summary>Timeline markers that aren't real tool work (delegations,
    /// role heartbeats) — excluded.</summary>
    private static bool IsToolStep(ActivityStep s)
    {
        if (s.IsDelegation) return false;
        if (s.Title.StartsWith("role.")) return false;
        // Needs a concrete target to be a meaningful "same work" key (a bare
        // tool name with no target — e.g. "TodoWrite" — isn't duplicated
        // WORK in a comparable sense).
        return !string.IsNullOrWhiteSpace(s.Detail);
    }

    private static string AgentLabel(ActivityStep s) => string.IsNullOrEmpty(s.Agent) ? "orchestrator" : s.Agent;

    /// <summary>Find tool steps repeated (same tool + same target) across the
    /// timeline. Sorted by the most-repeated first; ties broken by
    /// cross-agent (the worse kind) then title.</summary>
    public static IReadOnlyList<DuplicatedWork> Detect(IEnumerable<ActivityStep> steps)
    {
        var order = new List<string>();
        var byKey = new Dictionary<string, (string Title, string Detail, int Count, List<string> Agents)>();
        foreach (var s in steps)
        {
            if (!IsToolStep(s)) continue;
            var detail = s.Detail!.Trim();
            var key = s.Title + "" + detail;
            if (!byKey.TryGetValue(key, out var v))
            {
                v = (s.Title, detail, 0, new List<string>());
                order.Add(key);
            }
            var agent = AgentLabel(s);
            if (!v.Agents.Contains(agent)) v.Agents.Add(agent);
            byKey[key] = (v.Title, v.Detail, v.Count + 1, v.Agents);
        }

        var result = order
            .Select(key => byKey[key])
            .Where(v => v.Count > 1)
            .Select(v => new DuplicatedWork(v.Title, v.Detail, v.Count, v.Agents))
            .ToList();

        result.Sort((a, b) =>
        {
            if (a.Count != b.Count) return b.Count - a.Count; // higher count first
            if (a.CrossAgent != b.CrossAgent) return a.CrossAgent ? -1 : 1; // cross-agent (worse) first
            return string.CompareOrdinal(a.Title, b.Title);
        });
        return result;
    }

    /// <summary>Total number of REDUNDANT tool calls (occurrences beyond the
    /// first of each duplicated step) — the count of work the team paid for
    /// more than once.</summary>
    public static int RedundantCount(IReadOnlyList<DuplicatedWork> dups) => dups.Sum(d => d.Count - 1);
}
