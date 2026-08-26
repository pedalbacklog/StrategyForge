using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Coral.Core.Services;

/// <summary>One rate-limit window (e.g. the 5-hour or weekly bucket) as Codex
/// reports it. Port of <c>CodexUsageStore.swift</c>'s <c>CodexWindow</c>.</summary>
public sealed record CodexWindow(double UsedPercent, int WindowMinutes, DateTimeOffset? ResetsAt)
{
    /// <summary>A short human label KEY for the window based on its length —
    /// matches Swift's own localization-key naming
    /// ("usage.codex.window.week/day/5h"); no UI resolves these yet (this
    /// port has no localization system), kept for parity with the data shape
    /// a future usage view would consume.</summary>
    public string KindLabelKey =>
        WindowMinutes >= 6 * 24 * 60 ? "usage.codex.window.week" :   // >= ~1 week
        WindowMinutes >= 24 * 60 ? "usage.codex.window.day" :
        "usage.codex.window.5h";
}

/// <summary>Aggregated Codex usage read from local session logs. Port of
/// <c>CodexUsageStore.swift</c>'s <c>CodexUsage</c>.</summary>
public sealed record CodexUsage(
    string? PlanType,
    CodexWindow? Primary,
    CodexWindow? Secondary,
    int TotalTokens,
    DateTimeOffset? LastActivity,
    DateTimeOffset ComputedAt)
{
    public bool HasData => Primary is not null || TotalTokens > 0;

    public static readonly CodexUsage Empty = new(null, null, null, 0, null, DateTimeOffset.MinValue);
}

/// <summary>
/// Real OpenAI Codex usage read from its local session logs. Port of
/// <c>CodexUsageStore.swift</c>. Unlike Claude (which publishes no cap, so
/// only tokens can be counted), the Codex CLI writes the SERVER'S
/// authoritative rate-limit percentage to disk on every turn — so this can
/// show a true "% of your plan used" with the exact reset time, no network
/// and no API key.
///
/// Source: <c>%USERPROFILE%\.codex\sessions\YYYY\MM\DD\rollout-*.jsonl</c> —
/// lines whose <c>payload.type == "token_count"</c> carry
/// <c>rate_limits.primary/secondary</c> (<c>used_percent</c>,
/// <c>window_minutes</c>, <c>resets_at</c>) plus <c>plan_type</c> and a token
/// total. The counters are account-wide, so the most recent event is the
/// freshest state. Tolerant parsing: unknown/legacy lines are skipped, never
/// fatal. Same "data layer ahead of any UI" scope as
/// <see cref="ClaudeUsageStore"/> — see its own doc comment for why.
/// </summary>
public static class CodexUsageStore
{
    /// <summary>Scan the logs under <paramref name="homeDirectory"/>'s
    /// <c>.codex/sessions</c> and return the freshest usage state (null if
    /// there's nothing to report — matches Swift's own optional return,
    /// distinct from <see cref="ClaudeUsageStore"/>'s <c>Empty</c> sentinel:
    /// this store's freshest-state search can legitimately find no
    /// <c>token_count</c> event at all). Pure given its inputs — fully
    /// unit-testable against a temp directory standing in for
    /// <c>%USERPROFILE%</c>.</summary>
    public static CodexUsage? LoadUncached(string homeDirectory, DateTimeOffset now)
    {
        var root = Path.Combine(homeDirectory, ".codex", "sessions");
        if (!Directory.Exists(root)) return null;

        // Rate-limit state is recent — only recently-touched session files matter.
        var cutoff = now - TimeSpan.FromDays(4);
        var files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
            .Select(path => (Path: path, Mod: File.GetLastWriteTimeUtc(path)))
            .Where(f => f.Mod >= cutoff.UtcDateTime)
            .OrderByDescending(f => f.Mod)
            .Take(40);

        // Look through the most recent files until one yields a token_count event.
        foreach (var file in files)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file.Path);
            }
            catch
            {
                continue;
            }

            (JsonElement Payload, DateTimeOffset Date)? best = null;
            foreach (var line in lines)
            {
                if (!line.Contains("token_count")) continue;
                var hit = ParseTokenCountLine(line);
                if (hit is null) continue;
                if (best is null || hit.Value.Date >= best.Value.Date) best = hit;
            }
            if (best is null) continue;

            var payload = best.Value.Payload;
            var rateLimits = payload.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == JsonValueKind.Object
                ? rl : (JsonElement?)null;
            var totalTokens = payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                && info.TryGetProperty("total_token_usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("total_tokens", out var tt) && tt.ValueKind == JsonValueKind.Number
                ? tt.GetInt32() : 0;

            return new CodexUsage(
                PlanType: rateLimits is { } rlv && rlv.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String
                    ? pt.GetString() : null,
                Primary: ParseWindow(rateLimits, "primary"),
                Secondary: ParseWindow(rateLimits, "secondary"),
                TotalTokens: totalTokens,
                LastActivity: best.Value.Date,
                ComputedAt: now);
        }
        return null;
    }

    /// <summary>The real usage state, reading <c>%USERPROFILE%\.codex\sessions</c>.</summary>
    public static CodexUsage? Load(DateTimeOffset? now = null) =>
        LoadUncached(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), now ?? DateTimeOffset.Now);

    private static CodexWindow? ParseWindow(JsonElement? rateLimits, string key)
    {
        if (rateLimits is not { } rl || !rl.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("used_percent", out var pctEl) || pctEl.ValueKind != JsonValueKind.Number) return null;
        if (!w.TryGetProperty("window_minutes", out var minsEl) || minsEl.ValueKind != JsonValueKind.Number) return null;
        DateTimeOffset? resetsAt = w.TryGetProperty("resets_at", out var resetsEl) && resetsEl.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.UnixEpoch.AddSeconds(resetsEl.GetDouble())
            : null;
        return new CodexWindow(pctEl.GetDouble(), minsEl.GetInt32(), resetsAt);
    }

    private static (JsonElement Payload, DateTimeOffset Date)? ParseTokenCountLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return null;
            if (!payload.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "token_count") return null;

            var date = DateTimeOffset.MinValue;
            if (root.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsed))
            {
                date = parsed;
            }
            // Clone: JsonDocument disposes at the end of this call, but the payload
            // element is returned to the caller past that point.
            return (payload.Clone(), date);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
