using System.Text.Json;
using System.Text.Json.Nodes;

namespace Coral.Core.Services;

/// <summary>A task item from Claude Code's TodoWrite tool.</summary>
public sealed record AgentTodo(string Content, string Status);

/// <summary>
/// Port of <c>StrategyForge/Services/ClaudeRunner.swift</c>'s <c>ChatEvent</c>. A
/// single event streamed back from a headless Claude Code run. A closed hierarchy
/// (private base constructor — only the nested cases below can exist), mirroring
/// the Swift enum's associated values as one sealed record per case.
/// </summary>
public abstract record ChatEvent
{
    private ChatEvent() { }

    /// <summary>A complete assistant text block.</summary>
    public sealed record AssistantText(string Text) : ChatEvent;
    /// <summary>A streamed text fragment (partial messages).</summary>
    public sealed record AssistantDelta(string Text) : ChatEvent;
    /// <summary>A tool the agent invoked (+ a short target/detail).</summary>
    public sealed record Tool(string Name, string? Detail) : ChatEvent;
    /// <summary>A Bash command began (Id links to its later output).</summary>
    public sealed record CommandStarted(string Id, string Command) : ChatEvent;
    /// <summary>A tool_result's output text.</summary>
    public sealed record CommandOutput(string Id, string Output) : ChatEvent;
    /// <summary>The orchestrator delegated to this subagent.</summary>
    public sealed record Delegated(string SubagentName) : ChatEvent;
    /// <summary>The agent's task list (TodoWrite).</summary>
    public sealed record Todos(IReadOnlyList<AgentTodo> Items) : ChatEvent;
    /// <summary>Absolute path of a file the agent wrote/edited.</summary>
    public sealed record FileEdited(string Path) : ChatEvent;
    /// <summary>An Agent Skill the model pulled into context.</summary>
    public sealed record SkillUsed(string Slug) : ChatEvent;
    /// <summary>Tool uses the run wasn't permitted to perform.</summary>
    public sealed record Denied(IReadOnlyList<string> Items) : ChatEvent;
    /// <summary>Authoritative run total (result line).</summary>
    public sealed record Usage(int Tokens, double CostUsd) : ChatEvent;
    /// <summary>Per-message usage tagged with its model.</summary>
    public sealed record ModelUsage(string Model, int Tokens) : ChatEvent;
    /// <summary>A live approval gate.</summary>
    public sealed record PermissionRequest(string Id, string ToolName, string Detail) : ChatEvent;
    /// <summary>The run completed successfully.</summary>
    public sealed record Finished : ChatEvent;
    /// <summary>The run could not start / errored.</summary>
    public sealed record Failed(string Message) : ChatEvent;
}

