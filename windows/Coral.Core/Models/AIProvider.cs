namespace Coral.Core.Models;

/// <summary>
/// The AI back-ends Coral can run a chat on. Mirrors
/// <c>StrategyForge/Models/AIProvider.swift</c> on the macOS app — same three
/// providers, same string keys, because <c>models.json</c> at the repo root is a
/// contract shared by both clients (see windows/README.md).
/// </summary>
public enum AIProvider
{
    Claude,
    Openai,
    Gemini,
}

public static class AIProviderExtensions
{
    /// <summary>The key used in models.json's "providers" map — matches the Swift
    /// enum's rawValue exactly, since both clients read the same file.</summary>
    public static string ToKey(this AIProvider provider) => provider switch
    {
        AIProvider.Claude => "claude",
        AIProvider.Openai => "openai",
        AIProvider.Gemini => "gemini",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    public static AIProvider? FromKey(string key) => key switch
    {
        "claude" => AIProvider.Claude,
        "openai" => AIProvider.Openai,
        "gemini" => AIProvider.Gemini,
        _ => null,
    };

    /// <summary>Human name shown in the UI.</summary>
    public static string DisplayName(this AIProvider provider) => provider switch
    {
        AIProvider.Claude => "Claude",
        AIProvider.Openai => "ChatGPT · Codex",
        AIProvider.Gemini => "Gemini",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>The CLI binary spawned for this provider. Matches
    /// <c>AIProvider.swift</c>'s <c>binaryName</c>.</summary>
    public static string BinaryName(this AIProvider provider) => provider switch
    {
        AIProvider.Claude => "claude",
        AIProvider.Openai => "codex",
        AIProvider.Gemini => "gemini",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>Fallback binaries to try if the primary isn't found. Google
    /// retired the free <c>gemini</c> CLI in favour of <c>agy</c> (Antigravity) —
    /// matches <c>AIProvider.swift</c>'s <c>alternativeBinaries</c>.</summary>
    public static IReadOnlyList<string> AlternativeBinaries(this AIProvider provider) => provider switch
    {
        AIProvider.Gemini => new[] { "agy" },
        AIProvider.Claude or AIProvider.Openai => Array.Empty<string>(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>The npm package that installs this provider's CLI.</summary>
    public static string NpmPackage(this AIProvider provider) => provider switch
    {
        AIProvider.Claude => "@anthropic-ai/claude-code",
        AIProvider.Openai => "@openai/codex",
        AIProvider.Gemini => "@google/gemini-cli",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>Version this Coral release pins each CLI to (supply-chain guard
    /// against a bare <c>npm install -g</c> resolving a compromised <c>@latest</c>).
    /// Empty = unpinned. Mirrors <c>AIProvider.swift</c>'s <c>pinnedCLIVersions</c>.</summary>
    public static readonly IReadOnlyDictionary<AIProvider, string> PinnedCliVersions = new Dictionary<AIProvider, string>
    {
        [AIProvider.Claude] = "",
        [AIProvider.Openai] = "",
        [AIProvider.Gemini] = "",
    };

    /// <summary>The exact <c>npm install -g</c> spec: the package, plus a pinned
    /// <c>@version</c> when one is vetted. Falls back to the bare package.</summary>
    public static string NpmInstallSpec(this AIProvider provider)
    {
        var pin = PinnedCliVersions.TryGetValue(provider, out var v) ? v.Trim() : "";
        return pin.Length == 0 ? provider.NpmPackage() : $"{provider.NpmPackage()}@{pin}";
    }

    /// <summary>The command that starts the provider's browser sign-in.</summary>
    public static string LoginCommand(this AIProvider provider) => provider switch
    {
        AIProvider.Claude => "claude auth login --claudeai",
        AIProvider.Openai => "codex login",
        AIProvider.Gemini => "gemini",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>Whether the sign-in must open a visible terminal up front.
    /// Coral drives every login in a hidden pseudo-console instead (see
    /// ProviderInstaller.cs), so this is false for every provider — matches
    /// <c>AIProvider.swift</c>'s <c>loginNeedsTerminal</c>, kept only so callers
    /// read the same way the Swift original does.</summary>
    public static bool LoginNeedsTerminal(this AIProvider provider) => false;
}
