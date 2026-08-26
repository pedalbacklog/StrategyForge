using Coral.Core.Models;
using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Tests for ProviderOneShotRunner's ORCHESTRATION (binary
/// resolution, Claude JSON parsing, PTY auth-prompt fast-fail, exit-code
/// handling, the watchdog) against fakes — no Swift source to port from
/// (ProviderRun.swift's own tests, if any, aren't in scope here since the
/// streaming half of its protocol wasn't ported). Mirrors
/// ProviderInstallerTests's fake-based style.</summary>
public class ProviderOneShotRunnerTests
{
    private static ProviderOneShotRunner Runner(FakeProcessLauncher pipes, FakePseudoConsoleLauncher pty,
        Func<string, string?>? resolveBinary = null, TimeSpan? callTimeout = null) =>
        // recordDiagnostic: no-op — a failure path exercised here must never write to the
        // real %LOCALAPPDATA%\Coral\diagnostics.log on whatever machine runs the tests.
        new(pipes, pty, resolveBinary ?? (n => $"/usr/bin/{n}"), callTimeout: callTimeout,
            recordDiagnostic: (_, _) => { });

    private static FakeProcessLauncher NoPipes() => new((_, _) => new FakeChildProcess(new List<string>()));
    private static FakePseudoConsoleLauncher NoPty() => new((_, _) => new FakePseudoConsoleSession(new List<string>()));

    [Fact]
    public async Task ThrowsNotInstalledWhenTheBinaryCannotBeResolved()
    {
        var runner = Runner(NoPipes(), NoPty(), resolveBinary: _ => null);

        var ex = await Assert.ThrowsAsync<OneShotException>(
            () => runner.RunAsync("task", AIProvider.Claude, "claude-sonnet-5", "/repo"));

        Assert.Equal(OneShotErrorKind.NotInstalled, ex.Kind);
    }

