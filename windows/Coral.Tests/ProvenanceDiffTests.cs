using System.Linq;
using Coral.Core.Models;
using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>The per-line diff annotator: added lines map to their new-file
/// line number via the hunk header, and thus to the provider that wrote each
/// line. Port of <c>ProvenanceDiffTests.swift</c>.</summary>
public class ProvenanceDiffTests
{
    private static readonly EditProvenance Gem = new("backend", "Gemini 2.5 Pro", AIProvider.Gemini);
    private static readonly EditProvenance Oai = new("frontend", "GPT-5", AIProvider.Openai);

    [Fact]
    public void AddedLinesAreTaggedWithTheirProvider()
    {
        // A new file "shared.txt" with two added lines: line 1 by gemini, line 2 by openai.
        var diff = "diff --git a/shared.txt b/shared.txt\n" +
            "new file mode 100644\n" +
            "--- /dev/null\n" +
            "+++ b/shared.txt\n" +
            "@@ -0,0 +1,2 @@\n" +
            "+line by gemini\n" +
            "+line by openai";
        var authors = new Dictionary<string, IReadOnlyList<EditProvenance?>>
        {
            ["shared.txt"] = new List<EditProvenance?> { Gem, Oai },
        };

        var annotated = ProvenanceDiff.Annotate(diff, authors);

        var added = annotated.Where(l => l.Kind == ProvenanceDiffLineKind.Added).ToList();
        Assert.Equal(2, added.Count);
        Assert.Equal(AIProvider.Gemini, added[0].Provider);
        Assert.Equal(AIProvider.Openai, added[1].Provider);
        // File + hunk headers are classified, never colored.
        Assert.Contains(annotated, l => l.Kind == ProvenanceDiffLineKind.HunkHeader && l.Text.StartsWith("@@"));
        Assert.Contains(annotated, l => l.Kind == ProvenanceDiffLineKind.FileHeader && l.Text.Contains("shared.txt"));
    }

    [Fact]
    public void HunkOffsetsMapAddedLinesToTheRightAuthor()
    {
        // Change at new-file line 3: only line 3 is added, authored by openai.
        var diff = "+++ b/f.swift\n" +
            "@@ -2,2 +2,3 @@\n" +
            " context a\n" +
            "+inserted line\n" +
            " context b";
        // f.swift final lines: [1,2,3,4] where line 3 (index 2) is openai's insert.
        var authors = new Dictionary<string, IReadOnlyList<EditProvenance?>>
        {
            ["f.swift"] = new List<EditProvenance?> { Gem, Gem, Oai, Gem },
        };

        var annotated = ProvenanceDiff.Annotate(diff, authors);

        var added = annotated.Where(l => l.Kind == ProvenanceDiffLineKind.Added).ToList();
        var only = Assert.Single(added);
        Assert.Equal(AIProvider.Openai, only.Provider); // mapped to new-file line 3
    }

    [Fact]
    public void UnknownFileOrMissingAuthorsLeavesProviderNull()
    {
        var diff = "+++ b/x.txt\n@@ -0,0 +1,1 @@\n+hello";

        var annotated = ProvenanceDiff.Annotate(diff, new Dictionary<string, IReadOnlyList<EditProvenance?>>());

        Assert.Null(annotated.First(l => l.Kind == ProvenanceDiffLineKind.Added).Provider);
    }

    [Fact]
    public void RemovedAndContextLinesAreNeverAttributed()
    {
        var diff = "+++ b/f.txt\n@@ -1,2 +1,2 @@\n-old line\n context line\n+new line";
        var authors = new Dictionary<string, IReadOnlyList<EditProvenance?>>
        {
            ["f.txt"] = new List<EditProvenance?> { Oai, Oai },
        };

        var annotated = ProvenanceDiff.Annotate(diff, authors);

        Assert.Null(annotated.First(l => l.Kind == ProvenanceDiffLineKind.Removed).Provider);
        Assert.Null(annotated.First(l => l.Kind == ProvenanceDiffLineKind.Context).Provider);
    }

    [Fact]
    public void DevNullSourceHeaderIsHandledWithoutCrashing()
    {
        var diff = "--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1,1 @@\n+hi";
        var authors = new Dictionary<string, IReadOnlyList<EditProvenance?>>
        {
            ["new.txt"] = new List<EditProvenance?> { Oai },
        };

        var annotated = ProvenanceDiff.Annotate(diff, authors);

        Assert.Equal(AIProvider.Openai, annotated.First(l => l.Kind == ProvenanceDiffLineKind.Added).Provider);
    }

    [Fact]
    public void MultipleFilesEachTrackTheirOwnNewLineCounter()
    {
        var diff = "+++ b/a.txt\n@@ -0,0 +1,1 @@\n+a-line\n" +
            "+++ b/b.txt\n@@ -0,0 +1,1 @@\n+b-line";
        var authors = new Dictionary<string, IReadOnlyList<EditProvenance?>>
        {
            ["a.txt"] = new List<EditProvenance?> { Gem },
            ["b.txt"] = new List<EditProvenance?> { Oai },
        };

        var annotated = ProvenanceDiff.Annotate(diff, authors);

        var added = annotated.Where(l => l.Kind == ProvenanceDiffLineKind.Added).ToList();
        Assert.Equal(AIProvider.Gemini, added[0].Provider);
        Assert.Equal(AIProvider.Openai, added[1].Provider);
    }
}
