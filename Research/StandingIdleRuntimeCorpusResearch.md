# Standing Idle Runtime and Corpus Research

## Scope

This read-only investigation used the installed Dancy Debug plugin, the active
Conduit developer bridge, the active VFXEditor developer plugin, current game
data, and the Penumbra library at `C:\Users\Nick\Documents\FFXIV Mods`.

No Penumbra setting, mod metadata, redirect, generated PAP, writer path, or
creator asset was changed. The machine-readable index is deliberately local
and untracked at `Research/PenumbraAnimationCorpusIndex.json`; it contains only
metadata, structure, and hashes.

## Runtime Observation Capability

The loaded VFXEditor developer plugin resolves the local player object and
Conduit safely reads the local player's name, position, rotation, moving flag,
casting flag, and action-manager animation lock. It does not expose a current
PAP path, ActionTimeline identity, animation event, section index, pose index,
or draw-object animation slot through a supported surface.

Controlled Conduit chat commands were issued only for the local player:

| Sequence | Command acknowledgements | Supported after-state observations | Conclusion |
| --- | --- | --- | --- |
| Neutral baseline | none | stationary, not casting, animation lock `0` | Control captured. |
| Standing pose cycle | `/changepose` five times, spaced by 1.5 seconds | every command call completed; position, rotation, moving, casting, and lock stayed stable | No supported PAP/section signal changed or was available. |
| Sit control | `/sit`, `/changepose`, `/sit` | every command call completed; the same generic signals remained stable | The bridge can issue the state command but cannot identify its active PAP section. |
| Ground-sit control | `/groundsit`, `/changepose`, `/groundsit` | every command call completed; the same generic signals remained stable | Same limitation as the sit control. |

`/doze` was not used. The available read-only signals could not prove a safe
entry and return for a lying state, so treating it as a control would have
added an unverified state transition without improving the evidence.

This is negative runtime evidence only. It does not show that either Standing
Idle section is absent, inactive, additive, or optional.

## Standing Idle Structure

The existing current-game observation remains unchanged for all 16 resolved
`bt_common/resident/idle.pap` variants, which are Dancy's normal Standing Idle target:

| Section | Header event | Havok index | TMB index | Header type | Runtime role |
| --- | --- | --- | --- | --- | --- |
| A0 | `cbna_add_dmg_f` | 0 | 0 | 15 | Unknown |
| A1 | `cbnm_id0` | 1 | 1 | 0 | Unknown |

The index records that header-to-Havok binding is explicit, while
header-to-TMB binding is only sequential PAP structure. The PAP contains one
Havok payload, so a differing payload hash cannot be attributed honestly to an
individual section without a separate Havok-level parser.

The installed Noffletoff Smoking Idle directory and its matching PMP both map
one-section `cbnm_id0` PAPs to race-specific `bt_common/resident/idle.pap` paths. This is
corpus evidence that creators associate `cbnm_id0` with the visible idle
motion. It is not proof that A1 can be transplanted independently into a
two-section target.

## Corpus Method and Integrity

The live read-only corpus suite completed `6/6 PASS`.

| Measure | Result |
| --- | ---: |
| Library files | 69,050 |
| Library bytes | 206,509,610,995 |
| Indexed mod documents | 2,235 |
| PAP mappings | 19,009 |
| Structural PAP summaries | 18,856 |
| Payload-hashed PAPs | 1,063 |
| Multi-section PAP mappings | 764 |
| All `resident/idle` mappings, including armed job idles | 473 |
| Canonical `bt_common/resident/idle` Standing Idle mappings | 469 |
| Canonical Standing Idle mappings with multiple sections | 136 |
| Relevant vanilla paths requested/resolved | 424 / 151 |
| Compared mappings | 625 |
| Corpus inventory samples | 64 before and after, all equal |

All library files and the deterministic sampled hashes were identical before
and after indexing. Archives were opened read-only in place. Directory PAPs
were structurally streamed; full payload hashes were captured only for
`resident/idle` and discovered multi-section PAPs, avoiding a broad asset load
inside the game process.

## Creator Corpus

### Noffletoff

The metadata-author filter found 21 installed author-associated PAP mod
documents, including the Smoking Idle directory and matching PMP. Smoking Idle
has 17 race-option canonical Standing Idle mappings per physical package, all defaulted
within their male/female option groups. Its mapped PAPs are one-section
`cbnm_id0` files; they are useful role evidence, but not a partial-section
replacement example.

