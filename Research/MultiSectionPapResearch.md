# Dancy Multi-Section PAP Research

## Current Writer Assumptions

| Location | Current assumption | Why safe for current targets | Why unsafe for multi-section targets |
| --- | --- | --- | --- |
| `PapFileInspector.Inspect` | Reads `AnimationCount` header records and the same number of sequential TMB sections. | Validates the one-header/one-TMB container Dancy currently supports. | It exposes structure, not semantic section roles or independence. |
| `PapEditor.ReadTargetEventIdentifier` | Uses `AnimationNames.FirstOrDefault()`. | A one-section target has one possible event. | Selects a section by position without identifying its role. |
| `PapEditor.ReadTimelineEventIdentifiers` | Returns one flattened list across all TMB sections. | Diagnostics only need the single current section. | Loses the section that owned an event reference. |
| `PapEditor.PatchPap` | Rewrites `animationHeaders[0]` and `tmbSections[0]` in a source-derived PAP. | The sole header and TMB are the complete target container topology. | A source-derived file cannot preserve target-native companions, and index zero is not a proven primary role. |
| `PapCompatibilityPreflight.Evaluate` | Requires source and every target to be exactly one header and one TMB. | Prevents the writer from silently editing only the first section. | Correctly blocks all currently unknown multi-section files. |
| `OverrideExecutionService.CreatePapCopies` | Groups target paths by the first target event, then invokes the source-centric writer. | One event represents each supported target PAP. | Multi-section targets may have more than one event and require a target-section identity. |
| `DancyPushupsWaterRegressionRunner` | Validates the generated PAP as a one-header, one-TMB output. | Matches the Water regression contract. | It is intentionally not a multi-section output validator. |
| `MainWindow.GetTargetPapStatus` | Leaves multi-section targets disabled. | Prevents user-facing production use beyond the writer's contract. | Must remain in place until an independent structural proof and writer milestone exist. |

## Water Control

`/water` resolves seven current player variants. Every inspected PAP has one animation header, one explicit Havok index (`0`), and one sequential TMB (`0`). The header event and that TMB's C009 reference both equal `cbem_sp60_2lp`; this is a trivial `SingleSection` topology.

| Race | Game path | Header / Havok / TMB | TMB offset / size | C009 |
| --- | --- | --- | --- | --- |
| c0101 | `chara/human/c0101/animation/a0001/bt_common/emote_sp/sp60_loop.pap` | A0 `cbem_sp60_2lp` / H0 / T0 | 17408 / 1011 | `cbem_sp60_2lp` |
| c0201 | `chara/human/c0201/animation/a0001/bt_common/emote_sp/sp60_loop.pap` | A0 `cbem_sp60_2lp` / H0 / T0 | 16544 / 1011 | `cbem_sp60_2lp` |
| c0501 | `chara/human/c0501/animation/a0001/bt_common/emote_sp/sp60_loop.pap` | A0 `cbem_sp60_2lp` / H0 / T0 | 17536 / 1011 | `cbem_sp60_2lp` |
| c0601 | `chara/human/c0601/animation/a0001/bt_common/emote_sp/sp60_loop.pap` | A0 `cbem_sp60_2lp` / H0 / T0 | 17456 / 1011 | `cbem_sp60_2lp` |
| c0801 | `chara/human/c0801/animation/a0001/bt_common/emote_sp/sp60_loop.pap` | A0 `cbem_sp60_2lp` / H0 / T0 | 17312 / 1011 | `cbem_sp60_2lp` |
| c0901 | `chara/human/c0901/animation/a0001/bt_common/emote_sp/sp60_loop.pap` | A0 `cbem_sp60_2lp` / H0 / T0 | 20832 / 1011 | `cbem_sp60_2lp` |
| c1101 | `chara/human/c1101/animation/a0001/bt_common/emote_sp/sp60_loop.pap` | A0 `cbem_sp60_2lp` / H0 / T0 | 16896 / 1011 | `cbem_sp60_2lp` |

Binding evidence: the header's Havok index is an explicit 16-bit PAP header field. VFXEditor's PAP parser reads one TMB after each animation object in sequence, so the only current header-to-TMB relationship is structural and positional, not an explicit per-header pointer.

## Standing Idle Structure

`normal/idle` resolves 16 current variants. Every one has two headers and two TMB sections. Header-to-Havok is explicit; header-to-TMB is the sequential PAP-parser relationship. `c0301` and `c1201` do not currently resolve a player Standing Idle PAP.

