using Coral.Core.Services;
using Coral.Core.ViewModels;
using Xunit;

namespace Coral.Tests;

/// <summary>Tests for ChatViewModel's event-handling/state-mutation logic
/// against a FakeProcessLauncher — mirrors ClaudeRunnerTests's style. No real
/// process ever spawns; ChatViewModel's own orchestration (dedup between
/// AssistantDelta/AssistantText, activity mapping, usage formatting,
/// session-missing retry, cancellation) is what's under test here, not
/// ClaudeRunner itself (already covered by ClaudeRunnerTests).</summary>
public class ChatViewModelTests
{
    private static ChatViewModel MakeViewModel(IProcessLauncher launcher) =>
        new(launcher, "/repo", resolveBinary: _ => "/resolved/claude");

    [Fact]
    public async Task SendAsyncAddsUserMessageThenStreamsAssistantDeltas()
    {
        var lines = new List<string>
        {
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hel"}}}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"lo"}}}""",
            """{"type":"result","subtype":"success","result":"done"}""",
        };
        var vm = MakeViewModel(new FakeProcessLauncher((_, _) => new FakeChildProcess(lines)));

        vm.PromptText = "hi";
        await vm.SendAsync();

        Assert.Equal(2, vm.Messages.Count);
        Assert.Equal(ChatRole.User, vm.Messages[0].Role);
        Assert.Equal("hi", vm.Messages[0].Text);
        Assert.Equal(ChatRole.Assistant, vm.Messages[1].Role);
        Assert.Equal("Hello", vm.Messages[1].Text);
        Assert.False(vm.IsSending);
        Assert.Equal("", vm.PromptText);
    }

    [Fact]
    public async Task AssistantTextIsIgnoredOnceDeltasHaveStreamed()
    {
        // --include-partial-messages means both a delta AND the final full
        // text block arrive for the same content; only the delta should count,
        // matching ChatViewModel.swift's gotDelta guard.
        var lines = new List<string>
        {
            """{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hi"}}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Hi"}]}}""",
            """{"type":"result","subtype":"success","result":"done"}""",
        };
        var vm = MakeViewModel(new FakeProcessLauncher((_, _) => new FakeChildProcess(lines)));

        vm.PromptText = "hi";
        await vm.SendAsync();

        var assistant = Assert.Single(vm.Messages, m => m.Role == ChatRole.Assistant);
        Assert.Equal("Hi", assistant.Text); // not "HiHi"
    }

    [Fact]
    public async Task FallsBackToAssistantTextWhenNoDeltasArrive()
    {
        var lines = new List<string>
        {
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Hi there"}]}}""",
            """{"type":"result","subtype":"success","result":"done"}""",
        };
        var vm = MakeViewModel(new FakeProcessLauncher((_, _) => new FakeChildProcess(lines)));

        vm.PromptText = "hi";
        await vm.SendAsync();

        var assistant = Assert.Single(vm.Messages, m => m.Role == ChatRole.Assistant);
        Assert.Equal("Hi there", assistant.Text);
    }

