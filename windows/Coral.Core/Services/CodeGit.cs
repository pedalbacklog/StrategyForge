namespace Coral.Core.Services;

/// <summary>One rendered line of a unified diff. Port of
/// <c>CodeGit.swift</c>'s <c>DiffLine</c>.</summary>
public sealed record DiffLine(int Id, DiffLineKind Kind, int? OldNumber, int? NewNumber, string Text);

public enum DiffLineKind { Hunk, Add, Del, Context }

/// <summary>One changed file in the working tree, with its +/− counts and
/// what kind of change it is. Port of <c>CodeGit.swift</c>'s
/// <c>ChangedFile</c>.</summary>
public sealed record ChangedFile(string Path, int Insertions, int Deletions, ChangedFileKind Kind);

public enum ChangedFileKind { Modified, Added, Deleted, Untracked, Renamed }

/// <summary>The working branch plus its +/− against the repo's default
/// branch — what a PR would show, including uncommitted work. Port of
/// <c>CodeGit.swift</c>'s <c>BranchStat</c>.</summary>
public sealed record BranchStat(string Branch, string Base, int Insertions, int Deletions)
{
    public bool IsOnBase => Branch == Base;
}

/// <summary>
/// Port of <c>StrategyForge/Services/CodeGit.swift</c> — Fase 7 (Code mode)'s
/// git helper. SCOPE, decided the same way earlier phases were scoped down
/// (see windows/PORT-PLAN.md §6):
///
/// PORTED — the pure diff/status parsers (<see cref="Parse"/>,
/// <see cref="ParseChangedFiles"/>, <see cref="ParseShortstat"/>,
/// <see cref="RepoName"/>); the READ-ONLY real-git operations
/// (<see cref="DiffAsync"/>, <see cref="CurrentBranchAsync"/>,
/// <see cref="BranchStatAsync"/>, <see cref="ChangedFilesAsync"/>,
/// <see cref="HasUncommittedChangesAsync"/>); and the git-panel WRITE
/// operations on an existing repo (<see cref="StageAsync"/>,
/// <see cref="UnstageAsync"/>, <see cref="RevertAsync"/>,
/// <see cref="StagedFilesAsync"/>, <see cref="CommitAsync"/>,
/// <see cref="CommitStagedAsync"/>, <see cref="PushAsync"/>,
/// <see cref="CreateBranchAsync"/>, <see cref="BranchesAsync"/>,
/// <see cref="CheckoutAsync"/>); and now <see cref="CloneAsync"/> too — all
/// one-shot subprocess calls via the existing <see cref="IProcessLauncher"/>,
/// no new Windows primitive needed the way ConPTY was for Fase 3, and just as
/// unit-testable against a fake as the read-only half — "no UI consumes this
/// yet" turned out not to be a good reason to leave any of these unported
/// when a fake proves the argument-building and exit-code handling are
/// correct regardless of who calls them (the same reversal that already
/// applied to the write operations applies here too).
///
/// UPDATE (2026-08-21): both items below WERE ported, once each had a real
/// consumer (the "run the current team for real, isolated in a worktree"
/// work — see <c>TeamRunEngine.cs</c>). The worktree deferral's original reason
/// no longer held up under a check: grepping the Swift source shows
/// <c>addWorktree</c>/<c>mergeNoFF</c>/<c>commitAll</c>/<c>removeWorktree</c>/
/// <c>deleteBranch</c> are called from THREE places — <c>LoopRunner.swift</c>
/// (Fase 8, still vetoed), but ALSO <c>CodeArenaEngine.swift</c> (the Code
/// Arena feature) and <c>ChatViewModel.swift</c> (its own worktree-isolated-
/// turn toggle) — neither of which is Loop code. These are generic `git
/// worktree`/`git merge`/`git branch` wrappers with no reference to
/// <c>LoopPlan</c>/<c>LoopScheduler</c>/<c>LoopRunner</c>/<c>LoopFileGenerator</c>
/// (the four files CLAUDE.md actually names) — being a Loop DEPENDENCY isn't
/// the same as BEING Loop code, and conflating the two was overcautious, not
/// the firm boundary the earlier note claimed. Ported below:
/// <see cref="FullDiffAsync"/>, <see cref="AddWorktreeAsync"/>,
/// <see cref="CommitAllAsync"/>, <see cref="MergeNoFFAsync"/>,
/// <see cref="RemoveWorktreeAsync"/>, <see cref="DeleteBranchAsync"/>.
/// </summary>
public static class CodeGit
{
    /// <summary>Parse a unified diff into displayable lines with old/new line
    /// numbers. Port of <c>CodeGit.swift</c>'s <c>parse(_:)</c>.</summary>
    public static IReadOnlyList<DiffLine> Parse(string diff)
    {
        var result = new List<DiffLine>();
        var oldNum = 0;
        var newNum = 0;

        foreach (var raw in diff.Split('\n'))
        {
            if (raw.StartsWith("diff --git", StringComparison.Ordinal) || raw.StartsWith("index ", StringComparison.Ordinal)
                || raw.StartsWith("--- ", StringComparison.Ordinal) || raw.StartsWith("+++ ", StringComparison.Ordinal)
                || raw.StartsWith("new file", StringComparison.Ordinal) || raw.StartsWith("deleted file", StringComparison.Ordinal)
                || raw.StartsWith("similarity", StringComparison.Ordinal) || raw.StartsWith("rename ", StringComparison.Ordinal))
            {
                continue;
            }

            if (raw.StartsWith("@@", StringComparison.Ordinal))
            {
                var starts = HunkStarts(raw);
                if (starts is not null) (oldNum, newNum) = starts.Value;
                result.Add(new DiffLine(result.Count, DiffLineKind.Hunk, null, null, raw));
                continue;
            }

            if (raw.StartsWith('+'))
            {
                result.Add(new DiffLine(result.Count, DiffLineKind.Add, null, newNum, raw[1..]));
                newNum++;
            }
            else if (raw.StartsWith('-'))
            {
                result.Add(new DiffLine(result.Count, DiffLineKind.Del, oldNum, null, raw[1..]));
                oldNum++;
            }
            else
            {
                var text = raw.StartsWith(' ') ? raw[1..] : raw;
                result.Add(new DiffLine(result.Count, DiffLineKind.Context, oldNum, newNum, text));
                oldNum++; newNum++;
            }
        }
        return result;
    }

