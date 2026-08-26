using Coral.Core.Models;

namespace Coral.Core.Generators;

/// <summary>
/// Port of <c>StrategyForge/Generators/LaunchCommandGenerator.swift</c>. Produces
/// the exact command to start Claude Code with the orchestrator's model, since
/// that model is a launch setting rather than agent frontmatter.
/// </summary>
public static class LaunchCommandGenerator
{
    /// <summary>The shell command to launch Claude Code with the orchestrator model.</summary>
    public static string Command(Strategy strategy, string binary = "claude")
    {
        var model = (strategy.Orchestrator?.Model ?? ClaudeModel.Fable5).ToRawValue();
        // Quote an absolute binary path that contains spaces so the copyable
        // command still runs (e.g. "/Users/me/My Tools/claude").
        var bin = binary.Contains(' ') ? $"'{binary.Replace("'", "'\\''")}'" : binary;
        return $"{bin} --model {model}";
    }

    /// <summary>Shell command that stages, commits AND pushes the generated config,
    /// so the team-shared configuration lands on the remote. Guards against a
    /// non-git folder so the user gets a clear message instead of a raw error.</summary>
    public static string GitCommitCommand() =>
        "if git rev-parse --is-inside-work-tree >/dev/null 2>&1; then "
        + "git add .claude CLAUDE.md && git commit -m \"Add Claude Code multi-agent config (Coral)\" && git push; "
        + "else echo '⚠️  This folder is not a git repository — nothing to push. Run: git init, add a remote, then commit & push.'; fi";

    /// <summary>The equivalent instruction for switching model from inside a running session.</summary>
    public static string InSessionInstruction(Strategy strategy)
    {
        var model = (strategy.Orchestrator?.Model ?? ClaudeModel.Fable5).ToRawValue();
        return $"/model {model}";
    }

    /// <summary>Both forms as a short human-readable block for the CLAUDE.md / preview.</summary>
    public static string Instructions(Strategy strategy, string binary = "claude")
    {
        var modelName = strategy.Orchestrator?.Model.DisplayName() ?? "the orchestrator model";
        return $"Launch Claude Code in this repo with the orchestrator model ({modelName}):\n" +
               "\n" +
               $"    {Command(strategy, binary)}\n" +
               "\n" +
               "Or, from inside an already-running session, switch with:\n" +
               "\n" +
               $"    {InSessionInstruction(strategy)}";
    }
}
