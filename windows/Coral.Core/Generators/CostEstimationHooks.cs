using System.Globalization;
using Coral.Core.Models;
using Coral.Core.Services;

namespace Coral.Core.Generators;

/// <summary>
/// Port of <c>StrategyForge/Generators/CostEstimationHooks.swift</c>. Approximate,
/// relative cost estimation per strategy. This is NOT a billing figure — it's a
/// rough "how expensive is this shape" signal so a user can compare strategies
/// (Solo is cheap, a big Domain team is pricey) and see which model drives the
/// cost. Pure and testable; no network.
/// </summary>
public readonly struct StrategyCost
{
    /// <summary>Approximate USD for one task/run.</summary>
    public double PerRun { get; }
    /// <summary>Approximate total tokens (input + output) for one run — the
    /// headline figure.</summary>
    public int PerRunTokens { get; }
    /// <summary>Contribution per model (USD), for the breakdown — keyed by the
    /// resolved model id string so cross-provider teams (GPT/Gemini roles) appear
    /// alongside Claude ones instead of being collapsed onto a Claude enum.</summary>
    public IReadOnlyDictionary<string, double> ByModel { get; }

    public StrategyCost(double perRun, int perRunTokens, IReadOnlyDictionary<string, double> byModel)
    {
        PerRun = perRun;
        PerRunTokens = perRunTokens;
        ByModel = byModel;
    }

    public enum Tier { Low, Medium, High }

    /// <summary>Relative bucket for a quick glance.</summary>
    public Tier CostTier => PerRun switch
    {
        < 2 => Tier.Low,
        < 5 => Tier.Medium,
        _ => Tier.High,
    };

    /// <summary>Compact token count, e.g. "1.2M" or "135k".</summary>
    public string TokensShort
    {
        get
        {
            if (PerRunTokens >= 1_000_000) return $"{(PerRunTokens / 1_000_000.0).ToString("F1", CultureInfo.InvariantCulture)}M";
            if (PerRunTokens >= 1_000) return $"{PerRunTokens / 1_000}k";
            return PerRunTokens.ToString();
        }
    }

    public string UsdShort => $"${PerRun.ToString("F2", CultureInfo.InvariantCulture)}";

    /// <summary>The headline cost, tokens first with the dollar figure in parentheses.</summary>
    public string Headline => $"~{TokensShort} ({UsdShort})";
}

/// <summary>How thorough Claude works on a request. Per the model-vs-effort model,
/// effort scales token usage (higher effort ≈ more reading/verifying/thinking).
/// This is used ONLY to make the cost estimate more realistic — it is never
/// written to any generated file. Medium is the baseline (×1) so estimates are
/// stable by default.</summary>
public enum CostEffort { Low, Medium, High }

public static class CostEffortExtensions
{
    /// <summary>The value passed to Claude Code's <c>--effort</c> flag. The three
    /// estimate levels map 1:1 onto the CLI's lower three (low|medium|high); the
    /// CLI also accepts xhigh|max, which the loop editor doesn't expose.</summary>
    public static string CliValue(this CostEffort effort) => effort switch
    {
        CostEffort.Low => "low",
        CostEffort.Medium => "medium",
        CostEffort.High => "high",
        _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, null),
    };

    /// <summary>Token multiplier applied to each role's workload.</summary>
    public static double Multiplier(this CostEffort effort) => effort switch
    {
        CostEffort.Low => 0.4,
        CostEffort.Medium => 1.0,
        CostEffort.High => 2.8,
        _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, null),
    };

    public static string LabelKey(this CostEffort effort) => $"effort.{effort.CliValue()}";
}

public static class CostEstimator
{
    /// <summary>Rough tokens a role consumes/produces on one medium task. Workers
    /// do the heavy lifting; advisory roles read a lot and write little.
    ///
    /// The orchestrator's own load is dominated by *what it decides not to do
    /// itself*. With a team to delegate to, it mostly plans, briefs and reviews —
    /// a light load. With NO subagents (a solo config), it reads, implements and
    /// verifies everything itself, so it carries a full worker load. Pricing the
    /// solo lead as if it delegated understates its cost — the whole point of the
    /// delegation economics is that the lead's token bill tracks how much it hands
    /// off, not just its per-token price.</summary>
    private readonly record struct Workload(double Input, double Output);

