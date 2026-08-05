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
}