| Race | Game path | A0 / H0 / T0 | A1 / H1 / T1 |
| --- | --- | --- | --- |
| c0101 | `chara/human/c0101/animation/a0001/bt_common/resident/idle.pap` | `cbna_add_dmg_f`, type 15, T0 193 bytes, C009 same | `cbnm_id0`, type 0, T1 161 bytes, C009 same |
| c0201 | `chara/human/c0201/animation/a0001/bt_common/resident/idle.pap` | `cbna_add_dmg_f`, type 15, T0 141 bytes, C009 same | `cbnm_id0`, type 0, T1 161 bytes, C009 same |
| c0401 | `chara/human/c0401/animation/a0001/bt_common/resident/idle.pap` | `cbna_add_dmg_f`, type 15, T0 141 bytes, C009 same | `cbnm_id0`, type 0, T1 161 bytes, C009 same |
| c0501 | `chara/human/c0501/animation/a0001/bt_common/resident/idle.pap` | `cbna_add_dmg_f`, type 15, T0 141 bytes, C009 same | `cbnm_id0`, type 0, T1 161 bytes, C009 same |
| c0601 | `chara/human/c0601/animation/a0001/bt_common/resident/idle.pap` | `cbna_add_dmg_f`, type 15, T0 141 bytes, C009 same | `cbnm_id0`, type 0, T1 161 bytes, C009 same |
| c0701 | `chara/human/c0701/animation/a0001/bt_common/resident/idle.pap` | `cbna_add_dmg_f`, type 15, T0 141 bytes, C009 same | `cbnm_id0`, type 0, T1 161 bytes, C009 same |
| c0801 | `chara/human/c0801/animation/a0001/bt_common/resident/idle.pap` | `cbna_add_dmg_f`, type 15, T0 193 bytes, C009 same | `cbnm_id0`, type 0, T1 161 bytes, C009 same |
| c0901 | `chara/human/c0901/animation/a0001/bt_common/resident/idle.pap` | `cbna_add_dmg_f`, type 15, T0 141 bytes, C009 same | `cbnm_id0`, type 0, T1 161 bytes, C009 same |
| c1001 | `chara/human/c1001/animation/a0001/bt_common/resident/idle.pap` | `cbna_add_dmg_f`, type 15, T0 141 bytes, C009 same | `cbnm_id0`, type 0, T1 161 bytes, C009 same |
| c1101 | `chara/human/c1101/animation/a0001/bt_common/resident/idle.pap` | `cbna_add_dmg_f`, type 15, T0 141 bytes, C009 same | `cbnm_id0`, type 0, T1 161 bytes, C009 same |
| c1301 | `chara/human/c1301/animation/a0001/bt_common/resident/idle.pap` | `cbna_add_dmg_f`, type 15, T0 141 bytes, C009 same | `cbnm_id0`, type 0, T1 161 bytes, C009 same |
| c1401 | `chara/human/c1401/animation/a0001/bt_common/resident/idle.pap` | `cbna_add_dmg_f`, type 15, T0 141 bytes, C009 same | `cbnm_id0`, type 0, T1 161 bytes, C009 same |
| c1501 | `chara/human/c1501/animation/a0001/bt_common/resident/idle.pap` | `cbna_add_dmg_f`, type 15, T0 141 bytes, C009 same | `cbnm_id0`, type 0, T1 161 bytes, C009 same |
| c1601 | `chara/human/c1601/animation/a0001/bt_common/resident/idle.pap` | `cbna_add_dmg_f`, type 15, T0 141 bytes, C009 same | `cbnm_id0`, type 0, T1 161 bytes, C009 same |
| c1701 | `chara/human/c1701/animation/a0001/bt_common/resident/idle.pap` | `cbna_add_dmg_f`, type 15, T0 141 bytes, C009 same | `cbnm_id0`, type 0, T1 161 bytes, C009 same |
| c1801 | `chara/human/c1801/animation/a0001/bt_common/resident/idle.pap` | `cbna_add_dmg_f`, type 15, T0 141 bytes, C009 same | `cbnm_id0`, type 0, T1 161 bytes, C009 same |

The Debug inspector records the exact offset and full SHA-256 of every TMB section at run time. Content hashes vary among races despite the stable names and sizes, so a future writer must not replace a companion merely because its section number appears stable.

## Section Roles

| Section | Observed event | Role | Evidence | Confidence |
| --- | --- | --- | --- | --- |
| A0 / H0 / T0 | `cbna_add_dmg_f` | Unknown | Header type `15`, explicit Havok `0`, sequential TMB `0`, and matching C009 reference are observed. No safe runtime state oracle identifies its playback role. | Unknown |
| A1 / H1 / T1 | `cbnm_id0` | Unknown | Header type `0`, explicit Havok `1`, sequential TMB `1`, and matching C009 reference are observed. No safe runtime state oracle identifies its playback role. | Unknown |

