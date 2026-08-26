using Coral.Core.Models;
using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of StrategyForgeTests/CrossProviderEditorTests.swift.
/// Unlike the Swift original (a real temp git repo + real `git` process),
/// this uses fakes for both the provider runner AND git — matching the rest
/// of this port's testing philosophy (CodeGitTests, ProviderInstallerTests,
/// ProviderOneShotRunnerTests) and avoiding a second, differently-flavored
/// test style for one file. CodeGit's own parsing is already covered by
/// CodeGitTests, so the git fake here only needs to be internally
/// consistent, not exhaustively realistic.</summary>
public class CrossProviderEditorTests
{
    /// <summary>Each call APPENDS a provider-tagged line to an in-memory
    /// "shared.txt" — so after both providers run, the file has one line per
    /// provider and per-line authorship is checkable. Mirrors the Swift
    /// original's SequentialEditRunner, minus real disk I/O.</summary>
    private sealed class SequentialEditRunner : IOneShotRunner
    {
        public readonly Dictionary<string, string> Files = new();

        public Task<OneShotResult> RunAsync(string prompt, AIProvider provider, string model, string? cwd,
            CancellationToken ct = default)
        {
            var contents = Files.TryGetValue("shared.txt", out var c) ? c : "";
            if (contents.Length > 0 && !contents.EndsWith('\n')) contents += "\n";
            contents += $"line by {provider.ToKey()}\n";
            Files["shared.txt"] = contents;
            return Task.FromResult(new OneShotResult("done", 10, 0.01, provider, model));
        }
    }

    private static AgentRole Role(string name, AIProvider provider) =>
        new(name, RoleKind.Worker, ClaudeModel.Sonnet5, "", "", provider: provider);

    /// <summary>A two-role team: a Gemini worker then an OpenAI worker,
    /// editing the same file.</summary>
    private static Strategy MixedTeam() => new("Test", "", new List<AgentRole>
    {
        new("lead", RoleKind.Orchestrator, ClaudeModel.Opus5, "", "", isOrchestrator: true),
        Role("scout", AIProvider.Gemini),
        Role("coder", AIProvider.Openai),
    }, "");

    [Fact]
    public void DetectsCrossProviderTeams()
    {
        Assert.True(CrossProviderEditor.IsCrossProvider(MixedTeam()));
        Assert.False(CrossProviderEditor.IsCrossProvider(StrategyLibrary.Solo())); // all Claude
    }

    [Fact]
    public void EditorsAreTheSubagentsOrTheOrchestratorAloneForASoloTeam()
    {
        var editors = CrossProviderEditor.Editors(MixedTeam());
        Assert.Equal(new[] { "scout", "coder" }, editors.Select(r => r.Name));

        var solo = CrossProviderEditor.Editors(StrategyLibrary.Solo());
        Assert.Single(solo);
        Assert.True(solo[0].IsOrchestrator);
    }

    [Fact]
    public async Task ReportsAnErrorWhenTheTeamHasNoAgents()
    {
        var empty = new Strategy("Empty", "", new List<AgentRole>(), "");
        var runner = new SequentialEditRunner();
        var gitLauncher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));

        var result = await CrossProviderEditor.RunAsync("task", "/repo", empty, runner, gitLauncher, _ => { });

        Assert.Equal("This team has no agents to run.", result.Error);
    }

    [Fact]
    public async Task SequentialEditsAttributeEachLineToItsProvider()
    {
        var runner = new SequentialEditRunner();
        var gitLauncher = new FakeProcessLauncher((_, args) =>
        {
            if (args.Contains("--numstat")) return new FakeChildProcess(new List<string>());
            if (args.Contains("--porcelain")) return new FakeChildProcess(new List<string> { " M shared.txt\0" });
            if (args.Contains("--others")) return new FakeChildProcess(new List<string>());
            if (args.Contains("HEAD")) // FullDiffAsync's tracked-changes pass
            {
                return new FakeChildProcess(new List<string>
                {
                    "diff --git a/shared.txt b/shared.txt\n@@ -0,0 +1,2 @@\n+line by gemini\n+line by openai",
                });
            }
            return new FakeChildProcess(new List<string>());
        });

        var workerProviders = new List<AIProvider>();
        var result = await CrossProviderEditor.RunAsync(
            "append your line", "/repo", MixedTeam(), runner, gitLauncher,
            author => workerProviders.Add(author.Provider),
            readFile: p => runner.Files.TryGetValue(Path.GetFileName(p), out var c) ? c : null,
            resolveGitBinary: n => n == "git" ? "/usr/bin/git" : null);

        // Both providers ran, in order.
        Assert.Equal(new[] { AIProvider.Gemini, AIProvider.Openai }, workerProviders);
        Assert.Null(result.Error);
        Assert.Contains("shared.txt", result.Diff);

        // shared.txt was touched by BOTH providers.
        var file = Assert.Single(result.PerFile, f => f.File == "shared.txt");
        Assert.Equal(new[] { AIProvider.Gemini, AIProvider.Openai }, file.Authors.Select(a => a.Provider));

        // Per-line authorship: the gemini line is credited to gemini, the openai line to openai.
        var authors = result.LineAuthors["shared.txt"];
        Assert.True(authors.Count >= 2);
        Assert.Equal(AIProvider.Gemini, authors[0]!.Provider);
        Assert.Equal(AIProvider.Openai, authors[1]!.Provider);

        Assert.Equal(20, result.Tokens); // 10 + 10
        Assert.Equal(0.02, result.CostUsd, precision: 10);
    }

    [Fact]
    public async Task OneWorkerFailingDoesNotAbortTheSequence()
    {
        var calls = 0;
        var runner = new FakeSometimesFailingRunner(() => { calls++; return calls == 1; });
        var gitLauncher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));

        var result = await CrossProviderEditor.RunAsync("task", "/repo", MixedTeam(), runner, gitLauncher, _ => { });

        Assert.Null(result.Error);
        Assert.Equal(2, calls); // both workers were attempted despite the first failing
    }

    private sealed class FakeSometimesFailingRunner : IOneShotRunner
    {
        private readonly Func<bool> _shouldFail;
        public FakeSometimesFailingRunner(Func<bool> shouldFail) => _shouldFail = shouldFail;

        public Task<OneShotResult> RunAsync(string prompt, AIProvider provider, string model, string? cwd,
            CancellationToken ct = default)
        {
            if (_shouldFail()) throw new OneShotException(OneShotErrorKind.Failed, "boom");
            return Task.FromResult(new OneShotResult("done", 1, 0, provider, model));
        }
    }
}
