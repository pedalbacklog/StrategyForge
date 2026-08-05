using Coral.Core.Models;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of the EvalScoringTests/EvalRubricAndRegressionTests portions of
/// StrategyForgeTests/EvalRunnerTests.swift that only touch Models/EvalSuite.swift
/// (pure data — no judge/parsing, which lives in the not-yet-ported Services/EvalRunner.swift).</summary>
public class EvalScoringTests
{
    private static List<EvalResult> Results(IEnumerable<bool> passes) =>
        passes.Select(p => new EvalResult(Guid.NewGuid(), p, "")).ToList();

    [Fact]
    public void PassRateAndGate()
    {
        var run = new EvalRun(Results(new[] { true, true, true, false }), 0.7);
        Assert.Equal(3, run.Passed);
        Assert.Equal(4, run.Total);
        Assert.True(Math.Abs(run.PassRate - 0.75) < 0.0001);
        Assert.True(run.MeetsThreshold); // 0.75 >= 0.70

        var below = new EvalRun(Results(new[] { true, false, false, false }), 0.7);
        Assert.False(below.MeetsThreshold); // 0.25 < 0.70

        var empty = new EvalRun(new List<EvalResult>(), 0.0);
        Assert.False(empty.MeetsThreshold); // an empty run never passes
    }
}

public class EvalRubricAndRegressionTests
{
    [Fact]
    public void HumanVerdictOverridesTheGate()
    {
        var r = new EvalResult(Guid.NewGuid(), false, "judge said no");
        Assert.False(r.EffectivePassed);
        r.HumanVerdict = true; // a human disagrees with the judge
        Assert.True(r.EffectivePassed);
        var run = new EvalRun(new List<EvalResult> { r }, 0.5);
        Assert.Equal(1, run.Passed); // the override moves the gate
    }

    [Fact]
    public void RegressionDeltaMatchesByScenario()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var baseline = new Dictionary<string, bool>
        {
            [a.ToString()] = true,
            [b.ToString()] = true,
            [c.ToString()] = false,
        };
        var run = new EvalRun(new List<EvalResult>
        {
            new(a, false, ""), // regressed
            new(b, true, ""),  // still passing
            new(c, true, ""),  // fixed
        }, 0.5);
        var delta = EvalRegression.Between(baseline, run);
        Assert.True(delta.HasBaseline);
        Assert.Equal(new[] { a }, delta.Regressed);
        Assert.Equal(new[] { c }, delta.Fixed);
    }

    [Fact]
    public void NoBaselineMeansNoRegression()
    {
        var run = new EvalRun(new List<EvalResult> { new(Guid.NewGuid(), false, "") }, 0.5);
        var delta = EvalRegression.Between(new Dictionary<string, bool>(), run);
        Assert.False(delta.HasBaseline);
        Assert.Empty(delta.Regressed);
    }
}