    private static (int Old, int New)? HunkStarts(string header)
    {
        // e.g. "@@ -12,7 +12,9 @@ func foo()"
        var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return null;
        int? Start(string token)
        {
            var body = token[1..]; // drop +/-
            var n = body.Split(',')[0];
            return int.TryParse(n, out var v) ? v : null;
        }
        var o = Start(parts[1]);
        var n = Start(parts[2]);
        return o is null || n is null ? null : (o.Value, n.Value);
    }

    /// <summary>Pure parser for <c>git diff HEAD --numstat</c> +
    /// <c>git status --porcelain -z</c>, so path edge cases (spaces,
    /// non-ASCII, renames, binaries) are unit-testable without a repo.
    /// Untracked files come back with 0/0 (the caller fills their line
    /// counts). Port of <c>CodeGit.swift</c>'s <c>parseChangedFiles(numstat:
    /// statusZ:)</c>.</summary>
    public static IReadOnlyList<ChangedFile> ParseChangedFiles(string numstat, string statusZ)
    {
        var stats = new Dictionary<string, (int Add, int Del)>();
        foreach (var line in numstat.Split('\n'))
        {
            if (line.Length == 0) continue;
            var p = line.Split('\t', 3);
            if (p.Length != 3) continue;
            // Binary files show "-" for the counts → treat as 0.
            stats[p[2]] = (int.TryParse(p[0], out var a) ? a : 0, int.TryParse(p[1], out var d) ? d : 0);
        }

        // NUL-separated records; a rename/copy is TWO records (new path, then old path).
        var fields = statusZ.Split('\0');
        var files = new List<ChangedFile>();
        var i = 0;
        while (i < fields.Length)
        {
            var entry = fields[i]; i++;
            if (entry.Length <= 3) continue;
            var xy = entry[..2];
            var path = entry[3..];
            if ((xy.Contains('R') || xy.Contains('C')) && i < fields.Length) i++; // consume the old path

            var kind = xy switch
            {
                "??" => ChangedFileKind.Untracked,
                _ when xy.Contains('R') => ChangedFileKind.Renamed,
                _ when xy.Contains('D') => ChangedFileKind.Deleted,
                _ when xy.Contains('A') => ChangedFileKind.Added,
                _ => ChangedFileKind.Modified,
            };
            var (add, del) = stats.TryGetValue(path, out var s) ? s : (0, 0);
            files.Add(new ChangedFile(path, add, del, kind));
        }
        return files;
    }

