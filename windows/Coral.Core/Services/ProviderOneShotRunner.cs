using System.Text.Json;
using Coral.Core.Models;

namespace Coral.Core.Services;

/// <summary>The result of a single model call. Port of
/// <c>ProviderRun.swift</c>'s <c>OneShotResult</c>.</summary>
/// <param name="Estimated">True when <paramref name="Tokens"/>/<paramref name="CostUsd"/>
/// are an ESTIMATE (the CLI reported no usage — Codex's plain output,
/// Gemini), so a caller can mark them "~" instead of implying a real
/// count.</param>
public sealed record OneShotResult(string Text, int Tokens, double CostUsd, AIProvider Provider,
    string Model, bool Estimated = false);

public enum OneShotErrorKind
{
    NotInstalled,
    Failed,
    /// <summary>The watchdog terminated the call after <see cref="ProviderOneShotRunner.CallTimeout"/>.</summary>
    TimedOut,
    /// <summary>The CLI wants an interactive login (its saved session is
    /// stale/missing) — caught the moment it prints an auth prompt or a
    /// recognizable auth failure.</summary>
    AuthRequired,
}

/// <summary>Port of <c>ProviderRun.swift</c>'s <c>OneShotError</c>.</summary>
public sealed class OneShotException : Exception
{
    public OneShotErrorKind Kind { get; }
    public OneShotException(OneShotErrorKind kind, string message) : base(message) => Kind = kind;
}

/// <summary>A provider-agnostic single-shot completion. Injectable so a
/// caller can be unit-tested with a fake and run for real with
/// <see cref="ProviderOneShotRunner"/>. Port of <c>ProviderRun.swift</c>'s
/// <c>protocol OneShotRunner</c> — buffered only (no live streaming
/// <c>onEvent</c> variant): the consumer this exists for
/// (<c>CrossProviderEditor</c>/<c>TeamRunEngine</c>) shows a final diff, not
/// live tool-by-tool progress, so the streaming half of the Swift protocol
/// wasn't ported — a smaller surface for the same reason Fase 2/3's other
/// cuts were smaller than their Swift originals.</summary>
public interface IOneShotRunner
{
    Task<OneShotResult> RunAsync(string prompt, AIProvider provider, string model, string? cwd,
        CancellationToken ct = default);
}

/// <summary>The real runner: spawns each provider's CLI headlessly. Claude
/// runs over plain pipes via <see cref="IProcessLauncher"/> (its
/// <c>--output-format json</c> doesn't need a real terminal — same as the
/// interactive chat path's streaming variant). Codex/Gemini run under a
/// hidden pseudo-console via <see cref="IPseudoConsoleLauncher"/>, same
/// infrastructure already proven for their sign-in flows in
/// <c>ProviderInstaller.cs</c> — matches the Swift original's own reasoning
/// for using a PTY there (these CLIs can block-buffer on a plain pipe).</summary>
public sealed class ProviderOneShotRunner : IOneShotRunner
{
    /// <summary>Per-call watchdog. Generous on purpose — a real multi-agent
    /// task can legitimately take minutes — still a backstop against a
    /// genuinely hung CLI. Matches <c>ProviderRun.swift</c>'s <c>callTimeout</c>.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(600);

    private readonly IProcessLauncher _processLauncher;
    private readonly IPseudoConsoleLauncher _ptyLauncher;
    private readonly Func<string, string?> _resolveBinary;
    private readonly string _permissionMode;
    private readonly TimeSpan _callTimeout;

    /// <param name="callTimeout">Overrides <see cref="CallTimeout"/> — test-only
    /// knob so the watchdog path can be exercised in milliseconds instead of
    /// 10 real minutes; production callers leave it at the default.</param>
    public ProviderOneShotRunner(IProcessLauncher processLauncher, IPseudoConsoleLauncher ptyLauncher,
        Func<string, string?>? resolveBinary = null, string permissionMode = "bypassPermissions",
        TimeSpan? callTimeout = null)
    {
        _processLauncher = processLauncher;
        _ptyLauncher = ptyLauncher;
        _resolveBinary = resolveBinary ?? BinaryResolver.Resolve;
        _permissionMode = permissionMode;
        _callTimeout = callTimeout ?? CallTimeout;
    }

