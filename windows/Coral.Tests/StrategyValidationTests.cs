using Coral.Core.Models;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of the StrategyValidationTests portion of StrategyForgeTests/GeneratorTests.swift.</summary>
public class StrategyValidationTests
{
    [Fact]
    public void TemplatesAreAllValid()
    {
        foreach (var strategy in StrategyLibrary.All)
        {
            Assert.True(strategy.IsValid, $"Template {strategy.Name} should be valid");
        }
    }

    [Fact]
    public void DetectsMissingOrchestrator()
    {
        var strategy = StrategyLibrary.OrchestratorWorkers();
        strategy.Roles = strategy.Roles.Where(r => !r.IsOrchestrator).ToList();
        Assert.False(strategy.IsValid);
    }

    [Fact]
    public void DetectsDuplicateNames()
    {
        var strategy = StrategyLibrary.OrchestratorWorkers();
        strategy.Roles[1].Name = strategy.Roles[0].Name;
        Assert.False(strategy.IsValid);
    }

    [Fact]
    public void DetectsExpandedNameCollision()
    {
        // A "worker" with count 3 writes worker-1…worker-3; a literal "worker-1" writes
        // the same file — different names, so the plain duplicate check misses it.
        var strategy = StrategyLibrary.OrchestratorWorkers();
        var orch = strategy.Roles.First(r => r.IsOrchestrator);
        strategy.Roles = new List<AgentRole>
        {
            orch,
            new AgentRole(name: "worker", role: RoleKind.Worker, model: ClaudeModel.Haiku45,
                systemPrompt: "", description: "", count: 3),
            new AgentRole(name: "worker-1", role: RoleKind.Worker, model: ClaudeModel.Haiku45,
                systemPrompt: "", description: "", count: 1),
        };
        Assert.False(strategy.IsValid);
        Assert.Contains(strategy.Validate(), i => i.Message.Contains("worker-1.md"));
    }
}