The strings, type values, and stable position are evidence of identity, not evidence that either section is the ordinary idle, additive, optional, or independent. No semantic role was assigned.

## Cross-Race Consistency

- Stable topology: YES. All 16 resolved variants are 2 headers / 2 TMBs.
- Stable observed identities: YES. A0/H0/T0 is `cbna_add_dmg_f`; A1/H1/T1 is `cbnm_id0` in every resolved variant.
- Stable semantic roles: UNKNOWN. No runtime evidence establishes roles.
- Stable section indices: YES structurally, but not sufficient to authorize a role-based writer.

## Cross-Section Dependencies

The readable C009 reference in each Standing Idle TMB names its own header event. No C009 reference to the sibling index, sibling event, or another within-PAP motion was observed. This is negative evidence only: it does not inspect every TMB command or prove the Havok motions are independently selectable. The live DAB capability set has no safe actor timeline/skeleton-state oracle, and no native hook was added.

Classification: `MultiSectionUnknown`.

## Proposed Structural Model

The read-only prototype adds:

- `PapStructure`: source identity, header sections, TMB sections, topology, and evidence.
- `PapAnimationSection`: animation index/name/type/face flag, explicit Havok index, sequential TMB index, role, and binding confidence.
- `PapTimelineSection`: index, offset, byte size, SHA-256, C009 event identifiers, and an optional parser-inspection issue.
- `PapTopology`: `SingleSection`, `MultiSectionIndependent`, `MultiSectionCoupled`, or `Unknown`.
- `PapStructuralPlanner`: a pure, dry-run-only planner with no writer or file-I/O dependency.

`PapSectionRole` deliberately contains only `Unknown`. The model does not turn naming conventions into a semantic role claim.

## Target-Centric Writer Assessment

RECOMMENDED as the eventual architecture, but not ready to implement.

The existing source-centric writer copies a source PAP and patches its first header/TMB. A future target-centric writer should use a target PAP as the structural shell, identify one independently proven target motion slot, transplant a complete source motion without changing its tracks or keyframes, and retain untouched target headers, TMB payloads, identifiers, and their meaning. Header/TMB offsets may need structural relocation; untouched payloads should otherwise remain byte-identical. This approach is the only credible way to preserve target-native companion sections.

## Standing Idle Dry-Run Plan

The installed Push-ups Loop source resolved eight loop entries. Every physical source PAP inspected as `SingleSection`. Each corresponding Standing Idle target resolved as `Unknown`, so each dry-run result is:

| Source target race | Source topology | Target | Replacement | Preserved | Compatibility |
| --- | --- | --- | --- | --- |
| c0101, c0201, c0501, c0601, c0801, c0901, c1101, c1401 | SingleSection | matching `normal/idle` variant | none | none | Unsupported: target multi-section topology is not understood |

No file was written, no writer method was reachable from the planner, and no Penumbra metadata or redirect changed.

## Other Multi-Section Targets Found

The live catalog scan found multi-section variants for: Chuckle, Cry, Dance, Deny, Doze, Eureka, Examine Self, Flame Salute, Fume, Furious, Grovel, Joy, Overreact, Panic, Poke, Serpent Salute, Shocked, Songbird, Stagger, Storm Salute, Stretch, Sulk, Surprised, and Thumbs Up. Each has a mix of one-section variants and one or two 2-header/2-TMB variants. Standing Idle is the only discovered catalog entry with all currently resolved variants multi-section. None were enabled or broadened into production scope.

## Production Readiness

Standing Idle writer: BLOCKED BY UNKNOWN STRUCTURE.

The structural mapping is known, but role selection and cross-section independence are not. The existing one-section preflight remains authoritative, and Standing Idle remains disabled in the target UI.

## Validation

- Offline structural/model tests: 72/72 passed after adding the eight required planner cases.
- Live DAB multi-section research: 4/4 passed, with no retained artifacts.
- Live DAB target catalog: 7/7 passed.
- Live DAB core suite: 18/18 passed, including fixture cleanup and source-integrity verification.
- Live Push-ups to Water regression: 19/19 passed.
- Debug and Release builds: passed with zero compiler warnings or errors.
- Runtime provenance: DAB verified the deployed Debug DLL SHA-256 before the read-only run.
- `git diff --check`: passed with no whitespace errors.

## Next Recommended Implementation Step

Add one read-only, supported runtime-observation milestone that can establish whether the two Standing Idle sections are independently selected or simultaneously required; do not begin any writer work until that evidence exists.