    /// <summary>Parse <c>git diff --shortstat</c> ("… 42 insertions(+), 9
    /// deletions(-)"). Port of <c>CodeGit.swift</c>'s <c>parseShortstat</c>.</summary>
    public static (int Insertions, int Deletions) ParseShortstat(string s)
    {
        int Num(string keyword)
        {
            var m = System.Text.RegularExpressions.Regex.Match(s, @"(\d+) " + keyword);
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }
        return (Num("insertion"), Num("deletion"));
    }

    /// <summary>A local folder name inferred from a clone URL
    /// ("git@…/foo.git" → "foo"). Port of <c>CodeGit.swift</c>'s
    /// <c>repoName(from:)</c>.</summary>
    public static string RepoName(string url)
    {
        var s = url.Trim();
        if (s.EndsWith(".git", StringComparison.Ordinal)) s = s[..^4];
        while (s.EndsWith('/')) s = s[..^1];
        var last = s.Contains('/') ? s[(s.LastIndexOf('/') + 1)..] : s;
        return last.Length == 0 ? "repo" : last;
    }

    /// <summary>Unified diff of <paramref name="file"/> vs HEAD, parsed. Null
    /// if not a repo / no git / no diff. Port of <c>CodeGit.swift</c>'s
    /// <c>diff(repo:file:)</c>.</summary>
    public static async Task<IReadOnlyList<DiffLine>?> DiffAsync(IProcessLauncher launcher, string repo, string file,
        Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var git = (resolveBinary ?? BinaryResolver.Resolve)("git");
        if (git is null) return null;
        var (ok, stdout, _) = await RunGitAsync(launcher, git, repo, new[] { "diff", "--no-color", "HEAD", "--", file }, ct);
        return ok && stdout.Length > 0 ? Parse(stdout) : null;
    }

    /// <summary>Current branch name, or null if not a repo / no git. Port of
    /// <c>CodeGit.swift</c>'s <c>currentBranch(repo:)</c>.</summary>
    public static async Task<string?> CurrentBranchAsync(IProcessLauncher launcher, string repo,
        Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var git = (resolveBinary ?? BinaryResolver.Resolve)("git");
        if (git is null) return null;
        var (ok, stdout, _) = await RunGitAsync(launcher, git, repo, new[] { "rev-parse", "--abbrev-ref", "HEAD" }, ct);
        var name = stdout.Trim();
        return ok && name.Length > 0 ? name : null;
    }

