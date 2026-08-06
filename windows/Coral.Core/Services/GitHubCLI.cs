using System.Text.Json;

namespace Coral.Core.Services;

/// <summary>A pull request's headline status for the working branch. Port of
/// <c>GitHubCLI.swift</c>'s <c>PRInfo</c>.</summary>
public sealed record PRInfo(int Number, string State, string Url, string Title, bool IsDraft);

/// <summary>A GitHub repository the signed-in user can open (for the Code
/// launcher list). Port of <c>GitHubCLI.swift</c>'s <c>RepoRef</c>.</summary>
public sealed record RepoRef(string NameWithOwner, string Description, bool IsPrivate, string Url)
{
    public string Name => NameWithOwner.Contains('/')
        ? NameWithOwner[(NameWithOwner.LastIndexOf('/') + 1)..]
        : NameWithOwner;
}

/// <summary>
/// Port of <c>StrategyForge/Services/GitHubCLI.swift</c> — a tiny wrapper
/// around the <c>gh</c> CLI so Code Mode can open a pull request in one tap,
/// using the user's own <c>gh</c>/git credentials (no app tokens), the same
/// "bring your own login" bet the rest of Coral makes. SCOPE for this pass,
/// decided the same way earlier phases were scoped down (see
/// windows/PORT-PLAN.md §6):
///
/// PORTED — the Code Mode PR flow: <see cref="IsInstalled"/>,
/// <see cref="IsAuthenticatedAsync"/>, <see cref="CreatePRAsync"/>,
/// <see cref="PrInfoAsync"/>, <see cref="MergePRAsync"/>; and the repo
/// browse/create flow: <see cref="ListReposAsync"/>, <see cref="CreateRepoAsync"/>
/// — all one-shot subprocess calls via the shared <see cref="OneShotProcess"/>
/// runner (also used by <see cref="CodeGit"/>) — no new Windows primitive
/// needed, and just as fake-testable regardless of whether a repo-picker UI
/// consumes them yet (same reversal already applied to <see cref="CodeGit"/>'s
/// write operations and <c>clone</c>).
///
/// DEFERRED, deliberately:
/// - <c>searchCommunitySkills</c>/<c>RemoteSkill</c> — an entirely different
///   feature area (skills catalog discovery, not Code Mode) with
///   meaningfully more complex logic (bounded multi-round-trip API calls,
///   star-based ranking) that deserves its own scoped pass, not a drive-by
///   port alongside the PR/repo flow.
/// </summary>
public static class GitHubCLI
{
    /// <summary>Whether the GitHub CLI is installed at all (gate the PR button).</summary>
    public static bool IsInstalled(Func<string, string?>? resolveBinary = null) =>
        (resolveBinary ?? BinaryResolver.Resolve)("gh") is not null;

    /// <summary>Whether <c>gh auth status</c> succeeds (the user is logged
    /// in to GitHub).</summary>
    public static async Task<bool> IsAuthenticatedAsync(IProcessLauncher launcher,
        Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var gh = (resolveBinary ?? BinaryResolver.Resolve)("gh");
        if (gh is null) return false;
        var (ok, _, _) = await RunAsync(launcher, gh, null, new[] { "auth", "status" }, ct);
        return ok;
    }

    /// <summary>Open a pull request from the current branch of
    /// <paramref name="repo"/>. Returns the PR URL on success (<c>gh</c>
    /// prints it to stdout); <c>gh</c> resolves the base branch + remote
    /// itself. Port of <c>GitHubCLI.swift</c>'s <c>createPR(repo:title:body:)</c>.</summary>
    public static async Task<(bool Ok, string? Url, string Output)> CreatePRAsync(IProcessLauncher launcher,
        string repo, string title, string body, Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var gh = (resolveBinary ?? BinaryResolver.Resolve)("gh");
        if (gh is null) return (false, null, "GitHub CLI (gh) not found");
        var (ok, stdout, stderr) = await RunAsync(launcher, gh, repo, new[] { "pr", "create", "--title", title, "--body", body }, ct);
        return (ok, LastHttpsLine(stdout), OneShotProcess.CombineOutput(stdout, stderr));
    }

    /// <summary>The last line starting with <c>https://</c> in <paramref name="output"/>
    /// (gh prints the PR URL as its final line on success). Pure — port of
    /// the URL-extraction expression inside <c>createPR</c>.</summary>
    public static string? LastHttpsLine(string output) =>
        output.Split('\n').LastOrDefault(l => l.StartsWith("https://", StringComparison.Ordinal));

    /// <summary>The PR (if any) for <paramref name="branch"/>, with its
    /// state — for the composer branch bar. Null when <c>gh</c> is missing,
    /// unauthenticated, or no PR exists. Port of <c>GitHubCLI.swift</c>'s
    /// <c>prInfo(repo:branch:)</c>.</summary>
    public static async Task<PRInfo?> PrInfoAsync(IProcessLauncher launcher, string repo, string branch,
        Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var gh = (resolveBinary ?? BinaryResolver.Resolve)("gh");
        if (gh is null) return null;
        var (ok, stdout, _) = await RunAsync(launcher, gh, repo,
            new[] { "pr", "view", branch, "--json", "number,state,url,title,isDraft" }, ct);
        return ok ? ParsePRInfo(stdout) : null;
    }

