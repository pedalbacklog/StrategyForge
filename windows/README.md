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
  Write operations (stage/commit/push/clone/branch) and worktree operations
  (loop isolation — Fase 8's explicitly human-reviewed zone) are deliberately
  not ported yet — see Status.
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
  the same way as everything here, with an injected `IProcessLauncher`. 10
  tests cover the delta/full-text dedup, activity mapping, invariant-culture cost
  formatting, the resumed-session-missing retry, and cancellation — all real
  behavior ported from the Swift original, not reinvented. **Confirmed working
  end to end on real Windows** — see Status. And `ConnectViewModel` (Fase 6):
  drives one provider's "Connect" flow — install-if-missing, then sign in —
  for a UI, consuming `ProviderInstaller.Connect()`'s event stream into
  observable `Phase`/`LogLines`/`NeedsCode`/`StatusMessage` state, with an
  injectable `openUrl` delegate (default: `Process.Start(UseShellExecute:
  true)`) so opening the login link is still unit-testable. 7 tests; **not
  yet confirmed on real Windows** — see Status.

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

> `Coral.Core`/`Coral.Tests` (plain net8.0, no WinUI dependency) build and pass **210/210**
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
on real Windows** — see `PORT-PLAN.md` §6 for the live
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
`ProviderInstaller`/`ConnectViewModel` (Fase 6), and `CodeGit` (Fase 7's
first piece — see below) — 210 automated
xUnit tests, all passing (including `TemplatesAreAllValid`, which iterates
every template through `Strategy.Validate()`, `StrategyWriterTests`, which
round-trips real writes to a temp directory, `RealProcessLauncherTests`, which
spawns a genuinely real process to smoke-test the no-shell
`Process.Start`/async-stdout/exit-code/`Kill()` plumbing, `LocaleRegressionTests`,
added after the first real-Windows run, `ProviderAuthTests`,
`ChatViewModelTests`, `AgentNameMatcherTests`, `ProviderInstallerTests`
(against a new `FakePseudoConsoleLauncher`, mirroring `FakeProcessLauncher`),
`ConnectViewModelTests`, `CodeGitTests`, and the `MissionReport.AgentLines`
cases — see below) — plus 4 manual tests excluded from that count and from CI
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

**Fase 6 (Instalación de CLIs) is in progress: the service layer, its
ViewModel, and a first UI surface are all built and unit-tested, but nothing
has run on real Windows yet.** `ProviderInstaller.cs` covers npm-based CLI
install, headless sign-in (reusing the now-confirmed ConPTY primitive the
same way the Swift original reuses `openpty`), Gemini's
TUI-nudge-and-creds-mtime success detection, Antigravity-migration detection,
and the unified install-then-sign-in `Connect()` flow. `ConnectViewModel.cs`
consumes that event stream for a UI (same pattern as `ChatViewModel`), and
`MainPage.xaml` now has a "Connect Claude" button/flyout wired to it — 35 new
tests between the two (195 automated total). Deliberately deferred, each for
being a platform redesign rather than a missing translation: an automated
Node.js bootstrap (Swift's `installNode()` shells out to Homebrew; Windows
has no single trusted equivalent verified in this port, so it falls back to
sending the user to nodejs.org, the same terminal state Swift itself uses
when Homebrew isn't present), and the Terminal.app/AppleScript fallback
(already dead code in Swift today — no provider needs a visible terminal
anymore). Opening the sign-in URL in a browser IS wired up (`ConnectViewModel`'s
injectable `openUrl`, defaulting to `Process.Start(UseShellExecute: true)`) —
unlike the rest of Fase 6, this one piece is exercised by a real system call
in production, just not yet run for real. Two things specifically need a real
Windows machine before this phase can close: the `Flyout` with `x:Bind`
content (a pattern not used elsewhere in this project yet, though the same
underlying mechanism already works for `ChatList`'s `DataTemplate`), and —
the actual point of this phase — clicking "Connect Claude" against a real
npm install and a real `claude auth login`. `ManualProviderInstallerSmokeTest`
(new — see "Testing the pieces that need a real Windows machine" above) is
the tool for exactly that second piece, split into a safe-to-run-unattended
install check and an opt-in (`CORAL_MANUAL_RUN_SIGNIN=1`), interactive
sign-in check that's honest about replacing your machine's Claude login when
run. See `PORT-PLAN.md` §6/§9 for the full breakdown.

**Fase 7 (Code mode) started: `CodeGit`'s pure parsers and read-only git
operations are ported and unit-tested (15 tests, 210 automated total), but
Code Mode has no UI yet.** Same scope discipline as Fases 4/6: the diff/
changed-files parsers and the read-only real-git calls (current branch,
branch stat, changed files, has-uncommitted-changes) are done, reusing the
existing `IProcessLauncher` since git is a plain one-shot subprocess with no
new Windows primitive to build. Deliberately not ported yet: every git WRITE
operation (stage/commit/push/clone/branch — the git panel's actions, with no
UI to consume them yet) and every worktree operation (used only for loop
isolation, which is Fase 8's zone requiring human review of the diff, not
just green tests — porting worktree logic here would sidestep that gate).
Written while GitHub Actions was down (see below) — none of this needs
Windows or CI to build/test, so there was no reason to wait idle.
