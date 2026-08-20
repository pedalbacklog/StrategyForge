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
end on real Windows** — see `PORT-PLAN.md` §6 for the live
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
`StrategyPickerViewModel` (P0 item 2, Phase 1), and `StrategyGenerator`/`AdvisorEngine`/`AdvisorViewModel`
(P0 item 2, Phase 3 — see below) — 340 automated
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
`AdvisorEngineTests`, `AdvisorViewModelTests`, and the `MissionReport.AgentLines` cases) — plus 4 manual
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
  polish pass, not the engine), and `assignProviders`
  (`AdvisorEngine+Providers.swift`, cross-provider role reassignment — a
  smaller, separate follow-up, parked).

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
both green). Pending, most importantly: the founder retrying "Use this" on
real Windows to know whether the "stuck IsBusy" diagnosis was right or
there's still a different cause to chase.
