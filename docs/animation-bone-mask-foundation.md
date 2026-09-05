# Animation Bone Mask foundation

## Research result

The Male Miqo'te `c0701` Smoking Idle fixture is classified as:

`TRACK_EXCLUSION_FIXES - HIGH`

The validated fixture contained 147 mapped transform tracks. Excluding the eight explicitly selected names produced 139 tracks, with no unrelated mappings removed and 951 retained-track samples classified as semantically equivalent. Human validation confirmed the expected visible result. This is evidence for this fixture only; it does not establish a general retargeting rule.

The reusable research pieces are the named-track semantics, deterministic asset identity, structural gates, and retained-motion comparison. The Debug AgentBridge runner, Conduit flow, temporary Penumbra setting, and fixture-specific hashes remain research infrastructure.

## Production model

`BoneMask` persists a stable GUID, display name, enabled state, replacement association, and explicit bone names. It never persists a binding index, skeleton index, or timestamp. At generation time Dancy resolves the names again through the selected target skeleton and selected PAP/HKX binding.

The MVP resolves only the explicitly selected name. A child name is never added because its parent was selected. The stored parent relationship exists solely to support a later opt-in descendant feature.

Each selected name receives one of these states:

| State | Meaning |
| --- | --- |
| `FOUND_AND_ANIMATED` | The target skeleton contains one matching name and the selected binding has one animated transform track. It can be excluded. |
| `FOUND_NOT_ANIMATED` | The target skeleton and binding match, but the track has no animation. It is a no-op for MVP. |
| `MISSING_FROM_TARGET_SKELETON` | The selected target skeleton has no matching name. |
| `MISSING_FROM_BINDING` | The selected binding has no transform mapping for the named target bone. |
| `AMBIGUOUS` | The skeleton or selected binding has more than one matching name. |

Missing and ambiguous states block generation. No fallback by numeric index is allowed.

## Writer boundary and recommendation

`IAnimationTrackExclusionWriter` separates the Bone Mask plan from PAP/HKX reconstruction. A writer receives an immutable source path, target association, exact resolved track plan, and a Dancy-owned output path. It returns exact excluded names, source/output counts, generated asset identity, and structural validation. Dancy rejects a successful result when those facts do not match the request.

The investigated options are:

| Option | Finding |
| --- | --- |
| Dancy-owned writer | Not suitable now. Dancy can validate PAP framing but has no independently maintained Havok authoring/serialization implementation. |
| Shared maintained component | Worth revisiting only if the VFXEditor maintainers extract a versioned library with documented initialization and ownership rules. Copying its internal writer would duplicate game-version-sensitive native contracts. |
| VFXEditor IPC | Recommended. VFXEditor already owns the live Havok initialization, serializer, and GLTF reconstruction path. A small upstream public IPC can keep that logic with its maintainer and make the result contract stable for consumers. |
| Conduit reflection | Development and regression research only. It is not a production dependency. |

The chosen architecture is `VFXEDITOR_IPC`. Dancy does not require Conduit. Dancy's model, resolver, preview, and cache identity do not require VFXEditor; derivative generation will require a supported writer backend. The next writer backend should be an upstream VFXEditor IPC that accepts source/output paths, motion identity, target skeleton context, and explicit bone names, then returns the exact `IAnimationTrackExclusionWriter` result facts.

## Generated asset ownership and validation

Every generated output belongs below Dancy's configured generated-asset root. Its SHA-256 cache key covers the source PAP SHA-256, normalized selected names, replacement game path, motion identity, target skeleton identity, and writer schema version. A changed source, skeleton, selection, motion, or writer invalidates the entry.

Generation must keep the original PAP immutable, report the generated PAP SHA-256, reparse the generated PAP and HKX, validate bindings and expected removals, reject unexpected mappings, and, for reconstructed writers, run retained-track semantic comparison. A passing serializer call alone is never sufficient to apply an output through Penumbra.

## Future UI contract

`Animation -> Bone Mask -> searchable bones -> explicit selected exclusions -> compatibility preview -> generate/apply`

The production core deliberately leaves hierarchy browsing, presets, descendant selection, and final generation UI for later work.
