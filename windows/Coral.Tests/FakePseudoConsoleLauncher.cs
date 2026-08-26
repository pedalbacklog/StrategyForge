using System.Runtime.CompilerServices;
using Coral.Core.Services;

namespace Coral.Tests;

/// <summary>Fake <see cref="IPseudoConsoleSession"/> — an in-memory simulated
/// pseudo-console session, so ProviderInstaller's sign-in ORCHESTRATION
/// (URL detection, Antigravity-migration handling, exit-code/creds-mtime
/// race) can be unit-tested without a real Windows ConPTY.</summary>
public sealed class FakePseudoConsoleSession : IPseudoConsoleSession
{
    private readonly List<string> _lines;
    private readonly TimeSpan _delayBetweenLines;
    private readonly int _exitCode;
    private readonly bool _hangAfterLines;

    public bool Killed { get; private set; }
    public List<string> WrittenLines { get; } = new();

    /// <param name="hangAfterLines">True simulates a CLI that doesn't exit on
    /// its own once its output is done (e.g. Gemini's first-run TUI) — the
    /// output stream only ends once <see cref="Kill"/> is called.</param>
    public FakePseudoConsoleSession(List<string> lines, int exitCode = 0,
        TimeSpan? delayBetweenLines = null, bool hangAfterLines = false)
    {
        _lines = lines;
        _exitCode = exitCode;
        _delayBetweenLines = delayBetweenLines ?? TimeSpan.Zero;
        _hangAfterLines = hangAfterLines;
    }

    public async IAsyncEnumerable<string> ReadOutputLinesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var line in _lines)
        {
            if (Killed) yield break;
            if (_delayBetweenLines > TimeSpan.Zero) await Task.Delay(_delayBetweenLines, ct);
            ct.ThrowIfCancellationRequested();
            if (Killed) yield break;
            yield return line;
        }
        if (!_hangAfterLines) yield break;
        while (!Killed)
        {
            await Task.Delay(15, ct);
        }
    }

    public Task WriteLineAsync(string line, CancellationToken ct)
    {
        WrittenLines.Add(line);
        return Task.CompletedTask;
    }

    public Task<int> WaitForExitAsync(CancellationToken ct) => Task.FromResult(_exitCode);

    public void Kill() => Killed = true;

    public void Dispose() { }
}

/// <summary>Builds a <see cref="FakePseudoConsoleSession"/> regardless of what
/// ProviderInstaller asks to launch — records the launch args for
/// assertions, matching <see cref="FakeProcessLauncher"/>'s shape.</summary>
public sealed class FakePseudoConsoleLauncher : IPseudoConsoleLauncher
{
    private readonly Func<string, IReadOnlyList<string>, FakePseudoConsoleSession> _factory;
    public (string FileName, IReadOnlyList<string> Args, string WorkingDirectory,
        IReadOnlyDictionary<string, string?> Env)? LastStart { get; private set; }

    public FakePseudoConsoleLauncher(Func<string, IReadOnlyList<string>, FakePseudoConsoleSession> factory) =>
        _factory = factory;

    public IPseudoConsoleSession Start(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string?> environmentOverrides, short columns = 160, short rows = 48)
    {
        LastStart = (fileName, arguments, workingDirectory, environmentOverrides);
        return _factory(fileName, arguments);
    }
}
