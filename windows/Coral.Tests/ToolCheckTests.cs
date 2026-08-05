using Coral.Core.Models;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of the ToolCheckEngineTests portion of StrategyForgeTests/ToolCheckTests.swift.
/// (`ToolChecksRunTests`, which drives a real command runner, is skipped — that's
/// process-spawning and belongs to Fase 3; see windows/PORT-PLAN.md.)</summary>
public class ToolCheckEngineTests
{
    [Fact]
    public void PassesWhenExitZeroAndOutputContains()
    {
        var c = new ToolCheck(name: "weather", command: "…", expectContains: "temp", expectExitZero: true);
        var r = ToolCheckEngine.Evaluate("{\"temp\": 21}", 0, c);
        Assert.True(r.Passed);
    }

    [Fact]
    public void FailsOnNonZeroExit()
    {
        var c = new ToolCheck(command: "…", expectContains: "", expectExitZero: true);
        var r = ToolCheckEngine.Evaluate("boom", 1, c);
        Assert.False(r.Passed);
        Assert.Contains("1", r.Reason);
    }

    [Fact]
    public void FailsWhenOutputMissingNeedle()
    {
        var c = new ToolCheck(command: "…", expectContains: "temperature", expectExitZero: false);
        var r = ToolCheckEngine.Evaluate("cloudy", 0, c);
        Assert.False(r.Passed);
        Assert.Contains("temperature", r.Reason);
    }

    [Fact]
    public void ExitZeroCheckIgnoresOutputWhenNoNeedle()
    {
        var c = new ToolCheck(command: "…", expectContains: "", expectExitZero: true);
        Assert.True(ToolCheckEngine.Evaluate("anything", 0, c).Passed);
    }

    [Fact]
    public void NoAssertionVacuouslyPassesButIsFlagged()
    {
        var c = new ToolCheck(command: "…", expectContains: "", expectExitZero: false);
        Assert.False(c.HasAssertion);
        var r = ToolCheckEngine.Evaluate("", 0, c);
        Assert.True(r.Passed);
        Assert.Contains("no assertion", r.Reason);
    }
}
