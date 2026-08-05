using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Coral.Core.Models;

namespace Coral.Core.Generators;

/// <summary>
/// Port of <c>StrategyForge/Generators/McpConfigGenerator.swift</c>. Turns a
/// strategy's MCP servers into the <c>.mcp.json</c> Claude Code auto-loads from the
/// project root, plus the Gemini/Codex CLI equivalents. Pure — no disk access.
/// </summary>
public static class McpConfigGenerator
{
    /// <summary>The project-root file Claude Code reads MCP servers from.</summary>
    public const string FileName = ".mcp.json";

    /// <summary>Pretty-printed <c>.mcp.json</c> for these servers, or null if none
    /// are usable (a server needs both a name and a command).</summary>
    public static string? Json(List<McpServer> servers)
    {
        var entries = UsableEntries(servers);
        if (entries.Count == 0) return null;
        var root = new JsonObject { ["mcpServers"] = entries };
        return Encode(root);
    }

    /// <summary>Merge our servers INTO an existing <c>.mcp.json</c>, preserving the
    /// user's own <c>mcpServers</c> entries (and any other top-level keys); ours win
    /// only on a name collision. Returns null when there's nothing to write — OR
    /// when an existing file is present but unparseable, so a hand-authored file is
    /// never clobbered.</summary>
    public static string? MergedJson(string? existing, List<McpServer> servers)
    {
        var ours = UsableEntries(servers);
        JsonObject root;
        JsonObject theirs;
        if (!string.IsNullOrWhiteSpace(existing))
        {
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(existing); }
            catch (JsonException) { return null; }
            if (parsed is not JsonObject obj) return null;
            root = obj;
            theirs = obj["mcpServers"] as JsonObject ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
            theirs = new JsonObject();
        }
        if (ours.Count == 0 && theirs.Count == 0) return null;
        var merged = new JsonObject();
        foreach (var kv in theirs.ToList()) merged[kv.Key] = kv.Value?.DeepClone();
        foreach (var kv in ours.ToList()) merged[kv.Key] = kv.Value?.DeepClone();
        root["mcpServers"] = merged;
        return Encode(root);
    }

    /// <summary><c>[name: { command, args?, env? }]</c> for the servers that have a
    /// name + command.</summary>
    private static JsonObject UsableEntries(List<McpServer> servers)
    {
        var byName = new JsonObject();
        foreach (var s in servers)
        {
            if (string.IsNullOrWhiteSpace(s.Name) || string.IsNullOrWhiteSpace(s.Command)) continue;
            var entry = new JsonObject { ["command"] = s.Command };
            if (s.Args.Count > 0)
            {
                entry["args"] = new JsonArray(s.Args.Select(a => (JsonNode?)a).ToArray());
            }
            if (s.Env.Count > 0)
            {
                var envObj = new JsonObject();
                foreach (var kv in s.Env) envObj[kv.Key] = kv.Value;
                entry["env"] = envObj;
            }
            byName[s.Name] = entry;
        }
        return byName;
    }

    private static string Encode(JsonObject root)
    {
        var sorted = SortKeys(root);
        return sorted.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Recursively sorts object keys, mirroring Swift's
    /// <c>JSONSerialization.SortedKeys</c> option so output is deterministic.</summary>
    private static JsonObject SortKeys(JsonObject obj)
    {
        var result = new JsonObject();
        foreach (var kv in obj.ToList().OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var value = kv.Value?.DeepClone();
            if (value is JsonObject childObj) value = SortKeys(childObj);
            result[kv.Key] = value;
        }
        return result;
    }

    // MARK: - Gemini CLI

    /// <summary>Gemini CLI reads MCP servers from <c>.gemini/settings.json</c>, using
    /// the SAME <c>mcpServers</c> shape as Claude's <c>.mcp.json</c>, so we can reuse
    /// the merge logic — it preserves the user's other settings keys and only
    /// adds/overrides our servers.</summary>
    public const string GeminiFileName = ".gemini/settings.json";

    public static string? GeminiSettingsJson(string? existing, List<McpServer> servers) =>
        MergedJson(existing, servers);

    // MARK: - Codex CLI

    /// <summary>Codex reads MCP servers from a TOML config as
    /// <c>[mcp_servers.NAME]</c> blocks. We emit a project-scoped
    /// <c>.codex/config.toml</c> as a portable artifact (Codex may also read the
    /// global <c>~/.codex/config.toml</c>). No merge — TOML has no stdlib parser
    /// here — so the caller writes this only when no file exists, to never clobber
    /// a hand-authored one.</summary>
    public const string CodexFileName = ".codex/config.toml";

    public static string? CodexToml(List<McpServer> servers)
    {
        var usable = servers
            .Where(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Command))
            .ToList();
        if (usable.Count == 0) return null;
        var sb = new StringBuilder();
        foreach (var s in usable.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var key = TomlKey(s.Name);
            sb.Append("[mcp_servers.").Append(key).Append("]\n");
            sb.Append("command = ").Append(TomlString(s.Command)).Append('\n');
            if (s.Args.Count > 0)
            {
                sb.Append("args = [").Append(string.Join(", ", s.Args.Select(TomlString))).Append("]\n");
            }
            if (s.Env.Count > 0)
            {
                sb.Append('\n').Append("[mcp_servers.").Append(key).Append(".env]\n");
                foreach (var kv in s.Env.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    sb.Append(TomlKey(kv.Key)).Append(" = ").Append(TomlString(kv.Value)).Append('\n');
                }
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>A TOML basic string (double-quoted, backslash/quote escaped).</summary>
    private static string TomlString(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>A bare TOML key when it's [A-Za-z0-9_-], else a quoted key.</summary>
    private static string TomlKey(string s)
    {
        var bare = s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-');
        return bare ? s : TomlString(s);
    }
}
