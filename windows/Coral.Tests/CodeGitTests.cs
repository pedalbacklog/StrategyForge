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
}
