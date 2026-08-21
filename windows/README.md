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
  `.bat` shims in that order; `ClaudeRunArgs` (Fase 3): the CLI argument
  list for one headless turn (`--model`, `--resume`/`--session-id`, `--effort`,
  `--add-dir` per attached folder, `-p <prompt>`), in the exact order
  `Process.Start` will need it; `CLIOneShotRunner` (Fase 3): the pure half of
  the multi-provider one-shot runner — per-provider command building
  (Claude/Codex/Gemini each get their own flags), ANSI-stripping/progress-line
  cleanup for providers with no structured stream (Codex/Gemini), auth-prompt/
  auth-failure detection by text, and token/cost estimation for CLIs that report
  no usage; and now `ClaudeRunner` (Fase 3): the real spawn, wired behind an
  `IProcessLauncher` abstraction so its streaming/watchdog/cancellation
  orchestration is unit-tested with a fake, while `RealProcessLauncher` (a thin
  `System.Diagnostics.Process` wrapper — no shell, `ArgumentList` only) is
  smoke-tested against a genuinely real process (`dotnet` itself) — **confirmed
  passing against the real `claude` CLI on a real Windows machine** (see Status);
  and `IPseudoConsoleLauncher`/`Win32PseudoConsoleLauncher` (Fase 3): the ConPTY
  primitive — raw P/Invoke against `kernel32` (no shell-out equivalent exists on
  Windows the way `openpty` does on macOS), same interface-plus-real-wrapper shape
  as the process launcher. **Confirmed passing on real Windows** (see Status) after
  finding and fixing two real bugs along the way (a hang, then `STATUS_DLL_INIT_FAILED`)
  — see "Testing the pieces that need a real Windows machine" below. And `ProviderAuth`
  (Fase 4): a cheap, no-subprocess check of whether `claude`/`codex`/`gemini`'s stored
  login still looks usable, reading only each CLI's own on-disk credentials file (never
  Credential Manager/Keychain — that would prompt at launch). Same `XUncached(...)`
  pure-plus-real-wrapper shape as `BinaryResolver`: `FreshnessUncached(provider,
  homeDirectory)` is fully unit-tested against a temp directory, `Freshness`/
  `VerifyAsync` wire in the real `%USERPROFILE%`. The rest of Fase 4 (Credential
  Manager wrapper, `Account`/`AuthProviderKind`, Google OAuth+PKCE with a loopback
  listener) is deliberately deferred — see `PORT-PLAN.md` §6/§9 for why.
  `ProviderInstaller` (Fase 6): npm-based CLI install and headless sign-in,
  reusing the same `IPseudoConsoleLauncher` primitive `ProviderAuth`'s
  neighbor above relies on — one hidden pseudo-console per login, output
  streamed as `InstallEvent`s/`ConnectEvent`s (a closed `record` hierarchy,
  same shape as `ChatEvent`), with a `Channel<InstallEvent>` handling the one
  case with two concurrent producers (Gemini's TUI-nudge-and-creds-mtime
  success race). Unit-tested against a new `FakePseudoConsoleLauncher`; not
  yet run against real Windows/npm/CLI logins — see Status.
  `CodeGit` (Fase 7's first piece): the pure diff/status parsers (`Parse` —
  unified diff → renderable lines with old/new line numbers; `ParseChangedFiles`
  — `git diff --numstat` + `git status --porcelain -z` → a changed-file list,
  NUL-separated so paths with spaces/non-ASCII survive; `ParseShortstat`;
  `RepoName`) plus the read-only real-git operations (`DiffAsync`,
  `CurrentBranchAsync`, `BranchStatAsync`, `ChangedFilesAsync`,
  `HasUncommittedChangesAsync`), all via the existing `IProcessLauncher` — git
  is a one-shot subprocess, no new Windows primitive needed. Reads stdout and
  stderr concurrently rather than merging them into one pipe like the Swift
  original does, to avoid a full-stderr-buffer deadlock on a chatty command.
  Now also the git-panel WRITE operations on an existing repo
  (stage/unstage/revert/staged-files/commit/commit-staged/push/create-branch/
  branches/checkout) AND `CloneAsync` (repo lifecycle, with injectable
  folder-dedup so it's just as fake-testable as everything else) — the same
  fakes-based testing applies just as well to all of these as to the
  read-only half, so "no UI consumes it yet" wasn't a strong enough reason
  to leave any of them unported. Still deliberately deferred: every worktree
  operation (loop isolation — Fase 8's explicitly human-reviewed zone) — see
  Status.
  `OneShotProcess` (new, internal) factors out the "launch, drain stdout/
  stderr concurrently, wait for the exit code" runner `CodeGit` needed —
  `GitHubCLI` (below) needs the identical shape for `gh`, so it moved out
  rather than being duplicated a second time; `CodeGit`'s own tests still
  pass unchanged after the refactor. `GitHubCLI` (Fase 7): the Code Mode
  one-tap PR flow — `IsInstalled`, `IsAuthenticatedAsync`, `CreatePRAsync`
  (+ the pure `LastHttpsLine`, extracting the PR url gh prints), `PrInfoAsync`
  (+ the pure, tolerant `ParsePRInfo`, never throwing on malformed/missing
  JSON fields — same contract as `ClaudeStreamParser`), and `MergePRAsync`.
  Also `ListReposAsync`/`RepoRef` and `CreateRepoAsync` — browsing/creating
  GitHub repos, same injectable-folder-dedup pattern as `CodeGit.CloneAsync`.
  Deliberately not ported: `searchCommunitySkills` (a different feature area
  — skills catalog discovery — with meaningfully more complex logic that
  deserves its own scoped pass).
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
  and `MissionReport` (shareable run headline + Markdown report, including
  `AgentLines`, which derives per-agent stats from the live activity
  timeline) — equivalents of `Generators/*.swift`.
- **ViewModels**: `ChatViewModel`/`ChatMessage`/`ActivityStep` (Fase 5) — a minimal
  port of `ChatViewModel.swift`'s plain single-provider `-p` path only (no "Ask"
  live-permission mode, no cross-provider `MetaOrchestrator`, no persisted turn
  history — each is its own much larger feature). Lives in `Coral.Core`, not the
  WinUI project: `ObservableCollection`/hand-rolled `INotifyPropertyChanged`
  (`ObservableObject`) have no WinUI dependency, so the ViewModel is unit-tested
  the same way as everything here, with an injected `IProcessLauncher`. Also
  carries `CommandLog`/`CommandRun` (Code Mode's terminal panel): pairs Bash's
  `CommandStarted`/`CommandOutput` events by tool_use id via a `_pendingCommands`
  dictionary (reset per turn, like `_activeSubagent`), trims long output with a
  `Trimmed(string, limit: 12_000)` head+tail port of the Swift original's
  `trimmed(_:limit:)`. Unlike Swift, `CommandLog` accumulates for the whole
  session rather than clearing per turn — matching the convention `Activity`
  already established in this port, for internal consistency. 15
  tests cover the delta/full-text dedup, activity mapping, invariant-culture cost
  formatting, the resumed-session-missing retry, cancellation, and the
  command-log pairing/trimming. All real
  behavior ported from the Swift original, not reinvented. **Confirmed working
  end to end on real Windows** — see Status. And `ConnectViewModel` (Fase 6):
  drives one provider's "Connect" flow — install-if-missing, then sign in —
  for a UI, consuming `ProviderInstaller.Connect()`'s event stream into
  observable `Phase`/`LogLines`/`NeedsCode`/`StatusMessage` state, with an
  injectable `openUrl` delegate (default: `Process.Start(UseShellExecute:
  true)`) so opening the login link is still unit-testable. 7 tests; **not
  yet confirmed on real Windows** — see Status. And `GitPanelViewModel` (Fase
  7): Code Mode's git panel — changed files, the selected file's diff,
  stage/unstage/revert, commit (all or just staged), push, branch
  create/checkout — wrapping `CodeGit` the same interface-plus-fake way as
  the others. Unlike `ChatViewModel`/`ConnectViewModel`, there's no 1:1 Swift
  type behind it: `CodeModeView.swift` keeps this state as plain `@State` on
  the View itself, SwiftUI's norm, not a separate ViewModel class — so this
  is a fresh design in the same shape this port already established, not a
  translation.
  10 tests; **not yet confirmed on real
  Windows** — see Status. And `PullRequestViewModel` (Fase 7): the one-tap PR
  flow, kept as its own ViewModel rather than folded into
  `GitPanelViewModel` — same one-ViewModel-one-concern separation as
  `ChatViewModel`/`ConnectViewModel`. Wraps `GitHubCLI.PrInfoAsync`/
  `CreatePRAsync`/`MergePRAsync`; the branch it acts on is passed in by the
  caller rather than owned here, since `GitPanelViewModel` is what tracks
  the active branch. Also owns the opt-in `AutoPr` toggle (persisted via
  the new `AppSettings`, see the 2026-08-11 update below). 10 tests. And
  `RepoPickerViewModel` (Fase 7): the repo
  picker that had been the standing "Fase 5 leftover" for a while — browse
  the signed-in user's GitHub repos, clone one by URL, or create a new one,
  wrapping `GitHubCLI.ListReposAsync`/`CodeGit.CloneAsync`/`GitHubCLI.
  CreateRepoAsync`. Deliberately doesn't try to hot-swap an already-open
  session's repo — on success it hands `ResultRepoPath` to the caller,
  which opens a fresh `MainWindow` pointed at it rather than mutating the
  current page's `OneTime`-bound `RepoPath`. 8 tests. `CodeModePage` now also
  renders a collapsible terminal panel (shell commands + output), fed by
  `ChatViewModel.CommandLog` — matching `CodeModeView.swift`, which takes the
  SAME `ChatViewModel` as the main chat rather than a Code-Mode-local copy;
  `CodeModeWindow`/`CodeModePage` both gained a `chatViewModel` constructor
  parameter for this, threaded from `MainPage.OnCodeModeClick`. **All of Fase 6/7's
  UI — `ConnectViewModel`'s flyout, `CodeModePage`, the "Open Repo"
  flyout — is now CONFIRMED compiling against the real Windows App SDK on
  `windows-latest` CI** (2026-08-07, see Status) — not yet run on a real
  Windows machine, but no longer "written and hoped."

`Coral.Tests` mirrors the matching macOS test files (`GeneratorTests`, `DiffTests`,
`ModelJSONTests`), plus `ModelCatalogTests` parses the *real* repo-root `models.json`
(copied into the test output at build time) rather than a fixture that can drift from it.

## First build

Requires **Windows + Visual Studio 2022** (17.11+) with the ".NET Desktop Development"
and "Windows application development" workloads, or the .NET 8 SDK + `dotnet` CLI. Open
`Coral.sln`, or from a terminal:

```
dotnet test windows/Coral.Tests/Coral.Tests.csproj -c Release --filter "Category!=Manual"
dotnet build windows/Coral/Coral.csproj -c Release -p:Platform=x64
```

> `Coral.Core`/`Coral.Tests` (plain net8.0, no WinUI dependency) build and pass **316/316**
> tests on Linux too — verified locally with the .NET 8 SDK, not just assumed. (The
> `--filter` excludes two more tests, `ManualClaudeRunnerSmokeTest` and
> `ManualPseudoConsoleSmokeTest`, that need a real, logged-in `claude` CLI and real
> Windows respectively — see "Testing the pieces that need a real Windows machine"
> below.) The `Coral`
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
CI excludes `Category=Manual` (see below) — those tests need a real, logged-in `claude`
CLI, which the runner doesn't have.

## Testing the pieces that need a real Windows machine

Some of `Coral.Core` can only be genuinely verified on Windows, with the real CLIs —
`dotnet test` here and on `windows-latest` proves it compiles and the *logic* is
right, not that it behaves correctly against the real thing. Two tests exist for
exactly this gap, both tagged `Category=Manual` and excluded from the normal
suite/CI:

**`ManualClaudeRunnerSmokeTest`** — ✅ confirmed passing on real Windows
(2026-08-06, see `PORT-PLAN.md` §10). Install and log in first:

```
npm install -g @anthropic-ai/claude-code
claude   # sign in once, interactively, to your plan
```

Then:

```
dotnet test windows/Coral.Tests/Coral.Tests.csproj -c Release --filter "FullyQualifiedName~ManualClaudeRunnerSmokeTest"
```

It resolves `claude` via `BinaryResolver`, sends it a one-word prompt through
`ClaudeRunner.Stream()`, and prints every `ChatEvent` it streams back — the whole
Fase 3 chain (`BinaryResolver` → `ClaudeRunArgs` → `RealProcessLauncher` →
`ClaudeStreamParser`) end to end. A `[FAILED] ...` line or a thrown assertion means
something in that chain needs fixing; a normal run ends with `[usage] ... tokens`
and `[finished]`. Point `CORAL_MANUAL_REPO_PATH` at a specific folder to run
`claude` there instead of the current directory.

**`ManualPseudoConsoleSmokeTest`** — ✅ confirmed passing on real Windows
(2026-08-06, see `PORT-PLAN.md` §10). ConPTY (`Win32PseudoConsoleLauncher`) is raw
P/Invoke against `kernel32` with no cross-platform equivalent, so unlike
`ClaudeRunner`'s plumbing, this had no Linux-side signal to fall back on. Two real
bugs surfaced and got fixed along the way: a hang (the ConPTY output pipe doesn't EOF
on its own when the child exits) and `STATUS_DLL_INIT_FAILED` (the
`PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` attribute was being set to a pointer-to-the-handle
instead of the handle value itself) — full writeup in `PORT-PLAN.md` §10. Run it:

```
dotnet test windows/Coral.Tests/Coral.Tests.csproj -c Release --filter "FullyQualifiedName~ManualPseudoConsoleSmokeTest"
```

It spawns `cmd.exe /c echo hello-from-conpty` attached to a real pseudo-console and
checks the echoed text comes back through it. **If it ever seems to fail this way when
run manually inside Windows Terminal**, re-run it from a legacy `conhost` window
(Win+R → `cmd`) before assuming it's a regression — Windows Terminal can "pass through"
a nested ConPTY session's rendering directly to itself instead of relaying it through
this process's pipe, which looks identical to a real failure but isn't one (doesn't
affect the real app, which is never launched from a terminal). See the XML doc on the
test and `PORT-PLAN.md` §10 for the full story.

**`ManualProviderInstallerSmokeTest`** — not yet run on real Windows (see
Status below). Fase 6's `ProviderInstaller` has only ever been exercised
against fakes; this is the first real check against actual npm/CLI. Two
independent pieces, split because they carry very different risk:

```
dotnet test windows/Coral.Tests/Coral.Tests.csproj -c Release --filter "FullyQualifiedName~InstallsTheClaudeCliForReal"
```

`InstallsTheClaudeCliForReal` runs `npm install -g @anthropic-ai/claude-code`
for real and checks it reports `Finished`. Safe to run unattended — npm
install is idempotent, so if `claude` is already installed (likely, since
`ManualClaudeRunnerSmokeTest` needs it too) this just confirms npm reports
it up to date.

```
CORAL_MANUAL_RUN_SIGNIN=1 dotnet test windows/Coral.Tests/Coral.Tests.csproj -c Release --filter "FullyQualifiedName~SignsInForReal"
```

`SignsInForReal` runs `claude auth login --claudeai` for real, through a
hidden pseudo-console — this starts a REAL browser OAuth flow and, if
completed, **replaces your machine's current Claude login**. It's gated
behind the `CORAL_MANUAL_RUN_SIGNIN=1` env var on purpose (skips with a
warning otherwise, even when the test itself is selected), and it's
interactive: it prints the login URL and, if Claude asks for a pasted
browser code, reads one from stdin.

## Status

**Fase 1 (scaffolding) done**; **Fase 2 (portable core) done**;
**Fase 3 (process runner) done**; **Fase 4 (secretos + auth) started** (scope
deliberately cut, see below); **Fase 5 (Chat MVP) done — confirmed end to end
on real Windows**; **Fase 6 (Instalación de CLIs) done — confirmed end to
end on real Windows** (Claude only — "Connect Codex"/"Connect Gemini"
buttons were added 2026-08-21, reusing the same `ConnectViewModel`, but
not yet verified against those two CLIs for real) — see `PORT-PLAN.md` §6 for the live
done/remaining checklist. Ported: the full "Strategy → subagent `.md` files +
CLAUDE.md + dynamic workflow + MCP configs, written to disk" path
(`AgentRole`/`Strategy`/validation + `Strategy.AutoFixed()` +
`AgentFileGenerator`/`ClaudeMdGenerator`/`LaunchCommandGenerator`/`WorkflowGenerator`/
`McpConfigGenerator`/`FileDiff`/`StrategyWriter`), all 15 built-in `StrategyLibrary`
templates, `EvalSuite`/`ToolCheck` (pure scoring/assertion logic — the judge and
command runner that produce their inputs are Services, not ported), cost estimation
(`CostEstimationHooks`), the shareable mission-report headline/Markdown/
per-agent stats (`MissionReport`, including `AgentLines` — see below),
`AgentNameMatcher`, `ModelCatalog`, the pure NDJSON stream parser
(`ClaudeStreamParser`, turns Claude Code's `--output-format stream-json` lines
into `ChatEvent`s), CLI binary resolution (`BinaryResolver`), the CLI
argument-list builder (`ClaudeRunArgs`), the pure half of the multi-provider
one-shot runner (`CLIOneShotRunner`: per-provider command building,
ANSI/progress-line cleanup, auth-prompt/failure detection, token/cost
estimation), the real spawn (`ClaudeRunner.Stream()`,
`IProcessLauncher`/`RealProcessLauncher`), the ConPTY primitive
(`IPseudoConsoleLauncher`/`Win32PseudoConsoleLauncher`), `ProviderAuth`
(login-freshness check for `claude`/`codex`/`gemini`, reading only their own
on-disk credentials files — the first piece of Fase 4), and the minimal
`ChatViewModel` (Fase 5, single-provider `-p` path only),
`ProviderInstaller`/`ConnectViewModel` (Fase 6), and `CodeGit`/
`GitPanelViewModel`/`GitHubCLI`/`PullRequestViewModel`/`RepoPickerViewModel`/`ShipFlow`/`AppSettings` (Fase 7 — see below), and
`StrategyPickerViewModel` (P0 item 2, Phase 1), `StrategyGenerator`/`AdvisorEngine`/`AdvisorViewModel`
(P0 item 2, Phase 3), and `StrategyEditorViewModel` (P0 item 2, Phase 2 — see below) — 345 automated
xUnit tests, all passing (including `TemplatesAreAllValid`, which iterates
every template through `Strategy.Validate()`, `StrategyWriterTests`, which
round-trips real writes to a temp directory, `RealProcessLauncherTests`, which
spawns a genuinely real process to smoke-test the no-shell
`Process.Start`/async-stdout/exit-code/`Kill()` plumbing, `LocaleRegressionTests`,
added after the first real-Windows run, `ProviderAuthTests`,
`ChatViewModelTests`, `AgentNameMatcherTests`, `ProviderInstallerTests`
(against a new `FakePseudoConsoleLauncher`, mirroring `FakeProcessLauncher`),
`ConnectViewModelTests`, `CodeGitTests`, `GitPanelViewModelTests`,
`GitHubCLITests`, `AppSettingsTests`, `StrategyPickerViewModelTests`,
`AdvisorEngineTests`, `AdvisorViewModelTests`, `StrategyEditorViewModelTests`, and the `MissionReport.AgentLines` cases) — plus 4 manual
tests excluded from that count and from CI
(see "Testing the pieces that need a real Windows machine").
Fase 2's last loose end (`MissionReport.agentLines()`, which needed
`ActivityStep`/`AgentNameMatcher`) is now closed — `ActivityStep` picked up
`IsDelegation`/`Agent` fields once `ChatViewModel` existed to populate them,
and `ChatViewModel` now tracks which subagent is active (reset per turn) to
attribute each step correctly, matching the Swift original. Still not
ported: the rest of Fase 4 (Credential Manager wrapper, `Account`/
`AuthProviderKind`, Google
OAuth+PKCE with a loopback listener — deliberately deferred, see `PORT-PLAN.md`
§6/§9: it only exists on macOS to gate CloudKit sync, itself a Windows
non-goal), the rest of Fase 5 (repo picker, model/effort/permission-mode
settings, "Ask" live-permission mode, the cross-provider `MetaOrchestrator`,
persisted turn history — each its own follow-up), and the rest of Fase 6
(running the "Connect Claude" flow for real against npm and a real CLI login
on Windows — the service layer and its ViewModel/UI are built and
unit-tested, see Status above, but nobody has clicked the button yet).
Everything else under `Services/` (git, loops — the last one stays vetoed
for human review per Fase 8) is still unported.

**The `Coral` WinUI 3 app project's first real chat UI is confirmed working
end to end on real Windows** — prompt box, transcript, activity panel,
Send/Stop, in `MainPage.xaml`/`.xaml.cs` hosted by a minimal `MainWindow`.
Getting there took five real bugs, none of which this sandbox could catch (no
Windows App SDK here at all): (1) `x:Bind` bindings directly on `MainWindow`
failed to compile — WinUI 3's `Window` isn't a `FrameworkElement`, unlike
UWP's `Page` — fixed by moving the actual content/bindings into `MainPage`,
the standard WinUI 3 pattern; (2) a blank window at runtime —
`BoolNegationConverter` lived in `Border.Resources`, but x:Bind's generated
converter lookup expects it in `Page.Resources`, so `Bindings.Initialize()`
threw before anything rendered; (3) mojibake in any accented reply —
`RealProcessLauncher` didn't set `StandardOutputEncoding`, so .NET decoded
the CLI's UTF-8 output using the console's active code page instead; (4)
reply text couldn't be selected/copied — `TextBlock.IsTextSelectionEnabled`
defaults to `false`; (5) the transcript didn't keep following the bottom
while a reply streamed in — `ScrollIntoView` only guarantees an item is
visible once, it doesn't track a still-growing item's bottom edge, so this
needed the ListView's actual `ScrollViewer` and `ChangeView` instead. All
five found via the founder running it for real (mostly in Visual Studio's
debugger), not by review. See `PORT-PLAN.md` §10 for the full story.

**`ClaudeRunner` has now run against the real `claude` CLI, on a real Windows
machine** (`ManualClaudeRunnerSmokeTest` — see "Testing the pieces that need a
real Windows machine" above), and passed on the first try: `BinaryResolver`
found `claude` on PATH, the no-shell spawn worked, and the stream parsed
correctly end to end. That same run surfaced one real bug neither this sandbox
nor `windows-latest` could have caught: `$`/token formatting (`MissionReport`,
`CostEstimationHooks`, the smoke test's own diagnostic output) used
`:F1`/`:F2`/`:F4` string interpolation, which is culture-sensitive — on a
Spanish-locale Windows machine `$0.83` rendered as `$0,83`. Fixed by forcing
`CultureInfo.InvariantCulture` everywhere a dollar/token figure is formatted,
with `LocaleRegressionTests` added to catch a repeat by pinning
`CurrentCulture` to `es-ES` for the duration of each case — no non-English CI
runner needed to guard against it going forward.

**The ConPTY primitive (`Win32PseudoConsoleLauncher`) has since also been
confirmed on real Windows** (`ManualPseudoConsoleSmokeTest`), closing out
Fase 3 completely. Getting there took two more real bugs: a hang (the ConPTY
output pipe doesn't EOF on its own when the child process exits — conhost
keeps its write handle open until `ClosePseudoConsole` is called explicitly)
and `STATUS_DLL_INIT_FAILED` (`UpdateProcThreadAttribute`'s `lpValue` was a
pointer to a heap copy of the `HPCON` handle instead of the handle value
itself). Full story, including a red herring that turned out to be a Windows
Terminal ConPTY-passthrough artifact rather than a bug, in `PORT-PLAN.md` §10.

**Fase 6 (Instalación de CLIs) is done — confirmed end to end on real
Windows.** `ProviderInstaller.cs` covers npm-based CLI
install, headless sign-in (reusing the now-confirmed ConPTY primitive the
same way the Swift original reuses `openpty`), Gemini's
TUI-nudge-and-creds-mtime success detection, Antigravity-migration detection,
and the unified install-then-sign-in `Connect()` flow. `ConnectViewModel.cs`
consumes that event stream for a UI (same pattern as `ChatViewModel`), and
`MainPage.xaml` now has a "Connect Claude" button/flyout wired to it — 35 new
tests between the two (195 automated total at the time). Deliberately deferred, each for
being a platform redesign rather than a missing translation: an automated
Node.js bootstrap (Swift's `installNode()` shells out to Homebrew; Windows
has no single trusted equivalent verified in this port, so it falls back to
sending the user to nodejs.org, the same terminal state Swift itself uses
when Homebrew isn't present), and the Terminal.app/AppleScript fallback
(already dead code in Swift today — no provider needs a visible terminal
anymore). Opening the sign-in URL in a browser IS wired up (`ConnectViewModel`'s
injectable `openUrl`, defaulting to `Process.Start(UseShellExecute: true)`).
`ManualProviderInstallerSmokeTest`
(see "Testing the pieces that need a real Windows machine" above) remains
available as an automated equivalent, split into a safe-to-run-unattended
install check and an opt-in (`CORAL_MANUAL_RUN_SIGNIN=1`), interactive
sign-in check that's honest about replacing your machine's Claude login when
run. See `PORT-PLAN.md` §9/§10 for the full breakdown.

**Update (2026-08-07): clicking "Connect Claude" for real surfaced two
genuine UI bugs, both fixed, then a clean real login confirmed the phase
closed.** The `Flyout` auto-dismisses
(WinUI3's default "light dismiss" on any outside click or window-focus
change) the instant the sign-in browser window opens and steals focus — and
`Closed` was wired straight to `ConnectViewModel.CancelConnect()`, so the
whole connect flow aborted right as the user was sent to the browser.
`ProviderConnectSheet.swift`'s modal `.sheet` never had this problem (macOS
sheets don't auto-dismiss on focus loss). First fix — no longer cancelling on
`Closed` — was necessary but incomplete: the Flyout still visually
light-dismissed when the browser stole focus, so the "paste the code" box
kept vanishing before the user could reach it (confirmed the hard way — the
founder ended up pasting the auth code into the main chat prompt box
instead, since the login one wasn't visible). Second fix: a `Closing` handler
(cancellable, unlike `Closed`) that blocks the light-dismiss outright while
`ConnectViewModel.IsConnecting` is true, bounded by `RunSignInAsync`'s
existing 150s sign-in timeout. A stopgap header badge ("⚠ Paste the code —
click Connect Claude", visible whenever `NeedsCode` is true, outside the
Flyout) was added alongside it in case that still wasn't enough. It was
enough: the next real run showed the badge, kept the flyout open with the
code box visible, and finished with "Connected." — `claude auth login`
resolved via its local loopback listener before a manual paste was even
needed, closing the loop on a real npm install → real browser OAuth → real
signed-in CLI. One coverage gap remains, non-blocking: `SubmitCodeAsync`/
`LoginInput.SubmitAsync` (the actual manual-code-paste submission) has never
fired against a real CLI waiting for it — both real attempts resolved via
loopback first. Still covered by fake-based tests only. See `PORT-PLAN.md`
§10 for the full writeup, and below for a partial close of this gap.

**Fase 7 (Code mode) started: the whole service layer — `CodeGit` (all
real-git operations, read/write/clone) and `GitHubCLI` (PR flow + repo
browse/create) — plus all three ViewModels (`GitPanelViewModel`,
`PullRequestViewModel`, `RepoPickerViewModel`) are ported and unit-tested
(278 automated total), and Code Mode now has a first real UI: `CodeModePage`
in its own `CodeModeWindow`, opened from a new "Code Mode" button in
`MainPage`, plus an "Open Repo" flyout on `MainPage` itself (the repo
picker that had been a standing Fase 5 leftover).** Same scope discipline as
Fases 4/6: the diff/changed-files parsers, the read-only real-git calls
(current branch, branch stat, changed files, has-uncommitted-changes), the
write actions a git panel needs (stage/unstage/revert/staged-files/commit/
commit-staged/push/create-branch/branches/checkout), and `CloneAsync` are
all done, reusing the existing `IProcessLauncher` since git is a plain
one-shot subprocess with no new Windows primitive to build — the initial
reasoning for deferring each of these in turn ("no UI consumes it yet")
kept not holding up: a fake proves the argument construction and exit-code
handling exactly as well regardless of who calls it, so each got un-deferred
once that was noticed. `GitPanelViewModel` then wraps the git half into the
actual git-panel state a UI would bind to (changed files, selected file's
diff, staging, commit, push, branches) — a fresh design, since
`CodeModeView.swift` keeps this state as plain `@State` on the View rather
than a separate ViewModel type. `GitHubCLI` adds the GitHub half: install/
auth checks, opening/reading/merging a PR, and now also browsing/creating
repos (`ListReposAsync`/`RepoRef`/`CreateRepoAsync`) — the same
one-shot-subprocess shape as `CodeGit`, sharing the new `OneShotProcess`
runner both now use instead of duplicating it. `PullRequestViewModel` wraps
the PR half of `GitHubCLI` as its own ViewModel — deliberately kept separate
from `GitPanelViewModel` rather than merged in, the same one-ViewModel-
one-concern split as `ChatViewModel`/`ConnectViewModel`. Still deliberately
deferred: `searchCommunitySkills` (a different feature area — skills catalog
discovery — with meaningfully more complex logic deserving its own scoped
pass), Auto-PR, and every worktree operation (used only
for loop isolation, which is Fase 8's zone requiring human review of the
diff, not just green tests — porting worktree logic here would sidestep
that gate).

The terminal panel followed: `ChatViewModel` gained `CommandLog`/`CommandRun`
(pairing Bash's `CommandStarted`/`CommandOutput` events by tool_use id, with
a `Trimmed` head+tail truncation port of Swift's `trimmed(_:limit:)`), and
`CodeModePage` renders it as a collapsible panel — no new C# service needed,
since `ClaudeStreamParser` already emitted both events from Fase 3, the
current minimal `ChatViewModel` just wasn't consuming `CommandOutput` yet.
`CodeModeWindow`/`CodeModePage` take the SAME `ChatViewModel` instance as
`MainPage`'s chat (matching `CodeModeView.swift`, which does the same) rather
than constructing a Code-Mode-local one, so the terminal reflects the live
session.

Then the one-tap "Commit + PR" button — `CodeModeView.swift`'s
`commitAndPR(auto:)` engine, minus its opt-in Auto-PR auto-trigger (see
below). New `ShipFlow` service (a peer to `CodeGit`/`GitHubCLI`, not another
ViewModel): commit (everything, or just what's staged, depending on whether
anything's staged) → push → open a PR, or skip opening one and just report
"updated" if the branch already had one open (a second push alone brings
gh's existing PR up to date; calling `gh pr create` again would just error).
Tolerates a "nothing to commit" failure — there may already be local commits
to ship. Takes an `IProcessLauncher` directly rather than depending on
either ViewModel, so it stays fake-testable on its own without coupling
`GitPanelViewModel` to `PullRequestViewModel` — a coupling Fase 7 has kept
deliberately absent throughout. `PullRequestViewModel.ShipAsync` is the thin
wrapper that calls it and updates `Info`/`Title`/`Body`/`StatusMessage`;
`ChatViewModel` gained `DraftCommitMessage()`/`DraftPrBody()` (ports of
`draftMessage()`/`prBody()`) so the button has something to fall back to
when the commit box/PR title/body are blank. Deliberately NOT wired: the
opt-in Auto-PR toggle that fires this automatically when a run finishes —
Swift persists it with `@AppStorage`, and this port has no settings-storage
mechanism yet; wiring the auto-trigger would also mean reaching into
`ChatViewModel`'s turn lifecycle from Code Mode, a boundary Fase 5 confirmed
working that this pass isn't willing to risk without a human running it.

`CodeModePage` is a fresh design (not a port — `CodeModeView.swift` is 900
lines including the Auto-PR toggle, which this pass still excludes): changed
files with per-file Stage/Revert on the left, the
selected file's diff on the right (a `+`/`-`/`@@` glyph gutter via the new
`DiffLineKindToGlyphConverter`), a branch bar (switch via `ComboBox`, create
via a `Flyout`), a "Pull Request" `Flyout` (same pattern as MainPage's
"Connect Claude") wired to `PullRequestViewModel`, a commit message box
with Commit/Push/**Commit + PR**, and a collapsible terminal panel across
the bottom
(command + trimmed output, toggled via a plain code-behind click handler —
no ViewModel-bound bool needed for pure UI state). Deliberately in its **own window** (`CodeModeWindow`, same
thin-shell pattern as `MainWindow`/`MainPage`) rather than embedded in
`MainPage`, so this UI can't put the already-confirmed Fase 5/6 chat flow at
risk. The "Open Repo" flyout on `MainPage` (`RepoPickerViewModel`) browses/
clones/creates a repo and, on success, opens a **new** `MainWindow` pointed
at it — `MainWindow`/`MainPage` now both take an optional `repoPath`
parameter for this — rather than trying to hot-swap the current page's
`OneTime`-bound `RepoPath` in place.

**Milestone: as of 2026-08-07 (~05:26 UTC), all of this is CONFIRMED
compiling for real on `windows-latest` CI**, including the "Build the WinUI
3 app (Coral)" step — the first time that's happened since Fase 6/7 started;
every earlier attempt that day hit a real, hours-long GitHub Actions
infrastructure incident before a runner ever picked it up (see
\ §10 for the full timeline). The one real (non-infra) CI failure that did
surface along the way wasn't a code regression: `ClaudeRunnerTests.
ActivityResetsTheWatchdogSoALongQuietRunSurvives` had a timing margin too
tight for a loaded runner (2.5x, 40ms delay vs. a 100ms watchdog) — fixed by
widening it to 20x, verified with 5 consecutive local runs before repushing.
**Update (2026-08-10): now run on a real Windows machine, three real UI bugs
found and fixed** — the same gap Fase 5's UI had before five real bugs
turned up despite a clean compile, so "compiles in CI" was never a
sufficient signal here, only a necessary one. Push and the terminal panel
worked as designed on the first try. Three didn't: (1) the "New" branch
flyout gave no success feedback and never closed itself —
`GitPanelViewModel.CreateBranchAsync` only set `StatusMessage` on failure,
so a stale error from an earlier attempt stayed on screen even after a
later success; now returns `bool` and sets a success message, and
`CodeModePage` closes the flyout + clears the textbox only on success
(same fix applied to `CheckoutAsync`'s identical silent-success gap); (2)
the branch `ComboBox` highlighted whichever branch happened to be first in
the list, not the actually-active one — `GitPanelViewModel.RefreshAsync()`
assigned `Branch` before repopulating `Branches`, so the `x:Bind`
`SelectedItem`'s `OneWay` push landed while `Branches` didn't contain that
value yet, and WinUI3 fell back to auto-selecting index 0 once items were
added, never re-evaluating the binding; fixed by reordering `RefreshAsync`
to populate `Branches` first — no regression test possible here, since
`Coral.Core`/`Coral.Tests` have no WinUI dependency to observe a real
`ComboBox` binding; (3) Enter in the branch-name box didn't trigger
"Create" — no `KeyDown` handler was wired, unlike `MainPage`'s prompt box.
3 new tests, 296 total. Reconfirmed visually afterward: the `ComboBox` now
highlights the real current branch, and Enter creates one.

**Update, same day: a fourth real bug — reopening the same repo from "Open
Repo" re-cloned it into a new "-2"/"-3" folder every time**, which in turn
made branches from earlier sessions look like they'd vanished (they
hadn't — they were sitting in the earlier, now-orphaned clone folder the
app no longer pointed at). `MainPage.OnRepoSelectionChanged` always called
`RepoPickerViewModel.CloneAsync`, which uses `CodeGit.CloneAsync`'s
folder-dedup logic — correct for the "clone by URL" box, where a second
clone can be a real intent, but wrong for re-selecting an already-cloned
repo from the browse list, where the intent is always "open this one." New
`RepoPickerViewModel.OpenOrCloneAsync(RepoRef, parentDir)` checks whether
`parentDir/repo-name` already exists first — if so, opens it directly with
no git operation at all; only clones if it isn't there yet. The "clone by
URL" box is untouched — still dedups on purpose. Existing orphaned "-2"/"-3"
folders aren't cleaned up automatically (their contents/branches are still
there, just no longer pointed at); this only stops new ones from being
created. 2 new tests, 298 total. See `PORT-PLAN.md` §10 for the full
writeup.

**Update, same day: three more findings from testing the PR flow for
real** — one a real bug in Fase 5's own code, not Fase 7's, just surfaced by
this testing. (1) **`ChatViewModel.cs` ran with `permission-mode "default"`
instead of `"acceptEdits"`.** Asked to have the agent create a branch, the
requested `git checkout -b` sat forever reporting "This command requires
approval" — `"default"` needs interactive approval for most tool calls, and
a headless `-p` run has no channel to give it. `ChatViewModel.swift`'s own
default is `"acceptEdits"`; the port had `"default"` hardcoded, a real
divergence rather than a deliberate choice. Fixed the literal, added a test
asserting the actual `--permission-mode` argument passed to the process —
299 total.

**`"acceptEdits"` alone turned out not to be enough — a follow-up fix
minutes later.** The founder tried it for real: asking the chat to create a
branch and commit a README change, the `git add`/`git commit` calls got
denied again, and the model itself asked the user in the chat to "accept a
dialog" that doesn't exist in this UI — a reasonable thing for it to assume,
since it has no way to know this port has no live approval channel. Root
cause, found by reading `ChatViewModel.swift` all the way through this
time: `"acceptEdits"` CAN still deny tool calls — Swift's answer to that is
a live "Ask" permission UI (`pendingPermission`/`respondPermission`/
`retryAllowingAll`) that resumes the denied turn with `"bypassPermissions"`
once the user approves. That UI has been out of scope for this port from
the start (see Status below) — but without it, a denial in a one-shot
headless `-p` run is a dead end, not a recoverable prompt, since there's no
mid-turn channel to ask through. Fixed by running with `"bypassPermissions"`
directly from the start of every turn — the same thing `retryAllowingAll`
does in Swift, just unconditional here since the interactive alternative
doesn't exist. The earlier test was updated (not added) to assert
`"bypassPermissions"` instead — still 299 total. If "Ask" mode is ever
ported for real, this default is worth revisiting. (2) **The "Pull Request" flyout had the same missing
light-dismiss guard "Connect Claude" already needed** (see the Fase 6
update above) — switching windows while drafting a title/description
closed it (the text survived, since it lives on the ViewModel, but the
popup itself vanished). Unlike Connect Claude there's no `IsConnecting`-
style busy window to key a conditional block off, so this one blocks
unconditionally and pairs it with an explicit ✕ close button. Deliberately
doesn't auto-close on a successful create/merge, unlike the branch-create
flyout — this one shows `Info.Title`/`.State` updating in place, and
snapping it shut would rob the user of that confirmation. (3) **"Commit +
PR" gave no visible feedback at all** — `PullRequestViewModel.ShipAsync`
does set `StatusMessage`, but the only `TextBlock` bound to it lived inside
the separate "Pull Request" flyout, which "Commit + PR" never opens. Fixed
by adding the same `TextBlock` next to the "Commit + PR" button itself.

**Update (2026-08-11): "Create PR"/"Merge" tested for real — worked — plus
one serious bug found and fixed.** The founder isolated each button on its
own branch and clicked them directly: PR opened, then merged, confirmed
on GitHub. Along the way, clicking "Connect Claude" while already signed
in re-ran the full install+signin flow — opened a second browser window
and left the app looking stuck (the `Closing`-blocks-light-dismiss fix from
before was doing exactly its job, just for an operation that should never
have started). Two combined causes: `ConnectViewModel` never checked
`ProviderAuth.Freshness` (ported in Fase 4 for exactly this, never wired to
this button) before running install+signin, and `ProviderInstaller.Connect`
opened the browser once per matching log line rather than once per
attempt — real CLIs print the login URL more than once (once when opening
the browser, again as an "if it didn't open, visit:" fallback). Fixed both:
`ConnectAsync()` now short-circuits to "Already connected." when the
stored login is fresh, and `Connect()` guards the URL-open to fire once per
attempt, matching the single-open invariant `ProviderInstaller.swift` keeps
directly in its read loop. 2 new tests — 301 total. Separately, no Code
Mode button gave any feedback WHILE it was working, only once it finished
— the ViewModels already track `IsBusy`, it just wasn't bound to anything.
Fixed by disabling Commit/Push/Refresh/branch-Create/Create PR/Merge while
their respective `IsBusy` is true.
Written with the same patterns already confirmed working (converters in
`Page.Resources`, `ViewModel` set before `InitializeComponent()`,
`UpdateSourceTrigger=PropertyChanged` on text inputs, and — new this round —
relying on x:Bind's documented automatic null-propagation across a binding
path like `PullRequestViewModel.Info.Title` rather than needing `?.` or a
converter).
Written while GitHub Actions was down (see below) — none of the service
layer needs Windows or CI to build/test, so there was no reason to wait
idle for it.

**Update (2026-08-11): the opt-in Auto-PR toggle, built end to end.** With
Fase 6 and 7 both closed, the founder asked to keep going on the product and
picked Auto-PR — the one piece Fase 7 had deliberately deferred, for lack of
any settings-persistence layer. New `AppSettings` (JSON file under
`%LOCALAPPDATA%\Coral\settings.json`, not `Windows.Storage.ApplicationData`
since Coral is still unpackaged) backs a new `PullRequestViewModel.AutoPr`
property (injectable load/save, same pattern as `ConnectViewModel`'s
`checkFreshness`) and a checkbox on `CodeModePage`. The actual trigger —
mirroring `CodeModeView.swift`'s `.onChange(of: vm.isRunning)` — lives in
`CodeModePage.xaml.cs`: the page subscribes to `ChatViewModel.PropertyChanged`
in its constructor and, when `IsSending` flips to `false` with the toggle on
and `gh` installed and files changed, refreshes the git panel and calls
`PullRequestViewModel.ShipAsync` the same way "Commit + PR" does, using the
same drafted-message fallback. `PullRequestViewModel.ShouldAutoShip(autoPr,
hasRepo, ghInstalled, hasChanges)` is a pure static spec of the guard (5
`[Theory]` cases) but the wiring in `CodeModePage` can't call it directly —
`CodeModePage` also has an instance property named `PullRequestViewModel`,
so the identifier resolves to that instance before the type (C# CS0176) —
worked around by inlining the same four-part check instead. 14 new tests —
316 total. CONFIRMED compiling on windows-latest CI (commit `88d0781`, run
[31472737875](https://github.com/pedalbacklog/StrategyForge/actions/runs/31472737875) —
"Build the WinUI 3 app (Coral)" green, including the new checkbox's XAML).

**Update, same day: verified end to end on real Windows, plus one style
bug found and fixed.** The founder tested it on a real branch
(`ui-test-create-pr-2`): a chat-driven change with the toggle on opened a
PR the moment the turn finished (confirmed both in Coral's own "Pull
request opened." message and on the branch on GitHub), a second change on
the same branch updated the existing PR instead of opening a second one
("Pull request updated." — same `TextBlock` "Commit + PR" already used,
just easy to miss live since it's small and sits right above the
checkbox), and a third change with the toggle off correctly did nothing —
the file changed locally but nothing was committed/pushed, exactly as
designed. One real bug found along the way: the checkbox and its label
text rendered visibly misaligned — WinUI3's default `CheckBox` template
doesn't vertically center its content against the check glyph once
`FontSize` is overridden down (here, to 12). Fixed by adding
`VerticalContentAlignment="Center"` to the `CheckBox` in
`CodeModePage.xaml`. XAML-only change, no unit test possible for it —
pending visual reconfirmation.

**Follow-up polish, explicitly requested after the verification above:**
the "Pull request opened."/"Pull request updated." `TextBlock` next to
"Commit + PR" was styled the same as every other status message on the
page (`Opacity="0.7"`, regular weight) — easy to miss live, which is
exactly what happened during testing. Unlike the rest, this is the one the
Auto-PR toggle needs noticed, so it's now full opacity and
`FontWeight="SemiBold"` (13px instead of 12). The other status
`TextBlock`s on the page (`GitPanelViewModel`'s, and the copy inside the
"Pull Request" flyout) are left as they were on purpose — they weren't the
finding, and the flyout's copy already sits in its own prominent modal
context. CONFIRMED compiling on windows-latest CI (commit `7560aeb`, run
[31475443776](https://github.com/pedalbacklog/StrategyForge/actions/runs/31475443776) —
"Build the WinUI 3 app (Coral)" green); pending visual reconfirmation on
real Windows, same as the checkbox alignment fix above.

**Update: partially closing the Fase 6 `SubmitCodeAsync`/`WriteLineAsync`
coverage gap.** With Fase 9 (packaging) parked pending a signing-strategy
decision, this stale gap got priority instead. The full end-to-end path —
a real `claude auth login` actually reaching `NeedsCode` — still can't be
forced deterministically, since both real attempts so far resolved via
loopback first, a network condition outside this port's control. But the
low-level mechanism `SubmitAsync` relies on —
`IPseudoConsoleSession.WriteLineAsync` writing into a real ConPTY session —
CAN be tested deterministically, independent of any CLI or network at all.
`ManualPseudoConsoleSmokeTest`'s existing test only ever exercised the
READ half of that primitive (`cmd.exe /c echo ...`, no stdin involved). A
new test, `WriteLineAsyncDeliversInputToARealChildProcessStdin`, starts
`cmd.exe /c "findstr /r ."` (no file — `findstr` echoes back whatever it
reads from stdin, a clean stdin-echo with none of `set /p`/delayed
expansion's timing traps), writes a line via `WriteLineAsync`, and confirms
it comes back out through the child process's real stdout. Unlike the
other manual tests here, `findstr` never exits on its own (the ConPTY's
input pipe stays open), so this one reads only until it's seen what it's
looking for (10s timeout) and explicitly kills the session rather than
waiting for a natural exit. `Category=Manual` like the rest — builds clean
on Linux (316/316 unaffected, since it's excluded by the `Category!=Manual`
filter) but, like every other test in this file, can only really run on
Windows — pending the founder running it:
`dotnet test windows/Coral.Tests/Coral.Tests.csproj -c Release --filter "FullyQualifiedName~WriteLineAsyncDeliversInputToARealChildProcessStdin"`.

**Update: attempted on real Windows — unresolved environment issue, documented
rather than chased further.** The founder ran it on their physical machine
(not RDP, not a VM — explicitly ruled out), and both `ManualPseudoConsoleSmokeTest`
tests failed with the exact same signature as Fase 3's original
`STATUS_DLL_INIT_FAILED` investigation (see above): only the 16-byte ConPTY
handshake crosses the pipe, then nothing — including the `echo` test that
was confirmed passing back on 2026-08-06. The same fix from that
investigation (switch to the classic console host, away from Windows
Terminal) was retried, this time with the founder explicitly confirming
the window had no tab strip (i.e. genuinely wasn't Windows Terminal hosting
a `cmd`/PowerShell tab) before rerunning — and it failed identically
anyway. That rules out Fase 3's documented cause as the complete
explanation: something else is intercepting the nested ConPTY creation on
this machine today that wasn't intercepting it (or not the same way) back
then. Plausible, unconfirmed candidates — none diagnosable remotely without
hands-on access to the machine: a Windows update since then changing
`conhost.exe`'s internal behavior, some third-party terminal/console
software hooking console creation system-wide, or a local group
policy/registry setting.

**Decision: parked, non-blocking.** The real app never launches from a
terminal (it opens from Explorer, with no ConPTY anywhere above it in the
process tree), so this nested-ConPTY scenario can't occur in production —
it only affects manually running these two tests via `dotnet test`.
`WriteLineAsyncDeliversInputToARealChildProcessStdin` stays written,
compiling in CI, and documented as pending future verification if the
environment ever changes (or gets investigated with direct machine
access) — no more time spent chasing it blind over chat.

**Update: Strategy selection (P0 item 2), Phase 1 — chat stops being a bare
`claude -p`.** A real gap surfaced while reviewing what to build next: the
requirements list itself marks "strategy selection/editing, basic Advisor"
as P0 ("without this it's not Coral, it's an empty shell"), but it was
never wired into the Windows chat — `ChatViewModel.cs`/`MainPage.xaml` had
zero references to `Strategy` anywhere. The chat always ran bare, never
generating the subagent `.md`/`CLAUDE.md` files `StrategyWriter` (ported
and tested since Fase 2) has been able to write for months. A planning
subagent researched both the Swift original (`ChatView.swift`,
`StrategyPickerColumn.swift`, `AdvisorEngine.swift` — ~4400 lines total)
and this port's current state before any code was written, and proposed
cutting the work into 4 phases. Phase 1 (this entry): a read-only template
picker → generate its files → the chat runs against the generated team.
No strategy editing, no Advisor yet (separate later phases).

Why Phase 1 is smaller than it sounds: the `claude` CLI reads
`.claude/agents/*.md`/`CLAUDE.md`/`.mcp.json` from its own working
directory — nothing needs to be passed as an argument. Once
`StrategyWriter.Write` drops those files in the repo, the very NEXT chat
turn already runs against the full generated team without
`ChatViewModel` needing to know anything about `Strategy` at all. The one
real change needed: `ChatViewModel._model` (private, snapshotted once at
construction) became `ChatViewModel.Model` (a mutable public property), so
picking a template can change the next turn's orchestrator model without
reconstructing the ViewModel or losing the in-progress transcript/session id.

New pieces: `StrategyPickerViewModel` (`Coral.Core`, new) wraps
`StrategyWriter.Write` in a `Task.Run` (it's sync disk I/O) and exposes
`Templates`/`SelectedStrategy`/`IsBusy`/`StatusMessage` — no
`IProcessLauncher` dependency at all, since `StrategyWriter` never spawns a
process. A new "Strategy" button on `MainPage`'s header opens a `Flyout`
(the same pattern already used three times: Connect Claude, Open Repo, the
Code Mode PR flyout) listing all 15 templates by name + description — no
topology-diagram cards like macOS has (deliberately out of scope, see
below). Deliberately doesn't auto-close on selection — the `StatusMessage`
updating in place is the confirmation, same reasoning as the Pull Request
flyout staying open through create/merge. `MainPage.xaml.cs`'s
`OnStrategySelectionChanged` calls `SelectAsync` and, on success, sets
`ViewModel.Model` to the picked strategy's orchestrator model.

**Deliberate deviation from the plan's own recommendation:** nothing gets
written automatically on startup. The plan proposed silently writing
`Solo()` as a "behaviorally neutral" default — dropped after checking two
things the plan hadn't verified: (1) `RepoPath` falls back to the user's
ENTIRE home directory when no repo is open, so writing files there
unprompted is a real surprise, not a neutral one; (2)
`StrategyLibrary.Solo()`'s suggested model is `ClaudeModel.Opus5`, while
today's chat defaults to `claude-sonnet-5` — applying that automatically
would have silently raised the cost of every chat for everyone. Instead:
chat behaves exactly as it did before this feature existed until the user
explicitly opens "Strategy" and picks something — zero required action,
zero surprise side effect.

Other findings from the plan, recorded as deliberate cuts (same pattern as
Fase 6/7's deferred-scope calls): macOS's animated topology-diagram card
grid, cost-tier pills, and topic filtering are pure presentation and don't
block "pick a template and run against it" — deferred to a later polish
pass. The guided wizards (`ChooseStrategyWizard`/`TaskToStrategySheet`) are
redundant once Advisor (Phase 3) exists, not worth building twice.
"Advisor" itself turns out NOT to be an LLM call — it's a local,
deterministic heuristic engine (keywords/length/language) that returns a
recommended `Strategy`; the one piece that IS AI-backed
(`adviseWithAI`, an on-device Apple Intelligence upgrade) has no Windows
equivalent and is dropped outright, not deferred.

4 new tests (`StrategyPickerViewModelTests`: all templates exposed, a real
write to a temp directory, a controlled error instead of an unhandled
exception on an unwritable path; `ChatViewModelTests`: a regression
confirming `Model` mutated after construction is reflected in the next
turn's `--model` argument) — 320 total. Fixed in passing: the requirements
list said "13 templates" — both macOS and this port actually have 15; a
stale number, not a real missing feature.

Covered by unit tests, local build/test (320/320), and CONFIRMED compiling
on windows-latest CI (commit `7945288`, run
[31489629778](https://github.com/pedalbacklog/StrategyForge/actions/runs/31489629778) —
"Build the WinUI 3 app (Coral)" green) — pending hands-on verification on
real Windows (pick a template, confirm real `.claude/agents/*.md` files
show up in the repo, and that the next chat turn actually delegates to the
generated subagents).

**Update, same day: Phase 3 — the Advisor engine, ported and wired into the
UI (heuristic half).** The founder wasn't going to be able to test on real
Windows for a few hours and asked to keep advancing meanwhile — Phase 3 is
exactly the kind of work that can be fully completed and verified WITHOUT
Windows: pure logic, 100% covered by `dotnet test` on Linux.

Real finding along the way: `AdvisorEngine.advise()` isn't self-contained —
it depends on `StrategyGenerator.swift` (351 lines, the task classifier →
team shape), a file the planning subagent hadn't identified as a
dependency. Ported first:

- `StrategyGenerator.cs` (new, `Coral.Core/Generators`): `Classify(task)`
  (the multi-axis keyword reader — intent, scope, adversarial, needs
  scouting, etc., bilingual EN/ES with diacritic folding via Unicode NFD
  normalization, the .NET equivalent of Swift's `.folding(options:
  .diacriticInsensitive)`), `ShapeFor(profile, connected)` (the
  deterministic profile → shape + size map), `BuildStrategy(shape,
  teamSize)` (builds a real `Strategy` from `StrategyLibrary` and runs it
  through `AutoFixed()`), and `HeuristicShape` (the two composed).
  Deliberately NOT ported, same cut as Fase 6/7: Apple's on-device AI path
  (`generate(from:)`, `isAIAvailable`, `TaskRead`) and the semantic-embeddings
  paraphrase assist (`SemanticClassifier`, also on-device) that
  `heuristicShape` layers on top of a low-confidence read — neither has a
  free/private Windows equivalent. Without them, a strangely-phrased task
  might classify a bit less precisely than macOS; never incorrectly in a
  way the keyword path wouldn't also risk.
- `AdvisorEngine.cs` (new, `Coral.Core/Services`): `Advise(task, connected)`
  — the full deterministic decision tree (depth → model → team shape →
  cheap/non-delegable adjustments → loop kind → effort), with
  `DecisionStep`/`Advice` ported as types with semantic equality (Swift
  hand-writes `==`/`hash` so two identical recommendations compare equal
  even though `Strategy.Id` mints a fresh GUID every call — the port does
  the same via a manual `IEquatable<Advice>` rather than trusting a
  record's default equality, which would compare collections by reference).
  Scope of THIS pass: `Advise()` only. Deliberately not yet ported:
  `adviseWithAI` (on-device AI upgrade, same reason as above), `adviseTiers`
  (the Economy/Recommended/Max three-tier UI — belongs with the Phase 4 UI
  polish pass, not the engine). `assignProviders`
  (`AdvisorEngine+Providers.swift`, cross-provider role reassignment) WAS
  later ported, on 2026-08-21 — see the Status section and the dated entry
  further down for the full detail.

**A delicate call worth documenting on its own: `LoopKind.cs` (new).**
`Advice.loopKind` is part of `advise()`'s contract — but `LoopKind` is
defined in `Models/LoopPlan.swift`, one of the four files `CLAUDE.md` marks
off-limits for autonomous changes ("loop changes need human review of the
diff, not just green tests... this is a firm limit, not a scheduling
matter"). `LoopPlan.swift` was NOT read for this: all four enum values
(`turnBased`/`goalBased`/`timeBased`/`proactive`) were already visible in
`AdvisorEngine.swift` itself (an allowed file), so `LoopKind.cs` was
written as a minimal, standalone enum sourced from that alone — no read of,
dependency on, or progress toward anything in the off-limits zone, and
Loops' actual scheduling/running/generation (Fase 8) remains entirely
unstarted on Windows. Documented explicitly here so this reads as a
deliberate, considered call, not a boundary slip.

**Real UI hooked up too, not just the engine.** Inside the same "Strategy"
flyout from Phase 1: a new "Or describe the task and let Advisor suggest a
team" section — a `TextBox` + "Suggest" button (`AdvisorViewModel.Suggest()`,
wrapping `AdvisorEngine.Advise()`, no network/subprocess, instant) showing
a one-line summary (team · model · effort) and a "Use this" button that
applies the recommendation the exact same way manually picking a template
does (`StrategyPickerViewModel.SelectAsync` + updating `ChatViewModel.Model`).
`AdvisorViewModel` is its own new ViewModel, separate from
`StrategyPickerViewModel` — same one-concern-per-ViewModel rule as the rest
of this port; it never writes files itself, only produces a recommendation.

20 new tests (`AdvisorEngineTests.cs`, an almost-mechanical translation of
`AdvisorEngineTests.swift` — all 18 cases passed on the first run,
confirming the port is faithful to the original; `AdvisorViewModelTests.cs`,
2 cases) — 340 total.

Covered by unit tests, local build/test (340/340), and CONFIRMED compiling
on windows-latest CI (commit `cd871b0`, run
[31490734337](https://github.com/pedalbacklog/StrategyForge/actions/runs/31490734337) —
"Test Coral.Core (via Coral.Tests)" and "Build the WinUI 3 app (Coral)"
both green).

**Verified on real Windows, same day: point 1 (picking a template) works
perfectly** — confirmed with a screenshot, header showing "Executor +
Advisor" and the Activity panel with a real "→ advisor" delegation entry.
**Real bug found in "Suggest": the flyout was closing itself a few seconds
in, never showing the recommendation.** The founder described it
precisely: "it jumped to what looked like an identical window, and after a
few seconds it closed on its own." Cause: the same bug pattern already
seen twice in this port (Connect Claude, Pull Request) — the "Use this"
row appears once `AdvisorViewModel.HasAdvice` flips true, the flyout's
content grows, WinUI3 repositions it to stay anchored under the "Strategy"
button, and that reposition reads as an outside interaction that triggers
the default light-dismiss — closing it before the recommendation was ever
visible. Fixed with the same proven pattern as the Pull Request flyout:
unconditional `Closing` block (`e.Cancel = true`) paired with an explicit
✕ button (`OnCloseStrategyFlyoutClick`) as the only real way to close it.
XAML/code-behind only — 340/340 unchanged (nothing here is fake-testable,
it's pure WinUI3 popup behavior). CONFIRMED compiling on windows-latest CI
(commit `8b498ea`, run
[32400427691](https://github.com/pedalbacklog/StrategyForge/actions/runs/32400427691) —
"Build the WinUI 3 app (Coral)" green). The founder already reconfirmed
this on real Windows the same day: the flyout stopped self-closing — see
the two follow-up findings below (Enter key, "Use this"), found precisely
because the recommendation could finally be seen long enough to interact
with it.

**Second round, same day: with the flyout no longer self-closing, two more
findings — one cosmetic, one real without a confirmed root cause yet.**

1. **Enter doesn't trigger "Suggest".** The task TextBox had no `KeyDown`,
   unlike every other text box in this port (the main prompt, the new-branch
   name box). Fixed by adding `OnAdvisorTaskBoxKeyDown` — the exact same
   pattern as the others.
2. **The recommendation in the attached screenshot ("Debate / Consensus
   (mediated) · Sonnet 5 · Medium effort" for "define the architecture for
   an android app") was NOT a bug — confirmed with the founder the
   screenshot was just showing state, not a complaint.** It's actually the
   correct result: "arquitectura" lands in `StrategyGenerator.Classify`'s
   `Decide` intent group, which without `multiDomain`/`breadth` signals maps
   to `DebateConsensus`; with no depth-group words, the model stays at
   Sonnet 5. Matches exactly what the port predicts — another fidelity
   confirmation, not a finding.
3. **"Use this" produced no visible change at all — not the header, not the
   status message, not the flyout.** Root cause NOT confirmed yet (not
   reproducible remotely), but reviewing `StrategyPickerViewModel.SelectAsync`
   turned up a real silent no-op: if `IsBusy` is already `true` when called
   (e.g. an earlier write still in flight), the method returns `false`
   without touching `StatusMessage` or `SelectedStrategy` at all —
   indistinguishable from "the button isn't wired to anything," which is
   exactly what the founder described. Fixed so that path leaves a visible
   trace (`StatusMessage = "Busy — try again in a moment."`) instead of
   staying silent, and added `IsEnabled` on "Use this" bound to `!IsBusy`
   (same pattern the template list already had) for visual feedback while
   busy. This turns a silent, undiagnosable bug into one with a trail — if
   the founder retries and sees "Busy…", it confirms this hypothesis; if
   they still see absolutely nothing, the cause is elsewhere (the click
   isn't reaching the handler at all) and that's next time's investigation.
   1 new test (`SelectAsyncReportsBusyRatherThanSilentlyNoOpingWhileAlreadyRunning`,
   two overlapping `SelectAsync` calls confirming the second leaves a
   trace) — 341 total.

Local build/test confirmed (341/341) and CONFIRMED compiling on
windows-latest CI (commit `5d53e03`, run
[32402981485](https://github.com/pedalbacklog/StrategyForge/actions/runs/32402981485) —
"Test Coral.Core (via Coral.Tests)" and "Build the WinUI 3 app (Coral)"
both green).

**RECONFIRMED on real Windows: "Suggest"/"Use this" work end to end.** The
founder typed a task, clicked "Suggest," saw the recommendation ("Debate /
Consensus (mediated) · Sonnet 5 · Medium effort"), clicked "Use this," and
the header updated to "Debate / Consensus (mediated)" — screenshot
confirmed it. With that, Phase 3 (Advisor, heuristic half) is fully
closed: engine ported, wired into the UI, all three real bugs found along
the way (the flyout self-closing, Enter not triggering Suggest, "Use this"
going silent from a stuck `IsBusy`) fixed and verified on real Windows.

**Update: Phase 2 — strategy editing, a deliberately scoped-down first
cut.** The founder asked to continue with "the first item" (strategy
editing) from the remaining-work list. Before writing any code,
`StrategyEditorView.swift` (589 lines) and `RoleRowView.swift` (469
lines) were read — much bigger than "tweak the roles I already have and
save" actually needs: a repo picker embedded in the editor (this port's
repo is already open), an animated topology diagram, a cost popover, an
MCP server editor, cross-provider role mixing (Windows can't connect
Codex/Gemini yet), prompt/description/memory editing, and several
"generate" variants (Terminal, commit, download a brief, copy a starter
prompt). An explicit cut was agreed with the founder before any code was
written: only name, model, instance count, and tools per role, with live
`Strategy.Validate()` and a "Fix All" (`Strategy.AutoFixed()`) button —
everything else stays out, documented as a deliberate cut, not an
oversight.

New pieces: `StrategyEditorViewModel` (`Coral.Core`, new) wraps a
`Strategy` in place — `Roles` exposed separately from `Strategy` so the
role-row `ItemsControl` only re-binds when the role LIST itself changes,
`Issues`/`IssueLines` (pre-formatted plain-text lines — `"❌ ..."`/`"⚠ ..."` —
so the issues list's DataTemplate x:Binds against `string` instead of
needing a XAML type reference to the nested `Strategy.ValidationIssue`
record), `IsValid`, `HasAutoFixableIssues`, `Revalidate()`, `AutoFix()`
(swaps the whole `Strategy` for `AutoFixed()`'s fixed copy — not an
in-place mutation). `StrategyEditorWindow`/`StrategyEditorPage` (`Coral`,
new): a separate window, same pattern as `CodeModeWindow` — a real role
editor needs more room than a 380px flyout comfortably gives, and keeping
it separate means this not-yet-real-Windows-verified UI can't destabilize
the already-confirmed chat/Strategy flow. An `ItemsControl` (not
`ListView`, so selection handling doesn't fight clicking into a `TextBox`)
renders one card per role. Most of each row's controls (model, instance
count, tools) are wired in code-behind rather than x:Bind, since
`AgentRole` isn't an observable type and none of those three fields had a
clean x:Bind path without a converter that wasn't worth it for this small
a surface: the model uses 4 fixed `ComboBoxItem`s with `Loaded`/
`SelectionChanged` mapping by hand (avoids a `ClaudeModel`↔index
converter); instance count is a numeric `TextBox` with `Loaded`/
`TextChanged` (avoids an int↔double converter for `NumberBox`); tools are
a comma-separated `TextBox` with the same `Loaded`/`TextChanged` shape.
The role name DOES use plain `x:Bind Mode=TwoWay` (a simple `string`, no
type friction) — disabled for the orchestrator, matching Swift, since it
generates no subagent file of its own.

**Real bug caught before it ever reached CI**: `Text="{x:Bind Role}"`
(each row's `RoleKind`) would NOT have compiled — x:Bind doesn't
implicitly convert an enum to `string`, the same reason
`DiffLineKindToGlyphConverter` already exists for `DiffLineKind`. Caught
in self-review before pushing this time (not on real Windows), fixed with
a new converter of the same shape, `RoleKindToDisplayNameConverter.cs`.

A new "Edit current team…" button on the Fase 1 "Strategy" flyout, enabled
only when `StrategyPickerViewModel.HasSelectedStrategy` is true (new
property, same pattern as `AdvisorViewModel.HasAdvice` — this port only
has bool→something converters, not one for a nullable reference). "Save"
in the editor writes through the same path
(`StrategyPickerViewModel.SelectAsync`) picking a template or applying
Advisor's suggestion already uses, so the header and the orchestrator's
model stay in sync the same way.

Persistence is deliberately minimal, same as the original plan flagged:
edits live on the in-memory `Strategy` for as long as the window is open;
there's no "my saved named templates" library yet — a separate future
follow-up if it's ever needed.

5 new tests (`StrategyEditorViewModelTests.cs`: initial validation,
`Revalidate()` picking up a real hand-made error on a role, `AutoFix()`
swapping the strategy and revalidating, `IssueLines` distinguishing
error/warning by prefix) — 345 total.

Covered by unit tests, local build/test (345/345), and CONFIRMED compiling
on windows-latest CI (commit `9da8730`, run
[32431703595](https://github.com/pedalbacklog/StrategyForge/actions/runs/32431703595) —
raw log inspected line by line, not just the green check: "Passed! -
Failed: 0, Passed: 345, Skipped: 0, Total: 345" and "Build succeeded. 0
Warning(s) 0 Error(s)" for `Coral.csproj`; the `x:DataType="x:String"` and
the `Loaded` events inside the nested `DataTemplate` compiled clean, no
warnings). Pending, most importantly: hands-on verification on real
Windows — this is the biggest single UI surface in this whole block of
work, so expect at least one round of "the founder tries it and a WinUI
bug shows up," same as every phase before it.

**Verified on real Windows, same day: one real bug explained all three
reported symptoms.** The founder tried leaving a role's instance count
blank (expecting it to block "Save" — it didn't) and giving two roles the
same name (expecting an "Issues" entry and a blocked "Save" — neither
happened, and it saved anyway with two roles both named "curri"). Single
root cause: the name `TextBox` used `Text="{x:Bind Name, Mode=TwoWay}"`
WITHOUT `UpdateSourceTrigger=PropertyChanged` — WinUI3's default for a
two-way `TextBox.Text` binding is `LostFocus`, not every keystroke.
`TextChanged` does fire `Revalidate()` on every key, but at that moment
the `AgentRole`'s real `Name` hadn't been written yet (only committed on
focus loss) — so validation always ran against the PREVIOUS name, never
the duplicate just typed, leaving "Issues"/`IsValid` stuck on stale state.
Fixed by adding `UpdateSourceTrigger=PropertyChanged`, the same pattern
`PromptText`/`CloneUrl`/etc. already use elsewhere in this port — a real
oversight writing this piece, not a new WinUI3 quirk.

Along the way, a related bug in the count field: on a failed
`int.TryParse` from a blank box, the code left `role.Count` at its last
valid value instead of reflecting the broken state — which is why "Save"
didn't disable there either. A failed parse now writes `role.Count = 0`,
which `Strategy.Validate()`'s own "count below 1" rule catches the same
as any other bad edit.

Neither is testable with `Coral.Tests`'s fakes — pure WinUI3 runtime
binding behavior, not `Coral.Core` logic — so this bug could only be
found by actually running the app on Windows, exactly as happened.
CONFIRMED compiling on windows-latest CI (commit `6ffdccd`, run
[32433412699](https://github.com/pedalbacklog/StrategyForge/actions/runs/32433412699) —
raw log: "Passed! - Failed: 0, Passed: 345, Skipped: 0, Total: 345" and
"Build succeeded. 0 Warning(s) 0 Error(s)"). Reconfirmed by the founder:
"Save" DID stay disabled with the error visible — the block works.

**Same day — a second real finding testing "Fix All" against a trickier
case: two identically-named roles, one of them with more than one
instance.** The founder deduplicated "curri"/"curri" by hand (worked
fine, "Issues" caught it live) and clicked "Fix All". Result: the second
role got renamed to "curri-2" — but the FIRST role ("curri", `Count=2`)
itself generates `curri-1.md`/`curri-2.md`, so the auto-rename collided
with its own second instance. `Issues` correctly caught it ("Two
subagents both generate 'curri-2.md'") and `Save` stayed disabled — the
system didn't go quiet on the problem, but "Fix All" hadn't actually
resolved it.

Checked first whether this was a port-introduced bug: Swift's
`Strategy.autoFixed()` (`StrategyForge/Models/Strategy.swift`) has the
EXACT same purely-literal dedup logic — so this is a pre-existing macOS
limitation, not something the port introduced. Asked the founder whether
to fix it (touches model logic conceptually shared with macOS, though the
Swift file itself isn't touched) or leave it — they chose to fix it, in
`Coral.Core` only (not `StrategyForge/Models/Strategy.swift`, out of
scope for this Windows-focused session).

**Fix:** `AutoFixed()`'s dedup (`Coral.Core/Models/Strategy.cs`) now
reserves, alongside each role's literal name, the full set of files that
role will generate given its `Count` (`name` if Count≤1, `name-1..name-Count`
otherwise) — and tries `-2`, `-3`… suffixes until it finds a name whose
file set doesn't collide with ANY role processed so far, not just its
literal name. Deterministic: same role order, same result every time. 1
new test (`DeduplicationAvoidsCollidingWithAnotherRolesExpandedInstanceFiles`,
reproduces the exact "curri"/"curri" with Count=2 case and confirms
`Strategy.Validate()` comes back empty after `AutoFixed()`) — 346 total.
Existing `AutoFixTests.cs` cases still pass unchanged (the simple
no-multi-instance case behaves exactly as before).

Covered by unit tests, local build/test (346/346), and CONFIRMED compiling
on windows-latest CI (commit `3d59a54`, run
[32464962533](https://github.com/pedalbacklog/StrategyForge/actions/runs/32464962533) —
raw log: "Passed! - Failed: 0, Passed: 346, Skipped: 0, Total: 346" and
"Build succeeded. 0 Warning(s) 0 Error(s)"). **RECONFIRMED by the founder
on real Windows: "Fix All" now fully resolves the "curri"/"curri" with
Count=2 case, leaving no remaining error.**

With that, Phase 2 (strategy editing, first cut) is closed: edit
name/model/instance-count/tools per role, live validation, "Fix All" —
all three real bugs found along the way (stale validation from a missing
`UpdateSourceTrigger`, a blank count not reflecting an invalid state, and
`AutoFixed()` itself colliding with its own fan-out case) fixed and
verified on real Windows.

**2026-08-21 — Cross-provider role reassignment, first cut: `assignProviders`
engine ported + "Connect Codex"/"Connect Gemini" buttons.** The founder
asked why an earlier message in this session said "there's no way to
connect Codex/Gemini." Looking into it found the actual gap: the generic
connect engine (`ProviderInstaller`/`ConnectViewModel`, Phase 6) already
supports any `AIProvider`, but only ONE button ("Connect Claude") was
ever wired in `MainPage`, and `assignProviders` itself had never been
ported. Scope was agreed with the founder before writing any code (same
cut discipline as Phase 2): YES to the pure `assignProviders` engine plus
its tests, YES to the two missing connect buttons; NO to real
cross-provider execution (`CrossProviderEditor.swift` — depends on
`CodeArenaEngine`, `LineAttributor`, and worktree isolation, none ported
yet, a bigger phase of its own).

- `Coral.Core/Services/AdvisorEngine.Providers.cs` (new, a `partial class`
  split from `AdvisorEngine.cs` — mirrors the Swift extension file). Ports
  `AssignProviders` in full: the `ModelProfiles` catalog (5 Claude + 3
  OpenAI + 2 Gemini models, scored 1-5 on 4 axes — reasoning/coding/
  breadth/speed), `PrimaryAxis` per role kind, cost-band capping
  (`CostBand`, so a cheap seat never gets upgraded on a provider swap),
  pass 1 (primary axis per role, orchestrator never on Gemini — it stalls
  the real meta run, per the Swift comment this mirrors), pass 2 (reviewer
  forced onto a DIFFERENT model family than the "coder" — diversity by
  design), pass 3 (`EnsureCoverage` — every connected provider lands on at
  least one role, capped at a 1-point loss on that role's axis),
  deterministic tie-breaking (score desc → provider order asc → model id
  asc, same order as `AIProvider.allCases`/`Enum.GetValues<AIProvider>()`),
  and `CollapsingLocked` (a ChatGPT-account Codex login rejects `--model`,
  so a "locked" provider collapses to one "account default" profile).
  Deliberately NOT ported in this pass (nothing calls them yet):
  `aspirationalPicks` (a display-only "ideal mix" preview with no UI to
  show it), `adviseCrossProvider`/`applyingProviders` (wrap the not-yet-
  ported `adviseWithAI`).
- `AdvisorEngine.cs`: the class became `public static partial class` to
  host the second file — nothing else in it changed.
- `Coral.Tests/AdvisorProvidersTests.cs` (new): a 1:1 port of all 26 tests
  in `AdvisorProvidersTests.swift` (catalog integrity, role-to-axis
  routing, reviewer diversity, tier bias, the Claude-only no-op guarantee,
  determinism, coverage, deprioritized providers, the orchestrator's
  Gemini exclusion, the model-locked provider, and the "a swap never
  raises a role's cost band" invariant). All 26 pass locally (`dotnet
  test`, Linux sandbox — this slice is pure C#, no WinUI3, so it runs fine
  outside Windows). Project total: 377 tests, 375 passing — the 2 that
  fail (`ManualClaudeRunnerSmokeTest`/`ManualProviderInstallerSmokeTest`)
  are pre-existing smoke tests that need a real `claude`/`npm` on PATH,
  unrelated to this change (they fail the same way in any sandbox lacking
  those binaries).
- `MainPage.xaml`/`MainPage.xaml.cs`: two new buttons, "Connect Codex" and
  "Connect Gemini", cloning the existing "Connect Claude" pattern exactly
  (same `Flyout`, same light-dismiss guard while `IsConnecting`, same "⚠
  Paste the code" header warning) — two new `ConnectViewModel` instances
  (`ConnectCodexViewModel`/`ConnectGeminiViewModel`) constructed with
  `AIProvider.Openai`/`AIProvider.Gemini`, reusing the Phase 6
  `ConnectViewModel` untouched. Not yet verified against those two real
  CLIs on Windows — only Claude has been (Phase 6).

**Real finding made BEFORE writing a per-role provider picker into the
Strategy Editor (Phase 2), which cut the scope again:** reading how
`AgentFileGenerator.cs` writes each subagent's frontmatter
(`model: {role.Model.ToRawValue()}`, line ~69) confirmed it ignores
`role.Provider` entirely — and that the Swift original does the exact
same thing (`AgentFileGenerator.swift:74`). Not a port bug: a subagent
`.md`'s `model:` field was never the source of truth for a non-Claude
role — `CrossProviderEditor` is, reading `role.provider`/
`role.providerModelID` straight off the `Strategy` at RUN time, not off
the generated file. The problem is Windows has no `CrossProviderEditor`
at all yet: subagents are delegated natively by the `claude` CLI itself
(its `Agent` tool reads those `.md` files), so marking a worker role
"Codex" in the editor and saving would have Claude Code silently keep
delegating it as Claude at run time — the provider change would have no
real effect. Asked the founder before building anything: chose to leave
the per-role provider picker OUT of this cut (the recommended option)
rather than add it dimmed/disabled, and explicitly asked whether the
provider change could ever have real effect — answer: yes, that's
exactly what the already-parked `CrossProviderEditor` phase delivers
(an isolated worktree via `CodeArenaEngine` + sequential per-role
execution via `CrossProviderEditor` + line attribution via
`LineAttributor`, none ported yet), and some of the groundwork already
exists: `CLIOneShotRunner.cs` already builds the real `codex exec`/
`gemini -p` commands, from Phase 3.

With that, this first cut of cross-provider reassignment is closed at
its agreed scope: the pure engine plus tests, plus connecting the other
two providers from the UI. The per-role provider picker in the editor
and real cross-provider execution remain explicitly out of scope, future
work.

CONFIRMED compiling on windows-latest CI (commit `b19d982`, run
[32467676681](https://github.com/pedalbacklog/StrategyForge/actions/runs/32467676681) —
raw log: "Passed! - Failed: 0, Passed: 372, Skipped: 0, Total: 372" and,
separately, "Build succeeded. 0 Warning(s) 0 Error(s)" for the WinUI 3
app's own `dotnet build` (`Coral.csproj`) — that second build is what
actually exercises the new "Connect Codex"/"Connect Gemini" XAML in
`MainPage.xaml`, which this Linux sandbox can't compile at all).

**2026-08-21 — the founder clicked the new buttons on real Windows: two
real findings, both inherited from macOS, not the port.** "Connect
Codex" really opened ChatGPT's login page (`auth.openai.com/log-in`),
but "Sign-in timed out after 150 seconds" fired before the founder
finished signing in — and `localhost:1455` (Codex's local OAuth
callback) went to "Not Found" right after, since the hidden process
gets killed at 150s and the local server waiting for the redirect no
longer exists. "Connect Gemini" showed a panel of raw ANSI escapes
("1. Yes / 2. No ... Enter to select") instead of a readable menu —
Gemini has no login command, it's a first-run TUI, and Coral (in both
apps) blindly nudges it with 3 Enters at 1.2s/2.8s/4.4s hoping to accept
whatever's highlighted, never actually reading the menu. Checked
whether either was a port bug first: `ProviderInstaller.swift` has the
SAME 150s timeout (line 246) and the SAME blind 3-Enter nudge (lines
258-262) — confirmed, both are pre-existing macOS limitations. Asked
the founder before touching anything.

**Codex:** the founder confirmed lengthening the timeout, in the
Windows port only (not touching `ProviderInstaller.swift`) — same
treatment as the `AutoFixed()` fix. `ProviderInstaller.cs`: the timeout
goes from 150s to a new named `SignInTimeout = TimeSpan.FromSeconds(300)`
(the error message now derives from it instead of a bare "150"), and
Gemini's creds watcher (`WatchGeminiCredsAsync`) moves its own fixed
140s inner deadline to `SignInTimeout - 10s`, keeping the same "10s of
slack before the outer kill" relationship the Swift original has — so
Gemini's watcher always gives up BEFORE the overall timeout fires, never
after (giving up after would mean nobody reports the outcome). Deliberate,
positive side effect: bumping the overall timeout also widens Gemini's
own window (140s → 290s), which helps even though the blind nudge itself
wasn't touched.

**Gemini (the blind nudge):** the founder didn't ask for the minimal
mitigation offered (more nudge attempts) — instead retried on real
Windows and reported that a second attempt DID reach Google's sign-in,
and later confirmed with screenshots that authentication fully completed
on Google's side ("Authentication was successful" on
developers.google.com), passing through the account chooser, the native-
app-warning screen, and a Windows Security prompt asking to allow Node.js
JavaScript Runtime through the firewall (expected: Gemini spins up a
local server for its OAuth callback, same as Codex's `localhost:1455` —
needs "Allow"). With that confirmed: the login mechanism itself DOES work
end to end on real Windows; what's fragile is only Coral's blind nudge
(same design as macOS) plus how long a real multi-screen human login
takes — partially mitigated by the wider window above. Left parked by
design, not fixed at the root (actually reading the menu instead of
blindly nudging would be a bigger change). Still to reconfirm: whether,
after that Google success screen, Coral's own "Connect Gemini" button
actually flipped to "Connected." (whether the creds-file mtime detector
caught it in time) — the founder hasn't confirmed that part yet.

Covered by local build/test (375/377 — the 2 failures are the
pre-existing manual smoke tests needing real CLIs on PATH, unrelated to
this change). CONFIRMED compiling on windows-latest CI (commit
`b1ff1fb`, run
[32473164056](https://github.com/pedalbacklog/StrategyForge/actions/runs/32473164056) —
raw log: "Passed! - Failed: 0, Passed: 372, Skipped: 0, Total: 372" and
"Build succeeded. 0 Warning(s) 0 Error(s)" for `Coral.csproj`'s `dotnet
build`).

**The Gemini finding is now fully closed, not just parked:** the founder
confirmed on real Windows, with a screenshot, that after Google's
success screen the "Connect Gemini" button DID flip to "Connected." —
the creds watcher (`WatchGeminiCredsAsync`) correctly catches the real
success; the only fragile part was multi-screen human login timing,
already mitigated by the wider window above. **Reconfirmed by the founder on real Windows: "Connect Codex" no longer
hits the 150s timeout — it completed, and a second click correctly shows
"Already connected."** (the same freshness-check logic that skips
re-running a login when one's already valid, built for Claude in Phase
6, working the same way here for Codex — confirms that piece
generalizes cleanly to the other providers). An honest caveat from the
founder themself: they already had a ChatGPT browser session open and
had just installed the ChatGPT desktop app before this attempt, so this
doesn't fully isolate whether the new 300s limit alone saved it (sign-in
may have simply gone faster this time, with no credentials to type from
scratch) — but the observable result (no timeout, connection completed)
is what matters, and it's exactly what the fix was for. With that, this
first cut of cross-provider reassignment is fully closed: engine + tests
+ connecting all three providers, all three verified on real Windows.

**A stronger Codex reconfirmation:** the founder logged out of EVERY
ChatGPT session before retrying "Connect Codex", isolating for real
whether the 300s limit — not an already-warm browser session — is what
fixes it. It completed with no timeout. Along the way they reported two
UI issues, neither blocking, both confirmed by reading the code before
replying: (1) the "Signing in..." panel shows raw ANSI control
sequences (`[?9001h[?1004h...`) instead of readable text — the same
issue already seen with Gemini's TUI, a UI-polish item, not fixed in
this cut; (2) two browser tabs open (sign-in + `localhost:1455`) —
confirmed Coral itself only ever opens ONE (the `!openedUrl` guard at
`ProviderInstaller.cs:312`), the second is codex's own OAuth redirect
flow, not a Coral duplicate. Left for the UI-polish pass (alongside the
Advisor's Economy/Recommended/Max tiers): strip the ANSI control codes
from the log panel and clarify these connect flyouts' messaging.

**2026-08-21 — Advisor: Economy/Recommended/Max tiers, full card port.**
The founder asked to continue with "the tiers" from the UI-polish
backlog, and explicitly picked the largest scope offered ("all of it
at once": tiers + "why this?" + cross-provider mix + loop hint), after
seeing the real size (`adviseTiers` in `AdvisorEngine.swift`, 90 lines;
`AdvisorInlineCard.swift`, 255 lines of UI).

- `Coral.Core/Services/AdvisorEngine.Tiers.cs` (new, a `partial class`
  split): ports `adviseTiers` in full — `Tier` (id/label/note/advice),
  the saver/max variants (`Variant`: shifts EVERY role's model up/down
  a tier, nudges the largest fan-out role's `Count`, re-estimates
  cost/effort), dedup (a cheap variant that collapses onto the exact
  same shape as Recommended — e.g. a task that already lands on Haiku45
  solo, nowhere cheaper to go — is dropped, never shown as a
  duplicate), per-tier cross-provider reassignment (each tier uses its
  own bias: Economy → `TierBias.Saver`, Max → `TierBias.Max`) via
  `ApplyingProviders` (new: it only ever needed the already-ported
  `AssignProviders` — it never actually depended on `adviseWithAI`,
  despite what `AdvisorEngine.cs`'s previous class doc comment implied,
  corrected in this commit), and `ForcingHeadDown` (if the reassignment
  reverts Economy's headline model back to the same one as Recommended,
  it's stepped down one tier so it still reads as genuinely cheaper).
  Deliberately NOT ported: `adviseWithAI` itself (no Windows
  equivalent) — `AdviseTiers` uses `Advise()` (the deterministic path)
  everywhere Swift calls `adviseWithAI`, which is EXACTLY the fallback
  Swift itself takes when AI is unavailable — not a new behavior cut, a
  substitution for a path Swift already exercises.
- `Coral.Core/Services/AdvisorEngine.Providers.cs`: new
  `AspirationalPicks` (the IDEAL mix computed against every provider in
  the catalog, ignoring what's connected — display only, reuses the
  existing private `AssignCore`) and `TierBiasFrom(tierId)`.
- `Coral.Core/Services/AdvisorEngine.cs`: `Advice` gains three new
  fields (`AiRationale`, `UsedAI` — always `""`/`false` in this port, no
  on-device AI equivalent — and `ProviderPicks`), with the
  constructor/equality/hash updated to include `ProviderPicks` (matching
  `Advice.swift`'s own equality, which includes it too).
- `Coral.Core/ViewModels/AdvisorViewModel.cs` (rewritten): moves from a
  single `Advice` to `Tiers`/`SelectedTierId`/`SelectedTier` +
  `TierChips` (label/cost/selection, binding-ready),
  `SummaryText`/`SelectedNoteText` for the selected tier,
  `DecisionLines` (the decision path pre-formatted line by line — same
  pattern as `StrategyEditorViewModel.IssueLines`), `ProviderMixLines`
  (the selected tier's provider mix, real when ≥2 are connected, else
  aspirational with "(not connected)"), `ShowLoopHint`/`LoopHintText`
  (informational only — see below for why there's no button), and
  `ChosenTeamName`/`HasChosenTeam`/`ChosenTeamHintText` (the original's
  "you chose X" framing). Since Windows has no localization layer (every
  other ported string in this app is already a plain English literal
  directly in XAML), tier/question/evidence/provider-reason copy was
  ported as private `switch` dictionaries with the EN text from
  `Localization+Advisor.swift`, with ONE deliberate change: the
  "hardest = no" answer names the model this port ACTUALLY assigns on
  that branch (Opus 5, per `Advise()`), not the Swift copy's already-
  stale "Opus 4.8" — not touching shared logic, just not writing a new
  factual error into text being authored from scratch.
- `Coral/Converters/BoolToAccentButtonStyleConverter.cs` (new): bool →
  button `Style` (`AccentButtonStyle` when it's the selected tier, else
  the default) — so the three tier chips read as selectable tabs
  without inventing a general style system, just this one use.
- `Coral/MainPage.xaml`/`.xaml.cs`: the old one-line "summary + Use
  this" row is replaced with: the tier-chip row (`ItemsControl` over
  `TierChips`, 1 to 3 depending on dedup), the selected tier's
  summary + note, a "Why?" button with a `Flyout` showing
  `DecisionLines`, the provider-mix row, and the loop hint (no button —
  see below). New `OnTierChipClick` (reads the clicked button's `Tag`,
  carrying the tier id via `x:Bind Id`). `OnSuggestTeamClick`/
  `OnAdvisorTaskBoxKeyDown` now also set
  `AdvisorViewModel.ChosenTeamName` from
  `StrategyPickerViewModel.SelectedStrategy?.Name` before suggesting.
  `OnUseSuggestedTeamClick` now applies `SelectedTier.Advice` instead of
  the old single `Advice`.

**A delicate call checked BEFORE writing any code, not after:** the
Swift card has a "Create loop" button that opens real loop creation
when a task reads as recurring/event-driven. Fase 8 (Loops) has NO
scheduling/generation UI in this port at all yet, and `CLAUDE.md` is
explicit: Loop changes need a human reading the diff, never an
autonomous change — there's nowhere to send that button without
crossing into that vetoed territory. The informational hint TEXT was
ported (reads `Advice.LoopKind`, already safely ported without touching
vetoed files — see `Models/LoopKind.cs`), but NOT the "Create loop"
button/action. This wasn't asked about separately since `CLAUDE.md`
already settles it as a hard rule, not a scoping question — the founder
was told about this specific cut as part of the plan, not silently.

New tests: `AdvisorTiersTests.cs` (11, original coverage — there's no
`AdviseTiersTests.swift` in the Swift source, nor does Swift have
dedicated tests for `adviseTiers` at all): the three options in order,
Recommended always present and matching plain `Advise()`, Economy
cheaper/Max pricier, dedup of a collapsed tier, no picks Claude-only,
real picks with 3 providers connected, the "Economy never reads as the
same model as Recommended" guarantee, determinism, and
`ApplyingProviders`/`AspirationalPicks` separately.
`AdvisorViewModelTests.cs` rewritten (the old single-`Advice` API no
longer exists) plus 6 new tests for tiers/mix/loop hint/chosen team.

Covered by local build/test: 395/397 (the 2 failures are the
pre-existing manual smoke tests, unrelated). CONFIRMED compiling on
windows-latest CI (commit `7d0ac54`, run
[32477877689](https://github.com/pedalbacklog/StrategyForge/actions/runs/32477877689) —
raw log: "Passed! - Failed: 0, Passed: 392, Skipped: 0, Total: 392" and,
crucially, "Build succeeded. 0 Warning(s) 0 Error(s)" for `Coral.csproj`'s
`dotnet build` — the step that actually compiles all the new XAML: the
tier-chip row, the "Why?" flyout, the provider-mix row, and the new
converter). Pending: verification on real Windows by the founder
(describe a task, switch between tiers, open "Why?", apply/switch team).

**2026-08-21 — Real cross-provider execution (engine), minimal cut: "run the
current team," not the full Arena.** The founder asked to continue with
real cross-provider execution. Before writing any code it turned out that
in macOS this isn't wired into normal chat at all: a grep of
`CrossProviderEditor`/`CodeArenaEngine` only finds `ArenaView.swift` (1054
lines of UI) — the "Code Arena" mode, where several contestants race the
same task, each in its own isolated worktree, and the diffs are compared.
Normal chat never touches them. The founder was asked with this finding:
build the full Arena, a minimal cut ("just run the current team," diverging
a bit from macOS), or park it — chose the minimal cut.

**An additional finding that reverses a prior decision in the port
itself:** `CodeGit.cs` already had a class doc comment explaining why the
worktree primitives (`addWorktree`/`mergeNoFF`/`commitAll`/
`removeWorktree`/`deleteBranch`) had been left unported — "these exist ONLY
for loop isolation," Fase 8's vetoed zone. Grepping those functions in
Swift shows THREE callers: `LoopRunner.swift` (Fase 8, still vetoed), but
ALSO `CodeArenaEngine.swift` (the Arena itself) and `ChatViewModel.swift`
(its own "isolated-turn worktree" toggle, a normal-chat feature unrelated
to Loops). These are generic `git worktree`/`git merge`/`git branch`
wrappers with no reference to `LoopPlan`/`LoopScheduler`/`LoopRunner`/
`LoopFileGenerator` (the four files `CLAUDE.md` actually names) — being a
Loop DEPENDENCY isn't the same as BEING Loop code, and the earlier note
conflated the two. Corrected in `CodeGit.cs`'s own class doc comment, citing
the three real consumers.

- `Coral.Core/Services/CodeGit.cs`: added `FullDiffAsync` (the whole
  uncommitted diff, including untracked files, with the same 256KB
  per-untracked-file cap as the original) and the worktree primitives:
  `AddWorktreeAsync`, `CommitAllAsync`, `MergeNoFFAsync`,
  `RemoveWorktreeAsync`, `DeleteBranchAsync`. 15 new tests.
- `Coral.Core/Services/OneShotProcess.cs`: a root-cause fix found while
  designing the new runner's timeout — cancelling `RunAsync`'s
  `CancellationToken` threw the expected exception but NEVER killed the
  child process (`Dispose()` only releases the .NET handle, it sends no
  signal) — a cancelled `git`/`gh` process was left running orphaned in the
  background. A `finally { live.Kill(); }` now guarantees it never outlives
  the call, on every exit path — `Kill()` is already a documented no-op
  once a process has exited normally, so this changes nothing on the happy
  path. Benefits EVERY existing caller of `OneShotProcess` (`CodeGit`,
  `GitHubCLI`), not just the new code.
- `Coral.Core/Services/ProviderOneShotRunner.cs` (new): ports
  `ProviderRun.swift`'s `OneShotRunner`/`CLIOneShotRunner`, only the
  buffered half (no live-streaming `onEvent` variant — its only consumer,
  `TeamRunEngine`, shows a final diff, not step-by-step progress, so that
  cut is deliberate and smaller than the original, same as other Phase
  2/3 cuts). Claude runs over plain pipes via `IProcessLauncher` (its
  `--output-format json` doesn't need a real terminal — reuses
  `ClaudeRunner.BuildEnvironment`, now `internal` instead of `private`, so
  the `ANTHROPIC_API_KEY`/`ANTHROPIC_AUTH_TOKEN` stripping isn't
  duplicated); Codex/Gemini run under the same pseudo-console
  infrastructure (`IPseudoConsoleLauncher`) already proven in production
  for their sign-ins in `ProviderInstaller.cs` — same reasoning as the
  Swift original (these CLIs can block-buffer on a plain pipe). A 600s
  watchdog (test-overridable), live auth-prompt detection (reuses
  `IsAuthPrompt`/`AuthFailureMessage`/`StripAnsi`/`EstimateTokens`/
  `EstimatedCostUsd`, already ported in `CLIOneShotRunner.cs`), preferring
  an alternative binary when installed (Antigravity's `agy` over the
  retired `gemini`). 10 new tests, including one that verifies the
  timeout actually kills the session with an injected 50ms timeout.
- `Coral.Core/Services/EditProvenance.cs` (new): `EditProvenance` (record),
  `EditProvenanceResolver.Attribute` (resolves an active-subagent name to
  the responsible team member, using the already-ported
  `AgentNameMatcher.TitlesMatch`), `LineAttributor.Attribute` (pure LCS
  alignment, same 400,000-line-pair cap as the original — past that,
  the whole file is credited to the current editor instead of aligning). A
  1:1 port of `EditProvenance.swift` with its tests
  (`EditProvenanceTests.swift`), 12 total.
- `Coral.Core/Services/CrossProviderEditor.cs` (new): `IsCrossProvider`,
  `Editors` (the subagents, or the orchestrator alone for a solo team), and
  `RunAsync` — runs each team member in SEQUENCE inside the worktree,
  re-attributing every changed file after each turn via `LineAttributor`.
  Only needed ONE function from `MetaOrchestrator.swift` (`modelID(for:)`,
  3 lines) — inlined rather than porting the whole 390-line file (meta-
  orchestration outside a worktree is a bigger, separate piece, out of
  scope). 5 new tests, with fakes (no real process, no real disk) — unlike
  the Swift original (a real temp repo), matching this port's already-
  established test style (`CodeGitTests`, `ProviderInstallerTests`).
- `Coral.Core/Services/TeamRunEngine.cs` (new): the piece that ties it all
  together — isolates the CURRENT strategy in a fresh worktree off HEAD,
  writes its files (`StrategyWriter`, already ported), commits them as a
  baseline, runs (cross-provider via `CrossProviderEditor`, or a single
  native call for a Claude-only team — same as `CodeArenaEngine.swift`'s
  own non-cross-provider branch), captures the diff, commits the work.
  `ApplyAsync`/`DiscardAsync` merge or discard. Deliberately NOT the full
  `CodeArenaEngine`: a single contestant (the team you already have), not a
  race between several — see its own class doc comment for why. 4 new
  tests against a REAL git repo (unlike the rest of this port — here it's
  worth it: `TeamRunEngine` chains several git operations together, and a
  real integration test proves far more than mocking each call separately
  — `CrossProviderEditorTests` already covers that lower layer with fakes).

Covered by local build/test: 436/438 (the 2 failures are the pre-existing
manual smoke tests, unrelated). No file in this cut touches XAML, so this
layer is verified without needing CI. Pending: the UI ("Run for real") and,
after that, CI plus real-Windows verification.

**UI: "Run for real…" — completes the minimal cut.** A new button next to
"Edit current team…" in the "Strategy" flyout (same `IsEnabled` as it,
tied to `StrategyPickerViewModel.HasSelectedStrategy`). Opens
`TeamRunWindow` (same "separate window" pattern as
`StrategyEditorWindow`/`CodeModeWindow` — so this new, not-yet-real-
Windows-verified UI can't destabilize the chat flow already confirmed
working). `TeamRunViewModel.cs` (new): wraps `TeamRunEngine` with
`Task`/`IsRunning`/`StatusMessage`/`Outcome`, `DiffLines` (reuses
`CodeGit.Parse`, the same format Code Mode's own git panel already uses),
`AuthorshipLines` (only has content for a cross-provider run — a Claude-
only team has no per-line provenance; Claude Code's own Agent tool did
the delegating), and `RunAsync`/`ApplyAsync`/`DiscardAsync`.
`TeamRunPage.xaml`: a task box + "Run", the diff viewer (same
`ListView`/`DiffLineKindToGlyphConverter` as `CodeModePage.xaml`),
authorship lines, and "Discard"/"Apply". 4 new ViewModel tests (reusing
`TeamRunEngineTests.cs`'s same real-git pattern for the cases that need a
real `Done` outcome to check `CanApply` against).

Covered by local build/test: 440/442 (the 2 failures are the pre-existing
manual smoke tests). Pending: CI confirmation of the real WinUI 3 app
build (new XAML, can't compile in this Linux sandbox) and verification on
real Windows by the founder.

**CI was red on the first attempt — a real Windows bug, not a production
one.** 6 of the new tests (`TeamRunEngineTests`/`TeamRunViewModelTests`)
failed on `windows-latest` with
`System.UnauthorizedAccessException: Access to the path '...' is denied`
while deleting the temp repo in the cleanup `finally`. Root cause: git
marks loose objects under `.git/objects/` read-only, and .NET's plain
`Directory.Delete(recursive: true)` throws that exception on Windows when
it hits one instead of clearing the attribute first (unlike Linux, where
this sandbox's own runs never surfaced it). Production code never hits
this — `TeamRunEngine`/`CodeGit` remove a worktree via
`git worktree remove`, a real git call that handles its own read-only
objects — so this is a test-cleanup-only fix. Fixed with a
`DeleteDirectoryRobustly` helper that clears `FileAttributes.ReadOnly`
from every file before deleting, in both affected test files.

**A second red CI run — this time a real finding, not just a test one.**
With cleanup fixed, failures dropped from 6 to 3: `CLAUDE.md` still showed
up as an uncommitted new file in the final diff, and "Apply" failed to
merge. Root cause: the `windows-latest` CI runner has no global git
identity configured (`user.name`/`user.email`), so `git commit` inside
`CodeGit.CommitAllAsync` was silently failing with "Please tell me who you
are" — the baseline commit never actually happened, so
`CLAUDE.md`/`.claude/agents` files stayed untracked, and the real work
never landed on the run's branch either, so "Apply" merged an empty
branch.

Checked whether this affects Swift too first: `CodeGit.swift` doesn't
configure any identity for its arena/loop commits either — confirmed via
grep, zero results. Not a port-introduced bug; a shared assumption (that
the host machine already has git configured) that was never exercised
until `TeamRunEngine` started actually committing. Since everything going
through `CommitAllAsync` is ALWAYS an internal, disposable, app-owned
commit (never something attributed to the real user — unlike
`CommitAsync`/`CommitStagedAsync`, the git panel's commit path,
deliberately left untouched), there's no reason for it to depend on host
config at all. Fixed directly in `CodeGit.CommitAllAsync`: it now always
self-identifies as `Coral <coral@localhost>` via
`-c user.name=`/`-c user.email=`, without asking first (this is new code
from this port with no exact Swift counterpart to "diverge" from — both
apps share the same underlying gap, and fixing it here touches no shared
or user-visible behavior). Verified manually outside the test suite: a
`git commit` with no identity configured fails with "Please tell me who
you are"; with the new `-c` override, it succeeds regardless of host
config — reproduces and confirms the exact fix CI needs.

**A third red CI run, same pattern — the merge needed identity too.**
Failures dropped from 3 to 2: the baseline/work commits were now landing
correctly (CI's own log showed the diff carrying real content, `claude.txt`
with `CostUsd = 0.01`), but "Apply" still failed — this time at the merge
step itself, not the commits before it. Same root cause, one step further
down the chain: `CodeGit.MergeNoFFAsync` runs
`git merge --no-ff branch -m message`, and a `--no-ff` merge always creates
a new merge commit — which also needs `user.name`/`user.email` — and
`MergeNoFFAsync`, unlike the now-fixed `CommitAllAsync`, had no `-c`
override at all. Same reasoning as the previous finding (an internal,
app-owned commit, never attributed to the real user, with no exact Swift
counterpart to diverge from): fixed by adding the same
`-c user.name=Coral -c user.email=coral@localhost` to `MergeNoFFAsync`,
without asking first. Covered by local build/test: 440/442 (the 2 failures
are still the pre-existing manual smoke tests). Pending: CI confirmation
that this closes out the last 2 failures.

**CI green, confirmed via raw logs.** Run
[32484080792](https://github.com/pedalbacklog/StrategyForge/actions/runs/32484080792)
on commit `e8841f8`: `Passed! - Failed: 0, Passed: 437, Skipped: 0, Total:
437`, and the WinUI 3 app build (`dotnet build ... Coral.csproj`) also
green, "0 Warning(s), 0 Error(s)". This closes out the whole "real
cross-provider execution" minimal cut: the engine (worktrees +
`ProviderOneShotRunner` + `EditProvenance`/`LineAttributor` +
`CrossProviderEditor` + `TeamRunEngine`), the UI (`TeamRunViewModel`/
`TeamRunWindow`/`TeamRunPage`, the "Run for real…" button), and all three
Windows-specific CI fixes (read-only-object test cleanup, git identity in
`CommitAllAsync`, git identity in `MergeNoFFAsync`). Pending: confirmation
on real Windows by the founder, exercising "Run for real" end to end
(pick a team, a small task, Run, review the diff, Apply or Discard).

**First real-Windows test: a crash and a silent error.** The founder
tried "Run for real" on his machine. First attempt (team "Executor +
Advisor", both Claude): the app crashed (`0xc0000005`, a native access
violation inside `Microsoft.UI.Xaml.dll`) while switching windows during
an in-progress run. Two hypotheses were ruled out with direct evidence
before asking for help blind: (1) the untested ConPTY code
(`Win32PseudoConsoleLauncher`) wasn't the culprit — "Executor + Advisor"
is 100% Claude, so `CrossProviderEditor.IsCrossProvider` is `false` and
the run takes the plain-pipe route (`OneShotProcess`/
`RealProcessLauncher`), never touching ConPTY; (2) it also doesn't match
the usual "updating UI from the wrong thread" pattern — at the moment of
the crash `TeamRunViewModel` wasn't touching any bound property, only
awaiting the `claude` process. The founder also confirmed switching
windows with nothing running doesn't crash (rules out a general WinUI3
multi-window issue on his machine) — so the crash is specific to having a
run in progress, but its exact cause is still unconfirmed: no memory dump
analyzed yet (WER's `.dmp` exists but hasn't been opened in a debugger),
so no real call stack to pin it down. Pending until the founder can pull
that stack (Visual Studio → open the `.dmp`) or get a reliable repro.

Second attempt (team "Orchestrator + workers (fan-out)"): no crash, but
`Error: Claude exited with an error.` — a real, reproducible failure with
no crash, much easier to diagnose. Comparing
`ProviderOneShotRunner.RunClaudeAsync` against `ProviderRun.swift`'s own
`run(prompt:...)` (line 154 onward): Swift DOES log `stdout` (up to 500
chars) alongside stderr/args/cwd to an exportable
`DiagnosticsLog.record(...)` before throwing — infrastructure this port
doesn't have yet — but the message it actually throws, same as the port,
only looks at `stderr`. The real difference is that the port, having no
diagnostics-log fallback, was discarding `stdout` with no trace left
anywhere — with `--output-format json`, a failure that never reaches a
valid JSON result often puts its only diagnostic text there instead of on
stderr. Fixed in `ProviderOneShotRunner.RunClaudeAsync`: the error message
now falls back to `stdout` when `stderr` is empty, instead of the generic
`"Claude exited with an error."` This is a gap the port introduced (not
shared with Swift — Swift keeps that text a different way), so fixed it
directly, without asking. 1 new test
(`ClaudeFallsBackToStdoutWhenStderrIsEmptyOnAnOrdinaryError`) locks in the
behavior. Covered by local build/test: 441/443 (the 2 failures are still
the pre-existing manual smoke tests, neither related). Pending: push, CI,
and having the founder repeat the same run to see the real error message
this time — that's what will let us diagnose why that particular CLI call
actually failed.

**Confirmed — the stdout fallback works, and the real cause is account
credits, not a Coral bug.** CI went green on commit `2d6248c` (438/438
tests, WinUI 3 build clean), and the founder repeated the same
"Orchestrator + Workers (Fan-out)" run: this time the UI showed the real
`claude` JSON payload instead of the generic message —
`"api_error_status":429`, `"result":"Fable 5 requires usage credits. Run
/usage-credits to continue or switch models with /model."`. This
strategy's orchestrator role is configured with `ClaudeModel.Fable5`
(`StrategyLibrary.cs:49`, the most expensive model available) — the 429
means the account ran out of usage credits for that specific model, an
account-level limit, not a code defect. No further fix needed here; this
closes out the "Claude exited with an error" investigation as a genuine
success for the stdout-fallback fix — it did exactly what it was for.
The crash from the first attempt (see above) remains open, still pending
a real call stack from the founder's WER dump.

**A second, real finding from the same test session: silent no-op runs.**
With a lower-cost team the founder retried "create a README.md with one
sentence" and got `Done — no changes were made.` — no crash, no error,
but nothing written either. Bisected by having him run the exact
`claude --output-format json --permission-mode bypassPermissions --model
... -p "..."` command by hand outside Coral entirely, three times: (1) in
his everyday repo — worked; (2) in a brand-new `git init`'d folder Claude
had never seen — also worked, ruling out a first-run "trust this folder"
gate; (3) inside the actual Coral-generated worktree for that failed run
(left on disk since he hadn't pressed Discard yet) — by the time he got to
it a retry inside the app itself had already succeeded, showing a real
diff for the identical team + task. That flip (same team, same task, same
worktree shape — fails once, succeeds the next) rules out a deterministic
bug and points at model non-determinism: "Orchestrator + Workers
(Fan-out)"'s system prompt tells the orchestrator to delegate to a
`worker` subagent, and in a single unsupervised `-p` call that delegation
can apparently complete with `is_error:false` and zero effect, without
the top-level orchestrator noticing or retrying.

Root cause identified, not a Windows-port bug: `CrossProviderEditor`'s
solo-editor prompt already appends "Edit the files in this repository
directly to complete the task." to push the model toward acting instead
of describing/delegating — `TeamRunEngine`'s native-Claude path (used for
any Claude-only team, i.e. most of them) never did. This exact gap exists
in `ProviderRun.swift`/`CodeArenaEngine.swift` too, so it's shared with
Swift — flagged instead of fixed unprompted, and the founder chose to
apply it to the Windows port after weighing it (the point of "run for
real" is a diff to review, a silent no-op run defeats it; the two
call sites already disagreed on this within the SAME window; the nudge
doesn't prevent legitimate delegation, it only biases the top-level call
toward acting). Fixed: extracted the suffix as
`CrossProviderEditor.DirectEditSuffix` (`internal const`) so both call
sites share the literal, and `TeamRunEngine.RunAsync`'s native-Claude
branch now appends it to the task before calling `runner.RunAsync`.
Covered by local build/test: 441/443 (same 2 pre-existing manual-only
failures). CI confirmed green on commit `9946d08` (438/438 tests, WinUI 3
build clean) before the founder retested.

**Nudge confirmed working on the first retest.** "Crea un archivo
NOTES.md con tres líneas explicando qué es este repositorio" (test
prompt #1) produced a real diff on the first try — the file was created
with actual content, not a no-op. Not proof the nudge fixes every case
(it biases the model, doesn't force it — the plan is to keep retrying
the other prompts to see how often a no-op still shows up), but a solid
first signal.

**Small UX gap found in the same pass: Enter didn't submit the task
box.** `MainPage`'s chat prompt box and the Advisor task box both submit
on Enter (`OnPromptBoxKeyDown`/`OnAdvisorTaskBoxKeyDown`, an established
convention in this app) — `TeamRunPage`'s task `TextBox` never got the
same wiring when it was built. Fixed: added `KeyDown="OnTaskBoxKeyDown"`
to the `TextBox` in `TeamRunPage.xaml` and a handler in
`TeamRunPage.xaml.cs` that calls `ViewModel.RunAsync()` on Enter, same
shape as the other two. `RunAsync()` already no-ops on a blank task or
while already running, so no extra guard needed in the handler. XAML-only
change — can't compile-check `Coral.csproj` in this Linux sandbox
(`EnableWindowsTargeting`, same limitation as every other UI change in
this port), so this needs CI to actually confirm it builds, plus the
founder confirming Enter now works on real Windows.

**Autonomous follow-up work (founder away for a few hours) — ported
`DiagnosticsLog`, scoped to the exact gap this session already found.**
Earlier in this same investigation, `ProviderOneShotRunner.RunClaudeAsync`'s
stdout-fallback fix was compared against `ProviderRun.swift`, which also
records the full invocation (cmd/cwd/stderr/stdout prefix) to an exportable
`DiagnosticsLog` before throwing — infrastructure this port didn't have.
Swift's `DiagnosticsLog.record(...)` is called from ~30 places across
services this port hasn't built yet (`MetaOrchestrator`, `KeychainStore`,
`CrashReporter`, `GraphifyService`, `AppModel`'s transcript sidecars...) —
porting all of that would be a large, unscoped feature. Ported only the
class itself (`DiagnosticsLog.cs`: `Record`/`Contents`/`Clear` real-path
wrappers over pure `*Uncached` methods, same shape as `AppSettings`/
`ProviderAuth`; rolling 256KB cap, trims to the header + newest ~60% once
exceeded; `%LOCALAPPDATA%\Coral\diagnostics.log`), and wired it into the
two failure call sites `ProviderOneShotRunner` already has —
`RunClaudeAsync`'s `if (!ok)` block and `RunPtyAsync`'s
`if (exitCode != 0)` block — matching `ProviderRun.swift`'s own two
`DiagnosticsLog.record` sites for these exact paths. Prompt is redacted to
`<prompt: N chars>` in the logged command line, same reasoning as Swift:
the log is meant to be exported/shared, and the prompt carries the user's
own conversation. 10 new tests (`DiagnosticsLogTests.cs`) covering the
pure core (header, level tags, newline escaping, ordering, clearing,
trimming, never-throws-on-a-bad-path).

**No UI wired to read this log yet** — Swift's own export flow lives in
`AppModel.swift`, not ported. Deliberately left open: where this should
surface in the Windows UI (a menu item? a button in "Run for real"
itself, right where a silent failure would be most useful to explain?) is
a product decision, not a mechanical port — flagging for the founder
rather than inventing a UI unprompted.

**A correctness bug found while wiring this in: `ProviderOneShotRunner`
called the real `DiagnosticsLog.Record` directly**, which would have made
every test exercising a Claude/PTY failure path (several already existed)
silently write to the real `%LOCALAPPDATA%\Coral\diagnostics.log` on
whatever machine runs `dotnet test` — including the founder's own
machine, every time he runs the suite locally. Fixed before it shipped:
added an injectable `recordDiagnostic` constructor parameter (same
pattern as `resolveBinary`/`callTimeout`), defaulting to the real
`DiagnosticsLog.Record`; all existing fake-based tests now inject a
no-op. 3 new tests confirm the wiring itself (diagnostic recorded with
the right fields, prompt redacted with the right length).

**A second real bug found in the same pass: a PTY launch failure escaped
as a raw exception, not the documented `OneShotException` contract.**
`RunClaudeAsync`'s launch failures already flow through
`OneShotProcess.RunAsync`'s own try/catch and surface as a normal
`OneShotException`; `RunPtyAsync`'s `_ptyLauncher.Start(...)` call had no
equivalent guard — a ConPTY setup failure (the one piece of this whole
port confirmed to have real, sharp edges — see the
`STATUS_DLL_INIT_FAILED` bug from Fase 3) would have propagated as a bare
`InvalidOperationException` instead of the `OneShotException` every other
caller (`TeamRunEngine`, `CrossProviderEditor`) already catches
specifically. Fixed: wrapped `_ptyLauncher.Start(...)` in its own
try/catch, recording a diagnostic and rethrowing as
`OneShotException(Failed, ...)`, matching every other failure kind in this
class. Covered by a new test.

**Flagged, not fixed — a real architecture question for the founder to
weigh in on.** Re-reading `ProviderRun.swift` closely while wiring the
diagnostics call sites surfaced something not noticed before: Swift's
BUFFERED (non-streaming) `run(prompt:...)` — the one this port's
`ProviderOneShotRunner` is actually a port of — uses the plain-pipe
`launch()` for ALL THREE providers, Codex/Gemini included. PTY
(`launchPTY`) in Swift is used ONLY by the STREAMING variant
(`run(prompt:...:onEvent:)`, not ported — see `IOneShotRunner`'s own doc
comment), because PTY there solves block-buffering that only matters for
LIVE progress; a buffered call that just waits for full output doesn't
need it. This port's `ProviderOneShotRunner.RunPtyAsync`, by contrast,
routes Codex/Gemini through ConPTY even in the buffered, non-streaming
path — meaning every cross-provider "Run for real" call currently
exercises the single least-proven piece of Windows-specific code in this
entire port (`Win32PseudoConsoleLauncher`, raw P/Invoke, confirmed working
only for the sign-in flow so far). Routing Codex/Gemini one-shot calls
through the already-proven `RealProcessLauncher`/`OneShotProcess` plain
pipe instead — matching what Swift's buffered mode actually does — would
remove that risk entirely for this path. Real behavior change to a
working code path, not a bug fix, so this needs the founder's decision
before touching it, not an autonomous change.

**Also flagged, not fixed — the orphaned-worktree gap raised in
conversation is confirmed shared with Swift.** Grepped `ArenaView.swift`
for any close/dismiss cleanup: there is none — Swift's own Arena leaves a
worktree behind too if the view is dismissed without discarding. Not a
Windows-port regression, so left alone pending the founder's decision
(same standing rule as every other shared-with-Swift behavior this
session).

Covered by local build/test: 454/456 (same 2 pre-existing manual-only
failures, unrelated).

**CI confirmed green via raw logs.** Run
[32531781896](https://github.com/pedalbacklog/StrategyForge/actions/runs/32531781896)
on commit `c4fc685`: `Passed! - Failed: 0, Passed: 451, Skipped: 0, Total:
451` — the exact +13 delta from the previous CI baseline (438), matching
the 13 new tests added this round — and the WinUI 3 app build also green,
"0 Warning(s), 0 Error(s)".

**Ported `ClaudeUsageStore` (P1 backlog item #10, "usage view") — data
layer only, ahead of any UI.** `ClaudeUsageStore.swift` reads Claude
Code's local session logs (`~/.claude/projects/**/*.jsonl`) and aggregates
real token usage into a 5-hour rate-limit "block" plus a rolling 7-day
window, per model — pure log parsing, no CLI spawning, no UI dependency,
so (like this port's Generators and `CLIOneShotRunner`'s command-building
half) it's useful and fully testable standing entirely on its own, well
before the ViewModel/View (`UsageStore.swift`/`UsageView.swift`, 622
lines combined) get ported. Faithful port: `ModelUsage`/`UsageSummary`
records, `LoadUncached(homeDirectory, now)` (pure — same
*Uncached-plus-real-wrapper shape as `ProviderAuth`) walks
`%USERPROFILE%\.claude\projects\**\*.jsonl` (Windows' equivalent of
Swift's `~/.claude/projects`, matching the home-dir convention
`ProviderAuth.cs` already established), skips files untouched in the
last 8 days before doing any JSON work, tolerantly parses each
`"assistant"`-typed line with `System.Text.Json` (malformed/legacy lines
skipped, never fatal — same contract as `ClaudeStreamParser`), sums
`input_tokens + output_tokens + cache_creation_input_tokens +
cache_read_input_tokens`, floors the earliest sample to the hour to
anchor the current 5-hour block (restarting whenever a sample lands past
the running block's reset), and ranks models by capability
(`PowerRank`) rather than raw usage for display ordering — `Fable`/`Opus`
first, `Haiku`/`mini`/`flash` last, matching the Swift original exactly.
`FriendlyModel` maps a raw model id ("claude-opus-4-7") to a short name
("Opus 4.7"), filtering out a trailing build-date suffix
("claude-haiku-4-5-20251001" → "Haiku 4.5", not "Haiku 5.20251001") the
same way Swift does. 23 new tests (`ClaudeUsageStoreTests.cs`): empty/
missing directory, malformed-line tolerance, multi-field token
aggregation, zero-token lines skipped, the 8-day file cutoff, the 7-day
sample window, block restart after 5 hours idle, block reporting zero
once elapsed, capability-based model ordering, `FriendlyModel`/
`PowerRank` table cases.

**No ViewModel/View ported yet** — same reasoning as `DiagnosticsLog`
above: what this should look like in the Windows UI (a tab? a flyout? how
much of the 541-line `UsageView.swift` layout to carry over vs. simplify)
is a product decision for the founder, not a mechanical port. The data
layer is ready whenever that's decided.

Covered by local build/test: 477/479 (same 2 pre-existing manual-only
failures, unrelated).
