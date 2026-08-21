using Coral.Core.Models;
using Coral.Core.Services;
using Coral.Core.ViewModels;
using Xunit;

namespace Coral.Tests;

/// <summary>TeamRunViewModel's own orchestration (IsRunning/StatusMessage/
/// CanApply wiring, blank-task no-op) — TeamRunEngine's actual git behavior
/// is already covered by TeamRunEngineTests, so this reuses the same
/// real-git helper pattern only for the couple of tests that need a real
/// Done outcome to check CanApply/Apply/Discard against.</summary>
public class TeamRunViewModelTests
{
    private sealed class WritingRunner : IOneShotRunner
    {
        public Task<OneShotResult> RunAsync(string prompt, AIProvider provider, string model, string? cwd,
            CancellationToken ct = default)
        {
            if (cwd is not null) File.WriteAllText(Path.Combine(cwd, "claude.txt"), "edit\n");
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
        var dir = Path.Combine(Path.GetTempPath(), $"coral-teamrunvm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await RunGitAsync(dir, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(dir, "README.md"), "seed\n");
        await RunGitAsync(dir, "add", ".");
        await RunGitAsync(dir, "commit", "-q", "-m", "base");
        return dir;
    }

    /// <summary>Same fix as TeamRunEngineTests.cs's — see its doc comment:
    /// git leaves loose objects read-only, and .NET's plain
    /// <c>Directory.Delete(recursive: true)</c> throws on Windows when it
    /// hits one instead of clearing the attribute first.</summary>
    private static void DeleteDirectoryRobustly(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(path, recursive: true);
    }

    private static Strategy ClaudeTeam() => new("Test", "", new List<AgentRole>
    {
        new("lead", RoleKind.Orchestrator, ClaudeModel.Sonnet5, "", "", isOrchestrator: true),
    }, "");

    private static Func<string, string?> GitOnly => n => n == "git" ? "git" : null;

    [Fact]
    public async Task RunIsANoOpOnABlankTask()
    {
        var vm = new TeamRunViewModel("/repo", ClaudeTeam(), new WritingRunner(), new RealProcessLauncher(),
            resolveGitBinary: GitOnly) { Task = "   " };

        await vm.RunAsync();

        Assert.False(vm.IsRunning);
        Assert.Null(vm.Outcome);
        Assert.False(vm.HasResult);
    }

    [Fact]
    public async Task RunProducesADoneOutcomeWithADiffAndEnablesApply()
    {
        var repo = await MakeRepoAsync();
        try
        {
            var vm = new TeamRunViewModel(repo, ClaudeTeam(), new WritingRunner(), new RealProcessLauncher(),
                resolveGitBinary: GitOnly) { Task = "add a file" };

            await vm.RunAsync();

            Assert.False(vm.IsRunning);
            Assert.True(vm.HasResult);
            Assert.True(vm.CanApply);
            Assert.NotEmpty(vm.DiffLines);
            Assert.Contains("Done", vm.StatusMessage);

            await vm.DiscardAsync();
            Assert.Null(vm.Outcome);
            Assert.Equal("Discarded.", vm.StatusMessage);
        }
        finally
        {
            DeleteDirectoryRobustly(repo);
        }
    }

    [Fact]
    public async Task ApplyMergesAndClearsTheOutcome()
    {
        var repo = await MakeRepoAsync();
        try
        {
            var vm = new TeamRunViewModel(repo, ClaudeTeam(), new WritingRunner(), new RealProcessLauncher(),
                resolveGitBinary: GitOnly) { Task = "add a file" };
            await vm.RunAsync();

            await vm.ApplyAsync();

            Assert.Null(vm.Outcome);
            Assert.False(vm.HasResult);
            Assert.Equal("Applied to your repo.", vm.StatusMessage);
            Assert.True(File.Exists(Path.Combine(repo, "claude.txt")));
        }
        finally
        {
            DeleteDirectoryRobustly(repo);
        }
    }

    [Fact]
    public async Task ReportsAFailureWhenTheWorktreeCannotBeCreated()
    {
        var vm = new TeamRunViewModel("/does/not/exist", ClaudeTeam(), new WritingRunner(), new RealProcessLauncher(),
            resolveGitBinary: GitOnly) { Task = "task" };

        await vm.RunAsync();

        Assert.True(vm.HasResult);
        Assert.False(vm.CanApply);
        Assert.Contains("Error:", vm.StatusMessage);
    }
}
