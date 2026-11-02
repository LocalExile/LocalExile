# Tornado Shot terminal RNG and ordinary child geometry

Part of [RE 012](../012-projectile-execution.md).
English consolidation of the earlier static case study retained in the R81 source history.
Use [the concrete TS chapter](tornado-shot.md) for final bindings and later composed controls.

## 1. Terminal context

At `0x141a829ad`, the ordinary generator initializes `0x1419c29b0` with:

```text
[u16(sequence11a), u8(motherDerived11c), u32(owner88),
 outerRemaining2d0, u8(innerRemaining2b8), 0, 0, 0]
```

It reads the mother's derived byte, not the intermediate template's incremented byte.
The [offline reference](../../../../scripts/re/projectiles/native_tornado_rng.py) implements the noncanonical
TinyMT-like uint32 transition, eight-word initialization and eight warmups.
Native angle sampling uses 7001 discrete values, closed integer range `0..7000`:

```text
limit = floor(2^32 / 7001) * 7001
draw until raw < limit
sample = raw % 7001
phi = f32(f32(f32(sample) / f32(7000)) * f32(6.283180236816406))
```

Rejected raw draws still advance state.
For `[1,2,4096,0,0,0,0,0]`, warm state is `[3227931310,2515714579,462529914,99662]`,
first raw output is `1057121180`, sample `5185`, and angle bits `0x4094ede8`.
This checks reference arithmetic, not native trig or live child directions.

## 2. Two quantization boundaries

At mother integer point `P`, the ordinary path constructs an integer aim:

```text
A.x = P.x - trunc(f32(sinf(phi) * -120.0f))
A.y = P.y + trunc(f32(-cosf(phi) * 120.0f))
```

Signs and truncation matter; that integer ray is not exactly the continuous sampled angle.
The multi-child radial branch converts cell centers using ordered float32 operations:
`world(c)=f32(f32(f32(c)+0.5)*10.8695650100708f)`.
It normalizes by reciprocal float32 length and rotates by `i*f32(6.283180236816406/count)+callerPhase`.
The ordinary radial phase is zero; randomness already entered through the aim.
The baseline is a random whole-group rotation with equal-circle spacing, rather than independent per-child angles.
Nonzero later angular/distance variance can change that baseline.

The helper uses a 150-world-unit constructed ray when both control bytes are zero; otherwise it retains length.
That constructed ray is not proof of a final lifetime or total skill range.
Mover setup quantizes world coordinates again using `floorf(f32(world*0.092f))`.
Count one has a separate dispatch path; do not blindly apply the multi-ray formula.
Full bit-exact trig/FMA and actual-input geometry agreement are not established by the public scalar RNG tool.

## 3. Identity, history and target limits

Mother final registers an intermediate template at `u8(motherDerived+1)` without consuming the shared allocator
at that step; flying children consume it separately.
For a fresh ordinary mother byte 2, template byte 3 can coexist with child bytes 3/4/5.
Completion order across mothers changes which child identities each group receives.
Each ordinary mother creates a fresh child-group history; `can_shotgun` changes it to independent child histories.
These are local memory rules, not numerical damage confirmation.

A bounded nearby-target query is recovered, but accepted/rejected/empty query controls produced equal initial
child trajectories in the composed paths.
There is no established universal guarantee that a child homes on or aims at a nearby enemy.
Use the later TS origin/contact/continuation/retirement sections without extending this conclusion to all variants.
