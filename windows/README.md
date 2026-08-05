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

Everything Windows-specific goes under this directory. Suggested shape once the project
exists (adjust to whatever the chosen stack actually needs):

```
windows/
  Coral.sln
  Coral/            # app project
  Coral.Tests/       # test project
  README.md          # this file
```

## CI

`.github/workflows/windows-tests.yml` triggers only on changes under `windows/**` and
currently runs a placeholder job — replace it with a real build+test step once there's a
project to build. It runs on `windows-latest` and is independent of the macOS
`tests.yml` gate; changes here never trigger a macOS run, and macOS-only changes
(anything outside `windows/**`, `models.json`, `skills.json`) never trigger this one.

## Status

Scaffolding only — no project yet. `PORT-PLAN.md` proposes WinUI 3 + .NET (C#) with
MSIX packaging; once that's confirmed, first contribution is Phase 1 from that plan:
scaffold the solution here, wire `windows-tests.yml` to a real `dotnet test` step, and
replace this section with real "first build" instructions (mirroring the macOS "First
build" section in the root `CONTRIBUTING.md`).
