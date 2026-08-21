using Coral.Core.Models;
using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of StrategyForgeTests/CodeArenaEngineTests.swift, adapted
/// to TeamRunEngine's single-contestant shape (no multi-contestant racing —
/// see TeamRunEngine.cs's doc comment for why the full Arena wasn't
/// ported). Against a REAL temp git repo and REAL `git`, same as the Swift
/// original — TeamRunEngine chains several git operations together
/// (worktree add, commit, diff, commit again, merge/remove), so a real
/// integration test here is worth much more than mocking each call
/// individually (CrossProviderEditorTests already covers that lower layer
/// with fakes).</summary>
public class TeamRunEngineTests
{
    /// <summary>A write-capable fake runner: writes a per-provider file into
    /// the given cwd (the worktree), so isolation + diff capture are
    /// checkable without a real CLI. Mirrors the Swift original's
    /// WritingRunner.</summary>
    private sealed class WritingRunner : IOneShotRunner
    {
        public HashSet<AIProvider> Failing { get; init; } = new();

        public Task<OneShotResult> RunAsync(string prompt, AIProvider provider, string model, string? cwd,
            CancellationToken ct = default)
        {
            if (Failing.Contains(provider)) throw new OneShotException(OneShotErrorKind.Failed, $"{provider.DisplayName()} down");
            if (cwd is not null) File.WriteAllText(Path.Combine(cwd, $"{provider.ToKey()}.txt"), $"edit by {provider.DisplayName()}\n");
            return Task.FromResult(new OneShotResult("done", 10, 0.01, provider, model));
        }
    }

    private static async Task RunGitAsync(string dir, params string[] args)
    {
        var launcher = new RealProcessLauncher();
        var fullArgs = new List<string> { "-c", "user.name=t", "-c", "user.email=t@t.t", "-c", "commit.gpgsign=false" };
        fullArgs.AddRange(args);
        using var child = launcher.Start("git", fullArgs, dir, new Dictionary<string, string?>());
        await foreach (var _ in child.ReadStandardOutputLinesAsync(CancellationToken.None)) { }
        await child.WaitForExitAsync(CancellationToken.None);
    }

    private static async Task<string> MakeRepoAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"coral-teamrun-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await RunGitAsync(dir, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(dir, "README.md"), "seed\n");
        await RunGitAsync(dir, "add", ".");
        await RunGitAsync(dir, "commit", "-q", "-m", "base");
        return dir;
    }

    private static async Task<int> ChangedFileCountAsync(string dir)
    {
        var launcher = new RealProcessLauncher();
        using var child = launcher.Start("git", new List<string> { "status", "--porcelain" }, dir, new Dictionary<string, string?>());
        var lines = new List<string>();
        await foreach (var line in child.ReadStandardOutputLinesAsync(CancellationToken.None)) lines.Add(line);
        await child.WaitForExitAsync(CancellationToken.None);
        return lines.Count(l => l.Trim().Length > 0);
    }

    private static Strategy ClaudeTeam() => new("Test", "", new List<AgentRole>
    {
        new("lead", RoleKind.Orchestrator, ClaudeModel.Sonnet5, "", "", isOrchestrator: true),
    }, "");

    [Fact]
    public async Task RunIsolatesEditsAndLeavesTheUserTreeClean()
    {
        var repo = await MakeRepoAsync();
        try
        {
            var launcher = new RealProcessLauncher();
            var outcome = await TeamRunEngine.RunAsync("add a file", repo, ClaudeTeam(), new WritingRunner(), launcher, resolveGitBinary: n => n == "git" ? "git" : null);

            Assert.Equal(TeamRunState.Done, outcome.State);
            Assert.Contains("claude.txt", outcome.Diff);
            Assert.True(outcome.ProducedChanges);
            Assert.Equal(0, await ChangedFileCountAsync(repo)); // user tree untouched

            await TeamRunEngine.DiscardAsync(launcher, repo, outcome, resolveGitBinary: n => n == "git" ? "git" : null);
            Assert.False(Directory.Exists(outcome.WorktreePath));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyMergesTheRunIntoTheUserTree()
    {
        var repo = await MakeRepoAsync();
        try
        {
            var launcher = new RealProcessLauncher();
            var outcome = await TeamRunEngine.RunAsync("add a file", repo, ClaudeTeam(), new WritingRunner(), launcher, resolveGitBinary: n => n == "git" ? "git" : null);

            var (ok, _) = await TeamRunEngine.ApplyAsync(launcher, repo, outcome, resolveGitBinary: n => n == "git" ? "git" : null);

            Assert.True(ok);
            Assert.True(File.Exists(Path.Combine(repo, "claude.txt")));
            Assert.False(Directory.Exists(outcome.WorktreePath));
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task AFailingRunnerReportsFailedWithoutTouchingTheUserTree()
    {
        var repo = await MakeRepoAsync();
        try
        {
            var launcher = new RealProcessLauncher();
            var runner = new WritingRunner { Failing = { AIProvider.Claude } };

            var outcome = await TeamRunEngine.RunAsync("x", repo, ClaudeTeam(), runner, launcher, resolveGitBinary: n => n == "git" ? "git" : null);

            Assert.Equal(TeamRunState.Failed, outcome.State);
            Assert.Contains("down", outcome.Error);
            Assert.Equal(0, await ChangedFileCountAsync(repo));

            await TeamRunEngine.DiscardAsync(launcher, repo, outcome, resolveGitBinary: n => n == "git" ? "git" : null);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }

    [Fact]
    public async Task ScaffoldingIsCommittedAsABaselineSoTheDiffIsOnlyCodeWork()
    {
        var repo = await MakeRepoAsync();
        try
        {
            var launcher = new RealProcessLauncher();
            var outcome = await TeamRunEngine.RunAsync("edit", repo, StrategyLibrary.Solo(), new WritingRunner(), launcher, resolveGitBinary: n => n == "git" ? "git" : null);

            Assert.Equal(TeamRunState.Done, outcome.State);
            Assert.Contains("claude.txt", outcome.Diff);       // the team's code work
            Assert.DoesNotContain("CLAUDE.md", outcome.Diff);  // scaffolding was committed as baseline

            await TeamRunEngine.DiscardAsync(launcher, repo, outcome, resolveGitBinary: n => n == "git" ? "git" : null);
        }
        finally
        {
            Directory.Delete(repo, recursive: true);
        }
    }
}
