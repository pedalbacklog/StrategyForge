namespace Coral.Core.Generators;

/// <summary>
/// Port of <c>StrategyForge/Generators/FileDiff.swift</c>. A pre-write diff — what
/// a generated file would change on disk, computed as pure data so a UI can show
/// exactly what will happen BEFORE anything is written. Never touches disk itself;
/// callers pass in the existing contents.
/// </summary>
public enum FileDiffLineKind { Context, Added, Removed }

public sealed record FileDiffLine(FileDiffLineKind Kind, string Text);

public enum FileChange { Created, Modified, Unchanged, Deleted }

/// <summary>The change a single generated file represents against what's on disk.</summary>
public sealed class FileDiff
{
    /// <summary>The intended new state of the file.</summary>
    public GeneratedFile File { get; }
    /// <summary>Current on-disk contents, or null if the file does not exist yet.</summary>
    public string? Existing { get; }
    public FileChange Change { get; }
    /// <summary>The full line-by-line diff (context + added + removed), in file order.</summary>
    public IReadOnlyList<FileDiffLine> Lines { get; }

    public string RelativePath => File.RelativePath;
    public string DisplayName => File.DisplayName;
    public int Added => Lines.Count(l => l.Kind == FileDiffLineKind.Added);
    public int Removed => Lines.Count(l => l.Kind == FileDiffLineKind.Removed);

    private FileDiff(GeneratedFile file, string? existing, FileChange change, IReadOnlyList<FileDiffLine> lines)
    {
        File = file;
        Existing = existing;
        Change = change;
        Lines = lines;
    }

    /// <summary>Classify and diff a generated file against its current on-disk contents.</summary>
    public static FileDiff Make(GeneratedFile file, string? existing)
    {
        if (existing is null)
        {
            return new FileDiff(file, null, FileChange.Created,
                Split(file.Contents).Select(l => new FileDiffLine(FileDiffLineKind.Added, l)).ToList());
        }
        if (existing == file.Contents)
        {
            return new FileDiff(file, existing, FileChange.Unchanged,
                Split(existing).Select(l => new FileDiffLine(FileDiffLineKind.Context, l)).ToList());
        }
        return new FileDiff(file, existing, FileChange.Modified, LineDiff.Compute(existing, file.Contents));
    }

    /// <summary>A file that will be removed on write (a pruned managed agent file).
    /// There is no intended new state, so every existing line shows as removed.</summary>
    public static FileDiff Deleted(string relativePath, string? existing) =>
        new(new GeneratedFile(relativePath, ""), existing, FileChange.Deleted,
            Split(existing ?? "").Select(l => new FileDiffLine(FileDiffLineKind.Removed, l)).ToList());

    /// <summary>Split into lines without inventing a trailing empty line for empty input.</summary>
    public static List<string> Split(string s) => s.Length == 0 ? new List<string>() : s.Split('\n').ToList();
}

/// <summary>A minimal LCS line differ. Config files are small, so the O(n·m) table
/// is fine and keeps the diff stable and deterministic (good for tests).</summary>
public static class LineDiff
{
    public static List<FileDiffLine> Compute(string old, string @new)
    {
        var a = FileDiff.Split(old);
        var b = FileDiff.Split(@new);
        var n = a.Count;
        var m = b.Count;
        if (n == 0) return b.Select(l => new FileDiffLine(FileDiffLineKind.Added, l)).ToList();
        if (m == 0) return a.Select(l => new FileDiffLine(FileDiffLineKind.Removed, l)).ToList();

        // dp[i, j] = length of the LCS of a[i...] and b[j...].
        var dp = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                dp[i, j] = a[i] == b[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var result = new List<FileDiffLine>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (a[x] == b[y])
            {
                result.Add(new FileDiffLine(FileDiffLineKind.Context, a[x]));
                x++; y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                result.Add(new FileDiffLine(FileDiffLineKind.Removed, a[x]));
                x++;
            }
            else
            {
                result.Add(new FileDiffLine(FileDiffLineKind.Added, b[y]));
                y++;
            }
        }
        while (x < n) { result.Add(new FileDiffLine(FileDiffLineKind.Removed, a[x])); x++; }
        while (y < m) { result.Add(new FileDiffLine(FileDiffLineKind.Added, b[y])); y++; }
        return result;
    }
}