    /// <summary>Port of <c>CodeGit.swift</c>'s <c>branchStat(repo:)</c>.</summary>
    public static async Task<BranchStat?> BranchStatAsync(IProcessLauncher launcher, string repo,
        Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        resolveBinary ??= BinaryResolver.Resolve;
        var git = resolveBinary("git");
        if (git is null) return null;

        var (branchOk, branchOut, _) = await RunGitAsync(launcher, git, repo, new[] { "rev-parse", "--abbrev-ref", "HEAD" }, ct);
        var branch = branchOut.Trim();
        if (!branchOk || branch.Length == 0) return null;

        var baseBranch = "main";
        var (headOk, headOut, _) = await RunGitAsync(launcher, git, repo,
            new[] { "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD" }, ct);
        if (headOk)
        {
            var refName = headOut.Trim(); // "origin/main"
            var slash = refName.LastIndexOf('/');
            if (slash >= 0) baseBranch = refName[(slash + 1)..];
        }

        var insertions = 0;
        var deletions = 0;
        if (branch != baseBranch)
        {
            var (ok, diffOut, _) = await RunGitAsync(launcher, git, repo, new[] { "diff", "--shortstat", $"{baseBranch}...HEAD" }, ct);
            if (ok) { var (i, d) = ParseShortstat(diffOut); insertions += i; deletions += d; }
        }
        var (wtOk, wtOut, _) = await RunGitAsync(launcher, git, repo, new[] { "diff", "--shortstat" }, ct);
        if (wtOk) { var (i, d) = ParseShortstat(wtOut); insertions += i; deletions += d; }

        return new BranchStat(branch, baseBranch, insertions, deletions);
    }

    /// <summary>Every changed file in <paramref name="repo"/>, each with
    /// +insertions / −deletions and a change kind. Port of
    /// <c>CodeGit.swift</c>'s <c>changedFiles(repo:)</c>.
    /// <paramref name="readUntrackedFileContent"/> is injectable so untracked
    /// files' line counts are testable without touching real disk; defaults
    /// to a real file read.</summary>
    public static async Task<IReadOnlyList<ChangedFile>> ChangedFilesAsync(IProcessLauncher launcher, string repo,
        Func<string, string?>? resolveBinary = null, Func<string, string?>? readUntrackedFileContent = null,
        CancellationToken ct = default)
    {
        resolveBinary ??= BinaryResolver.Resolve;
        var git = resolveBinary("git");
        if (git is null) return Array.Empty<ChangedFile>();

        var (numOk, numOut, _) = await RunGitAsync(launcher, git, repo,
            new[] { "-c", "core.quotePath=false", "diff", "HEAD", "--numstat" }, ct);
        var (statusOk, statusOut, _) = await RunGitAsync(launcher, git, repo,
            new[] { "-c", "core.quotePath=false", "status", "--porcelain", "-z" }, ct);
        if (!statusOk) return Array.Empty<ChangedFile>();

        readUntrackedFileContent ??= p => File.Exists(p) ? File.ReadAllText(p) : null;

        var files = ParseChangedFiles(numOk ? numOut : "", statusOut)
            .Select(f =>
            {
                if (f.Kind != ChangedFileKind.Untracked || f.Insertions != 0 || f.Deletions != 0) return f;
                var full = Path.Combine(repo, f.Path);
                var content = readUntrackedFileContent(full);
                if (string.IsNullOrEmpty(content)) return f;
                var lineCount = content.Split('\n').Length;
                return f with { Insertions = lineCount };
            })
            .OrderBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

        return files;
    }

    /// <summary>True if the working tree has uncommitted changes. Port of
    /// <c>CodeGit.swift</c>'s <c>hasUncommittedChanges(repo:)</c>.</summary>
    public static async Task<bool> HasUncommittedChangesAsync(IProcessLauncher launcher, string repo,
        Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var git = (resolveBinary ?? BinaryResolver.Resolve)("git");
        if (git is null) return false;
        var (ok, stdout, _) = await RunGitAsync(launcher, git, repo, new[] { "status", "--porcelain" }, ct);
        return ok && stdout.Trim().Length > 0;
    }

    /// <summary>Largest untracked file diffed inline before it's noted with a
    /// stub hunk instead — a huge generated/vendored file would otherwise
    /// balloon a reviewer prompt for no benefit. Matches
    /// <c>CodeGit.swift</c>'s <c>fullDiff</c>.</summary>
    private const int FullDiffMaxUntrackedFileBytes = 256 * 1024;

