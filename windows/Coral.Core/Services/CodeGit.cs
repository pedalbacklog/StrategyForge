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
/// <see cref="CheckoutAsync"/>) — all one-shot subprocess calls via the
/// existing <see cref="IProcessLauncher"/>, no new Windows primitive needed
/// the way ConPTY was for Fase 3, and just as unit-testable against a fake as
/// the read-only half — "no UI consumes this yet" turned out not to be a good
/// reason to leave these unported when a fake proves the argument-building
/// and exit-code handling are correct regardless of who calls them.
///
/// DEFERRED, deliberately, and each for a real reason (not "hasn't gotten to
/// it yet"):
/// - <c>fullDiff</c> (the whole uncommitted diff incl. untracked files, with
///   per-file size capping) — only has a consumer once an automated diff
///   reviewer is ported, which hasn't happened yet; the shape of what it
///   needs isn't settled.
/// - <c>clone</c> (repo lifecycle: local folder naming/dedup, directory
///   creation) — meaningfully different code shape from everything else here
///   (no existing repo to <c>-C</c> into), best built alongside the actual
///   "add a repo" UI flow that would drive it rather than speculatively now.
/// - Every WORKTREE operation (addWorktree/mergeNoFF/commitAll/removeWorktree/
///   deleteBranch) — these exist ONLY for loop isolation, which is Fase 8's
///   explicitly vetoed zone (CLAUDE.md: loop-related changes need a human
///   reading the diff, not just tests). Porting them here would put loop
///   plumbing outside that review gate — a firm boundary, not a scheduling
///   choice.
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

    // MARK: - Write operations (Code Mode git panel)

    /// <summary>Whether <c>git</c> is available at all (for gating clone/push
    /// UI). Port of <c>CodeGit.swift</c>'s <c>isAvailable</c>.</summary>
    public static bool IsAvailable(Func<string, string?>? resolveBinary = null) =>
        (resolveBinary ?? BinaryResolver.Resolve)("git") is not null;

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
