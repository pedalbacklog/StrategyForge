using Coral.Core.Models;
using Coral.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace Coral.Tests;

/// <summary>
/// NOT part of the automated suite — <c>windows-tests.yml</c> excludes
/// <c>Category=Manual</c>, and this one specifically needs a real Windows
/// machine with npm/claude on PATH. First real exercise of Fase 6's
/// <see cref="ProviderInstaller"/>: everything up to this point (URL
/// extraction, install/sign-in/connect orchestration, the Gemini
/// mtime/nudge race) was verified with fakes, never against real npm or a
/// real CLI login. Claude only — matches the rest of this port's
/// single-provider scope (see PORT-PLAN.md §6).
///
/// Two independent pieces, because they carry very different risk:
///
/// <see cref="InstallsTheClaudeCliForReal"/> is safe to run unattended —
/// `npm install -g` is idempotent, so if `claude` is already installed
/// (likely, since <see cref="ManualClaudeRunnerSmokeTest"/> needs it too)
/// this just confirms npm reports it up to date.
///
/// <see cref="SignsInForReal"/> actually runs `claude auth login --claudeai`
/// against a hidden pseudo-console, which starts a REAL browser OAuth flow
/// and, if completed, REPLACES the machine's current Claude login — so it's
/// gated behind an explicit opt-in env var (CORAL_MANUAL_RUN_SIGNIN=1)
/// rather than running just because Category=Manual tests were selected.
/// It's interactive: it prints the login URL and, if Claude asks for a
/// pasted browser code, reads one from stdin.
/// </summary>
[Trait("Category", "Manual")]
public class ManualProviderInstallerSmokeTest
{
    private readonly ITestOutputHelper _output;

    public ManualProviderInstallerSmokeTest(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task InstallsTheClaudeCliForReal()
    {
        var launcher = new RealProcessLauncher();
        InstallEvent? last = null;

        await foreach (var evt in ProviderInstaller.Install(launcher, AIProvider.Claude))
        {
            _output.WriteLine(evt switch
            {
                InstallEvent.Log l => $"[log] {l.Line}",
                InstallEvent.Finished => "[finished]",
                InstallEvent.NeedsNode => "[needs-node]",
                InstallEvent.Failed f => $"[FAILED] {f.Message}",
                _ => $"[{evt.GetType().Name}]",
            });
            last = evt;
        }

        Assert.True(last is InstallEvent.Finished,
            $"Install did not finish cleanly — last event was {last?.GetType().Name ?? "(none)"}. " +
            "See the log lines above. A NeedsNode result means npm itself couldn't be found on PATH.");
    }

    [Fact]
    public async Task SignsInForReal()
    {
        if (Environment.GetEnvironmentVariable("CORAL_MANUAL_RUN_SIGNIN") != "1")
        {
            _output.WriteLine("Skipped: set CORAL_MANUAL_RUN_SIGNIN=1 to run this. It starts a REAL " +
                "browser OAuth flow and, if completed, REPLACES your machine's current Claude login.");
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("Skipped: ConPTY only exists on Windows.");
            return;
        }

        var launcher = new Win32PseudoConsoleLauncher { Diagnostics = _output.WriteLine };
        var input = new LoginInput();
        InstallEvent? last = null;

        await foreach (var evt in ProviderInstaller.SignIn(launcher, AIProvider.Claude, input))
        {
            switch (evt)
            {
                case InstallEvent.Log l:
                    _output.WriteLine($"[log] {l.Line}");
                    if (ProviderInstaller.FirstUrl(l.Line) is { } url)
                        _output.WriteLine($"[url] Open this if it didn't open automatically: {url}");
                    break;
                case InstallEvent.NeedsCode:
                    _output.WriteLine("[needs-code] Paste the code shown in the browser, then press Enter:");
                    var code = Console.ReadLine() ?? "";
                    await input.SubmitAsync(code);
                    break;
                case InstallEvent.Finished:
                    _output.WriteLine("[finished]");
                    break;
                case InstallEvent.Failed f:
                    _output.WriteLine($"[FAILED] {f.Message}");
                    break;
                case InstallEvent.NeedsNode:
                    _output.WriteLine("[needs-node]");
                    break;
            }
            last = evt;
        }

        Assert.True(last is InstallEvent.Finished,
            $"Sign-in did not finish cleanly — last event was {last?.GetType().Name ?? "(none)"}. " +
            "See the log lines above.");
    }
}
