using System.Text.Json;
using Coral.Core.Models;

namespace Coral.Core.Services;

/// <summary>
/// A cheap, no-subprocess check that a provider's stored login looks USABLE — a
/// credentials file exists and, where the format exposes an expiry, hasn't
/// lapsed. Windows counterpart of <c>StrategyForge/Services/ProviderAuth.swift</c>.
/// Deliberately does NOT touch Windows Credential Manager/DPAPI here (the macOS
/// original avoids the Keychain for the same reason: it would prompt at
/// launch) — only the providers' own on-disk credential files.
/// </summary>
public static class ProviderAuth
{
    /// <summary>The result of a startup liveness check for one provider.</summary>
    public enum State
    {
        Ok,       // a token exists and isn't known-expired
        Expired,  // a token exists but its expiry has passed
        Missing,  // installed, but no stored login found
        Unknown,  // can't tell cheaply — treat as fine
    }

    /// <summary>Check every given provider's login freshness, concurrently, off
    /// the calling thread (these are blocking file reads).</summary>
    public static async Task<Dictionary<AIProvider, State>> VerifyAsync(
        IEnumerable<AIProvider> providers, CancellationToken ct = default)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var tasks = providers.Select(p => Task.Run(() => (Provider: p, State: FreshnessUncached(p, home)), ct));
        var results = await Task.WhenAll(tasks);
        return results.ToDictionary(r => r.Provider, r => r.State);
    }

    /// <summary>One provider's login freshness from its on-disk credentials,
    /// against the real <c>%USERPROFILE%</c>.</summary>
    public static State Freshness(AIProvider provider) =>
        FreshnessUncached(provider, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Pure given <paramref name="homeDirectory"/> — fully unit-testable
    /// against a temp directory standing in for <c>%USERPROFILE%</c>.</summary>
    public static State FreshnessUncached(AIProvider provider, string homeDirectory)
    {
        switch (provider)
        {
            case AIProvider.Claude:
                // Claude usually stores its OAuth in Credential Manager (which we
                // must not touch at launch), so only the plain credentials file is
                // a cheap signal. Absent -> Unknown (likely Credential-Manager-backed
                // and fine), never Missing.
                var claudePath = Path.Combine(homeDirectory, ".claude", ".credentials.json");
                return File.Exists(claudePath)
                    ? ExpiryState(claudePath, new[] { "claudeAiOauth", "expiresAt" }, TimeUnit.Millis)
                    : State.Unknown;

            case AIProvider.Gemini:
                // `gemini` / `agy` write this on a successful Google login.
                var geminiPath = Path.Combine(homeDirectory, ".gemini", "oauth_creds.json");
                return File.Exists(geminiPath)
                    ? ExpiryState(geminiPath, new[] { "expiry_date" }, TimeUnit.Millis)
                    : State.Missing;

            case AIProvider.Openai:
                // `codex login` writes a ChatGPT token here; format is opaque enough
                // that mere presence is the honest signal (its own run-time auth
                // check catches expiry).
                var codexPath = Path.Combine(homeDirectory, ".codex", "auth.json");
                return File.Exists(codexPath) ? State.Ok : State.Missing;

            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
        }
    }

    private enum TimeUnit { Millis, Seconds }

    /// <summary>Read a nested numeric expiry from a JSON credentials file and
    /// compare to now. If the field is absent we can't judge -> Ok (token
    /// present, unknown lifetime). If the file can't be read/parsed -> Unknown.</summary>
    private static State ExpiryState(string path, string[] keyPath, TimeUnit unit)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllBytes(path));
        }
        catch
        {
            return State.Unknown;
        }

        using (doc)
        {
            var node = doc.RootElement;
            foreach (var key in keyPath)
            {
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key, out var next))
                    return State.Ok; // present but no expiry field
                node = next;
            }

            if (node.ValueKind != JsonValueKind.Number || !node.TryGetDouble(out var raw))
                return State.Ok;

            var divisor = unit == TimeUnit.Millis ? 1000.0 : 1.0;
            var expiry = DateTimeOffset.UnixEpoch.AddSeconds(raw / divisor);
            return expiry > DateTimeOffset.UtcNow ? State.Ok : State.Expired;
        }
    }
}
