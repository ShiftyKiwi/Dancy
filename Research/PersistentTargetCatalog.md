# Persistent Target Catalog Research

Date: 2026-09-26

This note records the current client-data evidence used by Dancy's target
catalog. It describes target selection only. It does not add animation
editing, skeleton work, or retargeting.

## Evidence

- Current game data was read from the installed client with Lumina.
- `ActionTimeline` provides the timeline key, resident state, priority, slot,
  motion-cancel behavior, and phase-associated key names.
- Dancy's existing PAP inspector read current target PAP headers, Havok
  indices, TMB section counts, and animation event identifiers.
- The read-only DAB target-catalog inspection passed 7/7 against the running
  client.

## Supported Persistent Targets

| Target | Timeline | Context | Trigger/state | Current coverage | Representative PAP structure |
| --- | --- | --- | --- | ---: | --- |
| Sit | `emote/sit` | Chair Sit | `/lounge`, seated on furniture | 18 | 1 animation; Havok `0`; 1 TMB; `cbem_sit` |
| Sit on Ground | `emote/jmn` | Ground Sit | `/groundsit` | 18 | 1 animation; Havok `0`; 1 TMB; `cbem_jmn` |
| Sleep | `emote/bed_liedown_loop` | Sleeping / Lying | Bed or inn sleep state | 18 | 1 animation; Havok `0`; 1 TMB; `cbem_liedown_2lp` |
| Standing Pose 1-4 | `emote/poseNN_loop` | Standing Idle | Change Pose while standing | 18 for Pose 1 | 1 animation; Havok `0`; 1 TMB; Pose 1 `cbem_pose01_2lp` |
| Chair Pose 1-4 | `emote/s_poseNN_loop` | Chair Sit | Change Pose while seated on furniture | 18 for Pose 1 | 1 animation; Havok `0`; 1 TMB; Pose 1 `cbem_s_pose01_2lp` |
| Ground-sit Pose 1-3 | `emote/j_poseNN_loop` | Ground Sit | Change Pose while sitting on the ground | 6 for Pose 1 | 1 animation; Havok `0`; 1 TMB; Pose 1 `cbem_j_pose01_2lp` |
| Lying Pose 1-2 | `emote/l_poseNN_loop` | Sleeping / Lying | Change Pose while lying down | 18 for Pose 1 | 1 animation; Havok `0`; 1 TMB; Pose 1 `cbem_l_pose01_2lp` |

The state-specific Change Pose contexts are not inferred from their prefixes.
Their current `ActionTimeline` priority/slot signatures match the named state
targets: standing `7/0`, chair sit `9/2`, ground sit `8/2`, and sleep `0/3`.

`emote/pose05_loop` and `emote/pose06_loop` are also current resident looped
Change Pose timelines, but do not match those named state signatures. Dancy
keeps them visible as `Other Persistent Pose` with an explicitly unknown
context rather than guessing that they are standing, seated, or lying states.

## Bounded Discoveries

- `normal/idle` resolves to 16 current player paths under
  `bt_common/resident/idle.pap`. It is a standing state but its PAP has two
  animation headers, Havok indices `0,1`, two TMB sections, and events
  `cbna_add_dmg_f,cbnm_id0`; Dancy shows it as unsupported rather than
  creating a partial rewrite.
- `/doze` remains an advanced target. Its current EmoteMode permits movement,
  unlike the named sit and sleep state modes, so no persistence claim is made
  without a separate duration observation. Its representative PAP is one
  animation, Havok `0`, one TMB section, event `cbem_doze`, across 18 variants.
- One-shot command emotes, including `/wave`, remain in One-shot / Advanced.
  Current `/wave` resolves `emote/goodbye_st` across 18 variants with event
  `cbem_goodbye_st`.
- Movement, mounts, swimming, combat locomotion, instruments, ornaments, and
  other contextual state machines were observed in ActionTimeline data but are
  intentionally outside Dancy's normal target catalog.
