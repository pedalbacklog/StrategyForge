using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Tests for ShipFlow's commit→push→PR orchestration against a
/// FakeProcessLauncher — mirrors CodeGitTests/GitHubCLITests's style. No real
/// git/gh process ever spawns; dispatches on the subcommand in the args (or,
/// for git-vs-gh, the resolved binary path) each test's launcher is given.</summary>
public class ShipFlowTests
{
    private static readonly Func<string, string?> Resolve =
        n => n switch { "git" => "/usr/bin/git", "gh" => "/usr/bin/gh", _ => null };

    [Fact]
    public async Task CommitsAllPushesAndOpensAPrWhenNoneExisted()
    {
        var launcher = new FakeProcessLauncher((file, args) =>
        {
            if (file.EndsWith("gh")) return new FakeChildProcess(new List<string> { "https://github.com/o/r/pull/9" });
            if (args.Contains("commit")) return new FakeChildProcess(new List<string> { "[main abc] msg" });
            return new FakeChildProcess(new List<string> { "main" }); // add -A, rev-parse, push
        });

        var result = await ShipFlow.RunAsync(launcher, "/repo", "msg", "PR title", "PR body",
            auto: false, anyStaged: false, hadPR: false, Resolve);

        Assert.True(result.Ok);
        Assert.True(result.PrWasCreated);
        Assert.Equal("https://github.com/o/r/pull/9", result.PrUrl);
    }

    [Fact]
    public async Task SkipsCreatingAPrWhenOneAlreadyExists()
    {
        var ghCalled = false;
        var launcher = new FakeProcessLauncher((file, args) =>
        {
            if (file.EndsWith("gh")) { ghCalled = true; return new FakeChildProcess(new List<string>()); }
            if (args.Contains("commit")) return new FakeChildProcess(new List<string> { "[main abc] msg" });
            return new FakeChildProcess(new List<string> { "main" });
        });

        var result = await ShipFlow.RunAsync(launcher, "/repo", "msg", "t", "b",
            auto: false, anyStaged: false, hadPR: true, Resolve);

        Assert.True(result.Ok);
        Assert.False(result.PrWasCreated);
        Assert.False(ghCalled);
    }

    [Fact]
    public async Task ToleratesNothingToCommitAndStillPushes()
    {
        var pushed = false;
        var launcher = new FakeProcessLauncher((file, args) =>
        {
            if (file.EndsWith("gh")) return new FakeChildProcess(new List<string> { "https://github.com/o/r/pull/1" });
            if (args.Contains("commit"))
                return new FakeChildProcess(new List<string>(), exitCode: 1, stderr: "nothing to commit, working tree clean");
            if (args.Contains("push")) { pushed = true; return new FakeChildProcess(new List<string>()); }
            return new FakeChildProcess(new List<string> { "main" });
        });

        var result = await ShipFlow.RunAsync(launcher, "/repo", "msg", "t", "b",
            auto: false, anyStaged: false, hadPR: false, Resolve);

        Assert.True(result.Ok);
        Assert.True(pushed);
    }

    [Fact]
    public async Task ARealCommitFailureStopsBeforePushing()
    {
        var pushed = false;
        var launcher = new FakeProcessLauncher((file, args) =>
        {
            if (args.Contains("commit"))
                return new FakeChildProcess(new List<string>(), exitCode: 1, stderr: "fatal: not a git repository");
            if (args.Contains("push")) { pushed = true; return new FakeChildProcess(new List<string>()); }
            return new FakeChildProcess(new List<string>());
        });

        var result = await ShipFlow.RunAsync(launcher, "/repo", "msg", "t", "b",
            auto: false, anyStaged: false, hadPR: false, Resolve);

        Assert.False(result.Ok);
        Assert.False(pushed);
        Assert.Contains("not a git repository", result.Error);
    }

    [Fact]
    public async Task APushFailureIsReportedAndSkipsCreatingAPr()
    {
        var ghCalled = false;
        var launcher = new FakeProcessLauncher((file, args) =>
        {
            if (file.EndsWith("gh")) { ghCalled = true; return new FakeChildProcess(new List<string>()); }
            if (args.Contains("commit")) return new FakeChildProcess(new List<string> { "[main abc] msg" });
            if (args.Contains("push"))
                return new FakeChildProcess(new List<string>(), exitCode: 1, stderr: "rejected");
            return new FakeChildProcess(new List<string> { "main" });
        });

        var result = await ShipFlow.RunAsync(launcher, "/repo", "msg", "t", "b",
            auto: false, anyStaged: false, hadPR: false, Resolve);

        Assert.False(result.Ok);
        Assert.False(ghCalled);
        Assert.Contains("rejected", result.Error);
    }

    [Fact]
    public async Task CommitsOnlyStagedFilesWhenNotAutoAndSomethingIsStaged()
    {
        var addCalled = false;
        var launcher = new FakeProcessLauncher((file, args) =>
        {
            if (args.Contains("add")) { addCalled = true; return new FakeChildProcess(new List<string>()); }
            if (args.Contains("commit")) return new FakeChildProcess(new List<string> { "[main abc] msg" });
            return new FakeChildProcess(new List<string> { "main" });
        });

        await ShipFlow.RunAsync(launcher, "/repo", "msg", "t", "b", auto: false, anyStaged: true, hadPR: true, Resolve);

        Assert.False(addCalled);
    }

    [Fact]
    public async Task CommitsEverythingWhenAutoEvenIfSomethingIsStaged()
    {
        var addCalled = false;
        var launcher = new FakeProcessLauncher((file, args) =>
        {
            if (args.Contains("add")) { addCalled = true; return new FakeChildProcess(new List<string>()); }
            if (args.Contains("commit")) return new FakeChildProcess(new List<string> { "[main abc] msg" });
            return new FakeChildProcess(new List<string> { "main" });
        });

        await ShipFlow.RunAsync(launcher, "/repo", "msg", "t", "b", auto: true, anyStaged: true, hadPR: true, Resolve);

        Assert.True(addCalled);
    }
}
