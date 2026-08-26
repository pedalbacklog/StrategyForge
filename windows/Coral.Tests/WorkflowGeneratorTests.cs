using Coral.Core.Generators;
using Coral.Core.Models;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of StrategyForgeTests/GeneratorTests.swift's WorkflowGeneratorTests.</summary>
public class WorkflowGeneratorTests
{
    [Fact]
    public void RoleParallelWorkflowHasPhasesAndParallel()
    {
        // Domain specialists (different roles) -> one parallel branch per specialist,
        // plus a Verify phase because the team has a reviewer.
        var strategy = StrategyLibrary.DomainSpecialists();
        var js = WorkflowGenerator.Workflow(strategy)!;
        Assert.Contains("export const meta = {", js);
        Assert.Contains("phase('Plan')", js);
        Assert.Contains("await parallel([", js);
        Assert.Contains("return final", js);
        var producers = strategy.SubagentRoles.Where(r => r.Role != RoleKind.Reviewer).ToList();
        var workBranches = js.Split("phase: 'Work'").Length - 1;
        Assert.Equal(producers.Count, workBranches);
        Assert.Contains("typeof args === 'string'", js);
    }

    [Fact]
    public void FanoutOfIdenticalWorkersBecomesItemFanout()
    {
        // "N identical workers each take a slice" is one-agent-per-item: scout the
        // work-list, fan out over items. No reviewer here -> parallel, 3 phases.
        var js = WorkflowGenerator.Workflow(StrategyLibrary.OrchestratorWorkers())!;
        Assert.Contains("phases: [{ title: 'Scout' }, { title: 'Work' }, { title: 'Synthesize' }]", js);
        Assert.Contains("phase('Scout')", js);
        Assert.Contains("JSON.parse", js);
        Assert.Contains("items.map((item) => () => agent(", js);
        Assert.Contains("isolation: 'worktree'", js);
        Assert.DoesNotContain("phase('Verify')", js);
    }

    [Fact]
    public void ItemFanoutWithReviewerVerifiesEachItemInAPipeline()
    {
        // N implementers + a reviewer -> item fan-out where each translated item is
        // verified as soon as it lands (pipeline), before synthesis.
        var js = WorkflowGenerator.Workflow(StrategyLibrary.PlannerImplementersReviewer())!;
        Assert.Contains("{ title: 'Scout' }", js);
        Assert.Contains("await pipeline(items,", js);
        Assert.Contains("label: 'verify:' + item", js);
        Assert.Contains("VERIFIED FINDINGS", js);
    }

    [Fact]
    public void SoloTeamHasNoWorkflow()
    {
        Assert.Null(WorkflowGenerator.Workflow(StrategyLibrary.Solo()));
    }

    [Fact]
    public void FileNameSlugIsFilesystemSafe()
    {
        var s = StrategyLibrary.Solo();
        s.Name = "My Team / v2!";
        Assert.Equal(".claude/workflows/my-team-v2.mjs", WorkflowGenerator.FileName(s));
    }

    [Fact]
    public void PersonaBackticksCannotBreakOutOfTheTemplate()
    {
        var s = StrategyLibrary.OrchestratorWorkers();
        s.Roles[1].SystemPrompt = "do `x` and ${danger}";
        var js = WorkflowGenerator.Workflow(s)!;
        // The backtick and ${ are escaped so they can't terminate/interpolate the literal.
        Assert.Contains("do \\`x\\` and \\${danger}", js);
    }

    [Fact]
    public void RoleParallelReviewerBecomesAnIndependentVerifyPhase()
    {
        // A role-parallel team (specialists) with a reviewer gates the edge: the
        // reviewer moves out of Work into a Verify phase on fresh context.
        var s = StrategyLibrary.DomainSpecialists();
        var js = WorkflowGenerator.Workflow(s)!;
        Assert.Contains("{ title: 'Verify' }", js);
        Assert.Contains("const verified = await parallel(results", js);
        Assert.Contains("VERIFIED FINDINGS", js);
        var reviewerName = s.SubagentRoles.First(r => r.Role == RoleKind.Reviewer).Name;
        Assert.DoesNotContain($"label: '{reviewerName}', phase: 'Work'", js);
    }

    [Fact]
    public void MigrationTemplateEmitsItemFanoutWithAdversarialVerify()
    {
        // Language Migration = cheap implementers fan out one-file-per-agent from a
        // rulebook, verified per item by a reviewer.
        var s = StrategyLibrary.LanguageMigration();
        Assert.NotNull(s.Orchestrator);
        Assert.Contains(s.SubagentRoles, r => r.Role == RoleKind.Worker && r.Count >= 2);
        Assert.True(s.SubagentRoles.Count(r => r.Role == RoleKind.Reviewer) >= 2);
        var js = WorkflowGenerator.Workflow(s)!;
        Assert.Contains("phase('Scout')", js);
        Assert.Contains("await pipeline(items,", js);
        Assert.Contains("label: 'verify:' + item", js);
    }

    [Fact]
    public void RoleParallelNoReviewerKeepsTheThreePhaseShape()
    {
        // Executor + advisor: one producer, no reviewer -> plain Plan/Work/Synthesize.
        var js = WorkflowGenerator.Workflow(StrategyLibrary.ExecutorAdvisor())!;
        Assert.DoesNotContain("phase('Verify')", js);
        Assert.DoesNotContain("phase('Scout')", js);
        Assert.Contains("phases: [{ title: 'Plan' }, { title: 'Work' }, { title: 'Synthesize' }]", js);
    }
}
