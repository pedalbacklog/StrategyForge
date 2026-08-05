using Coral.Core.Services;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of the ClaudeStreamParserTests portion of
/// StrategyForgeTests/ChatTests.swift. (ChatTitleTests and the CodeGit diff-parser
/// test aren't ported — AppModel/CodeGit aren't in Coral.Core.)</summary>
public class ClaudeStreamParserTests
{
    [Fact]
    public void ParsesAssistantText()
    {
        const string line = """{"type":"assistant","message":{"content":[{"type":"text","text":"Hello there"}]}}""";
        var e = Assert.Single(ClaudeStreamParser.Events(line));
        var text = Assert.IsType<ChatEvent.AssistantText>(e);
        Assert.Equal("Hello there", text.Text);
    }

    [Fact]
    public void ParsesToolUse()
    {
        const string line = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"/a/b/App.swift"}}]}}""";
        var e = Assert.Single(ClaudeStreamParser.Events(line));
        var tool = Assert.IsType<ChatEvent.Tool>(e);
        Assert.Equal("Read", tool.Name);
        Assert.Equal("App.swift", tool.Detail);
    }

    [Fact]
    public void MixedTextAndToolInOneMessage()
    {
        const string line = """{"type":"assistant","message":{"content":[{"type":"text","text":"Editing"},{"type":"tool_use","name":"Bash","input":{"command":"npm test"}}]}}""";
        var events = ClaudeStreamParser.Events(line);
        Assert.Equal(2, events.Count);
        Assert.Equal("Editing", Assert.IsType<ChatEvent.AssistantText>(events[0]).Text);
        var tool = Assert.IsType<ChatEvent.Tool>(events[1]);
        Assert.Equal("Bash", tool.Name);
        Assert.Equal("npm test", tool.Detail);
    }

    [Fact]
    public void BashToolUseEmitsCommandStarted()
    {
        const string line = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"tu_1","name":"Bash","input":{"command":"npm test"}}]}}""";
        var events = ClaudeStreamParser.Events(line);
        Assert.Contains(events, e => e is ChatEvent.Tool t && t.Name == "Bash" && t.Detail == "npm test");
        Assert.Contains(events, e => e is ChatEvent.CommandStarted c && c.Id == "tu_1" && c.Command == "npm test");
    }

    [Fact]
    public void ToolResultEmitsCommandOutput()
    {
        const string line = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tu_1","content":[{"type":"text","text":"3 passed"}]}]}}""";
        var e = Assert.Single(ClaudeStreamParser.Events(line));
        var output = Assert.IsType<ChatEvent.CommandOutput>(e);
        Assert.Equal("tu_1", output.Id);
        Assert.Equal("3 passed", output.Output);
    }

    [Fact]
    public void ParsesTodos()
    {
        const string line = """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"TodoWrite","input":{"todos":[{"content":"Audit HUD","status":"in_progress"}]}}]}}""";
        var e = Assert.Single(ClaudeStreamParser.Events(line));
        var todos = Assert.IsType<ChatEvent.Todos>(e);
        Assert.Equal(new List<AgentTodo> { new("Audit HUD", "in_progress") }, todos.Items);
    }

    [Fact]
    public void UsageSumsAllInputTokenKinds()
    {
        const string line = """{"type":"result","subtype":"success","usage":{"input_tokens":10,"output_tokens":5,"cache_creation_input_tokens":3,"cache_read_input_tokens":2},"total_cost_usd":0.01}""";
        var events = ClaudeStreamParser.Events(line);
        Assert.Contains(events, e => e is ChatEvent.Usage u && u.Tokens == 20 && Math.Abs(u.CostUsd - 0.01) < 0.0001);
        Assert.Contains(events, e => e is ChatEvent.Finished);
    }

    [Fact]
    public void ResultSuccessFinishes()
    {
        const string line = """{"type":"result","subtype":"success","result":"done"}""";
        var e = Assert.Single(ClaudeStreamParser.Events(line));
        Assert.IsType<ChatEvent.Finished>(e);
    }

    [Fact]
    public void ResultErrorFails()
    {
        const string line = """{"type":"result","subtype":"error_max_turns"}""";
        var e = Assert.Single(ClaudeStreamParser.Events(line));
        var failed = Assert.IsType<ChatEvent.Failed>(e);
        Assert.Equal("error_max_turns", failed.Message);
    }

    [Fact]
    public void IgnoresSystemAndGarbageLines()
    {
        Assert.Empty(ClaudeStreamParser.Events("""{"type":"system","subtype":"init"}"""));
        Assert.Empty(ClaudeStreamParser.Events("not json"));
        Assert.Empty(ClaudeStreamParser.Events(""));
    }
}
