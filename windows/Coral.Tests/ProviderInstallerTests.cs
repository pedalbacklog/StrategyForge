using Coral.Core.Models;
using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Tests for ProviderInstaller's pure URL extraction and its
/// install/sign-in/connect ORCHESTRATION against fakes — mirrors
/// ChatViewModelTests/ClaudeRunnerTests's style. No real npm or Windows
/// pseudo-console ever spawns.</summary>
public class ProviderInstallerTests
{
    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source) items.Add(item);
        return items;
    }

    [Theory]
    [InlineData("Open this link: https://example.com/login?code=abc to continue.", "https://example.com/login?code=abc")]
    [InlineData("Visit https://example.com/tos-privacy.", "https://example.com/tos-privacy")]
    [InlineData("no link here", null)]
    public void FirstUrlFindsAndTrimsTrailingPunctuation(string line, string? expected)
    {
        var url = ProviderInstaller.FirstUrl(line);
        Assert.Equal(expected, url?.ToString());
    }

    [Fact]
    public void FirstUrlStripsAnsiEscapeCodesSoTheyDontLeakIntoTheUrl()
    {
        // A trailing ANSI color-reset code right after the link must not be
        // swallowed into the URL (the "reconnect opened an error page" bug).
        var line = "[36mhttps://example.com/tos-privacy[39m for details";
        var url = ProviderInstaller.FirstUrl(line);
        Assert.Equal("https://example.com/tos-privacy", url?.ToString());
    }

    [Fact]
    public async Task InstallStreamsLinesAndFinishesOnSuccess()
    {
        var launcher = new FakeProcessLauncher((_, _) =>
            new FakeChildProcess(new List<string> { "added 1 package" }));

        var events = await CollectAsync(ProviderInstaller.Install(
            launcher, AIProvider.Claude, resolveBinary: name => name == "npm" ? "/usr/bin/npm" : null));

        Assert.Equal(new InstallEvent.Log("added 1 package"), events[0]);
        Assert.IsType<InstallEvent.Finished>(events[1]);
        Assert.Equal("npm", System.IO.Path.GetFileName(launcher.LastStart!.Value.FileName));
        Assert.Contains("@anthropic-ai/claude-code", launcher.LastStart.Value.Args);
    }

    [Fact]
    public async Task InstallReportsNeedsNodeWhenNpmIsMissing()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));

        var events = await CollectAsync(ProviderInstaller.Install(
            launcher, AIProvider.Claude, resolveBinary: _ => null));

        Assert.Equal(new List<InstallEvent> { new InstallEvent.NeedsNode() }, events);
        Assert.Null(launcher.LastStart);
    }

    [Fact]
    public async Task InstallReportsFailureFromNonZeroExit()
    {
        var launcher = new FakeProcessLauncher((_, _) =>
            new FakeChildProcess(new List<string>(), exitCode: 1, stderr: "EACCES"));

        var events = await CollectAsync(ProviderInstaller.Install(
            launcher, AIProvider.Claude, resolveBinary: name => name == "npm" ? "/usr/bin/npm" : null));

        Assert.Equal(new InstallEvent.Failed("EACCES"), Assert.Single(events));
    }

    [Fact]
    public async Task SignInDetectsTheLoginUrlAndFinishesOnCleanExitForClaude()
    {
        var launcher = new FakePseudoConsoleLauncher((_, _) => new FakePseudoConsoleSession(
            new List<string> { "Open https://claude.ai/login?code=abc123 to continue" }));

        var events = await CollectAsync(ProviderInstaller.SignIn(
            launcher, AIProvider.Claude, resolveBinary: name => name == "claude" ? "/bin/claude" : null));

        Assert.Contains(events, e => e is InstallEvent.Log log && log.Line.Contains("claude.ai/login"));
        Assert.Contains(events, e => e is InstallEvent.NeedsCode); // Claude finishes by pasting a code
        Assert.IsType<InstallEvent.Finished>(events[^1]);
    }

    [Fact]
    public async Task SignInReportsFailureFromNonZeroExitCode()
    {
        var launcher = new FakePseudoConsoleLauncher((_, _) =>
            new FakePseudoConsoleSession(new List<string>(), exitCode: 1));

        var events = await CollectAsync(ProviderInstaller.SignIn(
            launcher, AIProvider.Openai, resolveBinary: name => name == "codex" ? "/bin/codex" : null));

        Assert.Equal(new InstallEvent.Failed("Sign-in exited with code 1"), Assert.Single(events));
    }

    [Fact]
    public async Task SignInFailsFastWhenTheCliCantBeResolved()
    {
        var launcher = new FakePseudoConsoleLauncher((_, _) => new FakePseudoConsoleSession(new List<string>()));

        var events = await CollectAsync(ProviderInstaller.SignIn(launcher, AIProvider.Gemini, resolveBinary: _ => null));

        var failed = Assert.IsType<InstallEvent.Failed>(Assert.Single(events));
        Assert.Contains("gemini", failed.Message);
        Assert.Null(launcher.LastStart);
    }

    [Fact]
    public async Task SignInDetectsGooglesAntigravityMigrationAndKillsTheSession()
    {
        FakePseudoConsoleSession? started = null;
        var launcher = new FakePseudoConsoleLauncher((_, _) =>
        {
            started = new FakePseudoConsoleSession(new List<string>
            {
                "Error: IneligibleTier — this account can no longer use the free Gemini CLI",
            }, hangAfterLines: true);
            return started;
        });

        var events = await CollectAsync(ProviderInstaller.SignIn(
            launcher, AIProvider.Gemini, resolveBinary: name => name == "gemini" ? "/bin/gemini" : null,
            geminiCredsMtimeOverride: () => null)); // never "succeeds" via mtime — only the migration path should fire

        var failed = Assert.IsType<InstallEvent.Failed>(events[^1]);
        Assert.Contains("Antigravity", failed.Message);
        Assert.True(started!.Killed);
    }

    [Fact]
    public async Task SignInDetectsGeminiSuccessViaCredsFileMtimeChange()
    {
        // Gemini's TUI never exits on success — SignIn must notice the creds
        // file's mtime changing and finish anyway.
        var callCount = 0;
        DateTime? MtimeOverride()
        {
            callCount++;
            return callCount <= 1 ? null : DateTime.UtcNow; // "before" is null, then it changes
        }

        FakePseudoConsoleSession? started = null;
        var launcher = new FakePseudoConsoleLauncher((_, _) =>
        {
            started = new FakePseudoConsoleSession(new List<string> { "Gemini CLI first run..." }, hangAfterLines: true);
            return started;
        });

        var events = await CollectAsync(ProviderInstaller.SignIn(
            launcher, AIProvider.Gemini, resolveBinary: name => name == "gemini" ? "/bin/gemini" : null,
            geminiCredsMtimeOverride: MtimeOverride));

        Assert.IsType<InstallEvent.Finished>(events[^1]);
        Assert.True(started!.Killed);
        Assert.NotEmpty(started.WrittenLines); // the theme/auth-menu nudges were sent
    }

    [Fact]
    public async Task ConnectSkipsInstallWhenTheCliIsAlreadyPresent()
    {
        var processLauncher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));
        var ptyLauncher = new FakePseudoConsoleLauncher((_, _) => new FakePseudoConsoleSession(new List<string>()));

        var events = await CollectAsync(ProviderInstaller.Connect(
            processLauncher, ptyLauncher, AIProvider.Claude, resolveBinary: _ => "/bin/claude"));

        Assert.DoesNotContain(events, e => e is ConnectEvent.Phase p && p.Step == ConnectPhase.Installing);
        Assert.Contains(events, e => e is ConnectEvent.Phase p && p.Step == ConnectPhase.SigningIn);
        Assert.IsType<ConnectEvent.Done>(events[^1]);
        Assert.Null(processLauncher.LastStart);
    }

    [Fact]
    public async Task ConnectInstallsFirstWhenTheCliIsMissing()
    {
        var processLauncher = new FakeProcessLauncher((_, _) =>
            new FakeChildProcess(new List<string> { "added 1 package" }));
        var ptyLauncher = new FakePseudoConsoleLauncher((_, _) => new FakePseudoConsoleSession(new List<string>()));

        var events = await CollectAsync(ProviderInstaller.Connect(
            processLauncher, ptyLauncher, AIProvider.Claude, resolveBinary: name => name == "npm" ? "/usr/bin/npm" : null));

        Assert.Equal(ConnectPhase.Installing, Assert.IsType<ConnectEvent.Phase>(events[0]).Step);
        Assert.Contains(events, e => e is ConnectEvent.Log log && log.Line == "added 1 package");
        Assert.Contains(events, e => e is ConnectEvent.Phase p && p.Step == ConnectPhase.SigningIn);
        // The fake resolver still can't find "claude" post-install (it only
        // ever resolves "npm"), so sign-in reports the same failure a real
        // "install succeeded but the CLI still isn't on PATH yet" case would.
        Assert.IsType<ConnectEvent.Failed>(events[^1]);
        Assert.NotNull(processLauncher.LastStart);
    }

    [Fact]
    public async Task ConnectSurfacesTheLoginUrlAsItsOwnEvent()
    {
        var processLauncher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));
        var ptyLauncher = new FakePseudoConsoleLauncher((_, _) => new FakePseudoConsoleSession(
            new List<string> { "Open https://claude.ai/login?code=xyz to continue" }));

        var events = await CollectAsync(ProviderInstaller.Connect(
            processLauncher, ptyLauncher, AIProvider.Claude, resolveBinary: name => name == "claude" ? "/bin/claude" : null));

        Assert.Contains(events, e => e is ConnectEvent.Url u && u.Value == "https://claude.ai/login?code=xyz");
        Assert.Contains(events, e => e is ConnectEvent.NeedsCode);
    }
}
