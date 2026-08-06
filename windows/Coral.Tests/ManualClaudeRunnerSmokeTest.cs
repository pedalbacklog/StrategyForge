using Coral.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace Coral.Tests;

/// <summary>
/// NOT part of the automated suite — <c>windows-tests.yml</c> excludes
/// <c>Category=Manual</c> because CI has no `claude` CLI installed or logged
/// in. This is the first real, end-to-end exercise of Fase 3's
/// <see cref="ClaudeRunner"/> against the actual CLI: everything up to this
/// point (ClaudeStreamParser, BinaryResolver, ClaudeRunArgs, ClaudeRunner's
/// orchestration) was verified with fakes and a smoke test against `dotnet`
/// itself, never against `claude`. See windows/README.md "Testing against the
/// real claude CLI" for how to run this.
/// </summary>
[Trait("Category", "Manual")]
public class ManualClaudeRunnerSmokeTest
{
    private readonly ITestOutputHelper _output;

    public ManualClaudeRunnerSmokeTest(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task StreamsARealTurnFromTheClaudeCli()
    {
        // Point CORAL_MANUAL_REPO_PATH at any real folder (a git repo isn't
        // required for this smoke test) to run `claude` there instead; falls
        // back to the current directory.
        var repoPath = Environment.GetEnvironmentVariable("CORAL_MANUAL_REPO_PATH")
            ?? Directory.GetCurrentDirectory();
        _output.WriteLine($"Repo path: {repoPath}");

        var resolved = BinaryResolver.Resolve("claude");
        _output.WriteLine($"Resolved `claude` binary: {resolved ?? "(not found)"}");
        Assert.False(string.IsNullOrEmpty(resolved),
            "BinaryResolver couldn't find `claude` on PATH or in %APPDATA%\\npm. " +
            "Install it first: npm install -g @anthropic-ai/claude-code, then `claude` once to log in.");

        var launcher = new RealProcessLauncher();
        var sawFinished = false;
        var sawFailed = false;

        await foreach (var evt in ClaudeRunner.Stream(launcher, "claude", repoPath,
            "Reply with exactly one word: pong", "claude-sonnet-5",
            Guid.NewGuid().ToString(), resume: false, permissionMode: "default",
            inactivityTimeout: TimeSpan.FromMinutes(2)))
        {
            _output.WriteLine(evt switch
            {
                ChatEvent.AssistantText t => $"[assistant] {t.Text}",
                ChatEvent.AssistantDelta d => $"[delta] {d.Text}",
                ChatEvent.Tool tool => $"[tool] {tool.Name} {tool.Detail}",
                ChatEvent.Usage u => $"[usage] {u.Tokens} tokens, ${u.CostUsd.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)}",
                ChatEvent.Finished => "[finished]",
                ChatEvent.Failed f => $"[FAILED] {f.Message}",
                _ => $"[{evt.GetType().Name}]",
            });

            if (evt is ChatEvent.Finished) sawFinished = true;
            if (evt is ChatEvent.Failed) sawFailed = true;
        }

        Assert.False(sawFailed, "See the [FAILED] line above for the reason.");
        Assert.True(sawFinished, "Stream ended without a Finished or Failed event — report this.");
    }
}
