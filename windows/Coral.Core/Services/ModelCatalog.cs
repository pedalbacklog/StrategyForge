using System.Text.Json;
using System.Text.Json.Serialization;
using Coral.Core.Models;

namespace Coral.Core.Services;

/// <summary>
/// Port of <c>StrategyForge/Services/ModelCatalog.swift</c>. Providers rotate their
/// model line-ups faster than app releases ship, so Coral fetches a small JSON
/// catalog from the repo root at launch and merges it over compiled-in defaults —
/// the compiled-in list is the always-works fallback (offline, or before the first
/// fetch). Best-effort and non-fatal by design: a bad network or a malformed file
/// must never leave a picker empty.
/// </summary>
public static class ModelCatalog
{
    /// <summary>The repo-hosted catalog (raw GitHub, public repo — no auth needed).</summary>
    public static readonly Uri RemoteUri =
        new("https://raw.githubusercontent.com/marcosnovo/StrategyForge/main/models.json");

    private static readonly object Lock = new();
    private static Dictionary<string, IReadOnlyList<ProviderModel>> _overrides = new();

    /// <summary>The effective model list for a provider: the fetched override when
    /// present, else the compiled-in default. Never empty.</summary>
    public static IReadOnlyList<ProviderModel> Models(AIProvider provider)
    {
        lock (Lock)
        {
            if (_overrides.TryGetValue(provider.ToKey(), out var over) && over.Count > 0)
                return over;
        }
        return BuiltIn(provider);
    }

    /// <summary>Compiled-in defaults — mirrors <c>AIProvider.builtInModels</c> /
    /// <c>Constants.models</c> in the macOS app. Catalog current as of Aug 2026;
    /// refresh alongside the Swift copy when it changes.</summary>
    public static IReadOnlyList<ProviderModel> BuiltIn(AIProvider provider) => provider switch
    {
        AIProvider.Claude => new[]
        {
            new ProviderModel("claude-opus-5", "Opus 5", "model.tier.expert"),
            new ProviderModel("claude-fable-5", "Fable 5", "model.tier.specialist"),
            new ProviderModel("claude-sonnet-5", "Sonnet 5", "model.tier.generalist"),
            new ProviderModel("claude-haiku-4-5", "Haiku 4.5", "model.tier.fast"),
            new ProviderModel("claude-opus-4-8", "Opus 4.8", "model.tier.expert"), // legacy, still priced
        },
        AIProvider.Openai => new[]
        {
            new ProviderModel("gpt-5.6-terra", "GPT-5.6 Terra", "model.tier.expert"),
            new ProviderModel("gpt-5.5", "GPT-5.5", "model.tier.expert"),
            new ProviderModel("gpt-5.6-luna", "GPT-5.6 Luna", "model.tier.generalist"),
            new ProviderModel("gpt-5", "GPT-5", "model.tier.generalist"),
            new ProviderModel("gpt-5-mini", "GPT-5 mini", "model.tier.fast"),
        },
        AIProvider.Gemini => new[]
        {
            new ProviderModel("gemini-3.1-pro", "Gemini 3.1 Pro", "model.tier.expert"),
            new ProviderModel("gemini-3.6-flash", "Gemini 3.6 Flash", "model.tier.generalist"),
            new ProviderModel("gemini-3.5-flash", "Gemini 3.5 Flash", "model.tier.generalist"),
            new ProviderModel("gemini-2.5-pro", "Gemini 2.5 Pro", "model.tier.expert"),
            new ProviderModel("gemini-2.5-flash", "Gemini 2.5 Flash", "model.tier.fast"),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    private sealed class CatalogDocument
    {
        [JsonPropertyName("providers")]
        public Dictionary<string, List<ProviderModel>>? Providers { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Parse raw models.json bytes into a providers map, keeping only known
    /// provider keys with a non-empty list. Pure — no I/O — so it can be unit-tested
    /// directly against the real repo-root file. Returns an empty map on any parse
    /// failure (unknown keys, malformed JSON, wrong shape).</summary>
    public static Dictionary<string, IReadOnlyList<ProviderModel>> Parse(ReadOnlySpan<byte> json)
    {
        var result = new Dictionary<string, IReadOnlyList<ProviderModel>>();

        CatalogDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<CatalogDocument>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return result;
        }
        if (doc?.Providers is null) return result;

        foreach (var (key, list) in doc.Providers)
        {
            if (AIProviderExtensions.FromKey(key) is null) continue; // unknown provider key
            if (list is { Count: > 0 }) result[key] = list;
        }
        return result;
    }

    /// <summary>Swap the in-memory overrides. Kept separate from <see cref="RefreshAsync"/>
    /// so the lock is never held across an await.</summary>
    private static void Apply(Dictionary<string, IReadOnlyList<ProviderModel>> next)
    {
        lock (Lock) { _overrides = next; }
    }

    /// <summary>Fetch <see cref="RemoteUri"/> and merge it over the built-ins. Silently
    /// keeps the built-ins on any failure (network, non-2xx, malformed JSON) — a picker
    /// must never end up empty because of a transient fetch problem.</summary>
    public static async Task RefreshAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        try
        {
            bytes = await client.GetByteArrayAsync(RemoteUri, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        var next = Parse(bytes);
        if (next.Count == 0) return;
        Apply(next);
    }
}
