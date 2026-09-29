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

## Decisive c0701 Hybrid-Section Experiment

On 2026-09-28, a Debug-only VFXEditor endpoint constructed two disposable
target-shell variants under the Dancy temporary directory. The only source was
the human-validated ogRayrei Male Miqo Relaxed Default Idle PAP. Both source
PAPs were read-only inputs; the original ogRayrei mod, its options, and its
metadata were never written.

The target shell was the current vanilla c0701 `normal/idle` PAP. Its two
headers were retained in their original order: A0 `cbna_add_dmg_f`, type 15,
Havok 0/TMB 0; then A1 `cbnm_id0`, type 0, Havok 1/TMB 1. VFXEditor's
initialized Havok writer replaced the complete source motion 1 and its binding
inside a copied target shell. It did not edit tracks, keyframes, transforms, or
either input asset.

| Variant | A0 motion / TMB | A1 motion / TMB | SHA-256 | Structural result |
| --- | --- | --- | --- | --- |
| A1 | vanilla / vanilla | ogRayrei motion 1 / vanilla TMB 1 | `0B3C841409F178094F7050F9CAB21F80528E51C5BEF52570C463AA25FC557481` | PASS |
| A2 | vanilla / vanilla | ogRayrei motion 1 / ogRayrei TMB 1 | `9F10A20ADB68E9BA23B658414B6082495FDA35E8A80DE9DFDF326633F3B211CD` | PASS |

For both outputs, Dancy independently re-opened the PAP, verified exactly two
headers and two TMB sections, verified the original header ordering and
timeline hashes, and used VFXEditor's read-only motion fingerprints to prove
that A0 remained vanilla while A1 exactly matched ogRayrei's source motion 1.
The output's Havok binding count was also required to match the copied target
shell before Penumbra could see a redirect.

The player-scoped A1 run then passed its supported controls:

- Control V resolved c0701 `normal/idle` to the copied vanilla target shell.
- Control M restored the full ogRayrei source mapping and its original SHA-256.
- Hybrid A1 resolved only Dalkand's individual collection to its temporary PAP.
- Human observation found no visible T-pose, freeze, corruption, or broken
  Change Pose transition through the available cycle for both A1 and A2.
- The custom-versus-vanilla visual difference was not decisively
  distinguishable for either motion-1 variant. This is clean playback evidence,
  not proof that motion 1 alone drives the visible ordinary-idle appearance.
- Dancy removed the temporary redirect, re-verified the individual collection,
  original resolved source path, source bytes, source settings, and Changed
  Items count, then deleted the temporary workspace. No generated PAP remains.

The runtime result proves, for the tested c0701 Standing Idle topology only,
that a complete source motion 1 can replace the target's motion 1 while the
target-native motion 0, headers, and vanilla TMB 0/1 remain intact without
visible corruption. A1 did not need source TMB 1 to remain clean, but neither
motion-1 variant demonstrated a separately recognizable ordinary-idle look.

## Rust Reverse Hybrid B1

Because Rust's c0701 mapping changes both motions and both TMBs, the clean but
visually non-distinct Rust A1/A2 results required the prompt's reverse test.
Rust B1 used a vanilla c0701 target shell with complete Rust motion 0, vanilla
TMB 0, and untouched vanilla motion 1/TMB 1. B2 was constructed and statically
verified as the source-TMB-0 control, but was not activated because B1 already
showed the expected Rust idle.

| Variant | A0 motion / TMB | A1 motion / TMB | SHA-256 | Structural result |
| --- | --- | --- | --- | --- |
| Rust B1 | Rust motion 0 / vanilla TMB 0 | vanilla / vanilla | `20767E7A81074E741FCE8A4EF57EDCDD771C17FFE5E0ECE46D0AA24F40CECC68` | PASS |
| Rust B2 | Rust motion 0 / Rust TMB 0 | vanilla / vanilla | `A45F976FE2B22178D38FA0DEB98BE0687AB6B5DF2C8DD7AED17E4B2F66380577` | PASS |

