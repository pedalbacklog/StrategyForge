using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Coral.Core.Models;

namespace Coral.Core.Services;

/// <summary>One event from a plain install or sign-in stream. Port of
/// <c>ProviderInstaller.swift</c>'s <c>InstallEvent</c>.</summary>
public abstract record InstallEvent
{
    private InstallEvent() { }

    /// <summary>A line of installer/login output.</summary>
    public sealed record Log(string Line) : InstallEvent;
    /// <summary>Install or sign-in completed successfully.</summary>
    public sealed record Finished : InstallEvent;
    /// <summary>npm/Node isn't installed — can't proceed automatically.</summary>
    public sealed record NeedsNode : InstallEvent;
    /// <summary>The login is waiting for the browser auth code (Claude).</summary>
    public sealed record NeedsCode : InstallEvent;
    /// <summary>Install or sign-in failed with this message.</summary>
    public sealed record Failed(string Message) : InstallEvent;
}

/// <summary>Which half of <see cref="ProviderInstaller.Connect"/> is running.</summary>
public enum ConnectPhase { Installing, SigningIn }

/// <summary>One event from the unified connect flow (install if needed → sign
/// in). Port of <c>ProviderInstaller.swift</c>'s <c>ConnectEvent</c>.</summary>
public abstract record ConnectEvent
{
    private ConnectEvent() { }

    public sealed record Phase(ConnectPhase Step) : ConnectEvent;
    public sealed record Log(string Line) : ConnectEvent;
    /// <summary>The sign-in URL to open in a browser — Coral.Core stays UI-free,
    /// so unlike the Swift original (which opens it itself via NSWorkspace),
    /// opening it is the caller's job; this event is what a ViewModel wires up
    /// to the platform's browser-launch API.</summary>
    public sealed record Url(string Value) : ConnectEvent;
    /// <summary>Paste the browser's auth code to finish (Claude).</summary>
    public sealed record NeedsCode : ConnectEvent;
    public sealed record NeedsNode : ConnectEvent;
    /// <summary>This CLI's login must be finished in a visible terminal. Never
    /// emitted today — see <see cref="AIProviderExtensions.LoginNeedsTerminal"/>.</summary>
    public sealed record NeedsTerminal : ConnectEvent;
    public sealed record Failed(string Message) : ConnectEvent;
    public sealed record Done : ConnectEvent;
}

/// <summary>Writes the browser auth code back into a running login's hidden
/// pseudo-console. Claude's <c>auth login</c> ends by asking you to paste a
/// code shown in the browser — the UI collects it and this sends it to the
/// CLI's stdin so sign-in completes. Port of <c>ProviderInstaller.swift</c>'s
/// <c>LoginInput</c>, minus the raw <c>FileHandle</c> plumbing —
/// <see cref="IPseudoConsoleSession.WriteLineAsync"/> already does that.</summary>
public sealed class LoginInput
{
    private readonly object _lock = new();
    private IPseudoConsoleSession? _session;

    public void Attach(IPseudoConsoleSession session)
    {
        lock (_lock) { _session = session; }
    }

    public async Task SubmitAsync(string code, CancellationToken ct = default)
    {
        IPseudoConsoleSession? session;
        lock (_lock) { session = _session; }
        if (session is null) return;
        await session.WriteLineAsync(code.Trim(), ct);
    }
}

