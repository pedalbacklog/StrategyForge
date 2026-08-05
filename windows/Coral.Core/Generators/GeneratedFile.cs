namespace Coral.Core.Generators;

/// <summary>
/// Port of <c>StrategyForge/Generators/GeneratedFile.swift</c>. A file the app
/// intends to produce, as pure data. Generators return these so a UI can preview
/// exact contents before anything touches disk.
/// </summary>
/// <param name="RelativePath">Path relative to the repo root, e.g. <c>.claude/agents/worker-1.md</c>.</param>
/// <param name="Contents">Exact file contents.</param>
public sealed record GeneratedFile(string RelativePath, string Contents)
{
    /// <summary>The last path component, for tab labels (e.g. <c>worker-1.md</c>).</summary>
    public string DisplayName => Path.GetFileName(RelativePath);
}
