using Coral.Core.Generators;
using Coral.Core.Models;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of StrategyForgeTests/GeneratorTests.swift's StrategyWriterTests.
/// Round-trips real writes to a temp directory — the first Coral.Core port that
/// touches disk.</summary>
public class StrategyWriterTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "coral-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void WritesAgentsAndClaudeMd()
    {
        var tmp = TempDir();
        try
        {
            var writer = new StrategyWriter(tmp);
            var strategy = StrategyLibrary.OrchestratorWorkers();
            var written = writer.Write(strategy);

            Assert.Contains(".claude/agents/worker-1.md", written);
            Assert.Contains("CLAUDE.md", written);

            var workerPath = Path.Combine(tmp, ".claude", "agents", "worker-1.md");
            var worker = File.ReadAllText(workerPath);
            Assert.Contains("model: claude-sonnet-5", worker);

            // Second write preserves surrounding CLAUDE.md content and stays idempotent.
            var claudePath = Path.Combine(tmp, "CLAUDE.md");
            var body = "# User header\n\n" + File.ReadAllText(claudePath);
            File.WriteAllText(claudePath, body);
            writer.Write(strategy);
            var after = File.ReadAllText(claudePath);
            Assert.Contains("# User header", after);
            var markerCount = after.Split(ClaudeMdGenerator.StartMarker).Length - 1;
            Assert.Equal(1, markerCount);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void RenamePrunesStaleGeneratedWorkflow()
    {
        var tmp = TempDir();
        try
        {
            var writer = new StrategyWriter(tmp);
            var strategy = StrategyLibrary.OrchestratorWorkers();
            strategy.Name = "Old Name";
            writer.Write(strategy);
            var oldPath = Path.Combine(tmp, ".claude", "workflows", "old-name.mjs");
            Assert.True(File.Exists(oldPath));

            // A hand-written workflow (no managed signature) must survive pruning.
            var handPath = Path.Combine(tmp, ".claude", "workflows", "mine.mjs");
            File.WriteAllText(handPath, "export const meta = { name: 'mine' }\n");

            strategy.Name = "New Name";
            Assert.Equal(new List<string> { ".claude/workflows/old-name.mjs" }, writer.PrunedWorkflowPaths(strategy));
            writer.Write(strategy);
            Assert.False(File.Exists(oldPath));
            Assert.True(File.Exists(Path.Combine(tmp, ".claude", "workflows", "new-name.mjs")));
            Assert.True(File.Exists(handPath));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void SoloDowngradePrunesSameSlugWorkflow()
    {
        var tmp = TempDir();
        try
        {
            var writer = new StrategyWriter(tmp);
            var team = StrategyLibrary.OrchestratorWorkers();
            team.Name = "Same Name";
            writer.Write(team);
            var wfPath = Path.Combine(tmp, ".claude", "workflows", "same-name.mjs");
            Assert.True(File.Exists(wfPath));

            var solo = StrategyLibrary.Solo();
            solo.Name = "Same Name";
            Assert.Equal(new List<string> { ".claude/workflows/same-name.mjs" }, writer.PrunedWorkflowPaths(solo));
            writer.Write(solo);
            Assert.False(File.Exists(wfPath));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
