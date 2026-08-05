using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Fake <see cref="IExecutableProbe"/> — an in-memory set of paths
/// considered executable, so BinaryResolver's candidate-path logic can be tested
/// without touching real disk or depending on which OS runs the test.</summary>
file sealed class FakeExecutableProbe : IExecutableProbe
{
    private readonly HashSet<string> _executables;
    public FakeExecutableProbe(params string[] executables) => _executables = new HashSet<string>(executables);
    public bool IsExecutable(string path) => _executables.Contains(path);
}

/// <summary>Tests for BinaryResolver.ResolveUncached — the pure candidate-path
/// logic. All paths are built with Path.Combine (never hardcoded separators), so
/// these are valid and meaningful on whatever OS actually runs `dotnet test` —
/// verifying real Windows path/extension conventions still needs windows-latest,
/// but the resolution ALGORITHM (absolute -> PATH -> well-known fallback,
/// preferring a shim before a real executable) is exercised here.</summary>
public class BinaryResolverTests
{
    [Fact]
    public void AbsolutePathWinsWhenExecutable()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "claude.exe");
        var probe = new FakeExecutableProbe(absolute);
        var result = BinaryResolver.ResolveUncached(absolute, probe, Path.GetTempPath(), new List<string>());
        Assert.Equal(absolute, result);
    }

    [Fact]
    public void AbsolutePathThatDoesNotExistFallsThroughToPathSearch()
    {
        var pathDir = Path.Combine(Path.GetTempPath(), "path-dir");
        var found = Path.Combine(pathDir, "claude.cmd");
        var probe = new FakeExecutableProbe(found);
        var missingAbsolute = Path.Combine(Path.GetTempPath(), "nowhere", "claude.exe");
        var result = BinaryResolver.ResolveUncached(missingAbsolute, probe, Path.GetTempPath(), new List<string> { pathDir });
        Assert.Equal(found, result);
    }

    [Fact]
    public void SearchesPathDirectoriesPreferringCmdShimOverExe()
    {
        var pathDir = Path.Combine(Path.GetTempPath(), "path-dir");
        var cmd = Path.Combine(pathDir, "claude.cmd");
        var exe = Path.Combine(pathDir, "claude.exe");
        var probe = new FakeExecutableProbe(cmd, exe);
        var result = BinaryResolver.ResolveUncached("claude", probe, Path.GetTempPath(), new List<string> { pathDir });
        Assert.Equal(cmd, result);
    }

    [Fact]
    public void FallsBackToNpmGlobalPrefixWhenNotOnPath()
    {
        var home = Path.Combine(Path.GetTempPath(), "home");
        var npmGlobal = Path.Combine(home, "AppData", "Roaming", "npm", "claude.cmd");
        var probe = new FakeExecutableProbe(npmGlobal);
        var elsewhere = Path.Combine(Path.GetTempPath(), "somewhere-else");
        var result = BinaryResolver.ResolveUncached("claude", probe, home, new List<string> { elsewhere });
        Assert.Equal(npmGlobal, result);
    }

    [Fact]
    public void ReturnsNullWhenNowhereFound()
    {
        var probe = new FakeExecutableProbe();
        var pathDir = Path.Combine(Path.GetTempPath(), "path-dir");
        var result = BinaryResolver.ResolveUncached("claude", probe, Path.GetTempPath(), new List<string> { pathDir });
        Assert.Null(result);
    }

    [Fact]
    public void EmptyNameDefaultsToClaude()
    {
        var pathDir = Path.Combine(Path.GetTempPath(), "path-dir");
        var found = Path.Combine(pathDir, "claude.cmd");
        var probe = new FakeExecutableProbe(found);
        var result = BinaryResolver.ResolveUncached("", probe, Path.GetTempPath(), new List<string> { pathDir });
        Assert.Equal(found, result);
    }

    [Fact]
    public void QualifiedExeNameStillResolvesViaVariantSearch()
    {
        // A name already ending in .exe still finds a .cmd shim for the same
        // leaf — the search always tries all three extensions, regardless of how
        // the configured name was spelled.
        var pathDir = Path.Combine(Path.GetTempPath(), "path-dir");
        var cmd = Path.Combine(pathDir, "codex.cmd");
        var probe = new FakeExecutableProbe(cmd);
        var result = BinaryResolver.ResolveUncached("codex.exe", probe, Path.GetTempPath(), new List<string> { pathDir });
        Assert.Equal(cmd, result);
    }
}