    /// <summary>Pure, tolerant parser for <c>gh pr view --json ...</c>'s
    /// output — null on anything malformed rather than throwing, matching
    /// Swift's optional-cast tolerance. Port of the parsing inside
    /// <c>prInfo(repo:branch:)</c>.</summary>
    public static PRInfo? ParsePRInfo(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("number", out var numEl) || numEl.ValueKind != JsonValueKind.Number) return null;
            return new PRInfo(numEl.GetInt32(), GetString(root, "state") ?? "OPEN", GetString(root, "url") ?? "",
                GetString(root, "title") ?? "", GetBool(root, "isDraft"));
        }
    }

    private static string? GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement root, string property) =>
        root.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Merge the PR for <paramref name="branch"/> (squash by
    /// default) using the user's own gh auth. The caller re-reads
    /// <see cref="PrInfoAsync"/> afterwards to reflect MERGED. Port of
    /// <c>GitHubCLI.swift</c>'s <c>mergePR(repo:branch:squash:)</c>.</summary>
    public static async Task<(bool Ok, string Output)> MergePRAsync(IProcessLauncher launcher, string repo,
        string branch, bool squash = true, Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var gh = (resolveBinary ?? BinaryResolver.Resolve)("gh");
        if (gh is null) return (false, "GitHub CLI (gh) not found");
        var (ok, stdout, stderr) = await RunAsync(launcher, gh, repo,
            new[] { "pr", "merge", branch, squash ? "--squash" : "--merge", "--delete-branch=false" }, ct);
        return (ok, OneShotProcess.CombineOutput(stdout, stderr));
    }

    /// <summary>The signed-in user's repositories (newest first), via
    /// <c>gh repo list</c>. Empty when <c>gh</c> is missing/unauthenticated —
    /// the caller shows the manual clone field instead. Port of
    /// <c>GitHubCLI.swift</c>'s <c>listRepos(limit:)</c>.</summary>
    public static async Task<IReadOnlyList<RepoRef>> ListReposAsync(IProcessLauncher launcher, int limit = 50,
        Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var gh = (resolveBinary ?? BinaryResolver.Resolve)("gh");
        if (gh is null) return Array.Empty<RepoRef>();
        var (ok, stdout, _) = await RunAsync(launcher, gh, null,
            new[] { "repo", "list", "--limit", limit.ToString(), "--json", "nameWithOwner,description,isPrivate,url" }, ct);
        return ok ? ParseRepoList(stdout) : Array.Empty<RepoRef>();
    }

    /// <summary>Pure, tolerant parser for <c>gh repo list --json ...</c>'s
    /// output — skips any entry missing <c>nameWithOwner</c> rather than
    /// throwing. Port of the parsing inside <c>listRepos(limit:)</c>.</summary>
    public static IReadOnlyList<RepoRef> ParseRepoList(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Array.Empty<RepoRef>();
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<RepoRef>();
            var results = new List<RepoRef>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var nwo = GetString(entry, "nameWithOwner");
                if (string.IsNullOrEmpty(nwo)) continue;
                results.Add(new RepoRef(nwo, GetString(entry, "description") ?? "", GetBool(entry, "isPrivate"),
                    GetString(entry, "url") ?? $"https://github.com/{nwo}"));
            }
            return results;
        }
    }

    /// <summary>Create a NEW repo on GitHub under the signed-in account and
    /// clone it into <c>parentDir/&lt;name&gt;</c> — so you never have to
    /// leave for github.com to start one. Port of <c>GitHubCLI.swift</c>'s
    /// <c>createRepo(name:isPrivate:into:)</c>. <paramref name="createDirectory"/>/
    /// <paramref name="pathExists"/> are injectable the same way
    /// <see cref="CodeGit.CloneAsync"/>'s are, for testability without real disk.</summary>
    public static async Task<(bool Ok, string? Path, string Output)> CreateRepoAsync(IProcessLauncher launcher,
        string name, bool isPrivate, string parentDir, Func<string, string?>? resolveBinary = null,
        Action<string>? createDirectory = null, Func<string, bool>? pathExists = null, CancellationToken ct = default)
    {
        var gh = (resolveBinary ?? BinaryResolver.Resolve)("gh");
        if (gh is null) return (false, null, "GitHub CLI (gh) not found");

        createDirectory ??= p => Directory.CreateDirectory(p);
        pathExists ??= p => Directory.Exists(p) || File.Exists(p);
        try { createDirectory(parentDir); } catch { /* best-effort, matches Swift's try? */ }

        var (ok, stdout, stderr) = await RunAsync(launcher, gh, parentDir,
            new[] { "repo", "create", name, isPrivate ? "--private" : "--public", "--clone", "--add-readme" }, ct);
        var path = Path.Combine(parentDir, name);
        var success = ok && pathExists(path);
        return (success, success ? path : null, OneShotProcess.CombineOutput(stdout, stderr));
    }

    private static Task<(bool Ok, string Stdout, string Stderr)> RunAsync(IProcessLauncher launcher, string ghPath,
        string? cwd, IReadOnlyList<string> args, CancellationToken ct) =>
        OneShotProcess.RunAsync(launcher, ghPath, args, cwd ?? Environment.CurrentDirectory, ct: ct);
}
