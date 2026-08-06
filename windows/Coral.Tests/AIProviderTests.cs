using Coral.Core.Models;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of the Fase 6 additions to AIProvider.swift's extension
/// (binaryName, npmInstallSpec, loginCommand, ...) — no direct Swift test
/// file existed to mirror, so these are written from the documented
/// behavior in AIProvider.swift.</summary>
public class AIProviderTests
{
    [Theory]
    [InlineData(AIProvider.Claude, "claude")]
    [InlineData(AIProvider.Openai, "codex")]
    [InlineData(AIProvider.Gemini, "gemini")]
    public void BinaryNameMatchesTheCliEachProviderSpawns(AIProvider provider, string expected) =>
        Assert.Equal(expected, provider.BinaryName());

    [Fact]
    public void OnlyGeminiHasAnAlternativeBinary() =>
        Assert.Equal(new[] { "agy" }, AIProvider.Gemini.AlternativeBinaries());

    [Theory]
    [InlineData(AIProvider.Claude, "@anthropic-ai/claude-code")]
    [InlineData(AIProvider.Openai, "@openai/codex")]
    [InlineData(AIProvider.Gemini, "@google/gemini-cli")]
    public void NpmInstallSpecFallsBackToTheBarePackageWhenUnpinned(AIProvider provider, string expected) =>
        Assert.Equal(expected, provider.NpmInstallSpec());

    [Theory]
    [InlineData(AIProvider.Claude, "claude auth login --claudeai")]
    [InlineData(AIProvider.Openai, "codex login")]
    [InlineData(AIProvider.Gemini, "gemini")]
    public void LoginCommandMatchesEachProvidersSignInFlow(AIProvider provider, string expected) =>
        Assert.Equal(expected, provider.LoginCommand());

    [Theory]
    [InlineData(AIProvider.Claude)]
    [InlineData(AIProvider.Openai)]
    [InlineData(AIProvider.Gemini)]
    public void NoProviderNeedsAVisibleTerminalAnymore(AIProvider provider) =>
        Assert.False(provider.LoginNeedsTerminal());
}
