# Findings

## Background

The investigation began while testing Smoking Idle custom male genital skeleton deformation, potential female-authored-to-male-target animation repathing, and whether selective animation-track removal could avoid Blender for limited incompatibilities.

## Demonstrated Results

The Dancy/VFXEditor experiment reconstructed PAP/HKX animation data while removing selected named transform tracks. Structural validation reported the requested track changes, and derived PAPs were usable through Penumbra without modifying the original Smoking Idle source mod.

The initial Smoking variant removed `iv_ochinko_a` through `iv_ochinko_f`. A controlled follow-up retained `iv_ochinko_a` while removing `iv_ochinko_b` through `iv_ochinko_f`. `iv_kougan_l` and `iv_kougan_r` were intentionally retained in both variants.

The successful live read-only follow-up sampled all eight supported male rigs against their own skeleton rest poses. For `iv_ochinko_a`, translation and scale deltas were zero while rotation had a stable approximately 49.33 degree offset. This supports a component-level hypothesis, but it does not prove a visually correct component-level repair.

## Visual Findings

With A through F removed, the original downward penis behavior improved, but the scrotum and genital base appeared to pull inward or backward.

With A retained and B through F removed, genital-base and scrotum behavior changed, while the penis again exhibited a smooth downward curve beginning around the base.

Whole-bone masking was therefore too coarse for this case. A reasonable hypothesis is that `iv_ochinko_a` contains both useful positional or base information and unwanted animation influence. The hypothesis was not promoted to a Dancy feature and requires a future tool with dedicated visual validation.

## Companion Asset Export Research

The first standalone Bone Mask PMP contained only PAP assets. That exposed a general packaging requirement: a standalone animation export must preserve companion TMB, AVFX/VFX, and ATEX assets where the source option needs them.

The later exporter preserved the Smoking Idle source's VFX and TMB Multi group: one TMB, four AVFX files, and four ATEX files, copied unchanged. This is a useful general lesson for any future normal Dancy standalone repath export, even though the experimental exporter itself is archived with this research.

## Male-Only Export Research

The Smoking experiment included c0101 Midlander Male, c0301 Highlander Male, c0501 Elezen Male, c0701 Miqo'te Male, c0901 Roegadyn Male, c1301 Au Ra Male, c1501 Hrothgar Male, and c1701 Viera Male. It explicitly excluded c1101 Lalafell Male because that source animation was not intended for Lalafell.

The general lesson is that "male only" must not mean every male playable race. The supported export set should be:

`source-supported paths INTERSECT requested sex/race filter MINUS explicit exclusions`.

That selection rule remains relevant to normal, path-level Dancy repathing.
