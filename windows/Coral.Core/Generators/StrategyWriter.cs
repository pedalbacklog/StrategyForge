using Coral.Core.Models;

namespace Coral.Core.Generators;

/// <summary>
/// Port of <c>StrategyForge/Generators/StrategyWriter.swift</c>. Disk I/O layer
/// around the pure generators. Handles previewing (merging the managed CLAUDE.md
/// section against whatever is currently on disk), detecting overwrites, and
/// writing safely.
///
/// Safety rules:
///  - Deletes ONLY agent/workflow files it previously generated (managed
///    signature) that are no longer part of the strategy; hand-written files are
///    never touched.
///  - CLAUDE.md is merged, never clobbered: only the marked section changes.
/// </summary>
public sealed class StrategyWriter
{
    public string RepoPath { get; }
    public string Binary { get; set; } = "claude";
    /// <summary>Pre-rendered "Learnings from past runs" markdown (MemoryDigest, not
    /// ported yet), injected into the managed CLAUDE.md block. Empty (default) keeps
    /// output identical to before it existed.</summary>
    public string Memory { get; set; } = "";

    public StrategyWriter(string repoPath)
    {
        RepoPath = repoPath;
    }

    private string FullPath(string relativePath) =>
        Path.Combine(RepoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private string ClaudeMdPath => FullPath(ClaudeMdGenerator.FileName);

    private static string? ReadIfExists(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    // MARK: - Preview

    /// <summary>All files that would be written, with exact contents, WITHOUT
    /// touching disk (beyond reading whatever already exists). The CLAUDE.md
    /// preview reflects a merge against the file currently on disk.</summary>
    public List<GeneratedFile> PreviewFiles(Strategy strategy)
    {
        var files = AgentFileGenerator.Generate(strategy);
        var merged = ClaudeMdGenerator.Merged(ReadIfExists(ClaudeMdPath), strategy, Binary, Memory);
        files.Add(new GeneratedFile(ClaudeMdGenerator.FileName, merged));
        var mcp = McpConfigGenerator.Json(strategy.McpServers);
        if (mcp != null)
        {
            files.Add(new GeneratedFile(McpConfigGenerator.FileName, mcp));
        }
        // Per-agent memory seeds are created ONCE and then owned by the agent, so
        // only show (and later write) the ones that don't exist yet.
        foreach (var seed in AgentFileGenerator.MemorySeedFiles(strategy))
        {
            if (!File.Exists(FullPath(seed.RelativePath)))
            {
                files.Add(seed);
            }
        }
        var workflow = WorkflowGenerator.Workflow(strategy);
        if (workflow != null)
        {
            files.Add(new GeneratedFile(WorkflowGenerator.FileName(strategy), workflow));
        }
        return files;
    }

    /// <summary>A pre-write diff for every file that would be written: created vs.
    /// modified vs. unchanged, with the line-by-line changes, WITHOUT touching disk
    /// (reads existing contents only).</summary>
    public List<FileDiff> PreviewDiffs(Strategy strategy)
    {
        var diffs = PreviewFiles(strategy)
            .Select(file => FileDiff.Make(file, ReadIfExists(FullPath(file.RelativePath))))
            .ToList();

        // Deletions: write() prunes managed agent/workflow files that dropped out of
        // the strategy. Surface them so the diff the user approves matches exactly
        // what write() does.
        foreach (var rel in PrunedAgentPaths(strategy))
        {
            diffs.Add(FileDiff.Deleted(rel, ReadIfExists(FullPath(rel))));
        }
        foreach (var rel in PrunedWorkflowPaths(strategy))
        {
            diffs.Add(FileDiff.Deleted(rel, ReadIfExists(FullPath(rel))));
        }
        return diffs;
    }

    /// <summary>Managed agent files we previously generated that are no longer part
    /// of the strategy. <see cref="Write"/> deletes exactly these; the preview uses
    /// the same list so the two never diverge. Only files bearing our managed
    /// signature qualify — hand-written agent files are never included.</summary>
    public List<string> PrunedAgentPaths(Strategy strategy)
    {
        var agentsDir = FullPath(AgentFileGenerator.AgentsDirectory);
        var newPaths = new HashSet<string>(AgentFileGenerator.Generate(strategy).Select(f => f.RelativePath));
        if (!Directory.Exists(agentsDir)) return new List<string>();

        var pruned = new List<string>();
        foreach (var entry in Directory.GetFiles(agentsDir, "*.md"))
        {
            var fileName = Path.GetFileName(entry);
            // The loop verifier carries the same managed signature but belongs to
            // the Loops feature — writing a team must not dismantle a configured
            // loop, so it is never pruned here.
            if (fileName == "loop-verifier.md") continue;
            var rel = $"{AgentFileGenerator.AgentsDirectory}/{fileName}";
            if (newPaths.Contains(rel)) continue;
            string content;
            try { content = File.ReadAllText(entry); }
            catch { continue; }
            if (!content.Contains(AgentFileGenerator.ManagedSignature) &&
                !content.Contains(AgentFileGenerator.LegacyManagedSignature)) continue;
            pruned.Add(rel);
        }
        return pruned;
    }

    /// <summary>Managed workflow files we previously generated that no longer match
    /// the team: the slug changed (rename) or the team no longer yields a workflow
    /// at all. <see cref="Write"/> deletes exactly these; the preview uses the same
    /// list so the two never diverge. Only files bearing the generated-workflow
    /// signature qualify — hand-written workflows are never touched.</summary>
    public List<string> PrunedWorkflowPaths(Strategy strategy)
    {
        var workflowsDir = FullPath(WorkflowGenerator.WorkflowsDirectory);
        var expected = WorkflowGenerator.Workflow(strategy) is null
            ? new HashSet<string>()
            : new HashSet<string> { WorkflowGenerator.FileName(strategy) };
        if (!Directory.Exists(workflowsDir)) return new List<string>();

        var pruned = new List<string>();
        foreach (var entry in Directory.GetFiles(workflowsDir, "*.mjs"))
        {
            var fileName = Path.GetFileName(entry);
            var rel = $"{WorkflowGenerator.WorkflowsDirectory}/{fileName}";
            if (expected.Contains(rel)) continue;
            string content;
            try { content = File.ReadAllText(entry); }
            catch { continue; }
            if (!content.Contains(WorkflowGenerator.ManagedSignature)) continue;
            pruned.Add(rel);
        }
        return pruned;
    }

    /// <summary>Relative paths of files that already exist on disk and would be
    /// overwritten. (CLAUDE.md is always merged rather than clobbered, so it is not
    /// a conflict.)</summary>
    public List<string> ExistingAgentConflicts(Strategy strategy) =>
        AgentFileGenerator.Generate(strategy)
            .Where(f => File.Exists(FullPath(f.RelativePath)))
            .Select(f => f.RelativePath)
            .ToList();

    // MARK: - Write

    /// <summary>Write the agent files and merge the CLAUDE.md section. Returns the
    /// relative paths written, in order. Throws on any filesystem error.</summary>
    public List<string> Write(Strategy strategy)
    {
        var written = new List<string>();

        // 1. Subagent files.
        var agentsDir = FullPath(AgentFileGenerator.AgentsDirectory);
        Directory.CreateDirectory(agentsDir);
        var generated = AgentFileGenerator.Generate(strategy);

        // Prune stale agents WE previously generated (renamed/removed roles) so they
        // don't linger as active subagents. Uses the SAME list the preview diff
        // surfaces, so what the user approved is what happens.
        foreach (var rel in PrunedAgentPaths(strategy))
        {
            try { File.Delete(FullPath(rel)); } catch { /* best-effort, mirrors Swift's try? */ }
        }

        foreach (var file in generated)
        {
            File.WriteAllText(FullPath(file.RelativePath), file.Contents);
            written.Add(file.RelativePath);
        }

        // 2. CLAUDE.md — merge into whatever exists.
        var merged = ClaudeMdGenerator.Merged(ReadIfExists(ClaudeMdPath), strategy, Binary, Memory);
        File.WriteAllText(ClaudeMdPath, merged);
        written.Add(ClaudeMdGenerator.FileName);

        // 2a. Per-agent memory seeds — create ONLY if absent so the agent's
        // accumulated notes are never clobbered by a re-generate.
        foreach (var seed in AgentFileGenerator.MemorySeedFiles(strategy))
        {
            var path = FullPath(seed.RelativePath);
            if (File.Exists(path)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? RepoPath);
            File.WriteAllText(path, seed.Contents);
            written.Add(seed.RelativePath);
        }

        // 2a-bis. Dynamic workflow — the team's topology as a runnable Claude Code
        // program. Regenerated like the agent files, so it's overwritten. Prune
        // stale generated workflows first (renamed team / solo downgrade).
        foreach (var rel in PrunedWorkflowPaths(strategy))
        {
            try { File.Delete(FullPath(rel)); } catch { /* best-effort */ }
        }
        var workflow = WorkflowGenerator.Workflow(strategy);
        if (workflow != null)
        {
            var path = FullPath(WorkflowGenerator.FileName(strategy));
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? RepoPath);
            File.WriteAllText(path, workflow);
            written.Add(WorkflowGenerator.FileName(strategy));
        }

        // 2b. Skills — copy each attached skill folder into the repo's
        // .claude/skills so Claude Code discovers it. A slug already present in the
        // repo is left as is; otherwise it's copied from the personal store
        // (~/.claude/skills).
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var slug in strategy.Skills)
        {
            var dest = FullPath($".claude/skills/{slug}");
            if (Directory.Exists(dest)) continue;
            var source = Path.Combine(home, ".claude", "skills", slug);
            if (!Directory.Exists(source)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? RepoPath);
            CopyDirectory(source, dest);
            written.Add($".claude/skills/{slug}");
        }

        // 3. .mcp.json — external tool servers Claude Code auto-loads. MERGE into
        // any existing file so a hand-authored server list is preserved (ours win
        // only on a name collision); an unparseable existing file is left untouched.
        var mcpPath = FullPath(McpConfigGenerator.FileName);
        var mcpJson = McpConfigGenerator.MergedJson(ReadIfExists(mcpPath), strategy.McpServers);
        if (mcpJson != null)
        {
            File.WriteAllText(mcpPath, mcpJson);
            written.Add(McpConfigGenerator.FileName);
        }

        // 3b. Per-provider MCP config for the OTHER CLIs on the team — only emit a
        // provider's config when that provider is actually on the team.
        var providers = new HashSet<AIProvider>(strategy.Roles.Select(r => r.Provider));
        if (providers.Contains(AIProvider.Gemini))
        {
            var gemPath = FullPath(McpConfigGenerator.GeminiFileName);
            var gem = McpConfigGenerator.GeminiSettingsJson(ReadIfExists(gemPath), strategy.McpServers);
            if (gem != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(gemPath) ?? RepoPath);
                File.WriteAllText(gemPath, gem);
                written.Add(McpConfigGenerator.GeminiFileName);
            }
        }
        if (providers.Contains(AIProvider.Openai))
        {
            var codexPath = FullPath(McpConfigGenerator.CodexFileName);
            // No TOML merge — only write when absent so a hand-authored config.toml is safe.
            if (!File.Exists(codexPath))
            {
                var toml = McpConfigGenerator.CodexToml(strategy.McpServers);
                if (toml != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(codexPath) ?? RepoPath);
                    File.WriteAllText(codexPath, toml);
                    written.Add(McpConfigGenerator.CodexFileName);
                }
            }
        }

        return written;
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)));
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }
}