/// <summary>
/// Port of <c>StrategyForge/Services/ProviderInstaller.swift</c>: one-tap CLI
/// install (npm) and headless browser sign-in, streamed as events. SCOPE for
/// this pass, decided the same way Fase 4 was scoped down (see
/// windows/PORT-PLAN.md §6):
///
/// PORTED — install via npm, sign-in via the existing <see cref="IPseudoConsoleLauncher"/>
/// (the Windows counterpart of Swift's <c>openpty</c>-based hidden PTY), the
/// unified connect flow, and the pure <see cref="FirstUrl"/> URL extractor.
///
/// DEFERRED, deliberately, each for a reason that's a platform redesign, not a
/// missing translation:
/// - <c>installNode()</c> (Homebrew-based Node bootstrap): no single trusted
///   Windows equivalent verified in this port (winget's elevation semantics
///   and package availability need a real Windows machine to verify) — every
///   platform falls back to <see cref="NodeDownloadUrl"/>, which is exactly
///   the terminal state Swift itself uses when Homebrew isn't present.
/// - <c>launchSignIn</c> (AppleScript + Terminal.app fallback): unreachable
///   dead code even in Swift today — <c>loginNeedsTerminal</c> is
///   unconditionally false (see AIProvider.swift's own comment: "Coral now
///   drives ALL logins in a HIDDEN pseudo-terminal"). Not ported; the
///   <c>ConnectEvent.NeedsTerminal</c> branch is kept only for structural
///   parity in case a future provider needs it again.
/// - Opening the sign-in URL in a browser: Swift's <c>signIn</c> calls
///   <c>NSWorkspace.shared.open</c> directly. Coral.Core has no WinUI
///   dependency (kept portable/testable, same reasoning as ChatViewModel not
///   touching navigation) — so this port surfaces the URL as a
///   <see cref="ConnectEvent.Url"/> event instead; a future ViewModel/View
///   pass wires that to the platform's browser-launch API.
/// </summary>
public static class ProviderInstaller
{
    /// <summary>Where to send the user when Node/npm isn't installed. See the
    /// deferred-<c>installNode</c> note on this class.</summary>
    public static readonly Uri NodeDownloadUrl = new("https://nodejs.org/en/download");

    private static readonly Regex UrlPattern = new(@"https?://[^\s""'<>]+", RegexOptions.Compiled);

