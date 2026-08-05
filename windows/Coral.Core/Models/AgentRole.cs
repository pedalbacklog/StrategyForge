using Coral.Core.Services;

namespace Coral.Core.Models;

/// <summary>
/// Port of <c>StrategyForge/Models/AgentRole.swift</c>. One role in a strategy — a
/// role expands into one or more Claude Code subagent files
/// (<c>.claude/agents/&lt;name&gt;-N.md</c>), except the orchestrator, whose model
/// is a launch setting and which never gets a frontmatter file.
///
/// A mutable class (not a Swift-style immutable-with-copy struct): callers mutate
/// fields in place, matching how the ported tests (and Strategy.AutoFixed logic,
/// not yet ported) use it.
/// </summary>
public sealed class AgentRole
{
    public Guid Id { get; set; }
    /// <summary>Slug used as the subagent name / filename. Must be unique within a
    /// strategy and contain no spaces (see <c>Strategy.IsValidRoleName</c>).</summary>
    public string Name { get; set; }
    public RoleKind Role { get; set; }
    /// <summary>The Claude model pinned in this subagent's frontmatter (used when
    /// this role runs on Claude). Ignored for the orchestrator.</summary>
    public ClaudeModel Model { get; set; }
    /// <summary>Which AI back-end runs this role — enables cross-provider mixes.</summary>
    public AIProvider Provider { get; set; }
    /// <summary>The chosen model id within <see cref="Provider"/> when it isn't
    /// Claude (else null, and <see cref="Model"/> applies).</summary>
    public string? ProviderModelId { get; set; }
    /// <summary>The body of the generated <c>.md</c> file — editable.</summary>
    public string SystemPrompt { get; set; }
    /// <summary>Frontmatter <c>description</c> — what makes Claude decide to
    /// delegate, so it should be specific and action-oriented.</summary>
    public string Description { get; set; }
    /// <summary>Tools granted to the subagent. Empty means "inherit all".</summary>
    public List<string> Tools { get; set; }
    /// <summary>Number of instances to expand this role into. Always 1 for the orchestrator.</summary>
    public int Count { get; set; }
    /// <summary>Whether this role is the single orchestrator (the main session).</summary>
    public bool IsOrchestrator { get; set; }
    /// <summary>Persistent per-agent memory: reads + appends a durable notes file
    /// (<c>.claude/memory/&lt;slug&gt;.md</c>) across runs.</summary>
    public bool MemoryEnabled { get; set; }

    public AgentRole(
        string name,
        RoleKind role,
        ClaudeModel model,
        string systemPrompt,
        string description,
        AIProvider provider = AIProvider.Claude,
        string? providerModelId = null,
        List<string>? tools = null,
        int count = 1,
        bool isOrchestrator = false,
        bool memoryEnabled = false,
        Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Name = name;
        Role = role;
        Model = model;
        Provider = provider;
        ProviderModelId = providerModelId;
        SystemPrompt = systemPrompt;
        Description = description;
        Tools = tools ?? new List<string>();
        Count = count;
        IsOrchestrator = isOrchestrator;
        MemoryEnabled = memoryEnabled;
    }

    /// <summary>The repo-relative path of this role's persistent memory file (per
    /// expanded instance, so <c>worker-2</c> keeps its own notes).</summary>
    public string MemoryPath(string instanceName) => $".claude/memory/{instanceName}.md";

    /// <summary>The model name to display for this role, honoring the provider choice.</summary>
    public string ModelDisplayName
    {
        get
        {
            if (Provider == AIProvider.Claude) return Model.DisplayName();
            var models = ModelCatalog.Models(Provider);
            var match = ProviderModelId is null ? null : models.FirstOrDefault(m => m.Id == ProviderModelId);
            return match?.DisplayName ?? (models.Count > 0 ? models[0].DisplayName : Provider.ToKey());
        }
    }

    /// <summary>Localization key for the capability tier shown under the model.</summary>
    public string TierNameKey
    {
        get
        {
            if (Provider == AIProvider.Claude) return Model.TierNameKey();
            var models = ModelCatalog.Models(Provider);
            var match = ProviderModelId is null ? null : models.FirstOrDefault(m => m.Id == ProviderModelId);
            return match?.TierKey ?? (models.Count > 0 ? models[0].TierKey : "model.tier.generalist");
        }
    }
}
