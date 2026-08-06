using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of ChatTests.swift's diffParserTagsAddsRemovesAndNumbers
/// (the only Swift spec test for CodeGit.parse) plus tests for the other
/// pure parsers and the read-only real-git operations against a
/// FakeProcessLauncher — mirrors ProviderInstallerTests's style. No real git
/// process ever spawns.</summary>
public class CodeGitTests
{
    [Fact]
    public void ParseTagsAddsRemovesAndNumbers()
    {
        // Exact spec from StrategyForgeTests/ChatTests.swift.
        var diff = string.Join('\n',
            "diff --git a/App.swift b/App.swift",
            "index 111..222 100644",
            "--- a/App.swift",
            "+++ b/App.swift",
            "@@ -1,3 +1,4 @@",
            " let a = 1",
            "-let b = 2",
            "+let b = 3",
            "+let c = 4");

        var lines = CodeGit.Parse(diff);

        Assert.Contains(lines, l => l.Kind == DiffLineKind.Hunk);
        Assert.Equal(2, lines.Count(l => l.Kind == DiffLineKind.Add));
        Assert.Equal(1, lines.Count(l => l.Kind == DiffLineKind.Del));
        var firstContext = lines.First(l => l.Kind == DiffLineKind.Context);
        Assert.Equal(1, firstContext.OldNumber);
        Assert.Equal(1, firstContext.NewNumber);
    }

    [Fact]
    public void ParseDropsGitHeaderNoiseButKeepsTheHunkHeaderText()
    {
        var diff = string.Join('\n',
            "diff --git a/x.txt b/x.txt",
            "new file mode 100644",
            "index 000..111",
            "--- /dev/null",
            "+++ b/x.txt",
            "@@ -0,0 +1,1 @@",
            "+hello");

        var lines = CodeGit.Parse(diff);

        Assert.Equal(2, lines.Count); // just the hunk header + the add
        Assert.Equal(DiffLineKind.Hunk, lines[0].Kind);
        Assert.Equal(DiffLineKind.Add, lines[1].Kind);
        Assert.Equal("hello", lines[1].Text);
    }

    [Fact]
    public void ParseChangedFilesTagsModifiedAddedDeletedUntrackedAndRenamed()
    {
        var numstat = "3\t1\tmodified.txt\n0\t0\tuntracked.txt\n";
        var statusZ = string.Join('\0',
            " M modified.txt",
            "A  added.txt",
            " D deleted.txt",
            "?? untracked.txt",
            "R  renamed-new.txt", "renamed-old.txt") + "\0";

        var files = CodeGit.ParseChangedFiles(numstat, statusZ).ToList();

        Assert.Equal(5, files.Count);
        Assert.Equal(ChangedFileKind.Modified, files.Single(f => f.Path == "modified.txt").Kind);
        Assert.Equal(3, files.Single(f => f.Path == "modified.txt").Insertions);
        Assert.Equal(1, files.Single(f => f.Path == "modified.txt").Deletions);
        Assert.Equal(ChangedFileKind.Added, files.Single(f => f.Path == "added.txt").Kind);
        Assert.Equal(ChangedFileKind.Deleted, files.Single(f => f.Path == "deleted.txt").Kind);
        Assert.Equal(ChangedFileKind.Untracked, files.Single(f => f.Path == "untracked.txt").Kind);
        var renamed = files.Single(f => f.Path == "renamed-new.txt");
        Assert.Equal(ChangedFileKind.Renamed, renamed.Kind); // old-path record consumed, not a 6th file
    }

    [Theory]
    [InlineData(" 2 files changed, 42 insertions(+), 9 deletions(-)", 42, 9)]
    [InlineData(" 1 file changed, 1 insertion(+)", 1, 0)]
    [InlineData("", 0, 0)]
    public void ParseShortstatExtractsInsertionsAndDeletions(string s, int expectedIns, int expectedDel)
    {
        var (ins, del) = CodeGit.ParseShortstat(s);
        Assert.Equal(expectedIns, ins);
        Assert.Equal(expectedDel, del);
    }

