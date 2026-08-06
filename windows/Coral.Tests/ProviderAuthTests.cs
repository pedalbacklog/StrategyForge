using Coral.Core.Models;
using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of the behavior in StrategyForge/Services/ProviderAuth.swift —
/// no direct Swift test file existed to mirror, so these are written from the
/// documented behavior of freshness(_:). Uses FreshnessUncached against a temp
/// directory standing in for %USERPROFILE%, the same pattern as
/// BinaryResolverTests/StrategyWriterTests.</summary>
public class ProviderAuthTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "coral-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteJson(string home, string relativeDir, string fileName, string json)
    {
        var dir = Path.Combine(home, relativeDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), json);
    }

    [Fact]
    public void ClaudeWithNoCredentialsFileIsUnknownNotMissing()
    {
        var home = TempDir();
        try
        {
            Assert.Equal(ProviderAuth.State.Unknown, ProviderAuth.FreshnessUncached(AIProvider.Claude, home));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void ClaudeWithUnexpiredTokenIsOk()
    {
        var home = TempDir();
        try
        {
            var future = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
            WriteJson(home, ".claude", ".credentials.json",
                "{\"claudeAiOauth\":{\"expiresAt\":" + future + "}}");
            Assert.Equal(ProviderAuth.State.Ok, ProviderAuth.FreshnessUncached(AIProvider.Claude, home));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void ClaudeWithExpiredTokenIsExpired()
    {
        var home = TempDir();
        try
        {
            var past = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
            WriteJson(home, ".claude", ".credentials.json",
                "{\"claudeAiOauth\":{\"expiresAt\":" + past + "}}");
            Assert.Equal(ProviderAuth.State.Expired, ProviderAuth.FreshnessUncached(AIProvider.Claude, home));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void ClaudeCredentialsFileWithoutExpiryFieldIsOk()
    {
        var home = TempDir();
        try
        {
            WriteJson(home, ".claude", ".credentials.json", """{"claudeAiOauth":{}}""");
            Assert.Equal(ProviderAuth.State.Ok, ProviderAuth.FreshnessUncached(AIProvider.Claude, home));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void ClaudeUnparsableCredentialsFileIsUnknown()
    {
        var home = TempDir();
        try
        {
            WriteJson(home, ".claude", ".credentials.json", "not json");
            Assert.Equal(ProviderAuth.State.Unknown, ProviderAuth.FreshnessUncached(AIProvider.Claude, home));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void GeminiWithNoCredentialsFileIsMissing()
    {
        var home = TempDir();
        try
        {
            Assert.Equal(ProviderAuth.State.Missing, ProviderAuth.FreshnessUncached(AIProvider.Gemini, home));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void GeminiWithUnexpiredTokenIsOk()
    {
        var home = TempDir();
        try
        {
            var future = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
            WriteJson(home, ".gemini", "oauth_creds.json", "{\"expiry_date\":" + future + "}");
            Assert.Equal(ProviderAuth.State.Ok, ProviderAuth.FreshnessUncached(AIProvider.Gemini, home));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void GeminiWithExpiredTokenIsExpired()
    {
        var home = TempDir();
        try
        {
            var past = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
            WriteJson(home, ".gemini", "oauth_creds.json", "{\"expiry_date\":" + past + "}");
            Assert.Equal(ProviderAuth.State.Expired, ProviderAuth.FreshnessUncached(AIProvider.Gemini, home));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void OpenaiWithAuthFileIsOk()
    {
        var home = TempDir();
        try
        {
            WriteJson(home, ".codex", "auth.json", """{"token":"opaque"}""");
            Assert.Equal(ProviderAuth.State.Ok, ProviderAuth.FreshnessUncached(AIProvider.Openai, home));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void OpenaiWithNoAuthFileIsMissing()
    {
        var home = TempDir();
        try
        {
            Assert.Equal(ProviderAuth.State.Missing, ProviderAuth.FreshnessUncached(AIProvider.Openai, home));
        }
        finally { Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public async Task VerifyAsyncChecksEachRequestedProviderConcurrently()
    {
        var results = await ProviderAuth.VerifyAsync(new[] { AIProvider.Claude, AIProvider.Gemini, AIProvider.Openai });

        Assert.Equal(3, results.Count);
        Assert.True(results.ContainsKey(AIProvider.Claude));
        Assert.True(results.ContainsKey(AIProvider.Gemini));
        Assert.True(results.ContainsKey(AIProvider.Openai));
    }
}
