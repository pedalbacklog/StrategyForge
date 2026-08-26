using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Tests for CodexUsageStore's pure LoadUncached core against a real
/// temp directory standing in for %USERPROFILE% — mirrors
/// ClaudeUsageStoreTests's style. Never touches the real ~/.codex.</summary>
public class CodexUsageStoreTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "coral-codex-usage-tests-" + Guid.NewGuid());
    private string SessionsDir => Path.Combine(_home, ".codex", "sessions");

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true);
    }

    private void WriteLog(string relativePath, params string[] lines)
    {
        var full = Path.Combine(SessionsDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllLines(full, lines);
    }

    private static string TokenCountLine(string timestampIso, double primaryPct, int primaryWindowMinutes,
        double? resetsAtEpoch = null, int totalTokens = 0, string planType = "plus") =>
        "{\"timestamp\":\"" + timestampIso + "\",\"payload\":{\"type\":\"token_count\"," +
        "\"info\":{\"total_token_usage\":{\"total_tokens\":" + totalTokens + "}}," +
        "\"rate_limits\":{\"plan_type\":\"" + planType + "\",\"primary\":{\"used_percent\":" + primaryPct +
        ",\"window_minutes\":" + primaryWindowMinutes +
        (resetsAtEpoch is { } r ? ",\"resets_at\":" + r.ToString(System.Globalization.CultureInfo.InvariantCulture) : "") +
        "}}}}";

    [Fact]
    public void NullWhenTheSessionsDirectoryDoesntExist()
    {
        Assert.Null(CodexUsageStore.LoadUncached(_home, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void NullWhenNoFileHasAUsableTokenCountLine()
    {
        WriteLog("2026/08/21/rollout-a.jsonl", """{"timestamp":"2026-08-21T10:00:00Z","payload":{"type":"turn_started"}}""");

        Assert.Null(CodexUsageStore.LoadUncached(_home, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ToleratesMalformedLinesAndFindsTheRealOne()
    {
        var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        WriteLog("2026/08/21/rollout-a.jsonl",
            "not even json",
            """{"payload":{"type":"token_count"}}""", // missing rate_limits entirely — still tolerated
            TokenCountLine(now.AddMinutes(-5).ToString("o"), 42.5, 300, totalTokens: 1000));

        var usage = CodexUsageStore.LoadUncached(_home, now);

        Assert.NotNull(usage);
        Assert.True(usage!.HasData);
        Assert.Equal(1000, usage.TotalTokens);
        Assert.Equal(42.5, usage.Primary!.UsedPercent);
    }

    [Fact]
    public void ParsesPrimaryPlanTypeAndResetsAt()
    {
        var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        var resetsAtEpoch = now.AddHours(3).ToUnixTimeSeconds();
        WriteLog("2026/08/21/rollout-a.jsonl",
            TokenCountLine(now.AddMinutes(-1).ToString("o"), 10, 300, resetsAtEpoch, 500, "pro"));

        var usage = CodexUsageStore.LoadUncached(_home, now);

        Assert.Equal("pro", usage!.PlanType);
        Assert.Equal(300, usage.Primary!.WindowMinutes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(resetsAtEpoch), usage.Primary.ResetsAt);
    }

    [Fact]
    public void PicksTheLatestTokenCountLineWithinAFile()
    {
        var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        WriteLog("2026/08/21/rollout-a.jsonl",
            TokenCountLine(now.AddHours(-2).ToString("o"), 10, 300, totalTokens: 100),
            TokenCountLine(now.AddMinutes(-1).ToString("o"), 90, 300, totalTokens: 900)); // the freshest state wins

        var usage = CodexUsageStore.LoadUncached(_home, now);

        Assert.Equal(900, usage!.TotalTokens);
        Assert.Equal(90, usage.Primary!.UsedPercent);
    }

    [Fact]
    public void PicksTheNewestFileWhenMultipleFilesHaveTokenCountLines()
    {
        var now = new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        WriteLog("2026/08/20/rollout-old.jsonl", TokenCountLine(now.AddHours(-20).ToString("o"), 10, 300, totalTokens: 111));
        WriteLog("2026/08/21/rollout-new.jsonl", TokenCountLine(now.AddMinutes(-1).ToString("o"), 55, 300, totalTokens: 999));
        File.SetLastWriteTimeUtc(Path.Combine(SessionsDir, "2026/08/20/rollout-old.jsonl"), now.AddHours(-20).UtcDateTime);
        File.SetLastWriteTimeUtc(Path.Combine(SessionsDir, "2026/08/21/rollout-new.jsonl"), now.AddMinutes(-1).UtcDateTime);

        var usage = CodexUsageStore.LoadUncached(_home, now);

        Assert.Equal(999, usage!.TotalTokens);
    }

    [Fact]
    public void FilesOlderThanFourDaysAreSkippedEntirely()
    {
        var now = DateTimeOffset.UtcNow;
        var path = "2026/08/10/rollout-stale.jsonl";
        WriteLog(path, TokenCountLine(now.AddDays(-5).ToString("o"), 10, 300, totalTokens: 500));
        File.SetLastWriteTimeUtc(Path.Combine(SessionsDir, path), now.AddDays(-5).UtcDateTime);

        Assert.Null(CodexUsageStore.LoadUncached(_home, now));
    }

    [Theory]
    [InlineData(10_080, "usage.codex.window.week")] // 7 days
    [InlineData(1_440, "usage.codex.window.day")]   // 1 day
    [InlineData(300, "usage.codex.window.5h")]
    public void KindLabelKeyClassifiesByWindowLength(int minutes, string expectedKey)
    {
        var window = new CodexWindow(10, minutes, null);

        Assert.Equal(expectedKey, window.KindLabelKey);
    }
}
