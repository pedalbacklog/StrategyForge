namespace Coral.Core.Services;

/// <summary>
/// Port of <c>CodeModeView.swift</c>'s <c>commitAndPR(auto:)</c> — the engine
/// behind Code Mode's one-tap "Commit + PR" button, and (not wired up yet —
/// see <see cref="ViewModels.PullRequestViewModel"/>'s doc comment) the future
/// Auto-PR opt-in. Pure orchestration over the already-ported <see cref="CodeGit"/>/
/// <see cref="GitHubCLI"/> primitives, using <see cref="IProcessLauncher"/>
/// directly rather than depending on either ViewModel — keeps this fake-testable
/// on its own and avoids coupling <see cref="ViewModels.GitPanelViewModel"/> to
/// <see cref="ViewModels.PullRequestViewModel"/>, which Fase 7 has kept
/// deliberately separate throughout.
/// </summary>
public static class ShipFlow
{
    /// <param name="Ok">False if either the commit (a real failure, not
    /// "nothing to commit") or the push failed, or the PR create failed.</param>
    /// <param name="PrWasCreated">True if a new PR was opened this call; false
    /// if <paramref name="hadPR"/> meant the push alone was enough to update
    /// an existing one.</param>
    public readonly record struct Result(bool Ok, bool PrWasCreated, string? PrUrl, string? Error);

    /// <summary>Commit (all changes, or just what's staged), push, then open a
    /// PR — or, if <paramref name="hadPR"/>, skip opening one since the push
    /// alone updates gh's existing PR. Matches Swift's <c>(auto || staged.isEmpty)
    /// ? commit(all) : commitStaged</c> and its tolerance of a "nothing to
    /// commit" error (there may already be local commits to push/PR).</summary>
    public static async Task<Result> RunAsync(IProcessLauncher launcher, string repoPath,
        string commitMessage, string prTitle, string prBody, bool auto, bool anyStaged, bool hadPR,
        Func<string, string?>? resolveBinary = null, CancellationToken ct = default)
    {
        var stagedOnly = !auto && anyStaged;
        var (commitOk, commitOutput) = stagedOnly
            ? await CodeGit.CommitStagedAsync(launcher, repoPath, commitMessage, resolveBinary, ct)
            : await CodeGit.CommitAsync(launcher, repoPath, commitMessage, resolveBinary, ct);
        if (!commitOk && !commitOutput.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
        {
            return new Result(false, false, null, commitOutput);
        }

        var (pushOk, pushOutput) = await CodeGit.PushAsync(launcher, repoPath, resolveBinary, ct);
        if (!pushOk) return new Result(false, false, null, pushOutput);

        if (hadPR) return new Result(true, false, null, null);

        var (prOk, prUrl, prOutput) = await GitHubCLI.CreatePRAsync(launcher, repoPath, prTitle, prBody, resolveBinary, ct);
        return prOk ? new Result(true, true, prUrl, null) : new Result(false, true, null, prOutput);
    }
}