    private static Workload GetWorkload(bool isOrchestrator, RoleKind role, bool canDelegate)
    {
        if (isOrchestrator)
        {
            return canDelegate
                ? new Workload(40_000, 15_000)   // delegates -> light
                : new Workload(90_000, 45_000);  // solo -> does the work itself
        }
        return role switch
        {
            RoleKind.Advisor or RoleKind.Reviewer or RoleKind.Researcher => new Workload(60_000, 20_000),
            _ => new Workload(90_000, 45_000), // worker, planner, specialist
        };
    }

    /// <summary>The model id the role actually runs (a non-Claude role carries it
    /// in ProviderModelId).</summary>
    private static string ModelId(AgentRole role)
    {
        if (role.Provider == AIProvider.Claude) return role.Model.ToRawValue();
        if (role.ProviderModelId != null) return role.ProviderModelId;
        var models = ModelCatalog.Models(role.Provider);
        return models.Count > 0 ? models[0].Id : "";
    }

    /// <summary>Estimate the cost of running <paramref name="strategy"/> once at
    /// the baseline (medium) effort.</summary>
    public static StrategyCost Estimate(Strategy strategy) => Estimate(strategy, CostEffort.Medium);

    /// <summary>Estimate the cost of running <paramref name="strategy"/> once at a
    /// given effort level. Effort scales every role's token workload; it does not
    /// change model pricing.</summary>
    public static StrategyCost Estimate(Strategy strategy, CostEffort effort)
    {
        var total = 0.0;
        var tokens = 0.0;
        var byModel = new Dictionary<string, double>();
        var m = effort.Multiplier();
        // Whether the orchestrator has anyone to delegate to. Drives how much of
        // the work the lead does itself (see GetWorkload).
        var canDelegate = strategy.Roles.Any(r => !r.IsOrchestrator);

        foreach (var role in strategy.Roles)
        {
            // Resolve the model id the role actually runs. We don't ship rate
            // cards for GPT/Gemini, so PRICE such a role at its configured Claude
            // tier as a documented proxy — but ATTRIBUTE the cost to the real
            // model id so the breakdown shows "gpt-5", not a Claude model.
            var modelId = ModelId(role);
            var displayId = modelId.Length == 0 ? role.Model.ToRawValue() : modelId;
            var priceKey = Constants.Pricing.ContainsKey(displayId) ? displayId : role.Model.ToRawValue();
            if (!Constants.Pricing.TryGetValue(priceKey, out var price)) continue;
            var load = GetWorkload(role.IsOrchestrator, role.Role, canDelegate);
            var count = (double)Math.Max(role.Count, 1);

            // Input dominates an agent's bill (~100:1 read:write), and much of it
            // is a repeated prefix served from cache — so price the cached share
            // at the cache-read rate and only the rest at the fresh-input rate.
            var inputTokens = load.Input * m;
            var cachedIn = inputTokens * Constants.CostModel.CachedInputFraction;
            var freshIn = inputTokens - cachedIn;
            var inputCost = (freshIn * price.InputPerM
                              + cachedIn * price.InputPerM * Constants.CostModel.CacheReadMultiplier) / 1_000_000;
            var outputCost = load.Output * m / 1_000_000 * price.OutputPerM;
            // Tokenizer overhead lifts the effective spend above the sticker rate.
            var cost = count * (inputCost + outputCost) * Constants.CostModel.TokenizerOverhead;

            total += cost;
            tokens += count * (load.Input + load.Output) * m;
            byModel[displayId] = byModel.GetValueOrDefault(displayId) + cost;
        }

        return new StrategyCost(total, (int)tokens, byModel);
    }
}
