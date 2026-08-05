namespace Coral.Core.Models;

/// <summary>
/// Port of <c>StrategyForge/Models/ClaudeModel.swift</c>. The set of Claude models
/// that can be assigned to a role. Raw values are the exact model ids passed to
/// Claude Code, both in subagent frontmatter (<c>model:</c>) and the launch
/// command (<c>--model</c>).
/// </summary>
public enum ClaudeModel
{
    Opus5,
    Fable5,
    Sonnet5,
    Haiku45,
    Opus48, // legacy — kept so saved configs still decode
}

public static class ClaudeModelExtensions
{
    public static string ToRawValue(this ClaudeModel model) => model switch
    {
        ClaudeModel.Opus5 => "claude-opus-5",
        ClaudeModel.Fable5 => "claude-fable-5",
        ClaudeModel.Sonnet5 => "claude-sonnet-5",
        ClaudeModel.Haiku45 => "claude-haiku-4-5",
        ClaudeModel.Opus48 => "claude-opus-4-8",
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, null),
    };

    public static ClaudeModel? FromRawValue(string value) => value switch
    {
        "claude-opus-5" => ClaudeModel.Opus5,
        "claude-fable-5" => ClaudeModel.Fable5,
        "claude-sonnet-5" => ClaudeModel.Sonnet5,
        "claude-haiku-4-5" => ClaudeModel.Haiku45,
        "claude-opus-4-8" => ClaudeModel.Opus48,
        _ => null,
    };

    /// <summary>Human-friendly name for pickers.</summary>
    public static string DisplayName(this ClaudeModel model) => model switch
    {
        ClaudeModel.Opus5 => "Opus 5",
        ClaudeModel.Fable5 => "Fable 5",
        ClaudeModel.Sonnet5 => "Sonnet 5",
        ClaudeModel.Haiku45 => "Haiku 4.5",
        ClaudeModel.Opus48 => "Opus 4.8",
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, null),
    };

    /// <summary>A plain-language "what kind of mind is this" framing — the model
    /// setting is about how CAPABLE the model is, distinct from effort. Values are
    /// localization keys resolved in the UI (not ported yet).</summary>
    public static string TierNameKey(this ClaudeModel model) => model switch
    {
        ClaudeModel.Fable5 => "model.tier.specialist",
        ClaudeModel.Opus5 or ClaudeModel.Opus48 => "model.tier.expert",
        ClaudeModel.Sonnet5 => "model.tier.generalist",
        ClaudeModel.Haiku45 => "model.tier.fast",
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, null),
    };
}
