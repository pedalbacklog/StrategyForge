using Coral.Core.Models;
using Coral.Core.Services;
using Coral.Core.ViewModels;
using Xunit;

namespace Coral.Tests;

/// <summary>Tests for ConnectViewModel's event-handling/state-mutation logic
/// against fakes — mirrors ChatViewModelTests's style. No real npm process or
/// Windows pseudo-console ever spawns.</summary>
public class ConnectViewModelTests
{
    private static ConnectViewModel MakeViewModel(
        FakeProcessLauncher processLauncher, FakePseudoConsoleLauncher ptyLauncher,
        AIProvider provider = AIProvider.Claude, List<string>? openedUrls = null,
        Func<AIProvider, ProviderAuth.State>? checkFreshness = null) =>
        new(processLauncher, ptyLauncher, provider,
            resolveBinary: name => name == "claude" ? "/bin/claude" : null,
            openUrl: openedUrls is null ? null : openedUrls.Add,
            checkFreshness: checkFreshness ?? (_ => ProviderAuth.State.Unknown));

    [Fact]
    public async Task ConnectAsyncStreamsPhasesAndLogsThenReportsDone()
    {
        var processLauncher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));
        var ptyLauncher = new FakePseudoConsoleLauncher((_, _) =>
            new FakePseudoConsoleSession(new List<string> { "signing in..." }));
        var vm = MakeViewModel(processLauncher, ptyLauncher);

        await vm.ConnectAsync();

        Assert.Equal(ConnectPhase.SigningIn, vm.Phase);
        Assert.Contains("signing in...", vm.LogLines);
        Assert.Equal("Connected.", vm.StatusMessage);
        Assert.False(vm.IsConnecting);
    }

    [Fact]
    public async Task ConnectAsyncOpensTheLoginUrlAndAsksForTheClaudeCode()
    {
        var processLauncher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));
        var ptyLauncher = new FakePseudoConsoleLauncher((_, _) => new FakePseudoConsoleSession(
            new List<string> { "Open https://claude.ai/login?code=abc to continue" }));
        var openedUrls = new List<string>();
        var vm = MakeViewModel(processLauncher, ptyLauncher, openedUrls: openedUrls);

        await vm.ConnectAsync();

        Assert.Equal(new List<string> { "https://claude.ai/login?code=abc" }, openedUrls);
        // NeedsCode fires mid-stream then clears once the flow finishes.
        Assert.False(vm.NeedsCode);
        Assert.Equal("Connected.", vm.StatusMessage);
    }

    [Fact]
    public async Task SubmitCodeAsyncWritesTheCodeIntoTheRunningLoginAndClearsNeedsCode()
    {
        var processLauncher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));
        FakePseudoConsoleSession? started = null;
        var ptyLauncher = new FakePseudoConsoleLauncher((_, _) =>
        {
            // Hangs after its one line so the connect flow is still "in
            // flight" (NeedsCode still true) when we submit the code.
            started = new FakePseudoConsoleSession(
                new List<string> { "Open https://claude.ai/login?code=abc to continue" }, hangAfterLines: true);
            return started;
        });
        var vm = MakeViewModel(processLauncher, ptyLauncher);

        var connect = vm.ConnectAsync();
        while (!vm.NeedsCode) await Task.Delay(5);

        vm.CodeInput = " ABC123 ";
        await vm.SubmitCodeAsync();

        Assert.Equal(new List<string> { "ABC123" }, started!.WrittenLines);
        Assert.False(vm.NeedsCode);
        Assert.Equal("", vm.CodeInput);

        vm.CancelConnect();
        await connect;
    }

    [Fact]
    public async Task ConnectAsyncReportsFailure()
    {
        var processLauncher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));
        var ptyLauncher = new FakePseudoConsoleLauncher((_, _) =>
            new FakePseudoConsoleSession(new List<string>(), exitCode: 1));
        var vm = MakeViewModel(processLauncher, ptyLauncher);

        await vm.ConnectAsync();

        Assert.Equal("Error: Sign-in exited with code 1", vm.StatusMessage);
    }

    [Fact]
    public async Task ConnectAsyncIsANoOpWhileAlreadyConnecting()
    {
        var processLauncher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));
        var ptyLauncher = new FakePseudoConsoleLauncher((_, _) =>
            new FakePseudoConsoleSession(new List<string>(), hangAfterLines: true));
        var vm = MakeViewModel(processLauncher, ptyLauncher);

        var first = vm.ConnectAsync();
        while (!vm.IsConnecting) await Task.Delay(5);
        await vm.ConnectAsync(); // no-op — must not throw or start a second flow

        vm.CancelConnect();
        await first;
    }

    [Fact]
    public async Task CancelConnectStopsTheFlowAndReportsCancelled()
    {
        var processLauncher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));
        var ptyLauncher = new FakePseudoConsoleLauncher((_, _) =>
            new FakePseudoConsoleSession(new List<string>(), hangAfterLines: true));
        var vm = MakeViewModel(processLauncher, ptyLauncher);

        var connect = vm.ConnectAsync();
        while (!vm.IsConnecting) await Task.Delay(5);
        vm.CancelConnect();
        await connect;

        Assert.Equal("Cancelled.", vm.StatusMessage);
        Assert.False(vm.IsConnecting);
    }

    [Fact]
    public async Task ConnectAsyncSkipsSignInEntirelyWhenAlreadyFresh()
    {
        var processCalled = false;
        var ptyCalled = false;
        var processLauncher = new FakeProcessLauncher((_, _) => { processCalled = true; return new FakeChildProcess(new List<string>()); });
        var ptyLauncher = new FakePseudoConsoleLauncher((_, _) => { ptyCalled = true; return new FakePseudoConsoleSession(new List<string>()); });
        var vm = MakeViewModel(processLauncher, ptyLauncher, checkFreshness: _ => ProviderAuth.State.Ok);

        await vm.ConnectAsync();

        Assert.False(processCalled);
        Assert.False(ptyCalled);
        Assert.Equal("Already connected.", vm.StatusMessage);
        Assert.False(vm.IsConnecting);
    }

    [Fact]
    public async Task ConnectAsyncOnlyOpensTheLoginUrlOnceEvenIfTheCliPrintsItTwice()
    {
        // Real CLIs commonly print the URL once when opening the browser,
        // then again as a "if it didn't open, visit: <url>" fallback line —
        // confirmed on real Windows to open a second browser window before
        // this fix.
        var processLauncher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));
        var ptyLauncher = new FakePseudoConsoleLauncher((_, _) => new FakePseudoConsoleSession(new List<string>
        {
            "Opening browser to https://claude.ai/login?code=abc",
            "If the browser didn't open, visit: https://claude.ai/login?code=abc",
        }));
        var openedUrls = new List<string>();
        var vm = MakeViewModel(processLauncher, ptyLauncher, openedUrls: openedUrls);

        await vm.ConnectAsync();

        Assert.Equal(new List<string> { "https://claude.ai/login?code=abc" }, openedUrls);
    }

    [Fact]
    public void PhaseLabelReflectsCurrentPhase()
    {
        var processLauncher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>()));
        var ptyLauncher = new FakePseudoConsoleLauncher((_, _) => new FakePseudoConsoleSession(new List<string>()));
        var vm = MakeViewModel(processLauncher, ptyLauncher);

        Assert.Equal("", vm.PhaseLabel); // no connect attempt yet
    }
}
