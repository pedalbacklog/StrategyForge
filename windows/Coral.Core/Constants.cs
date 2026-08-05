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

    // MARK: - Cost estimation (approximate)

    /// <summary>Approximate token pricing per model, USD per 1M tokens. These are
    /// estimates for a *relative* comparison between strategies, not a billing
    /// figure — keep them here so they're trivial to update as prices change.</summary>
    public readonly record struct ModelPrice(double InputPerM, double OutputPerM);

    public static readonly IReadOnlyDictionary<string, ModelPrice> Pricing = new Dictionary<string, ModelPrice>
    {
        ["claude-opus-5"] = new ModelPrice(15, 75),
        ["claude-opus-4-8"] = new ModelPrice(15, 75), // legacy, still priced
        ["claude-fable-5"] = new ModelPrice(10, 50),
        ["claude-sonnet-5"] = new ModelPrice(3, 15),
        ["claude-haiku-4-5"] = new ModelPrice(1, 5),
    };

    /// <summary>Adjustments that make the estimate reflect *effective* spend rather
    /// than the sticker rate. These are deliberate, tunable assumptions (not
    /// billing facts): the estimate is for comparing shapes, so what matters is
    /// that they're applied consistently. See <c>CostEstimator</c>.</summary>
    public static class CostModel
    {
        /// <summary>Anthropic's newer tokenizer emits ~30% more tokens for the same
        /// text, so the effective bill sits above the sticker rate. Applied to
        /// every model's dollar figure (a flat factor never changes the *relative*
        /// ordering).</summary>
        public const double TokenizerOverhead = 1.3;

        /// <summary>Agents re-read a large, stable prefix (system prompt,
        /// CLAUDE.md, prior turns) every call, so a good share of input tokens are
        /// cache *reads*, not fresh input. Rough fraction of input priced at the
        /// cache-read rate.</summary>
        public const double CachedInputFraction = 0.5;

        /// <summary>Cache reads cost about a tenth of fresh input at both labs
        /// (~90% off).</summary>
        public const double CacheReadMultiplier = 0.1;
    }
}
