using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Coral.Core.Services;

/// <summary>The only <see cref="IProcessLauncher"/> that touches a real OS
/// process — a thin wrapper around <see cref="System.Diagnostics.Process"/>.
/// No shell: arguments go through <c>ArgumentList</c>. Its plumbing is
/// smoke-tested against a real (non-`claude`) executable in Coral.Tests, but a
/// run against the real <c>claude</c> CLI hasn't happened yet — see
/// windows/PORT-PLAN.md Fase 3.</summary>
public sealed class RealProcessLauncher : IProcessLauncher
{
    public IChildProcess Start(string fileName, IReadOnlyList<string> arguments,
        string workingDirectory, IReadOnlyDictionary<string, string?> environmentOverrides)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // Without this, .NET decodes the redirected streams using the
            // console's active code page (on Windows, often a legacy ANSI/OEM
            // page, not UTF-8) — the CLIs here (claude/codex/gemini) all write
            // UTF-8, so anything outside ASCII (á, é, ñ, ¿, …) comes through as
            // mojibake without an explicit encoding.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);

        // EnvironmentVariables starts as a copy of THIS process's environment
        // (inherited) — apply overrides/removals on top of it, matching Swift's
        // `ProcessInfo.processInfo.environment` + mutate-in-place pattern.
        foreach (var (key, value) in environmentOverrides)
        {
            if (value is null) psi.EnvironmentVariables.Remove(key);
            else psi.EnvironmentVariables[key] = value;
        }

        var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to start '{fileName}'.");
        }
        return new RealChildProcess(process);
    }
}

internal sealed class RealChildProcess : IChildProcess
{
    private readonly Process _process;

    public RealChildProcess(Process process) => _process = process;

    public async IAsyncEnumerable<string> ReadStandardOutputLinesAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(ct);
            if (line is null) yield break;
            yield return line;
        }
    }

    public Task<string> ReadStandardErrorToEndAsync() => _process.StandardError.ReadToEndAsync();

    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct);
        return _process.ExitCode;
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best-effort — the process may have exited between the check and the call.
        }
    }

    public void Dispose() => _process.Dispose();
}
