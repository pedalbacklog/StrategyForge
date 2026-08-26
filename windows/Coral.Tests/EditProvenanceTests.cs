using Coral.Core.Models;
using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of StrategyForgeTests/EditProvenanceTests.swift: resolving
/// an active-subagent name to the responsible team member (its pinned
/// model/provider), the orchestrator fallback, and the compact badge
/// label.</summary>
public class EditProvenanceTests
{
    private static Strategy MakeStrategy()
    {
        var orch = new AgentRole("lead", RoleKind.Orchestrator, ClaudeModel.Opus48, "", "", isOrchestrator: true);
        var reviewer = new AgentRole("reviewer", RoleKind.Worker, ClaudeModel.Sonnet5, "", "", provider: AIProvider.Gemini);
        var coder = new AgentRole("coder", RoleKind.Worker, ClaudeModel.Haiku45, "", "");
        return new Strategy("T", "", new List<AgentRole> { orch, reviewer, coder }, "");
    }

    [Fact]
    public void NoSubagentCreditsTheOrchestrator()
    {
        var p = EditProvenanceResolver.Attribute(null, MakeStrategy());
        Assert.Null(p.Agent);
        Assert.Equal(AIProvider.Claude, p.Provider);
        Assert.Equal($"lead · {ClaudeModel.Opus48.DisplayName()}", p.Label("lead"));
    }

    [Fact]
    public void MatchedSubagentCreditsItsPinnedModelAndProvider()
    {
        var p = EditProvenanceResolver.Attribute("reviewer", MakeStrategy());
        Assert.Equal("reviewer", p.Agent);
        Assert.Equal(AIProvider.Gemini, p.Provider);
        Assert.StartsWith("reviewer · ", p.Label("lead"));
    }

    [Fact]
    public void SubagentMatchIsLooseOnTitleForm()
    {
        var p = EditProvenanceResolver.Attribute("Reviewer", MakeStrategy());
        Assert.Equal("reviewer", p.Agent);
        Assert.Equal(AIProvider.Gemini, p.Provider);
    }

    [Fact]
    public void UnknownSubagentIsCreditedByNameWithUnknownModel()
    {
        var p = EditProvenanceResolver.Attribute("ghost", MakeStrategy());
        Assert.Equal("ghost", p.Agent);
        Assert.Equal("", p.Model);
        Assert.Equal("ghost", p.Label("lead"));
    }

    [Fact]
    public void BlankSubagentFallsBackToOrchestrator()
    {
        var p = EditProvenanceResolver.Attribute("   ", MakeStrategy());
        Assert.Null(p.Agent);
        Assert.Contains("lead", p.Label("lead"));
    }
}

/// <summary>Port of StrategyForgeTests/EditProvenanceTests.swift's
/// LineAttributorTests.</summary>
public class LineAttributorTests
{
    private static readonly EditProvenance Alice = new("alice", "Sonnet 5", AIProvider.Claude);
    private static readonly EditProvenance Bob = new("bob", "Gemini", AIProvider.Gemini);

    [Fact]
    public void FirstEditCreditsEveryLineToTheAuthor()
    {
        var outp = LineAttributor.Attribute(Array.Empty<string>(), Array.Empty<EditProvenance?>(),
            new[] { "a", "b", "c" }, Alice);
        Assert.Equal(new EditProvenance?[] { Alice, Alice, Alice }, outp);
    }

    [Fact]
    public void EmptyNewYieldsEmpty()
    {
        Assert.Empty(LineAttributor.Attribute(new[] { "x" }, new EditProvenance?[] { Alice }, Array.Empty<string>(), Bob));
    }

    [Fact]
    public void InsertedLinesGetTheNewAuthorUnchangedKeepTheirs()
    {
        var outp = LineAttributor.Attribute(
            new[] { "a", "b", "c" }, new EditProvenance?[] { Alice, Alice, Alice },
            new[] { "a", "b", "b2", "c" }, Bob);
        Assert.Equal(new EditProvenance?[] { Alice, Alice, Bob, Alice }, outp);
    }

    [Fact]
    public void ReplacedLineIsCreditedToTheEditorOnlyOnThatLine()
    {
        var outp = LineAttributor.Attribute(
            new[] { "a", "b", "c" }, new EditProvenance?[] { Alice, Alice, Alice },
            new[] { "a", "B!", "c" }, Bob);
        Assert.Equal(new EditProvenance?[] { Alice, Bob, Alice }, outp);
    }

    [Fact]
    public void DeletedLinesLeaveRemainingAuthorshipIntact()
    {
        var outp = LineAttributor.Attribute(
            new[] { "a", "b", "c" }, new EditProvenance?[] { Alice, Alice, Alice },
            new[] { "a", "c" }, Bob);
        Assert.Equal(new EditProvenance?[] { Alice, Alice }, outp);
    }

    [Fact]
    public void AppendedLinesAtEndGetTheEditor()
    {
        var outp = LineAttributor.Attribute(new[] { "a" }, new EditProvenance?[] { Alice },
            new[] { "a", "b", "c" }, Bob);
        Assert.Equal(new EditProvenance?[] { Alice, Bob, Bob }, outp);
    }

    [Fact]
    public void OversizeDiffFallsBackToAllAuthorWithoutAligning()
    {
        var big = Enumerable.Repeat("x", 1500).ToArray();
        var outp = LineAttributor.Attribute(big, Enumerable.Repeat<EditProvenance?>(Alice, 1500).ToArray(), big, Bob);
        Assert.Equal(1500, outp.Count);
        Assert.All(outp, a => Assert.Equal(Bob, a));
    }
}