    public async Task<OneShotResult> RunAsync(string prompt, AIProvider provider, string model, string? cwd,
        CancellationToken ct = default)
    {
        var bin = ResolveProviderBinary(provider, _resolveBinary);
        if (bin is null)
        {
            throw new OneShotException(OneShotErrorKind.NotInstalled,
                $"{provider.DisplayName()}'s CLI isn't installed. Connect it first.");
        }

        var workingDirectory = cwd ?? Environment.CurrentDirectory;
        using var timeoutCts = new CancellationTokenSource(_callTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            return provider == AIProvider.Claude
                ? await RunClaudeAsync(bin, prompt, model, workingDirectory, provider, linkedCts.Token)
                : await RunPtyAsync(bin, prompt, provider, model, workingDirectory, linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new OneShotException(OneShotErrorKind.TimedOut,
                $"{provider.DisplayName()} didn't finish within {_callTimeout.TotalSeconds:0} seconds — the task may be too large. Try splitting it, using fewer agents, or a faster model.");
        }
    }

    private async Task<OneShotResult> RunClaudeAsync(string bin, string prompt, string model,
        string cwd, AIProvider provider, CancellationToken ct)
    {
        var command = CLIOneShotRunner.Command(provider, prompt, model, _permissionMode);
        var (ok, stdout, stderr) = await OneShotProcess.RunAsync(_processLauncher, bin, command.Args, cwd,
            ClaudeRunner.BuildEnvironment(bin), ct);
        if (!ok)
        {
            var authMessage = CLIOneShotRunner.AuthFailureMessage(provider, stdout, stderr);
            if (authMessage is not null) throw new OneShotException(OneShotErrorKind.AuthRequired, authMessage);
            // Prefer stderr (matches ProviderRun.swift's own fallback), but fall back to
            // stdout rather than discarding it: with --output-format json, a failure that
            // never reaches a valid JSON result often puts its only diagnostic text there
            // instead of stderr — Swift keeps that text too, via a DiagnosticsLog capture
            // this port hasn't ported yet, so the thrown message is the only place left to
            // carry it.
            var trimmed = stderr.Trim();
            if (trimmed.Length == 0) trimmed = stdout.Trim();
            throw new OneShotException(OneShotErrorKind.Failed,
                trimmed.Length > 0 ? trimmed : $"{provider.DisplayName()} exited with an error.");
        }
        var parsed = ParseClaudeJson(stdout, provider, model);
        if (parsed is null)
        {
            throw new OneShotException(OneShotErrorKind.Failed, $"Couldn't parse {provider.DisplayName()} output.");
        }
        return parsed;
    }

    private async Task<OneShotResult> RunPtyAsync(string bin, string prompt, AIProvider provider, string model,
        string cwd, CancellationToken ct)
    {
        var command = CLIOneShotRunner.Command(provider, prompt, model, _permissionMode);
        using var session = _ptyLauncher.Start(bin, command.Args, cwd, new Dictionary<string, string?>());
        var lines = new List<string>();
        try
        {
            await foreach (var line in session.ReadOutputLinesAsync(ct))
            {
                lines.Add(line);
                if (CLIOneShotRunner.IsAuthPrompt(line))
                {
                    session.Kill();
                    throw new OneShotException(OneShotErrorKind.AuthRequired,
                        $"{provider.DisplayName()} needs you to sign in again — its saved login looks expired. Reconnect {provider.DisplayName()} and retry.");
                }
            }
        }
        finally
        {
            session.Kill();
        }
        var exitCode = await session.WaitForExitAsync(CancellationToken.None);
        var rawOutput = string.Join("\n", lines);
        if (exitCode != 0)
        {
            var authMessage = CLIOneShotRunner.AuthFailureMessage(provider, rawOutput, "");
            if (authMessage is not null) throw new OneShotException(OneShotErrorKind.AuthRequired, authMessage);
            var tail = CLIOneShotRunner.StripAnsi(rawOutput).Trim();
            throw new OneShotException(OneShotErrorKind.Failed,
                tail.Length > 0 ? tail[..Math.Min(tail.Length, 400)] : $"{provider.DisplayName()} exited with code {exitCode}");
        }
        // PTY output carries terminal formatting — strip ANSI before returning it.
        var cleanText = CLIOneShotRunner.StripAnsi(rawOutput).Trim();
        var estTokens = CLIOneShotRunner.EstimateTokens(prompt, cleanText);
        var estCost = CLIOneShotRunner.EstimatedCostUsd(estTokens, model);
        return new OneShotResult(cleanText, estTokens, estCost, provider, model, Estimated: true);
    }

    /// <summary>Resolve a provider's binary, preferring an alternative when
    /// it's installed (e.g. Google retired the free <c>gemini</c> CLI for
    /// individuals in favor of Antigravity's <c>agy</c> — the dead
    /// <c>gemini</c> binary may still be on disk but can never authenticate).
    /// Matches <c>ProviderRun.swift</c>'s <c>resolveBinary(_:provider:)</c>.</summary>
    private static string? ResolveProviderBinary(AIProvider provider, Func<string, string?> resolveBinary)
    {
        foreach (var alt in provider.AlternativeBinaries())
        {
            if (resolveBinary(alt) is { } p) return p;
        }
        return resolveBinary(provider.BinaryName());
    }

    /// <summary>Parse <c>claude -p --output-format json</c>'s single result
    /// object. Matches <c>ProviderRun.swift</c>'s <c>parseClaudeJSON</c>.</summary>
    private static OneShotResult? ParseClaudeJson(string text, AIProvider provider, string model)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var result = root.TryGetProperty("result", out var resultEl) && resultEl.ValueKind == JsonValueKind.String
                ? resultEl.GetString() ?? ""
                : "";
            var tokens = 0;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                tokens += IntOrZero(usage, "input_tokens") + IntOrZero(usage, "output_tokens")
                    + IntOrZero(usage, "cache_creation_input_tokens") + IntOrZero(usage, "cache_read_input_tokens");
            }
            var cost = root.TryGetProperty("total_cost_usd", out var costEl) && costEl.ValueKind == JsonValueKind.Number
                ? costEl.GetDouble()
                : 0.0;
            return new OneShotResult(result, tokens, cost, provider, model);
        }
    }

    private static int IntOrZero(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
