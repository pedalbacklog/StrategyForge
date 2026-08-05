using Coral.Core.Generators;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of the LaunchCommandGeneratorTests portion of StrategyForgeTests/GeneratorTests.swift.</summary>
public class LaunchCommandGeneratorTests
{
    [Fact]
    public void UsesOrchestratorModel()
    {
        var strategy = TestStrategies.DebateConsensus(); // moderator is Opus 5 (current expert)
        Assert.Equal("claude --model claude-opus-5", LaunchCommandGenerator.Command(strategy));
        Assert.Equal("/model claude-opus-5", LaunchCommandGenerator.InSessionInstruction(strategy));
    }

    [Fact]
    public void RespectsCustomBinaryPath()
    {
        var strategy = TestStrategies.Solo();
        var cmd = LaunchCommandGenerator.Command(strategy, "/usr/local/bin/claude");
        Assert.StartsWith("/usr/local/bin/claude --model ", cmd);
    }

    [Fact]
    public void GitCommitCommandStagesConfigFiles()
    {
        var cmd = LaunchCommandGenerator.GitCommitCommand();
        Assert.Contains("git add .claude CLAUDE.md", cmd);
        Assert.Contains("git commit -m", cmd);
    }
}