    /// <summary>The first http(s) URL in a line of CLI output (the OAuth
    /// link). Strips ANSI colour codes first (a trailing escape can otherwise
    /// get swallowed into the URL) and trims trailing punctuation the CLI
    /// sometimes appends after the link. Pure — port of
    /// <c>ProviderInstaller.swift</c>'s <c>firstURL(in:)</c>.</summary>
    public static Uri? FirstUrl(string line)
    {
        var clean = CLIOneShotRunner.StripAnsi(line);
        var match = UrlPattern.Match(clean);
        if (!match.Success) return null;
        var s = match.Value;
        while (s.Length > 0 && ".,;:)]}>".Contains(s[^1])) s = s[..^1];
        return Uri.TryCreate(s, UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>Install a provider's CLI globally via npm, streaming progress.
    /// Unlike the Swift original there's no need to redirect into a
    /// user-writable <c>--prefix</c>: npm's default global prefix on Windows
    /// (<c>%APPDATA%\npm</c>) is already user-writable, so a bare
    /// <c>npm install -g</c> never hits the EACCES that macOS's
    /// <c>/usr/local/lib</c> default would (see BinaryResolver.cs's own note
    /// on this).</summary>
    public static async IAsyncEnumerable<InstallEvent> Install(
        IProcessLauncher launcher, AIProvider provider, Func<string, string?>? resolveBinary = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        resolveBinary ??= BinaryResolver.Resolve;
        var npm = resolveBinary("npm");
        if (npm is null)
        {
            yield return new InstallEvent.NeedsNode();
            yield break;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var binDir = Path.GetDirectoryName(npm);
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        var env = new Dictionary<string, string?>
        {
            ["PATH"] = string.IsNullOrEmpty(binDir) ? currentPath : $"{binDir}{Path.PathSeparator}{currentPath}",
        };

        IChildProcess? child = null;
        string? launchFailure = null;
        try
        {
            child = launcher.Start(npm, new[] { "install", "-g", provider.NpmInstallSpec() }, home, env);
        }
        catch (Exception ex)
        {
            launchFailure = ex.Message;
        }
        if (launchFailure is not null)
        {
            yield return new InstallEvent.Failed(launchFailure);
            yield break;
        }

        var live = child!;
        using (live)
        {
            await foreach (var line in live.ReadStandardOutputLinesAsync(ct))
            {
                yield return new InstallEvent.Log(line);
            }
            var exitCode = await live.WaitForExitAsync(ct);
            if (exitCode == 0)
            {
                yield return new InstallEvent.Finished();
            }
            else
            {
                var err = (await live.ReadStandardErrorToEndAsync()).Trim();
                yield return new InstallEvent.Failed(err.Length == 0 ? $"npm exited with code {exitCode}" : err);
            }
        }
    }

    /// <summary>How long a hidden sign-in is allowed to run before it's killed
    /// as hung. See the inline note at its one use in <see cref="RunSignInAsync"/>
    /// for why this is longer than the Swift original's 150s. Gemini's own
    /// creds-mtime watcher (<see cref="WatchGeminiCredsAsync"/>) uses a
    /// slightly shorter deadline so it always gives up before this outer
    /// timeout fires, not after.</summary>
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromSeconds(300);

    /// <summary>Web sign-in without a visible terminal: run the CLI's login
    /// command attached to a hidden pseudo-console, stream its output, and
    /// finish when the process exits (or, for Gemini, when its creds file
    /// updates — see below). Port of <c>ProviderInstaller.swift</c>'s
    /// <c>signIn</c>. Uses a <see cref="Channel{T}"/> rather than a plain
    /// iterator because — like the Swift original's <c>AsyncStream</c>
    /// continuation — TWO concurrent producers can complete the stream: the
    /// normal output-then-exit-code path, and (Gemini only) a background
    /// watcher racing to detect success via the creds file's mtime. Only the
    /// first of the two writes a terminal event (a small, deliberate
    /// improvement on the Swift original, which can emit a stray second event
    /// from this same race — see the inline note below).</summary>
    public static IAsyncEnumerable<InstallEvent> SignIn(
        IPseudoConsoleLauncher launcher, AIProvider provider, LoginInput? input = null,
        Func<string, string?>? resolveBinary = null, Func<DateTime?>? geminiCredsMtimeOverride = null,
        CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<InstallEvent>();
        _ = RunSignInAsync(channel.Writer, launcher, provider, input, resolveBinary ?? BinaryResolver.Resolve,
            geminiCredsMtimeOverride, ct);
        return channel.Reader.ReadAllAsync(ct);
    }

    private static async Task RunSignInAsync(
        ChannelWriter<InstallEvent> writer, IPseudoConsoleLauncher launcher, AIProvider provider,
        LoginInput? input, Func<string, string?> resolveBinary, Func<DateTime?>? geminiCredsMtimeOverride,
        CancellationToken ct)
    {
        var terminalWritten = 0;
        async Task WriteTerminalAsync(InstallEvent evt)
        {
            if (Interlocked.CompareExchange(ref terminalWritten, 1, 0) == 0) await writer.WriteAsync(evt, ct);
        }

        try
        {
            var parts = provider.LoginCommand().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var bin = resolveBinary(parts[0]);
            if (bin is null)
            {
                await WriteTerminalAsync(new InstallEvent.Failed(
                    $"Couldn't find the {provider.BinaryName()} CLI — install it first."));
                return;
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var binDir = Path.GetDirectoryName(bin);
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            var env = new Dictionary<string, string?>
            {
                ["PATH"] = string.IsNullOrEmpty(binDir) ? currentPath : $"{binDir}{Path.PathSeparator}{currentPath}",
            };
            // Login must write creds to the same config dir the app runs from.
            if (provider == AIProvider.Claude)
            {
                env["ANTHROPIC_API_KEY"] = null;
                env["ANTHROPIC_AUTH_TOKEN"] = null;
            }

            IPseudoConsoleSession session;
            try
            {
                session = launcher.Start(bin, parts.Skip(1).ToArray(), home, env);
            }
            catch (Exception ex)
            {
                await WriteTerminalAsync(new InstallEvent.Failed(ex.Message));
                return;
            }

            using (session)
            {
                input?.Attach(session);

                // Never hang forever on a login (esp. an interactive TUI that won't exit).
                // 150s (matching the Swift original) proved too short for a REAL human
                // login on real Windows — Codex's ChatGPT sign-in (email, password,
                // maybe 2FA) routinely takes longer, and the multi-screen Google flow
                // Gemini goes through (account chooser, native-app warning, consent,
                // plus a one-time Windows Firewall prompt for the local OAuth callback
                // server) isn't much faster. Bumped here only (not in the Swift
                // original — same "Windows-only fix, shared limitation" pattern as
                // Strategy.AutoFixed()'s dedup fix, see PORT-PLAN.md).
                using var timeoutCts = new CancellationTokenSource(SignInTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                // Gemini has no login command: it's a first-run TUI that DOESN'T exit
                // on success. Nudge past its theme/auth menus and detect success by
                // the creds file updating, racing against the normal read loop below.
                var geminiWatcher = provider == AIProvider.Gemini
                    ? WatchGeminiCredsAsync(session, geminiCredsMtimeOverride, WriteTerminalAsync, linkedCts.Token)
                    : Task.CompletedTask;

                var openedUrl = false;
                try
                {
                    await foreach (var line in session.ReadOutputLinesAsync(linkedCts.Token))
                    {
                        await writer.WriteAsync(new InstallEvent.Log(line), ct);

                        // Google retired the free Gemini CLI: the login can never
                        // complete, so fail fast with the real remedy instead of
                        // letting the creds-mtime watcher time out.
                        if (provider == AIProvider.Gemini && CLIOneShotRunner.IsAntigravityMigration(line))
                        {
                            session.Kill();
                            await WriteTerminalAsync(new InstallEvent.Failed(
                                "Google retired the free Gemini CLI for individuals. Install Antigravity (the agy CLI) from antigravity.google and sign in there — Coral uses it automatically."));
                            break;
                        }

                        if (!openedUrl && FirstUrl(line) is not null)
                        {
                            openedUrl = true;
                            // Claude's login finishes by pasting a code from the browser.
                            if (provider == AIProvider.Claude) await writer.WriteAsync(new InstallEvent.NeedsCode(), ct);
                        }
                    }
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    session.Kill();
                    await WriteTerminalAsync(new InstallEvent.Failed(
                        $"Sign-in timed out after {(int)SignInTimeout.TotalSeconds} seconds."));
                }

                if (Volatile.Read(ref terminalWritten) == 0)
                {
                    var exitCode = await session.WaitForExitAsync(CancellationToken.None);
                    await WriteTerminalAsync(exitCode == 0
                        ? new InstallEvent.Finished()
                        : new InstallEvent.Failed($"Sign-in exited with code {exitCode}"));
                }

                linkedCts.Cancel();
                try { await geminiWatcher; } catch { /* best-effort */ }
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    /// <summary>Poll the Gemini creds file for a modification-time change,
    /// nudging past the CLI's first-run TUI menus (sending Enter) so the
    /// highlighted "Login with Google" option gets accepted. Runs concurrently
    /// with the normal output read loop in <see cref="RunSignInAsync"/> — the
    /// first of the two to reach <paramref name="writeTerminal"/> wins.</summary>
    private static async Task WatchGeminiCredsAsync(
        IPseudoConsoleSession session, Func<DateTime?>? credsMtimeOverride,
        Func<InstallEvent, Task> writeTerminal, CancellationToken ct)
    {
        DateTime? CredsMtime()
        {
            if (credsMtimeOverride is not null) return credsMtimeOverride();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(home, ".gemini", "oauth_creds.json");
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }

        var before = CredsMtime();

        // Fire three independent nudges at their own offsets (not sequential
        // waits) — matches the Swift original's three asyncAfter timers.
        foreach (var delaySeconds in new[] { 1.2, 2.8, 4.4 })
        {
            var d = delaySeconds;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(d), ct);
                    await session.WriteLineAsync("", ct); // sends a bare Enter
                }
                catch { /* best-effort nudge */ }
            }, ct);
        }

        // 10s short of SignInTimeout — matches the Swift original's 140-vs-150
        // relationship, so this watcher always gives up before the outer kill
        // fires (never after, which would leave no one to report the outcome).
        var deadline = DateTime.UtcNow + (SignInTimeout - TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1.5), ct); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;

            var now = CredsMtime();
            if (now is not null && now != before)
            {
                session.Kill();
                await writeTerminal(new InstallEvent.Finished());
                return;
            }
        }
    }

    /// <summary>The whole "connect" journey as one stream: install the CLI if
    /// it's missing, then run the web sign-in. Port of
    /// <c>ProviderInstaller.swift</c>'s <c>connect(_:input:)</c>.</summary>
    public static async IAsyncEnumerable<ConnectEvent> Connect(
        IProcessLauncher processLauncher, IPseudoConsoleLauncher ptyLauncher, AIProvider provider,
        LoginInput? input = null, Func<string, string?>? resolveBinary = null,
        Func<DateTime?>? geminiCredsMtimeOverride = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        resolveBinary ??= BinaryResolver.Resolve;

        // 1. Install only if the CLI can't be found.
        if (resolveBinary(provider.BinaryName()) is null)
        {
            yield return new ConnectEvent.Phase(ConnectPhase.Installing);
            var installOk = false;
            await foreach (var ev in Install(processLauncher, provider, resolveBinary, ct))
            {
                switch (ev)
                {
                    case InstallEvent.Log log: yield return new ConnectEvent.Log(log.Line); break;
                    case InstallEvent.NeedsNode: yield return new ConnectEvent.NeedsNode(); yield break;
                    case InstallEvent.Failed failed: yield return new ConnectEvent.Failed(failed.Message); yield break;
                    case InstallEvent.Finished: installOk = true; break;
                    // InstallEvent.NeedsCode is never emitted during install.
                }
            }
            if (!installOk)
            {
                yield return new ConnectEvent.Failed("Install did not finish");
                yield break;
            }
        }

        // 2. Sign-in. loginNeedsTerminal is unconditionally false today (see
        // AIProvider.LoginNeedsTerminal) — this branch is unreachable and kept
        // only for structural parity with the Swift original.
        yield return new ConnectEvent.Phase(ConnectPhase.SigningIn);
        if (provider.LoginNeedsTerminal())
        {
            yield return new ConnectEvent.NeedsTerminal();
            yield break;
        }

        // Guards ConnectEvent.Url below to fire at most once per attempt —
        // without it, a login CLI that prints the URL on more than one line
        // (e.g. "Opening browser to <url>" followed by a "if it didn't open,
        // visit: <url>" fallback line, both real on real Windows) opened a
        // SECOND browser window for the same login. Mirrors the single-open
        // invariant ProviderInstaller.swift keeps with its own `openedURL`
        // flag (there it gates the actual NSWorkspace.open call directly,
        // since Swift doesn't split "detect the URL" from "open it" across
        // two layers the way SignIn/Connect do here).
        var urlOpened = false;
        await foreach (var ev in SignIn(ptyLauncher, provider, input, resolveBinary, geminiCredsMtimeOverride, ct))
        {
            switch (ev)
            {
                case InstallEvent.Log log:
                    yield return new ConnectEvent.Log(log.Line);
                    if (!urlOpened && FirstUrl(log.Line) is { } url)
                    {
                        urlOpened = true;
                        yield return new ConnectEvent.Url(url.AbsoluteUri);
                    }
                    break;
                case InstallEvent.NeedsCode: yield return new ConnectEvent.NeedsCode(); break;
                case InstallEvent.NeedsNode: yield return new ConnectEvent.NeedsNode(); yield break;
                case InstallEvent.Failed failed: yield return new ConnectEvent.Failed(failed.Message); yield break;
                case InstallEvent.Finished: yield return new ConnectEvent.Done(); yield break;
            }
        }
    }
}