    [Theory]
    [InlineData("git@github.com:owner/foo.git", "foo")]
    // The ".git" suffix check runs BEFORE the trailing-slash strip (matching
    // CodeGit.swift's exact order), so a URL ending "…bar.git/" does NOT get
    // its ".git" stripped — the slash comes off first, leaving "bar.git" as
    // the last path component. Faithful to the original, not a bug.
    [InlineData("https://github.com/owner/bar.git/", "bar.git")]
    [InlineData("https://github.com/owner/baz", "baz")]
    [InlineData("", "repo")]
    public void RepoNameInfersAFolderNameFromACloneUrl(string url, string expected) =>
        Assert.Equal(expected, CodeGit.RepoName(url));

    [Fact]
    public async Task DiffAsyncParsesRealGitOutput()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>
        {
            "@@ -1,1 +1,1 @@",
            "-old",
            "+new",
        }));

        var lines = await CodeGit.DiffAsync(launcher, "/repo", "a.txt", resolveBinary: n => n == "git" ? "/usr/bin/git" : null);

        Assert.NotNull(lines);
        Assert.Equal(3, lines!.Count);
        Assert.Contains("-C", launcher.LastStart!.Value.Args);
        Assert.Contains("/repo", launcher.LastStart.Value.Args);
    }

    [Fact]
    public async Task DiffAsyncReturnsNullWhenGitIsNotFound()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));

        var lines = await CodeGit.DiffAsync(launcher, "/repo", "a.txt", resolveBinary: _ => null);

        Assert.Null(lines);
        Assert.Null(launcher.LastStart);
    }

    [Fact]
    public async Task CurrentBranchAsyncReturnsTheTrimmedBranchName()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string> { "main" }));

        var branch = await CodeGit.CurrentBranchAsync(launcher, "/repo", resolveBinary: n => n == "git" ? "/usr/bin/git" : null);

        Assert.Equal("main", branch);
    }

    [Fact]
    public async Task HasUncommittedChangesAsyncReflectsPorcelainOutput()
    {
        var dirty = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string> { " M a.txt" }));
        Assert.True(await CodeGit.HasUncommittedChangesAsync(dirty, "/repo", resolveBinary: n => n == "git" ? "/usr/bin/git" : null));

        var clean = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));
        Assert.False(await CodeGit.HasUncommittedChangesAsync(clean, "/repo", resolveBinary: n => n == "git" ? "/usr/bin/git" : null));
    }

    [Fact]
    public async Task ChangedFilesAsyncFillsLineCountsForUntrackedFiles()
    {
        var callCount = 0;
        var launcher = new FakeProcessLauncher((_, args) =>
        {
            callCount++;
            // First call: --numstat (empty — the file is untracked, not in `diff HEAD`).
            // Second call: status --porcelain -z.
            return args.Contains("--numstat")
                ? new FakeChildProcess(new List<string>())
                : new FakeChildProcess(new List<string> { "?? new.txt\0" });
        });

        var files = await CodeGit.ChangedFilesAsync(launcher, "/repo",
            resolveBinary: n => n == "git" ? "/usr/bin/git" : null,
            readUntrackedFileContent: _ => "line1\nline2\nline3");

        var file = Assert.Single(files);
        Assert.Equal("new.txt", file.Path);
        Assert.Equal(ChangedFileKind.Untracked, file.Kind);
        Assert.Equal(3, file.Insertions);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public void IsAvailableReflectsWhetherGitResolves()
    {
        Assert.True(CodeGit.IsAvailable(n => n == "git" ? "/usr/bin/git" : null));
        Assert.False(CodeGit.IsAvailable(_ => null));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task StageUnstageAndRevertReflectTheExitCode(int exitCode, bool expected)
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>(), exitCode: exitCode));
        var resolveBinary = (Func<string, string?>)(n => n == "git" ? "/usr/bin/git" : null);

        Assert.Equal(expected, await CodeGit.StageAsync(launcher, "/repo", "a.txt", resolveBinary));
        Assert.Equal(expected, await CodeGit.UnstageAsync(launcher, "/repo", "a.txt", resolveBinary));
        Assert.Equal(expected, await CodeGit.RevertAsync(launcher, "/repo", "a.txt", resolveBinary));
    }

    [Fact]
    public async Task StagedFilesAsyncJoinsNulSeparatedPathsWithTheRepoRoot()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string> { "a.txt\0sub/b.txt\0" }));

        var staged = await CodeGit.StagedFilesAsync(launcher, "/repo", n => n == "git" ? "/usr/bin/git" : null);

        Assert.Equal(new HashSet<string> { Path.Combine("/repo", "a.txt"), Path.Combine("/repo", "sub/b.txt") }, staged);
    }

    [Fact]
    public async Task CommitAsyncStagesEverythingThenCommits()
    {
        var calls = new List<IReadOnlyList<string>>();
        var launcher = new FakeProcessLauncher((_, args) =>
        {
            calls.Add(args);
            return new FakeChildProcess(new List<string> { "[main abc123] msg" });
        });

        var (ok, output) = await CodeGit.CommitAsync(launcher, "/repo", "msg", n => n == "git" ? "/usr/bin/git" : null);

        Assert.True(ok);
        Assert.Contains("abc123", output);
        Assert.Equal(2, calls.Count);
        Assert.Contains("-A", calls[0]);
        Assert.Contains("commit", calls[1]);
    }

    [Fact]
    public async Task CommitStagedAsyncDoesNotStageFirst()
    {
        var calls = new List<IReadOnlyList<string>>();
        var launcher = new FakeProcessLauncher((_, args) =>
        {
            calls.Add(args);
            return new FakeChildProcess(new List<string>(), exitCode: 1, stderr: "nothing to commit");
        });

        var (ok, output) = await CodeGit.CommitStagedAsync(launcher, "/repo", "msg", n => n == "git" ? "/usr/bin/git" : null);

        Assert.False(ok);
        Assert.Contains("nothing to commit", output);
        Assert.Single(calls); // no "add -A" call
    }

    [Fact]
    public async Task PushAsyncPushesTheCurrentBranchWithUpstream()
    {
        var launcher = new FakeProcessLauncher((_, args) =>
            args.Contains("rev-parse")
                ? new FakeChildProcess(new List<string> { "feature-x" })
                : new FakeChildProcess(new List<string> { "branch set up to track" }));

        var (ok, output) = await CodeGit.PushAsync(launcher, "/repo", n => n == "git" ? "/usr/bin/git" : null);

        Assert.True(ok);
        Assert.Contains("track", output);
    }

    [Fact]
    public async Task BranchesAsyncListsNonEmptyTrimmedNames()
    {
        var launcher = new FakeProcessLauncher((_, _) =>
            new FakeChildProcess(new List<string> { "main", "  feature-x  ", "" }));

        var branches = await CodeGit.BranchesAsync(launcher, "/repo", n => n == "git" ? "/usr/bin/git" : null);

        Assert.Equal(new List<string> { "main", "feature-x" }, branches);
    }

    [Fact]
    public async Task CreateBranchAndCheckoutReflectSuccess()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string> { "Switched" }));
        var resolveBinary = (Func<string, string?>)(n => n == "git" ? "/usr/bin/git" : null);

        var (createOk, _) = await CodeGit.CreateBranchAsync(launcher, "/repo", "feature-x", resolveBinary);
        var (checkoutOk, _) = await CodeGit.CheckoutAsync(launcher, "/repo", "main", resolveBinary);

        Assert.True(createOk);
        Assert.True(checkoutOk);
    }

    [Fact]
    public async Task WriteOperationsReportGitNotFoundWhenGitIsMissing()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));

        Assert.False(await CodeGit.StageAsync(launcher, "/repo", "a.txt", _ => null));
        var (ok, output) = await CodeGit.CommitAsync(launcher, "/repo", "msg", _ => null);
        Assert.False(ok);
        Assert.Equal("git not found", output);
        Assert.Null(launcher.LastStart);
    }
}
