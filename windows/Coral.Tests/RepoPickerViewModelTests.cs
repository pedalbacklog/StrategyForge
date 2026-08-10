using Coral.Core.Services;
using Coral.Core.ViewModels;
using Xunit;

namespace Coral.Tests;

/// <summary>Tests for RepoPickerViewModel's state-mutation logic against a
/// FakeProcessLauncher — mirrors GitPanelViewModelTests/
/// PullRequestViewModelTests's style. No real git/gh process ever spawns.</summary>
public class RepoPickerViewModelTests
{
    private static readonly Func<string, string?> Resolve = n => n is "git" or "gh" ? $"/usr/bin/{n}" : null;

    [Fact]
    public async Task LoadReposAsyncPopulatesReposFromARealResponse()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>
        {
            """[{"nameWithOwner":"o/r","description":"","isPrivate":false,"url":"https://github.com/o/r"}]""",
        }));
        var vm = new RepoPickerViewModel(launcher, Resolve);

        await vm.LoadReposAsync();

        Assert.Single(vm.Repos);
        Assert.Null(vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task LoadReposAsyncReportsAStatusMessageWhenEmpty()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));
        var vm = new RepoPickerViewModel(launcher, _ => null); // gh not found -> empty list

        await vm.LoadReposAsync();

        Assert.Empty(vm.Repos);
        Assert.Contains("gh", vm.StatusMessage);
    }

    [Fact]
    public async Task CloneAsyncIsANoOpOnABlankUrl()
    {
        var called = false;
        var launcher = new FakeProcessLauncher((_, _) => { called = true; return new FakeChildProcess(new List<string>()); });
        var vm = new RepoPickerViewModel(launcher, Resolve);

        vm.CloneUrl = "   ";
        await vm.CloneAsync("/parent");

        Assert.False(called);
    }

    [Fact]
    public async Task CloneAsyncSetsResultRepoPathOnSuccess()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string> { "Cloning..." }));
        // No-op disk hooks — never touches the real filesystem.
        var vm = new RepoPickerViewModel(launcher, Resolve, createDirectory: _ => { }, pathExists: _ => false);
        vm.CloneUrl = "git@github.com:owner/foo.git";

        await vm.CloneAsync("/parent");

        Assert.Equal(Path.Combine("/parent", "foo"), vm.ResultRepoPath);
        Assert.Null(vm.StatusMessage);
    }

    [Fact]
    public async Task CloneAsyncReportsFailureAndLeavesResultPathNull()
    {
        var launcher = new FakeProcessLauncher((_, _) =>
            new FakeChildProcess(new List<string>(), exitCode: 1, stderr: "repository not found"));
        var vm = new RepoPickerViewModel(launcher, Resolve, createDirectory: _ => { }, pathExists: _ => false);
        vm.CloneUrl = "git@github.com:owner/missing.git";

        await vm.CloneAsync("/parent");

        Assert.Null(vm.ResultRepoPath);
        Assert.Equal("Error: repository not found", vm.StatusMessage);
    }

    [Fact]
    public async Task OpenOrCloneAsyncOpensTheExistingFolderWithoutCloningWhenAlreadyPresent()
    {
        var called = false;
        var launcher = new FakeProcessLauncher((_, _) => { called = true; return new FakeChildProcess(new List<string>()); });
        var vm = new RepoPickerViewModel(launcher, Resolve, createDirectory: _ => { }, pathExists: _ => true);
        var repo = new RepoRef("owner/foo", "", false, "git@github.com:owner/foo.git");

        await vm.OpenOrCloneAsync(repo, "/parent");

        Assert.False(called); // never re-cloned — no "-2" suffix, no wasted clone
        Assert.Equal(Path.Combine("/parent", "foo"), vm.ResultRepoPath);
    }

    [Fact]
    public async Task OpenOrCloneAsyncClonesWhenNotYetPresent()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string> { "Cloning..." }));
        var vm = new RepoPickerViewModel(launcher, Resolve, createDirectory: _ => { }, pathExists: _ => false);
        var repo = new RepoRef("owner/foo", "", false, "git@github.com:owner/foo.git");

        await vm.OpenOrCloneAsync(repo, "/parent");

        Assert.Equal(Path.Combine("/parent", "foo"), vm.ResultRepoPath);
        Assert.Equal("git@github.com:owner/foo.git", vm.CloneUrl);
    }

    [Fact]
    public async Task CreateRepoAsyncIsANoOpOnABlankName()
    {
        var called = false;
        var launcher = new FakeProcessLauncher((_, _) => { called = true; return new FakeChildProcess(new List<string>()); });
        var vm = new RepoPickerViewModel(launcher, Resolve);

        vm.NewRepoName = "  ";
        await vm.CreateRepoAsync("/parent");

        Assert.False(called);
    }

    [Fact]
    public async Task CreateRepoAsyncSetsResultRepoPathOnSuccess()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string> { "Created" }));
        var vm = new RepoPickerViewModel(launcher, Resolve, createDirectory: _ => { }, pathExists: _ => true);
        vm.NewRepoName = "my-repo";

        await vm.CreateRepoAsync("/parent");

        Assert.Equal(Path.Combine("/parent", "my-repo"), vm.ResultRepoPath);
        Assert.Null(vm.StatusMessage);
    }

    [Fact]
    public async Task CreateRepoAsyncReportsFailureWhenTheClonedPathNeverAppears()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>())); // exit 0
        var vm = new RepoPickerViewModel(launcher, Resolve, createDirectory: _ => { }, pathExists: _ => false);
        vm.NewRepoName = "my-repo";

        await vm.CreateRepoAsync("/parent");

        // Confirms the ViewModel forwards CreateRepoAsync's own success check
        // (exit code AND the cloned path actually appearing) rather than
        // assuming success from a clean exit code alone.
        Assert.Null(vm.ResultRepoPath);
        Assert.NotNull(vm.StatusMessage);
    }
}
