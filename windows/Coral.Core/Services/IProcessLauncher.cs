namespace Coral.Core.Services;

/// <summary>A running child process's async output — abstracted so
/// <see cref="ClaudeRunner"/>'s streaming/watchdog/cancellation orchestration can
/// be unit-tested without spawning a real OS process. The only implementation
/// that touches a real process is <see cref="RealProcessLauncher"/>; tests use a
/// fake (Coral.Tests' FakeProcessLauncher).</summary>
public interface IChildProcess : IDisposable
{
    /// <summary>Async line-by-line stdout. Completes when the process closes
    /// stdout (normal exit) or throws <see cref="OperationCanceledException"/>
    /// when <paramref name="ct"/> fires mid-read.</summary>
    IAsyncEnumerable<string> ReadStandardOutputLinesAsync(CancellationToken ct);

    /// <summary>All stderr accumulated so far. Call after the process has
    /// exited for the complete text.</summary>
    Task<string> ReadStandardErrorToEndAsync();

    /// <summary>Await process exit and return its exit code.</summary>
    Task<int> WaitForExitAsync(CancellationToken ct);

    /// <summary>Terminate the process (and any children it spawned) if still
    /// running. Best-effort — never throws.</summary>
    void Kill();
}

/// <summary>Spawns a child process, no shell — arguments go through
/// <c>ArgumentList</c>, never a concatenated command string, so a prompt or
/// path containing shell metacharacters can't inject anything.</summary>
public interface IProcessLauncher
{
    /// <summary><paramref name="environmentOverrides"/> is applied ON TOP OF the
    /// inherited environment: a null value REMOVES that key (matching Swift's
    /// <c>env[key] = nil</c>), a non-null value sets/overrides it. Everything
    /// else the child would normally inherit is left alone.</summary>
    IChildProcess Start(string fileName, IReadOnlyList<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string?> environmentOverrides);
}
