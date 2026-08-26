using System.Text.Json;
using Coral.Core.Generators;
using Coral.Core.Models;
using Xunit;

namespace Coral.Tests;

/// <summary>Port of the McpConfigGenerator-related cases in
/// StrategyForgeTests/ImportExportTests.swift.</summary>
public class McpConfigGeneratorTests
{
    [Fact]
    public void MergePreservesUserServersOursWinOnCollision()
    {
        const string existing = """
        { "mcpServers": {
            "userTool": { "command": "npx", "args": ["-y", "user-server"] },
            "shared":   { "command": "old" }
        } }
        """;
        var ours = new List<McpServer>
        {
            new() { Name = "shared", Command = "new" },
            new() { Name = "coralTool", Command = "run" },
        };
        var merged = McpConfigGenerator.MergedJson(existing, ours);
        Assert.NotNull(merged);
        using var doc = JsonDocument.Parse(merged!);
        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.True(servers.TryGetProperty("userTool", out _));
        Assert.True(servers.TryGetProperty("coralTool", out _));
        Assert.Equal("new", servers.GetProperty("shared").GetProperty("command").GetString());
    }

    [Fact]
    public void MergeLeavesUnparseableFileUntouched()
    {
        Assert.Null(McpConfigGenerator.MergedJson("{ not json",
            new List<McpServer> { new() { Name = "x", Command = "y" } }));
    }
}

/// <summary>Port of StrategyForgeTests/McpMultiProviderTests.swift — the Gemini
/// (settings.json merge) and Codex (TOML) generators.</summary>
public class McpMultiProviderTests
{
    private static McpServer Server(string name, string cmd, List<string>? args = null, Dictionary<string, string>? env = null) =>
        new() { Name = name, Command = cmd, Args = args ?? new List<string>(), Env = env ?? new Dictionary<string, string>() };

    [Fact]
    public void CodexTomlHasBlocksArgsAndEnv()
    {
        var toml = McpConfigGenerator.CodexToml(new List<McpServer>
        {
            Server("github", "npx", args: new List<string> { "-y", "gh-mcp" }, env: new Dictionary<string, string> { ["TOKEN"] = "x" }),
        });
        Assert.NotNull(toml);
        Assert.Contains("[mcp_servers.github]", toml);
        Assert.Contains("command = \"npx\"", toml);
        Assert.Contains("args = [\"-y\", \"gh-mcp\"]", toml);
        Assert.Contains("[mcp_servers.github.env]", toml);
        Assert.Contains("TOKEN = \"x\"", toml);
    }

    [Fact]
    public void CodexTomlQuotesNonBareKeysAndEscapes()
    {
        var toml = McpConfigGenerator.CodexToml(new List<McpServer> { Server("wei rd", "a\"b") });
        Assert.NotNull(toml);
        Assert.Contains("[mcp_servers.\"wei rd\"]", toml);
        Assert.Contains("command = \"a\\\"b\"", toml);
    }

    [Fact]
    public void CodexTomlNilWhenNoUsableServers()
    {
        Assert.Null(McpConfigGenerator.CodexToml(new List<McpServer>()));
        Assert.Null(McpConfigGenerator.CodexToml(new List<McpServer> { Server("", "") }));
    }

    [Fact]
    public void GeminiMergesPreservingOtherSettings()
    {
        const string existing = """{ "theme": "dark", "mcpServers": { "old": { "command": "x" } } }""";
        var json = McpConfigGenerator.GeminiSettingsJson(existing, new List<McpServer> { Server("new", "cmd") });
        Assert.NotNull(json);
        Assert.Contains("\"theme\"", json);
        Assert.Contains("\"old\"", json);
        Assert.Contains("\"new\"", json);
    }
}
