using Coral.Core.Models;
using Coral.Core.Services;

namespace Coral.Core.ViewModels;

/// <summary>
/// Drives the "Suggest a team" section of the Strategy flyout — wraps
/// <see cref="AdvisorEngine.AdviseTiers"/> (Economy/Recommended/Max), the
/// pure/deterministic/offline heuristic plus its cross-provider reassignment
/// (see <c>AdvisorEngine.cs</c>'s own doc comment: no LLM call, no network,
/// no cost). Port of the Advisor half of macOS's <c>AdvisorInlineCard.swift</c>
/// (which lives inline in the chat composer there; here it stays inside the
/// Strategy flyout, matching this port's existing layout — see Phase 3's
/// notes in PORT-PLAN.md).
///
/// Deliberately NOT ported from the Swift card: the "Apple Intelligence vs
/// local engine" source badge (no on-device AI path exists in this port —
/// <see cref="AdvisorEngine.Advice.UsedAI"/> is always false, so the badge
/// would never say anything but "Local engine"). The loop-kind HINT text is
/// ported (purely informational, reads <c>Advice.LoopKind</c>, which was
/// already safely ported without touching Fase 8's vetoed files — see
/// <c>Models/LoopKind.cs</c>'s own doc comment) but its "Create loop" BUTTON
/// is not: Fase 8 (Loops) has no scheduling/generation UI in this port yet,
/// and CLAUDE.md is explicit that Loop code needs a human reading the diff,
/// not an autonomous change — there is nowhere for that button to lead yet.
///
/// Kept separate from <see cref="StrategyPickerViewModel"/> on purpose, same
/// one-ViewModel-one-concern separation as <c>GitPanelViewModel</c>/
/// <c>PullRequestViewModel</c>: this ViewModel only produces a
/// recommendation, it never writes files — applying one is the caller's job
/// (<c>MainPage.xaml.cs</c>), via the exact same
/// <see cref="StrategyPickerViewModel.SelectAsync"/> path a manually picked
/// template already uses.
/// </summary>
public sealed class AdvisorViewModel : ObservableObject
{
    private string _task = "";
    public string Task { get => _task; set => SetProperty(ref _task, value); }

    /// <summary>The name of whatever strategy is currently selected elsewhere
    /// in the flyout, if any — lets the summary frame itself as "you chose
    /// X, here's what I'd recommend" instead of a blank suggestion. Set by
    /// the caller; null when nothing's selected yet.</summary>
    public string? ChosenTeamName
    {
        get => _chosenTeamName;
        set
        {
            if (SetProperty(ref _chosenTeamName, value))
            {
                OnPropertyChanged(nameof(HasChosenTeam));
                OnPropertyChanged(nameof(ChosenTeamHintText));
                OnPropertyChanged(nameof(ApplyButtonLabel));
            }
        }
    }
    private string? _chosenTeamName;

    public bool HasChosenTeam => ChosenTeamName is not null;

    /// <summary>"You chose "X" — here's what I'd recommend for this task:" —
    /// frames the section as a comparison instead of a blank pitch, once a
    /// team has actually been picked.</summary>
    public string ChosenTeamHintText => ChosenTeamName is null
        ? ""
        : $"You chose \"{ChosenTeamName}\" — here's what I'd recommend for this task:";

    private IReadOnlyList<AdvisorEngine.Tier> _tiers = Array.Empty<AdvisorEngine.Tier>();
    private IReadOnlyList<AdvisorEngine.Tier> Tiers
    {
        get => _tiers;
        set
        {
            if (SetProperty(ref _tiers, value))
            {
                OnPropertyChanged(nameof(HasAdvice));
                OnPropertyChanged(nameof(TierChips));
            }
        }
    }

    private string _selectedTierId = "balanced";
    public string SelectedTierId
    {
        get => _selectedTierId;
        private set
        {
            if (SetProperty(ref _selectedTierId, value))
            {
                OnPropertyChanged(nameof(TierChips));
                OnPropertyChanged(nameof(SummaryText));
                OnPropertyChanged(nameof(SelectedNoteText));
                OnPropertyChanged(nameof(DecisionLines));
                OnPropertyChanged(nameof(ProviderMixLines));
                OnPropertyChanged(nameof(HasLockedProviderInMix));
                OnPropertyChanged(nameof(ShowLoopHint));
                OnPropertyChanged(nameof(LoopHintText));
                OnPropertyChanged(nameof(ApplyButtonLabel));
            }
        }
    }

    /// <summary>The currently selected tier's full advice, or null while
    /// nothing's been suggested yet.</summary>
    public AdvisorEngine.Tier? SelectedTier =>
        Tiers.FirstOrDefault(t => t.Id == SelectedTierId) ?? Tiers.FirstOrDefault(t => t.Id == "balanced") ?? Tiers.FirstOrDefault();

    /// <summary>Bool-typed companion to <see cref="Tiers"/> — the port's
    /// existing <c>BoolToVisibilityConverter</c> only handles <c>bool</c>.</summary>
    public bool HasAdvice => Tiers.Count > 0;

