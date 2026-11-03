# Harbinger private gameplay integration

## Delivery and exact inputs

This is the private combined Whisperer/Harbinger stack for #339.
The publisher owns publication and native acceptance.
The order remains #333, #339, ordinary Inquisitor, ordinary Slayer/Deadeye.
No original branch, PR, root worktree, running process or shared data was modified.

This feature-only reconstruction starts at published Whisperer
`0d398bc3dea0a19fc66df7db14e6b4c43c4ba977`, including correction `1d87a4f3`.
Main remains `ec522f1876690c0b203636bf78b508f308365163`.
It replays the Harbinger-only source from `fdfffd48`, `5318b7b9` and `f9793c84`.
The later accepted-transfer and pre-admission fixes are taken from combined reference `c2bd5dda`.
No ordinary-class implementation is imported from that reference.

The descriptor is registered for Phrecia Occultist revision `3.29.3b`.
Admission still requires `Game_VERIFIED_HARBINGER=1` and the existing read-only installed-graph check.
Ordinary Occultist and other revisions do not match it.
No archive installation, generator change or alternate production data path is introduced.

## Gameplay consumers

| Node | Production consumer |
|---|---|
| 20105 | Paid passive-granted Arcane summon, both metadata buffs, recipient stats and native count |
| 60867 | Paid Time summon; received action speed feeds raw cast/attack/movement and native projection |
| 18859 | Paid Focus summon; exact current paid channel/action/generation gates each new minion cast |
| 41042 | Category-specific elapsed rates on remaining BASE effect budgets |
| 39702 | Extra ES from final Life/Mana; typed non-chaos bypass after Ward |
| 34822 | Temporal Chains area, free Blasphemy reservation, accepted curses, aura membership and reflection |
| 62155 | Existing shared Mana/ES recovery consumers read the current mode-specific passive source |
| 3702 | Existing owner cooldown engine reads the current source; minion cooldowns remain independent |

`HarbingerGameplay` subscribes to Game's existing passive-grant, cast, stat-source, minion-tick,
pause and closing contracts.
There is no additional worker, connection writer or resource engine.
Summons pass the normal skill requirements, cooldown and payment gates before their pending cast is recorded.
At completion the action, exact skill source, requirements, source node, living owner and current rules are checked.
The original completion deadline is retained when the minion tick observes it late.

Actors spawn through the shared owned-monster acceptance gate with a one-Life maximum,
metadata skills, immortality/minion flags, no fighting AI and no corpse persistence.
Admission additionally checks the exact roster member, so replacement cannot publish an old member's actor.
Cast phases use native actor skill ids; the existing minion schedule observes completions and publishes buffs.
Following teleports a companion back beside its owner beyond twelve cells.

The character roster retains transfer leases and freezes at the exact common pause boundary.
Old leases cannot mutate a destination connection's roster.
Refunds reconcile the successfully saved current node vector before recipient stat collection.
Only a successful Auth transfer preserves a living owner's roster when the source closes before arrival.
Pending transfer intent alone cannot preserve it.
Initial zero-Life stat/pool construction before admission restores vitals does not acquire a roster lease.
Death clears summons and received effects; logout retires the session.
A new Game lifetime retires the old session on access; optional-provider retirement uses the existing area-close policy.
Retired actor/buff removals use the common minion output queue and current writer.

Buff publication revalidates epoch, lease, source, living owner, actor and rules inside the current cipher gate.
Pool/stat rebuilding precedes AddBuff's remaining BASE duration.
A final resample suppresses an add that expired during stat construction while retaining consumed zero clears.
Only a successfully constructed add enters the presented-instance ledger.
Received Focus survives ending its channel; a further cast requires a fresh valid paid receipt.

## Curses, action timing and effect clocks

Temporal Chains uses the actual supported gem's source stats and ordinary paid cast admission.
Blasphemy uses existing aura intent, support compatibility, restriction, reservation and publication ownership.
The caster receives an aura source, never an accidental received self curse.
Removing the free-reservation passive reprices the current aura and suspends it if it cannot be afforded.
A maintained aura retains identical instances and replaces changed source snapshots.
A timed curse keeps its captured source; hostile identity checks prevent object-id reuse from receiving an old removal.
Fixed variety and rolled curse-immunity, Hexproof and reflection flags are combined.
Ignore-Hexproof is read from the real caster source.
Reflection installs a timed self curse; suppression retains its running clock and exposes only the remaining budget.
Generic/curse/Temporal duration increases add before support more-duration and recipient self duration.
The curse's own elapsed clock excludes its other-effects term.

