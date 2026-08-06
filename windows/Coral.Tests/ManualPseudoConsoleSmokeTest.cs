using Coral.Core.Services;
using Xunit;
using Xunit.Abstractions;

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

        var launcher = new Win32PseudoConsoleLauncher();
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
}
