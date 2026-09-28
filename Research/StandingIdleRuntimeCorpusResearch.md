# Standing Idle Runtime and Corpus Research

## Scope

This read-only investigation used the installed Dancy Debug plugin, the active
Conduit developer bridge, the active VFXEditor developer plugin, current game
data, and the Penumbra library at `C:\Users\Nick\Documents\FFXIV Mods`.

No persistent Penumbra setting, mod metadata, redirect, generated PAP, writer
path, or creator asset was changed. The later player-scoped validation used a
dedicated temporary setting only after a restoration preflight, then removed it
and verified the original state. The machine-readable index is deliberately
local and untracked at `Research/PenumbraAnimationCorpusIndex.json`; it contains
only metadata, structure, and hashes.

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

## Previous Recommended Step

The opt-in, player-scoped validation protocol described above has now been
implemented and run. It is intentionally Debug-only, leaves no temporary
setting behind, and still does not generate or write a Dancy PAP.

## Per-Motion Havok Fingerprinting

The active VFXEditor developer plugin now exposes a read-only selected-motion
fingerprint endpoint. It loads a PAP through VFXEditor's already-initialized
Havok model, samples the selected `PapMotion` against the matching race
skeleton at 30 FPS, and SHA-256 hashes the normalized per-bone local
translation, rotation, and scale samples. The fingerprint contains the binding
of transform tracks to skeleton bones, but does not incorporate the requested
motion index or repeat the whole shared Havok payload hash.

This method was validated with repeated reads of a single-motion Water PAP,
the installed single-motion Push-ups source, and both motions of the local
player's canonical Standing Idle PAP. Repeated fingerprints were stable. The
local `c0701` Standing Idle PAP has distinct motion fingerprints for Havok 0
and Havok 1, with 95 and 97 animated tracks respectively, so the result is
motion-specific rather than a shared-container identity.

The corrected read-only per-motion corpus pass completed `4/4 PASS`:

| Measure | Result |
| --- | ---: |
| Canonical two-section mappings | 136 |
| Readable, matching-rig comparisons | 80 |
| Unknown comparisons | 56 |
| Motion 0 same, Motion 1 changed, TMB 0 same, TMB 1 changed | 5 |
| Corpus files / bytes | 69,050 / 206,509,610,995 |
| Deterministic source-hash samples | 64 unchanged |

The full observed matrix was: 50 changed/changed/changed/changed; 16
changed/changed/same/changed; five changed/changed/same/same; five
same/changed/same/changed; two same/same/same/same; one
changed/changed/changed/same; and one same/changed/same/same. Values are in
`Motion 0 / Motion 1 / TMB 0 / TMB 1` order. The five strongest natural
experiments include Coldship's Rust Idle for `c0101`, three Keow MoogleLover
Eorzean Nightlife options for `c0801`, and one Eorzean Nightlife option for
`c1401`.

This is strong evidence that installed creator PAPs can retain the sampled
Havok 0 motion and TMB 0 while changing sampled Havok 1 and TMB 1. It is not a
serialized-section boundary or a safe merge/write recipe. The existing writer
continues to fail closed for multi-section Standing Idle PAPs.

## Player-Scoped Existing-Mod Validation

Two existing c0701-compatible installed mods were tested only through the
supported `SetTemporaryModSettingsPlayer` / `RemoveTemporaryModSettingsPlayer`
Penumbra API. Before either temporary setting was applied, Dancy captured the
local player's effective collection, candidate persistent state and group
selections, current idle resolution, relevant Changed Items count, and
candidate PAP SHA-256. The test refuses to run if a candidate already has any
temporary setting, because replacing an unknown temporary owner would not be
exactly reversible.

| Candidate | Author | Option | Motion 0 / 1 | TMB 0 / 1 | Runtime redirect | Restoration |
| --- | --- | --- | --- | --- | --- | --- |
| Male Miqo Relaxed Default Idle | ogRayrei | DefaultData | changed / changed | same / changed | c0701 path resolved to the candidate PAP | PASS |
| Rust Idle | Coldship | `Races: Miqo'te` | changed / changed | changed / changed | c0701 path resolved to the selected c0101 candidate PAP | PASS |

The Rust option was already persistently enabled for the local player before
the experiment. Dancy temporarily disabled it to prove a candidate-free
control mapping, restored that state, then used its isolated Miqo'te option for
the timed test. After both tests, DAB readback confirmed the original effective
collection, persistent enabled state, multi-option selection, resolved path,
and candidate PAP hashes exactly matched their snapshots. No collection,
metadata, redirect, archive, or source file was changed.

Conduit ran four paced `/changepose` inputs for each timed observation window.
There is no supported pose or skeleton oracle. Human validation confirmed that
Rust Idle animated correctly through the idle-pose cycle with no T-pose,
freezing, or visible corruption. `[IV] ogRayrei Male Miqo Relaxed Default Idle`
was subsequently enabled normally for Dalkand. A separate DAB read-only check
confirmed that its persistent setting was enabled and the live c0701 idle path
resolved directly to its installed PAP before human validation confirmed a
clean full idle-pose cycle. Neither visual result is inferred from redirection
alone.

## Evidence Escalation Decision

| Claim | File structure | Per-motion fingerprint | Creator corpus | Player-scoped test | Confidence |
| --- | --- | --- | --- | --- | --- |
| `cbnm_id0` / Havok 1 / TMB 1 is the ordinary Standing Idle motion | Type 0, explicit Havok 1 binding | distinct local motion 1; five natural examples change motion 1 while retaining motion 0 | repeated working complete PAP combinations | Rust Idle completed the c0701 pose cycle without visible corruption | MEDIUM |
| `cbna_add_dmg_f` / Havok 0 / TMB 0 can remain target-native | Type 15, explicit Havok 0 binding | five natural examples retain sampled motion 0 and TMB 0 | present across Rust Idle and Eorzean Nightlife examples | Rust changed both motions/TMBs; no player-compatible natural example was visually confirmed | MEDIUM |
| The sections are independently replaceable by Dancy | two explicit Havok indices, one shared container | motion identities can differ independently in existing PAPs | five exact natural examples | Rust validates a complete PAP only; no partial write was attempted | LOW |

Standing Idle remains `MultiSectionUnknown`. No Dancy Standing Idle PAP was
written, no production target was enabled, and no production PAP writer was
changed. The evidence improves semantic confidence in the observed existing
PAPs, but does not establish an engine-level safe partial replacement or
writer readiness.
