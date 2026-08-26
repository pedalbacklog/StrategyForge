namespace Coral.Core.Services;

/// <summary>A process running attached to a pseudo-console (a real terminal, not
/// a plain pipe) — abstracted the same way <see cref="IChildProcess"/> is, so
/// higher-level login-flow logic (read a URL out of the output, write an auth
/// code back, detect success) can eventually be unit-tested without a real
/// Windows pseudo-console. The only implementation that touches one is
/// <see cref="Win32PseudoConsoleLauncher"/>.</summary>
public interface IPseudoConsoleSession : IDisposable
{
    /// <summary>Async line-by-line terminal output. Completes when the pseudo-console
    /// closes (the child exited and released it) or throws
    /// <see cref="OperationCanceledException"/> when <paramref name="ct"/> fires.</summary>
    IAsyncEnumerable<string> ReadOutputLinesAsync(CancellationToken ct);

    /// <summary>Write a line to the session's input (the child's stdin, as if
    /// typed at the terminal) — e.g. pasting a browser auth code back in.</summary>
    Task WriteLineAsync(string line, CancellationToken ct);

    /// <summary>Await process exit and return its exit code.</summary>
    Task<int> WaitForExitAsync(CancellationToken ct);

    /// <summary>Terminate the process if still running. Best-effort — never throws.</summary>
    void Kill();
}

/// <summary>Spawns a process attached to a hidden pseudo-console — the Windows
/// counterpart of the macOS original's <c>openpty</c> usage: a CLI login that
/// insists on a real TTY (like `claude auth login`) behaves normally, with no
/// visible terminal window.</summary>
public interface IPseudoConsoleLauncher
{
    /// <summary><paramref name="columns"/>/<paramref name="rows"/> size the
    /// virtual terminal — large enough that a full-screen TUI (e.g. Gemini's
    /// first-run login) can render and navigate.</summary>
    IPseudoConsoleSession Start(string fileName, IReadOnlyList<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string?> environmentOverrides,
        short columns = 160, short rows = 48);
}
