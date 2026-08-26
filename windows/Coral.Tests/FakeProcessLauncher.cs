using System.Runtime.CompilerServices;
using Coral.Core.Services;

namespace Coral.Tests;

/// <summary>Fake <see cref="IChildProcess"/>/<see cref="IProcessLauncher"/> — an
/// in-memory simulated child process, so ClaudeRunner's streaming/watchdog/
/// cancellation ORCHESTRATION can be unit-tested without spawning a real OS
/// process. Real process I/O plumbing is covered separately by
/// RealProcessLauncherTests.</summary>
public sealed class FakeChildProcess : IChildProcess
{
    private readonly List<string> _lines;
    private readonly TimeSpan _delayBetweenLines;
    private readonly TimeSpan? _hangForever;
    private readonly int _exitCode;
    private readonly string _stderr;

    public bool Killed { get; private set; }
    public string? StartedWithFileName { get; init; }
    public IReadOnlyList<string>? StartedWithArgs { get; init; }
    public IReadOnlyDictionary<string, string?>? StartedWithEnv { get; init; }

    public FakeChildProcess(List<string> lines, int exitCode = 0, string stderr = "",
        TimeSpan? delayBetweenLines = null, TimeSpan? hangForever = null)
    {
        _lines = lines;
        _exitCode = exitCode;
        _stderr = stderr;
        _delayBetweenLines = delayBetweenLines ?? TimeSpan.Zero;
        _hangForever = hangForever;
    }

    public async IAsyncEnumerable<string> ReadStandardOutputLinesAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var line in _lines)
        {
            if (_delayBetweenLines > TimeSpan.Zero) await Task.Delay(_delayBetweenLines, ct);
            ct.ThrowIfCancellationRequested();
            yield return line;
        }
        if (_hangForever.HasValue)
        {
            // Simulate a wedged process: never produces another line until
            // cancelled (by the watchdog or the caller).
            await Task.Delay(_hangForever.Value, ct);
        }
    }

    public Task<string> ReadStandardErrorToEndAsync() => Task.FromResult(_stderr);

    public Task<int> WaitForExitAsync(CancellationToken ct) => Task.FromResult(_exitCode);

    public void Kill() => Killed = true;

    public void Dispose() { }
}

/// <summary>Builds a <see cref="FakeChildProcess"/> regardless of what
/// ClaudeRunner asks to launch — records the launch args for assertions.</summary>
public sealed class FakeProcessLauncher : IProcessLauncher
{
    private readonly Func<string, IReadOnlyList<string>, FakeChildProcess> _factory;
    public (string FileName, IReadOnlyList<string> Args, string WorkingDirectory,
        IReadOnlyDictionary<string, string?> Env)? LastStart { get; private set; }

    public FakeProcessLauncher(Func<string, IReadOnlyList<string>, FakeChildProcess> factory) => _factory = factory;

    public IChildProcess Start(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string?> environmentOverrides)
    {
        LastStart = (fileName, arguments, workingDirectory, environmentOverrides);
        return _factory(fileName, arguments);
    }
}
