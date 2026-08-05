namespace Coral.Core.Services;

/// <summary>
/// Port of the argument-construction slice of <c>StrategyForge/Services/
/// ClaudeRunner.swift</c>'s <c>stream()</c> — builds the CLI argument list for a
/// headless <c>claude -p …</c> turn. Pure: given the same inputs, always the same
/// args in the same order, so it's fully unit-testable without spawning anything.
/// The actual <c>Process.Start</c> + stdout streaming that consumes this (feeding
/// each line to <see cref="ClaudeStreamParser"/>) isn't ported yet — see
/// windows/PORT-PLAN.md Fase 3; it needs Windows to verify with confidence
/// (real async pipe draining, cancellation, an inactivity watchdog).
/// </summary>
public static class ClaudeRunArgs
{
    /// <summary>The argument list for one turn. <paramref name="resume"/> selects
    /// <c>--resume</c> (continuing the repo's session) vs. <c>--session-id</c> (a
    /// fresh one) so chats on the same repo don't mix. <paramref name="effort"/> is
    /// omitted by loops-vs-chat callers that steer effort via a prompt directive
    /// instead of the flag.</summary>
    public static List<string> Build(
        string prompt,
        string model,
        string sessionId,
        bool resume,
        string permissionMode,
        IReadOnlyList<string>? extraDirs = null,
        string? effort = null)
    {
        var args = new List<string>
        {
            "--model", model,
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--permission-mode", permissionMode,
        };

        if (effort != null)
        {
            args.Add("--effort");
            args.Add(effort);
        }

        // A per-chat session id keeps chats on the same repo from mixing.
        if (resume)
        {
            args.Add("--resume");
            args.Add(sessionId);
        }
        else
        {
            args.Add("--session-id");
            args.Add(sessionId);
        }

        // Grant read access to the folders of any attached files.
        foreach (var dir in extraDirs ?? Array.Empty<string>())
        {
            args.Add("--add-dir");
            args.Add(dir);
        }

        args.Add("-p");
        args.Add(prompt);
        return args;
    }
}
