using Coral.Core.Services;
using Coral.Core.ViewModels;
using Xunit;

namespace Coral.Tests;

/// <summary>Tests for PullRequestViewModel's state-mutation logic against a
/// FakeProcessLauncher — mirrors GitPanelViewModelTests's style. No real gh
/// process ever spawns.</summary>
public class PullRequestViewModelTests
{
    private static readonly Func<string, string?> ResolveGh = n => n == "gh" ? "/usr/bin/gh" : null;

    [Fact]
    public async Task RefreshAsyncPopulatesInfoFromARealResponse()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>
        {
            """{"number":5,"state":"OPEN","url":"https://github.com/o/r/pull/5","title":"Fix","isDraft":false}""",
        }));
        var vm = new PullRequestViewModel(launcher, "/repo", ResolveGh);

        await vm.RefreshAsync("feature-x");

        Assert.NotNull(vm.Info);
        Assert.Equal(5, vm.Info!.Number);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task RefreshAsyncClearsInfoWhenNoPrExists()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>(), exitCode: 1));
        var vm = new PullRequestViewModel(launcher, "/repo", ResolveGh);

        await vm.RefreshAsync("feature-x");

        Assert.Null(vm.Info);
    }

    [Fact]
    public async Task CreateAsyncIsANoOpOnABlankTitle()
    {
        var called = false;
        var launcher = new FakeProcessLauncher((_, _) => { called = true; return new FakeChildProcess(new List<string>()); });
        var vm = new PullRequestViewModel(launcher, "/repo", ResolveGh);

        vm.Title = "   ";
        await vm.CreateAsync("feature-x");

        Assert.False(called);
    }

    [Fact]
    public async Task CreateAsyncClearsTitleAndBodyAndRefreshesOnSuccess()
    {
        var launcher = new FakeProcessLauncher((_, args) =>
            args.Contains("create")
                ? new FakeChildProcess(new List<string> { "https://github.com/o/r/pull/9" })
                : new FakeChildProcess(new List<string>
                {
                    """{"number":9,"state":"OPEN","url":"https://github.com/o/r/pull/9","title":"Fix","isDraft":false}""",
                }));
        var vm = new PullRequestViewModel(launcher, "/repo", ResolveGh);
        vm.Title = "Fix the bug";
        vm.Body = "Details";

        await vm.CreateAsync("feature-x");

        Assert.Equal("PR created: https://github.com/o/r/pull/9", vm.StatusMessage);
        Assert.Equal("", vm.Title);
        Assert.Equal("", vm.Body);
        Assert.NotNull(vm.Info);
        Assert.Equal(9, vm.Info!.Number);
    }

    [Fact]
    public async Task CreateAsyncReportsFailureAndKeepsTheTitle()
    {
        var launcher = new FakeProcessLauncher((_, _) =>
            new FakeChildProcess(new List<string>(), exitCode: 1, stderr: "no commits between main and feature-x"));
        var vm = new PullRequestViewModel(launcher, "/repo", ResolveGh);
        vm.Title = "Fix the bug";

        await vm.CreateAsync("feature-x");

        Assert.Equal("Error: no commits between main and feature-x", vm.StatusMessage);
        Assert.Equal("Fix the bug", vm.Title);
    }

    [Fact]
    public async Task MergeAsyncRefreshesToReflectMergedOnSuccess()
    {
        var launcher = new FakeProcessLauncher((_, args) =>
            args.Contains("merge")
                ? new FakeChildProcess(new List<string> { "Merged" })
                : new FakeChildProcess(new List<string>
                {
                    """{"number":9,"state":"MERGED","url":"https://github.com/o/r/pull/9","title":"Fix","isDraft":false}""",
                }));
        var vm = new PullRequestViewModel(launcher, "/repo", ResolveGh);

        await vm.MergeAsync("feature-x");

        Assert.Equal("Merged.", vm.StatusMessage);
        Assert.Equal("MERGED", vm.Info!.State);
    }

    [Fact]
    public async Task MergeAsyncReportsFailure()
    {
        var launcher = new FakeProcessLauncher((_, _) =>
            new FakeChildProcess(new List<string>(), exitCode: 1, stderr: "checks have not passed"));
        var vm = new PullRequestViewModel(launcher, "/repo", ResolveGh);

        await vm.MergeAsync("feature-x");

        Assert.Equal("Error: checks have not passed", vm.StatusMessage);
    }
}
