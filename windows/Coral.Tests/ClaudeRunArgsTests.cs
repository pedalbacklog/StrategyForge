using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Tests for ClaudeRunArgs.Build — port of the argument-construction
/// slice of ClaudeRunner.swift's stream(), exercised via the exact flag
/// order/presence rather than a Swift test file (the original isn't
/// unit-tested standalone; it's implicit in the process-spawning code).</summary>
public class ClaudeRunArgsTests
{
    [Fact]
    public void NewSessionUsesSessionIdFlag()
    {
        var args = ClaudeRunArgs.Build("hello", "claude-sonnet-5", "sess-1", resume: false, permissionMode: "default");
        Assert.Contains("--session-id", args);
        Assert.Contains("sess-1", args);
        Assert.DoesNotContain("--resume", args);
    }

    [Fact]
    public void ResumeUsesResumeFlagWithTheSameSessionId()
    {
        var args = ClaudeRunArgs.Build("hello", "claude-sonnet-5", "sess-1", resume: true, permissionMode: "default");
        Assert.Contains("--resume", args);
        var resumeIndex = args.IndexOf("--resume");
        Assert.Equal("sess-1", args[resumeIndex + 1]);
        Assert.DoesNotContain("--session-id", args);
    }

    [Fact]
    public void EffortFlagOmittedByDefault()
    {
        var args = ClaudeRunArgs.Build("hello", "claude-sonnet-5", "sess-1", resume: false, permissionMode: "default");
        Assert.DoesNotContain("--effort", args);
    }

    [Fact]
    public void EffortFlagIncludedWhenPinned()
    {
        var args = ClaudeRunArgs.Build("hello", "claude-sonnet-5", "sess-1", resume: false,
            permissionMode: "default", effort: "high");
        var i = args.IndexOf("--effort");
        Assert.True(i >= 0);
        Assert.Equal("high", args[i + 1]);
    }

    [Fact]
    public void ExtraDirsEachGetTheirOwnAddDirFlag()
    {
        var args = ClaudeRunArgs.Build("hello", "claude-sonnet-5", "sess-1", resume: false,
            permissionMode: "default", extraDirs: new List<string> { "/a", "/b" });
        var addDirCount = args.Count(a => a == "--add-dir");
        Assert.Equal(2, addDirCount);
        Assert.Contains("/a", args);
        Assert.Contains("/b", args);
    }

    [Fact]
    public void PromptIsLastAfterTheDashPFlag()
    {
        var args = ClaudeRunArgs.Build("do the thing", "claude-sonnet-5", "sess-1", resume: false,
            permissionMode: "default");
        Assert.Equal("-p", args[^2]);
        Assert.Equal("do the thing", args[^1]);
    }

    [Fact]
    public void ModelAndPermissionModePassThrough()
    {
        var args = ClaudeRunArgs.Build("hello", "claude-opus-5", "sess-1", resume: false, permissionMode: "acceptEdits");
        var modelIndex = args.IndexOf("--model");
        Assert.Equal("claude-opus-5", args[modelIndex + 1]);
        var pmIndex = args.IndexOf("--permission-mode");
        Assert.Equal("acceptEdits", args[pmIndex + 1]);
    }
}
