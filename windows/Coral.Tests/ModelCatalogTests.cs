using Coral.Core.Models;
using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

public class ModelCatalogTests
{
    [Theory]
    [InlineData(AIProvider.Claude)]
    [InlineData(AIProvider.Openai)]
    [InlineData(AIProvider.Gemini)]
    public void BuiltInIsNeverEmpty(AIProvider provider)
    {
        Assert.NotEmpty(ModelCatalog.BuiltIn(provider));
    }

    [Fact]
    public void ModelsFallsBackToBuiltInWhenNoOverrideIsLoaded()
    {
        Assert.Equal(ModelCatalog.BuiltIn(AIProvider.Gemini), ModelCatalog.Models(AIProvider.Gemini));
    }

    [Fact]
    public void ParsesTheRealRepoRootModelsJson()
    {
        // models.json is the cross-platform contract (windows/README.md "Relationship
        // to the macOS app") — this reads the SAME file StrategyForge/Services/
        // ModelCatalog.swift parses, copied into the test output by the .csproj.
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "models.json");
        var bytes = File.ReadAllBytes(path);

        var parsed = ModelCatalog.Parse(bytes);

        Assert.True(parsed.ContainsKey("openai"));
        Assert.True(parsed.ContainsKey("gemini"));
        Assert.False(parsed.ContainsKey("claude")); // claude has no override in models.json today

        Assert.Contains(parsed["openai"], m =>
            m.Id == "gpt-5.6-terra" && m.DisplayName == "GPT-5.6 Terra" && m.TierKey == "model.tier.expert");
        Assert.Contains(parsed["gemini"], m => m.Id == "gemini-3.1-pro" && m.TierKey == "model.tier.expert");
    }

    [Fact]
    public void ParseIgnoresUnknownProviderKeys()
    {
        var json = """{ "providers": { "totally-made-up": [ { "id": "x", "displayName": "X", "tierKey": "model.tier.fast" } ] } }"""u8.ToArray();

        Assert.Empty(ModelCatalog.Parse(json));
    }

    [Fact]
    public void ParseDropsEmptyProviderLists()
    {
        var json = """{ "providers": { "gemini": [] } }"""u8.ToArray();

        Assert.False(ModelCatalog.Parse(json).ContainsKey("gemini"));
    }

    [Fact]
    public void ParseReturnsEmptyMapOnGarbageJson()
    {
        Assert.Empty(ModelCatalog.Parse("not json at all"u8.ToArray()));
    }

    [Fact]
    public async Task RefreshAsyncKeepsBuiltInsWhenTheRequestFails()
    {
        var client = new HttpClient(new ThrowingHandler());

        await ModelCatalog.RefreshAsync(client);

        // Never throws, and the built-in fallback is still what Models() returns —
        // mirrors the Swift refresh()'s "silently keeps the built-ins on any failure".
        Assert.Equal(ModelCatalog.BuiltIn(AIProvider.Openai), ModelCatalog.Models(AIProvider.Openai));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("simulated network failure");
    }
}