/// <summary>
/// Port of <c>ClaudeRunner.swift</c>'s <c>ClaudeStreamParser</c>. Pure, tolerant
/// parser for Claude Code's <c>--output-format stream-json</c> lines. Kept
/// separate from process handling (Fase 3's process-spawning side, not ported
/// yet) so it can be tested without spawning a real <c>claude</c> process.
/// </summary>
public static class ClaudeStreamParser
{
    /// <summary>Parse one NDJSON line into zero or more chat events.
    /// Unknown/invalid lines yield nothing (forward-compatible with schema
    /// changes — never throws on malformed or adversarial input).</summary>
    public static List<ChatEvent> Events(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return new List<ChatEvent>();

        JsonNode? node;
        try { node = JsonNode.Parse(trimmed); }
        catch (JsonException) { return new List<ChatEvent>(); }
        if (node is not JsonObject obj) return new List<ChatEvent>();

        var type = Str(obj["type"]);

        // Streamed text fragment (with --include-partial-messages).
        if (type == "stream_event"
            && obj["event"] is JsonObject ev
            && Str(ev["type"]) == "content_block_delta"
            && ev["delta"] is JsonObject delta
            && Str(delta["type"]) == "text_delta")
        {
            var text = Str(delta["text"]);
            return string.IsNullOrEmpty(text)
                ? new List<ChatEvent>()
                : new List<ChatEvent> { new ChatEvent.AssistantDelta(text) };
        }

        // Assistant turn: pull text + tool_use blocks from message.content.
        if (type == "assistant" && obj["message"] is JsonObject message
            && message["content"] is JsonArray content)
        {
            var events = new List<ChatEvent>();
            foreach (var blockNode in content)
            {
                if (blockNode is not JsonObject block) continue;
                var blockType = Str(block["type"]);
                if (blockType == "text")
                {
                    var text = Str(block["text"]);
                    if (!string.IsNullOrEmpty(text)) events.Add(new ChatEvent.AssistantText(text));
                }
                else if (blockType == "tool_use")
                {
                    var name = Str(block["name"]);
                    if (name != null)
                    {
                        var input = block["input"] as JsonObject;
                        // Delegation (the differentiator): surface WHICH subagent runs.
                        if (name is "Task" or "Agent")
                        {
                            var sub = Str(input?["subagent_type"]) ?? Str(input?["description"]) ?? name;
                            events.Add(new ChatEvent.Delegated(sub));
                        }
                        else if (name == "TodoWrite" && input?["todos"] is JsonArray todosArr)
                        {
                            var todos = todosArr.OfType<JsonObject>()
                                .Select(t => new AgentTodo(Str(t["content"]) ?? "", Str(t["status"]) ?? "pending"))
                                .ToList();
                            events.Add(new ChatEvent.Todos(todos));
                        }
                        else if (name == "Skill")
                        {
                            // Claude Code surfaces a skill as a Skill tool_use; the
                            // slug is in command/name/skill depending on version.
                            var slug = Str(input?["command"]) ?? Str(input?["name"]) ?? Str(input?["skill"]) ?? "skill";
                            events.Add(new ChatEvent.SkillUsed(slug));
                        }
                        else
                        {
                            events.Add(new ChatEvent.Tool(name, ToolDetail(input)));
                        }

                        // Bash: remember the command so its later output (tool_result)
                        // can be shown in the code-mode terminal.
                        if (name == "Bash" && Str(block["id"]) is { } id && Str(input?["command"]) is { } cmd)
                        {
                            events.Add(new ChatEvent.CommandStarted(id, cmd));
                        }
                        // Note which files it edits, for a post-turn summary.
                        if (name is "Write" or "Edit" or "MultiEdit" or "NotebookEdit"
                            && Str(input?["file_path"]) is { } path)
                        {
                            events.Add(new ChatEvent.FileEdited(path));
                        }
                    }
                }
            }
            // Per-message token usage tagged with the model that produced it — the
            // native, exact source for the per-model breakdown (subagent messages
            // carry their own model, e.g. Haiku).
            if (Str(message["model"]) is { } modelId && message["usage"] is JsonObject usage)
            {
                var t = IntOrZero(usage, "input_tokens") + IntOrZero(usage, "output_tokens")
                    + IntOrZero(usage, "cache_creation_input_tokens") + IntOrZero(usage, "cache_read_input_tokens");
                if (t > 0) events.Add(new ChatEvent.ModelUsage(modelId, t));
            }
            return events;
        }

        // User turn carrying tool_result blocks — the output of the tools/commands.
        if (type == "user" && obj["message"] is JsonObject userMessage
            && userMessage["content"] is JsonArray userContent)
        {
            var events = new List<ChatEvent>();
            foreach (var blockNode in userContent)
            {
                if (blockNode is not JsonObject block || Str(block["type"]) != "tool_result") continue;
                var id = Str(block["tool_use_id"]);
                if (id == null) continue;
                events.Add(new ChatEvent.CommandOutput(id, ToolResultText(block["content"])));
            }
            return events;
        }

        // Final result line — carries token usage and success/failure.
        if (type == "result")
        {
            var events = new List<ChatEvent>();
            if (obj["permission_denials"] is JsonArray denials && denials.Count > 0)
            {
                var items = denials.OfType<JsonObject>().Select(den =>
                {
                    var name = Str(den["tool_name"]) ?? "tool";
                    var input = den["tool_input"] as JsonObject;
                    var detail = Str(input?["file_path"]) is { } fp ? Path.GetFileName(fp) : Str(input?["command"]) ?? "";
                    return string.IsNullOrEmpty(detail) ? name : $"{name} {detail}";
                }).ToList();
                events.Add(new ChatEvent.Denied(items));
            }
            if (obj["usage"] is JsonObject resultUsage)
            {
                var inputTokens = IntOrZero(resultUsage, "input_tokens");
                var outputTokens = IntOrZero(resultUsage, "output_tokens");
                var cacheCreate = IntOrZero(resultUsage, "cache_creation_input_tokens");
                var cacheRead = IntOrZero(resultUsage, "cache_read_input_tokens");
                var tokens = inputTokens + outputTokens + cacheCreate + cacheRead;
                var cost = DoubleOrZero(obj["total_cost_usd"]);
                if (tokens > 0 || cost > 0) events.Add(new ChatEvent.Usage(tokens, cost));
            }
            // A run can carry is_error: true while still reporting subtype
            // "success" (notably a 401 auth failure), so check both — otherwise
            // it reads as a silent empty finish.
            var subtype = Str(obj["subtype"]);
            var isError = BoolOrFalse(obj["is_error"]);
            if (isError || (subtype != null && subtype != "success"))
            {
                var raw = Str(obj["result"]) ?? subtype ?? "The run failed.";
                var apiErrorStatus = IntOrNull(obj["api_error_status"]);
                events.Add(apiErrorStatus == 401 || raw.ToLowerInvariant().Contains("authenticate")
                    ? new ChatEvent.Failed("Claude couldn't authenticate (401). Your saved Claude login looks expired — open Terminal, run `claude`, sign in to your plan, then retry.")
                    : new ChatEvent.Failed(raw));
            }
            else
            {
                events.Add(new ChatEvent.Finished());
            }
            return events;
        }

        return new List<ChatEvent>();
    }

    /// <summary>Flatten a tool_result's content (a string, or an array of text blocks).</summary>
    private static string ToolResultText(JsonNode? content)
    {
        var s = Str(content);
        if (s != null) return s;
        if (content is JsonArray arr)
        {
            return string.Join("\n", arr.OfType<JsonObject>().Select(b => Str(b["text"])).Where(t => t != null));
        }
        return "";
    }

    /// <summary>A short human-readable "what it's acting on" for a tool use.</summary>
    private static string? ToolDetail(JsonObject? input)
    {
        if (input == null) return null;
        if (Str(input["file_path"]) is { } path) return Path.GetFileName(path);
        if (Str(input["command"]) is { } cmd) return cmd.Length > 60 ? cmd[..60] + "…" : cmd;
        if (Str(input["pattern"]) is { } pattern) return pattern;
        if (Str(input["url"]) is { } url) return url;
        if (Str(input["query"]) is { } query) return query;
        return null;
    }

    // MARK: - Tolerant JSON value extraction (never throws on a type mismatch,
    // matching Swift's `as?` casts).

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int IntOrZero(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

    private static int? IntOrNull(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;

    private static double DoubleOrZero(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    private static bool BoolOrFalse(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
