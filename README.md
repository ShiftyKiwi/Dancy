# Dancy

Dancy is an Early Access Dalamud plugin for creating non-destructive animation
overrides inside installed Penumbra mods. It copies only the PAP files needed
for an override, creates a Dancy-owned option in the selected mod, and leaves
the original mod's options and files untouched.

## Requirements

- FINAL FANTASY XIV with Dalamud.
- Penumbra installed, enabled, and configured with a readable mod directory.
- A Penumbra animation mod containing PAP redirects to use as a source.

## Workflow

1. Open `/dancy`, select a Penumbra mod, and scan its options.
2. Choose a source option. Dancy separates normal loop PAPs from start,
   transition, end, and unknown paths so only valid loop sources are selected
   for a normal override by default. Paired options can be narrowed to one
   game path when only one side should change.
3. Choose a target. The default `Looped Emotes` tab is for regular looping
   emotes. `Poses & Idles` contains persistent character states such as Standing
   Idle, chair sit, ground sit, sleep/lying, and supported Change Pose families.
   `One-shot / Advanced` contains targets whose duration is controlled by the
   game and may end naturally.
4. Review Dancy's mapping preview, source/target variants, and structural PAP
   compatibility result. Create the override only when the preflight permits it.

Target search spans these categories and labels each result with its playback
behavior and state context. Raw paths are retained in the expandable Details
section rather than crowding the normal summary.

## Override Lifecycle

Dancy writes generated PAPs under `yucksdancy/paps` and adds or updates a
`Yuck's Dancy` option. Repeating the same source and target selection updates
the existing Dancy-owned override instead of accumulating duplicate options.

You can remove an individual Dancy override or remove all Dancy overrides from
the selected mod. Dancy removes only its metadata and generated files, then
asks Penumbra to reload the mod. If Penumbra's supported reload API has a
transient failure, Dancy retries once and reports whether disk cleanup completed
even when the Penumbra UI needs a manual refresh.

## Diagnostics

`Copy diagnostics` records the active Dancy build, selected source and target,
mapping strategy, PAP event identifiers, metadata format, structural preflight,
and Penumbra reload result. Review diagnostics before sharing because they can
contain local file paths.

## Known Limitations

- Standing Idle is supported only for canonical `normal/idle` variants that
  pass Dancy's exact two-section preflight. Dancy replaces the primary idle
  motion while preserving the target-native auxiliary motion and both timelines.
  This does not enable arbitrary multi-section PAP editing.
- Some `One-shot / Advanced` targets finish naturally because their duration is
  controlled by the game.
- A semantic target classification never bypasses structural PAP preflight.
- Dancy is Early Access. Test an override on a copy of an important mod first.

Dancy does not include animation retargeting, skeleton editing, or archived
animation-transformation experiments.

## Development Validation

Run the focused offline suite:

```powershell
dotnet test Dancy.Tests/Dancy.Tests.csproj -c Debug
```

Build the developer DLL:

```powershell
dotnet build Dancy/Dancy.csproj -c Debug -p:Platform=x64
```

The local developer artifact is `Dancy/bin/x64/Debug/Dancy.dll`. Offline tests
do not replace in-game validation against real Penumbra mods and client PAP
data.

For a topology already proven by a prior runtime validation, the production
regression may be accepted automatically when Dancy reparses the generated PAP,
verifies source and preserved-target fingerprints/timelines, Penumbra reports
the canonical target resolving to the generated PAP, Conduit invokes the
relevant state command, and cleanup restores the exact prior state. A human
visual check remains appropriate for a new topology, a new replacement model,
or an unexplained structural/runtime mismatch.
