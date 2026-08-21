namespace Coral.Core.Services;

/// <summary>Shared runner for one-shot ("run to completion, read the output")
/// subprocesses — the pattern both <see cref="CodeGit"/> (<c>git</c>) and
/// <see cref="GitHubCLI"/> (<c>gh</c>) need. Reads stdout and stderr
/// CONCURRENTLY, not stdout-then-stderr: a command with enough stderr output
/// (a warning, progress info) could otherwise fill the OS pipe buffer while
/// nobody's draining it and deadlock the process — the Swift originals
/// sidestep this by merging stdout+stderr into one pipe;
/// <see cref="IProcessLauncher"/> keeps them separate (the more idiomatic
/// .NET shape), so this reads both sides at once instead.</summary>
internal static class OneShotProcess
{
    public static async Task<(bool Ok, string Stdout, string Stderr)> RunAsync(
        IProcessLauncher launcher, string fileName, IReadOnlyList<string> args, string workingDirectory,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null, CancellationToken ct = default)
    {
        IChildProcess? child = null;
        string? launchFailure = null;
        try
        {
            child = launcher.Start(fileName, args, workingDirectory, environmentOverrides ?? new Dictionary<string, string?>());
        }
        catch (Exception ex)
        {
            launchFailure = ex.Message;
        }
        if (launchFailure is not null) return (false, "", launchFailure);

        var live = child!;
        using (live)
        {
            // Guarantee the child is never left running past this call — a
            // cancelled `ct` (a caller's watchdog, a UI Cancel) otherwise
            // orphans the process: cancelling an await here throws, but
            // Dispose() alone only releases the .NET Process handle, it
            // doesn't send a kill signal. Kill() is a documented best-effort
            // no-op once the process has already exited normally, so this
            // adds no behavior on the happy path.
            try
            {
                var stdoutTask = CollectLinesAsync(live.ReadStandardOutputLinesAsync(ct), ct);
                var stderrTask = live.ReadStandardErrorToEndAsync();
                var stdoutLines = await stdoutTask;
                var stderr = await stderrTask;
                var exitCode = await live.WaitForExitAsync(ct);
                return (exitCode == 0, string.Join("\n", stdoutLines), stderr);
            }
            finally
            {
                live.Kill();
            }
        }
    }

    private static async Task<List<string>> CollectLinesAsync(IAsyncEnumerable<string> source, CancellationToken ct)
    {
        var lines = new List<string>();
        await foreach (var line in source.WithCancellation(ct)) lines.Add(line);
        return lines;
    }

    /// <summary>Approximates a single merged stdout+stderr pipe for
    /// error-surfacing callers: the tools this runs (git, gh) put their
    /// actual error text on stderr almost always, so put it last (after any
    /// stdout) rather than trying to reproduce exact interleaving, which two
    /// separate streams can't give us anyway.</summary>
    public static string CombineOutput(string stdout, string stderr) =>
        stdout.Length > 0 && stderr.Length > 0 ? $"{stdout}\n{stderr}" : stdout + stderr;
}