    [Fact]
    public async Task ClaudeParsesTheResultJsonAndUsage()
    {
        var json = """{"result":"done","usage":{"input_tokens":10,"output_tokens":5},"total_cost_usd":0.02}""";
        var pipes = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string> { json }));

        var result = await Runner(pipes, NoPty()).RunAsync("task", AIProvider.Claude, "claude-sonnet-5", "/repo");

        Assert.Equal("done", result.Text);
        Assert.Equal(15, result.Tokens);
        Assert.Equal(0.02, result.CostUsd);
        Assert.False(result.Estimated);
    }

    [Fact]
    public async Task ClaudeThrowsAuthRequiredOnA401()
    {
        var pipes = new FakeProcessLauncher((_, _) =>
            new FakeChildProcess(new List<string>(), exitCode: 1, stderr: "Error: 401 invalid authentication"));

        var ex = await Assert.ThrowsAsync<OneShotException>(
            () => Runner(pipes, NoPty()).RunAsync("task", AIProvider.Claude, "claude-sonnet-5", "/repo"));

        Assert.Equal(OneShotErrorKind.AuthRequired, ex.Kind);
    }

    [Fact]
    public async Task ClaudeThrowsFailedWithStderrOnAnOrdinaryError()
    {
        var pipes = new FakeProcessLauncher((_, _) =>
            new FakeChildProcess(new List<string>(), exitCode: 1, stderr: "boom"));

        var ex = await Assert.ThrowsAsync<OneShotException>(
            () => Runner(pipes, NoPty()).RunAsync("task", AIProvider.Claude, "claude-sonnet-5", "/repo"));

        Assert.Equal(OneShotErrorKind.Failed, ex.Kind);
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task ClaudeRecordsADiagnosticOnFailureWithThePromptRedacted()
    {
        var records = new List<(string Message, string Level)>();
        var pipes = new FakeProcessLauncher((_, _) =>
            new FakeChildProcess(new List<string>(), exitCode: 1, stderr: "boom"));
        var runner = new ProviderOneShotRunner(pipes, NoPty(), n => $"/usr/bin/{n}",
            recordDiagnostic: (msg, level) => records.Add((msg, level)));
        var prompt = "a secret task description";

        await Assert.ThrowsAsync<OneShotException>(
            () => runner.RunAsync(prompt, AIProvider.Claude, "claude-sonnet-5", "/repo"));

        Assert.Single(records);
        Assert.Equal("ERROR", records[0].Level);
        Assert.Contains("stderr: boom", records[0].Message);
        Assert.Contains("cwd: /repo", records[0].Message);
        Assert.DoesNotContain(prompt, records[0].Message);
        Assert.Contains($"<prompt: {prompt.Length} chars>", records[0].Message);
    }

    [Fact]
    public async Task PtyRecordsADiagnosticOnANonZeroExit()
    {
        var records = new List<(string Message, string Level)>();
        var pty = new FakePseudoConsoleLauncher((_, _) =>
            new FakePseudoConsoleSession(new List<string> { "some error text" }, exitCode: 1));
        var runner = new ProviderOneShotRunner(NoPipes(), pty, n => $"/usr/bin/{n}",
            recordDiagnostic: (msg, level) => records.Add((msg, level)));

        await Assert.ThrowsAsync<OneShotException>(
            () => runner.RunAsync("task", AIProvider.Gemini, "gemini-2.5-pro", "/repo"));

        Assert.Single(records);
        Assert.Contains("exit code 1", records[0].Message);
    }

    [Fact]
    public async Task PtyLaunchFailureIsWrappedAsOneShotExceptionAndRecorded()
    {
        // Real gap found while wiring diagnostics: unlike the Claude path (whose launch
        // failures already flow through OneShotProcess's own try/catch), a PTY launch
        // failure (e.g. ConPTY setup) used to escape as a raw exception instead of the
        // documented OneShotException contract every other failure kind uses.
        var records = new List<(string Message, string Level)>();
        var pty = new FakePseudoConsoleLauncher((_, _) => throw new InvalidOperationException("no ConPTY for you"));
        var runner = new ProviderOneShotRunner(NoPipes(), pty, n => $"/usr/bin/{n}",
            recordDiagnostic: (msg, level) => records.Add((msg, level)));

        var ex = await Assert.ThrowsAsync<OneShotException>(
            () => runner.RunAsync("task", AIProvider.Gemini, "gemini-2.5-pro", "/repo"));

        Assert.Equal(OneShotErrorKind.Failed, ex.Kind);
        Assert.Equal("no ConPTY for you", ex.Message);
        Assert.Single(records);
        Assert.Contains("no ConPTY for you", records[0].Message);
    }

    [Fact]
    public async Task ClaudeFallsBackToStdoutWhenStderrIsEmptyOnAnOrdinaryError()
    {
        // Real bug found via a Windows crash report: with --output-format json, a
        // failure that never reaches a valid JSON result can put its only
        // diagnostic text on stdout instead of stderr — discarding stdout left the
        // user with nothing but "Claude exited with an error."
        var pipes = new FakeProcessLauncher((_, _) =>
            new FakeChildProcess(new List<string> { "the real reason is here" }, exitCode: 1, stderr: ""));

        var ex = await Assert.ThrowsAsync<OneShotException>(
            () => Runner(pipes, NoPty()).RunAsync("task", AIProvider.Claude, "claude-sonnet-5", "/repo"));

        Assert.Equal(OneShotErrorKind.Failed, ex.Kind);
        Assert.Equal("the real reason is here", ex.Message);
    }

    [Fact]
    public async Task ClaudeThrowsFailedWhenOutputIsNotParseableJson()
    {
        var pipes = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string> { "not json" }));

        var ex = await Assert.ThrowsAsync<OneShotException>(
            () => Runner(pipes, NoPty()).RunAsync("task", AIProvider.Claude, "claude-sonnet-5", "/repo"));

        Assert.Equal(OneShotErrorKind.Failed, ex.Kind);
    }

    [Fact]
    public async Task CodexReturnsEstimatedTokensAndCostFromPlainOutput()
    {
        var pty = new FakePseudoConsoleLauncher((_, _) => new FakePseudoConsoleSession(new List<string> { "Here is the answer." }));

        var result = await Runner(NoPipes(), pty).RunAsync("task", AIProvider.Openai, "gpt-5", "/repo");

        Assert.Equal("Here is the answer.", result.Text);
        Assert.True(result.Estimated);
        Assert.True(result.Tokens > 0);
    }

    [Fact]
    public async Task GeminiAuthPromptTerminatesEarlyAndKillsTheSession()
    {
        FakePseudoConsoleSession? spawned = null;
        var pty = new FakePseudoConsoleLauncher((_, _) =>
        {
            spawned = new FakePseudoConsoleSession(new List<string> { "Please sign in to continue" });
            return spawned;
        });

        var ex = await Assert.ThrowsAsync<OneShotException>(
            () => Runner(NoPipes(), pty).RunAsync("task", AIProvider.Gemini, "gemini-2.5-pro", "/repo"));

        Assert.Equal(OneShotErrorKind.AuthRequired, ex.Kind);
        Assert.True(spawned!.Killed);
    }

    [Fact]
    public async Task PtyThrowsFailedOnANonZeroExit()
    {
        var pty = new FakePseudoConsoleLauncher((_, _) =>
            new FakePseudoConsoleSession(new List<string> { "some error text" }, exitCode: 1));

        var ex = await Assert.ThrowsAsync<OneShotException>(
            () => Runner(NoPipes(), pty).RunAsync("task", AIProvider.Gemini, "gemini-2.5-pro", "/repo"));

        Assert.Equal(OneShotErrorKind.Failed, ex.Kind);
        Assert.Contains("error text", ex.Message);
    }

    [Fact]
    public async Task PtyRunKillsTheSessionOnTimeoutAndThrowsTimedOut()
    {
        var pty = new FakePseudoConsoleLauncher((_, _) =>
            new FakePseudoConsoleSession(new List<string> { "thinking..." }, hangAfterLines: true));
        var runner = Runner(NoPipes(), pty, callTimeout: TimeSpan.FromMilliseconds(50));

        var ex = await Assert.ThrowsAsync<OneShotException>(
            () => runner.RunAsync("task", AIProvider.Gemini, "gemini-2.5-pro", "/repo"));

        Assert.Equal(OneShotErrorKind.TimedOut, ex.Kind);
    }

    [Fact]
    public async Task PrefersAnAlternativeBinaryWhenInstalled()
    {
        // Gemini's alternative (Antigravity's "agy") should be tried first —
        // resolveBinary only knows "agy", not "gemini".
        var pty = new FakePseudoConsoleLauncher((_, _) => new FakePseudoConsoleSession(new List<string> { "ok" }));
        Func<string, string?> resolveBinary = n => n == "agy" ? "/usr/bin/agy" : null;

        await Runner(NoPipes(), pty, resolveBinary).RunAsync("task", AIProvider.Gemini, "gemini-2.5-pro", "/repo");

        Assert.Equal("/usr/bin/agy", pty.LastStart!.Value.FileName);
    }
}