    /// <summary>The tier options as small binding-friendly chips (label +
    /// cost headline + selection/recommended flags) — sized 1 to 3, since a
    /// tier that collapses to the same shape as Recommended is dropped.</summary>
    public IReadOnlyList<TierChip> TierChips => Tiers
        .Select(t => new TierChip(t.Id, TierLabel(t.Id), t.Advice.EstimatedCost.Headline,
            t.Id == SelectedTierId, t.Id == "balanced"))
        .ToList();

    /// <summary>A one-line, binding-friendly summary of the selected tier.</summary>
    public string SummaryText => SelectedTier is null
        ? ""
        : $"{SelectedTier.Advice.Model.DisplayName()} · {StrategyDisplayName(SelectedTier.Advice.Strategy)} · {LoopKindLabel(SelectedTier.Advice.LoopKind)}";

    /// <summary>The selected tier's tradeoff note (e.g. "Cheaper & faster —
    /// good enough for most").</summary>
    public string SelectedNoteText => SelectedTier is null ? "" : TierNote(SelectedTier.Id);

    public string ApplyButtonLabel => ChosenTeamName is null ? "Apply team" : "Switch to this";

    /// <summary>The selected tier's decision path, pre-formatted as one line
    /// per question/answer/evidence — same "format in the ViewModel, plain
    /// TextBlocks in XAML" pattern as <c>StrategyEditorViewModel.IssueLines</c>.</summary>
    public IReadOnlyList<string> DecisionLines
    {
        get
        {
            if (SelectedTier is null) return Array.Empty<string>();
            var lines = new List<string>();
            foreach (var step in SelectedTier.Advice.DecisionPath)
            {
                lines.Add($"Q: {QuestionText(step.QuestionKey)}");
                lines.Add($"{(step.AnswerIsYes ? "YES" : "NO")} — {AnswerText(step.AnswerKey)}");
                foreach (var evidenceKey in step.EvidenceKeys) lines.Add($"  · {EvidenceText(evidenceKey)}");
            }
            lines.Add($"Result: {SelectedTier.Advice.Model.DisplayName()}");
            return lines;
        }
    }

    /// <summary>True when the selected tier's task reads as recurring/
    /// event-driven — an informational hint only, see this class's doc
    /// comment for why there's no "Create loop" action.</summary>
    public bool ShowLoopHint => SelectedTier is not null && SelectedTier.Advice.LoopKind != LoopKind.TurnBased;

    public string LoopHintText => SelectedTier is null ? "" : LoopHintFor(SelectedTier.Advice.LoopKind);

    /// <summary>The cross-provider mix for the selected tier, pre-formatted
    /// one line per role — real picks when the run will actually mix
    /// providers, else the aspirational (display-only) ideal mix, so the
    /// claim is visible even on a Claude-only setup. Same "format in the
    /// ViewModel, plain TextBlocks in XAML" pattern as
    /// <see cref="DecisionLines"/>.</summary>
    public IReadOnlyList<string> ProviderMixLines => DisplayPicks()
        .Select(p => $"{p.RoleName} → {p.Provider.DisplayName()} · {p.ModelDisplayName} — {ProviderReasonText(p.ReasonKey)}{(p.IsConnected ? "" : " (not connected)")}")
        .ToList();

    public bool HasLockedProviderInMix => DisplayPicks().Any(p => !p.IsConnected);

    private IReadOnlyList<AdvisorEngine.ProviderPick> DisplayPicks()
    {
        if (SelectedTier is null) return Array.Empty<AdvisorEngine.ProviderPick>();
        if (SelectedTier.Advice.ProviderPicks.Count > 0) return SelectedTier.Advice.ProviderPicks;
        return AdvisorEngine.AspirationalPicks(SelectedTier.Advice.Strategy, ConnectedProviders(),
            AdvisorEngine.TierBiasFrom(SelectedTier.Id));
    }

    /// <summary>Run the heuristic on <see cref="Task"/>. Synchronous and
    /// instant (no I/O, no subprocess) — unlike every other "action" method
    /// in this port, there's nothing to await.</summary>
    public void Suggest()
    {
        var trimmed = Task.Trim();
        if (trimmed.Length == 0)
        {
            Tiers = Array.Empty<AdvisorEngine.Tier>();
            return;
        }
        Tiers = AdvisorEngine.AdviseTiers(trimmed, ConnectedProviders());
        SelectedTierId = "balanced";
    }

    /// <summary>Switch the displayed tier without recomputing anything —
    /// each tier's advice was already built by <see cref="Suggest"/>.</summary>
    public void SelectTier(string tierId) => SelectedTierId = tierId;

    /// <summary>Which providers currently look connected, from their stored
    /// login freshness — the same signal <c>ConnectViewModel</c> uses to
    /// skip a redundant re-login (<c>ProviderAuth.State.Ok</c>).</summary>
    private static HashSet<AIProvider> ConnectedProviders() =>
        Enum.GetValues<AIProvider>().Where(p => ProviderAuth.Freshness(p) == ProviderAuth.State.Ok).ToHashSet();

    private static string StrategyDisplayName(Strategy s) => s.Name;

