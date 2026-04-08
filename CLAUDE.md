# CLAUDE.md -- Live Stack Custom Fork

Custom fork of isbeorn's Live Stack plugin for NINA.
Primary feature: multi-night stacking (resume stacks across imaging sessions).

---

## Project Overview

- **Upstream**: github.com/isbeorn/nina.plugin.livestack
- **Fork**: github.com/vorticose/nina.plugin.livestack
- **Language**: C# / .NET 8, targeting net8.0-windows
- **UI**: WPF
- **GUID**: `10bc1716-54af-425e-b307-c0ca1ce10600` (same as stock — intentional)
- **Plugin location**: `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Livestack\`

## Branching Strategy

```
main       <- clean mirror of upstream (isbeorn), never commit custom work here
dev        <- integration branch for custom changes
feature/*  <- short-lived branches off dev
```

- `origin` = vorticose fork, `upstream` = isbeorn
- Sync upstream: `git checkout main && git fetch upstream && git merge upstream/main`
- Then merge into dev: `git checkout dev && git merge main`

**Day-to-day workflow:**
1. Cut a feature branch: `git checkout dev && git checkout -b feature/my-feature`
2. Do the work, commit freely
3. Merge back: `git checkout dev && git merge feature/my-feature`
4. Delete: `git branch -d feature/my-feature`

## Build

```bash
dotnet build nina.plugin.livestack.sln -c Release
```

Post-build automatically copies DLL to `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Livestack\`.

## Key Directories

```
Image/              - Core data structures (LiveStackBag, LiveStackItem, ImageMath, ImageTransformer)
LivestackDockables/ - UI and main stacking controller (LivestackDockable.cs)
MultiNight/         - Multi-night stacking feature (sidecar, resume logic)
Instructions/       - NINA sequencer instructions (Start/Stop LiveStack, StackFlats)
QualityGate/        - Frame quality gate system
Properties/         - AssemblyInfo
```

## Related Source Repos

- **NINA source**: `C:\Users\Evan\Documents\nina-source`
- **Night Summary**: `C:\Users\Evan\Documents\nina.plugin.template`
- **Target Scheduler fork**: `C:\Users\Evan\Documents\nina.plugin.targetscheduler`

Always check these for API reference before guessing at interfaces.

## Multi-Night Implementation Plan

Full plan: `MULTINIGHT-PLAN.md` in repo root.

## Deploy

Do NOT deploy automatically. Always ask the user before running any deploy operation.