    /// <summary>The WHOLE uncommitted diff as raw unified-diff text (null if
    /// none / not a repo): tracked changes (<c>git diff HEAD</c>) PLUS each
    /// untracked file diffed against <c>/dev/null</c> — never-staged new
    /// files (the typical agent output) are invisible to <c>diff HEAD</c> but
    /// WILL be committed by a default <c>add -A</c>, so a diff reviewer must
    /// see them too. Port of <c>CodeGit.swift</c>'s <c>fullDiff(repo:)</c>.</summary>
    public static async Task<string?> FullDiffAsync(IProcessLauncher launcher, string repo,
        Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        resolveBinary ??= BinaryResolver.Resolve;
        var git = resolveBinary("git");
        if (git is null) return null;

        var pieces = new List<string>();
        // Fails on an unborn HEAD — treat that as "no tracked changes" and
        // still report untracked files.
        var (trackedOk, trackedOut, _) = await RunGitAsync(launcher, git, repo, new[] { "diff", "--no-color", "HEAD" }, ct);
        if (trackedOk && trackedOut.Trim().Length > 0) pieces.Add(trackedOut);

        var (untrackedOk, untrackedOut, _) = await RunGitAsync(launcher, git, repo,
            new[] { "-c", "core.quotePath=false", "ls-files", "--others", "--exclude-standard", "-z" }, ct);
        if (untrackedOk)
        {
            foreach (var rel in untrackedOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var abs = Path.Combine(repo, rel);
                var size = File.Exists(abs) ? new FileInfo(abs).Length : 0;
                if (size > FullDiffMaxUntrackedFileBytes)
                {
                    pieces.Add($"diff --git a/{rel} b/{rel}\nnew file, {size} bytes — too large to inline for review\n");
                    continue;
                }
                // --no-index exits 1 when the files differ (the expected case
                // here) and 0 when they're identical (an empty untracked
                // file) — accept either as long as there's output, matching
                // the Swift original's r.status == 0 || r.status == 1 check.
                var (_, diffOut, _) = await RunGitAsync(launcher, git, repo,
                    new[] { "diff", "--no-color", "--no-index", "--", "/dev/null", rel }, ct);
                if (diffOut.Length > 0) pieces.Add(diffOut);
            }
        }

        var full = string.Concat(pieces);
        return full.Trim().Length == 0 ? null : full;
    }

    // MARK: - Worktree operations (isolated team runs — see TeamRunEngine.cs)

    /// <summary>Create a new worktree at <paramref name="path"/> on a fresh
    /// <paramref name="branch"/> off HEAD. Port of <c>CodeGit.swift</c>'s
    /// <c>addWorktree(repo:path:branch:)</c>.</summary>
    public static async Task<(bool Ok, string Output)> AddWorktreeAsync(IProcessLauncher launcher, string repo,
        string path, string branch, Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var git = (resolveBinary ?? BinaryResolver.Resolve)("git");
        if (git is null) return (false, "git not found");
        var (ok, stdout, stderr) = await RunGitAsync(launcher, git, repo, new[] { "worktree", "add", "-b", branch, path, "HEAD" }, ct);
        return (ok, CombineOutput(stdout, stderr));
    }

    /// <summary>Stage everything and commit in <paramref name="dir"/>
    /// (typically a worktree). <c>Ok == false</c> when there was nothing to
    /// commit — the caller treats that as "no work produced". Port of
    /// <c>CodeGit.swift</c>'s <c>commitAll(dir:message:)</c>.</summary>
    public static async Task<(bool Ok, string Output)> CommitAllAsync(IProcessLauncher launcher, string dir,
        string message, Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var git = (resolveBinary ?? BinaryResolver.Resolve)("git");
        if (git is null) return (false, "git not found");
        _ = await RunGitAsync(launcher, git, dir, new[] { "add", "-A" }, ct);
        var (ok, stdout, stderr) = await RunGitAsync(launcher, git, dir, new[] { "commit", "-m", message }, ct);
        return (ok, CombineOutput(stdout, stderr));
    }

