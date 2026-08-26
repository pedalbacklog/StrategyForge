using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Tests for DiagnosticsLog's pure *Uncached core against a real
/// temp file — mirrors AppSettingsTests's/ProviderAuthTests's style. Never
/// touches the real %LOCALAPPDATA%.</summary>
public class DiagnosticsLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "coral-diagnostics-tests-" + Guid.NewGuid());
    private string LogPath => Path.Combine(_dir, "diagnostics.log");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void ContentsUncachedIsEmptyWhenTheFileDoesntExist()
    {
        Assert.Equal("", DiagnosticsLog.ContentsUncached(LogPath));
    }

    [Fact]
    public void RecordUncachedCreatesTheParentDirectoryAndAHeader()
    {
        Assert.False(Directory.Exists(_dir));

        DiagnosticsLog.RecordUncached(LogPath, "boom");

        var contents = DiagnosticsLog.ContentsUncached(LogPath);
        Assert.StartsWith("# Coral diagnostics", contents);
        Assert.Contains("boom", contents);
    }

    [Fact]
    public void RecordUncachedTagsTheLineWithItsLevel()
    {
        DiagnosticsLog.RecordUncached(LogPath, "something failed", level: "ERROR");
        DiagnosticsLog.RecordUncached(LogPath, "just fyi", level: "INFO");

        var contents = DiagnosticsLog.ContentsUncached(LogPath);
        Assert.Contains("[ERROR] something failed", contents);
        Assert.Contains("[INFO] just fyi", contents);
    }

    [Fact]
    public void RecordUncachedReplacesNewlinesSoEachRecordIsOneLine()
    {
        DiagnosticsLog.RecordUncached(LogPath, "line one\nline two");

        var contents = DiagnosticsLog.ContentsUncached(LogPath);
        Assert.Contains("line one ⏎ line two", contents);
        // Only the header adds a line break of its own — the record itself is one line.
        Assert.Equal(2, contents.TrimEnd('\n').Split('\n').Length);
    }

    [Fact]
    public void MultipleRecordsAppendInOrder()
    {
        DiagnosticsLog.RecordUncached(LogPath, "first");
        DiagnosticsLog.RecordUncached(LogPath, "second");

        var lines = DiagnosticsLog.ContentsUncached(LogPath).Split('\n');
        Assert.Contains(lines, l => l.Contains("first"));
        Assert.Contains(lines, l => l.Contains("second"));
        Assert.True(Array.FindIndex(lines, l => l.Contains("first")) < Array.FindIndex(lines, l => l.Contains("second")));
    }

    [Fact]
    public void ClearUncachedRemovesTheFile()
    {
        DiagnosticsLog.RecordUncached(LogPath, "boom");
        Assert.True(File.Exists(LogPath));

        DiagnosticsLog.ClearUncached(LogPath);

        Assert.False(File.Exists(LogPath));
        Assert.Equal("", DiagnosticsLog.ContentsUncached(LogPath));
    }

    [Fact]
    public void ClearUncachedIsANoOpWhenTheFileDoesntExist()
    {
        DiagnosticsLog.ClearUncached(LogPath); // must not throw
    }

    [Fact]
    public void TrimsTheFileOnceItGrowsPastTheCapKeepingTheRecentTail()
    {
        // Each record is well over 100 bytes; a few thousand blow past the 256KB cap.
        var longMessage = new string('x', 400);
        for (var i = 0; i < 2000; i++)
        {
            DiagnosticsLog.RecordUncached(LogPath, $"REC{i}-{longMessage}");
        }

        var contents = DiagnosticsLog.ContentsUncached(LogPath);
        Assert.True(new FileInfo(LogPath).Length < 2000L * (longMessage.Length + 50));
        Assert.StartsWith("# Coral diagnostics", contents);
        // The oldest record should have been trimmed away; the newest should survive.
        Assert.DoesNotContain("REC0-" + longMessage, contents);
        Assert.Contains("REC1999-" + longMessage, contents);
    }

    [Fact]
    public void RecordUncachedNeverThrowsEvenAgainstAnUnwritablePath()
    {
        // A path with an invalid/reserved segment on Windows (and just plain
        // unwritable on any OS, given the null byte) — the write must fail
        // silently, matching the Swift original's best-effort contract.
        var badPath = Path.Combine(_dir, "\0invalid");

        var ex = Record.Exception(() => DiagnosticsLog.RecordUncached(badPath, "boom"));

        Assert.Null(ex);
    }

    [Fact]
    public void DefaultPathPointsUnderLocalAppDataCoral()
    {
        var path = DiagnosticsLog.DefaultPath();

        Assert.Contains("Coral", path);
        Assert.EndsWith("diagnostics.log", path);
    }
}
