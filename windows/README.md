# Coral for Windows

Native Windows port of Coral, built with Visual Studio. Lives entirely in this folder so
it can be developed in parallel with the macOS app (`StrategyForge/`) without touching
its build, its CI gate, or its code.

## Relationship to the macOS app

There is **no shared code** between the two apps — the macOS app is SwiftUI/AppKit and
can only build on Apple platforms, so a Windows port is a from-scratch implementation in
whatever stack Visual Studio targets. See **[`PORT-PLAN.md`](PORT-PLAN.md)** for the
full analysis, the stack recommendation (WinUI 3 + .NET, pending sign-off), the
macOS→Windows service/API mapping, the phased work order, and a Windows-specific
security review. What *is* shared is the product surface and its data contracts:

- **`models.json`** and **`skills.json`** (repo root) — the live model/skills catalog.
  Both apps read the same files, so the schema is a cross-platform contract: don't change
  a field's shape without checking `StrategyForge/Models/` for how the macOS app parses
  it, and vice versa.
- **`README.md`** (repo root) — the "How multi-agent works here" invariants apply to any
  Coral client, not just the macOS one.
- **Localization strings** — no shared file yet (macOS uses `L10n`/English+Spanish); if
  the Windows app needs the same strings, that's worth a follow-up to extract them into a
  format both platforms can read instead of duplicating.

Everything else — UI, view models, provider-CLI process handling, storage — is
implemented independently per platform. That's intentional: it's what keeps a Windows
contributor from ever needing to touch, or being blocked by, the Xcode project.

## Layout

```
windows/
  Coral.sln
  Coral/               # WinUI 3 app project (unpackaged for now — see PORT-PLAN.md Fase 9)
  Coral.Core/           # portable logic: Models + Services ported from the macOS app,
                        #   no WinUI/Windows App SDK dependency, so it's unit-testable
                        #   on its own (mirrors PORT-PLAN.md §2's "Generators/Models
                        #   are the highest-ROI port")
  Coral.Tests/           # xUnit, references Coral.Core only
  PORT-PLAN.md           # the plan: stack, requirements, phases, security review
  README.md              # this file
```

`Coral.Core` today ports:
- **Models**: `ClaudeModel`, `RoleKind`, `AgentRole`, `Strategy` (+ validation),
  `McpServer`, `StrategyLibrary` (all 15 built-in templates), `EvalSuite`/
  `EvalScenario`/`EvalRun`/`EvalRegression`, `ToolCheck`/`ToolCheckEngine`,
  `AIProvider`/`ProviderModel` — equivalents of the macOS app's `Models/*.swift`.
- **Services**: `ModelCatalog` — built-in per-provider model defaults, plus parsing
  the live `models.json` (the shared contract above) as an override;
  `ClaudeStreamParser`/`ChatEvent`/`AgentTodo` (Fase 3): a pure, tolerant parser
  for Claude Code's `--output-format stream-json` NDJSON lines (text, tool use,
  tool results, todos, denials, token usage, the final success/failure line) —
  never throws on malformed input, unknown lines just yield no events; and
  `BinaryResolver` (Fase 3): resolves the `claude`/`codex`/`gemini` binary — a
  fresh Windows design rather than a port (the macOS original shells out to an
  interactive login shell, which has no Windows equivalent), but the same shape:
  absolute path → PATH search → `%APPDATA%\npm` fallback, trying `.cmd`/`.exe`/
  `.bat` shims in that order; and `ClaudeRunArgs` (Fase 3): the CLI argument
  list for one headless turn (`--model`, `--resume`/`--session-id`, `--effort`,
  `--add-dir` per attached folder, `-p <prompt>`), in the exact order
  `Process.Start` will need it. All three are pure enough to unit-test on Linux
  via fakes/parameterized inputs; the actual process-spawning side that feeds
  `ClaudeStreamParser` real subprocess output isn't ported yet (see Status below).
- **Generators**: `AgentFileGenerator` (Strategy → `.claude/agents/*.md`),
  `ClaudeMdGenerator` (idempotent, marker-delimited CLAUDE.md merge),
  `LaunchCommandGenerator`, `WorkflowGenerator` (team topology → a runnable
  `.claude/workflows/*.mjs` dynamic workflow), `McpConfigGenerator` (`.mcp.json` +
  Gemini/Codex CLI equivalents), `GeneratedFile`/`FileDiff`/`LineDiff` (pure LCS diff
  for before-you-write previews), `StrategyWriter` (the first port that actually
  touches disk — `System.IO`: writes subagents, merges CLAUDE.md, seeds memory
  files, writes the dynamic workflow and MCP configs, and prunes only the
  managed-signature files that fell out of the current strategy, never hand-written
  ones), `CostEstimationHooks` (rough per-strategy $/token estimate, effort-scaled),
  and `MissionReport` (shareable run headline + Markdown report; the part that
  derives per-agent stats from the live activity timeline isn't ported yet — see
  Status below) — equivalents of `Generators/*.swift`.

