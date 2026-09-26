# Architecture Notes

## Classification

| Finding or implementation | Classification | Reason |
| --- | --- | --- |
| Correct character-code race labels | Keep in Dancy Core | Race labels are needed for ordinary source inspection and race-aware path matching. |
| PAP Havok-index inspection | Keep in Dancy Core | It identifies a PAP motion origin without changing animation contents. |
| Source SHA-256 provenance and integrity checks | Already core / retain where present | Safe override operations need to detect source drift. |
| Logical game-path versus physical PAP distinction | Already core concept; experimental resolver archived | Dancy needs logical mapping inspection, but the experimental resolver also planned transformed physical outputs. |
| Companion TMB/VFX/ATEX preservation | Archive implementation; retain as a future core export requirement | The existing implementation is bound to Bone Mask patch generation, but the requirement benefits ordinary standalone repath PMP export. |
| Penumbra Multi option grouping and DefaultSettings recalculation | Archive implementation; retain as a future core export requirement | The exporter was transformation-specific, while preserving source UI structure is generically valuable. |
| Supported-race filtering and explicit exclusions | Retain as a core selection rule | It is path selection, not skeleton transformation. |
| Physical-output deduplication | Archive implementation | The present code only deduplicates transformed PAP outputs. A future core export can introduce a narrow repath-specific form. |
| HKX track deletion and component masks | Archive | These directly change animation contents. |
| Skeleton inspection, hierarchy expansion, semantic comparison, and rest-pose compensation | Archive | They exist to support animation compatibility transformation. |
| VFXEditor rebuild IPC extension | Archive companion patch | It is required to reproduce this experiment, but Dancy Core must not invoke it. |

## Restored Boundary

The retained production plugin must have no Bone Mask patch records, generated animation derivative cache, VFXEditor rebuild gateway, component-mask protocol, bone-selection window, smoking-specific diagnostic, or live transformation self-test. Dancy may still identify race, logical PAP paths, and source motion origin as part of ordinary override planning.

## Packaging Boundary

This folder contains Markdown and patches only. It is not compiled by the Dancy project and must not be included in a normal plugin package. It intentionally contains no binaries, generated PMP archives, self-test caches, or user paths.
