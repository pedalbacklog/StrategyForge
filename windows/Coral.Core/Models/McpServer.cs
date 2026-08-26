namespace Coral.Core.Models;

/// <summary>
/// Port of <c>StrategyForge/Models/Strategy.swift</c>'s <c>McpServer</c>. One MCP
/// (Model Context Protocol) tool server a strategy exposes to its agents —
/// eventually serialized into <c>.mcp.json</c> so Claude Code auto-loads it
/// (that write path isn't ported yet; StrategyWriter is a later phase).
/// </summary>
public sealed class McpServer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = new();
    public Dictionary<string, string> Env { get; set; } = new();
}
