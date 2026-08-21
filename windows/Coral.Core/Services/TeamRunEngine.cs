using Coral.Core.Generators;
using Coral.Core.Models;

namespace Coral.Core.Services;

public enum TeamRunState { Preparing, Running, Done, Failed }

/// <summary>One team run's outcome: the diff it produced, isolated in its
/// own worktree/branch until <see cref="TeamRunEngine.ApplyAsync"/> or
/// <see cref="TeamRunEngine.DiscardAsync"/> is called.</summary>
public sealed class TeamRunOutcome
{
    public TeamRunState State { get; set; } = TeamRunState.Preparing;
    public string Diff { get; set; } = "";
    public int Tokens { get; set; }
    public double CostUsd { get; set; }
    public bool Estimated { get; set; }
    /// <summary>Per-file authorship, populated only for a cross-provider run
    /// (empty for a Claude-native team, which has no per-line provenance to
    /// report — Claude Code's own Agent tool did the delegating).</summary>
    public List<FileProvenance> Authorship { get; set; } = new();
    public string Branch { get; set; } = "";
    public string WorktreePath { get; set; } = "";
    public string? Error { get; set; }

    public bool ProducedChanges => Diff.Trim().Length > 0;
}

/// <summary>
/// Runs the CURRENT team on a task for real, isolated in its own git
/// worktree off HEAD — never touching the user's own working tree until
/// <see cref="ApplyAsync"/> is explicitly called. A deliberately smaller
/// sibling of <c>StrategyForge/Services/CodeArenaEngine.swift</c>: that file
/// races MULTIPLE contestants (several providers/teams at once) with a full
/// comparison UI (<c>ArenaView.swift</c>, 1054 lines) — ported here is only
/// the single-contestant slice ("run the team I already have"), the part
/// that actually makes cross-provider mixing (<see cref="CrossProviderEditor"/>)
/// real instead of assignment-only. See PORT-PLAN.md for why the Arena
/// itself (multi-contestant racing + diff comparison) is a separate,
/// bigger, not-yet-started phase.
///
/// For a Claude-only team this still runs through <see cref="IOneShotRunner"/>
/// (one call with the orchestrator's model, cwd'd into the worktree) rather
/// than the interactive session — Claude Code's own Agent tool still
/// delegates to the subagent files <see cref="StrategyWriter"/> wrote,
/// exactly matching <c>CodeArenaEngine.swift</c>'s own non-cross-provider
/// branch.
/// </summary>
public static class TeamRunEngine
{
    /// <summary>Parent dir for run worktrees — app-owned scratch, never
    /// inside the user's repo. Matches <c>CodeArenaEngine.swift</c>'s
    /// <c>arenaRoot()</c> (App Support there, Local AppData here — same
    /// convention <c>AppSettings.cs</c> already uses).</summary>
    public static string RunRoot()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Coral", "run-worktrees");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Isolate <paramref name="strategy"/> in a fresh worktree off
    /// <paramref name="repo"/>'s HEAD, write its files there as a committed
    /// baseline, run it for real (cross-provider via
    /// <see cref="CrossProviderEditor"/>, or a single native call otherwise),
    /// then capture + commit the resulting diff. The worktree/branch are
    /// LEFT IN PLACE either way — the caller decides via
    /// <see cref="ApplyAsync"/>/<see cref="DiscardAsync"/>.</summary>
    public static async Task<TeamRunOutcome> RunAsync(
        string task, string repo, Strategy strategy, IOneShotRunner runner, IProcessLauncher gitLauncher,
        string binary = "claude", Func<string, string?>? resolveGitBinary = null, CancellationToken ct = default)
    {
        var outcome = new TeamRunOutcome { State = TeamRunState.Preparing };
        var token = Guid.NewGuid().ToString("N")[..8];
        var branch = $"coral-run/{token}";
        var path = Path.Combine(RunRoot(), $"run-{token}");
        outcome.Branch = branch;
        outcome.WorktreePath = path;

        // 1) Isolated worktree off HEAD.
        var add = await CodeGit.AddWorktreeAsync(gitLauncher, repo, path, branch, resolveGitBinary, ct);
        if (!add.Ok)
        {
            outcome.State = TeamRunState.Failed;
            outcome.Error = $"Couldn't create an isolated worktree: {Truncate(add.Output, 160)}";
            return outcome;
        }

        // 2) Materialize the team's .claude files, then commit them as a
        // baseline so the compared diff is the team's code work, not setup.
        try
        {
            new StrategyWriter(path) { Binary = binary }.Write(strategy);
        }
        catch (Exception ex)
        {
            outcome.State = TeamRunState.Failed;
            outcome.Error = $"Couldn't write the team's files: {ex.Message}";
            return outcome;
        }
        await CodeGit.CommitAllAsync(gitLauncher, path, $"coral: team setup ({strategy.Name})", resolveGitBinary, ct);

        // 3) Run, write-capable, inside the worktree.
        outcome.State = TeamRunState.Running;
        if (CrossProviderEditor.IsCrossProvider(strategy))
        {
            var res = await CrossProviderEditor.RunAsync(task, path, strategy, runner, gitLauncher, _ => { },
                resolveGitBinary: resolveGitBinary, ct: ct);
            outcome.Tokens = res.Tokens;
            outcome.CostUsd = res.CostUsd;
            outcome.Estimated = res.Estimated;
            outcome.Authorship = res.PerFile;
            if (res.Error is not null)
            {
                outcome.State = TeamRunState.Failed;
                outcome.Error = res.Error;
                return outcome;
            }
        }
        else
        {
            var orchestrator = strategy.Orchestrator;
            var provider = orchestrator?.Provider ?? AIProvider.Claude;
            var model = provider == AIProvider.Claude
                ? orchestrator?.Model.ToRawValue() ?? ""
                : orchestrator?.ProviderModelId ?? "";
            try
            {
                var r = await runner.RunAsync(task, provider, model, path, ct);
                outcome.Tokens = r.Tokens;
                outcome.CostUsd = r.CostUsd;
                outcome.Estimated = r.Estimated;
            }
            catch (OneShotException ex)
            {
                outcome.State = TeamRunState.Failed;
                outcome.Error = ex.Message;
                return outcome;
            }
        }

        // 4) Capture the diff (incl. new untracked files) BEFORE committing.
        outcome.Diff = await CodeGit.FullDiffAsync(gitLauncher, path, resolveGitBinary, ct) ?? "";
        // 5) Commit the work so a winner can be applied later.
        await CodeGit.CommitAllAsync(gitLauncher, path, $"coral: {strategy.Name} — {Truncate(task, 60)}", resolveGitBinary, ct);

        outcome.State = TeamRunState.Done;
        return outcome;
    }

