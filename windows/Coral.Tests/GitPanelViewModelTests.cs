using Coral.Core.Services;
using Coral.Core.ViewModels;
using Xunit;

namespace Coral.Tests;

/// <summary>Tests for GitPanelViewModel's state-mutation logic against a
/// FakeProcessLauncher — mirrors ChatViewModelTests/ConnectViewModelTests's
/// style. No real git process ever spawns; each test's launcher factory
/// dispatches on the git subcommand in the args it's given.</summary>
public class GitPanelViewModelTests
{
    private static readonly Func<string, string?> ResolveGit = n => n == "git" ? "/usr/bin/git" : null;

    /// <summary>A launcher that answers RefreshAsync's five git calls
    /// (numstat, status -z, staged --cached, current branch, branch list)
    /// with a fixed, self-consistent snapshot: one modified file, unstaged.</summary>
    private static FakeProcessLauncher MakeRefreshLauncher(string branch = "main") =>
        new((_, args) =>
        {
            if (args.Contains("--numstat")) return new FakeChildProcess(new List<string> { "2\t1\ta.txt" });
            if (args.Contains("--cached")) return new FakeChildProcess(new List<string>()); // nothing staged
            if (args.Contains("status")) return new FakeChildProcess(new List<string> { " M a.txt\0" });
            if (args.Contains("rev-parse")) return new FakeChildProcess(new List<string> { branch });
            if (args.Contains("branch")) return new FakeChildProcess(new List<string> { branch });
            return new FakeChildProcess(new List<string>());
        });

    [Fact]
    public async Task RefreshAsyncPopulatesFilesBranchAndSelectsTheFirstFile()
    {
        var vm = new GitPanelViewModel(MakeRefreshLauncher(), "/repo", ResolveGit);

        await vm.RefreshAsync();

        var file = Assert.Single(vm.ChangedFiles);
        Assert.Equal("a.txt", file.Path);
        Assert.Equal("main", vm.Branch);
        Assert.Contains("main", vm.Branches);
        Assert.Equal("a.txt", vm.SelectedFile);
        Assert.False(vm.IsStaged(file));
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task RefreshAsyncReportsAStatusMessageWhenTheFolderIsntARepo()
    {
        // Real gap found on real Windows: pointed at a non-repo folder (the
        // wrong window's RepoPath), every git call fails, Branch ends up
        // null, and Refresh used to give zero feedback about why.
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>(), exitCode: 1));
        var vm = new GitPanelViewModel(launcher, "/not-a-repo", ResolveGit);

        await vm.RefreshAsync();

