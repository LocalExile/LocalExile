# Owned combat summons

The first combat summon is Wildspeaker's Pack Mentality, node **26963** in the pinned 3.29.3b Phrecia graph.
It grants level 20 `SummonSpectralWolfTriggered` through GEPL 44179.
An actual player critical hit reserves the trigger at hit commitment, with a shared 500 ms cooldown.
Multiple critical targets from one area hit share the same impact timestamp.
Manual use of this trigger-only skill is refused before cost payment.

## Data and independent calculation

The skill's stat set supplies level **70**, duration **30,000 ms**, limit **10** and **6–12** added attack physical
damage for the owner per living wolf.
At the limit, the skill description says to refresh an existing wolf's duration and life.
The server refreshes the wolf whose deadline is earliest; it does not create an eleventh object.

`DisplayMinionMonsterTypes[8]` points to obsolete variety 331, the old Lion-based Spectral Wolf.
The current combat variant is variety **332**, `Metadata/Monsters/SummonedWolf/SummonedWolf`.
Its multipliers match the independently maintained
[pinned Path of Building minion entry](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b82d57f1c0de6eb40f37143c32d4da36a02/src/Data/Minions.lua).
Its data is 1,100% life, 620% damage, 1.5 seconds per attack, 11-unit attack reach and 20% damage spread.
At level 70, `DefaultMonsterStats.MinionLife=1478` gives **16,258 life**.
`MinionDamage=332.04998779296875 × 6.2 × 1.5`, with the spread, gives **2,470–3,706 physical damage**.
Player minions use `MinionDamage`, rather than the hostile monster `Damage` column.

Owner minion life/damage/speed modifiers are applied explicitly.
Minion damage per five Dexterity uses the owner's final Dexterity.
Generic owner damage, Rage, Gambler, penetration and critical increases do not transfer to a wolf.
The skill's explicit claw-critical transfer flags transfer claw-specific increases and multiplier only.
The baseline critical chance is 5%, as in pinned PoB `CalcActiveSkill.lua`; `Monster.ot` supplies 130% multiplier.
Current `Character.ot` supplies `minion_global_always_hit=1`, consistent with PoB's minion setup.
Ordinary owner accuracy is therefore not an extra wolf hit roll.

Variety 332 carries `SummonedWolfLifeLeech`, mod 4163: 100% of physical attack damage leeched as life.
Leech uses full mitigated hit damage, including overkill, rather than the amount of enemy life removed.
The standard rate is 2% maximum life per instance per second; `Monster.ot` caps each instance at 10% of maximum life
and total recovery at 20% per second.
Fractional recovery and exact instance expiry are retained, and reaching full life removes the instances.
The base rate agrees with
[PoB's calculation constants](https://github.com/PathOfBuildingCommunity/PathOfBuilding/blob/16de4b82d57f1c0de6eb40f37143c32d4da36a02/src/Modules/Data.lua).

## Ownership, AI and wire state

`SummonRoster` stores transient per-character ownership, life, cooldown and absolute deadlines.
Area connections have leases: an old connection cannot mutate a new connection's roster or consume its refresh.
Area travel preserves remaining lifetime and damaged life; logout and owner death clear the roster.
Pause boundaries update deadlines synchronously, including a pause/resume between AI ticks.
Refunding the source removes creatures and their owner buffs.
Owned creatures are excluded from the area's ordinary `SavedMonster` snapshot.

The client receives the real monster type at level 70, a friendly team, final life/attack/movement stats and its
normal melee skill record.
Its component list adds `DiesAfterTime`, whose native deserializer reads no payload; the server owns expiration.
The owner's native `spectral_wolf_buff`, BuffDefinitions 1559, is attached once per wolf, with that wolf as source.
It is not a recovery buff and carries no uncounted value payload.
Buff instance allocation is synchronized across all concurrent aura, flask, Ward, Rage and summon producers.

Wolves follow their owner and choose nearby living hostiles.
Hostiles can select a nearer living owned creature instead of the player.
Creature contact rechecks pause, source, lease, expiry, life and reach before committing damage.
Native life packets sample the current pool inside the shared send gate, so simultaneous hits cannot publish an
older pool value after a newer one.
Owner projectiles and area hits exclude owned summons.

Summon kills grant the owner's eligible experience, quest progress, flask charges and loot.
They do not trigger effects that require the player personally to kill, such as Death Rush Adrenaline.
Wildspeaker's nearby-kills-count-as-yours/rare-mod theft effect is a separate, unfinished exception.

## Verification and limits

Gateway integration activates and refunds the actual connected Pack Mentality path.
It checks the granted level, current monster variant, independent life/damage calculation, manual-cast rejection,
owner damage, stat isolation, source removal, pause/contact gates and current-life packet ordering.
Pure checks cover cooldown, limit refresh, life/deadline preservation, stale leases, exact pause boundaries,
concurrent buff allocation and fractional/capped leech.

Native checks use one client, fresh synthetic accounts and the isolated database under `logs/phrecia-completion/`.
Ten actual critical hits create ten level-70 wolves, ten native Spectral Pack buffs and 60–120 added damage.
A further critical hit refreshes a wolf without creating an eleventh.
With owner attacks stopped, wolf attacks changed the native target life from **239,463 to 169,386**.
Another pass confirmed native movement increase **50%**, attack duration **1,000 ms** and minimum physical damage
**4,940** after changing their respective minion modifiers, followed by actual combat and area/refund checks.
Area travel kept ten owned wolves, and paid source refund reduced the native count to zero.
A direct query of the native player stat getter after refund returned **0–0** added damage.
The last observed setter event alone retained the old 60–120 value and was not a current-state measurement.
An additional native pass used the normal pause packet for a four-second pause and a controlled three-second summon
duration: the wolf remained while paused, expired after its remaining active lifetime and cleared native damage.
Logout/relog also restored zero summons and zero added damage.

This implements the wolf slice, not every minion family or every Wildspeaker node.
Spiders, Zombies, Guardians, animated weapons, Herald minions, Harbingers and Relics still need their own source,
skill, stat, targeting and death rules.
General minion equipment/charge inheritance, auras, ailments and the accuracy-equals-owner mastery are separate work.
No cosmetic `PetAi` owner field is reused as a guessed combat-minion wire owner.

Code: `SummonRoster.cs`, `SpectralWolf.cs`, `MinionLeech.cs`, `Program.Summons.cs` and the shared monster hit paths.
