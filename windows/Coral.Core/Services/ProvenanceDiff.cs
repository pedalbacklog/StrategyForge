using System.Linq;
using Coral.Core.Models;

namespace Coral.Core.Services;

public enum ProvenanceDiffLineKind { FileHeader, HunkHeader, Added, Removed, Context }

/// <summary>One line of an annotated diff. Port of
/// <c>ProvenanceDiff.swift</c>'s <c>ProvenanceDiffLine</c>.</summary>
/// <param name="Provider">The provider that authored this line (added lines
/// only, when known).</param>
public sealed record ProvenanceDiffLine(int Index, string Text, ProvenanceDiffLineKind Kind, AIProvider? Provider);

/// <summary>
/// Annotates a unified git diff with per-line cross-provider authorship: each
/// added (<c>+</c>) line is mapped, via the hunk headers, to its new-file
/// line number and thus to the provider that wrote it — the per-line
/// authorship map <see cref="CrossProviderEditor"/> already computes
/// (<c>CrossProviderResult.LineAuthors</c>) but <c>TeamRunPage</c> doesn't
/// use yet (only the flat per-file summary, <c>TeamRunViewModel.AuthorshipLines</c>).
/// Port of <c>ProvenanceDiff.swift</c>. Pure parsing, so it's unit-tested; a
/// future diff view just colors each line by its <see cref="ProvenanceDiffLine.Provider"/> —
/// same "data layer ahead of any UI" scope as <c>DiagnosticsLog</c>/
/// <c>ClaudeUsageStore</c>/<c>WastedWork</c>, except this one has an obvious,
/// already-computed data source waiting for it (unlike those three, which
/// wait on a product decision about where to surface something new).
/// </summary>
public static class ProvenanceDiff
{
    /// <summary>Parse <paramref name="diff"/> (a git unified diff spanning
    /// one or more files) into annotated lines, tagging each <c>+</c> line
    /// with its author's provider from <paramref name="lineAuthors"/> (keyed
    /// by repo-relative path → one author per final-file line).</summary>
    public static IReadOnlyList<ProvenanceDiffLine> Annotate(
        string diff, IReadOnlyDictionary<string, IReadOnlyList<EditProvenance?>> lineAuthors)
    {
        var outp = new List<ProvenanceDiffLine>();
        var currentFile = "";
        var newLineNo = 0; // 1-based line number in the new file
        var i = 0;
        foreach (var raw in diff.Split('\n'))
        {
            if (raw.StartsWith("+++ "))
            {
                currentFile = PathFromPlusPlusPlus(raw);
                outp.Add(new ProvenanceDiffLine(i, raw, ProvenanceDiffLineKind.FileHeader, null));
            }
            else if (raw.StartsWith("diff --git") || raw.StartsWith("--- ") || raw.StartsWith("index ")
                || raw.StartsWith("new file") || raw.StartsWith("deleted file")
                || raw.StartsWith("rename ") || raw.StartsWith("similarity "))
            {
                outp.Add(new ProvenanceDiffLine(i, raw, ProvenanceDiffLineKind.FileHeader, null));
            }
            else if (raw.StartsWith("@@"))
            {
                newLineNo = NewStartFromHunkHeader(raw);
                outp.Add(new ProvenanceDiffLine(i, raw, ProvenanceDiffLineKind.HunkHeader, null));
            }
            else if (raw.StartsWith("+"))
            {
                AIProvider? provider = lineAuthors.TryGetValue(currentFile, out var authors)
                    && newLineNo >= 1 && newLineNo <= authors.Count
                    ? authors[newLineNo - 1]?.Provider
                    : null;
                outp.Add(new ProvenanceDiffLine(i, raw, ProvenanceDiffLineKind.Added, provider));
                newLineNo++;
            }
            else if (raw.StartsWith("-"))
            {
                outp.Add(new ProvenanceDiffLine(i, raw, ProvenanceDiffLineKind.Removed, null));
            }
            else
            {
                // Context line (leading space, or a blank line inside the diff body).
                outp.Add(new ProvenanceDiffLine(i, raw, ProvenanceDiffLineKind.Context, null));
                newLineNo++;
            }
            i++;
        }
        return outp;
    }

    // MARK: - Parsing helpers

    /// <summary>"+++ b/Sources/Foo.swift" → "Sources/Foo.swift" (strips the a//b/ prefix).</summary>
    private static string PathFromPlusPlusPlus(string line)
    {
        var p = line[4..].Trim();
        if (p == "/dev/null") return "";
        if (p.StartsWith("b/") || p.StartsWith("a/")) p = p[2..];
        return p;
    }

    /// <summary>"@@ -12,7 +34,9 @@ func x()" → 34 (the new-file start line).</summary>
    private static int NewStartFromHunkHeader(string line)
    {
        // Find the "+<num>" token between the @@ markers.
        var plus = line.IndexOf('+');
        if (plus < 0) return 1;
        var tail = line[(plus + 1)..];
        var digits = new string(tail.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 1;
    }
}