`Coral.Tests` mirrors the matching macOS test files (`GeneratorTests`, `DiffTests`,
`ModelJSONTests`), plus `ModelCatalogTests` parses the *real* repo-root `models.json`
(copied into the test output at build time) rather than a fixture that can drift from it.

## First build

Requires **Windows + Visual Studio 2022** (17.11+) with the ".NET Desktop Development"
and "Windows application development" workloads, or the .NET 8 SDK + `dotnet` CLI. Open
`Coral.sln`, or from a terminal:

```
dotnet test windows/Coral.Tests/Coral.Tests.csproj -c Release
dotnet build windows/Coral/Coral.csproj -c Release -p:Platform=x64
```

> `Coral.Core`/`Coral.Tests` (plain net8.0, no WinUI dependency) build and pass **102/102**
> tests on Linux too — verified locally with the .NET 8 SDK, not just assumed. The `Coral`
> WinUI 3 app project needs the Windows App SDK/Windows 10 SDK and can only be built on
> Windows — `windows-tests.yml` (below) is its real check, and it passed on the Fase 1
> scaffold. If a NuGet pin or a WinUI 3 project-file setting turns out to be wrong as the
> app project grows, fix it in place rather than starting over.

## CI

`.github/workflows/windows-tests.yml` triggers on changes under `windows/**` and to
`models.json` (which `Coral.Tests` now reads as a fixture), runs on `windows-latest`, and
does two things: `dotnet test` on `Coral.Tests` (fast — `Coral.Core` has no WinUI
dependency), then `dotnet build` on the `Coral` app project to catch WinUI 3/Windows App
SDK restore problems separately from test failures. Independent of the macOS `tests.yml`
gate either direction: changes here never trigger a macOS run, and macOS-only changes
(anything outside `windows/**`, `models.json`, `skills.json`) never trigger this one.

## Status

**Fase 1 (scaffolding) done**; **Fase 2 (portable core) essentially done**;
**Fase 3 (process runner) started** — see `PORT-PLAN.md` §6 for the live
done/remaining checklist. Ported: the full "Strategy → subagent `.md` files +
CLAUDE.md + dynamic workflow + MCP configs, written to disk" path
(`AgentRole`/`Strategy`/validation + `Strategy.AutoFixed()` +
`AgentFileGenerator`/`ClaudeMdGenerator`/`LaunchCommandGenerator`/`WorkflowGenerator`/
`McpConfigGenerator`/`FileDiff`/`StrategyWriter`), all 15 built-in `StrategyLibrary`
templates, `EvalSuite`/`ToolCheck` (pure scoring/assertion logic — the judge and
command runner that produce their inputs are Services, not ported), cost estimation
(`CostEstimationHooks`), the shareable mission-report headline/Markdown
(`MissionReport`), `ModelCatalog`, the pure NDJSON stream parser
(`ClaudeStreamParser`, turns Claude Code's `--output-format stream-json` lines
into `ChatEvent`s), CLI binary resolution (`BinaryResolver`), and the CLI
argument-list builder (`ClaudeRunArgs`) — 102 xUnit tests, all passing
(including `TemplatesAreAllValid`, which iterates every template through
`Strategy.Validate()`, and `StrategyWriterTests`, which round-trips real writes
to a temp directory). Still not ported: `MissionReport.agentLines()` (needs the
not-yet-built chat/activity runtime — `ActivityStep`/`AgentNameMatcher` —
deferred to Fase 5 on purpose), the actual process spawn + stdout streaming that
ties `BinaryResolver`/`ClaudeRunArgs`/`ClaudeStreamParser` together
(`System.Diagnostics.Process`, no shell, ConPTY for login — needs Windows to
verify with confidence, so it lands as its own `windows-latest`-checked
increment), and everything else under `Services/` (git,
providers, auth, loops — the last one stays vetoed for human review per Fase 8). The
`Coral` WinUI 3 app project itself is still just the Fase 1 blank window — no UI
wired to any of this yet (Fase 5).