`RecipientEffectTime` is shared by production consumers and the feature clock's category calculation.
Changes retain the remaining BASE budget, settle the old slope first and never resurrect expired effects.
Charges keep BASE budgets across area carry and set their rate before Step; `Walk.Moving(now)` is retained.
Fortification keeps exact stack-seconds/recovery and its independent hit-gain throttle.
Utility effect expiry changes without rescaling resource-flask recovery or periodic trigger clocks.
Incoming ailments, monster ailments, Ignite/Poison and taunt expiry retain independent damage/recovery fractions.
Native charge/Fortification/ailment duration fields carry BASE seconds.
Monster curse changes retime effects before changing action inputs and publish recipient clock stats before curse adds.
Monster walk retiming multiplies the existing speed roll; stationary actors stay stationary.
Swing duration is resampled at accepted action start and then retained for that contact.
Player action speed composes the raw bucket, Tailwind effect, Chill, Temporal Chains and action floor once.
Projectile flight and cooldown recovery remain independent of action speed.

Focus reduces each mitigated component before typed Ward.
Non-chaos ES bypass follows Ward; Whisperer's physical-only defensive Mana follows ES.
Mana Recoup observes reduced post-Ward damage, independently of actual Life loss.
Fractional non-chaos DoT bypass and copied flask-to-Life recovery retain their separate carries/rates.
Extra ES rebuilds once from final Life/Mana without feeding a previously derived ES pool back into its base.
Synchronous Hardcore death and the corrected quest, collision and current-writer retirement paths are preserved.

## Validation and remaining native acceptance

A clean full solution build passes with zero warnings/errors.
Core: 434 passing; Data: 45 passing; Gateway: 96 passing.
The two Harbinger Facts are extended rather than adding more Facts; all Whisperer tests remain.
The grouped production tests cover paid summons, native ownership/counts, Time pacing, following,
real gem curse casts/reflection/suppression, free Blasphemy, a saved companion refund,
a real paid channel producing Focus, post-channel retention, provider retirement and BASE carry/recovery.
The same grouped regression performs an accepted Auth transfer, closes the source before arrival,
and builds destination stats/pools at initial zero Life before restoring vitals and checking the roster.
The existing combined typed Ward/ES/Mana/Recoup/extra-ES tests and component pause/lease/publication races remain.
The Whisperer cross-assembly spawn call includes the two optional native-profile parameters.

Tests use private copies of known non-secret manifest/catalog fixtures from read-only input `t3-f62fbadf`,
including vendor stock and the existing read-only graph archive.
The shared TestEnvironment module initializer is unchanged.
No generated data, keys, accounts, saves, client files or third-party tools enter the commit.
No runtime/client window was used.

The unavailable official AIS is represented by the documented metadata order, initial cooldown,
700 ms phase, per-source cooldown suspension and twelve-cell follow policy.
Companion and curse observations use the existing 100 ms minion schedule.
Party propagation and strongest-of-multiple-casters curse arbitration remain outside this executor.
These boundaries do not establish native acceptance for this new combined head.

The publisher's native replay is:

1. Allocate and summon all three actors; inspect native skill lists, count stats and both Arcane buffs.
2. Compare Time's raw/native cast, attack and movement timing with ordinary and chilled controls.
3. Pay a real channel, observe Focus, stop/interrupt it and confirm the received buff's remaining duration.
4. Cast/reflect Temporal Chains; toggle supported Blasphemy; suppress and restore the self curse.
5. Change recipient rates with active charges, Fortification, flask effects, ailments, Ignite and taunt.
6. Refund/reallocate, pause/resume, travel with a failed-transfer control, die and reload Game/provider.
7. Replay typed Ward/ES/physical-Mana/Recoup, copied flask recovery, lockstep movement, monster spells and quests.

The publisher retains bounded native evidence on combined heads through `c2bd5dda`.
Those observations do not certify this feature-only reconstruction; its exact-head native replay remains pending.
No full class parity or reflected/protocol completeness is claimed.