    // MARK: - Display copy
    //
    // Windows has no localization layer (every other ported string in this
    // app is a plain English literal — see e.g. StrategyEditorPage.xaml).
    // These mirror the EN copy in StrategyForge/Localization+Advisor.swift
    // verbatim, with one deliberate wording fix: the "hardest = no" answer
    // names the model actually assigned on that branch (see AdvisorEngine.cs's
    // Advise()) — Opus 5, not the Swift copy's stale "Opus 4.8" (a pre-existing,
    // unrelated staleness in the shipping macOS strings, not reproduced here
    // since it would be a plain factual error against what this port assigns).

    private static string TierLabel(string tierId) => tierId switch
    {
        "saver" => "Economy",
        "balanced" => "Recommended",
        "max" => "Max quality",
        _ => tierId,
    };

    private static string TierNote(string tierId) => tierId switch
    {
        "saver" => "Cheaper & faster — good enough for most",
        "balanced" => "Best balance of quality and cost",
        "max" => "Better results, higher cost",
        _ => "",
    };

    private static string LoopKindLabel(LoopKind kind) => kind switch
    {
        LoopKind.TurnBased => "Turn-based",
        LoopKind.GoalBased => "Goal-based",
        LoopKind.TimeBased => "Time-based",
        LoopKind.Proactive => "Proactive",
        _ => kind.ToString(),
    };

    private static string LoopHintFor(LoopKind kind) => kind switch
    {
        LoopKind.GoalBased => "Repeat automatically until the goal is met (e.g. the tests pass).",
        LoopKind.TimeBased => "Run it automatically on a schedule (daily, weekly…).",
        LoopKind.Proactive => "Run it on an event — every PR, or whenever something fails.",
        _ => "",
    };

    private static string QuestionText(string key) => key switch
    {
        "advisor.q.depth" => "Does it need more than a quick answer?",
        "advisor.q.speed" => "Is maximum speed at minimal cost the priority?",
        "advisor.q.hardest" => "Is this your hardest, most ambitious work?",
        "advisor.q.team" => "Is a full team worth it here?",
        "advisor.q.loop" => "Does it repeat, or does it have a finish line?",
        _ => key,
    };

    private static string AnswerText(string key) => key switch
    {
        "advisor.a.depth.yes" => "This is real, multi-step work",
        "advisor.a.depth.no" => "A single good reply covers it",
        "advisor.a.speed.yes" => "Haiku 4.5: the fastest, cheapest tier",
        "advisor.a.speed.no" => "Sonnet 5: the balanced default",
        "advisor.a.hardest.yes" => "Fable 5: frontier reasoning for frontier work",
        "advisor.a.hardest.no" => "Opus 5: expert depth without the flagship price",
        "advisor.a.team.yes" => "The task splits into parallel work",
        "advisor.a.team.no" => "A lean setup fits a fast model better",
        "advisor.a.loop.turnBased" => "A simple turn-by-turn chat is enough",
        "advisor.a.loop.goalBased" => "It has a verifiable finish line: loop until it's met",
        "advisor.a.loop.timeBased" => "It recurs on a schedule: run it on a timer",
        "advisor.a.loop.proactive" => "It reacts to events: trigger it when they happen",
        _ => key,
    };

    private static string EvidenceText(string key) => key switch
    {
        "advisor.ev.multistep" => "multi-step verbs detected (migrate, refactor, build…)",
        "advisor.ev.long" => "long, detailed prompt",
        "advisor.ev.scope" => "mentions the repo, tests, many files or days of work",
        "advisor.ev.breadth" => "asks for breadth: several fronts, exhaustive, in parallel, a backlog",
        "advisor.ev.speedWords" => "quick-task words (summarize, translate, list…)",
        "advisor.ev.ambition" => "architecture / multi-day / deep-research scope",
        "advisor.ev.migration" => "a cross-cutting migration",
        "advisor.ev.orchestration" => "orchestration of many agents",
        "advisor.ev.complexScope" => "complex work over a large scope",
        "advisor.ev.cheapModel" => "a fast, cheap model doesn't need a fleet",
        "advisor.ev.serialDebug" => "a serial root-cause hunt — the accumulated context is the work",
        "advisor.ev.goalWords" => "a verifiable finish line (tests, lint, \"until…\")",
        "advisor.ev.timeWords" => "a recurring schedule (daily, every…)",
        "advisor.ev.eventWords" => "reacts to something happening (a failure, new work arriving)",
        _ => key,
    };

    private static string ProviderReasonText(string key) => key switch
    {
        "advisor.provider.reason.reasoning" => "strongest reasoning to lead",
        "advisor.provider.reason.coding" => "specialist at writing code",
        "advisor.provider.reason.breadth" => "widest context to read & research",
        "advisor.provider.reason.speed" => "fast & cheap for parallel work",
        "advisor.provider.reason.diversity" => "a second opinion from a different model family",
        "advisor.provider.reason.onlyOne" => "the only capable provider connected for this role",
        _ => key,
    };
}

/// <summary>One tier option as a binding-friendly chip for the tier-picker row.</summary>
public sealed record TierChip(string Id, string Label, string CostHeadline, bool IsSelected, bool IsRecommended);
