using Coral.Core.Generators;
using Coral.Core.Models;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of the Headline/Markdown portion of
/// StrategyForgeTests/GeneratorTests.swift's MissionReportTests.
/// <c>agentLines</c> isn't ported (see MissionReport.cs) — these tests build
/// <see cref="MissionReport.AgentLine"/> values directly, exactly what an empty
/// timeline would have produced (steps: 0, span: "").</summary>
public class MissionReportTests
{
    [Fact]
    public void HeadlineReadsLikeAShareableBrag()
    {
        Assert.Equal("A 4-agent team finished for $0.83.", MissionReport.Headline(4, 0.83, 1000));
        Assert.Contains("single agent", MissionReport.Headline(1, 0, 12_000));
    }

    [Fact]
    public void MarkdownIncludesTeamAgentsAndOutcome()
    {
        var strategy = StrategyLibrary.OrchestratorWorkers();
        var lines = strategy.Roles
            .Select(r => new MissionReport.AgentLine(r.Name, r.Role.DisplayName(), r.ModelDisplayName, 0, ""))
            .ToList();
        var md = MissionReport.Markdown("Improve the HUD", "Orchestrator + Workers", lines,
            24_500, 0.83, "2m 10s", "Reworked the HUD tokens.");
        Assert.Contains("# Mission report — Improve the HUD", md);
        Assert.Contains("finished for $0.83", md);
        Assert.Contains("| Agent | Role | Model | Steps | Time |", md);
        Assert.Contains("## Outcome", md);
        Assert.Contains("Reworked the HUD tokens.", md);
        Assert.Equal(strategy.Roles.Count, lines.Count); // one line per role
    }
}
