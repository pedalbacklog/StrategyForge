using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Tests for ClaudeRunner.Stream's ORCHESTRATION logic — streaming
/// order, exit-code handling, the inactivity watchdog, and cancellation —
/// against a FakeProcessLauncher, so none of this spawns a real OS process.
/// RealProcessLauncherTests separately smoke-tests the actual
/// System.Diagnostics.Process plumbing. Neither exercises the real `claude`
/// CLI — see windows/PORT-PLAN.md Fase 3.</summary>
public class ClaudeRunnerTests
{
    private static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events) list.Add(e);
        return list;
    }

    [Fact]
    public async Task StreamsParsedEventsThenFinishesOnCleanExit()
    {
        var lines = new List<string>
        {
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Hi"}]}}""",
            """{"type":"result","subtype":"success","result":"done"}""",
        };
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(lines, exitCode: 0));

        var events = await Collect(ClaudeRunner.Stream(launcher, "claude", "/repo", "hello",
            "claude-sonnet-5", "sess-1", resume: false, permissionMode: "default",
            resolveBinary: _ => "/resolved/claude"));

        // The "result" line itself parses to a Finished event, and the clean
        // process exit appends its own — matching the Swift original, whose
        // terminationHandler unconditionally reports finished/failed regardless
        // of what the last parsed line already said.
        Assert.Equal(3, events.Count);
        Assert.IsType<ChatEvent.AssistantText>(events[0]);
        Assert.IsType<ChatEvent.Finished>(events[1]);
        Assert.IsType<ChatEvent.Finished>(events[2]);
    }

    [Fact]
    public async Task NonZeroExitYieldsFailedWithStderr()
    {
        var launcher = new FakeProcessLauncher((_, _) =>
            new FakeChildProcess(new List<string>(), exitCode: 1, stderr: "boom"));

        var events = await Collect(ClaudeRunner.Stream(launcher, "claude", "/repo", "hello",
            "claude-sonnet-5", "sess-1", resume: false, permissionMode: "default",
            resolveBinary: _ => "/resolved/claude"));

        var failed = Assert.Single(events);
        var f = Assert.IsType<ChatEvent.Failed>(failed);
        Assert.Equal("boom", f.Message);
    }

    [Fact]
    public async Task NonZeroExitWithNoStderrReportsTheExitCode()
    {
        var launcher = new FakeProcessLauncher((_, _) =>
            new FakeChildProcess(new List<string>(), exitCode: 7, stderr: ""));

        var events = await Collect(ClaudeRunner.Stream(launcher, "claude", "/repo", "hello",
            "claude-sonnet-5", "sess-1", resume: false, permissionMode: "default",
            resolveBinary: _ => "/resolved/claude"));

        var f = Assert.IsType<ChatEvent.Failed>(Assert.Single(events));
        Assert.Contains("7", f.Message);
    }

    [Fact]
    public async Task MissingBinaryYieldsFailedWithoutLaunching()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));

        var events = await Collect(ClaudeRunner.Stream(launcher, "claude", "/repo", "hello",
            "claude-sonnet-5", "sess-1", resume: false, permissionMode: "default",
            resolveBinary: _ => null));

        var f = Assert.IsType<ChatEvent.Failed>(Assert.Single(events));
        Assert.Contains("claude", f.Message);
        Assert.Null(launcher.LastStart);
    }

    [Fact]
    public async Task InactivityWatchdogKillsAStalledRunAndReportsIt()
    {
        FakeChildProcess? spawned = null;
        var launcher = new FakeProcessLauncher((_, _) =>
        {
            spawned = new FakeChildProcess(new List<string>(), hangForever: TimeSpan.FromSeconds(30));
            return spawned;
        });

        var events = await Collect(ClaudeRunner.Stream(launcher, "claude", "/repo", "hello",
            "claude-sonnet-5", "sess-1", resume: false, permissionMode: "default",
            inactivityTimeout: TimeSpan.FromMilliseconds(50),
            resolveBinary: _ => "/resolved/claude"));

        var f = Assert.IsType<ChatEvent.Failed>(Assert.Single(events));
        Assert.Contains("stalled", f.Message);
        Assert.True(spawned?.Killed);
    }

    [Fact]
    public async Task ActivityResetsTheWatchdogSoALongQuietRunSurvives()
    {
        // Three lines, each ~20ms apart, watchdog at 400ms (20x margin) — if
        // bump() didn't reset the clock this would stall; it must complete
        // normally instead. The margin needs to be wide: a 40ms/100ms (2.5x)
        // gap flaked for real on a loaded CI runner (scheduler jitter pushed
        // one inter-line gap past the watchdog window), not just in theory.
        var lines = Enumerable.Range(0, 3)
            .Select(_ => """{"type":"result","subtype":"success","result":"ok"}""")
            .ToList();
        var launcher = new FakeProcessLauncher((_, _) =>
            new FakeChildProcess(lines, exitCode: 0, delayBetweenLines: TimeSpan.FromMilliseconds(20)));

        var events = await Collect(ClaudeRunner.Stream(launcher, "claude", "/repo", "hello",
            "claude-sonnet-5", "sess-1", resume: false, permissionMode: "default",
            inactivityTimeout: TimeSpan.FromMilliseconds(400),
            resolveBinary: _ => "/resolved/claude"));

        // 3 "finished" results (one per success line) + the final clean-exit Finished.
        Assert.Equal(4, events.Count);
        Assert.All(events, e => Assert.True(e is ChatEvent.Finished));
    }

    [Fact]
    public async Task CallerCancellationKillsTheProcessWithoutAStallMessage()
    {
        FakeChildProcess? spawned = null;
        var launcher = new FakeProcessLauncher((_, _) =>
        {
            spawned = new FakeChildProcess(new List<string>(), hangForever: TimeSpan.FromSeconds(30));
            return spawned;
        });
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(30));

        var events = await Collect(ClaudeRunner.Stream(launcher, "claude", "/repo", "hello",
            "claude-sonnet-5", "sess-1", resume: false, permissionMode: "default",
            inactivityTimeout: TimeSpan.FromSeconds(10),
            resolveBinary: _ => "/resolved/claude", ct: cts.Token));

        Assert.Empty(events); // a plain user cancellation yields nothing further
        Assert.True(spawned?.Killed);
    }

    [Fact]
    public async Task LaunchArgsCarryTheResolvedBinaryAndBuiltArgs()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));
        await Collect(ClaudeRunner.Stream(launcher, "claude", "/my/repo", "the prompt",
            "claude-opus-5", "sess-42", resume: true, permissionMode: "acceptEdits",
            resolveBinary: _ => "/resolved/claude"));

        Assert.NotNull(launcher.LastStart);
        var (fileName, args, workingDirectory, env) = launcher.LastStart!.Value;
        Assert.Equal("/resolved/claude", fileName);
        Assert.Equal("/my/repo", workingDirectory);
        Assert.Contains("--resume", args);
        Assert.Contains("sess-42", args);
        Assert.Contains("the prompt", args);
        Assert.True(env.ContainsKey("PATH"));
        Assert.Null(env["ANTHROPIC_API_KEY"]); // stripped, never inherited
    }
}
