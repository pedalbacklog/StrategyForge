namespace Coral.Core.Models;

/// <summary>
/// One selectable model for a provider. Mirrors the Swift <c>ProviderModel</c>
/// (<c>StrategyForge/Models/AIProvider.swift</c>) field for field — same JSON
/// shape, so both clients parse the same <c>models.json</c> entries.
/// </summary>
/// <param name="Id">The exact model id passed to the provider's CLI.</param>
/// <param name="DisplayName">Human-friendly name shown in pickers.</param>
/// <param name="TierKey">Localization key for the capability tier (Expert / Generalist / Fast…).</param>
public sealed record ProviderModel(string Id, string DisplayName, string TierKey);
