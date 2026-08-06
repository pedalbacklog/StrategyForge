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

    [Fact]
    public void ParseRepoListSkipsEntriesMissingNameWithOwnerAndFillsDefaults()
    {
        var json = """
        [
          {"nameWithOwner":"o/r1","description":"First","isPrivate":true,"url":"https://github.com/o/r1"},
          {"nameWithOwner":"o/r2"},
          {"description":"no name, skipped"}
        ]
        """;

        var repos = GitHubCLI.ParseRepoList(json);

        Assert.Equal(2, repos.Count);
        Assert.Equal("First", repos[0].Description);
        Assert.True(repos[0].IsPrivate);
        Assert.Equal("r1", repos[0].Name);
        Assert.Equal("", repos[1].Description);
        Assert.False(repos[1].IsPrivate);
        Assert.Equal("https://github.com/o/r2", repos[1].Url); // defaulted, not in the payload
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"nameWithOwner":"o/r"}""")] // an object, not an array
    public void ParseRepoListReturnsEmptyForMalformedJson(string json) =>
        Assert.Empty(GitHubCLI.ParseRepoList(json));

    [Fact]
    public async Task ListReposAsyncReturnsEmptyWhenGhIsMissing()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));

        var repos = await GitHubCLI.ListReposAsync(launcher, resolveBinary: _ => null);

        Assert.Empty(repos);
        Assert.Null(launcher.LastStart);
    }

    [Fact]
    public async Task ListReposAsyncParsesARealSuccessResponse()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>
        {
            """[{"nameWithOwner":"o/r","description":"","isPrivate":false,"url":"https://github.com/o/r"}]""",
        }));

        var repos = await GitHubCLI.ListReposAsync(launcher, resolveBinary: ResolveGh);

        Assert.Single(repos);
        Assert.Equal("o/r", repos[0].NameWithOwner);
    }

    [Fact]
    public async Task CreateRepoAsyncSucceedsWhenGhExitsCleanAndThePathExists()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string> { "Created" }));

        var (ok, path, output) = await GitHubCLI.CreateRepoAsync(launcher, "my-repo", isPrivate: true, "/parent",
            resolveBinary: ResolveGh, createDirectory: _ => { }, pathExists: _ => true);

        Assert.True(ok);
        Assert.Equal(Path.Combine("/parent", "my-repo"), path);
        Assert.Contains("Created", output);
        Assert.Contains("--private", launcher.LastStart!.Value.Args);
    }

    [Fact]
    public async Task CreateRepoAsyncFailsWhenTheClonedPathNeverAppears()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>())); // exit 0

        var (ok, path, _) = await GitHubCLI.CreateRepoAsync(launcher, "my-repo", isPrivate: false, "/parent",
            resolveBinary: ResolveGh, createDirectory: _ => { }, pathExists: _ => false);

        Assert.False(ok);
        Assert.Null(path);
    }

    [Fact]
    public async Task CreateRepoAsyncReportsGhNotFound()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));

        var (ok, path, output) = await GitHubCLI.CreateRepoAsync(launcher, "n", false, "/parent", resolveBinary: _ => null);

        Assert.False(ok);
        Assert.Null(path);
        Assert.Equal("GitHub CLI (gh) not found", output);
        Assert.Null(launcher.LastStart);
    }
}
