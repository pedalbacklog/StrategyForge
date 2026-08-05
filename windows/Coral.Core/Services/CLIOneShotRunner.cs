using System.Text.RegularExpressions;
using Coral.Core.Models;

namespace Coral.Core.Services;

/// <summary>How to read a provider CLI's output.</summary>
public enum OutputMode { ClaudeJson, PlainText }

/// <summary>The argv (after the binary) and how to read its output, for one
/// provider command.</summary>
public sealed record ProviderCommand(List<string> Args, OutputMode Mode);

/// <summary>
/// Port of the PURE slice of <c>StrategyForge/Services/ProviderRun.swift</c>'s
/// <c>CLIOneShotRunner</c> — command construction, output cleaning, and cost
/// estimation for a provider-agnostic one-shot CLI call. No process spawning
/// here (that needs Windows to verify — see windows/PORT-PLAN.md Fase 3); this
/// is exactly the part that's pure text/JSON transformation and was already
/// unit-tested standalone in the Swift original.
/// </summary>
public static class CLIOneShotRunner
{
    private static readonly Regex AnsiCsi = new(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex WordChar = new(@"[\p{L}\p{N}]", RegexOptions.Compiled);

    private static readonly string[] NoiseSubstrings =
    {
        "■", "▔", "────", "====", "thinking...", "loading", "esc to interrupt",
    };

    /// <summary>Remove ANSI/VT100 escape sequences from terminal output.</summary>
    public static string StripAnsi(string s) => AnsiCsi.Replace(s, "");

    /// <summary>Turn one raw CLI stdout line into a short, human "what's happening
    /// now" — or null to skip it. Strips ANSI colour codes, drops empty / spinner /
    /// pure-symbol / banner noise, collapses whitespace and truncates. This is the
    /// "summarize the important bit" for providers with no structured tool stream
    /// (Codex/Gemini).</summary>
    public static string? ProgressLine(string raw)
    {
        var noAnsi = AnsiCsi.Replace(raw, "");
        var collapsed = Whitespace.Replace(noAnsi, " ");
        var s = collapsed.Trim();
        if (s.Length < 4) return null;
        // Skip lines that are only symbols/box-drawing/spinner frames (no letters/digits).
        if (!WordChar.IsMatch(s)) return null;
        var low = s.ToLowerInvariant();
        if (NoiseSubstrings.Any(low.Contains)) return null;
        return s.Length > 140 ? s[..140] + "…" : s;
    }

    /// <summary>The CLI wants an interactive login/confirmation no one can answer
    /// headlessly — caught the moment it prints one of these, so the run fails
    /// fast instead of hanging.</summary>
    public static bool IsAuthPrompt(string line)
    {
        var s = line.ToLowerInvariant();
        return s.Contains("opening authentication page")
            || s.Contains("do you want to continue")
            || s.Contains("please set an auth method")
            || s.Contains("waiting for auth")
            || s.Contains("authenticate in your browser")
            || s.Contains("sign in to continue")
            || s.Contains("press enter to continue");
    }

    /// <summary>Google's "the free `gemini` CLI is no longer supported — migrate
    /// to Antigravity" error. A distinct case from a stale login: reconnecting
    /// the same CLI can never succeed.</summary>
    public static bool IsAntigravityMigration(string text)
    {
        var h = text.ToLowerInvariant();
        return h.Contains("antigravity") || h.Contains("ineligibletier")
            || h.Contains("unsupported_client")
            || (h.Contains("no longer supported") && h.Contains("gemini"));
    }

    /// <summary>If the CLI output smells like an authentication failure (401 /
    /// "failed to authenticate"), return a per-provider, actionable message; else
    /// null. Coral runs on each provider's own stored subscription login, so the
    /// fix is a re-sign-in.</summary>
    public static string? AuthFailureMessage(AIProvider provider, string stdout, string stderr)
    {
        var hay = (stdout + "\n" + stderr).ToLowerInvariant();
        var looksAuth = hay.Contains("401")
            || hay.Contains("invalid authentication")
            || hay.Contains("failed to authenticate")
            || hay.Contains("please set an auth method");
        if (!looksAuth) return null;
        return provider switch
        {
            AIProvider.Claude => "Claude couldn't authenticate (401). Coral uses your Claude Code login from its default location — your saved login looks expired. Open Terminal, run `claude`, sign in to your plan, then retry.",
            AIProvider.Openai => "Codex couldn't authenticate. Open Terminal and run `codex login` to sign in to your ChatGPT plan (or add an API key in Connect), then retry.",
            AIProvider.Gemini => "Gemini couldn't authenticate. Open Terminal and run `gemini` to sign in to your Google account (or add an API key in Connect), then retry.",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
    }

    /// <summary>Rough token estimate for a CLI that reports no usage: ~4
    /// characters per token across the prompt + the model's output. Coarse but
    /// honest — enough to stop cross-provider runs reading as "0 tokens / $0",
    /// and always surfaced with a "~".</summary>
    public static int EstimateTokens(string prompt, string output) =>
        Math.Max(1, (prompt.Length + output.Length) / 4);

    /// <summary>A rough "~$" for an estimated token count: the model's real
    /// blended price when we have one, else a mid-tier fallback. Always an
    /// estimate (the caller flags it).</summary>
    public static double EstimatedCostUsd(int tokens, string model)
    {
        var perM = Constants.Pricing.TryGetValue(model, out var price)
            ? (price.InputPerM + price.OutputPerM) / 2 // blended: we don't split in/out here
            : Constants.CostModel.EstimatedBlendedFallbackPerM;
        return (double)tokens / 1_000_000 * perM;
    }

    /// <summary>The argv (after the binary) and how to read its output, per
    /// provider. Flags go BEFORE the positional prompt (robust across arg
    /// parsers).</summary>
    public static ProviderCommand Command(AIProvider provider, string prompt, string model,
        string permissionMode, string reasoningEffort = "", bool hasApiKey = false, bool readOnly = false)
    {
        switch (provider)
        {
            case AIProvider.Claude:
            {
                // Claude uses real, full model ids (e.g. claude-opus-4-8).
                var a = new List<string> { "--output-format", "json", "--permission-mode", permissionMode };
                // Read-only: still allow reads + Bash (to run tests/greps) but
                // forbid every file-editing tool, so a verifier can't modify the
                // repo it's judging.
                if (readOnly)
                {
                    a.Add("--disallowedTools");
                    a.Add("Edit Write MultiEdit NotebookEdit");
                }
                if (model.Length > 0) { a.Add("--model"); a.Add(model); }
                a.Add("-p");
                a.Add(prompt);
                return new ProviderCommand(a, OutputMode.ClaudeJson);
            }
            case AIProvider.Openai:
            {
                // Codex CLI, non-interactive: `codex exec --skip-git-repo-check "<prompt>"`.
                var a = new List<string> { "exec", "--skip-git-repo-check" };
                // Reasoning effort works even on a ChatGPT-account login (unlike
                // --model), via a -c config override.
                if (reasoningEffort.Length > 0)
                {
                    a.Add("-c");
                    a.Add($"model_reasoning_effort=\"{reasoningEffort}\"");
                }
                // Model selection is only accepted with an API key. With a
                // ChatGPT-account login Codex rejects an explicit model, so we
                // omit --model unless a key is configured.
                if (hasApiKey && model.Length > 0) { a.Add("--model"); a.Add(model); }
                a.Add(prompt); // --skip-git-repo-check lets it run outside a git repo.
                return new ProviderCommand(a, OutputMode.PlainText);
            }
            case AIProvider.Gemini:
            {
                // Gemini CLI, non-interactive: `gemini -m <id> -p "<prompt>"`.
                var a = new List<string>();
                if (model.Length > 0) { a.Add("-m"); a.Add(model); }
                a.Add("-p");
                a.Add(prompt);
                return new ProviderCommand(a, OutputMode.PlainText);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
        }
    }
}
