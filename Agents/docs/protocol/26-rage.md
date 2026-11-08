# Generic Rage (3.29.3b, issue #281)

`RageState`, `RageStats` and `Program.Rage` implement the shared resource independently of class selection.
The server consumes its real passive, active equipment, development and supported-skill inputs.
Native verification is pending; this change has automated model, parsed-dispatch and packet coverage only.

## Evidence and arithmetic

[GGG's 3.25 Rage rework](https://www.pathofexile.com/forum/view-thread/3531661#rage) establishes the current
30 base maximum, 1% more attack damage per Rage, shared half-second hit-gain interval and two-second loss delay.
It also removes inherent attack/movement speed and distinguishes loss speed from the delay.
Extracted `BuffDefinitions` row 1060 (`rage`) describes individual losses every 0.1 seconds.
The supplied 3.29.3c client analysis identifies `0x14287e680` as dividing the loss delay by its recovery rate;
it does not multiply the loss speed.
`0x14287ee10` checks cannot-gain key 14363 with soul-gain-prevention key 8855.
Those native findings were provided with this task; this child did not run a client or reverse-engineering tools.

- Maximum is `max(0, 30 + maximum_rage)`, then integer-halved by `maximum_rage_is_halved`.
  A lower maximum clamps the held balance when inputs change.
- Delay is `max(0, 2000 + rage_loss_delay_ms) / (1 + rage_loss_delay_recovery_rate_+% / 100)` milliseconds.
  Nonpositive delay recovery stalls the timer as a bounded server policy, rather than dividing by zero.
- After that delay, loss is `10 * max(0, 1 + rage_decay_speed_+% / 100)` Rage per second.
  Fractional progress survives observations, rate changes and area carry; each complete unit removes one Rage.
  `no_inherent_rage_loss` and `should_not_lose_rage` suppress this inherent loss.
- Effective Rage is the held count times `max(0, 1 + rage_effect_+% / 100)`, truncated to an integer native stat.
  The attack packet receives the single final multiplier `1 + effective Rage / 100`.
  It scales attack damage types and converted/added damage once, without changing generic damage or spell damage.
  Existing impale bonuses are added after this multiplier.
  `RageStats.ApplyAttack` multiplies each unrounded bound by `100.0 + effective percent`, then divides by 100.
  Forming `1 + percent / 100.0` first can put fixed 100 damage at 15 Rage below 115 and make the panel floor to 114.
  Default attacks retain their existing charge multiplier before this operation; added and mana-derived damage
  remain inside the full packet, with unchanged attack/melee roles and support ordering.
  `AttackHitConverted` places conversion before the final Rage operation in combat and native hand packets.
  Attack-gem, ordinary bow and both native hand paths use the same Rage operation without global panel rounding.

The merged attack sources retain Whisperer's node 50932 transfer at 150% and Steel Willed's node 2103 contribution
from complete groups of final armour, in the existing additive bucket before Rage.
With zero Rage, the merged main's full base/added/mana, charge and conversion packet keeps its exact range bits.
At an accepted default melee contact, the direct packet rolls once and supplies Fortification's raw input.
Stored Impale remains separate; Rage generation happens after that hit's roll and life mutation.
The composed source regression uses 40 held Rage and fixed raw 100 against 400 Life:
the actual hit is 140, generating 11 Fortification under model v1, then gaining five Rage for later hits.
Supported area callbacks retain independent one-positive-grant latches for both mechanics.
Death, connection close and area travel retain both mechanics' lifecycle hooks.
Fortification's exact recovery baseline, immutable pools revision and shared snapshot factory remain unchanged.

## Sources and accepted contacts

Sources add through `gain_x_rage_on_hit`, `gain_x_rage_on_attack_hit`, `gain_x_rage_on_melee_hit`,
`gain_x_rage_on_attack_crit`, `gain_x_rage_on_melee_crit` and `gain_x_rage_when_hit`.
A linked Rage Support contributes its own `gain_x_rage_on_attack_hit` only to that supported skill.
Ordinary attacks classify melee using the active weapon set; gems use their actual skill types.
Projectile contacts exclude melee sources, and spell contacts exclude all attack/attack-critical sources.
The existing supported-hit router still decides whether a skill can produce a hit.
This change does not add a second generic hit, bypass payment or create a new execution path for unsupported skills.

Positive hit gains share one 0.5-second interval across outgoing/incoming hits and targets.
One resolved area sweep latches its first positive grant, so delayed per-target notifications cannot grant twice.
Rejected contacts and nonpositive gains do not consume that sweep's positive-grant latch.
Only a real positive source can roll `gain_max_rage_on_rage_gain_from_hit_%_chance`.
Thus Sheer Fury's +15 maximum and 15% maximum-gain chance never introduce a gain source themselves.
`cannot_gain_rage` blocks gains, as does `cannot_gain_rage_during_soul_gain_prevention` with
`has_soul_gain_prevention` (keys 14362, 14363 and 8855).
An enemy hit refreshes activity even without a grant or during gain prevention; evasion does not.
Blocked incoming contacts count as being hit under this server policy.

Each grant commits under `conn.Monsters` with the accepted life mutation, before any notification awaits.
Ordinary melee, supported area hits, ordinary arrows, skill projectiles and incoming monster melee use that rule.
An invalidated action, dead target, pause, leaving area, retired owner or closed connection cannot grant late Rage.
Supported area-hit and projectile behavior otherwise retains the existing combat approximations.

## Ownership and versioned policies

`RageState.PolicyVersion = 1` explicitly defines capped-attempt behavior:
a zero, blocked or capped gain does not refresh activity and does not consume the shared hit interval.
Official capped-attempt refresh behavior is undocumented in the evidence above and still needs measurement.
Change this version and the boundary regressions if native evidence requires a different rule.

The connection owns its transient state; there is no saved Rage or implicit login grant.
The stopped game clock freezes decay during pause.
Death clears Rage before death handling awaits, and presentation clears the count and buff even while paused.
On a successful Auth transfer, the old owner atomically settles and moves its count, activity age, hit-interval age,
fractional loss and previous inputs to the destination ticket under `Monsters`, then becomes permanently retired.
The same-character destination consumes the ticket once in `SendPlayer`, immediately before its initial stats build.
The ticket remains unconsumed during the running destination clock before `0x7d`; that loading time spends no Rage.
Another character cannot consume it, and duplicate restoration or the retired connection cannot copy the balance.
Loading time is frozen by server policy; official transfer timing is not established here.
Unclaimed carry expires after five minutes; a failed Auth transfer retains the original owner.

## Native presentation

`RunRage` observes at 100 ms intervals, and combat settles at contact independently of that ticker.
Refreshes are assembled inside the real connection send gate under `Monsters`.
They replace an old buff with `0x1c2`, then add `0x1c1` row 1060 with stacks equal to held Rage and no duration.
At zero only the removal remains.
`0x1b3` and `0x2bc` refresh player/skill damage stats, including `current_rage` (7594),
`virtual_maximum_rage` (12139), `effective_rage_for_per_rage_effects` (20860),
`attack_damage_+%_final_from_rage` (20817), `is_at_maximum_rage` and the final delay.
The count never grants inherent attack speed.
Logs emit `Rage <held>/<maximum>, effective <value>, policy v1` on presentation changes.
Queued old-area refreshes are discarded when they reach the send gate.
The queued-refresh regression also covers expiry and changes to another stat source before that gate opens.
Shared character, skill, command, equipment, charge and player-attribute refreshes use `SendPlayerStatSnapshot`.
This engine-neutral factory constructs their full stats/Actor packets under the send gate and `Monsters` lock.
Queued charge refreshes resample expired Rage and attack damage in both packets, rather than replaying old values.
The helper and its call-site hunks match the supplied shared patch, without importing any other mechanic's engine.
Rage's own presenter retains its guarded, fresh-at-gate buff/count capture.

## Native verification and remaining controls

Seventeen checks used a new unascended Witch, raw stat inputs and the single 3.29.3b client.
They verified source-qualified generation, forged-count rejection, limits of 30/45 and the installed `rage` buff.
Fixed-100 attacks dealt and displayed 120 at 20 Rage, 130 with 50% effect, 167 at 45 Rage with that effect,
and exactly 115 at 15 Rage with no effect increase.
Inherent attack speed stayed unchanged.
Cannot-gain and Soul Gain Prevention conditions blocked generation.
The ordinary delay and +100% delay recovery gave approximately two and one seconds before decay,
with the same subsequent loss rate; input, contact and ticker latency are included in these elapsed checks.
Pause held the balance across 2.2 wall-clock seconds.
An actual hostile ZombieBite hit granted Rage; travel carried the balance, and death/relog cleared it.
The build's Git version, DLL hash and process working directory were checked before attaching the probe.
These controls do not allocate a Phrecia node or establish pixel-level buff artwork parity.

Automated production tests also cover supported attacks, spell exclusion, raw source removal, active gear,
charged packets, all 0?45 integer damage boundaries and fractions, and queued send-gate refreshes.
The parsed 40-Rage/raw-100/400-Life case deals 140 and grants 11 Fortification before generating new Rage.
That combined Fortification oracle is automated; it is not a new native composition assertion.

Further native composition checks remain:

- Supported attack projectiles, area sweeps, linked Rage Support, active/inactive equipment and converted damage.
- Notification delays beyond the half-second interval and slow destination loading before player construction.
- Charge, equipment and command refreshes queued across expiry, plus separate decay-speed modifiers.
- Official capped-attempt refresh behavior and area-loading timer parity before replacing policy v1.

## Limits outside this mechanic

Vaal costs/souls, class selection and the rest of Behemoth remain separate mechanics.
This does not implement every item-specific per-Rage effect, regeneration, alternate resource spending,
weapon/warcry/stun/kill-specific conditional source or independent additional-loss mechanic.
The model retains a generic atomic spending primitive for future consumers; no Vaal consumer is introduced.
Existing attack-gem accuracy, skill shapes, projectile snapshots and target/block approximations remain as before.
Additional native composition checks are listed above; whole-class acceptance remains separate.
