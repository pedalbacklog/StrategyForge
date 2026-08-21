using System.Runtime.CompilerServices;

namespace Coral.Core.Services;

/// <summary>
/// Port of <c>StrategyForge/Services/ClaudeRunner.swift</c>'s <c>stream()</c> —
/// spawns and streams a headless Claude Code run. Orchestration only
/// (binary resolution, argument/environment construction, line-by-line
/// streaming into <see cref="ClaudeStreamParser"/>, an inactivity watchdog,
/// cancellation, exit handling); the actual process I/O is behind
/// <see cref="IProcessLauncher"/> so this is unit-testable without spawning
/// anything real. NOT yet verified against the real <c>claude</c> CLI or on a
/// real Windows machine — see windows/PORT-PLAN.md Fase 3. The Swift original's
/// hand-rolled <c>InactivityWatchdog</c> (a polling <c>DispatchSourceTimer</c>)
/// isn't ported as its own type: <see cref="CancellationTokenSource.CancelAfter"/>
/// already does "reset an inactivity timeout on activity" natively and exactly,
/// so re-arming it on every line is simpler and more precise than polling.
/// </summary>
public static class ClaudeRunner
{
    /// <summary>Stream a single turn. <paramref name="resume"/> continues the
    /// repo's latest Claude Code session so the chat is multi-turn.
    /// <paramref name="launcher"/> and <paramref name="resolveBinary"/> are
    /// injectable so this is unit-testable without spawning anything real —
    /// production callers pass <see cref="RealProcessLauncher"/> and leave
    /// <paramref name="resolveBinary"/> at its default
    /// (<see cref="BinaryResolver.Resolve"/>).</summary>
    public static async IAsyncEnumerable<ChatEvent> Stream(
        IProcessLauncher launcher,
        string binary,
        string repoPath,
        string prompt,
        string model,
        string sessionId,
        bool resume,
        string permissionMode,
        IReadOnlyList<string>? extraDirs = null,
        string? effort = null,
        TimeSpan? inactivityTimeout = null,
        Func<string, string?>? resolveBinary = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        resolveBinary ??= BinaryResolver.Resolve;
        var resolved = resolveBinary(binary);
        if (resolved is null)
        {
            yield return new ChatEvent.Failed(
                "Couldn't find the `claude` binary. Set its full path in Settings.");
            yield break;
        }

        var args = ClaudeRunArgs.Build(prompt, model, sessionId, resume, permissionMode, extraDirs, effort);
        var env = BuildEnvironment(resolved);

        IChildProcess? child = null;
        string? launchFailure = null;
        try
        {
            child = launcher.Start(resolved, args, repoPath, env);
        }
        catch (Exception ex)
        {
            // Most commonly: the resolved path isn't actually runnable.
            launchFailure = ex.Message;
        }
        if (launchFailure is not null)
        {
            yield return new ChatEvent.Failed(launchFailure);
            yield break;
        }

        var live = child!;
        using (live)
        {
            var timeout = inactivityTimeout ?? TimeSpan.FromMinutes(10);
            using var watchdogCts = new CancellationTokenSource();
            if (timeout > TimeSpan.Zero) watchdogCts.CancelAfter(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, watchdogCts.Token);

            var stopped = false;
            var stalled = false;
            await using var lines = live.ReadStandardOutputLinesAsync(linkedCts.Token)
                .GetAsyncEnumerator(linkedCts.Token);
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await lines.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    // The watchdog and the caller share one linked token; tell
                    // them apart so only a genuine stall gets the stall message
                    // (a plain user cancellation yields nothing further — the
                    // caller has already stopped listening).
                    stalled = watchdogCts.IsCancellationRequested && !ct.IsCancellationRequested;
                    stopped = true;
                    break;
                }
                if (!hasNext) break;

                // Any output resets the inactivity clock — a legitimately long,
                // quiet turn (a multi-minute test/build) is never killed; only
                // genuine silence trips the watchdog.
                if (timeout > TimeSpan.Zero) watchdogCts.CancelAfter(timeout);
                foreach (var evt in ClaudeStreamParser.Events(lines.Current))
                {
                    yield return evt;
                }
            }

            if (stopped)
            {
                live.Kill();
                if (stalled)
                {
                    yield return new ChatEvent.Failed(
                        "The turn stalled with no output for 10 minutes and was stopped.");
                }
                yield break;
            }

            var exitCode = await live.WaitForExitAsync(CancellationToken.None);
            if (exitCode != 0)
            {
                var err = (await live.ReadStandardErrorToEndAsync()).Trim();
                yield return new ChatEvent.Failed(err.Length == 0 ? $"claude exited with code {exitCode}" : err);
            }
            else
            {
                yield return new ChatEvent.Finished();
            }
        }
    }

    /// <summary>Build the child environment overrides: an augmented PATH (the
    /// resolved binary's own directory first), the pinned Claude config dir, and
    /// provider auth keys stripped so only what the user configured IN Coral
    /// takes effect — an inherited key from the launching shell would silently
    /// override the subscription login.</summary>
    /// <summary>Internal (not private): also reused by
    /// <see cref="ProviderOneShotRunner"/>'s Claude path — same env-stripping
    /// requirement (a stray inherited API key would silently override the
    /// subscription login), no reason to duplicate it.</summary>
    internal static Dictionary<string, string?> BuildEnvironment(string resolvedBinaryPath)
    {
        var overrides = new Dictionary<string, string?>();
        var binDir = Path.GetDirectoryName(resolvedBinaryPath);
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        overrides["PATH"] = string.IsNullOrEmpty(binDir)
            ? currentPath
            : $"{binDir}{Path.PathSeparator}{currentPath}";

        overrides["ANTHROPIC_API_KEY"] = null;
        overrides["ANTHROPIC_AUTH_TOKEN"] = null;

        var configDir = ResolveClaudeConfigDir();
        if (configDir is not null) overrides["CLAUDE_CONFIG_DIR"] = configDir;

        return overrides;
    }

    /// <summary>The Claude config dir Coral should use, chosen deterministically:
    /// an inherited <c>CLAUDE_CONFIG_DIR</c> that actually holds a login wins;
    /// then the default <c>%USERPROFILE%\.claude</c>. Mirrors
    /// <c>resolveClaudeConfigDir()</c>, minus the macOS-only Xcode
    /// dev-convenience path, which has no Windows equivalent.</summary>
    private static string? ResolveClaudeConfigDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        bool HasLogin(string dir) =>
            File.Exists(Path.Combine(dir, ".credentials.json")) || File.Exists(Path.Combine(dir, ".claude.json"));

        var candidates = new List<string>();
        var inherited = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrEmpty(inherited)) candidates.Add(inherited);
        candidates.Add(Path.Combine(home, ".claude"));

        return candidates.FirstOrDefault(HasLogin) ?? candidates.FirstOrDefault();
    }
}
