namespace Coral.Core.Models;

/// <summary>
/// Port of <c>StrategyForge/Models/EvalSuite.swift</c>. Evals for a team: a
/// suite is a set of test scenarios; running it scores the team with an
/// independent read-only judge (not ported yet — that's
/// <c>Services/EvalRunner.swift</c>) and gates "ready" on a pass rate. Pure
/// data only; the runner is a later phase (it parses judge output and, once
/// trajectory grading lands, inspects a real run — Fase 3+ territory).
/// </summary>
public enum EvalCategory
{
    AnswersCorrectly,   // the team should answer well
    RefusesWhenUnknown, // out of scope -> should say it doesn't know, not hallucinate
    MultiHop,           // needs combining multiple pieces
    Citation,           // must ground / cite its sources
    Adversarial,        // red-team: jailbreak / prompt injection / exfil / tool abuse
}

public static class EvalCategoryExtensions
{
    public static string RawValue(this EvalCategory category) => category switch
    {
        EvalCategory.AnswersCorrectly => "answersCorrectly",
        EvalCategory.RefusesWhenUnknown => "refusesWhenUnknown",
        EvalCategory.MultiHop => "multiHop",
        EvalCategory.Citation => "citation",
        EvalCategory.Adversarial => "adversarial",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    public static EvalCategory? FromRawValue(string value) => value switch
    {
        "answersCorrectly" => EvalCategory.AnswersCorrectly,
        "refusesWhenUnknown" => EvalCategory.RefusesWhenUnknown,
        "multiHop" => EvalCategory.MultiHop,
        "citation" => EvalCategory.Citation,
        "adversarial" => EvalCategory.Adversarial,
        _ => null,
    };

    /// <summary>Red-team scenarios probe SAFETY (resisting attack), so a pass
    /// means "held", and they belong to the safety dimension of the rubric.</summary>
    public static bool IsAdversarial(this EvalCategory category) => category == EvalCategory.Adversarial;

    public static string LabelKey(this EvalCategory category) => $"eval.category.{category.RawValue()}";
}

/// <summary>One test case: a prompt plus the behavior a judge should verify in the response.</summary>
public sealed class EvalScenario
{
    public Guid Id { get; set; }
    public string Prompt { get; set; }
    /// <summary>The behavior the judge checks the team's answer against.</summary>
    public string Expectation { get; set; }
    public EvalCategory Category { get; set; }
    /// <summary>What the team's PATH should look like — which subagents/tools
    /// it should (or must NOT) use — so a right answer for the wrong reason
    /// still fails. Empty = don't grade the trajectory for this scenario.</summary>
    public string TrajectoryExpectation { get; set; }

    public EvalScenario(string prompt, string expectation, EvalCategory category,
        string trajectoryExpectation = "", Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Prompt = prompt;
        Expectation = expectation;
        Category = category;
        TrajectoryExpectation = trajectoryExpectation;
    }
}

/// <summary>A reusable suite attached to a Strategy.</summary>
public sealed class EvalSuite
{
    public Guid Id { get; set; }
    public List<EvalScenario> Scenarios { get; set; }
    /// <summary>Pass-rate (0…1) a run must reach for the team to count as
    /// "ready" / a loop to be safe to schedule.</summary>
    public double PassThreshold { get; set; }
    /// <summary>The last run's per-scenario verdict (scenario id string →
    /// passed), kept so a re-run after a prompt/model change can diff against
    /// it. Empty until the first run.</summary>
    public Dictionary<string, bool> Baseline { get; set; }

    public EvalSuite(List<EvalScenario>? scenarios = null, double passThreshold = 0.8,
        Dictionary<string, bool>? baseline = null, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Scenarios = scenarios ?? new List<EvalScenario>();
        PassThreshold = passThreshold;
        Baseline = baseline ?? new Dictionary<string, bool>();
    }
}

/// <summary>The verdict for a single scenario after a run.</summary>
public sealed class EvalResult
{
    public Guid ScenarioId { get; set; }
    public bool Passed { get; set; }
    /// <summary>The judge's short reason.</summary>
    public string Reason { get; set; }
    /// <summary>Per-dimension rubric scores (0…5). Empty when the judge returned none.</summary>
    public Dictionary<string, int> Scores { get; set; } = new();
    /// <summary>Whether the team's PATH matched the scenario's trajectory
    /// expectation — null when the scenario didn't ask for one.</summary>
    public bool? TrajectoryPassed { get; set; }
    public string TrajectoryReason { get; set; } = "";
    /// <summary>A human's override of the judge's verdict, for calibration — null = not reviewed.</summary>
    public bool? HumanVerdict { get; set; }

    public EvalResult(Guid scenarioId, bool passed, string reason)
    {
        ScenarioId = scenarioId;
        Passed = passed;
        Reason = reason;
    }

    /// <summary>The verdict that counts: the human's override when present, else the judge's.</summary>
    public bool EffectivePassed => HumanVerdict ?? Passed;
}

/// <summary>The outcome of running a whole suite.</summary>
public sealed class EvalRun
{
    public List<EvalResult> Results { get; }
    public double Threshold { get; }

    public EvalRun(List<EvalResult> results, double threshold)
    {
        Results = results;
        Threshold = threshold;
    }

    public int Total => Results.Count;
    /// <summary>Counts the EFFECTIVE verdict (a human override wins over the
    /// judge), so calibrating a wrong verdict moves the gate.</summary>
    public int Passed => Results.Count(r => r.EffectivePassed);
    /// <summary>Fraction passed (0…1); 0 for an empty run.</summary>
    public double PassRate => Total == 0 ? 0 : (double)Passed / Total;
    /// <summary>The gate: did the run clear the suite's threshold? An empty run never passes.</summary>
    public bool MeetsThreshold => Total > 0 && PassRate >= Threshold;

    /// <summary>The pass-map to persist as the next run's baseline.</summary>
    public Dictionary<string, bool> BaselineMap =>
        Results.ToDictionary(r => r.ScenarioId.ToString(), r => r.EffectivePassed);
}

/// <summary>Regression delta between a suite's saved baseline and a fresh run:
/// which scenarios went pass→fail (regressions) and fail→pass (fixes),
/// matched by scenario id.</summary>
public sealed record EvalRegression(List<Guid> Regressed, List<Guid> Fixed, bool HasBaseline)
{
    public static EvalRegression Between(Dictionary<string, bool> baseline, EvalRun run)
    {
        if (baseline.Count == 0) return new EvalRegression(new List<Guid>(), new List<Guid>(), false);
        var regressed = new List<Guid>();
        var fixedScenarios = new List<Guid>();
        foreach (var r in run.Results)
        {
            if (!baseline.TryGetValue(r.ScenarioId.ToString(), out var was)) continue;
            var now = r.EffectivePassed;
            if (was && !now) regressed.Add(r.ScenarioId);
            if (!was && now) fixedScenarios.Add(r.ScenarioId);
        }
        return new EvalRegression(regressed, fixedScenarios, true);
    }
}