    [Fact]
    public async Task ToolAndDelegatedEventsPopulateActivity()
    {
        var lines = new List<string>
        {
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"/repo/a.txt"}}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Task","input":{"subagent_type":"reviewer"}}]}}""",
            """{"type":"result","subtype":"success","result":"done"}""",
        };
        var vm = MakeViewModel(new FakeProcessLauncher((_, _) => new FakeChildProcess(lines)));

        vm.PromptText = "hi";
        await vm.SendAsync();

        Assert.Equal(2, vm.Activity.Count);
        Assert.Equal("Read", vm.Activity[0].Title);
        Assert.Equal("a.txt", vm.Activity[0].Detail);
        Assert.Equal("→ reviewer", vm.Activity[1].Title);
    }

    [Fact]
    public async Task StepsAfterADelegationAreAttributedToTheActiveSubagent()
    {
        var lines = new List<string>
        {
            // Orchestrator reads a file first (Agent should be null).
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"/repo/a.txt"}}]}}""",
            // Delegates to "reviewer" — a delegation marker, excluded from step counts.
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Task","input":{"subagent_type":"reviewer"}}]}}""",
            // A step that happens while "reviewer" is active should be attributed to it.
            // (Grep, not Edit/Write/Bash — those also emit a second FileEdited/
            // CommandStarted event from the same tool_use, which isn't what this
            // test is checking.)
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t3","name":"Grep","input":{"pattern":"TODO"}}]}}""",
            """{"type":"result","subtype":"success","result":"done"}""",
        };
        var vm = MakeViewModel(new FakeProcessLauncher((_, _) => new FakeChildProcess(lines)));

        vm.PromptText = "hi";
        await vm.SendAsync();

        Assert.Equal(3, vm.Activity.Count);
        Assert.Null(vm.Activity[0].Agent);
        Assert.False(vm.Activity[0].IsDelegation);
        Assert.True(vm.Activity[1].IsDelegation);
        Assert.Null(vm.Activity[1].Agent); // the delegation itself is an orchestrator action
        Assert.Equal("reviewer", vm.Activity[2].Agent);
        Assert.False(vm.Activity[2].IsDelegation);
    }

    [Fact]
    public async Task UsageEventFormatsStatusMessageWithInvariantCulture()
    {
        var lines = new List<string>
        {
            """{"type":"result","subtype":"success","result":"done","usage":{"input_tokens":100,"output_tokens":200},"total_cost_usd":0.8321}""",
        };
        var vm = MakeViewModel(new FakeProcessLauncher((_, _) => new FakeChildProcess(lines)));

        var original = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("es-ES");
        try
        {
            vm.PromptText = "hi";
            await vm.SendAsync();
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }

        Assert.Equal("300 tokens · $0.8321", vm.StatusMessage);
    }

    [Fact]
    public async Task FailedEventSetsStatusMessage()
    {
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>(), exitCode: 1, stderr: "boom"));
        var vm = MakeViewModel(launcher);

        vm.PromptText = "hi";
        await vm.SendAsync();

        Assert.Equal("Error: boom", vm.StatusMessage);
        Assert.False(vm.IsSending);
    }

    [Fact]
    public async Task BlankPromptIsANoOp()
    {
        var called = false;
        var vm = MakeViewModel(new FakeProcessLauncher((_, _) => { called = true; return new FakeChildProcess(new List<string>()); }));

        vm.PromptText = "   ";
        await vm.SendAsync();

        Assert.False(called);
        Assert.Empty(vm.Messages);
    }

    [Fact]
    public async Task SecondTurnResumesTheSameSession()
    {
        var lines = new List<string> { """{"type":"result","subtype":"success","result":"done"}""" };
        var launcher = new FakeProcessLauncher((_, _) => new FakeChildProcess(lines));
        var vm = MakeViewModel(launcher);

        vm.PromptText = "first";
        await vm.SendAsync();
        Assert.DoesNotContain("--resume", launcher.LastStart!.Value.Args);

        vm.PromptText = "second";
        await vm.SendAsync();
        Assert.Contains("--resume", launcher.LastStart!.Value.Args);
    }

    [Fact]
    public async Task RetriesFreshWhenAResumedSessionIsMissing()
    {
        var callCount = 0;
        var launcher = new FakeProcessLauncher((_, _) =>
        {
            callCount++;
            return callCount switch
            {
                // Turn 1: succeeds, so turn 2 will try --resume.
                1 => new FakeChildProcess(new List<string>
                    { """{"type":"result","subtype":"success","result":"done"}""" }),
                // Turn 2 (resumed): the session is gone.
                2 => new FakeChildProcess(new List<string>(), exitCode: 1, stderr: "No conversation found for session"),
                // Retry as a fresh session: succeeds.
                _ => new FakeChildProcess(new List<string>
                {
                    """{"type":"assistant","message":{"content":[{"type":"text","text":"fresh reply"}]}}""",
                    """{"type":"result","subtype":"success","result":"done"}""",
                }),
            };
        });
        var vm = MakeViewModel(launcher);

        vm.PromptText = "first";
        await vm.SendAsync();

        vm.PromptText = "second";
        await vm.SendAsync();

        Assert.Equal(3, callCount);
        Assert.Null(vm.StatusMessage); // the retry succeeded silently, no error surfaced
        var assistant = Assert.Single(vm.Messages, m => m.Role == ChatRole.Assistant && m.Text == "fresh reply");
        Assert.NotNull(assistant);
    }

    [Fact]
    public async Task CancelCurrentTurnStopsTheStreamAndReportsCancelled()
    {
        var launcher = new FakeProcessLauncher((_, _) =>
            new FakeChildProcess(new List<string>(), hangForever: TimeSpan.FromSeconds(30)));
        var vm = MakeViewModel(launcher);

        vm.PromptText = "hi";
        var send = vm.SendAsync();
        await Task.Delay(50);
        vm.CancelCurrentTurn();
        await send;

        Assert.Equal("Cancelled.", vm.StatusMessage);
        Assert.False(vm.IsSending);
    }

    [Fact]
    public async Task BashCommandOutputIsPairedWithItsCommandInTheCommandLog()
    {
        var lines = new List<string>
        {
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"echo hi"}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"hi\n"}]}}""",
            """{"type":"result","subtype":"success","result":"done"}""",
        };
        var vm = MakeViewModel(new FakeProcessLauncher((_, _) => new FakeChildProcess(lines)));

        vm.PromptText = "hi";
        await vm.SendAsync();

        var run = Assert.Single(vm.CommandLog);
        Assert.Equal("echo hi", run.Command);
        Assert.Equal("hi\n", run.Output);
    }

    [Fact]
    public async Task ToolResultsForUntrackedIdsAreIgnored()
    {
        // A tool_result with no matching CommandStarted (e.g. Read/Grep's own
        // tool_result) shouldn't show up in the terminal panel.
        var lines = new List<string>
        {
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"/repo/a.txt"}}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"file contents"}]}}""",
            """{"type":"result","subtype":"success","result":"done"}""",
        };
        var vm = MakeViewModel(new FakeProcessLauncher((_, _) => new FakeChildProcess(lines)));

        vm.PromptText = "hi";
        await vm.SendAsync();

        Assert.Empty(vm.CommandLog);
    }

    [Fact]
    public async Task CommandLogAccumulatesAcrossTurnsButPendingCommandsResetPerTurn()
    {
        var callCount = 0;
        var launcher = new FakeProcessLauncher((_, _) =>
        {
            callCount++;
            return callCount switch
            {
                // Turn 1: starts a command but never gets its output (e.g. cancelled).
                1 => new FakeChildProcess(new List<string>
                {
                    """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"first"}}]}}""",
                    """{"type":"result","subtype":"success","result":"done"}""",
                }),
                // Turn 2: a DIFFERENT command reuses the same tool_use id "t1" —
                // its output must not be paired with turn 1's stale "first" entry.
                _ => new FakeChildProcess(new List<string>
                {
                    """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"second"}}]}}""",
                    """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"second output"}]}}""",
                    """{"type":"result","subtype":"success","result":"done"}""",
                }),
            };
        });
        var vm = MakeViewModel(launcher);

        vm.PromptText = "first";
        await vm.SendAsync();
        vm.PromptText = "second";
        await vm.SendAsync();

        var run = Assert.Single(vm.CommandLog);
        Assert.Equal("second", run.Command);
        Assert.Equal("second output", run.Output);
    }

    [Fact]
    public async Task DraftCommitMessageUsesTheFirstLineOfTheLastAssistantReply()
    {
        var lines = new List<string>
        {
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Fixed the bug\n\nMore detail here."}]}}""",
            """{"type":"result","subtype":"success","result":"done"}""",
        };
        var vm = MakeViewModel(new FakeProcessLauncher((_, _) => new FakeChildProcess(lines)));

        vm.PromptText = "hi";
        await vm.SendAsync();

        Assert.Equal("Fixed the bug", vm.DraftCommitMessage());
    }

    [Fact]
    public void DraftCommitMessageIsBlankWithNoAssistantReplyYet()
    {
        var vm = MakeViewModel(new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>())));

        Assert.Equal("", vm.DraftCommitMessage());
    }

    [Fact]
    public async Task DraftCommitMessageCapsAtSixtyFourChars()
    {
        var longLine = new string('a', 80);
        var lines = new List<string>
        {
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"" + longLine + "\"}]}}",
            """{"type":"result","subtype":"success","result":"done"}""",
        };
        var vm = MakeViewModel(new FakeProcessLauncher((_, _) => new FakeChildProcess(lines)));

        vm.PromptText = "hi";
        await vm.SendAsync();

        var drafted = vm.DraftCommitMessage();
        Assert.Equal(65, drafted.Length); // 64 chars + the ellipsis
        Assert.EndsWith("…", drafted);
    }

    [Fact]
    public void DraftPrBodyFallsBackToAFixedFooterWithNoAssistantReplyYet()
    {
        var vm = MakeViewModel(new FakeProcessLauncher((_, _) => new FakeChildProcess(new List<string>())));

        Assert.Equal("Opened from Coral.", vm.DraftPrBody());
    }

    [Fact]
    public async Task DraftPrBodyAppendsAFooterToTheLastAssistantReply()
    {
        var lines = new List<string>
        {
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Did the thing."}]}}""",
            """{"type":"result","subtype":"success","result":"done"}""",
        };
        var vm = MakeViewModel(new FakeProcessLauncher((_, _) => new FakeChildProcess(lines)));

        vm.PromptText = "hi";
        await vm.SendAsync();

        Assert.Equal("Did the thing.\n\n— Opened from Coral.", vm.DraftPrBody());
    }

    [Fact]
    public void TrimmedPassesShortStringsThrough()
    {
        Assert.Equal("short", ChatViewModel.Trimmed("short", limit: 100));
    }

    [Fact]
    public void TrimmedElidesTheMiddleOfLongStrings()
    {
        var s = new string('a', 12_000) + new string('b', 12_000);
        var result = ChatViewModel.Trimmed(s, limit: 12_000);

        Assert.StartsWith(new string('a', 100), result);
        Assert.EndsWith(new string('b', 100), result);
        Assert.Contains("chars elided", result);
        Assert.True(result.Length < s.Length);
    }
}
