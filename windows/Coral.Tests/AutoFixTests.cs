using Coral.Core.Models;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of StrategyForgeTests/ImportExportTests.swift's AutoFixTests.</summary>
public class AutoFixTests
{
    [Fact]
    public void StripsWriteToolsFromReviewerAndAgentFromWorker()
    {
        var s = StrategyLibrary.OrchestratorWorkers();
        // Corrupt: give a worker the Agent tool, and add a reviewer with Write.
        s.Roles[1].Tools = new List<string> { "Write", "Agent" };
        s.Roles.Add(new AgentRole("reviewer", RoleKind.Reviewer, ClaudeModel.Sonnet5,
            "review", "reviews", tools: new List<string> { "Read", "Edit" }));
        Assert.True(s.HasAutoFixableIssues);

        var fixedStrategy = s.AutoFixed();
        var worker = fixedStrategy.Roles[1];
        Assert.DoesNotContain("Agent", worker.Tools); // single level of delegation
        var reviewer = fixedStrategy.Roles.First(r => r.Role == RoleKind.Reviewer);
        Assert.DoesNotContain("Edit", reviewer.Tools); // read-only enforced
        Assert.Contains("Read", reviewer.Tools);
        Assert.False(fixedStrategy.HasAutoFixableIssues); // idempotent
    }

    [Fact]
    public void DeduplicatesNames()
    {
        var s = StrategyLibrary.OrchestratorWorkers();
        s.Roles[1].Name = "dup";
        s.Roles.Add(new AgentRole("dup", RoleKind.Worker, ClaudeModel.Sonnet5, "x", "y"));
        var fixedStrategy = s.AutoFixed();
        var names = fixedStrategy.Roles.Select(r => r.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count()); // all unique now
    }

    [Fact]
    public void DeduplicationAvoidsCollidingWithAnotherRolesExpandedInstanceFiles()
    {
        // Real bug caught on real Windows (Strategy Editor, Fix All): a
        // literal-only dedup can rename a duplicate onto exactly the slot a
        // fan-out role's own Nth instance already claims — e.g. two roles
        // named "curri" where one has Count 2 renaming the second to
        // "curri-2" collides with the first's own "curri-2.md". Confirmed
        // present in the Swift original too (Strategy.swift's autoFixed()
        // is a plain literal dedup) — fixed only in this port, see
        // PORT-PLAN.md for why Swift wasn't touched.
        var worker = new AgentRole("curri", RoleKind.Worker, ClaudeModel.Sonnet5, "x", "y", count: 2);
        var reviewer = new AgentRole("curri", RoleKind.Reviewer, ClaudeModel.Haiku45, "x", "y");
        var s = new Strategy("Test", "Test", new List<AgentRole>
        {
            new("orchestrator", RoleKind.Orchestrator, ClaudeModel.Sonnet5, "x", "y", isOrchestrator: true),
            worker,
            reviewer,
        }, "notes");

        var fixedStrategy = s.AutoFixed();

        Assert.True(fixedStrategy.IsValid);
        Assert.Empty(fixedStrategy.Validate());
    }
}
