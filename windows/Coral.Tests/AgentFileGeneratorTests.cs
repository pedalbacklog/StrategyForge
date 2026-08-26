using Coral.Core.Generators;
using Coral.Core.Models;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of the AgentFileGeneratorTests portion of StrategyForgeTests/GeneratorTests.swift.</summary>
public class AgentFileGeneratorTests
{
    [Fact]
    public void SkipsOrchestratorAndExpandsCount()
    {
        var strategy = StrategyLibrary.OrchestratorWorkers(); // 1 orchestrator + worker(count 3)
        var files = AgentFileGenerator.Generate(strategy);

        Assert.All(files, f => Assert.DoesNotContain("orchestrator", f.RelativePath));
        Assert.Equal(3, files.Count);
        var paths = files.Select(f => f.RelativePath).ToHashSet();
        Assert.Contains(".claude/agents/worker-1.md", paths);
        Assert.Contains(".claude/agents/worker-2.md", paths);
        Assert.Contains(".claude/agents/worker-3.md", paths);
    }

    [Fact]
    public void SingleInstanceHasNoSuffix()
    {
        var strategy = StrategyLibrary.ExecutorAdvisor(); // advisor count 1
        var files = AgentFileGenerator.Generate(strategy);
        Assert.Single(files);
        Assert.Equal(".claude/agents/advisor.md", files[0].RelativePath);
    }

    [Fact]
    public void FrontmatterIsValidAndPinsModel()
    {
        var strategy = StrategyLibrary.OrchestratorWorkers();
        var file = AgentFileGenerator.Generate(strategy).First();
        var contents = file.Contents;

        Assert.StartsWith("---\n", contents);
        var fenceCount = contents.Split("---").Length - 1;
        Assert.Equal(2, fenceCount);

        Assert.Contains("name: worker-1", contents);
        Assert.Contains("description:", contents);
        Assert.Contains("model: claude-sonnet-5", contents);

        // Workers inherit all tools → no `tools:` line.
        Assert.DoesNotContain("tools:", contents);

        Assert.Contains("You are a worker", contents);
    }

    [Fact]
    public void ToolsAreEmittedWhenPresent()
    {
        var strategy = StrategyLibrary.ResearchFanout(); // researchers are read-only
        var file = AgentFileGenerator.Generate(strategy).First();
        Assert.Contains("tools: Read, Grep, Glob", file.Contents);
    }

    [Fact]
    public void DescriptionWithColonIsQuoted()
    {
        var role = new AgentRole(name: "x", role: RoleKind.Worker, model: ClaudeModel.Sonnet5,
            systemPrompt: "body", description: "Use this: after building", tools: new List<string>());
        var md = AgentFileGenerator.Markdown(role, "x");
        Assert.Contains("description: \"Use this: after building\"", md);
    }

    [Fact]
    public void MemoryOffEmitsNoMemorySectionOrSeed()
    {
        var role = new AgentRole(name: "w", role: RoleKind.Worker, model: ClaudeModel.Sonnet5,
            systemPrompt: "body", description: "d");
        var md = AgentFileGenerator.Markdown(role, "w");
        Assert.DoesNotContain("## Memory", md);

        var strategy = new Strategy(name: "T", description: "", roles: new List<AgentRole> { role },
            orchestrationNotes: "");
        Assert.Empty(AgentFileGenerator.MemorySeedFiles(strategy));
    }

    [Fact]
    public void MemoryOnAddsSectionPointingAtItsFile()
    {
        var role = new AgentRole(name: "w", role: RoleKind.Worker, model: ClaudeModel.Sonnet5,
            systemPrompt: "body", description: "d", memoryEnabled: true);
        var md = AgentFileGenerator.Markdown(role, "w");
        Assert.Contains("## Memory", md);
        Assert.Contains(".claude/memory/w.md", md);
        // The self-improving loop: a rejection reason is written back so it doesn't recur.
        Assert.Contains("REJECTS your work, write the reason", md);
    }

    [Fact]
    public void MemorySeedIsOnePerExpandedInstance()
    {
        var role = new AgentRole(name: "w", role: RoleKind.Worker, model: ClaudeModel.Sonnet5,
            systemPrompt: "body", description: "d", count: 3, memoryEnabled: true);
        var strategy = new Strategy(name: "T", description: "", roles: new List<AgentRole> { role },
            orchestrationNotes: "");
        var seeds = AgentFileGenerator.MemorySeedFiles(strategy);
        Assert.Equal(
            new[] { ".claude/memory/w-1.md", ".claude/memory/w-2.md", ".claude/memory/w-3.md" },
            seeds.Select(s => s.RelativePath).OrderBy(s => s, StringComparer.Ordinal));
    }
}