Dancy statically re-opened both outputs and independently verified their two
headers, two TMB sections, original ordering, complete Rust motion-0
fingerprint, and preserved vanilla motion-1 fingerprint. B1 then resolved only
Dalkand's individual collection to its temporary PAP. Human observation
confirmed that the Rust idle appeared correctly across the pose cycle. Dancy
removed the temporary redirect and restored the user's intentionally active
Rust c0101 source path, source SHA-256
`E44B90430F03B821AEBB84BB28BEF9212B75A3720F6922ACFB296697CECDF5F3`,
settings, collection, and Changed Items count. No hybrid file remains.

This is direct visual evidence that c0701's motion 0 is independently
replaceable with a complete working Rust ordinary-idle motion while the target
retains motion 1 and both vanilla timelines. Rust B1 did not require source
TMB 0, so the tested safe structural unit is `MOTION ONLY` for Rust motion 0.
The earlier ogRayrei A1/A2 runs provide separate clean runtime evidence for a
motion-1 transplant, but do not establish that motion 1 is the visually active
ordinary-idle state.

Standing Idle is classified as `MultiSectionIndependent` with MEDIUM
confidence, qualified as: "c0701 Rust motion-0 replacement is visually proven
while preserving target section 1; ogRayrei motion-1 replacement is structurally
and playback-clean but visually non-distinct." This is not a claim about
arbitrary multi-section PAPs, other races, other sections, or source-to-target
retargeting.

Production remains blocked. A target-centric Standing Idle writer needs its own
design, implementation, and acceptance milestone; the existing one-section
preflight and disabled UI behavior remain authoritative.

## Final Validation Snapshot

The final post-B1 validation pass completed on 2026-09-29 without enabling a
production Standing Idle target or retaining a hybrid output:

| Check | Result |
| --- | --- |
| Rust B1 human visual observation | Rust appeared correctly through the idle-pose cycle |
| Per-motion installed-mod corpus | 4/4 PASS; 136 canonical mappings across 2,235 mods |
| Multi-section PAP research fixtures | 4/4 PASS |
| Current target catalog | 7/7 PASS |
| Dancy live core suite | 18/18 PASS |
| Push-ups to Water regression | 19/19 PASS |

The per-motion corpus verified the 69,050-file, 206,509,610,995-byte source
inventory and 64 deterministic sample hashes were unchanged across its
read-only pass. The B1 harness separately re-verified the restored Rust source
path, SHA-256, collection settings, and Changed Items count after removing its
temporary player-only redirect. Its temporary workspace was deleted.

These results establish the constrained c0701 evidence described above. They
do not authorize general multi-section writing, a Standing Idle product target,
or source-to-target retargeting.

## Production Support Finalization

This historical conclusion was superseded after implementation and automated
production validation of one target-centric writer. Dancy now supports only
canonical `normal/idle` PAP variants that satisfy all of these fixed facts:

- the game path is `bt_common/resident/idle.pap`;
- there are exactly two headers and two timeline sections;
- header 0 is `cbna_add_dmg_f`, type 15, Havok motion 0, non-face;
- header 1 is `cbnm_id0`, type 0, Havok motion 1, non-face.

The writer replaces complete source motion 0 only. It requires a one-section,
Havok-motion-0 Loop source and preserves target motion 1 plus both target
timeline sections. All other multi-section targets remain unsupported.

Current game-data preflight inspected all 16 resolved Standing Idle variants:
`c0101`, `c0201`, `c0401`, `c0501`, `c0601`, `c0701`, `c0801`, `c0901`,
`c1001`, `c1101`, `c1301`, `c1401`, `c1501`, `c1601`, `c1701`, and `c1801`.
Every current variant matched the fixed topology. A later divergent variant is
excluded individually rather than being inferred compatible from section counts.

The c0701 production regression generated a Dancy-owned PAP, reparsed it,
verified source motion 0 and preserved target motion 1 fingerprints, preserved
both target timeline hashes, and observed Penumbra resolve the local player's
canonical `normal/idle` path to that exact generated file. Conduit then invoked
`/changepose`; Dancy restored the prior mapping and removed its temporary output.

For this already proven topology, that structural and runtime chain is the
automation-first acceptance contract. Human visual confirmation is required for
a new topology, a new replacement model, or an unexplained structural/runtime
mismatch, not for every regression of this one.