    /// <summary>Merge <paramref name="branch"/> into whatever <paramref name="repo"/>
    /// has checked out, no-ff so the work stays a reviewable unit. On
    /// conflict git leaves a merge in progress — the caller decides whether
    /// to resolve or abort. Port of <c>CodeGit.swift</c>'s
    /// <c>mergeNoFF(repo:branch:message:)</c>.</summary>
    public static async Task<(bool Ok, string Output)> MergeNoFFAsync(IProcessLauncher launcher, string repo,
        string branch, string message, Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var git = (resolveBinary ?? BinaryResolver.Resolve)("git");
        if (git is null) return (false, "git not found");
        var (ok, stdout, stderr) = await RunGitAsync(launcher, git, repo, new[] { "merge", "--no-ff", branch, "-m", message }, ct);
        return (ok, CombineOutput(stdout, stderr));
    }

    /// <summary>Remove a worktree (force, since it may hold committed-but-
    /// unmerged work kept intentionally on its branch). Best-effort. Port of
    /// <c>CodeGit.swift</c>'s <c>removeWorktree(repo:path:)</c>.</summary>
    public static async Task RemoveWorktreeAsync(IProcessLauncher launcher, string repo, string path,
        Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var git = (resolveBinary ?? BinaryResolver.Resolve)("git");
        if (git is null) return;
        _ = await RunGitAsync(launcher, git, repo, new[] { "worktree", "remove", path, "--force" }, ct);
    }

    /// <summary>Delete a local branch (cleanup after a merge or a discard).
    /// Best-effort. Port of <c>CodeGit.swift</c>'s
    /// <c>deleteBranch(repo:name:)</c>.</summary>
    public static async Task DeleteBranchAsync(IProcessLauncher launcher, string repo, string name,
        Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var git = (resolveBinary ?? BinaryResolver.Resolve)("git");
        if (git is null) return;
        _ = await RunGitAsync(launcher, git, repo, new[] { "branch", "-D", name }, ct);
    }

    // MARK: - Write operations (Code Mode git panel)

    /// <summary>Whether <c>git</c> is available at all (for gating clone/push
    /// UI). Port of <c>CodeGit.swift</c>'s <c>isAvailable</c>.</summary>
    public static bool IsAvailable(Func<string, string?>? resolveBinary = null) =>
        (resolveBinary ?? BinaryResolver.Resolve)("git") is not null;

    // MARK: - Repo lifecycle (clone)

    /// <summary>Clone <paramref name="url"/> into <c>parentDir/&lt;name&gt;</c>
    /// (never clobbering an existing folder — appends <c>-2</c>, <c>-3</c>, …
    /// until the destination is free). Returns the local path on success.
    /// Relies on the user's own git credentials. Port of <c>CodeGit.swift</c>'s
    /// <c>clone(url:into:)</c>. <paramref name="createDirectory"/>/
    /// <paramref name="pathExists"/> are injectable so the folder-dedup logic
    /// is testable without touching real disk; default to real
    /// <see cref="Directory"/> calls.</summary>
    public static async Task<(bool Ok, string? Path, string Output)> CloneAsync(IProcessLauncher launcher,
        string url, string parentDir, Func<string, string?>? resolveBinary = null,
        Action<string>? createDirectory = null, Func<string, bool>? pathExists = null, CancellationToken ct = default)
    {
        var git = (resolveBinary ?? BinaryResolver.Resolve)("git");
        if (git is null) return (false, null, "git not found");

        createDirectory ??= p => Directory.CreateDirectory(p);
        pathExists ??= p => Directory.Exists(p) || File.Exists(p);
        try { createDirectory(parentDir); } catch { /* best-effort, matches Swift's try? */ }

        var basePath = Path.Combine(parentDir, RepoName(url));
        var dest = basePath;
        var n = 2;
        while (pathExists(dest)) { dest = $"{basePath}-{n}"; n++; }

        var (ok, stdout, stderr) = await OneShotProcess.RunAsync(launcher, git, new[] { "clone", "--", url, dest }, parentDir, ct: ct);
        return (ok, ok ? dest : null, OneShotProcess.CombineOutput(stdout, stderr));
    }

