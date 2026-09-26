# Dancy

Dancy creates non-destructive Penumbra options that remap a selected animation-mod PAP to a chosen emote's loop PAP paths. The original mod options and files remain unchanged.

## Normal workflow

1. Open `/dancy`, choose a Penumbra mod, and scan it.
2. Choose one or more source PAP paths from an option. Paired options can be narrowed to one row with the checkbox or `Only` button.
3. Choose a loop-capable target emote. Non-loop targets are hidden by default and can be included deliberately.
4. Review the mapping preview, then create the override.

Dancy writes generated PAPs under `yucksdancy/paps` and adds or updates its `Yuck's Dancy` option. Running the same source/target selection again uses the same override ID and output names instead of endlessly appending options.

## Safety and recovery

- The source PAP must stay inside the selected mod directory.
- Generated PAPs are created before metadata is changed.
- `meta.json` and legacy `group_*.json` changes are validated, written through a temporary file, and retain a `.dancy.bak` copy of the previous JSON.
- Removing Dancy deletes only its own group files and `yucksdancy` directory. If its metadata write cannot be persisted, generated files are left untouched.
- After any metadata change Dancy asks Penumbra to reload the mod. A reload warning means the override is written, but Penumbra's UI may need a manual mod reload or Refresh Data.

## Diagnostics

After an apply attempt, the wizard footer exposes `Copy diagnostics`. It includes the Dancy assembly version/MVID, plan ID, source and target identities, match strategies, PAP event identifiers, metadata format, warnings, and reload outcome. Do not paste a diagnostic that contains local paths into a public channel without reviewing it first.

## Cross-rig status

Dancy recognizes the standard `c0101` through `c1801` character-path identities and reports when it must expand a source PAP across target variants. That is path mapping, not skeletal retargeting.

No automatic cross-rig HKX, transform-track, or face-data retargeter is included. Real assets and in-game test cases are required before such a feature can be considered safe; the current supported path remains ordinary PAP override creation.

## Validation

Run the focused tests:

```powershell
dotnet test Dancy.Tests/Dancy.Tests.csproj -c Debug
```

Build the developer DLL:

```powershell
dotnet build Dancy/Dancy.csproj -c Debug -p:Platform=x64
```

The local game test artifact is `Dancy/bin/x64/Debug/Dancy.dll`.

The automated tests cover target-match fallback, deterministic plans, mapping-conflict refusal, metadata upsert idempotency, and atomic JSON backups. They do not replace in-game validation against real PAP/TMB/HKX assets.

### Confirmed Regression

The real installed-mod `Bench Press - /pushups` to `Water /water` regression was manually confirmed in game on 2026-09-26: `/water` played the intended Push-ups animation without a T-pose. The automated regression also verifies loop-only source selection, current Water event identifiers, generated PAP timeline references, runtime redirects, cleanup, and idempotency.
