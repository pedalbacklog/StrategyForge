namespace Coral.Core.Models;

/// <summary>
/// Port of <c>StrategyForge/Models/ToolCheck.swift</c>. Tool unit tests: "test
/// each tool on its own, with fixtures, no model in the loop — most agent bugs
/// are tool bugs wearing a costume." A ToolCheck runs a deterministic command
/// and asserts on its output/exit code — no LLM, so it's fast, cheap, and
/// repeatable. Travels with the team.
/// </summary>
public sealed class ToolCheck
{
    public Guid Id { get; set; }
    /// <summary>A human name, e.g. "weather MCP returns a temperature".</summary>
    public string Name { get; set; }
    /// <summary>The tool invocation to run in a shell — a curl, a skill script, an MCP probe, etc.</summary>
    public string Command { get; set; }
    /// <summary>stdout+stderr must CONTAIN this (empty = don't assert on output).</summary>
    public string ExpectContains { get; set; }
    /// <summary>The command must exit 0.</summary>
    public bool ExpectExitZero { get; set; }

    public ToolCheck(string name = "", string command = "", string expectContains = "",
        bool expectExitZero = true, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Name = name;
        Command = command;
        ExpectContains = expectContains;
        ExpectExitZero = expectExitZero;
    }

    /// <summary>A check with no assertion at all can only ever vacuously pass —
    /// the UI uses this to warn, and the runner treats it as "just ran".</summary>
    public bool HasAssertion => ExpectExitZero || ExpectContains.Trim().Length > 0;
}

/// <summary>The verdict for one tool check after running.</summary>
public sealed record ToolCheckResult(Guid CheckId, bool Passed, string Reason);

/// <summary>Pure evaluation of a captured command result against a check — no
/// process, no model, so it's directly unit-tested. The actual command
/// execution (<c>Services/ToolCheckRunner.swift</c>'s <c>ToolChecks.run</c>) is
/// process-spawning and belongs to Fase 3, not here.</summary>
public static class ToolCheckEngine
{
    public static ToolCheckResult Evaluate(string output, int exitCode, ToolCheck check)
    {
        if (check.ExpectExitZero && exitCode != 0)
        {
            return new ToolCheckResult(check.Id, false, $"exited {exitCode}");
        }
        var needle = check.ExpectContains.Trim();
        if (needle.Length > 0 && !output.Contains(needle))
        {
            return new ToolCheckResult(check.Id, false, $"output didn't contain “{needle}”");
        }
        return new ToolCheckResult(check.Id, true, check.HasAssertion ? "passed" : "ran (no assertion)");
    }
}
