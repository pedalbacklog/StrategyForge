using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Tests for ClaudeUsageStore's pure LoadUncached core against a real
/// temp directory standing in for %USERPROFILE% — mirrors ProviderAuthTests's/
/// AppSettingsTests's style. Never touches the real ~/.claude.</summary>
public class ClaudeUsageStoreTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "coral-usage-tests-" + Guid.NewGuid());
    private string ProjectsDir => Path.Combine(_home, ".claude", "projects");

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private void WriteLog(string relativePath, params string[] lines)
    {
        var full = Path.Combine(ProjectsDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllLines(full, lines);
    }

    private static string AssistantLine(string timestampIso, string model, int inputTokens, int outputTokens) =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + timestampIso + "\",\"message\":{\"model\":\"" + model +
        "\",\"usage\":{\"input_tokens\":" + inputTokens + ",\"output_tokens\":" + outputTokens + "}}}";

    [Fact]
    public void EmptyWhenTheProjectsDirectoryDoesntExist()
    {
        var summary = ClaudeUsageStore.LoadUncached(_home, DateTimeOffset.UtcNow);

        Assert.False(summary.HasData);
        Assert.Equal(0, summary.WeekTokens);
        Assert.Empty(summary.WeekByModel);
    }

    [Fact]
    public void EmptyWhenNoJsonlFilesHaveUsableSamples()
    {
        WriteLog("proj/session.jsonl", """{"type":"user","timestamp":"2026-08-20T10:00:00Z"}""");

        var summary = ClaudeUsageStore.LoadUncached(_home, DateTimeOffset.UtcNow);

        Assert.False(summary.HasData);
    }

    [Fact]
    public void ToleratesMalformedAndIrrelevantLinesWithoutThrowing()
    {
        var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        WriteLog("proj/session.jsonl",
            "not even json",
            """{"type":"user","timestamp":"bad-date","message":{}}""",
            """{"type":"assistant","timestamp":"not-a-date","message":{"usage":{"input_tokens":5}}}""",
            AssistantLine(now.AddMinutes(-10).ToString("o"), "claude-sonnet-4-5", 10, 5));

        var summary = ClaudeUsageStore.LoadUncached(_home, now);

        Assert.True(summary.HasData);
        Assert.Equal(15, summary.WeekTokens);
    }

    [Fact]
    public void AggregatesTokensAcrossInputOutputCacheCreationAndCacheRead()
    {
        var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        var line = "{\"type\":\"assistant\",\"timestamp\":\"" + now.AddMinutes(-5).ToString("o") +
            "\",\"message\":{\"model\":\"claude-sonnet-4-5\",\"usage\":{\"input_tokens\":10,\"output_tokens\":5," +
            "\"cache_creation_input_tokens\":3,\"cache_read_input_tokens\":2}}}";
        WriteLog("proj/session.jsonl", line);

        var summary = ClaudeUsageStore.LoadUncached(_home, now);

        Assert.Equal(20, summary.WeekTokens);
        Assert.Equal(20, summary.BlockTokens);
    }

    [Fact]
    public void SkipsLinesWithZeroOrMissingTokens()
    {
        var now = DateTimeOffset.UtcNow;
        WriteLog("proj/session.jsonl", AssistantLine(now.ToString("o"), "claude-sonnet-4-5", 0, 0));

        var summary = ClaudeUsageStore.LoadUncached(_home, now);

        Assert.False(summary.HasData);
    }

    [Fact]
    public void FilesOlderThanEightDaysAreSkippedEntirely()
    {
        var now = DateTimeOffset.UtcNow;
        WriteLog("proj/stale.jsonl", AssistantLine(now.AddDays(-9).ToString("o"), "claude-sonnet-4-5", 100, 0));
        File.SetLastWriteTimeUtc(Path.Combine(ProjectsDir, "proj/stale.jsonl"), now.AddDays(-9).UtcDateTime);

        var summary = ClaudeUsageStore.LoadUncached(_home, now);

        Assert.False(summary.HasData);
    }

    [Fact]
    public void WeekWindowExcludesSamplesOlderThanSevenDaysButKeepsTheBlock()
    {
        var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        WriteLog("proj/session.jsonl",
            AssistantLine(now.AddDays(-8).ToString("o"), "claude-sonnet-4-5", 50, 0), // outside the 8-day file cutoff
            AssistantLine(now.AddMinutes(-5).ToString("o"), "claude-sonnet-4-5", 10, 0));
        // Keep the file itself fresh so only the per-sample week filter is exercised.

        var summary = ClaudeUsageStore.LoadUncached(_home, now);

        Assert.Equal(10, summary.WeekTokens); // the 8-day-old sample fell outside the 7-day window
    }

    [Fact]
    public void CurrentBlockRestartsAfterFiveHoursOfNoNewerEntry()
    {
        var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        WriteLog("proj/session.jsonl",
            AssistantLine(now.AddHours(-7).ToString("o"), "claude-sonnet-4-5", 100, 0), // old block, long expired
            AssistantLine(now.AddMinutes(-30).ToString("o"), "claude-sonnet-4-5", 25, 0)); // current block

        var summary = ClaudeUsageStore.LoadUncached(_home, now);

        Assert.Equal(25, summary.BlockTokens);
        Assert.NotNull(summary.BlockStart);
        Assert.NotNull(summary.BlockResetAt);
    }

    [Fact]
    public void BlockIsZeroWhenTheMostRecentActivitysBlockAlreadyElapsed()
    {
        var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        WriteLog("proj/session.jsonl", AssistantLine(now.AddHours(-6).ToString("o"), "claude-sonnet-4-5", 40, 0));

        var summary = ClaudeUsageStore.LoadUncached(_home, now);

        Assert.Equal(0, summary.BlockTokens);
        Assert.Null(summary.BlockStart);
        Assert.Null(summary.BlockResetAt);
        Assert.True(summary.HasData); // week/lastActivity still reflect the old sample
    }

    [Fact]
    public void WeekByModelOrdersByCapabilityNotUsage()
    {
        var now = DateTimeOffset.UtcNow;
        WriteLog("proj/session.jsonl",
            AssistantLine(now.ToString("o"), "claude-haiku-4-5", 1000, 0), // cheap model, heavy usage
            AssistantLine(now.ToString("o"), "claude-opus-4-7", 1, 0));    // powerful model, tiny usage

        var summary = ClaudeUsageStore.LoadUncached(_home, now);

        Assert.Equal("Opus 4.7", summary.WeekByModel[0].Model); // opus ranks above haiku despite fewer tokens
        Assert.Equal("Haiku 4.5", summary.WeekByModel[1].Model);
    }

    [Theory]
    [InlineData("claude-opus-4-7", "Opus 4.7")]
    [InlineData("claude-sonnet-4-5", "Sonnet 4.5")]
    [InlineData("claude-haiku-4-5-20251001", "Haiku 4.5")] // trailing build date must not become the version
    [InlineData("claude-fable-5", "Fable 5")]
    [InlineData("gpt-5", "gpt-5")]
    [InlineData("—", "—")]
    public void FriendlyModelMapsRawIdsToShortNames(string raw, string expected)
    {
        Assert.Equal(expected, ClaudeUsageStore.FriendlyModel(raw));
    }

    [Theory]
    [InlineData("Opus 4.7", 0)]
    [InlineData("Fable 5", 0)]
    [InlineData("Sonnet 4.5", 1)]
    [InlineData("GPT-5", 1)]
    [InlineData("GPT-5 Mini", 3)]
    [InlineData("Haiku 4.5", 3)]
    [InlineData("Gemini 2.5 Flash", 3)]
    public void PowerRankOrdersModelsByCapability(string model, int expectedRank)
    {
        Assert.Equal(expectedRank, ClaudeUsageStore.PowerRank(model));
    }
}
