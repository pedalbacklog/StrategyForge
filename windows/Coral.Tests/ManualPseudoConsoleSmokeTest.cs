using Coral.Core.Services;
using Xunit;
using Xunit.Abstractions;
using System.Linq;

namespace Coral.Tests;

/// <summary>
/// NOT part of the automated suite — <c>windows-tests.yml</c> excludes
/// <c>Category=Manual</c>, and this one specifically needs a real Windows
/// machine: ConPTY (<see cref="Win32PseudoConsoleLauncher"/>) is raw kernel32
/// P/Invoke with no cross-platform equivalent, so unlike
/// <c>ManualClaudeRunnerSmokeTest</c> (whose underlying process-spawn plumbing
/// was already smoke-tested for real on Linux via `dotnet`), NOTHING here has
/// executed successfully anywhere yet — this is the first real exercise of
/// it, full stop. Run it explicitly on Windows:
///   dotnet test windows/Coral.Tests/Coral.Tests.csproj -c Release
///     --filter "FullyQualifiedName~ManualPseudoConsoleSmokeTest"
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

        // TEMPORARY: swapped from cmd.exe to powershell.exe to check whether the
        // missing-echo bypass is cmd.exe-specific (it has known quirky heuristics
        // for detecting a "real" console) or affects every console app equally.
        var launcher = new Win32PseudoConsoleLauncher { Diagnostics = _output.WriteLine };
        using var session = launcher.Start("powershell.exe",
            new List<string> { "-NoProfile", "-Command", "Write-Output hello-from-conpty" },
            Environment.CurrentDirectory, new Dictionary<string, string?>());

        // TEMPORARY: dump the raw bytes off the pipe (bypassing line-splitting)
        // to see exactly what conhost writes, unfiltered — see
        // Win32PseudoConsoleSession.ReadRawOutputForDiagnosticsAsync. Remove this
        // block (and go back to the line-based read below) once we understand
        // why the echoed text isn't coming through ReadOutputLinesAsync.
        if (session is Win32PseudoConsoleSession diagnosticSession)
        {
            var raw = await diagnosticSession.ReadRawOutputForDiagnosticsAsync(8192, TimeSpan.FromSeconds(8));
            _output.WriteLine($"Raw bytes read: {raw.Length}");
            for (var offset = 0; offset < raw.Length; offset += 16)
            {
                var chunk = raw.Skip(offset).Take(16).ToArray();
                var hex = string.Join(' ', chunk.Select(b => b.ToString("X2")));
                var ascii = new string(chunk.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray());
                _output.WriteLine($"{offset:X4}  {hex,-47}  {ascii}");
            }
        }

        var exitCode = await session.WaitForExitAsync(CancellationToken.None);
        _output.WriteLine($"Exit code: {exitCode}");
    }
}
