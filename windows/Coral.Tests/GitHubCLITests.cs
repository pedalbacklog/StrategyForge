using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Tests for GitHubCLI's pure JSON/URL parsing and its one-shot
/// gh operations against a FakeProcessLauncher — mirrors CodeGitTests's
/// style. No real gh process ever spawns.</summary>
public class GitHubCLITests
{
    private static readonly Func<string, string?> ResolveGh = n => n == "gh" ? "/usr/bin/gh" : null;

    [Fact]
    public void IsInstalledReflectsWhetherGhResolves()
    {
        Assert.True(GitHubCLI.IsInstalled(ResolveGh));
        Assert.False(GitHubCLI.IsInstalled(_ => null));
    }

    [Theory]
    [InlineData("Creating PR...\nhttps://github.com/o/r/pull/42\n", "https://github.com/o/r/pull/42")]
    // Finds the LAST matching line anywhere in the output (matches Swift's
    // `.last(where:)`), not just whether the final line happens to match.
    [InlineData("https://github.com/o/r/pull/1\nhttps://github.com/o/r/pull/2", "https://github.com/o/r/pull/2")]
    [InlineData("no url here", null)]
    public void LastHttpsLineFindsTheFinalUrlLine(string output, string? expected) =>
        Assert.Equal(expected, GitHubCLI.LastHttpsLine(output));

    [Fact]
    public void ParsePRInfoParsesAWellFormedPayload()
    {
        var json = """{"number":42,"state":"OPEN","url":"https://github.com/o/r/pull/42","title":"Fix the bug","isDraft":true}""";

        var info = GitHubCLI.ParsePRInfo(json);

        Assert.NotNull(info);
        Assert.Equal(42, info!.Number);
        Assert.Equal("OPEN", info.State);
        Assert.Equal("https://github.com/o/r/pull/42", info.Url);
        Assert.Equal("Fix the bug", info.Title);
        Assert.True(info.IsDraft);
    }

    [Fact]
    public void ParsePRInfoFillsDefaultsForMissingOptionalFields()
    {
        var info = GitHubCLI.ParsePRInfo("""{"number":7}""");

        Assert.NotNull(info);
        Assert.Equal(7, info!.Number);
        Assert.Equal("OPEN", info.State);
        Assert.Equal("", info.Url);
        Assert.Equal("", info.Title);
        Assert.False(info.IsDraft);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"state":"OPEN"}""")] // no "number"
    public void ParsePRInfoReturnsNullForMalformedOrIncompleteJson(string json) =>
        Assert.Null(GitHubCLI.ParsePRInfo(json));

    [Fact]
    public async Task IsAuthenticatedAsyncReflectsExitCode()
    {
        var ok = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));
        Assert.True(await GitHubCLI.IsAuthenticatedAsync(ok, ResolveGh));

        var fail = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>(), exitCode: 1));
        Assert.False(await GitHubCLI.IsAuthenticatedAsync(fail, ResolveGh));
    }

    [Fact]
    public async Task IsAuthenticatedAsyncIsFalseWhenGhIsMissing()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));
        Assert.False(await GitHubCLI.IsAuthenticatedAsync(launcher, _ => null));
        Assert.Null(launcher.LastStart);
    }

    [Fact]
    public async Task CreatePRAsyncExtractsTheUrlFromSuccessfulOutput()
    {
        var launcher = new FakeProcessLauncher((_, _) =>
            new FakeChildProcess(new List<string> { "Creating pull request...", "https://github.com/o/r/pull/9" }));

        var (ok, url, output) = await GitHubCLI.CreatePRAsync(launcher, "/repo", "Title", "Body", ResolveGh);

        Assert.True(ok);
        Assert.Equal("https://github.com/o/r/pull/9", url);
        Assert.Contains("pull/9", output);
        Assert.Contains("Title", launcher.LastStart!.Value.Args);
        Assert.Contains("Body", launcher.LastStart.Value.Args);
    }

    [Fact]
    public async Task CreatePRAsyncReportsGhNotFound()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));

        var (ok, url, output) = await GitHubCLI.CreatePRAsync(launcher, "/repo", "T", "B", _ => null);

        Assert.False(ok);
        Assert.Null(url);
        Assert.Equal("GitHub CLI (gh) not found", output);
        Assert.Null(launcher.LastStart);
    }

    [Fact]
    public async Task PrInfoAsyncReturnsNullOnFailure()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>(), exitCode: 1));

        var info = await GitHubCLI.PrInfoAsync(launcher, "/repo", "feature-x", ResolveGh);

        Assert.Null(info);
    }

    [Fact]
    public async Task PrInfoAsyncParsesARealSuccessResponse()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>
        {
            """{"number":5,"state":"MERGED","url":"https://github.com/o/r/pull/5","title":"Add feature","isDraft":false}""",
        }));

        var info = await GitHubCLI.PrInfoAsync(launcher, "/repo", "feature-x", ResolveGh);

        Assert.NotNull(info);
        Assert.Equal("MERGED", info!.State);
    }

    [Fact]
    public async Task MergePRAsyncSquashesByDefault()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string> { "Merged" }));

        var (ok, output) = await GitHubCLI.MergePRAsync(launcher, "/repo", "feature-x", resolveBinary: ResolveGh);

        Assert.True(ok);
        Assert.Contains("Merged", output);
        Assert.Contains("--squash", launcher.LastStart!.Value.Args);
    }

    [Fact]
    public async Task MergePRAsyncUsesMergeFlagWhenSquashIsFalse()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));

        await GitHubCLI.MergePRAsync(launcher, "/repo", "feature-x", squash: false, resolveBinary: ResolveGh);

        Assert.Contains("--merge", launcher.LastStart!.Value.Args);
        Assert.DoesNotContain("--squash", launcher.LastStart.Value.Args);
    }
}
