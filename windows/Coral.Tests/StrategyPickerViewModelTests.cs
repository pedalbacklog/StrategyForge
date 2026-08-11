using Coral.Core.Models;
using Coral.Core.ViewModels;
using Xunit;

namespace Coral.Tests;

/// <summary>Tests for StrategyPickerViewModel against a real temp directory —
/// same shape as StrategyWriterTests, since StrategyWriter.Write never spawns
/// a process, there's nothing to fake here.</summary>
public class StrategyPickerViewModelTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "coral-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void TemplatesExposesAllFifteenBuiltInStrategies()
    {
        var vm = new StrategyPickerViewModel("/repo");

        Assert.Equal(StrategyLibrary.All.Count, vm.Templates.Count);
        Assert.Contains(vm.Templates, s => s.Name == StrategyLibrary.Solo().Name);
    }

    [Fact]
    public async Task SelectAsyncWritesTheStrategyAndUpdatesSelectedStrategy()
    {
        var tmp = TempDir();
        try
        {
            var vm = new StrategyPickerViewModel(tmp);
            var strategy = StrategyLibrary.OrchestratorWorkers();

            var ok = await vm.SelectAsync(strategy);

            Assert.True(ok);
            Assert.Same(strategy, vm.SelectedStrategy);
            Assert.False(vm.IsBusy);
            Assert.Contains(strategy.Name, vm.StatusMessage);
            Assert.True(File.Exists(Path.Combine(tmp, ".claude", "agents", "worker-1.md")));
            Assert.True(File.Exists(Path.Combine(tmp, "CLAUDE.md")));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public async Task SelectAsyncReportsAnErrorRatherThanThrowingOnAnUnwritableRepoPath()
    {
        // A path that can't possibly exist as a writable directory (deep
        // under a file, not a folder) — Directory.CreateDirectory throws
        // IOException, which SelectAsync should turn into a StatusMessage
        // rather than an unhandled exception blowing up the flyout.
        var tmp = TempDir();
        try
        {
            var blockingFile = Path.Combine(tmp, "not-a-directory");
            File.WriteAllText(blockingFile, "x");
            var vm = new StrategyPickerViewModel(Path.Combine(blockingFile, "agents-live-here"));

            var ok = await vm.SelectAsync(StrategyLibrary.Solo());

            Assert.False(ok);
            Assert.Null(vm.SelectedStrategy);
            Assert.StartsWith("Error:", vm.StatusMessage);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }
}
