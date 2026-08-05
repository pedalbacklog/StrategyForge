using System.Text;
using Coral.Core.Models;

namespace Coral.Core.Generators;

/// <summary>
/// Port of <c>StrategyForge/Generators/WorkflowGenerator.swift</c>. Turns a team
/// (Strategy) into a Claude Code DYNAMIC WORKFLOW — the team's topology becomes a
/// runnable program a background runtime executes: the orchestrator plans,
/// teammates run in PARALLEL, then the orchestrator synthesizes. Pure + testable
/// (no disk). Emitted to <c>.claude/workflows/&lt;slug&gt;.mjs</c>. Only meaningful
/// for a team with teammates (a solo agent is just one prompt, not a graph).
/// </summary>
public static class WorkflowGenerator
{
    /// <summary>Repo-relative directory generated workflows live in.</summary>
    public const string WorkflowsDirectory = ".claude/workflows";

    /// <summary>Signature marking a workflow file WE generated — pruning only ever
    /// touches files carrying it, so hand-written workflows are never removed.</summary>
    public const string ManagedSignature = "// coral: generated dynamic workflow";

    /// <summary>Repo-relative path for a team's generated workflow.</summary>
    public static string FileName(Strategy strategy) => $"{WorkflowsDirectory}/{Slug(strategy)}.mjs";