Other Noffletoff-associated multi-section examples are not Standing Idle:
Toybox Hitachi Wand has two multi-section mappings and Dawntrail Expression
Library has 54. They demonstrate that the corpus index preserves complicated
creator options and multi-section shape without asserting runtime semantics.

### raykie

The metadata-author filter found 31 raykie PAP mod documents. They provide
large option-aware animation populations for emotes, chair states, ground
states, and lying states, but this installed raykie subset has no canonical
Standing Idle mapping and no multi-section PAP mapping. It is therefore useful
as a negative corpus result, not role evidence for Standing Idle.

### Other Useful Creators

The strongest canonical Standing Idle corpus consists of five distinct mods:
Viktoria's Idles 2.0 Megapack, Keow MoogleLover's Eorzean Nightlife,
ogRayrei's Male Miqo Relaxed Default Idle, Coldship's Rust Idle, and Massimo's
Upright Hrothgar - Male Hyur. They span multiple races, Single/Multi option
groups, defaults, and option priorities. The Noffletoff Smoking Idle PMP
duplicates its installed directory and is counted as a separate physical
source, not independent creator evidence. Lumiry's Record Keeper and Dual
Magnums are useful multi-section *armed* resident-idle controls, not canonical
Standing Idle replacements.

## Vanilla Versus Modded Standing Idle

There are 122 comparable two-section canonical Standing Idle mappings. Every one has a
different whole-Havok payload from vanilla. Per-section Havok status is
therefore `unknown-section-boundary-in-different-whole-payload` for every
comparison; this report intentionally does not claim a per-section HKX match.

| Pattern | Rows | Independent mods | Header events | TMB 0 vs vanilla | TMB 1 vs vanilla |
| --- | ---: | --- | --- | --- | --- |
| Both TMBs changed, headers retained | 64 | Eorzean Nightlife, Idles 2.0, Rust Idle, Upright Hrothgar | `cbna_add_dmg_f`, `cbnm_id0` | changed | changed |
| TMB 0 retained, TMB 1 changed, headers retained | 31 | ogRayrei, Eorzean Nightlife, Idles 2.0, Rust Idle | `cbna_add_dmg_f`, `cbnm_id0` | byte-identical | changed |
| Both TMBs retained, headers retained | 10 | Eorzean Nightlife, Idles 2.0 | `cbna_add_dmg_f`, `cbnm_id0` | byte-identical | byte-identical |
| TMB 0 changed, TMB 1 retained, headers retained | 1 | Eorzean Nightlife | `cbna_add_dmg_f`, `cbnm_id0` | changed | byte-identical |
| Header order changed | 16 | Idles 2.0 | `cbnm_id0`, `cbna_add_dmg_f` | changed | changed |

The second row is the best natural structural experiment in the installed
corpus: multiple unrelated mods retain TMB 0 exactly while changing TMB 1.
That supports the possibility that the two timeline sections can be represented
separately. It does not establish that the untouched motion remains playable,
that the Havok data is independently preserved, or that Dancy may safely write
only A1.

## Other Multi-Section PAPs

The corpus contains structurally useful shapes beyond Standing Idle: 198
two-section/two-TMB mappings, 22 three-section mappings, 39 four-section
mappings, and larger containers up to 164 sections. Useful examples include
Stretch (two sections), job resident idle/action files, movement files with six
or eight sections, Lumiry's two-section Record Keeper armed-idle control, and
Dual Magnums' four-section armed resident container.
These confirm that multi-section PAPs are common container forms, not that a
single generic section-replacement policy is valid.

## Decision

Standing Idle remains `MultiSectionUnknown`.

`cbnm_id0` is a likely ordinary Standing Idle motion candidate with LOW
confidence: the one-section Smoking Idle mapping and its event name agree with
that interpretation, but no supported runtime signal observed the active
section. `cbna_add_dmg_f` remains Unknown: neither file structure nor runtime
observation proves it is auxiliary, additive, conditional, or simultaneous.

The corpus supplies MEDIUM-confidence evidence that TMB sections can differ
independently in creator packages. It does not prove Havok-section independence
or an engine-supported partial replacement. The planner must remain fail-closed
and no Standing Idle writer, redirect, metadata, or UI enablement is justified.

## Next Recommended Step

Create one opt-in, player-scoped visual research protocol for a known working
corpus example that retains TMB 0 while changing TMB 1, with an exact setting
snapshot and restoration check. Its sole purpose should be to compare observed
Standing Idle behavior against its vanilla state; it must not generate or write
a Dancy PAP.
