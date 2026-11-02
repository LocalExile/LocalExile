# Survivors: reference alignment (Azeroth Survivors)

Date: 2026-11-03
Status: implementing

## Why

The mode already borrowed Azeroth Survivors' wave clock, but several of its curves had drifted: the rank-up damage
growth was the gem-level curve instead of +30% a rank, the spawner refilled four bodies a second instead of the
reference's fast refill, the horde capped at 120 instead of 220, the boss ladder had no rising health ratio, and the
level-up window ignored the reference's weighted offer. This change puts the numbers back on the reference and makes
them testable.

Reference: `OutThisLife/azeroth-survivors-re` (`crates/benilla-app/src/survivors/{data,enemies,hud}.rs`),
read 2026-11-03. PoE formulas still come from the pinned Path of Building snapshot (issue #503's port) and the
reference library; nothing is copied from either project.

## Reference numbers (source)

| Mechanic | Reference | Where |
|---|---|---|
| Run length | 900 s | `data.rs` `VICTORY_SECS` |
| Boss cadence | 135 s, 6 bosses | `data.rs` `BOSS_EVERY_SECS`, `enemies.rs` |
| Horde target | 14 → 220 alive | `data.rs` `target_alive` |
| Spawner | every 0.12 s, `ceil(gap/6)` capped at 4 | `enemies.rs` |
| Enemy life scale | 1 + 0.75·progress | `data.rs` `hp_scale` |
| Enemy damage scale | 1 + 0.9·progress | `data.rs` `damage_scale` |
| Tier ladder | 30 steps, pool of 3 consecutive tiers | `data.rs` `wave_pool` |
| Ring wave | every 67.5 s, 28 + 60·progress bodies | `enemies.rs` |
| XP curve | 5, 15, … 185; 195 + 13/lvl; 455 + 16/lvl | `data.rs` `xp_to_next` |
| Ability rank damage | 1 + 0.30·(rank−1), max rank 8 | `mod.rs` `Owned::damage_mult` |
| Ability rank cooldown | ×0.93 per rank | `mod.rs` `Owned::cooldown` |
| Ability count | +1 projectile at ranks 3, 5, 7 | `mod.rs` `Owned::count` |
| Ability radius / duration | +8% / +10% per rank | `mod.rs` `Owned::radius`, `duration` |
| Ability slots | 6 | `data.rs` `MAX_ABILITIES` |
| Passive slots / level | 6 / 5 | `data.rs` `MAX_PASSIVES`, `MAX_PASSIVE_LEVEL` |
| Passives | Might +10%, Fortitude +20% life, Pack +10% speed, Renew +0.5/s, Intellect +8% haste, Amplify +10% area, Detect +40% magnet, Wisdom +8% growth, Wild +1 armour, Kings +5% crit | `data.rs` `PASSIVES` |
| Offer | 3 weighted picks: rank 1.2, new ability 1.0, passive rank 0.8, new passive 0.5; full heal when nothing is left | `hud.rs` `choices_for` |
| Boss life ladder | 700, 1800, 4200, 7000, 12000, 20000: 26.92×, 25.71×, 49.41×, 31.82×, 50×, 47.62× the tier pool top at each 135 s boss | `data.rs` `BOSSES` |
| Boss XP | 60, 120, 200, 300, 400, 500 | `data.rs` `BOSSES` |
| Enemy XP rate | 2× the roster's gem value | `data.rs` `XP_RATE` |

## Mapping to LocalExile

The run fights real Path of Exile monsters with a real character, so absolute HP and damage cannot be copied. The
mode keeps the reference's **shape**:

- **Power**: each run skill is the gem's own row at gem level 1 for its base, then the reference's ×1.3 a rank (with
  the gem's own crit, effectiveness and supports layered on top). Six skill slots, eight ranks, six blessings.
- **Anchor**: one rank-1 cast of the catalogue's median spell removes ~one hit from the *starting tier pool* of the
  area, not its median over the whole roster. The starting pool is the reference's three-tier window at t = 0, so the
  opening TTK is the reference's one-to-two hits.
- **Horde**: 14 → 220 alive, 0.12 s refill of `ceil(gap/6)`, a pool of 3 consecutive tiers that slides over 30 steps,
  and the ring wave at 67.5 s in each 120 s area.
- **Bosses**: the lore gate stays (one area boss, 15 s in). Its life is the area's current tier pool top × the
  reference's ladder `[26.92, 25.71, 49.41, 31.82, 50, 47.62, 60]` × the clock's life scale, and its kill pays the
  reference's 60/120/200/300/400/500/600 XP. The seventh area extends the ladder at 60×.
- **Blessings**: Might 10%, Haste 8%, Area 10%, Critical +5 percentage points of base crit, Wisdom 8%, Leech 0.2%,
  Swiftness 10%. Fortitude is the one mapping: the reference's +20% maximum life a level is worth 16.7% less damage
  taken, i.e. `25 / 1.2^level` percent (25 → 10 at level 5) instead of the old flat −2.
- **Offer**: the reference's weighted three-pick pool (rank 1.2, new skill 1.0, support 0.9, blessing rank 0.8, new
  blessing 0.5, unique 0.4 every third level) and a full-heal fallback when every slot is mastered.

### Deliberate deviations

- No ground XP gems or magnet passive: the mode pays experience on the kill (the ground-loot sweep is part of the
  instance's rules), so "Detect" maps to nothing and Leech/Swiftness stay as the PoE-flavoured extras.
- Fortitude and Swiftness map to damage reduction and projectile speed; the client owns movement.
- The card window is the client's quest-reward window, so card rarity colours are not reproduced.
- The reference's 135 s boss cadence becomes one lore boss per 120 s area; the ladder, not the wall clock, carries the
  pressure curve.

## Work

- [x] Core: reference constants and curves (`TargetAlive` 220, `SpawnRefill`, `WavePool`, `SkillDamageMultiplier`,
      `BossLifeRatio`, `BossExperience`, blessing values, weighted `DealOffer`, `Heal`).
- [x] Gateway: spawn cadence and refill, wave pool, anchor at the starting tier pool, rank damage in `ComputeCast`,
      boss life ladder/XP, horde mods merge (magic/rare scale), heal fallback.
- [x] Tests: pin every reference number; offer legality, caps and heal fallback; rank damage.
- [x] Docs: `survivors-mode.md`, checkpoint, PR body.
- [x] Live: `/survivors curve`, a boss kill and a level-up window.

