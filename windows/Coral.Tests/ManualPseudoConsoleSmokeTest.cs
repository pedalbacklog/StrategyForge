using Coral.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace Coral.Tests;

/// <summary>
/// NOT part of the automated suite — <c>windows-tests.yml</c> excludes
/// <c>Category=Manual</c>, and this one specifically needs a real Windows
/// machine: ConPTY (<see cref="Win32PseudoConsoleLauncher"/>) is raw kernel32
/// P/Invoke with no cross-platform equivalent. Confirmed passing on real
/// Windows 2026-08-06 (see <c>PORT-PLAN.md</c> §10) after fixing a real
/// <c>STATUS_DLL_INIT_FAILED</c> bug. Run it explicitly on Windows:
///   dotnet test windows/Coral.Tests/Coral.Tests.csproj -c Release
///     --filter "FullyQualifiedName~ManualPseudoConsoleSmokeTest"
///
/// IMPORTANT if running this manually and it fails to see the echoed text:
/// Windows Terminal (and other terminal hosts that themselves run their shell
/// over a nested ConPTY) can "pass through" a further-nested ConPTY session's
/// rendering directly to the outer terminal instead of relaying it through
/// this process's own output pipe — the pipe then only ever carries the
/// initial win32-input-mode/focus-tracking handshake, nothing else, even
/// though the child ran and exited normally. This is a real, confirmed
/// behavior (reproduced identically with both cmd.exe and powershell.exe,
/// resolved by re-running from the legacy console host instead), not a bug
/// in this code — and it doesn't affect the real app, which is never
/// launched from a terminal in the first place. If this test ever seems to
/// fail this way, re-run it from a legacy conhost window (Win+R -> cmd)
/// rather than from inside Windows Terminal before assuming it's a regression.
/// </summary>
[Trait("Category", "Manual")]
public class ManualPseudoConsoleSmokeTest
{
    private readonly ITestOutputHelper _output;

    public ManualPseudoConsoleSmokeTest(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task SpawnsARealProcessAttachedToAPseudoConsole()
    {
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("Skipped: ConPTY only exists on Windows.");
            return;
        }

        var launcher = new Win32PseudoConsoleLauncher { Diagnostics = _output.WriteLine };
        using var session = launcher.Start("cmd.exe", new List<string> { "/c", "echo hello-from-conpty" },
            Environment.CurrentDirectory, new Dictionary<string, string?>());

        var sawExpectedText = false;
        await foreach (var line in session.ReadOutputLinesAsync(CancellationToken.None))
        {
            _output.WriteLine(line);
            if (line.Contains("hello-from-conpty")) sawExpectedText = true;
        }

        var exitCode = await session.WaitForExitAsync(CancellationToken.None);
        _output.WriteLine($"Exit code: {exitCode}");

        Assert.True(sawExpectedText,
            "Never saw the echoed text in the pseudo-console output — report the lines printed above.");
        Assert.Equal(0, exitCode);
    }

    /// <summary>Closes a real coverage gap flagged in <c>PORT-PLAN.md</c> §9:
    /// <see cref="SpawnsARealProcessAttachedToAPseudoConsole"/> above only
    /// ever ran a one-shot <c>echo</c> with no stdin involved, so
    /// <see cref="IPseudoConsoleSession.WriteLineAsync"/> — the exact
    /// mechanism <c>LoginInput.SubmitAsync</c> uses to deliver a pasted
    /// browser auth code to a real CLI's stdin — had never actually been
    /// exercised against a real ConPTY session, only against fakes in
    /// <c>ConnectViewModelTests</c>. Both real Claude sign-ins run so far
    /// resolved via loopback before <c>NeedsCode</c> ever fired (see
    /// PORT-PLAN.md §10), so that specific end-to-end path still can't be
    /// forced deterministically — it depends on network conditions this
    /// port doesn't control. What CAN be tested deterministically, without
    /// any CLI/network dependency at all, is the write itself: pipe a line
    /// in and confirm a real child process reading its own stdin actually
    /// receives it. <c>findstr /r .</c> (no file argument) is a plain
    /// stdin-echo — no <c>set /p</c>/delayed-expansion timing traps to
    /// worry about. <c>findstr</c> never reaches EOF on its own here (the
    /// ConPTY input pipe stays open), so this reads only until it's seen
    /// what it came for, then kills the session instead of waiting for a
    /// natural exit — unlike every other manual smoke test here.</summary>
    [Fact]
    public async Task WriteLineAsyncDeliversInputToARealChildProcessStdin()
    {
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("Skipped: ConPTY only exists on Windows.");
            return;
        }

        var launcher = new Win32PseudoConsoleLauncher { Diagnostics = _output.WriteLine };
        using var session = launcher.Start("cmd.exe", new List<string> { "/c", "findstr /r ." },
            Environment.CurrentDirectory, new Dictionary<string, string?>());

        const string payload = "hello-from-writelineasync";
        await session.WriteLineAsync(payload, CancellationToken.None);

        var sawEcho = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await foreach (var line in session.ReadOutputLinesAsync(cts.Token))
            {
                _output.WriteLine(line);
                if (line.Contains(payload))
                {
                    sawEcho = true;
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Handled below via sawEcho staying false — the assertion message
            // explains what to check.
        }
        finally
        {
            session.Kill();
        }

        Assert.True(sawEcho,
            $"Never saw '{payload}' echoed back — either the write didn't reach the child's stdin, or " +
            "the read timed out after 10s. Report the lines printed above. See this test's own doc comment " +
            "if this fails on Windows Terminal specifically — the same nested-ConPTY passthrough caveat as " +
            "the test above may apply.");
    }
}
