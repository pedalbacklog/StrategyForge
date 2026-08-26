using Coral.Core.Generators;
using Coral.Core.Models;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of the ClaudeMdGeneratorTests portion of StrategyForgeTests/GeneratorTests.swift.</summary>
public class ClaudeMdGeneratorTests
{
    [Fact]
    public void CreatesFromScratchWithMarkers()
    {
        var strategy = StrategyLibrary.OrchestratorWorkers();
        var output = ClaudeMdGenerator.Merged(null, strategy);
        Assert.Contains(ClaudeMdGenerator.StartMarker, output);
        Assert.Contains(ClaudeMdGenerator.EndMarker, output);
        Assert.Contains("Orchestrator + Workers", output);
        // Documents launch model, not frontmatter.
        Assert.Contains("claude --model claude-fable-5", output);
        Assert.Contains("`worker-1`", output);
    }

    [Fact]
    public void IncludesDelegationPlaybookForTeams()
    {
        var strategy = StrategyLibrary.OrchestratorWorkers();
        var output = ClaudeMdGenerator.Merged(null, strategy);
        Assert.Contains("Delegate like a manager, not a micromanager", output);
        Assert.Contains("Delegate early, not late", output);
        Assert.Contains("Review the diff; don't rewrite it", output);
    }

    [Fact]
    public void SoloHasNoDelegationPlaybook()
    {
        var output = ClaudeMdGenerator.Merged(null, StrategyLibrary.Solo());
        Assert.DoesNotContain("Delegate like a manager", output);
    }

    [Fact]
    public void PreservesUserContentWhenAppending()
    {
        var strategy = StrategyLibrary.Solo();
        const string existing = "# My project\n\nSome important notes.\n";
        var output = ClaudeMdGenerator.Merged(existing, strategy);
        Assert.Contains("# My project", output);
        Assert.Contains("Some important notes.", output);
        Assert.Contains(ClaudeMdGenerator.StartMarker, output);
    }

    [Fact]
    public void IsIdempotent()
    {
        var strategy = StrategyLibrary.PlannerImplementersReviewer();
        const string existing = "# Keep me\n";
        var once = ClaudeMdGenerator.Merged(existing, strategy);
        var twice = ClaudeMdGenerator.Merged(once, strategy);
        Assert.Equal(once, twice);
        var markerCount = twice.Split(ClaudeMdGenerator.StartMarker).Length - 1;
        Assert.Equal(1, markerCount);
    }

    [Fact]
    public void DisorderedMarkersDoNotCrash()
    {
        // End marker before start (corrupted/injected) must NOT throw — it should
        // ignore the markers and append a fresh managed block.
        var corrupted = $"{ClaudeMdGenerator.EndMarker}\nstray\n{ClaudeMdGenerator.StartMarker}\n";
        var output = ClaudeMdGenerator.Merged(corrupted, StrategyLibrary.Solo());
        Assert.Contains("Solo (baseline)", output);
        Assert.True(output.StartsWith(ClaudeMdGenerator.EndMarker) || output.Contains("stray"));
    }

    [Fact]
    public void ReplacesStaleSectionOnStrategyChange()
    {
        var first = ClaudeMdGenerator.Merged("# Repo\n", StrategyLibrary.Solo());
        Assert.Contains("Solo (baseline)", first);
        var second = ClaudeMdGenerator.Merged(first, StrategyLibrary.ResearchFanout());
        Assert.Contains("Research Fan-out", second);
        Assert.DoesNotContain("Solo (baseline)", second);
        Assert.Contains("# Repo", second); // user content preserved
    }
}

public class ClaudeMdMarkerTests
{
    [Fact]
    public void NeutralizesInjectedEndMarker()
    {
        // A name/description carrying the end marker must not survive into the
        // section body (it would prematurely close the managed block on the next merge).
        var strategy = StrategyLibrary.Solo();
        strategy.Description = "Sneaky <!-- CORAL:END --> injection";
        var section = ClaudeMdGenerator.Section(strategy);
        Assert.DoesNotContain(ClaudeMdGenerator.EndMarker, section);
    }
}