    /// <summary>Merge the run's branch back into the user's working tree,
    /// then tear down its worktree (and, on a successful merge, its branch).</summary>
    public static async Task<(bool Ok, string Output)> ApplyAsync(IProcessLauncher gitLauncher, string repo,
        TeamRunOutcome outcome, Func<string, string?>? resolveGitBinary = null, CancellationToken ct = default)
    {
        var merge = await CodeGit.MergeNoFFAsync(gitLauncher, repo, outcome.Branch,
            $"Coral: {outcome.Branch}", resolveGitBinary, ct);
        await CodeGit.RemoveWorktreeAsync(gitLauncher, repo, outcome.WorktreePath, resolveGitBinary, ct);
        if (merge.Ok) await CodeGit.DeleteBranchAsync(gitLauncher, repo, outcome.Branch, resolveGitBinary, ct);
        return merge;
    }

    /// <summary>Discard the run entirely — remove its worktree and delete
    /// its branch. Leaves the user's working tree exactly as it was.</summary>
    public static async Task DiscardAsync(IProcessLauncher gitLauncher, string repo, TeamRunOutcome outcome,
        Func<string, string?>? resolveGitBinary = null, CancellationToken ct = default)
    {
        await CodeGit.RemoveWorktreeAsync(gitLauncher, repo, outcome.WorktreePath, resolveGitBinary, ct);
        await CodeGit.DeleteBranchAsync(gitLauncher, repo, outcome.Branch, resolveGitBinary, ct);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