    /// <summary>Discard an agent's changes to one file. Port of
    /// <c>CodeGit.swift</c>'s <c>revert(repo:file:)</c>.</summary>
    public static Task<bool> RevertAsync(IProcessLauncher launcher, string repo, string file,
        Func<string, string?>? resolveBinary = null, CancellationToken ct = default) =>
        RunSimpleGitAsync(launcher, repo, new[] { "checkout", "--", file }, resolveBinary, ct);

    /// <summary>Stage a file. Port of <c>CodeGit.swift</c>'s <c>stage(repo:file:)</c>.</summary>
    public static Task<bool> StageAsync(IProcessLauncher launcher, string repo, string file,
        Func<string, string?>? resolveBinary = null, CancellationToken ct = default) =>
        RunSimpleGitAsync(launcher, repo, new[] { "add", "--", file }, resolveBinary, ct);

    /// <summary>Unstage a file. Port of <c>CodeGit.swift</c>'s
    /// <c>unstage(repo:file:)</c>.</summary>
    public static Task<bool> UnstageAsync(IProcessLauncher launcher, string repo, string file,
        Func<string, string?>? resolveBinary = null, CancellationToken ct = default) =>
        RunSimpleGitAsync(launcher, repo, new[] { "restore", "--staged", "--", file }, resolveBinary, ct);

