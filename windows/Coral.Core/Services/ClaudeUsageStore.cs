using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Coral.Core.Services;

/// <summary>Per-model token total for a window. Port of
/// <c>ClaudeUsageStore.swift</c>'s <c>ModelUsage</c>.</summary>
public sealed record ModelUsage(string Model, int Tokens);

/// <summary>Aggregated Claude usage read from local logs. Port of
/// <c>ClaudeUsageStore.swift</c>'s <c>UsageSummary</c>.</summary>
public sealed record UsageSummary(
    DateTimeOffset? BlockStart,
    DateTimeOffset? BlockResetAt,
    int BlockTokens,
    int WeekTokens,
    IReadOnlyList<ModelUsage> WeekByModel,
    DateTimeOffset? LastActivity,
    DateTimeOffset ComputedAt)
{
    public bool HasData => LastActivity is not null;

    public static readonly UsageSummary Empty = new(null, null, 0, 0,
        Array.Empty<ModelUsage>(), null, DateTimeOffset.MinValue);
}

/// <summary>
/// Reads Claude Code's local session logs (<c>~/.claude/projects/**/*.jsonl</c>)
/// and aggregates real token usage into a 5-hour "block" and a rolling 7-day
/// window, per model — ClaudeKarma-style. Port of <c>ClaudeUsageStore.swift</c>.
/// Undocumented format, so parsing is tolerant: unknown/legacy/malformed lines
/// are skipped, never fatal. No UI reads this yet — see <c>UsageStore.swift</c>/
/// <c>UsageView.swift</c> for the (not yet ported) ViewModel/View this would
/// eventually back; ported ahead of the UI the same way this port's other pure
/// logic (Generators, CLIOneShotRunner's command building) was, since it needs
/// no UI to be useful and testable.
/// </summary>
public static class ClaudeUsageStore
{
    /// <summary>The 5-hour rate-limit block length.</summary>
    private static readonly TimeSpan BlockLength = TimeSpan.FromHours(5);

    private sealed record Sample(DateTimeOffset Date, string Model, int Tokens);

    /// <summary>Scan the logs under <paramref name="homeDirectory"/>'s
    /// <c>.claude/projects</c> and aggregate. Pure given its inputs — fully
    /// unit-testable against a temp directory standing in for
    /// <c>%USERPROFILE%</c>, same pattern as <see cref="ProviderAuth"/>'s
    /// <c>FreshnessUncached</c>.</summary>
    public static UsageSummary LoadUncached(string homeDirectory, DateTimeOffset now)
    {
        var root = Path.Combine(homeDirectory, ".claude", "projects");
        if (!Directory.Exists(root)) return UsageSummary.Empty;

        // Only files touched in the last 8 days can hold entries for the 7-day
        // window (plus a margin for the current block), so skip the rest.
        var cutoff = now - TimeSpan.FromDays(8);

        var samples = new List<Sample>();
        foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime) continue;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch
            {
                continue;
            }

