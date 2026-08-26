namespace Coral.Core.Services;

/// <summary>Filesystem check <see cref="BinaryResolver"/> needs — abstracted so its
/// candidate-path logic can be unit tested without touching real disk.</summary>
public interface IExecutableProbe
{
    bool IsExecutable(string path);
}

/// <summary>Probes the real filesystem via <see cref="File.Exists"/>. Good enough
/// on Windows: unlike POSIX there's no separate "executable" bit to check —
/// anything on PATH with a recognized extension (.exe/.cmd/.bat) is runnable.</summary>
public sealed class RealExecutableProbe : IExecutableProbe
{
    public bool IsExecutable(string path) => File.Exists(path);
}

/// <summary>
/// Resolution strategy for the <c>claude</c>/<c>codex</c>/<c>gemini</c> CLI
/// binaries — the Windows counterpart of <c>StrategyForge/Services/
/// ClaudeRunner.swift</c>'s <c>resolveBinary()</c>/<c>which()</c>. NOT a
/// line-for-line port: the Swift original shells out to an interactive login
/// shell (<c>/bin/zsh -ilc</c>) to source <c>~/.zshrc</c>/nvm/Homebrew, which has
/// no Windows equivalent, and Windows CLIs installed via npm land as <c>.cmd</c>
/// shims in entirely different well-known locations (see windows/PORT-PLAN.md
/// §3's macOS→Windows map). So this is a fresh design for the platform rather
/// than a mechanical translation — but it keeps the same shape: an absolute
/// configured path first, then a PATH search, then well-known fallback
/// locations by leaf name, so a freshly-installed CLI is found even before a
/// new shell/session would pick up its updated PATH.
/// </summary>
public static class BinaryResolver
{
    /// <summary>Resolve <paramref name="configured"/> to an absolute, existing
    /// executable path, or null. Pure given its inputs — <paramref name="probe"/>
    /// stands in for the filesystem and <paramref name="pathDirs"/> for PATH, so
    /// this is fully unit-testable without touching real disk or the real PATH
    /// (and without needing to run on an actual Windows machine to verify the
    /// candidate-path logic).</summary>
    public static string? ResolveUncached(string configured, IExecutableProbe probe, string homeDirectory,
        IReadOnlyList<string> pathDirs)
    {
        var name = string.IsNullOrEmpty(configured) ? "claude" : configured;

        // 1. An absolute path the user configured.
        if (Path.IsPathRooted(name) && probe.IsExecutable(name)) return name;

        var leafCandidates = ExecutableLeafNames(Path.GetFileName(name));

        // 2. Search PATH directories — the Windows equivalent of a shell's `which`.
        foreach (var dir in pathDirs)
        {
            foreach (var leaf in leafCandidates)
            {
                var full = Path.Combine(dir, leaf);
                if (probe.IsExecutable(full)) return full;
            }
        }

        // 3. Well-known install locations by leaf name, so a fresh
        // `npm install -g` is found even when its bin dir isn't on PATH yet —
        // %APPDATA%\npm is npm's default global prefix on Windows.
        var npmGlobal = Path.Combine(homeDirectory, "AppData", "Roaming", "npm");
        foreach (var leaf in leafCandidates)
        {
            var full = Path.Combine(npmGlobal, leaf);
            if (probe.IsExecutable(full)) return full;
        }

        return null;
    }

    /// <summary>Resolve against the REAL filesystem/PATH/home directory. This is
    /// the thin, only-meaningfully-testable-on-Windows wiring around the pure
    /// <see cref="ResolveUncached"/> — no logic of its own to unit test.</summary>
    public static string? Resolve(string configured)
    {
        var probe = new RealExecutableProbe();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return ResolveUncached(configured, probe, home, pathDirs);
    }

    /// <summary>The file names to try for a leaf name: strip any recognized
    /// extension first, then try the Windows executable extensions in the order
    /// a CLI installed via npm is actually likely to appear — <c>.cmd</c> shim
    /// first, then a real <c>.exe</c>, then <c>.bat</c>.</summary>
    private static List<string> ExecutableLeafNames(string leaf)
    {
        var baseName = StripKnownExtension(leaf);
        return new List<string> { baseName + ".cmd", baseName + ".exe", baseName + ".bat" };
    }

    private static string StripKnownExtension(string leaf)
    {
        foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
        {
            if (leaf.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return leaf[..^ext.Length];
        }
        return leaf;
    }
}