    /// <summary>The set of currently-staged files, as ABSOLUTE paths (to
    /// match <c>ChangedFile</c>'s repo-relative paths joined with
    /// <paramref name="repo"/>). Port of <c>CodeGit.swift</c>'s
    /// <c>stagedFiles(repo:)</c>.</summary>
    public static async Task<IReadOnlySet<string>> StagedFilesAsync(IProcessLauncher launcher, string repo,
        Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var git = (resolveBinary ?? BinaryResolver.Resolve)("git");
        if (git is null) return new HashSet<string>();
        var (ok, stdout, _) = await RunGitAsync(launcher, git, repo,
            new[] { "-c", "core.quotePath=false", "diff", "--cached", "--name-only", "-z" }, ct);
        if (!ok) return new HashSet<string>();
        return stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Path.Combine(repo, p))
            .ToHashSet();
    }

    /// <summary>Commit only what's already staged (no <c>add -A</c>). Port of
    /// <c>CodeGit.swift</c>'s <c>commitStaged(repo:message:)</c>.</summary>
    public static async Task<(bool Ok, string Output)> CommitStagedAsync(IProcessLauncher launcher, string repo,
        string message, Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var git = (resolveBinary ?? BinaryResolver.Resolve)("git");
        if (git is null) return (false, "git not found");
        var (ok, stdout, stderr) = await RunGitAsync(launcher, git, repo, new[] { "commit", "-m", message }, ct);
        return (ok, CombineOutput(stdout, stderr));
    }

    /// <summary>Stage everything and commit. Port of <c>CodeGit.swift</c>'s
    /// <c>commit(repo:message:)</c>.</summary>
    public static async Task<(bool Ok, string Output)> CommitAsync(IProcessLauncher launcher, string repo,
        string message, Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var git = (resolveBinary ?? BinaryResolver.Resolve)("git");
        if (git is null) return (false, "git not found");
        _ = await RunGitAsync(launcher, git, repo, new[] { "add", "-A" }, ct);
        var (ok, stdout, stderr) = await RunGitAsync(launcher, git, repo, new[] { "commit", "-m", message }, ct);
        return (ok, CombineOutput(stdout, stderr));
    }

    /// <summary>Push the current branch to origin, setting upstream. Port of
    /// <c>CodeGit.swift</c>'s <c>push(repo:)</c>.</summary>
    public static async Task<(bool Ok, string Output)> PushAsync(IProcessLauncher launcher, string repo,
        Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var git = (resolveBinary ?? BinaryResolver.Resolve)("git");
        if (git is null) return (false, "git not found");
        var (_, branchOut, _) = await RunGitAsync(launcher, git, repo, new[] { "rev-parse", "--abbrev-ref", "HEAD" }, ct);
        var branch = branchOut.Trim();
        var (ok, stdout, stderr) = await RunGitAsync(launcher, git, repo, new[] { "push", "-u", "origin", branch }, ct);
        return (ok, CombineOutput(stdout, stderr));
    }

    /// <summary>Create a branch off HEAD and switch to it. Port of
    /// <c>CodeGit.swift</c>'s <c>createBranch(repo:name:)</c>.</summary>
    public static async Task<(bool Ok, string Output)> CreateBranchAsync(IProcessLauncher launcher, string repo,
        string name, Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var git = (resolveBinary ?? BinaryResolver.Resolve)("git");
        if (git is null) return (false, "git not found");
        var (ok, stdout, stderr) = await RunGitAsync(launcher, git, repo, new[] { "checkout", "-b", name }, ct);
        return (ok, CombineOutput(stdout, stderr));
    }

    /// <summary>List local branches (current first, per git's own default
    /// ordering). Port of <c>CodeGit.swift</c>'s <c>branches(repo:)</c>.</summary>
    public static async Task<IReadOnlyList<string>> BranchesAsync(IProcessLauncher launcher, string repo,
        Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var git = (resolveBinary ?? BinaryResolver.Resolve)("git");
        if (git is null) return Array.Empty<string>();
        var (ok, stdout, _) = await RunGitAsync(launcher, git, repo, new[] { "branch", "--format=%(refname:short)" }, ct);
        if (!ok) return Array.Empty<string>();
        return stdout.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    /// <summary>Switch to an existing branch. Port of <c>CodeGit.swift</c>'s
    /// <c>checkout(repo:branch:)</c>.</summary>
    public static async Task<(bool Ok, string Output)> CheckoutAsync(IProcessLauncher launcher, string repo,
        string branch, Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var git = (resolveBinary ?? BinaryResolver.Resolve)("git");
        if (git is null) return (false, "git not found");
        var (ok, stdout, stderr) = await RunGitAsync(launcher, git, repo, new[] { "checkout", branch }, ct);
        return (ok, CombineOutput(stdout, stderr));
    }

    private static async Task<bool> RunSimpleGitAsync(IProcessLauncher launcher, string repo,
        IReadOnlyList<string> args, Func<string, string?>? resolveBinary, CancellationToken ct)
    {
        var git = (resolveBinary ?? BinaryResolver.Resolve)("git");
        if (git is null) return false;
        var (ok, _, _) = await RunGitAsync(launcher, git, repo, args, ct);
        return ok;
    }

    private static string CombineOutput(string stdout, string stderr) => OneShotProcess.CombineOutput(stdout, stderr);

    /// <summary>Run git with <c>-C repo</c> prefixed, via the shared
    /// <see cref="OneShotProcess"/> runner.</summary>
    private static async Task<(bool Ok, string Stdout, string Stderr)> RunGitAsync(
        IProcessLauncher launcher, string gitPath, string repo, IReadOnlyList<string> args, CancellationToken ct)
    {
        var fullArgs = new List<string>(args.Count + 2) { "-C", repo };
        fullArgs.AddRange(args);
        return await OneShotProcess.RunAsync(launcher, gitPath, fullArgs, repo, GitEnvironment(), ct);
    }

    /// <summary>Never let git block on an interactive prompt (a repo needing
    /// credentials would hang otherwise): <c>GIT_TERMINAL_PROMPT=0</c> is the
    /// portable switch git itself honors; <c>GCM_INTERACTIVE=never</c>
    /// additionally silences Git Credential Manager's GUI prompt, which ships
    /// by default with Git for Windows — more relevant here than on macOS.
    /// Swift's original also sets <c>GIT_ASKPASS=/usr/bin/true</c>, a
    /// macOS-specific no-op path with no portable Windows equivalent, so it's
    /// dropped rather than pointed at something that doesn't exist.</summary>
    private static Dictionary<string, string?> GitEnvironment() => new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GCM_INTERACTIVE"] = "never",
    };
}
