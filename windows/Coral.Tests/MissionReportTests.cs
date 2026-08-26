using Coral.Core.Generators;
using Coral.Core.Models;
using Coral.Core.ViewModels;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of StrategyForgeTests/GeneratorTests.swift's MissionReportTests,
/// including <c>agentLines</c> (see MissionReport.cs).</summary>
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

    [Fact]
    public void AgentLinesAttributesStepsToOrchestratorAndSubagentsByAgentField()
    {
        var strategy = StrategyLibrary.OrchestratorWorkers(); // roles: "orchestrator", "worker"
        var t0 = DateTimeOffset.UtcNow;
        var timeline = new List<ActivityStep>
        {
            new("Read", null, t0, Agent: null),                                  // orchestrator step
            new("→ worker", null, t0.AddSeconds(1), IsDelegation: true),          // delegation marker, excluded
            new("Edit", null, t0.AddSeconds(2), Agent: "worker"),                 // worker step
            new("Edit", null, t0.AddSeconds(3), Agent: "worker"),                 // worker step
        };

        var lines = MissionReport.AgentLines(strategy, timeline);

        var orchestrator = lines.Single(l => l.Name == "Orchestrator");
        Assert.Equal(1, orchestrator.Steps);

        var worker = lines.Single(l => l.Name == "Worker");
        Assert.Equal(2, worker.Steps);
        Assert.Equal("1s", worker.Span); // last (t0+3) - first (t0+2) worker step = 1s
    }

    [Fact]
    public void AgentLinesMatchesSubagentNamesLoosely()
    {
        // AgentNameMatcher.TitlesMatch does substring containment either way after
        // normalizing — a streamed agent name like "worker-1" should still count
        // against the "worker" role.
        var strategy = StrategyLibrary.OrchestratorWorkers();
        var timeline = new List<ActivityStep>
        {
            new("Edit", null, DateTimeOffset.UtcNow, Agent: "worker-1"),
        };

        var lines = MissionReport.AgentLines(strategy, timeline);

        var worker = lines.Single(l => l.Name == "Worker");
        Assert.Equal(1, worker.Steps);
    }

    [Fact]
    public void AgentLinesReturnsEmptySpanForNoStepsOrSubSecondSpans()
    {
        var strategy = StrategyLibrary.OrchestratorWorkers();
        var t0 = DateTimeOffset.UtcNow;
        var timeline = new List<ActivityStep>
        {
            new("Read", null, t0, Agent: null),
            new("Read", null, t0.AddMilliseconds(500), Agent: null), // < 1s span
        };

        var lines = MissionReport.AgentLines(strategy, timeline);

        var orchestrator = lines.Single(l => l.Name == "Orchestrator");
        Assert.Equal(2, orchestrator.Steps);
        Assert.Equal("", orchestrator.Span);

        var worker = lines.Single(l => l.Name == "Worker");
        Assert.Equal(0, worker.Steps);
        Assert.Equal("", worker.Span);
    }
}
