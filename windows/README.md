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

`Coral.Core` today has one ported vertical slice: `Services/ModelCatalog.cs`, the
equivalent of `StrategyForge/Services/ModelCatalog.swift` — built-in model defaults per
provider, plus parsing the live `models.json` (the shared contract above) as an override.
`Coral.Tests/ModelCatalogTests.cs` parses the *real* repo-root `models.json` (copied into
the test output at build time), not a fixture that can drift from it.

## First build

Requires **Windows + Visual Studio 2022** (17.11+) with the ".NET Desktop Development"
and "Windows application development" workloads, or the .NET 8 SDK + `dotnet` CLI. Open
`Coral.sln`, or from a terminal:

```
dotnet test windows/Coral.Tests/Coral.Tests.csproj -c Release
dotnet build windows/Coral/Coral.csproj -c Release -p:Platform=x64
```

> This scaffold was authored without access to a Windows machine, so it has **not**
> been build-verified locally — `windows-tests.yml` (below) is the first real check.
> If a NuGet package version or a WinUI 3 project-file setting turns out to be wrong,
> that's expected for a first pass; fix it in place rather than starting over.

## CI

`.github/workflows/windows-tests.yml` triggers on changes under `windows/**` and to
`models.json` (which `Coral.Tests` now reads as a fixture), runs on `windows-latest`, and
does two things: `dotnet test` on `Coral.Tests` (fast — `Coral.Core` has no WinUI
dependency), then `dotnet build` on the `Coral` app project to catch WinUI 3/Windows App
SDK restore problems separately from test failures. Independent of the macOS `tests.yml`
gate either direction: changes here never trigger a macOS run, and macOS-only changes
(anything outside `windows/**`, `models.json`, `skills.json`) never trigger this one.

## Status

**Fase 1 (scaffolding) done**, per `PORT-PLAN.md`'s stack recommendation (WinUI 3 +
.NET 8/C#, confirmed): solution + WinUI 3 app project (unpackaged) + `Coral.Core` +
xUnit tests, with one real ported service (`ModelCatalog`) rather than an empty shell.
Not yet build-verified on real Windows/Visual Studio — watch the first `windows-tests.yml`
run. Next up is **Fase 2**: port the rest of `Models/` and `Generators/` into
`Coral.Core` (pure logic, highest ROI, mirrors the macOS test coverage in
`GeneratorTests`/`ModelJSONTests`/etc.).
