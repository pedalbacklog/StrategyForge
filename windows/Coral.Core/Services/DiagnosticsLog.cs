using System.Linq;

namespace Coral.Core.Services;

/// <summary>
/// A tiny rolling diagnostics log: timestamped failure/event lines written to
/// a file in the app's local data folder so that when something goes wrong
/// the user can find and share it. Port of <c>DiagnosticsLog.swift</c> —
/// records only what already surfaces elsewhere (run errors, launch
/// failures); nothing is sent anywhere. Scoped narrower than the Swift
/// original for now: Swift calls this from ~15 places across services this
/// port hasn't built yet (MetaOrchestrator, KeychainStore, CrashReporter,
/// GraphifyService...) — only <see cref="ProviderOneShotRunner"/>'s two
/// failure paths are wired here, matching <c>ProviderRun.swift</c>'s own
/// <c>launch()</c>/one-shot <c>run()</c> call sites. No UI reads this yet
/// (Swift's own export flow lives in <c>AppModel.swift</c>, not ported) —
/// that's a follow-up once there's a place in the Windows UI for it.
/// </summary>
public static class DiagnosticsLog
{
    /// <summary>Trim the file once it grows past this, keeping the most
    /// recent tail (matches the Swift original).</summary>
    private const int MaxBytes = 256 * 1024;

    private static readonly object Lock = new();

    /// <summary>Append a timestamped line to the log at <paramref name="path"/>,
    /// creating its parent directory and a header if the file doesn't exist
    /// yet. <paramref name="level"/> is a short tag (ERROR / WARN / INFO).
    /// Never throws — a diagnostics write must not itself break the caller's
    /// error path.</summary>
    public static void RecordUncached(string path, string message, string level = "ERROR")
    {
        try
        {
            lock (Lock)
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (!File.Exists(path)) File.WriteAllText(path, Header());

                var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                var line = $"{stamp} [{level}] {message.Replace("\n", " ⏎ ")}\n";
                File.AppendAllText(path, line);

                TrimIfNeeded(path);
            }
        }
        catch
        {
            // Best-effort — a diagnostics write failing is never itself an error worth surfacing.
        }
    }

    /// <summary>The current log contents at <paramref name="path"/> ("" if
    /// there's nothing yet or it can't be read).</summary>
    public static string ContentsUncached(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Wipe the log at <paramref name="path"/>.</summary>
    public static void ClearUncached(string path)
    {
        lock (Lock)
        {
            try { File.Delete(path); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>The real log file: <c>%LOCALAPPDATA%\Coral\diagnostics.log</c>.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Coral", "diagnostics.log");

    /// <summary>Append a timestamped line to the real log file.</summary>
    public static void Record(string message, string level = "ERROR") => RecordUncached(DefaultPath(), message, level);

    /// <summary>The real log file's current contents.</summary>
    public static string Contents() => ContentsUncached(DefaultPath());

    /// <summary>Wipe the real log file.</summary>
    public static void Clear() => ClearUncached(DefaultPath());

    private static string Header() => $"# Coral diagnostics · {Environment.OSVersion}\n";

    private static void TrimIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= MaxBytes) return;
        var text = File.ReadAllText(path);
        var lines = text.Split('\n');
        // Keep the header + the most recent ~60% of lines (matches Swift).
        var keep = lines.Skip(lines.Length - lines.Length * 6 / 10);
        File.WriteAllText(path, Header() + string.Join('\n', keep));
    }
}
