using Coral.Core.Models;
using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of the pure-function tests in
/// StrategyForgeTests/MetaOrchestratorTests.swift: ProgressLineTests,
/// ProviderUsageEstimateTests, and the command-building case from
/// MetaOrchestratorPureTests. (The rest of that file — MetaOrchestrator's
/// plan/delegate/synthesize orchestration, Arena/Loop mocks — isn't ported;
/// MetaOrchestrator.swift itself isn't in Coral.Core yet.)</summary>
public class CLIOneShotRunnerTests
{
    [Fact]
    public void ReadOnlyClaudeCommandForbidsEditTools()
    {
        // The loop verifier runs read-only: it must be able to read + run tests,
        // but the file-editing tools are forbidden so it can't modify the code
        // it's judging.
        var normal = CLIOneShotRunner.Command(AIProvider.Claude, "judge", "claude-haiku-4-5", "acceptEdits").Args;
        Assert.DoesNotContain("--disallowedTools", normal);

        var ro = CLIOneShotRunner.Command(AIProvider.Claude, "judge", "claude-haiku-4-5", "acceptEdits", readOnly: true).Args;
        var i = ro.IndexOf("--disallowedTools");
        Assert.True(i >= 0, "read-only claude command should disallow tools");
        var forbidden = ro[i + 1];
        Assert.Contains("Edit", forbidden);
        Assert.Contains("Write", forbidden);
        Assert.Contains("MultiEdit", forbidden);
        Assert.Contains("NotebookEdit", forbidden);
    }

    [Fact]
    public void CodexOmitsModelWithoutApiKeyEvenIfConfigured()
    {
        var cmd = CLIOneShotRunner.Command(AIProvider.Openai, "task", "gpt-5", "default", hasApiKey: false);
        Assert.DoesNotContain("--model", cmd.Args);
        var withKey = CLIOneShotRunner.Command(AIProvider.Openai, "task", "gpt-5", "default", hasApiKey: true);
        Assert.Contains("--model", withKey.Args);
    }

    [Fact]
    public void GeminiCommandUsesPlainTextMode()
    {
        var cmd = CLIOneShotRunner.Command(AIProvider.Gemini, "task", "gemini-2.5-pro", "default");
        Assert.Equal(OutputMode.PlainText, cmd.Mode);
        Assert.Contains("-m", cmd.Args);
        Assert.Contains("gemini-2.5-pro", cmd.Args);
    }
}

public class ProviderUsageEstimateTests
{
    [Fact]
    public void EstimatesTokensFromTextLength()
    {
        // ~4 chars/token across prompt + output.
        var n = CLIOneShotRunner.EstimateTokens(new string('a', 400), new string('b', 400));
        Assert.Equal(200, n);
        // Never zero, even for empty text.
        Assert.Equal(1, CLIOneShotRunner.EstimateTokens("", ""));
    }

    [Fact]
    public void EstimatedCostUsesRealPriceWhenKnown()
    {
        // A Claude model has a real price -> blended (input+output)/2 per M.
        var cost = CLIOneShotRunner.EstimatedCostUsd(1_000_000, "claude-sonnet-5");
        Assert.Equal((3.0 + 15.0) / 2, cost);
    }

    [Fact]
    public void EstimatedCostFallsBackForUnknownModel()
    {
        var cost = CLIOneShotRunner.EstimatedCostUsd(1_000_000, "some-codex-model");
        Assert.Equal(5.0, cost);
    }
}

/// <summary>The "summarize the important bit" cleaner for providers with no
/// structured tool stream.</summary>
public class ProgressLineTests
{
    [Fact]
    public void StripsAnsiAndKeepsTheText()
    {
        const string raw = "[32m✔[0m  Reading the repository structure";
        Assert.Equal("✔ Reading the repository structure", CLIOneShotRunner.ProgressLine(raw));
    }

    [Fact]
    public void DropsEmptySpinnerAndSymbolOnlyLines()
    {
        Assert.Null(CLIOneShotRunner.ProgressLine(""));
        Assert.Null(CLIOneShotRunner.ProgressLine("   "));
        Assert.Null(CLIOneShotRunner.ProgressLine("⠋⠙⠹"));            // spinner frames
        Assert.Null(CLIOneShotRunner.ProgressLine("────────────"));    // box-drawing rule
        Assert.Null(CLIOneShotRunner.ProgressLine("esc to interrupt")); // banner noise
    }

    [Fact]
    public void CollapsesWhitespaceAndTruncatesLongLines()
    {
        var long_ = string.Concat(Enumerable.Repeat("analysing files ", 20));
        var outp = CLIOneShotRunner.ProgressLine(long_);
        Assert.NotNull(outp);
        Assert.True((outp?.Length ?? 0) <= 141);
        Assert.True(outp?.EndsWith("…") == true);
    }

    [Fact]
    public void DetectsInteractiveAuthPrompts()
    {
        // These block the run waiting for input no one can give -> must fail fast.
        Assert.True(CLIOneShotRunner.IsAuthPrompt("Opening authentication page in your browser."));
        Assert.True(CLIOneShotRunner.IsAuthPrompt("Do you want to continue? [Y/n]"));
        Assert.True(CLIOneShotRunner.IsAuthPrompt("Please set an auth method"));
        // Ordinary model output must NOT trip it.
        Assert.False(CLIOneShotRunner.IsAuthPrompt("Here are five steps to research the repo."));
        Assert.False(CLIOneShotRunner.IsAuthPrompt("Reading package.json"));
    }

    [Fact]
    public void StripsAnsiFromTerminalOutput()
    {
        Assert.Equal("green text", CLIOneShotRunner.StripAnsi("[32mgreen[0m text"));
        Assert.Equal("plain", CLIOneShotRunner.StripAnsi("plain"));
    }
}