            foreach (var line in lines)
            {
                // Cheap pre-filter before JSON work.
                if (!line.Contains("\"assistant\"") || !line.Contains("\"usage\"")) continue;
                var sample = ParseSample(line);
                if (sample is not null) samples.Add(sample);
            }
        }

        if (samples.Count == 0) return UsageSummary.Empty;
        samples.Sort((a, b) => a.Date.CompareTo(b.Date));

        // Current 5-hour block: walk forward, restarting the block whenever an
        // entry lands past the running block's reset. The final anchor is the
        // live block.
        var anchor = FloorToHour(samples[0].Date);
        var blockTokens = 0;
        foreach (var s in samples)
        {
            if (s.Date >= anchor + BlockLength)
            {
                anchor = FloorToHour(s.Date);
                blockTokens = 0;
            }
            blockTokens += s.Tokens;
        }
        var resetAt = anchor + BlockLength;
        // If the block already elapsed (no recent activity), report zero for the live block.
        var liveBlockTokens = now < resetAt ? blockTokens : 0;
        DateTimeOffset? liveBlockStart = now < resetAt ? anchor : null;
        DateTimeOffset? liveResetAt = now < resetAt ? resetAt : null;

        // Rolling 7-day window, per model.
        var weekStart = now - TimeSpan.FromDays(7);
        var byModel = new Dictionary<string, int>();
        var weekTokens = 0;
        foreach (var s in samples)
        {
            if (s.Date < weekStart) continue;
            byModel[s.Model] = byModel.GetValueOrDefault(s.Model) + s.Tokens;
            weekTokens += s.Tokens;
        }
        // Order by capability (most powerful first), NOT by usage — so the
        // strongest model is always on top; tokens only break ties within a tier.
        var models = byModel.Select(kv => new ModelUsage(kv.Key, kv.Value))
            .OrderBy(m => PowerRank(m.Model))
            .ThenByDescending(m => m.Tokens)
            .ToList();

        return new UsageSummary(liveBlockStart, liveResetAt, liveBlockTokens, weekTokens,
            models, samples[^1].Date, now);
    }

    /// <summary>The real summary, reading <c>%USERPROFILE%\.claude\projects</c>.</summary>
    public static UsageSummary Load(DateTimeOffset? now = null) =>
        LoadUncached(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), now ?? DateTimeOffset.Now);

    /// <summary>Capability rank for display ordering — lower = more powerful
    /// (shown higher).</summary>
    public static int PowerRank(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains("opus") || m.Contains("fable")) return 0;
        if (m.Contains("sonnet") || m.Contains("gemini 2.5 pro") || (m.Contains("gpt-5") && !m.Contains("mini"))) return 1;
        if (m.Contains("haiku") || m.Contains("mini") || m.Contains("flash")) return 3;
        return 2;
    }

    /// <summary>Map a raw model id ("claude-opus-4-7") to a short friendly name
    /// ("Opus 4.7").</summary>
    public static string FriendlyModel(string raw)
    {
        var r = raw.ToLowerInvariant();

        string Version(string family)
        {
            // Pull the trailing "-4-7" / "-4-5" → "4.7". Filter to SMALL numbers so a
            // trailing build date ("claude-haiku-4-5-20251001") doesn't become the
            // version ("Haiku 5.20251001").
            var digits = raw.Split('-')
                .Select(p => int.TryParse(p, out var n) ? (int?)n : null)
                .Where(n => n is not null && n < 100)
                .Select(n => n!.Value)
                .ToList();
            var v = string.Join('.', digits.TakeLast(2));
            return v.Length == 0 ? family : $"{family} {v}";
        }

        if (r.Contains("opus")) return Version("Opus");
        if (r.Contains("sonnet")) return Version("Sonnet");
        if (r.Contains("haiku")) return Version("Haiku");
        if (r.Contains("fable")) return Version("Fable");
        if (r == "—" || r.Length == 0) return "—";
        return raw;
    }

    private static DateTimeOffset FloorToHour(DateTimeOffset date) =>
        new(date.Year, date.Month, date.Day, date.Hour, 0, 0, date.Offset);

    private static Sample? ParseSample(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "assistant") return null;
            if (!root.TryGetProperty("timestamp", out var tsEl) || tsEl.ValueKind != JsonValueKind.String) return null;
            if (!DateTimeOffset.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var date)) return null;
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return null;
            if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;

            var tokens = IntOrZero(usage, "input_tokens") + IntOrZero(usage, "output_tokens")
                + IntOrZero(usage, "cache_creation_input_tokens") + IntOrZero(usage, "cache_read_input_tokens");
            if (tokens <= 0) return null;

            var rawModel = message.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
                ? modelEl.GetString() ?? "—"
                : "—";
            return new Sample(date, FriendlyModel(rawModel), tokens);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int IntOrZero(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
