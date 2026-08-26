namespace Coral.Core.Services;

/// <summary>
/// Port of <c>StrategyForge/Services/AgentNameMatcher.swift</c>. Loose matching
/// between a diagram node title and a streamed agent name (a <c>subagent_type</c>
/// slug or a free-text description) — the run stream and the diagram both key
/// off it.
/// </summary>
public static class AgentNameMatcher
{
    /// <summary>True when two agent/role names refer to the same teammate after
    /// normalizing to letters+digits and testing substring containment either
    /// way.</summary>
    public static bool TitlesMatch(string a, string b)
    {
        static string Norm(string s) => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var na = Norm(a);
        var nb = Norm(b);
        if (na.Length == 0 || nb.Length == 0) return false;
        return na.Contains(nb) || nb.Contains(na);
    }
}
