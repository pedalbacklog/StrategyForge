using System.Linq;
using Coral.Core.Services;
using Coral.Core.ViewModels;
using Xunit;

namespace Coral.Tests;

/// <summary>Tests for WastedWork's pure detector against hand-built
/// ActivityStep timelines.</summary>
public class WastedWorkTests
{
    private static ActivityStep Step(string title, string? detail, bool isDelegation = false, string? agent = null) =>
        new(title, detail, DateTimeOffset.UtcNow, isDelegation, agent);

    [Fact]
    public void EmptyTimelineProducesNoDuplicates()
    {
        Assert.Empty(WastedWork.Detect(Array.Empty<ActivityStep>()));
    }

    [Fact]
    public void ASingleOccurrenceIsNotDuplicated()
    {
        var dups = WastedWork.Detect(new[] { Step("Read", "file.txt") });

        Assert.Empty(dups);
    }

    [Fact]
    public void TheSameToolAndTargetTwiceIsDetectedAsDuplicated()
    {
        var dups = WastedWork.Detect(new[] { Step("Read", "file.txt"), Step("Read", "file.txt") });

        var only = Assert.Single(dups);
        Assert.Equal("Read", only.Title);
        Assert.Equal("file.txt", only.Detail);
        Assert.Equal(2, only.Count);
    }

    [Fact]
    public void SameToolDifferentTargetsAreNotDuplicates()
    {
        var dups = WastedWork.Detect(new[] { Step("Read", "a.txt"), Step("Read", "b.txt") });

        Assert.Empty(dups);
    }

    [Fact]
    public void StepsWithoutAConcreteTargetAreNeverCountedAsWork()
    {
        var dups = WastedWork.Detect(new[]
        {
            Step("TodoWrite", null), Step("TodoWrite", null), Step("TodoWrite", "  "), Step("TodoWrite", "  "),
        });

        Assert.Empty(dups);
    }

    [Fact]
    public void DelegationAndRoleHeartbeatStepsAreExcluded()
    {
        var dups = WastedWork.Detect(new[]
        {
            Step("Task", "worker", isDelegation: true), Step("Task", "worker", isDelegation: true),
            Step("role.start", "orchestrator"), Step("role.start", "orchestrator"),
        });

        Assert.Empty(dups);
    }

    [Fact]
    public void DefaultsAMissingAgentToOrchestrator()
    {
        var dups = WastedWork.Detect(new[] { Step("Bash", "npm test"), Step("Bash", "npm test", agent: null) });

        var only = Assert.Single(dups);
        Assert.Equal(new[] { "orchestrator" }, only.Agents);
        Assert.False(only.CrossAgent);
    }

    [Fact]
    public void CrossAgentDuplicationTracksEachDistinctAgentOnce()
    {
        var dups = WastedWork.Detect(new[]
        {
            Step("Bash", "npm test", agent: "worker-a"),
            Step("Bash", "npm test", agent: "worker-b"),
            Step("Bash", "npm test", agent: "worker-a"), // same agent again — no new entry
        });

        var only = Assert.Single(dups);
        Assert.Equal(3, only.Count);
        Assert.Equal(new[] { "worker-a", "worker-b" }, only.Agents);
        Assert.True(only.CrossAgent);
    }

    [Fact]
    public void SortsByCountDescendingThenCrossAgentThenTitle()
    {
        var dups = WastedWork.Detect(new[]
        {
            Step("Zebra", "x"), Step("Zebra", "x"),                                      // count 2, single-agent
            Step("Alpha", "y"), Step("Alpha", "y"), Step("Alpha", "y"),                   // count 3
            Step("Beta", "z", agent: "a"), Step("Beta", "z", agent: "b"),                 // count 2, cross-agent
        });

        Assert.Equal(new[] { "Alpha", "Beta", "Zebra" }, dups.Select(d => d.Title));
    }

    [Fact]
    public void RedundantCountSumsOccurrencesBeyondTheFirstOfEachDuplicate()
    {
        var dups = WastedWork.Detect(new[]
        {
            Step("Read", "a.txt"), Step("Read", "a.txt"), Step("Read", "a.txt"), // 3 -> 2 redundant
            Step("Bash", "npm test"), Step("Bash", "npm test"),                  // 2 -> 1 redundant
        });

        Assert.Equal(3, WastedWork.RedundantCount(dups));
    }

    [Fact]
    public void RedundantCountIsZeroForNoDuplicates()
    {
        Assert.Equal(0, WastedWork.RedundantCount(Array.Empty<DuplicatedWork>()));
    }
}
