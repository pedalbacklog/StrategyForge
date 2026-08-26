using Coral.Core.Models;

namespace Coral.Core.Services;

/// <summary>A file's authorship after the sequence: which team members
/// touched it, in first-touch order.</summary>
public sealed record FileProvenance(string File, IReadOnlyList<EditProvenance> Authors);

public sealed class CrossProviderResult
{
    public string Diff { get; set; } = "";
    public List<FileProvenance> PerFile { get; set; } = new();
    public int Tokens { get; set; }
    public double CostUsd { get; set; }
    public bool Estimated { get; set; }
    /// <summary>Per-line authorship, keyed by repo-relative file -> one
    /// author per line of the final file (null where unknown).</summary>
    public Dictionary<string, IReadOnlyList<EditProvenance?>> LineAuthors { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>
/// Port of <c>StrategyForge/Services/CrossProviderEditor.swift</c>.
/// Cross-provider file editing with per-line provenance: a team whose roles
/// run on DIFFERENT providers (a Gemini planner, a Codex implementer, a
/// Claude reviewer...) edits the SAME worktree in SEQUENCE — one worker at a
/// time, so there are no concurrent-write conflicts — and after each worker
/// every changed line is re-attributed to whoever just wrote it (via
/// <see cref="LineAttributor"/>). The result is a diff where each line is
/// credited to the provider/model that authored it — the step Claude Code's
/// native subagent delegation can't do (it only ever runs Claude).
///
/// Meant to run inside an already-isolated git worktree (see
/// <c>TeamRunEngine</c>), so it never touches the user's own tree.
/// </summary>
public static class CrossProviderEditor
{
    /// <summary>Whether a strategy needs this path: any role runs on a
    /// non-Claude provider.</summary>
    public static bool IsCrossProvider(Strategy strategy) => strategy.Roles.Any(r => r.Provider != AIProvider.Claude);

    /// <summary>The team members who do the editing, in run order: the
    /// subagents, or the orchestrator alone for a solo config.</summary>
    public static List<AgentRole> Editors(Strategy strategy)
    {
        var subs = strategy.SubagentRoles;
        if (subs.Count > 0) return subs;
        return strategy.Orchestrator is { } orch ? new List<AgentRole> { orch } : new List<AgentRole>();
    }

    /// <summary>Run each editor in sequence inside <paramref name="worktree"/>,
    /// attributing lines as it goes. <paramref name="onWorker"/> fires before
    /// each worker starts, so a caller can show who's editing now.
    /// <paramref name="readFile"/> is injectable so this is unit-testable
    /// without touching real disk; defaults to a real UTF-8 file read.</summary>
    public static async Task<CrossProviderResult> RunAsync(
        string task, string worktree, Strategy strategy, IOneShotRunner runner, IProcessLauncher gitLauncher,
        Action<EditProvenance> onWorker, Func<string, string?>? readFile = null,
        Func<string, string?>? resolveGitBinary = null, CancellationToken ct = default)
    {
        readFile ??= p => File.Exists(p) ? File.ReadAllText(p) : null;
        var result = new CrossProviderResult();
        // file -> (current lines, per-line authors), carried across workers.
        var files = new Dictionary<string, (List<string> Lines, List<EditProvenance?> Authors)>();
        var order = new List<string>(); // first-touch order of files
        var touched = new Dictionary<string, List<EditProvenance>>();

        var team = Editors(strategy);
        if (team.Count == 0)
        {
            result.Error = "This team has no agents to run.";
            return result;
        }

        for (var i = 0; i < team.Count; i++)
        {
            var role = team[i];
            var author = new EditProvenance(role.IsOrchestrator ? null : role.Name, role.ModelDisplayName, role.Provider);
            onWorker(author);

            var prompt = WorkerPrompt(task, role, isSolo: team.Count == 1);
            try
            {
                var r = await runner.RunAsync(prompt, role.Provider, ModelIdFor(role), worktree, ct);
                result.Tokens += r.Tokens;
                result.CostUsd += r.CostUsd;
                if (r.Estimated) result.Estimated = true;
            }
            catch (OneShotException)
            {
                // One worker failing doesn't abort the sequence — the next may still work.
                continue;
            }

            // Re-attribute every changed file after this worker's edits.
            foreach (var changed in await CodeGit.ChangedFilesAsync(gitLauncher, worktree, resolveGitBinary, ct: ct))
            {
                var path = changed.Path;
                var abs = Path.Combine(worktree, path);
                var text = readFile(abs);
                if (text is null) continue;
                var newLines = text.Split('\n');
                var (prevLines, prevAuthors) = files.TryGetValue(path, out var prev)
                    ? prev
                    : (new List<string>(), new List<EditProvenance?>());
                var authors = LineAttributor.Attribute(prevLines, prevAuthors, newLines, author);
                files[path] = (newLines.ToList(), authors.ToList());
                if (!touched.ContainsKey(path)) { order.Add(path); touched[path] = new List<EditProvenance>(); }
                touched[path].Add(author);
            }
        }

        result.Diff = await CodeGit.FullDiffAsync(gitLauncher, worktree, resolveGitBinary, ct) ?? "";
        result.LineAuthors = files.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<EditProvenance?>)kv.Value.Authors);
        result.PerFile = order.Select(file => new FileProvenance(file, Distinct(touched[file]))).ToList();
        return result;
    }

    // MARK: - Helpers

    /// <summary>Appended to a one-shot task so the model acts instead of just
    /// describing/delegating — without it, a single unsupervised call can
    /// legitimately choose to explain a plan (or delegate to a subagent whose
    /// edit doesn't land) and return with nothing changed, which defeats the
    /// point of an isolated, reviewable "run for real". Shared with
    /// <see cref="TeamRunEngine"/>'s native-Claude path so both take the same
    /// nudge.</summary>
    internal const string DirectEditSuffix = "\n\nEdit the files in this repository directly to complete the task.";

    private static string WorkerPrompt(string task, AgentRole role, bool isSolo)
    {
        if (isSolo) return $"{task}{DirectEditSuffix}";
        return $"""
            You are the "{role.Name}" agent on a team working this task:

            {task}

            Do YOUR part now by editing the files in this repository directly. Build on any changes already present from teammates; don't undo their work. Keep your edits focused on your role.
            """;
    }

    /// <summary>The model id a role runs with. Port of
    /// <c>MetaOrchestrator.swift</c>'s <c>modelID(for:)</c> — the only piece
    /// of that (much larger, non-worktree meta-orchestration) file this
    /// needs, so it's inlined here rather than porting the rest of it.</summary>
    private static string ModelIdFor(AgentRole role)
    {
        if (role.Provider == AIProvider.Claude) return role.Model.ToRawValue();
        if (role.ProviderModelId is not null) return role.ProviderModelId;
        var models = ModelCatalog.Models(role.Provider);
        return models.Count > 0 ? models[0].Id : "";
    }

    /// <summary>Distinct authors preserving first-seen order.</summary>
    private static List<EditProvenance> Distinct(List<EditProvenance> authors)
    {
        var seen = new HashSet<EditProvenance>();
        var outp = new List<EditProvenance>();
        foreach (var a in authors)
        {
            if (seen.Add(a)) outp.Add(a);
        }
        return outp;
    }
}