    /// <summary>A stable, filesystem-safe slug from the team name (falls back to "team").</summary>
    public static string Slug(Strategy strategy)
    {
        var mapped = new string(strategy.Name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        var collapsed = string.Join("-", mapped.Split('-', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length == 0 ? "team" : collapsed;
    }

    /// <summary>The dynamic-workflow program for a team, or null for a solo team (no graph).</summary>
    public static string? Workflow(Strategy strategy)
    {
        var teammates = strategy.SubagentRoles.Where(r => Strategy.IsValidRoleName(r.Name)).ToList();
        var orchestrator = strategy.Orchestrator;
        if (teammates.Count == 0 || orchestrator == null) return null;

        // Split the roster into PRODUCERS (do the work in parallel) and REVIEWERS
        // (gate the edge). Moving a reviewer to a dedicated Verify phase makes it an
        // independent node on a fresh context, checking the producers' output before
        // it flows to synthesis.
        var reviewers = teammates.Where(t => t.Role == RoleKind.Reviewer).ToList();
        var producers = teammates.Where(t => t.Role != RoleKind.Reviewer).ToList();
        // If the team is ALL reviewers (unusual), treat them as producers so it still runs.
        var workers = producers.Count == 0 ? teammates : producers;
        var verifier = producers.Count == 0 ? null : reviewers.FirstOrDefault();
        var hasVerify = verifier != null;

        // A fan-out of identical workers (all producers are plain workers, >=2
        // instances via the role's Count) is a "one agent per unit of work" job.
        // Emit the canonical item fan-out instead of one branch per role.
        var workerInstances = producers.Where(p => p.Role == RoleKind.Worker).Sum(p => Math.Max(1, p.Count));
        if (producers.Count > 0 && producers.All(p => p.Role == RoleKind.Worker) && workerInstances >= 2)
        {
            return ItemFanoutWorkflow(strategy, orchestrator, producers[0], verifier);
        }

        var name = Slug(strategy);
        var description = JsLine(string.IsNullOrEmpty(strategy.Description) ? strategy.Name : strategy.Description);

        var sb = new StringBuilder();
        sb.Append(ManagedSignature).Append(" for the team \"").Append(JsLine(strategy.Name)).Append("\".\n");
        sb.Append("// Run it with Claude Code: pass your task as args. Regenerated on Generate.\n");
        sb.Append("// NOTE: this fans out parallel agents — it costs more than a single session and\n");
        sb.Append("// should be supervised. Start with a scoped task, watch usage, then widen.\n\n");

        var phases = hasVerify
            ? "[{ title: 'Plan' }, { title: 'Work' }, { title: 'Verify' }, { title: 'Synthesize' }]"
            : "[{ title: 'Plan' }, { title: 'Work' }, { title: 'Synthesize' }]";
        sb.Append("export const meta = {\n");
        sb.Append("  name: '").Append(name).Append("',\n");
        sb.Append("  description: '").Append(description).Append("',\n");
        sb.Append("  phases: ").Append(phases).Append(",\n");
        sb.Append("}\n\n");

        sb.Append("// The task comes in as `args` (falls back to the team's purpose).\n");
        sb.Append("const task = (typeof args === 'string' && args) ? args : `")
          .Append(JsTemplate(strategy.Description)).Append("`\n\n");

        // Plan.
        sb.Append("phase('Plan')\n");
        sb.Append("const plan = await agent(`").Append(JsTemplate(PlanPrompt(orchestrator)))
          .Append("\n\nTASK:\n${task}`, ");
        sb.Append("{ label: 'plan' })\n\n");

        // Work — one teammate per parallel branch. A producer that EDITS files runs
        // in its own git worktree so parallel writers can't overwrite each other.
        sb.Append("phase('Work')\n");
        sb.Append("const results = await parallel([\n");
        foreach (var w in workers)
        {
            var model = w.Model.ToRawValue();
            var isolation = w.Role.IsReadOnlyByDefault() ? "" : ", isolation: 'worktree'";
            sb.Append("  () => agent(`").Append(JsTemplate(WorkerPrompt(w)))
              .Append("\n\nTASK:\n${task}\n\nPLAN:\n${plan}`, ");
            sb.Append("{ label: '").Append(JsLine(w.Name)).Append("', phase: 'Work', model: '")
              .Append(model).Append('\'').Append(isolation).Append(" }),\n");
        }
        sb.Append("])\n\n");

        // Verify — an INDEPENDENT node on a fresh context checks each producer's
        // finding against real evidence before it flows downstream.
        var synthInput = "results";
        if (verifier != null)
        {
            var model = verifier.Model.ToRawValue();
            sb.Append("phase('Verify')\n");
            sb.Append("const verified = await parallel(results.filter(Boolean).map((r, i) => ");
            sb.Append("agent(`").Append(JsTemplate(VerifyPrompt(verifier)))
              .Append("\n\nTASK:\n${task}\n\nWORK TO VERIFY:\n${r}`, ");
            sb.Append("{ label: 'verify:' + i, phase: 'Verify', model: '").Append(model).Append("' })))\n\n");
            synthInput = "verified";
        }

        // Synthesize.
        sb.Append("phase('Synthesize')\n");
        sb.Append("const final = await agent(`").Append(JsTemplate(SynthesisPrompt)).Append("\n\n");
        sb.Append("TASK:\n${task}\n\n").Append(hasVerify ? "VERIFIED FINDINGS" : "TEAMMATE RESULTS")
          .Append(":\n${").Append(synthInput).Append(".filter(Boolean).join('\\n\\n---\\n\\n')}`, ");
        sb.Append("{ label: 'synthesize' })\n\n");
        sb.Append("return final\n");
        return sb.ToString();
    }

    /// <summary>The item fan-out workflow: a scout discovers the work-list, then a
    /// pipeline runs one worker agent per item and — when the team has a reviewer —
    /// verifies each finding as soon as it lands, before a final synthesis. Used for
    /// fan-out-of-identical-worker teams.</summary>
    public static string ItemFanoutWorkflow(Strategy strategy, AgentRole orchestrator, AgentRole worker, AgentRole? verifier)
    {
        var name = Slug(strategy);
        var description = JsLine(string.IsNullOrEmpty(strategy.Description) ? strategy.Name : strategy.Description);
        var workerModel = worker.Model.ToRawValue();
        var isolation = worker.Role.IsReadOnlyByDefault() ? "" : ", isolation: 'worktree'";
        var hasVerify = verifier != null;

        var sb = new StringBuilder();
        sb.Append(ManagedSignature).Append(" for the team \"").Append(JsLine(strategy.Name)).Append("\" — ITEM FAN-OUT.\n");
        sb.Append("// One agent per unit of work (file/item): scout the list, fan out, verify each.\n");
        sb.Append("// NOTE: this can fan out to many agents — it costs more than a single session and\n");
        sb.Append("// should be supervised. The scout starts scoped (≤50 items); widen once it behaves.\n\n");

        var phases = hasVerify
            ? "[{ title: 'Scout' }, { title: 'Work' }, { title: 'Verify' }, { title: 'Synthesize' }]"
            : "[{ title: 'Scout' }, { title: 'Work' }, { title: 'Synthesize' }]";
        sb.Append("export const meta = {\n  name: '").Append(name).Append("',\n  description: '")
          .Append(description).Append("',\n  phases: ").Append(phases).Append(",\n}\n\n");

        sb.Append("// The task/target comes in as `args` (falls back to the team's purpose).\n");
        sb.Append("const task = (typeof args === 'string' && args) ? args : `")
          .Append(JsTemplate(strategy.Description)).Append("`\n\n");

        // Scout — discover the work-list. Returns a JSON array of items (parsed tolerantly).
        sb.Append("phase('Scout')\n");
        sb.Append("const scoutText = await agent(`").Append(JsTemplate(ScoutPrompt(orchestrator)))
          .Append("\n\nTASK:\n${task}`, ");
        sb.Append("{ label: 'scout' })\n");
        sb.Append("let items = []\n");
        sb.Append("try { items = JSON.parse((scoutText.match(/\\[[\\s\\S]*\\]/) || ['[]'])[0]) } catch { items = [] }\n");
        sb.Append("items = items.filter(x => typeof x === 'string' && x.trim()).slice(0, 50)\n");
        sb.Append("log(`${items.length} item(s) to process`)\n\n");

        // Work + Verify — pipeline: each item flows through work -> verify
        // independently, no barrier.
        sb.Append("phase('Work')\n");
        if (hasVerify && verifier != null)
        {
            var vModel = verifier.Model.ToRawValue();
            sb.Append("const findings = await pipeline(items,\n");
            sb.Append("  (item) => agent(`").Append(JsTemplate(ItemWorkerPrompt(worker)))
              .Append("\n\nTASK:\n${task}\n\nITEM:\n${item}`, ");
            sb.Append("{ label: 'work:' + item, phase: 'Work', model: '").Append(workerModel).Append('\'')
              .Append(isolation).Append(" }),\n");
            sb.Append("  (result, item) => agent(`").Append(JsTemplate(VerifyPrompt(verifier)))
              .Append("\n\nITEM:\n${item}\n\nWORK TO VERIFY:\n${result}`, ");
            sb.Append("{ label: 'verify:' + item, phase: 'Verify', model: '").Append(vModel).Append("' })\n");
            sb.Append(")\n\n");
        }
        else
        {
            sb.Append("const findings = await parallel(items.map((item) => () => ");
            sb.Append("agent(`").Append(JsTemplate(ItemWorkerPrompt(worker)))
              .Append("\n\nTASK:\n${task}\n\nITEM:\n${item}`, ");
            sb.Append("{ label: 'work:' + item, phase: 'Work', model: '").Append(workerModel).Append('\'')
              .Append(isolation).Append(" })))\n\n");
        }

        // Synthesize — one report from the (verified) findings, not N separate chats.
        sb.Append("phase('Synthesize')\n");
        sb.Append("const final = await agent(`").Append(JsTemplate(SynthesisPrompt)).Append("\n\n");
        sb.Append("TASK:\n${task}\n\n").Append(hasVerify ? "VERIFIED FINDINGS" : "FINDINGS")
          .Append(":\n${findings.filter(Boolean).join('\\n\\n---\\n\\n')}`, ");
        sb.Append("{ label: 'synthesize' })\n\n");
        sb.Append("return final\n");
        return sb.ToString();
    }

    // MARK: - Prompts (pure)

    /// <summary>The scout node's prompt: discover the independent units of work and
    /// return them as a JSON array (files/items/crates), so the pipeline can fan out
    /// one agent per item.</summary>
    private static string ScoutPrompt(AgentRole orchestrator)
    {
        var persona = orchestrator.SystemPrompt.Trim();
        var lead = persona.Length == 0 ? "You are the orchestrator of a team of AI agents." : persona;
        return lead + "\nDiscover the independent units of work for this task — the files/items an "
            + "agent should each handle on its own. Prefer a deterministic listing (a glob, a git "
            + "ls-files, a script). Return ONLY a JSON array of strings (the item identifiers), "
            + "nothing else. Start scoped: at most 50 items.";
    }

    /// <summary>The per-item worker prompt: handle exactly ONE item and report its finding.</summary>
    private static string ItemWorkerPrompt(AgentRole role)
    {
        var persona = role.SystemPrompt.Trim();
        var lead = persona.Length == 0
            ? $"You are {role.Name}, a {role.Role.DisplayName().ToLowerInvariant()} on the team."
            : persona;
        return lead + "\nYou handle exactly ONE item (below). Do the task for just that item and "
            + "report your finding concisely. If you can't do it confidently, flag it with a clear reason.";
    }

    private static string PlanPrompt(AgentRole orchestrator)
    {
        var persona = orchestrator.SystemPrompt.Trim();
        var lead = persona.Length == 0 ? "You are the orchestrator of a team of AI agents." : persona;
        return lead + "\nBreak the task into focused, parallel-friendly pieces the teammates can each take. "
            + "Write a short plan the teammates will read.";
    }

    private static string WorkerPrompt(AgentRole role)
    {
        var persona = role.SystemPrompt.Trim();
        return persona.Length == 0
            ? $"You are {role.Name}, a {role.Role.DisplayName().ToLowerInvariant()} on the team. Do your part of the task and report back concisely."
            : persona;
    }

    /// <summary>The verifier node's prompt: it did NOT produce the work and runs on
    /// a fresh context, so it checks a real signal rather than "did the agent say
    /// it's done".</summary>
    private static string VerifyPrompt(AgentRole reviewer)
    {
        var persona = reviewer.SystemPrompt.Trim();
        var lead = persona.Length == 0
            ? "You are an INDEPENDENT verifier. You did not do this work."
            : persona;
        return lead + "\nVerify the work below against real evidence — run/inspect the actual "
            + "signal (a test that PASSED, a claim that's grounded), not whether it claims to be "
            + "done. Return the finding only if it holds; otherwise state exactly what fails.";
    }

    private static string SynthesisPrompt =>
        "You are the orchestrator. Combine the verified results into one coherent, "
            + "actionable final answer. Resolve conflicts, remove redundancy.";

    // MARK: - JS escaping

    /// <summary>Escape a value for a SINGLE-LINE JS context (single quotes /
    /// comments): flatten newlines and escape quotes/backslashes.</summary>
    private static string JsLine(string s) =>
        s.Replace("\\", "\\\\")
         .Replace("'", "\\'")
         .Replace("\r", " ")
         .Replace("\n", " ");

    /// <summary>Escape a value for embedding inside a JS TEMPLATE literal
    /// (backticks): escape backslash, backtick, and <c>${</c> so the persona text
    /// can't break out or interpolate.</summary>
    private static string JsTemplate(string s) =>
        s.Replace("\\", "\\\\")
         .Replace("`", "\\`")
         .Replace("${", "\\${");
}
