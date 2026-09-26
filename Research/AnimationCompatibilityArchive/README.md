# Animation Compatibility Research Archive

This archive preserves experimental animation-transformation research removed from Dancy's active product surface in September 2026.

## Scope Boundary

Dancy decides where an animation plays: discovery, path inspection, race-aware mapping, source and target selection, preview, and safe Penumbra overrides.

This research investigated what an animation does internally. HKX track removal, transform-component replacement, skeleton rest-pose sampling, and compatibility repair belong in a future dedicated Animation Transformation / Compatibility tool, not in Dancy Core.

## Recovery

The complete Dancy experiment is in `patches/dancy-bone-mask-experiment.patch`. It applies to pre-experiment core baseline `268667d` and contains the committed foundation plus uncommitted and formerly untracked source files.

The VFXEditor-side IPC extension is separately preserved in `patches/vfxeditor-pap-rebuild-extension.patch`, against VFXEditor baseline `b4a9590`. Recovering the experiment requires both patches, builds of both projects, and fresh runtime validation. The patches deliberately contain source only: no binaries, caches, user configuration, generated PMPs, or machine-specific paths.

The local-only Git reference `archive/animation-bone-mask-research` points to the last committed Dancy experimental foundation. The Dancy patch remains the authoritative complete recovery artifact because it also preserves the later uncommitted work.

See [FINDINGS.md](FINDINGS.md), [ARCHITECTURE_NOTES.md](ARCHITECTURE_NOTES.md), [EXPERIMENT_LOG.md](EXPERIMENT_LOG.md), and [FUTURE_PLUGIN_SCOPE.md](FUTURE_PLUGIN_SCOPE.md) for evidence and next steps.