        Assert.Null(vm.Branch);
        Assert.NotNull(vm.StatusMessage);
    }

    [Fact]
    public async Task SelectFileAsyncLoadsTheDiffForThatFile()
    {
        var launcher = new FakeProcessLauncher((_, args) =>
            args.Contains("diff") && args.Contains("--")
                ? new FakeChildProcess(new List<string> { "@@ -1,1 +1,1 @@", "-old", "+new" })
                : new FakeChildProcess(new List<string>()));
        var vm = new GitPanelViewModel(launcher, "/repo", ResolveGit);

        await vm.SelectFileAsync("a.txt");

        Assert.Equal("a.txt", vm.SelectedFile);
        Assert.NotNull(vm.DiffLines);
        Assert.Equal(3, vm.DiffLines!.Count);
    }

    [Fact]
    public async Task ToggleStageAsyncStagesThenUnstagesTheSameFile()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));
        var vm = new GitPanelViewModel(launcher, "/repo", ResolveGit);
        var file = new ChangedFile("a.txt", 2, 1, ChangedFileKind.Modified);

        await vm.ToggleStageAsync(file);
        Assert.True(vm.IsStaged(file));

        await vm.ToggleStageAsync(file);
        Assert.False(vm.IsStaged(file));
    }

    [Fact]
    public async Task RevertAsyncRefreshesOnSuccess()
    {
        var refreshed = false;
        var launcher = new FakeProcessLauncher((_, args) =>
        {
            if (args.Contains("checkout") && args.Contains("--")) return new FakeChildProcess(new List<string>());
            refreshed = true;
            if (args.Contains("--numstat")) return new FakeChildProcess(new List<string>());
            if (args.Contains("--cached")) return new FakeChildProcess(new List<string>());
            if (args.Contains("status")) return new FakeChildProcess(new List<string>());
            if (args.Contains("rev-parse")) return new FakeChildProcess(new List<string> { "main" });
            return new FakeChildProcess(new List<string>());
        });
        var vm = new GitPanelViewModel(launcher, "/repo", ResolveGit);

        await vm.RevertAsync("a.txt");

        Assert.True(refreshed);
    }

    [Fact]
    public async Task CommitAsyncIsANoOpOnABlankMessage()
    {
        var called = false;
        var launcher = new FakeProcessLauncher((_, _) => { called = true; return new FakeChildProcess(new List<string>()); });
        var vm = new GitPanelViewModel(launcher, "/repo", ResolveGit);

        vm.CommitMessage = "   ";
        await vm.CommitAsync();

        Assert.False(called);
    }

    [Fact]
    public async Task CommitAsyncClearsTheMessageAndRefreshesOnSuccess()
    {
        var launcher = new FakeProcessLauncher((_, args) =>
        {
            if (args.Contains("commit")) return new FakeChildProcess(new List<string> { "[main abc] msg" });
            if (args.Contains("rev-parse")) return new FakeChildProcess(new List<string> { "main" });
            return new FakeChildProcess(new List<string>());
        });
        var vm = new GitPanelViewModel(launcher, "/repo", ResolveGit);
        vm.CommitMessage = "fix the bug";

        await vm.CommitAsync();

        Assert.Equal("Committed.", vm.StatusMessage);
        Assert.Equal("", vm.CommitMessage);
    }

    [Fact]
    public async Task CommitAsyncReportsFailureAndKeepsTheMessage()
    {
        var launcher = new FakeProcessLauncher((_, args) =>
            args.Contains("commit")
                ? new FakeChildProcess(new List<string>(), exitCode: 1, stderr: "nothing to commit")
                : new FakeChildProcess(new List<string>()));
        var vm = new GitPanelViewModel(launcher, "/repo", ResolveGit);
        vm.CommitMessage = "fix the bug";

        await vm.CommitAsync();

        Assert.Equal("Error: nothing to commit", vm.StatusMessage);
        Assert.Equal("fix the bug", vm.CommitMessage);
    }

    [Fact]
    public async Task PushAsyncReportsSuccess()
    {
        var launcher = new FakeProcessLauncher((_, args) =>
            args.Contains("push")
                ? new FakeChildProcess(new List<string> { "branch set up to track" })
                : new FakeChildProcess(new List<string> { "main" }));
        var vm = new GitPanelViewModel(launcher, "/repo", ResolveGit);

        await vm.PushAsync();

        Assert.Equal("Pushed.", vm.StatusMessage);
    }

    [Fact]
    public async Task CreateBranchAsyncIsANoOpOnABlankName()
    {
        var called = false;
        var launcher = new FakeProcessLauncher((_, _) => { called = true; return new FakeChildProcess(new List<string>()); });
        var vm = new GitPanelViewModel(launcher, "/repo", ResolveGit);

        await vm.CreateBranchAsync("   ");

        Assert.False(called);
    }

    [Fact]
    public async Task CreateBranchAsyncReturnsTrueAndSetsASuccessMessage()
    {
        var launcher = new FakeProcessLauncher((_, args) =>
            args.Contains("-b")
                ? new FakeChildProcess(new List<string> { "Switched to a new branch 'feature-y'" })
                : new FakeChildProcess(new List<string> { "main" }));
        var vm = new GitPanelViewModel(launcher, "/repo", ResolveGit);

        var ok = await vm.CreateBranchAsync("feature-y");

        Assert.True(ok);
        Assert.Equal("Created branch 'feature-y'.", vm.StatusMessage);
    }

    [Fact]
    public async Task CreateBranchAsyncReturnsFalseAndKeepsTheErrorOnFailure()
    {
        var launcher = new FakeProcessLauncher((_, args) =>
            args.Contains("-b")
                ? new FakeChildProcess(new List<string>(), exitCode: 1,
                    stderr: "a branch named 'feature-y' already exists")
                : new FakeChildProcess(new List<string>()));
        var vm = new GitPanelViewModel(launcher, "/repo", ResolveGit);

        var ok = await vm.CreateBranchAsync("feature-y");

        Assert.False(ok);
        Assert.Equal("Error: a branch named 'feature-y' already exists", vm.StatusMessage);
    }

    [Fact]
    public async Task CheckoutAsyncRefreshesOnSuccess()
    {
        var launcher = MakeRefreshLauncher(branch: "feature-x");
        var vm = new GitPanelViewModel(launcher, "/repo", ResolveGit);

        await vm.CheckoutAsync("feature-x");

        Assert.Equal("feature-x", vm.Branch);
    }

    [Fact]
    public async Task CheckoutAsyncSetsASuccessMessage()
    {
        var launcher = MakeRefreshLauncher(branch: "feature-x");
        var vm = new GitPanelViewModel(launcher, "/repo", ResolveGit);

        await vm.CheckoutAsync("feature-x");

        Assert.Equal("Switched to 'feature-x'.", vm.StatusMessage);
    }
}
