using Coral.Core.Generators;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of StrategyForgeTests/DiffTests.swift.</summary>
public class FileDiffTests
{
    [Fact]
    public void CreatedWhenNoExistingFile()
    {
        var file = new GeneratedFile("CLAUDE.md", "a\nb\nc");
        var diff = FileDiff.Make(file, null);
        Assert.Equal(FileChange.Created, diff.Change);
        Assert.Equal(3, diff.Added);
        Assert.Equal(0, diff.Removed);
        Assert.All(diff.Lines, l => Assert.Equal(FileDiffLineKind.Added, l.Kind));
    }

    [Fact]
    public void UnchangedWhenIdentical()
    {
        var file = new GeneratedFile("x.md", "one\ntwo");
        var diff = FileDiff.Make(file, "one\ntwo");
        Assert.Equal(FileChange.Unchanged, diff.Change);
        Assert.Equal(0, diff.Added);
        Assert.Equal(0, diff.Removed);
        Assert.All(diff.Lines, l => Assert.Equal(FileDiffLineKind.Context, l.Kind));
    }

    [Fact]
    public void ModifiedCountsAddedAndRemoved()
    {
        var file = new GeneratedFile("x.md", "one\nTWO\nthree");
        var diff = FileDiff.Make(file, "one\ntwo\nthree");
        Assert.Equal(FileChange.Modified, diff.Change);
        Assert.Equal(1, diff.Added);   // "TWO"
        Assert.Equal(1, diff.Removed); // "two"
        Assert.Contains(new FileDiffLine(FileDiffLineKind.Context, "one"), diff.Lines);
        Assert.Contains(new FileDiffLine(FileDiffLineKind.Context, "three"), diff.Lines);
    }

    [Fact]
    public void EmptyContentsProducesNoLines()
    {
        var file = new GeneratedFile("x.md", "");
        var diff = FileDiff.Make(file, null);
        Assert.Equal(FileChange.Created, diff.Change);
        Assert.Empty(diff.Lines);
    }

    [Fact]
    public void DeletedShowsEveryLineRemoved()
    {
        var diff = FileDiff.Deleted(".claude/agents/old-worker.md", "line 1\nline 2");
        Assert.Equal(FileChange.Deleted, diff.Change);
        Assert.Equal(".claude/agents/old-worker.md", diff.RelativePath);
        Assert.Equal(2, diff.Removed);
        Assert.Equal(0, diff.Added);
        Assert.All(diff.Lines, l => Assert.Equal(FileDiffLineKind.Removed, l.Kind));
    }
}

/// <summary>Port of StrategyForgeTests/DiffTests.swift's LineDiffTests.</summary>
public class LineDiffTests
{
    [Fact]
    public void PureInsertionAddsOnlyTheNewLine()
    {
        var lines = LineDiff.Compute("a\nc", "a\nb\nc");
        Assert.Equal(new[] { new FileDiffLine(FileDiffLineKind.Added, "b") },
            lines.Where(l => l.Kind == FileDiffLineKind.Added));
        Assert.DoesNotContain(lines, l => l.Kind == FileDiffLineKind.Removed);
        Assert.Equal(new[]
        {
            new FileDiffLine(FileDiffLineKind.Context, "a"),
            new FileDiffLine(FileDiffLineKind.Added, "b"),
            new FileDiffLine(FileDiffLineKind.Context, "c"),
        }, lines);
    }

    [Fact]
    public void PureDeletionRemovesOnlyTheDroppedLine()
    {
        var lines = LineDiff.Compute("a\nb\nc", "a\nc");
        Assert.Equal(new[] { new FileDiffLine(FileDiffLineKind.Removed, "b") },
            lines.Where(l => l.Kind == FileDiffLineKind.Removed));
        Assert.DoesNotContain(lines, l => l.Kind == FileDiffLineKind.Added);
    }

    [Fact]
    public void FromEmptyIsAllAdded()
    {
        var lines = LineDiff.Compute("", "x\ny");
        Assert.Equal(new[]
        {
            new FileDiffLine(FileDiffLineKind.Added, "x"),
            new FileDiffLine(FileDiffLineKind.Added, "y"),
        }, lines);
    }

    [Fact]
    public void ToEmptyIsAllRemoved()
    {
        var lines = LineDiff.Compute("x\ny", "");
        Assert.All(lines, l => Assert.Equal(FileDiffLineKind.Removed, l.Kind));
        Assert.Equal(2, lines.Count);
    }
}
