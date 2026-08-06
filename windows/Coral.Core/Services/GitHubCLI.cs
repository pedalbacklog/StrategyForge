using System.Text.Json;

namespace Coral.Core.Services;

/// <summary>A pull request's headline status for the working branch. Port of
/// <c>GitHubCLI.swift</c>'s <c>PRInfo</c>.</summary>
public sealed record PRInfo(int Number, string State, string Url, string Title, bool IsDraft);

/// <summary>
/// Port of <c>StrategyForge/Services/GitHubCLI.swift</c> — a tiny wrapper
/// around the <c>gh</c> CLI so Code Mode can open a pull request in one tap,
/// using the user's own <c>gh</c>/git credentials (no app tokens), the same
/// "bring your own login" bet the rest of Coral makes. SCOPE for this pass,
/// decided the same way earlier phases were scoped down (see
/// windows/PORT-PLAN.md §6):
///
/// PORTED — the Code Mode PR flow itself: <see cref="IsInstalled"/>,
/// <see cref="IsAuthenticatedAsync"/>, <see cref="CreatePRAsync"/>,
/// <see cref="PrInfoAsync"/>, <see cref="MergePRAsync"/>, all one-shot
/// subprocess calls via the shared <see cref="OneShotProcess"/> runner (also
/// used by <see cref="CodeGit"/>) — no new Windows primitive needed.
///
/// DEFERRED, deliberately:
/// - <c>listRepos</c>/<c>RepoRef</c> and <c>createRepo</c> — these back a
///   repo-picker/launcher UI ("browse my GitHub repos", "create one from
///   scratch") that doesn't exist in this port yet (Fase 5's repo picker is
///   still its own pending follow-up); porting them now would be
///   speculative.
/// - <c>searchCommunitySkills</c>/<c>RemoteSkill</c> — an entirely different
///   feature area (skills catalog discovery, not Code Mode) with
///   meaningfully more complex logic (bounded multi-round-trip API calls,
///   star-based ranking) that deserves its own scoped pass, not a drive-by
///   port alongside the PR flow.
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

    private static Task<(bool Ok, string Stdout, string Stderr)> RunAsync(IProcessLauncher launcher, string ghPath,
        string? cwd, IReadOnlyList<string> args, CancellationToken ct) =>
        OneShotProcess.RunAsync(launcher, ghPath, args, cwd ?? Environment.CurrentDirectory, ct: ct);
}
