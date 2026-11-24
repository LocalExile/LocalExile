using System.Diagnostics;
using GameServer.Core.Protocol.Instance;
using WalkEstimator = GameServer.Core.World.WalkEstimator;
using PlayerWalk = GameServer.Core.World.PlayerWalk;
using ClientPathfinder = GameServer.Core.World.ClientPathfinder;
using DamageType = GameServer.Core.World.DamageType;
using DamagePacket = GameServer.Core.World.DamagePacket;

/// <param name="ArriveAt">Arrive at this cell (back through a town portal).</param>
public sealed record PendingTransfer(ushort Area, ulong Instance, bool New, bool AtSpawn = false, bool AtWaypoint = false, (int X, int Y)? ArriveAt = null)
{
    /// <summary>Called with Auth's answer to the transfer (a map portal records the map's new instance).</summary>
    public Action<ControlResponse>? Done { get; init; }

    /// <summary>With <see cref="New"/>: the new instance's seed (0 = Auth's choice; the capture tour sets it).</summary>
    public uint Seed { get; init; }

    public int? ArrivalEntrance { get; init; }

    /// <summary>Through a map portal: the map the instance is opened with, also when it was the last portal.</summary>
    public MapDevice.Run? Map { get; init; }

    /// <summary>When visiting another player's personal area (such as a hideout): the owner character name.</summary>
    public string? InstanceOwner { get; init; }
}

// A monster this connection spawned: label for /mobs, the server's estimate of its walk, and its AI state.
// A monster's attack: its skill id, attack time, reach (cells, centre to centre) and damage per hit.
/// <summary>How a monster's projectile skill releases its shot, from the rig and stat set the client itself plays
/// (Program.MonsterReleaseFor): the clip, its contact keyframe as a fraction of the attack, the keyframe's position when
/// the skill spawns the shot there (<c>projectile_uses_contact_position</c>), and whether the monster turns its back for
/// it (<c>action_should_face_away_from_target</c>: the Sand Spitter spits from its rear).</summary>
public sealed record MonsterRelease(string Clip, double Fraction, ContactPoint? Point, bool FacesAway);

/// <summary>One skill of a boss's kit (BossKits): the granted effect it is built from, its action, and how long the
/// tables make it wait between two uses (0: the plain attack every monster has).</summary>
public sealed record MonsterKitSkill(int Effect, MonsterAttack Attack, double CooldownSeconds)
{
    /// <summary>A trigger on damage taken: the damage its user takes before it is cast (0: no trigger, a skill the kit
    /// picks).</summary>
    public int TriggerThreshold { get; init; }
    /// <summary>Whether this kit skill is a boss teleport (<c>BossMoves.IsTeleport</c>).</summary>
    public bool IsTeleport { get; init; }
    /// <summary>Whether one cast of this kit skill fires a barrage of shots (<c>BossMoves.BarrageOf</c>).</summary>
    public bool IsBarrage { get; init; }
}

/// <summary>A delayed strike: how long its marker waits, the art it shows while it waits and as it strikes (null for
/// none), how long the impact art stays, and whether the strike also calls down every other marker of the same skill
/// its owner has out (Storm Call's).</summary>
public sealed record MonsterDelayedStrike(double Seconds, string? MarkerArt, string? ImpactArt, double ImpactSeconds,
    bool StrikesTogether);

/// <summary>Objects a monster's skill leaves in the area: its thrown traps (Game.MonsterTraps).</summary>
public sealed class MonsterPlacedObjects
{
    /// <summary>A monster's trap: its object, its owner, what it plays when set off, and its throw's times.</summary>
    public sealed record Trap(uint Id, uint Owner, SpawnedMonster Monster, MonsterAttack Payload, (int X, int Y) At,
        double LandAt, double Armed, double Until, double TriggerRadius)
    {
        public bool Landed;
        /// <summary>Set off, it stays until <see cref="Until"/> only because its action draws the ground it left
        /// (Fire Trap's burning ground); it is set off no more.</summary>
        public bool Spent;
    }
    public readonly Dictionary<uint, Trap> Traps = [];

    /// <summary>A delayed strike's marker: its object, its owner, the skill (Effect) and the hit it lands, where and when.</summary>
    public sealed record Marker(uint Id, uint Owner, SpawnedMonster Monster, int Effect, MonsterAttack Payload,
        MonsterDelayedStrike Strike, (int X, int Y) At, double StrikeAt);
    public readonly Dictionary<uint, Marker> Markers = [];
    /// <summary>Impact art objects of struck markers, and when each leaves.</summary>
    public readonly Dictionary<uint, double> Impacts = [];

    /// <summary>A ground a monster's skill left (Game.HostileGrounds): its object, its owner, the BuffDefinitions row it
    /// keeps on the player standing in it with that buff's stats, where it lies, how far it reaches (cells), when it ends
    /// (area time), the area it lies in, and whether it has an object of its own (not when the skill's action draws it).</summary>
    public sealed record Ground(uint Id, uint Owner, ushort BuffRow, IReadOnlyDictionary<uint, int> Stats, (int X, int Y) At,
        double Radius, double Until, object Area, bool Drawn);
    public readonly Dictionary<uint, Ground> Grounds = [];
}

/// <summary>What a kit skill does beyond a hit (BossKits, MonsterSkills.cs).</summary>
public enum MonsterSkillShape
{
    /// <summary>A movement skill (Leap Slam, Shield Charge, Flame Dash, Lightning Warp): it carries its user to a cell
    /// by the player and hits around where it lands, if it deals damage.</summary>
    Travel,
    /// <summary>A war cry or a buff (Enduring Cry, Blood Rage, Immortal Call): its buff on its user, for its time.</summary>
    Self,
    /// <summary>A curse: its debuff on the player, for its time.</summary>
    Curse,
    /// <summary>A spell on a corpse (Volatile Dead, Cremation, Detonate Dead): it consumes corpses and hits from them.</summary>
    Corpse,
}

/// <summary>A kit skill's use: its shape, the BuffDefinitions row that draws it (0: none), how long it lasts, the stats
/// it grants (on its user; on the player for a curse), and the nearest the player must be for a travel.
/// A skill on corpses (<paramref name="OnCorpse"/>: Bodyswap's travel, every <see cref="MonsterSkillShape.Corpse"/>) takes
/// up to <paramref name="Corpses"/> within <paramref name="Radius"/> of the first, hits within <paramref name="Reach"/> of
/// each, <paramref name="Pulses"/> times <paramref name="PulseSeconds"/> apart.</summary>
public sealed record MonsterSkillUse(MonsterSkillShape Shape, ushort BuffRow, double Seconds, IReadOnlyDictionary<uint, int> Stats,
    double MinRange = 0, bool OnCorpse = false, double Radius = 0, double Reach = 0, int Corpses = 1, int Pulses = 1,
    double PulseSeconds = 0);

/// <summary>A ground a monster skill leaves where it lands (Game.HostileGrounds): its GroundEffectTypes kind and skin
/// (<c>ground_*_art_variation</c>), the stats its buff gives whoever stands in it, how far it reaches (cells) and for how
/// long. <paramref name="ClientDrawn"/>: the skill's own action draws it (a Fire Trap's burning ground, on the trap), so the
/// server sends no object of its own and keeps the drawing object while it lasts.</summary>
public sealed record MonsterGroundSpec(string Type, int ArtVariation, IReadOnlyDictionary<uint, int> Stats, double Radius,
    double Seconds, bool ClientDrawn = false);

/// <summary>A debuff a monster skill puts on the player it hits (Game.PlayerDebuffs): a BuffDefinitions row, the stats it
/// gives the player and how long it lasts.</summary>
public sealed record MonsterDebuffSpec(ushort Row, IReadOnlyDictionary<uint, int> Stats, double Seconds);

public sealed record CorruptedBloodOnHit(int Stacks, double Seconds, int PhysicalPerMinute, int AverageHitPercentPerMinute)
{
    public static CorruptedBloodOnHit? From(Func<uint, int> stat)
    {
        int cleave = stat(Stat.CorruptedBloodCleaveStacksToAdd);
        int stacks = cleave > 0 ? cleave : stat(Stat.CorruptedBloodOnHitNumStacks);
        if (stacks <= 0) return null;
        int duration = stat(Stat.CorruptedBloodOnHitDuration);
        // shortcut: cleave rows omit duration; use the other three-stack beam's five seconds until a retail cleave trace confirms it.
        double seconds = duration > 0 ? duration / 1000d : cleave > 0 ? 5 : 0;
        return new(stacks, seconds, cleave > 0 ? stat(Stat.BasePhysicalDamageToDealPerMinute) : 0,
            stat(Stat.CorruptedBloodOnHitPctAverageDamageToDealPerMinutePerStack));
    }
}

public sealed record MonsterAttack(ushort SkillId, double Seconds, double Range, DamagePacket Damage)
{
    /// <summary>The ground the skill leaves where its area lands or its projectile ends (Game.HostileGrounds); null for
    /// none.</summary>
    public MonsterGroundSpec? Ground { get; init; }
    /// <summary>The debuffs the skill puts on the player it hits, an unblocked hit's or, for a skill that deals no damage
    /// (Wither), the player's standing in its area (Game.PlayerDebuffs).</summary>
    public IReadOnlyList<MonsterDebuffSpec> Debuffs { get; init; } = [];
    /// <summary>A body summoned for the skill uses it (a monster's totem): its action is sent without the totem flags,
    /// as the player's totem proxy is, so it plays the skill rather than another summon.</summary>
    public bool Proxy { get; init; }
    /// <summary>Native source ailment chances and modifiers, consumed once by the ordinary incoming-hit path.</summary>
    public PlayerElementalAilments.HitSettings Ailments { get; init; }
    /// <summary>The skill's own poison chance (%) and duration (seconds), applied on an unblocked hit beside the monster's
    /// mods (an essence boulder whose skill carries 100% poison). Zero: the skill adds none.</summary>
    public int PoisonChance { get; init; }
    public double PoisonSeconds { get; init; }
    public CorruptedBloodOnHit? CorruptedBlood { get; init; }
    public bool Ranged { get; init; }
    /// <summary>When and from where a projectile skill releases its shot; null: half the attack time, 1.5 cells ahead at
    /// the default height (no rig data for the variety).</summary>
    public MonsterRelease? Release { get; init; }
    /// <summary>The granted effect whose rig contact is selected for each ordered action. Null for a scripted release.</summary>
    public int? ReleaseEffect { get; init; }
    /// <summary>The kit skill's granted effect (BossKits): a weapon-stanced monster times its contact by the clip its stance
    /// plays for the effect's action (SkillAnimations, Program.StanceContact).</summary>
    public int? Effect { get; init; }
    /// <summary>An area skill without a projectile (Cold Snap, Rain of Arrows, Vortex): its hit lands at the contact within
    /// this radius (cells, its own stats) of the point its action aims at, rather than on the player wherever they went.
    /// 0: a plain hit.</summary>
    public double AreaRadius { get; init; }
    /// <summary>The area is around the user (a nova: ActiveSkillTypes Nova), not where the action aims.</summary>
    public bool AreaAtSelf { get; init; }
    /// <summary>A trap skill (ActiveSkillTypes Trapped): the action throws a trap at its aim, and the trap, set off,
    /// plays this attack from where it lies (Game.MonsterTraps). Null for any other skill.</summary>
    public MonsterAttack? Trap { get; init; }
    /// <summary>A skill whose area lands later than its cast (Storm Call): the action sets a marker at its aim, and the
    /// marker strikes with this attack's area when its time is up (Game.MonsterMarkers). Null for any other skill.</summary>
    public MonsterDelayedStrike? Strike { get; init; }
    /// <summary>How many times one use hits (Double Strike's 1 + base_skill_number_of_additional_hits, Dual Strike's two
    /// when dual wielding): each after the first lands on the clip's next contact frame, and one whose clip has no such
    /// frame does not land.</summary>
    public int Hits { get; init; } = 1;
    /// <summary>A ranged skill's projectile (SkillCatalog.MonsterProjectile): the shot flies from the monster at the
    /// attack's contact and lands its hit when it reaches the player (Program.ProjectileHostile.cs). Null keeps the
    /// instant contact at half the attack time.</summary>
    public SkillCatalog.MonsterProjectileInfo? Projectile { get; init; }
    /// <summary>A summoning skill (SkillCatalog.MonsterSummon, the Labyrinth's Immolator): its action brings monsters
    /// ahead of the summoner instead of a hit (Program.MonsterSwing). Null for an attack.</summary>
    public SkillCatalog.MonsterSummonInfo? Summon { get; init; }
    /// <summary>Whether this is a spell skill rather than a melee/ranged attack. A spell's cast duration comes from the
    /// virtual cast-time stat; an attack's from the weapon duration. Sending only attack stats for a spell left the client's
    /// cast-duration function without its value and crashed it (0xc0000005, live 2026-10-31).</summary>
    public bool Spell { get; init; }
    /// <summary>The action's clip only: it lands no contact. A scripted skill whose hit or area the controller applies on
    /// its own clock sets this, so the client does not also get a zero-damage hit at the clip's contact.</summary>
    public bool NoContact { get; init; }
    /// <summary>The action casts, but its contact is secondary damage rather than attack or spell damage.</summary>
    public bool Secondary { get; init; }
    /// <summary>The skill's native cooldown in seconds (GrantedEffectsPerLevel Cooldown) for non-kit monsters; 0 when none.</summary>
    public double CooldownSeconds { get; init; }
    /// <summary>It holds a weapon in each hand (<see cref="VisibleItem"/> lists it): the client sets the off hand's strike up
    /// from that hand's own stats (11-skills.md, "A dual-wielded attack has two hands").</summary>
    public bool DualWielding { get; init; }

    /// <summary>The attack's final stats (the client derives none, 11-skills.md §4): the swing time, reach and damage.
    /// These also go on the monster's Stats component.</summary>
    public IReadOnlyList<(uint, int)> Stats()
    {
        int ms = (int)Math.Round(Seconds * 1000);
        var physical = Damage[DamageType.Physical];
        var chaos = Damage[DamageType.Chaos];
        int min = (int)Math.Floor(physical.Min), max = (int)Math.Floor(physical.Max);
        int chaosMin = (int)Math.Floor(chaos.Min), chaosMax = (int)Math.Floor(chaos.Max);
        List<(uint, int)> stats =
        [
            (Stat.MainHandMinimumPhysicalDamage, min), (Stat.MainHandMaximumPhysicalDamage, max),
            (Stat.MainHandMinimumChaosDamage, chaosMin), (Stat.MainHandMaximumChaosDamage, chaosMax),
            (Stat.MainHandMinimumTotalDamage, (int)Math.Floor(Damage.MinTotal)),
            (Stat.MainHandMaximumTotalDamage, (int)Math.Floor(Damage.MaxTotal)),
            (Stat.MainHandBaseWeaponAttackDurationMs, ms), (Stat.MainHandTotalBaseWeaponAttackDurationMs, ms),
            (Stat.HundredTimesAttacksPerSecond, (int)Math.Round(100 / Seconds)),
            (Stat.MainHandBaseMaximumAttackDistance, (int)Range), (Stat.MainHandMaximumAttackDistance, (int)Range),
        ];
        if (DualWielding)
            stats.AddRange(
            [
                (Stat.OffHandMinimumPhysicalDamage, min), (Stat.OffHandMaximumPhysicalDamage, max),
                (Stat.OffHandMinimumTotalDamage, min), (Stat.OffHandMaximumTotalDamage, max),
                (Stat.OffHandBaseWeaponAttackDurationMs, ms), (Stat.OffHandTotalBaseWeaponAttackDurationMs, ms),
                (Stat.OffHandBaseMaximumAttackDistance, (int)Range), (Stat.OffHandMaximumAttackDistance, (int)Range),
            ]);
        return stats;
    }

    /// <summary>An actor whose actions a controller plays (KingInMists.cs): every skill starts at once, none walks it up.</summary>
    public bool Scripted { get; init; }

    /// <summary>A kit skill that is no plain hit (<see cref="MonsterSkillUse"/>): a travel, a buff on its user, a curse.</summary>
    public MonsterSkillUse? Use { get; init; }

    /// <summary>Plays where the actor stands. A plain melee attack instead walks the monster up (07 §5).</summary>
    public bool InPlace => Ranged || Spell || Scripted || Use is not null;

    /// <summary>The skill's stats. A melee attack keeps the client's pathing step: the action walks the monster up to its
    /// target and waits there until S→C 0x1ac starts the swing (07 §5). Anything else plays where it stands.</summary>
    public IReadOnlyList<(uint, int)> SkillStats() => InPlace ? InPlaceSkillStats() : Stats();

    /// <summary>An attack's <see cref="Stats"/> or a spell's cast time, plus skill_is_non_pathing: the action starts at once
    /// where the actor stands (the Melee action's begin, 0x14193df80). Only on the skill: on the monster's Stats component
    /// it also stops its Move action from pathing (live 2026-10-17: the monsters stood still).</summary>
    public IReadOnlyList<(uint, int)> InPlaceSkillStats() => [.. Spell ? SpellStats() : Stats(), (Stat.SkillIsNonPathing, 1)];

    IReadOnlyList<(uint, int)> SpellStats()
    {
        int ms = Math.Max(1, (int)Math.Round(Seconds * 1000));
        return
        [
            (Stat.BaseSpellCastTimeMs, ms), (Stat.VirtualBaseSpellCastTimeMs, ms),
            (Stat.HundredTimesCastsPerSecond, (int)Math.Round(100_000.0 / ms)),
            (Stat.CastingSpell, 1),
        ];
    }
}

public sealed record SpawnedNpc(NpcCatalog.Npc Npc, int X, int Y)
{
    /// <summary>Its Transitionable state as the client has it (NPCTalk scripts step it: Game.StepNpcTransition).</summary>
    public byte State { get; set; } = Npc.TransitionState;
}

// An open sell window: the NPC, the two offer inventories (manager 1), the offered items and the NPC's offer.
public sealed class NpcSale(SpawnedNpc npc, uint mine, uint theirs)
{
    public SpawnedNpc Npc { get; } = npc;
    public uint Mine { get; } = mine;
    public uint Theirs { get; } = theirs;
    // The offered items stay in the character's inventory cells until the sale ends (PlayerInventories.Offer).
    public List<InventoryItem> Reward = [];
    public IReadOnlyList<InventoryItem> QuotedItems = [];
    public IReadOnlyList<Item> QuotedRewards = [];
    public string? QuoteError;
    public uint MineRevision, TheirsRevision;
}

// An open shop tab: the NPC, its inventory id (manager 1), the items left by id and the tab's 0xbf revision.
public sealed class OpenShop(SpawnedNpc npc, Dictionary<uint, (uint Tab, InventoryItem Item)> items)
{
    public string? StockKey { get; init; }
    public Dictionary<uint, Guid> Finite { get; init; } = [];
    public SpawnedNpc Npc { get; } = npc;

    /// <summary>The items for sale by id, each with its tab's inventory id.</summary>
    public Dictionary<uint, (uint Tab, InventoryItem Item)> Items { get; } = items;

    /// <summary>The next revision of a tab's inventory (S→C 0xbf).</summary>
    public uint NextRevision(uint tab) => _revisions[tab] = _revisions.GetValueOrDefault(tab) + 1;
    readonly Dictionary<uint, uint> _revisions = [];
}

/// <summary>How a monster moves and closes in where its variety's row does not say: what an encounter that spawns it knows of its
/// object type and skills (SpawnMonster's <c>movement</c>). The default is the row's.</summary>
public sealed record MonsterMovement
{
    public static readonly MonsterMovement Default = new();

    /// <summary>Its reach as a share of the variety's attack distance, in place of the aggro range a ranged spell keeps (a
    /// skill's <c>spell_maximum_action_distance_+%</c>: -60 is 0.4), so it walks up before it casts; null keeps the row's.</summary>
    public double? Reach { get; init; }

    /// <summary>What its object type adds to its walk speed by itself, which the client applies on its own (an .ot's
    /// <c>base_movement_velocity_+%</c>): it scales only the server's walk estimate and is never sent.</summary>
    public double SpeedFactor { get; init; } = 1;

    /// <summary>Its walk goes through living bodies (a monster that walks through its own minions).</summary>
    public bool ThroughBodies { get; init; }

    /// <summary>While it walks up to a melee target it is ordered again at most this often, and only with its attack (an
    /// order restarts its walk clip, so a frequent one makes it walk in steps); null is the ordinary cadence.</summary>
    public double? ReorderSeconds { get; init; }
}

public sealed class SpawnedMonster(string label, PlayerWalk walk, int aggroRange, int stopRange, uint maxLife)
{
    public uint ObjectId { get; set; }
    /// <summary>What the players are shown of it changed: a timed debuff, a quest threshold reached. Set by whoever spawns
    /// it.</summary>
    public Action? PresentationChanged;
    public MadnessExplosion? MadnessExplosion;
    public readonly SpiderWebState SpiderWebs = new();

    /// <summary>How it moves where its variety's row does not say (SpawnMonster's <c>movement</c>).</summary>
    public MonsterMovement Movement { get; init; } = MonsterMovement.Default;
    public double ReceivedActionFactor = 1;
    public double ReceivedActionUntil;
    public bool ReceivedActionFloor;
    public double RecipientDebuffRate = 1;
    /// <summary>Sent out of the wake zone (S→C 0x1aa): the client no longer ticks it.</summary>
    public bool Asleep;
    /// <summary>Kept in the instance but absent from client views during a scripted disappearance.</summary>
    public bool WorldHidden;
    /// <summary>The action it takes now. A boss with a kit of its own swaps this for the chosen skill of that kit each
    /// AI pass (BossKits.Choose); everything downstream — the walk-up, the swing's clip, its hit — reads this one.</summary>
    public MonsterAttack? Attack { get; set; }
    /// <summary>The rest of a boss's skills (BossKits): the effect each one comes from, its action and its cooldown.
    /// Empty for every monster that swings one attack.</summary>
    public IReadOnlyList<MonsterKitSkill> Kit { get; init; } = [];
    /// <summary>The effect behind each id on the actor's skill list, so a mod's command can play any of the monster's own
    /// skills by the GrantedEffects Id the client shows. Empty when the spawn named none.</summary>
    public IReadOnlyList<(int Effect, ushort Id)> SkillIds { get; init; } = [];
    /// <summary>When each kit skill is ready again, by granted effect (Clock seconds), in the area's frame.</summary>
    public readonly Dictionary<int, double> KitReadyAt = [];
    /// <summary>The damage taken toward each kit trigger on damage taken since it was last cast, by granted effect.</summary>
    public readonly Dictionary<int, double> KitDamageTaken = [];
    bool originallyHostile = true;
    public bool OriginallyHostile => originallyHostile;
    public bool Hostile { get => originallyHostile && !TemporarilyAllied; init => originallyHostile = value; }
    /// <summary>The area's time when a temporary ally returns to its original allegiance.</summary>
    public double TemporaryAllyUntil;
    public bool TemporarilyAllied => TemporaryAllyUntil > 0 && !Dead && Life > 0;
    public bool FightsForPlayers => AlliedFighter || TemporarilyAllied;

    /// <summary>A controller drives every action (a Survivors gate boss): the ordinary AI neither chases nor swings.</summary>
    public bool Scripted { get; init; }
    /// <summary>Non-blocking actor (e.g. Mirage Warrior): excluded from living collision so walkers pass through it.</summary>
    public bool NonBlocking { get; init; }
    public uint? OwnerId { get; init; }
    // The monster whose summoning skill brought it (MonsterAttack.Summon), whether its death gives no experience or items
    // (summoned_monsters_no_drops_or_experience), and when its own summoning skill is off cooldown.
    public uint? SummonerId;
    /// <summary>When a summoned monster's duration runs out (MonsterSummonInfo.Lifetime): it is retired then.</summary>
    public double ExpiresAt = double.PositiveInfinity;
    public bool NoRewards;
    /// <summary>A shrine's ally (the Skeletal shrine's skeletons): it fights the player's enemies as a minion does while
    /// not being one. GGG's shrine summons are the shrine's, not the player's ("You are not considered to be summoning
    /// them": your modifiers do not apply), so it carries no <c>is_player_minion</c> stat, takes no minion modifier, is
    /// not in <see cref="PlayerMinions"/> and counts towards no minion limit.</summary>
    public bool AlliedFighter;
    public int Overkill;
    public readonly MonsterIgnites HeraldBurns = new();
    public double SummonReadyAt;
    /// <summary>When a non-kit monster's cooldown-gated attack (e.g. BatScreech, MotherOfFlamesMagmaOrb) is ready again.</summary>
    public double CooldownReadyAt;
    public double RampageBlindUntil;
    public int StunSerial;
    /// <summary>Server-side stun immunity independent of the rolled mods (pinnacle bosses such as The King in the Mists).</summary>
    public bool CannotBeStunned { get; init; }
    /// <summary>The humanoid King's life cannot reach 0 before the three fetishes hand the fight to the treant.</summary>
    public bool KingPhaseLocked { get; init; }
    /// <summary>The treant's emergence grace: hits do nothing until KingInMists clears it.</summary>
    public bool KingGrace;
    /// <summary>The humanoid's fetish channel: no damage, ailment, leech or hit reaction until the ritual ends.</summary>
    public bool RitualGrace;
    // A Mystic Fetish waits for the King's ritual before it can be damaged (the client-side buff and hidden mini bar
    // are the same pair the humanoid's intro uses).
    public bool FetishGrace;
    /// <summary>Pending life thresholds and immunity owned by an encounter controller.</summary>
    public LifePhaseGate? LifePhases;
    /// <summary>An encounter's own entrance: hits do nothing and no chill, freeze or shock lands until its script clears it
    /// (Izaro rising on his elevator; live, the ailments landed while hits did nothing).</summary>
    public bool EntranceGrace { get => Ailments.Grace; set => Ailments.Grace = value; }
    // No damage, ailments, impale or hit reaction while a scripted grace runs.
    public bool DamageImmune;
    public bool Invulnerable => DamageImmune || KingGrace || RitualGrace || FetishGrace || EntranceGrace || LifePhases?.Active == true;
    // What it is, for the experience and items its death gives (null for monsters made without a variety).
    public MonsterCatalog.Variety? Variety { get; init; }
    // Its known TakeHit clip lengths supply HitReaction's recovery duration. Native Actor TakeHit selects the actual
    // clip through its current ACT; rig indexes are retained as extraction/profile diagnostics.
    public IReadOnlyList<HitAnimation> HitAnimations { get; init; } = [];
    public byte Level { get; init; } = 1;
    public MonsterRarity Rarity { get; init; }
    // Its mods (MonsterMods.cs): the rarity's own and the rolled affixes, with what they change. Kept with the instance.
    public MonsterModSet Mods { get => mods; set { mods = value; MarkStats(ModsLayer); } }
    MonsterModSet mods = MonsterModSet.None;
    // A curse put on or taken off changes its stats.
    public MonsterCurses Curses => curses ??= new(() => MarkStats(CursesLayer));
    public bool HasCurses => curses is not null && (curses.All.Count > 0 || curses.Presented.Count > 0 || curses.PresentedVersion != curses.Version);
    MonsterCurses? curses;
    /// <summary>Stats an encounter's script gives it for a while (Izaro's statues' buffs), on top of its mods: they add to
    /// its hits (<see cref="MonsterModEffects.AddedTo"/>) and to what it takes.</summary>
    public IReadOnlyDictionary<uint, int>? EncounterStats { get => encounterStats; set { encounterStats = value; MarkStats(EncounterLayer); } }
    IReadOnlyDictionary<uint, int>? encounterStats;
    /// <summary>What its own buffs grant while they last (MonsterSkills.cs: a war cry's, a buff skill's).</summary>
    public IReadOnlyDictionary<uint, int>? BuffStats { get => buffStats; set { buffStats = value; MarkStats(BuffLayer); } }
    IReadOnlyDictionary<uint, int>? buffStats;
    /// <summary>Independent temporary sources, removed by their owner without restoring stale encounter snapshots.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<uint, int>> TemporaryStats => temporaryStats;
    readonly Dictionary<string, IReadOnlyDictionary<uint, int>> temporaryStats = new(StringComparer.Ordinal);
    public void SetTemporaryStats(string key, IReadOnlyDictionary<uint, int> stats)
    {
        temporaryStats[key] = stats;
        MarkStats(TemporaryLayer(key));
    }
    public bool RemoveTemporaryStats(string key)
    {
        bool removed = temporaryStats.Remove(key);
        if (removed) MarkStats(TemporaryLayer(key));
        return removed;
    }

    // Its stats (Effects) are a stat graph (StatGraph.cs): a layer per source (the mods, the encounter's stats, its buffs,
    // the curses, each temporary source, made on its first use), and a change marks only its own. The area's Stats phase
    // steps the graph once a frame (StatsByFrame, set by that phase), adding up again only the stats the marked layers
    // changed; until then reads see the last step. Outside an area's frames (tests, tools) a read after a change steps at once.
    const int ModsLayer = 0, EncounterLayer = 1, BuffLayer = 2, CursesLayer = 3;
    readonly Dictionary<string, int> temporaryLayers = new(StringComparer.Ordinal);
    StatGraph? statGraph;
    StatGraph Graph
    {
        get
        {
            if (statGraph is { } made) return made;
            var g = new StatGraph();
            g.AddLayer(into => AddStats(into, mods.Effects.Ids));
            g.AddLayer(into => { if (encounterStats is { } e) AddStats(into, e); });
            g.AddLayer(into => { if (buffStats is { } b) AddStats(into, b); });
            g.AddLayer(into => { if (curses is { } c) AddStats(into, c.Stats); });
            return statGraph = g;
        }
    }
    static void AddStats(StatWriter into, IReadOnlyDictionary<uint, int> stats)
    {
        foreach (var (id, value) in stats) if (value != 0) into.Add(id, value);
    }
    int TemporaryLayer(string key)
    {
        var graph = Graph;
        lock (graph)
        {
            if (temporaryLayers.TryGetValue(key, out int layer)) return layer;
            layer = graph.AddLayer(into => { if (temporaryStats.TryGetValue(key, out var t)) AddStats(into, t); });
            temporaryLayers[key] = layer;
            return layer;
        }
    }
    void MarkStats(int layer) { Graph.MarkLayer(layer); StatsDirty = true; StatsMarked?.TryAdd(this, 0); }
    /// <summary>Where its world lists the monsters a change marked (MonsterTable).</summary>
    internal System.Collections.Concurrent.ConcurrentDictionary<SpawnedMonster, byte>? StatsMarked;
    /// <summary>The graph's step and layer counts, for Game_TRACE=budget.</summary>
    public (long Steps, long Layers) StatWork => statGraph is { } g ? (g.Steps, g.LayerBuilds) : (0, 0);
    MonsterModEffects? effects;
    public bool StatsDirty { get; private set; } = true;
    public bool StatsByFrame { get; set; }
    readonly List<Action<MonsterModEffects, MonsterModEffects>> afterRebuild = [];
    /// <summary>Runs once after the next build, with the stats before and after (a maximum life that follows them).</summary>
    public void AfterStatsRebuild(Action<MonsterModEffects, MonsterModEffects> then)
    {
        if (!StatsByFrame) { var before = Effects; RebuildStats(); then(before, Effects); return; }
        afterRebuild.Add(then);
    }
    public MonsterModEffects Effects
    {
        get
        {
            if (effects is null || StatsDirty && !StatsByFrame) RebuildStats();
            return effects!;
        }
    }
    /// <summary>Applies the marked sources' changes (the Stats phase, or the first read).</summary>
    public void RebuildStats()
    {
        var before = effects ?? MonsterModEffects.None;
        StatsDirty = false;
        var graph = Graph;
        if (graph.Step() || effects is null) effects = new MonsterModEffects(graph.Values);
        if (afterRebuild.Count == 0) return;
        var then = afterRebuild.ToArray();
        afterRebuild.Clear();
        foreach (var action in then) action(before, effects);
    }
    // The client's speed roll for this object in percent (MonsterSpeedRoll.cs); Walk's speed already includes it.
    public int SpeedRoll { get; init; }
    // Damage over time not yet applied (fractions of a point between ticks).
    public double Burn;
    // Life regeneration not yet applied (fractions of a point between ticks; RegenerateMonsters).
    public double Regen;
    // When the player last hit it (Clock seconds): "enemies you've Hit Recently". Reset at the player's death.
    public double PlayerHitAt = double.NegativeInfinity;
    // When its attack last hit the player (Clock seconds; evaded and blocked attacks do not hit): "enemies that have Hit
    // you with an Attack Recently". Reset at the player's death. Owned by the area's frame.
    public double HitPlayerAt = double.NegativeInfinity;
    // The attack time its Stats component carries on the client (SendAttackerSlows), when it differs from Attack.Seconds.
    public double? SwingSecondsSent;
    // Impales retained for consumption/expiry, including on a corpse (Impale.cs).
    public readonly ImpaleState Impale = new();
    // The player's ignites on it (MonsterIgnites.cs), in the area's frame.
    public readonly MonsterIgnites Ignites = new();
    // The player's bleeds on it, in the area's frame: the same rules as ignites (wiki Bleeding: only the strongest
    // deals damage, each keeps its own expiry).
    public readonly MonsterIgnites Bleeds = new();
    // The player's poisons on it (MonsterPoisons.cs), in the area's frame.
    public readonly MonsterPoisons Poisons = new();
    // The player's Contagion on it (MonsterContagion.cs), in the area's frame.
    public readonly MonsterContagion Contagion = new();
    public MonsterContagion? Bane;
    // The player's Essence Drain debuff on it (MonsterEssenceDrain.cs), in the area's frame.
    public readonly MonsterEssenceDrain EssenceDrain = new();
    // The Ruptures its critical bleeding hits have put on it (MonsterRuptures.cs), in the area's frame.
    public readonly MonsterRuptures Ruptures = new();
    // Overpowered (Block Chance Reduction): the block chance its blocked supported hits take away until the deadline.
    public int OverpoweredPercent;
    public double OverpoweredUntil;
    // The chill, freeze and shock the player's hits have put on it (MonsterElementalAilments); kept on the corpse.
    public readonly MonsterElementalAilments Ailments = new();
    // The ailments of kinds the rules define (RuleAilments); kept on the corpse.
    public readonly RuleAilments RuleAilments = new();
    // All outgoing attacks share this defender's sequence, in the area's frame. A new spawn starts fresh.
    public readonly GameServer.Core.World.EvasionEntropy AttackEvasion = new();
    // While a swing runs (Clock seconds) the monster neither chases nor swings again.
    public double BusyUntil;
    // Every AI order owns a generation. A flinch invalidates queued echoes and contact work from that order.
    // SwingGeneration identifies ordinary attack recovery so canceling it does not clear an emerge or boss stage.
    public int ActionGeneration;
    public int? SwingGeneration;
    public double SwingOrderedAt;
    // A known ordinary melee clip's contact and recovery, measured before action speed. The generation prevents
    // a later ailment update from retiming a canceled swing or a different action's busy period.
    public (int Generation, ActionTimeClock Clock, double Contact, double Duration, double Tail)? MeleeClock;
    // The last melee order, or admitted lockstep AI Move/swing: one rising counter for the actor's orders.
    // The client picks a melee swing's clip by its action sequence.
    public ushort Sequence = 0x8000;
    public ushort NextSequence() => Sequence = (ushort)(0x8000 | ((Sequence + 1) & 0x7fff));
    // The player's debuffs on it (Game.EnemyDebuffsTick, in the area's frame): taunted by him until when (Clock
    // seconds) with the taunted buff shown and the end it shows, his passives' and Pride's enemy auras (BuffDefinitions
    // row → buff instance), and the increased damage taken they give (every type; fire, cold and lightning) that its
    // hits read.
    public double TauntedUntil = double.NegativeInfinity, TauntShownUntil;
    public uint? TauntingPlayerId;
    public (Player Owner, TotemState.Totem Source, double Until)? DecoyTaunt;
    public ushort TauntBuff;
    // Intimidated and unnerved by his hits (Game.HitDebuffsOnHitLocked) until when, shown the same way as the taunt.
    public double IntimidatedUntil = double.NegativeInfinity, IntimidateShownUntil, UnnervedUntil = double.NegativeInfinity,
        UnnerveShownUntil;
    public ushort IntimidateBuff, UnnerveBuff;
    // Crushed by his hits (Awakened Brutality) until when, shown with its own crushed buff; Crush lowers its physical
    // damage reduction (Game.CrushReduction).
    public double CrushedUntil = double.NegativeInfinity, CrushShownUntil;
    public ushort CrushBuff;
    // Covered in Ash by his hits (Wildfire Phloem, etc.) until when: 20% less movement speed and 20% increased fire
    // damage taken, shown with BuffDefinitions 807 (cover_in_ash).
    public double CoveredInAshUntil = double.NegativeInfinity, CoveredInAshShownUntil;
    public ushort CoveredInAshBuff;
    // Maimed by his hits until when (the sole movement owner) and the physical damage increase its support gave, whose
    // independent time plain Maim does not refresh; shown with its own maimed buff.
    public double MaimedUntil = double.NegativeInfinity, MaimShownUntil;
    public double MaimedPhysicalTakenUntil = double.NegativeInfinity;
    public ushort MaimBuff;
    public int MaimedPhysicalTakenPct;
    public int MaimedMovementShownPct;
    public bool MaimedMovementShown;
    public double WalkSpeedAtZeroFactor;
    // Hindered by a spell hit until when, shown with the client's hinder buff (ReminderTextHinder: 4 seconds);
    // hindered enemies have 30% reduced movement speed (Game.HinderSeconds and the walk factor).
    public double HinderedUntil = double.NegativeInfinity, HinderShownUntil;
    public ushort HinderBuff;
    // Knocked back by a hit (Game.KnockbackOnHitLocked): the cell the AI tick presents with a Teleport, null when none.
    public (int X, int Y)? KnockbackPending;
    // Pending native 0x1af knockback presentation (Client/Game.exe 0x141e9d030) to fold into the hit's ActionHit packet.
    public readonly record struct NativeKnockback(byte Angle, short SpeedMod, short DistanceMod);
    public NativeKnockback? NativeKnockbackPending;
    // Hallowing Flame (Hallow Support): until when the next attack against it gains extra fire, and that share.
    public double HallowingFlameUntil = double.NegativeInfinity;
    public double HallowingFlamePct;
    // Excommunicate Support: until when its chaos damage is suppressed.
    public double ExcommunicatedUntil = double.NegativeInfinity;
    // "Killed Enemies drop X% increased Items" from the skill of the last player hit (Item Quantity Support).
    public int KilledWithItemQuantityPct;
    // The same kill's increased rarity of dropped items from the skill of the last player hit (Item Rarity Support).
    public int KilledWithItemRarityPct;
    // His Exposure on it (Game.ExposureOnHitLocked): until when, by element and magnitude, the highest running one lowering
    // its resistance; per element (fire, cold, lightning) when the last of them ends and its shown buff.
    public readonly Dictionary<(DamageType Type, int Magnitude), double> Exposures = [];
    public readonly double[] ExposureUntil = [double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity];
    public readonly double[] ExposureShownUntil = new double[3];
    public readonly ushort[] ExposureBuff = new ushort[3];
    // His Withered stacks on it (Game.WitherOnHitLocked): each one's end and chaos damage taken; the buff shown for them.
    public readonly List<(double Until, double Pct)> Withered = [];
    public int WitherShownStacks;
    public double WitherShownUntil;
    public ushort WitherBuff;
    // By (the player's PeerId, the aura's BuffDefinitions row): each client's buff instance of each player's enemy aura.
    public readonly Dictionary<(uint Peer, ushort Definition), ushort> PlayerAuras = [];
    public (int All, int Elemental, int Physical) TakenFromPlayer;
    /// <summary>Whose Flesh and Stone (Blood Stance) it is in, and the aura's radius: a hit's falloff with the distance.</summary>
    public Player? FleshFrom;
    public int FleshRadius;
    // Values and timed marks each player keeps on it, by (the player's Id, a key the rules name): a party shares its
    // monsters, so each source sets and clears only its own entries and never another participant's.
    readonly Dictionary<(uint Source, string Stat), int> sourceValues = [];
    readonly Dictionary<(uint Source, string Key), double> sourceMarks = [];
    /// <summary>Whether any source keeps a value on it.</summary>
    public bool HasSourceValues => sourceValues.Count > 0;
    /// <summary>Every source's value of <paramref name="stat"/> on it.</summary>
    public IEnumerable<(uint Source, int Value)> SourceValues(string stat)
    {
        foreach (var ((source, key), value) in sourceValues) if (key == stat) yield return (source, value);
    }
    /// <summary>Sets <paramref name="source"/>'s value of <paramref name="stat"/> on it; 0 removes the entry.</summary>
    public void SetSourceValue(uint source, string stat, int value)
    {
        if (value == 0) { if (sourceValues.Count > 0) sourceValues.Remove((source, stat)); }
        else sourceValues[(source, stat)] = value;
    }
    /// <summary>Marks <paramref name="key"/> as <paramref name="source"/>'s until <paramref name="until"/> (the later
    /// of this and an earlier mark).</summary>
    public void MarkSource(uint source, string key, double until)
    {
        if (until > sourceMarks.GetValueOrDefault((source, key), double.NegativeInfinity)) sourceMarks[(source, key)] = until;
    }
    /// <summary>Whether <paramref name="source"/>'s mark of <paramref name="key"/> runs at <paramref name="now"/>.</summary>
    public bool SourceMarked(uint source, string key, double now) =>
        sourceMarks.TryGetValue((source, key), out double until) && until > now;
    /// <summary>Every source's mark of <paramref name="key"/> on it, expired ones included.</summary>
    public IEnumerable<(uint Source, double Until)> SourceMarks(string key)
    {
        foreach (var ((source, k), until) in sourceMarks) if (k == key) yield return (source, until);
    }
    /// <summary>Removes every value and mark <paramref name="source"/> keeps on it (the source left the instance).</summary>
    public void ForgetSource(uint source)
    {
        foreach (var key in sourceValues.Keys.Where(k => k.Source == source).ToList()) sourceValues.Remove(key);
        foreach (var key in sourceMarks.Keys.Where(k => k.Source == source).ToList()) sourceMarks.Remove(key);
    }
    // Pride's more Physical Damage taken, ramped by the time this monster has stayed in the aura (EnemyDebuffsTick).
    public double PrideSince = double.NaN;
    public double PridePhysicalMore;
    // Fleeing (MonsterFlee): from a hit's roll until its walk to the flee point ends; FleeTo is the point until the AI
    // orders that walk.
    public bool Fleeing;
    public (int X, int Y)? FleeTo;
    public string Label => label;
    public PlayerWalk Walk { get; set; } = walk;
    public int AggroRange => aggroRange;
    public int StopRange => stopRange;
    public uint MaxLife { get; set; } = maxLife;
    /// <summary>Its life; a change goes to every client at the frame's end (AreaLife).</summary>
    public uint Life { get; set { if (field == value) return; field = value; LifeChanged?.Invoke(); } } = maxLife;
    internal Action? LifeChanged;
    // Until when its last flinch plays (Clock seconds; HitReaction): no new action or flinch before.
    // Owned by the area's frame.
    public double FlinchUntil = double.NegativeInfinity;
    // A boss cannot be stunned again until then (HitReaction). Owned by the area's frame.
    public double StunImmuneUntil = double.NegativeInfinity;
    public bool Aggro, Dead;
    /// <summary>The player the instance's monster acts for now (its chase, swings and damage run on that player's
    /// connection: SharedMonsters.cs MonsterTargetsLocked); null while it has none.</summary>
    public Player? Target;
    /// <summary>The player whose hit landed on it last: an ignite, bleed or poison that kills it is theirs (SharedKills.cs).</summary>
    public Player? LastHitBy;
    // Its corpse was consumed (a kill explosion, the Kitava tattoo, a Devour-supported killing blow) and its object
    // removed. Owned by the area's frame.
    public bool CorpseConsumed;
    public int HitFireExplosionPercent;
    public bool CursedAtDeath;
    public double NonAuraHexUntilAtDeath;
    // Shared-instance sync shows these bodies to new viewers and removes each view on consumption or expiry.
    public bool CreatedAsCorpse;
    // The Unhinged hit-kill decision captured at the lethal Life mutation, bound to the client that accepted the hit.
    // Kept on this exact monster instance until the kill publication consumes it.
    public UnhingedHitKillReceipt? UnhingedHitKill;
    public sealed record UnhingedHitKillReceipt(Player Owner, bool DestroyCorpse);

    // A warcry's Howl of the Wolf already rerolled this corpse's loot: one reroll per corpse, like another use of it.
    // Owned by the area's frame.
    public bool CorpseLooted;
    // A Devour-supported killing blow recovered its life/mana: the kill consumes the corpse and removes its object.
    // Set by the hit, read by the kill.
    public bool DevourCorpse;
    // Soul credit alone is idempotent; this does not alter existing XP, loot, quest or charge policy.
    public bool SoulKillProcessed;
    public (int X, int Y)? LastTarget;
    // Lockstep (07 §5): the action generation of the chase order (the attack aimed at the player) whose walk-up the client
    // runs. The walk-up is open while that is still the current generation (Game.WalkUpOpen): a planned order, a give-up
    // and a freeze carry it along (Game.NextPlannedAction), while a swing order, a flinch, a stage and a transfer move the
    // generation on. Set when the order is placed at the gate (Game.PlaceLockstepChase), cleared when an order the client
    // takes is placed there (Game.LockstepOrderFrom) and when the walk-up swings or fails (Game.StepFrames).
    public int? ClientChase;
    // When its chase walk-up's MoveTo search timer (+0x55c) last started: the order's frame (Game.TakeLockstepChase), or
    // a search again on the way (Game.StepWalkUp).
    public double ClientChaseTimerFrom;
    // The start of the frame in which the client's walk-up found the player in reach and so began its swing, and the
    // action generation the swing belongs to, for the AI to take as its own (Game.PlanMonsterAi); null when none.
    public double? ClientSwingAt;
    public int ClientSwingAction;
    // The attack the chase order named (its 0x1ab's skill), which the walk-up's swing is: a kit may choose another while
    // the monster walks (Game.TakeLockstepChase).
    public MonsterAttack? ClientChaseAttack;
    // Lockstep: the start of the frame in whose tick the client ends a walk that an in-place action left going under it
    // (Game.MonsterSwingOrderLocked), the frame after its Actor ends that action; null when none is pending. The step
    // pass applies it (Game.StepFrames), and an order the client takes drops it (Game.LockstepOrderFrom).
    public double? ClientActionWalkStop;
    public double NextChaseRetryAt;
    // The cell a Move chase's current walk was cut to (Game.LegCells along its way), or null when the walk goes the whole
    // way: a walk at its leg's end is still on its way for the chase cadence.
    public (int X, int Y)? Leg;
    // A boss's stage (BossFights) and whether a hit has started it.
    public BossFights.Stage? Stage { get; init; }
    public bool StageTaken, StageRunning;
    public double StageEffectAt = double.NaN;
    public bool StageActionStarted, StageEffectApplied;
    // A quest threshold (Quests.ThresholdRule): the monster never dies; reaching it sets the flags once (Game.QuestThreshold).
    public Quests.ThresholdRule? Threshold { get; init; }
    public bool ThresholdReached;
    // Its StateMachine's shared states as last sent, and whether the big boss life bar is up.
    public StateMachineData? States;
    public bool BossBarShown;
}

/// <summary>A lockstep client's attack on a monster or ground point (07 §5, §6): the player's MoveTo toward its aim, which
/// the client's frames run and the step pass follows (Game.StepFrames), and the swing it ends in. Under the Monsters
/// lock.</summary>
public sealed class LockstepAttack(int action, uint targetId, SpawnedMonster? target, int reach, int tolerance, int precision,
    Func<double> hold)
{
    public int Action { get; } = action;
    public uint TargetId { get; } = targetId;
    public SpawnedMonster? Target { get; } = target;
    public (int X, int Y) GroundTarget { get; init; }
    public (int X, int Y) AimAt(double at) => Target?.Walk.Position(at) ?? GroundTarget;
    // The request's flag 0x0001, attack in place: the player swings where the frame's steps leave him and walks nowhere
    // (Game.PlaceLockstepAttack).
    public bool InPlace { get; init; }
    // MoveTo's max range (+0x524, the weapon's range less one), the target's size (+0x52d) and the precision (+0x528).
    public int Reach { get; } = reach;
    public int Tolerance { get; } = tolerance;
    public int Precision { get; } = precision;
    // How long the swing holds the player from the end of the frame it began in (LockstepAttackCutIn and the tail),
    // read when the swing begins: the attack time then, not at the request.
    public Func<double> Hold { get; } = hold;
    // Ground attacks have no target-hit task to advance their repeat timer when the swing starts.
    public Func<double>? GroundAttackSeconds { get; init; }
    // Placed where its 0x1ab went out and not decided yet: the step pass tests its goal every frame.
    public bool Open;
    // When MoveTo's search timer (+0x55c) last started (Game.StepWalkUp).
    public double TimerFrom;
    public readonly record struct Swing(double Start, double End, (int X, int Y) From);
    // Completed in the area's frame by the step pass: the attack's task must not run on there.
    readonly TaskCompletionSource<Swing?> decided = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // The swing (its frame and the cell it is swung from), or null when the walk-up failed or was replaced.
    public Task<Swing?> Decided => decided.Task;
    public void Swings(double start, double end, (int X, int Y) from)
    {
        Open = false;
        decided.TrySetResult(new(start, end, from));
    }
    public void Fails()
    {
        Open = false;
        decided.TrySetResult(null);
    }
}

// A running Righteous Fire: the player's buff instance, the skill, its damage (percent of maximum life per minute, to
// enemies and to the player), its current radius in cells, and the aura buff instance on each monster inside it.
public sealed record RighteousFireState(ushort Buff, ushort SelfAura, ushort Skill, int DamagePctPerMinute, int BurnPctPerMinute)
{
    /// <summary>The current ring object, replaced in place when the retained fire's radius changes.</summary>
    public uint? Ring { get; set; }
    /// <summary>The retained fire's current radius in cells.</summary>
    public int Radius { get; set; }
    /// <summary>The ring art selected on activation; null means this activation has no ring.</summary>
    public string? RingArt { get; init; }
    /// <summary>The current gem's neutral righteous_fire_spell_damage_+%_final value, refreshed from its own stats.</summary>
    public int SpellDamageMore { get; set; }
    /// <summary>The retained skill's two conditional support values and its pending changes in added damage factor.</summary>
    public (int Lifetap, int PerInspiration) ConditionalSupportMore { get; set; }
    public List<(double At, double Extra)>? SupportDamageChanges { get; set; }
    public int EnergyShieldBurnPctPerMinute { get; init; }
    public int EnergyShieldDamagePctPerMinute { get; init; }
    /// <summary>The exact physical gem source that activated this fire, or null for legacy state.</summary>
    public GemSourceLifetimes.SourceLifetime? SourceLifetime { get; init; }
    /// <summary>The instance that owned this activation; current-state checks must still name this exact instance.</summary>
    public InstanceState Instance { get; init; } = null!;
    public readonly Dictionary<uint, ushort> Auras = [];
    /// <summary>The worn skill effect's righteous_fire_aura visual (BuffVisuals row; null: the row's own), on the enemies too.</summary>
    public ushort? AuraVisual { get; init; }
}

// A running Bladefall of Trarthus (BladefallVolleys): its two buff instances (skill_cost_over_time, righteous_blades),
// the skill, the game time of its next volley and the mana it spends per minute (mana_spent_per_minute, as set on the
// player). Blades: the ground blades still standing, oldest first, with the game time each falls quiet and goes.
public sealed record BladefallTrarthusState(ushort CostBuff, ushort BladesBuff, ushort Skill)
{
    public double NextVolley;
    public int CostPerMinute;
    public bool PaysLife, OutOfLife;
    public double LifeCostCarry;
    public bool Ended;
    public readonly List<(uint Id, (int X, int Y) At, double Landed, double Gone)> Blades = [];
}

// The last instance list shown (0xae); 0xaf picks a row of it.
public sealed record ChooserList(ushort Area, IReadOnlyList<ulong> Ids, bool AtWaypoint = false, int? ArrivalEntrance = null);

/// <summary>An input of the player's stats the framework holds; its change is handed to the game (Player.StatsChanged).
/// Pools, Items and Passives are read through the stat graph (StatGraph.ReadInput), so their change runs again only the
/// steps that read them.</summary>
public enum StatInput : byte { Pools, Life, Mana, EnergyShield, Walk, VaalPrevention, RecentEvents, Items, Passives, RecentEnemyHits, FlaskEffects, EnemyHit }

public sealed partial class Player(AreaLayout layout, uint ticket, uint seed) : IHasComponents
{
    /// <summary>The character this player entity is (its save data).</summary>
    public Character Character => character ??= new() { StatsChanged = MarkStatsDirty };
    Character? character;
    /// <summary>The client's connection that controls this player.</summary>
    public Connection Connection => connection ??= new(this);
    Connection? connection;
    /// <summary>What modules keep on this player (their stores).</summary>
    public Components Components { get; } = new();

    /// <summary>The hand-over for a change of what the player's stats are built from: the game sets it and marks what reads
    /// the input. An input the framework holds marks itself when it changes (its setter).</summary>
    public static Action<Player, StatInput>? StatsChanged;
    public void MarkStatsDirty(StatInput input) => StatsChanged?.Invoke(this, input);

    public readonly CharacterBuildImports.Session BuildImport = new();
    public bool BuildFinalized;
    public bool Controlled, ControlledPrepared;
    public double ControlledNextAction, ControlledDeadSince = double.NaN;
    public AreaLayout Layout { get; private set; } = layout;
    public uint Ticket => ticket;
    public uint Seed => seed;
    public readonly Guid VitalsSessionId = Guid.NewGuid();
    /// <summary>The league this connection plays by (Program.LeagueFor); LeagueForm above is the character's league form, from Auth.</summary>
    public LeagueRuntime? LeagueModule;
    /// <summary>The hideout scene furnishing this area, for the modules placing stations.</summary>
    public HideoutScene? CurrentHideoutScene;
    /// <summary>The module whose native window is open (IPlayerWorld.OpenWindow).</summary>
    public string? ModuleWindow;
    /// <summary>Where the player stood when <see cref="ModuleWindow"/> opened, for walk-away closing.</summary>
    public (string Owner, int X, int Y)? ModuleWindowAnchor;
    public bool BossRecoveryHandled;
    /// <summary>The area the player left through an exit to come here, or null (login, Resurrect in Town).</summary>
    public ushort? CameFrom { get; init; }
    public int? ArrivalEntrance { get; init; }
    /// <summary>Optional return area WorldAreas HASH16 emitted in S→C 0x1d (flags byte 0 bit 0x08), set by a module during AreaHeader.</summary>
    public ushort? ReturnAreaHash;
    /// <summary>The instance's game time (InstanceWorld.Clock), shared with everyone in it.</summary>
    public System.Diagnostics.Stopwatch Clock => Instance?.World.Clock ?? ownClock;
    readonly System.Diagnostics.Stopwatch ownClock = System.Diagnostics.Stopwatch.StartNew();
    // The Escape menu's pause (C→S 0x1d4) stops the Clock: walks, cooldowns and flask effects stand still meanwhile.
    public bool Paused => !Clock.IsRunning;
    // A module-owned pause (the Survivors reward window) that must survive the client's own C→S 0x1d4 unpause: while
    // this is set, SetPaused(false) leaves the clock stopped and keeps the client's world paused.
    public bool PauseHeld;
    // A transfer invalidates flights even during the gap after PendingTransfer is consumed by Transfer.
    // Owned by the area's frame, like projectile impact commits and pause changes.
    public bool LeavingArea;
    // Auth accepted the destination; PendingTransfer is already consumed before this handoff.
    public bool TransferAccepted;
    public RecentEnemyHits RecentEnemyHits => recentEnemyHits ??= new() { Changed = () => MarkStatsDirty(StatInput.RecentEnemyHits) };
    RecentEnemyHits? recentEnemyHits;
    // The other "recently" events (issue #504): what happened in the last four seconds, for the stats that ask.
    public RecentEvents RecentEvents => recentEvents ??= new() { Changed = () => MarkStatsDirty(StatInput.RecentEvents) };
    RecentEvents? recentEvents;
    public readonly PlayerElementalAilments ElementalAilments = new();
    // The player's ailments of kinds the rules define (RuleAilments), cleared with the elemental ones.
    public readonly RuleAilments RuleAilments = new();
    // Incoming bleeding and poison from monster contacts, reflected outgoing ailments and traps. This
    // is the sole state/damage owner and is advanced by the player recovery writer.
    public readonly IncomingDamagingAilments IncomingAilments = new();
    // The King in the Mists' Portent Vapors: stacks taken from his Mystic Fetishes and when they expire. The server
    // applies the increased damage taken to his hits; the buff icon and count are presentation.
    public int KingPortentStacks;
    public double KingPortentUntil = double.NegativeInfinity;
    /// <summary>The Crux of Nothingness arena's "mapboss" cell: the King's spawn and the base of the fixed Mystic Fetish
    /// offsets (KingInMists.cs). Set when the area is populated, so both forms use the same origin.</summary>
    public (int X, int Y)? KingOrigin;
    public Random IncomingHitRandom = Random.Shared;
    // The player's ignite and bleed rolls on monsters (MonsterIgnites), apart from the hit's own streams.
    public Random IgniteRandom = Random.Shared;
    public int IncomingHitEpoch;
    /// <summary>The last incoming bleed/poison state actually presented through the player send gate.</summary>
    public IncomingDamagingAilments.State? PresentedIncomingAilments;
    public int PresentedIncomingAilmentEpoch;
    public int DeathNotificationEpoch = -1;
    public int Resurrecting;
    public int FreezeGeneration, FreezeShown;
    public double FreezeHeldUntil;
    public double NextWakeZoneAt;
    // How far every walker's steps have been checked (Game.StepWalkers).
    public double WalkersSteppedTo;
    // When the movement skill under way ends on the client: the server has already landed it (Game.ResyncAfter).
    public double LandingUntil;
    public int ActionInterruptionGeneration => unchecked(HitRecovery.Generation + FreezeGeneration);
    public (int Count, int Chance)? PresentedRecentEnemyHits;

    // A failed transfer retains history but cannot revive a swing from before the attempted transfer.
    public void InvalidateIncomingHits(bool clear = true)
    {
        if (PlayerLife == 0 || Closed)
        { PaidChannel = null; NonChaosDotBypassCarry = NonChaosDotTakenCarry = ChaosDotBypassCarry = 0; }
        IncomingHitEpoch++;
        // The action state machine replaces the old swing epoch argument. Retired work cannot
        // resume after a failed transfer, even when admission reopens on this same connection.
        foreach (var monster in Area.Monsters.Values)
        {
            monster.ActionGeneration++;
            monster.SwingGeneration = null;
        }
        if (clear)
        {
            RecentEnemyHits.Clear();
            RecentEvents.Clear();
            ElementalAilments.Clear();
            RuleAilments.Clear();
            IncomingAilments.Clear();
            PresentedIncomingAilments = null;
            PresentedIncomingAilmentEpoch = IncomingHitEpoch;
        }
    }
    public int ProjectileEpoch;
    public IProjectileRuntime? ProjectileRuntimeState;
    public readonly HashSet<uint> ProjectileObjects = [];

    public bool ProjectileActive(int epoch)
    {
        return !Closed && !LeavingArea && !RageRetired && ProjectileEpoch == epoch;
    }

    public bool TryBeginAreaChange(PendingTransfer transfer, out uint[] projectiles)
    {
        using (Program.GameLogicOrNull?.ProjectileMutation(this))
        {
            projectiles = [];
            if (Closed || PendingTransfer is not null || LeavingArea) return false;
            PendingTransfer = transfer;
            RampageTransferAt = ProjectileRuntimeState is { Active: true } runtime
                ? runtime.NowTicks / 120000d : Clock.Elapsed.TotalSeconds;
            ResourceEffectsState?.Freeze(RampageTransferAt.Value);
            LeavingArea = true;
            Instance.RemovePlayer(Id);
            PlayerSkillsCache = null;
            RetireOwnedCombat();
            InvalidateIncomingHits(clear: false);
            ProjectileEpoch++;
            projectiles = [.. ProjectileObjects];
            ProjectileRuntimeState?.Invalidate();
            ProjectileObjects.Clear();
            return true;
        }
    }

    public void ResumeAfterFailedTransfer()
    {
        ResourceEffectsState?.Resume(Clock.Elapsed.TotalSeconds);
        LeavingArea = false;
    }

    // Successful transfers retire all source-area workers independently of any particular carried mechanic.
    public void Retire() { RageRetired = true; Connection.WorldOutput.Close(); }

    /// <summary>The <see cref="Clock"/> time of a Stopwatch timestamp in the past (a socket read); now when 0 or paused.</summary>
    public double ClockAt(long timestamp)
    {
        double now = Clock.Elapsed.TotalSeconds;
        double at = timestamp == 0 || Paused ? now : Math.Max(0, now - System.Diagnostics.Stopwatch.GetElapsedTime(timestamp).TotalSeconds);
        // A buffered input cannot rewrite motion in history already consumed by projectile collision.
        return Math.Max(at, ProjectileRuntimeState?.NowTicks / 120000d ?? 0);
    }
    // The server's copy of the client-predicted walk (reported in each 0x1ab): the client's own pathfinder and stepping.
    public PlayerWalk Walk => _walk ??= WithMarks(NewPlayerWalk());
    PlayerWalk WithMarks(PlayerWalk walk) { walk.Changed = () => MarkStatsDirty(StatInput.Walk); return walk; }
    PlayerWalk? _walk;

    /// <summary>Switches to a layout with a walk map (before the player exists).</summary>
    public void Use(AreaLayout better)
    {
        Layout = better;
        _walk = WithMarks(NewPlayerWalk());
        ApplyMovementSpeed();
    }

    // The blocking objects the client's pathfinder walks around (Positioned blocking = true), with their Positioned size
    // (object_size − 1): NPCs (NPC.ot 5) and the stashes (Stash.ot, GuildStash.ot 3) by object id, and the living
    // hostile monsters where the server's estimate has them now (MonsterVarieties ObjectSize − 1).
    public const int NpcBlockerSize = 4, StashBlockerSize = 2, MapDeviceBlockerSize = 3;
    readonly Dictionary<uint, ClientPathfinder.Blocker> _blockers = [];
    volatile IReadOnlyList<ClientPathfinder.Blocker> _blockerList = [];
    // Cached player blocker snapshot (Blockers): reused while stationary, monster and peer bodies stay on the same cells.
    // Never mutated after publication so concurrent readers stay race-free.
    readonly Lock _playerBlockersGate = new();
    IReadOnlyList<ClientPathfinder.Blocker>? keptPlayerBlockers;
    List<ClientPathfinder.Blocker>? sparePlayerBlockers;
    public IReadOnlyList<ClientPathfinder.Blocker> Blockers
    {
        get
        {
            if (Instance?.World.AreaBodies is { } area) return area(Id);
            var fixedOnes = _blockerList;
            if (IsPhasing) return fixedOnes;   // phasing (Quartz Flask, Rampage threshold): monsters do not block
            double now = Clock.Elapsed.TotalSeconds;
            lock (_playerBlockersGate)
            {
                var list = sparePlayerBlockers ?? new List<ClientPathfinder.Blocker>();
                sparePlayerBlockers = null;
                list.Clear();
                for (int i = 0; i < fixedOnes.Count; i++) list.Add(fixedOnes[i]);
                if (Area.DormantMonsters.Count == 0)
                {
                    foreach (var (id, m) in Area.Monsters)
                        if (MonsterBody(id, m, now) is { } body) list.Add(body);
                }
                else
                {
                    foreach (var (id, m) in CollisionMonsters)
                        if (MonsterBody(id, m, now) is { } body) list.Add(body);
                }
                // Another player stands where its own walk has it.
                var peers = Instance?.PlayersExcept(Id);
                if (peers is { Length: > 0 })
                    foreach (var peer in peers)
                        if (peer.PlayerLife > 0)
                        {
                            var at = peer.Walk.Position(now);
                            list.Add(new(at.X, at.Y, LivingCollision.PlayerSize));
                        }
                if (list.Count == fixedOnes.Count)
                {
                    keptPlayerBlockers = null;
                    sparePlayerBlockers = list;
                    return fixedOnes;
                }
                if (keptPlayerBlockers is { } kept && SameBlockers(kept, list))
                {
                    sparePlayerBlockers = list;
                    return kept;
                }
                var built = new GameServer.Core.World.BlockerSet(list);
                keptPlayerBlockers = built;
                sparePlayerBlockers = list;
                return built;
            }
        }
    }

    static bool SameBlockers(IReadOnlyList<ClientPathfinder.Blocker> a, List<ClientPathfinder.Blocker> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < b.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    public IReadOnlyList<ClientPathfinder.Blocker> StationaryBlockers => _blockerList;

    public (int X, int Y)? StationaryPosition(uint id)
    {
        lock (_blockers) return _blockers.TryGetValue(id, out var b) ? (b.X, b.Y) : null;
    }

    public int StationaryInteractionSize(uint id)
    {
        lock (_blockers) return _blockers.TryGetValue(id, out var b)
            ? Math.Max(b.Size, (int)Math.Ceiling(b.CollisionRadius ?? 0)) : 0;
    }

    public void SetBlocker(uint id, int x, int y, int size, double? collisionRadius = null)
    {
        lock (_blockers) { _blockers[id] = new(x, y, size) { CollisionRadius = collisionRadius }; _blockerList = new GameServer.Core.World.BlockerSet(_blockers.Values); }
        BlockersChanged?.Invoke();
    }

    /// <summary>A stationary blocker was added or moved (the player may now stand in it).</summary>
    public Action? BlockersChanged;

    public void RemoveBlocker(uint id)
    {
        lock (_blockers) if (_blockers.Remove(id)) _blockerList = new GameServer.Core.World.BlockerSet(_blockers.Values);
    }

    /// <summary>Applies the passive movement speed to the server's walk estimate; /set_speed is a dev override.</summary>
    public void ApplyMovementSpeed() =>
        ApplyMovementSpeedAt(Clock.Elapsed.TotalSeconds);

    /// <summary>Replaces an area transition of this connection's layout (dev changes; the walk estimate keeps going).</summary>
    public void ReplaceAreaTransition(uint id, AreaTransitionObject transition) =>
        Layout = Layout with { AreaTransitions = [.. Layout.AreaTransitions.Select(e => e.Id == id ? (e.Id, transition) : e)] };

    // The quest walls (Barrier) are read on every path, so a wall that opens or closes counts from the next order on.
    private PlayerWalk NewPlayerWalk()
    {
        var l = Layout;
        Func<int, int, bool>? walkable = l.Walkable is { } w ? (cx, cy) => w(cx, cy) && (Barrier is not { Count: > 0 } b || !b.Contains((cx, cy))) : null;
        return new(l.SpawnX, l.SpawnY, Program.WalkSpeed, walkable, l.GridW > 0 ? l.GridW : 4096, l.GridH > 0 ? l.GridH : 4096,
            () => Blockers, sight: l.Sight) { FrameOf = LockstepFrameOf };
    }
    /// <summary>True when C→S 0x7d's tile hash differed from the server's generated <see cref="ServerLevels.Level.TileHash"/>.</summary>
    public bool LevelDesynced { get; set; }
    /// <summary>The tile and doodad hashes reported by the client in C→S 0x7d.</summary>
    public uint ClientTileHash { get; set; }
    public uint ClientDoodadHash { get; set; }
    /// <summary>The worn pets by object id (SpawnPets), replaced when a Pet slot changes; StepPetFollow walks them.</summary>
    public readonly Dictionary<uint, SpawnedPet> Pets = [];
    /// <summary>How map portals are turned around the vertical axis (degrees; Positioned rotation). Dev: /portal_rotation,
    /// which rebuilds the portals that already stand.</summary>
    public float PortalRotationDegrees;
    public float PortalRotationRadians => PortalRotationDegrees * MathF.PI / 180f;
    public long Ticks;
    public volatile PendingTransfer? PendingTransfer;
    public bool PendingCharacterSelectExit;
    public int ExitToLoginHandled;
    public int SkillBarRestoreGeneration;
    /// <summary>The 0x29d preview request (Hinekora's Lock): its key, the currency/target it names and the computed
    /// preview answered by 0x2a0. The next 0x71 on that target commits exactly this item.</summary>
    public (ushort Key, uint InventoryId, uint CurrencyId, uint TargetInventoryId, uint TargetId, uint Seed, Item? Preview)? ForesightRequest;
    public ChooserList? Chooser;
    public ChatCommands? Commands;
    // Server objects created after entering (monsters); the player is 0x1000 and area transitions start at 0x2000.
    public uint NextObjectId
    {
        get => Instance is { } i ? i.World.NextObjectId : ownNextObjectId;
        set { if (Instance is { } i) i.World.NextObjectId = value; else ownNextObjectId = value; }
    }
    uint ownNextObjectId = 0x3000;
    public uint TakeObjectId() => Instance is { } instance ? instance.World.TakeObjectId() : Interlocked.Increment(ref ownNextObjectId) - 1;
    // Every 0x1ab action on this connection gets a fresh sequence. The official server increments it per action (the
    // King's first actions are 0x8011, 0x8012, …); reusing 0xffff for several actors left the client's channel object
    // without a current action and crashed StartChannelling (0xc0000005 at 0x19e37bf, live 2026-10-31).
    int _nextActionSequence = 0x8000;
    public ushort NextActionSequence() => (ushort)Interlocked.Increment(ref _nextActionSequence);
    // The flags of a server order to a monster (0x1ab → action +0x122; ActionStarted adds 0x80 with a target id). GGG's
    // are 0: none of 56 175 monster orders in four lockstep recordings carries 0x400 or 0x8. On a monster the client reads
    // 0x400 only in the offer (0x141e937b0: a Move without it is refused while a non-Move action waits queued at +0x1c0)
    // and 0x8 nowhere (it is the client's own auto_equip bit on its requests, 0x14011ecb0). A predictive client keeps
    // 0x0408, the form it has always had.
    public ushort MonsterOrderFlags => Connection.LockstepClient ? (ushort)0 : (ushort)0x0408;
    // Room server objects a quest keeps in the area until its flag is set (The Twilight Strand's force wall), by id.
    public readonly Dictionary<uint, (string Flag, IReadOnlySet<(int X, int Y)> Cells)> QuestRoomObjects = [];
    // A Trial of Ascendancy's objects in this area (LabyrinthTrials), by id, and the cells its closed doors and risen
    // spike traps close, by id.
    public readonly Dictionary<uint, AreaPopulation.RoomObject> TrialObjects = [];
    public readonly Dictionary<uint, IReadOnlySet<(int X, int Y)>> DoorBarriers = [];
    // Cells server objects' Hull projectile shapes close to the server's projectiles, as the client stamps them into its
    // own projectile map (a trial's dart turrets). Changed and read in the area's frame.
    public readonly HashSet<(int X, int Y)> ProjectileWalls = [];
    // Cells quest walls and closed doors close to the player's walk estimate (QuestBarriers): the union of
    // QuestRoomObjects' and DoorBarriers' cells.
    public volatile IReadOnlySet<(int X, int Y)> Barrier = new HashSet<(int X, int Y)>();

    /// <summary>Recomputes <see cref="Barrier"/> from the standing quest walls and closed doors; the player's walk estimate
    /// reads it on its next order (call in the area's frame).</summary>
    public void UpdateBarrier() =>
        Barrier = new HashSet<(int X, int Y)>(QuestRoomObjects.Values.SelectMany(o => o.Cells).Concat(DoorBarriers.Values.SelectMany(c => c)));
    // The player's attacks: the next time one may start (Clock seconds). The client repeats 0x24 while the button is held.
    public double NextAttackAt;
    // Counts the player's actions; an attack's hit lands only if no other action started since (moving cancels it).
    public int ActionCount;
    // The newest Move click that came while the player could not act (Game.ReleaseHeldMove).
    public InstanceProtocol.UseSkillAtLocation? HeldMove;
    public readonly PlayerHitRecovery HitRecovery = new();
    public readonly WardState Ward = new();
    public readonly AntiquarianCombatState Antiquarian = new();
    public double WitchbaneImmunityUntil;
    public readonly TimedLifeRecovery EnduringCryRecovery = new();
    /// <summary>Petrified Blood's buff, reservation, deferred hit loss and life-cost rules (Combat/PetrifiedBlood.cs).</summary>
    public readonly PetrifiedBloodState PetrifiedBlood = new();
    /// <summary>Lifetap Support's life-paid costs, spent-Life counter and buff (Combat/Lifetap.cs).</summary>
    public readonly LifetapState Lifetap = new();
    /// <summary>Arcane Surge's buff and Inspiration's charges (Skill/SupportGemBuffs.cs).</summary>
    public readonly SupportGemState SupportGems = new();
    /// <summary>The invisible leech recovery buffs shown on the resource orbs (Combat/Leech.cs).</summary>
    public readonly LeechDisplayState LeechDisplay = new();
    // Life Recoup of the hits the player took (Game.RecoupHitLocked), paid by the resource recovery loop.
    public readonly RecoupRecovery LifeRecoup = new();
    public double SentEnduringCryUntil = double.NaN;
    public ushort EnduringCryBuff;
    public bool AppliedAdrenaline;
    public (uint Maximum, bool Broken)? SentWard;
    public double SentAdrenalineUntil = double.NaN;
    public double SentStunRecoveryUntil = double.NaN;
    public ushort WardBuff, AdrenalineBuff;
    /// <summary>The passive self buffs published on the player: BuffDefinitions row → buff instance (PassiveBuffAuras).</summary>
    public readonly Dictionary<ushort, ushort> PassiveBuffs = [];
    // Transient Rage belongs to this connection until its destination ticket takes ownership; owned by the area's frame.
    public readonly RageState Rage = new();
    public bool RageRetired;
    public ushort RageBuff;
    public RageState.Value? SentRage;
    // Reap's Blood Charges held (BloodCharges), the buff instance sent for them and the count it carried; owned by the area's frame.
    public int BloodCharges;
    public ushort BloodChargeBuff;
    public int SentBloodCharges;
    // Shared Vaal row budgets and native presentation belong to this connection; owned by the area's frame.
    public readonly VaalSoulState VaalSouls = new();
    public bool VaalSoulsRetired;
    public readonly Dictionary<uint, VaalSoulBudget> SentVaalSouls = [];
    // Running auras by skill id, with their buff instances and reservation. Owned by the area's frame.
    public readonly Dictionary<ushort, AuraCatalog.Aura> Auras = [];
    public bool AuraStatsDirty { get => auraStatsDirty; set { auraStatsDirty = value; if (value) AuraStatsDirtied?.Invoke(); } }
    bool auraStatsDirty;
    /// <summary>The auras' stats need publishing again (the aura presentation follows).</summary>
    public Action? AuraStatsDirtied;
    /// <summary>The stance the next Flesh and Stone activation starts in ("Blood" or "Sand"): the skill alternates on each
    /// use, and the stance flips when the aura starts (Game.ToggleAura), not when it ends.</summary>
    public string Stance = "Blood";
    // When each skill with a cooldown (GrantedEffects row) may be used again, in Clock seconds. Owned by the area's frame.
    public readonly Dictionary<int, double> SkillReadyAt = [];
    // Ordinary stored uses retain recharge debt and base-time progress through recovery-rate/capacity changes.
    public readonly Dictionary<int, SingleUseCooldownState> SingleUseCooldowns = [];
    // The skill ids the client has on this connection's player (SendPlayer, SendSkills): the 0x40 skill list only adds,
    // so the ones that leave are removed by id.
    public HashSet<ushort> ClientSkillIds = [];
    // The PlayerSkills build reused for a few frames: Headhunter, Rage, Fortification and charge stat changes call it
    // many times a second, and rebuilding the whole list each time inside the send gate stalled input handlers
    // (2026-11-05). A short lifetime keeps the client's skill records close while coalescing the builds.
    public IReadOnlyList<GrantedSkill>? PlayerSkillsCache;
    public double PlayerSkillsAt;
    // The character's level and the pools that follow from it and the class (CharacterStats).
    public EnergyShield EnergyShield => energyShield ??= new() { ConditionsChanged = () => MarkStatsDirty(StatInput.EnergyShield),
        Changed = () => Frames.Area?.Life.Changed(this) };
    EnergyShield? energyShield;
    CharacterStats.Pools pools = new(1, 50, 40, 2, 0);
    public CharacterStats.Pools Pools
    {
        get { StatGraph.ReadInput((int)StatInput.Pools); return pools; }
        set
        {
            bool changed = !Equals(pools, value);
            pools = value;
            EnergyShield.SetMaximum(value.EnergyShield);
            if (changed) MarkStatsDirty(StatInput.Pools);
        }
    }
    // The player stat keys the client holds (PlayerStatsWithCleared). Owned by the area's frame.
    // Dev-only forced lucky block, on top of the stats (Svalinn's "Chance to Block is Lucky").
    public bool ForceAttackBlockLucky, ForceSpellBlockLucky;
    // When the player last hit an enemy (attack or spell hit; Clock seconds), for "if you've hit an enemy recently".
    // Owned by the area's frame.
    public double LastEnemyHitAt { get => lastEnemyHitAt; set { lastEnemyHitAt = value; MarkStatsDirty(StatInput.EnemyHit); } }
    double lastEnemyHitAt = double.NegativeInfinity;
    // PoE evasion entropy (0..99): every attack adds the attacker's hit chance, and 100+ means a hit. Shared by all
    // attackers and reset after 3.33 s without an attack. Owned by the area's frame.
    public double EvasionEntropy = -1;
    public double LastEvasionAt = double.NegativeInfinity;
    // Dev-only forced lucky/unlucky Spell Suppression rolls, on top of the passive stat.
    public bool ForceSpellSuppressionLucky, ForceSpellSuppressionUnlucky;
    // /god (GodStats) and the running Righteous Fire, if any; buff instance ids (0x1c1) given out on this connection.
    public bool God;
    // /god immune: god mode that also takes no damage (hits and ailment ticks); only with God.
    public bool Immune;
    public readonly GracePeriod EntryProtection = new();
    public RighteousFireState? RighteousFire;
    public BladefallTrarthusState? BladefallTrarthus;
    public readonly List<BladefallTrarthusState> LingeringBladeSources = [];
    /// <summary>The hands' part of the player model last sent (Program.HandsKey): null until the area's first model.</summary>
    public string? ModelHands;
    /// <summary>The flasks recovering life and mana (C→S 0x70), applied by the player tick.</summary>
    public Flasks.Effects FlaskEffects => flaskEffects ??= new() { AnyChanged = () => MarkStatsDirty(StatInput.FlaskEffects) };
    Flasks.Effects? flaskEffects;
    /// <summary>The utility flasks whose effect runs (UtilityFlasks); their stats count in the pools until they end.</summary>
    public UtilityFlasks.Effects UtilityFlaskEffects { get; } = new();
    /// <summary>The tinctures active/lingering on the flask belt, with shared Mana Burn / Weeping Wounds and cooldown.</summary>
    public Tinctures.Effects TinctureEffects { get; } = new();
    /// <summary>Life, mana and hybrid flasks' "during effect" mods while their recovery runs.</summary>
    public UtilityFlasks.RecoveryDuringEffects RecoveryFlaskStats { get; } = new();
    /// <summary>Ignited corpses consumed since the flask-charge loop last paid them (Cinderswallow Urn's recharge).</summary>
    public int IgnitedCorpsesConsumed;
    /// <summary>What Headhunter took from slain rares (StolenMods).</summary>
    public StolenMods StolenMods { get; } = new();
    /// <summary>The Headhunter buff on the client, 0 for none (the area's frame).</summary>
    public ushort StolenModsBuff, InspiredLearningBuff;
    /// <summary>The flask buffs Mageblood has on the client, by flask item id (Program.StepMagebloodBuffs; Monsters lock).</summary>
    public Dictionary<uint, (byte Slot, ushort Buff, ushort? Extra, ushort Row, ushort ExtraRow)> MagebloodBuffs { get; } = [];
    /// <summary>The flasks' partial charges and the crit charge cooldown (FlaskCharges).</summary>
    public FlaskCharges.Carry FlaskChargeCarry { get; } = new();
    /// <summary>One change of the flask bar and its S→C 0xbf at a time (Program.ChangeFlaskBar), so the revisions go out in
    /// order with the latest charges.</summary>
    public SemaphoreSlim FlaskBarGate { get; } = new(1, 1);
    /// <summary>The smoke clouds and consecrated ground of flasks (FlaskGrounds).</summary>
    public FlaskGrounds.State FlaskGrounds { get; } = new();
    /// <summary>Each Strike skill's last use, for Ancestral Echo (StrikeTargets.Memory). Owned by the area's frame.</summary>
    public StrikeTargets.Memory StrikeLocations { get; } = new();
    /// <summary>Per-skill attack count (0..4) and active BuffDefinitions 2353 instance for Molten Strike of the Zenith.
    /// Owned by the area's frame.</summary>
    public readonly Dictionary<ushort, int> MoltenStrikeAttackCounts = [];
    public readonly Dictionary<ushort, ushort> MoltenStrikeFifthBuffs = [];
    /// <summary>The Spirit chieftain skills' shared cooldown and Leadership buff (SpiritChieftains). Guarded by the Monsters
    /// lock.</summary>
    public SpiritChieftains SpiritChieftains { get; } = new();
    /// <summary>The player's summoned totems (Totems.Rules.cs), kept apart from <see cref="Monsters"/>. Guarded by the
    /// Monsters lock.</summary>
    public TotemState Totems { get; } = new();
    /// <summary>The player's enemy auras and taunt increase, from the stats last sent (Game.EnemyDebuffsTick).</summary>
    public volatile EnemyDebuffs EnemyDebuffs = EnemyDebuffs.None;
    /// <summary>The monsters a passive's blind aura blinds (PassiveBuffAuras.BlindState, Game.EnemyDebuffsTick).</summary>
    public PassiveBuffAuras.BlindState BlindAura { get; } = new();
    /// <summary>Whether the player phases (a Quartz Flask runs): the walk estimate walks through monsters (Blockers).</summary>
    public volatile bool Phasing;
    /// <summary>The next buff instance (S→C 0x1c1): the area's, shared by every entity in it (AreaBuffs.Next).</summary>
    public ushort NextBuff { get => Area.Buffs.Next; set => Area.Buffs.Next = value; }
    /// <summary>The Elemental Overload indicator buff's instance (0: none sent yet). One instance, bound to the skill
    /// that last crit; the client draws the diamond under that skill's icon and expires the buff after its window.</summary>
    public ushort ElementalOverloadBuff;

    /// <summary>When the Elemental Overload indicator's window ends (last crit + 8 s); the player tick removes the buff
    /// then, as the client does not expire this invisible buff itself.</summary>
    public double ElementalOverloadBuffUntil;
    /// <summary>The lowest of the buff instances NextBuff never hands out: the rules keep these for buffs that always
    /// use the same instance on their object (MonsterElementalAilments.Instance is the top three).</summary>
    public const ushort FixedBuffInstances = ushort.MaxValue - 7;
    public uint PlayerMaxLife => Pools.Life;
    /// <summary>The shared Life reservation owner's latest usable cap. Full maximum Life is unchanged.</summary>
    public int PlayerLifeCap { get; set; } = int.MaxValue;
    /// <summary>The last qualified item percentage. The qualification stage supplies this neutral value.</summary>
    public int QualifiedItemLifeReservationPercent { get; set; }
    public uint PlayerReservedLife
    {
        get
        {
            return (uint)PlayerLifeReservation.Reserved;
        }
    }
    public uint PlayerUnreservedLife
    {
        get
        {
            return (uint)PlayerLifeReservation.Unreserved;
        }
    }
    LifeReservation.Value PlayerLifeReservation
    {
        get
        {
            int itemPercent = QualifiedItemLifeReservationPercent;
            int additionalSkillReserved = (int)Math.Clamp(
                (long)global::PetrifiedBlood.RequiredLife(Pools, PetrifiedBlood)
                + PlacedSkills.Reserved(Pools, Placed.All.Values, true),
                0, int.MaxValue);
            return LifeReservation.Of(Pools, itemPercent, Auras.Values, additionalSkillReserved);
        }
    }
    // The player's life as the server keeps it (PlayerObject's Life, full on creation); 0 = dead.
    public bool AuraDeathHandled;
    uint playerLife;
    public uint PlayerLife
    {
        get => playerLife;
        set
        {
            // Resource loss belongs to the actual death transition, never a delayed death notification.
            if (playerLife > 0 && value == 0)
            {
                VaalSouls.ClearBalance(); VaalPreventionUntil = 0; VaalUseState = null;
                MoltenStrikeAttackCounts.Clear(); MoltenStrikeFifthBuffs.Clear();
                Rage.Clear(); Rampage.Clear(); RetireOwnedCombat(); RetireResourceEffects();
            }
            bool changed = playerLife != value;
            playerLife = value;
            if (changed) { MarkStatsDirty(StatInput.Life); Frames.Area?.Life.Changed(this); }
        }
    }
    // Defaults to the original shared stream; an isolated connection may supply deterministic critical draws.
    public Random CriticalRandom { get; init; } = Random.Shared;
    public Random AttackAccuracyRandom { get; set; } = Random.Shared;
    // The player's mana as the server keeps it: spent by gem skills (mana_cost), regenerated by RunPlayerTick.
    public uint PlayerMana { get => playerMana; set { if (playerMana == value) return; playerMana = value; MarkStatsDirty(StatInput.Mana); Frames.Area?.Life.Changed(this); } }
    uint playerMana;
    // When the server last sent the pool back after an attack-move, whose use the client pays for but the server only walks.
    public double LastManaResyncAt = double.NegativeInfinity;
    // The previous recovery tick's states of the once-per-transition Loyalty triggers (Energy Shield Recharge started,
    // Low Life reached): Game.LoyaltyResourceStateLocked.
    public bool LoyaltyRechargeSeen, LoyaltyLowLifeSeen;
    /// <summary>The degeneration on the player from what is around them (a trial's trap, an encounter's pool, a ground or a
    /// debuff), points a second, energy shield first: StartResourceRecovery deals the total, and while any runs the client is
    /// sent no recovery rates to predict a rise with. In the area's frame.</summary>
    public PlayerDegens Degens { get; } = new();
    /// <summary>The total of <see cref="Degens"/>.</summary>
    public double TrapDegenPerSecond => Degens.Total;
    // (MonsterVarieties row, level) pairs already registered with 0x20 on this connection.
    public HashSet<(int Row, byte Level)> MonsterTypes => Area.MonsterTypes;
    // The open sell window (Sell Items): what the player offered and what the NPC offers back.
    public NpcSale? Sale;
    // The shop tab opened last (Purchase Items), with what is left in it.
    public OpenShop? Shop;
    // The town's Stash object (0 = none here), and the stash tabs whose inventory this connection has sent (0xc0 + 0xc8).
    public uint StashObject;
    /// <summary>The guild stash: its object in the hideout, its tabs this connection has the contents of, and whether its
    /// panel is the one open (then the stash messages act on it; StashOf).</summary>
    public uint GuildStashObject;
    public (int X, int Y) GuildStashAt;
    public uint CraftingBenchObject;
    public bool BenchOpen;
    public bool BenchBalanceSent;
    public uint BenchBalanceRevision;
    public IReadOnlyList<InventoryItem> BenchBalanceItems = [];
    public HideoutScene? Furniture;
    public readonly HideoutEditing HideoutEditor = new();
    public (int X, int Y) StashAt;
    public void MoveWaypoint(WaypointObject waypoint) => Layout = Layout with { Waypoint = waypoint };
    public (int X, int Y) CraftingBenchAt;
    public bool StashOpen;
    public bool GuildStashOpen;
    public int InteractionClosing;
    // Whether this connection opened the stash already (only the first open selects the account's last tab), and while
    // that first open runs (until StashForcedUntil, Clock seconds) the tab it selects: the panel's own request for the
    // tab it was on is then not the player's choice.
    public bool StashOpened;
    public int StashForcedTab = -1;
    public double StashForcedUntil;
    // The NPC whose dialogue window is open (0x84 sent, 0x85 not yet), 0 = none.
    HashSet<int>? automaticNpcTalkRows;
    public HashSet<int> AutomaticNpcTalkRows =>
        System.Threading.LazyInitializer.EnsureInitialized(ref automaticNpcTalkRows);
    public volatile uint TalkingTo;
    // Whether the open dialogue shows an NPCTalk row's talk (0x84 with its index) rather than the option list.
    public volatile bool InNpcTalk;
    public uint TutorialSceneId, TutorialChestId;
    public int TutorialChestOpening;   // 1 once this connection opened the support gem chest (OpenTutorialChestCore)
    // a1q4 in The Mud Flats (GlyphQuest): the captured layout, the nests by object id (guarded by itself), the wall and the waterfall.
    public GlyphQuest.Layout? Glyphs;
    public readonly Dictionary<uint, GlyphQuest.Nest> GlyphNests = [];
    public uint GlyphWallId, GlyphWaterId;
    // When the quest items' flags are synced next (Game.SyncQuestItems, about once a second from the player tick).
    public double QuestItemSyncAt;
    // The quest engine's objects in this area (Modules/Core/Game/World/Quest/QuestEngine.cs): the quest chests by object id (guarded by itself),
    // the room objects the sight rules watch, and the client's own blockers the server mirrors (the area's frame).
    public readonly Dictionary<uint, (Quests.ChestRule Rule, GlyphQuest.Placement At)> QuestChests = [];
    public List<(Quests.NearRule Rule, int X, int Y)> QuestNear = [];
    /// <summary>The instance's proximity spawns (Quests.NearSpawnRule) and whether each has spawned.</summary>
    public List<(Quests.NearSpawnRule Rule, int X, int Y, bool Spawned)> QuestNearSpawns = [];
    public List<(Quests.BlockerRule Rule, int X, int Y)> QuestBlockers = [];
    // The quest objects the engine created (Quests.ObjectRule: seals, levers, altars) and the room NPCs whose state follows a
    // flag (Quests.NpcRule.TransitionedBy), by object id (each guarded by itself).
    public readonly Dictionary<uint, (Quests.ObjectRule Rule, GlyphQuest.Placement At, QuestObjectTypes.Info Info)> QuestObjects = [];
    public TutorialObjects.Placement? TutorialChest;
    public AreaPopulation.RoomObject? TutorialZombiePlacement;
    public bool TutorialSceneRunning;
    public bool TutorialGettingUp;
    public TaskCompletionSource? TutorialDialogueSkipped;
    public string? NpcFinishFlag;
    public int? NpcFinishLeagueFlag;
    // The dialogue row whose script acts when its talk ends (DoNPCChat, FinishNPCChat, SendOpenScreen, AreaTransitionTo:
    // Game.EndTalkScript), or null.
    public NpcCatalog.Talk? NpcTalkScript;
    // The NPCs a quest decides in this instance (QuestNpc, Game's QuestNpcs.cs), created or not.
    public readonly List<QuestNpc> QuestNpcs = [];
    // Where the later forms the area leaves out would stand (AreaPopulation.LaterFormPoints), by MonsterVarieties Id, for a
    // quest that forms one (Quests.FormsRule); in the area's frame' lock.
    public Dictionary<string, (int X, int Y)> LaterFormPoints => Instance?.World.LaterFormPoints ?? ownLaterFormPoints;
    readonly Dictionary<string, (int X, int Y)> ownLaterFormPoints = new(StringComparer.Ordinal);
    // The area transitions created on this instance's client (ids), and those a script showed before their quest flag
    // (Game.TransitionShown).
    public HashSet<uint> ShownTransitions = [];
    public HashSet<uint> ScriptShownTransitions = [];
    public LabyrinthDay.Entry? LabyrinthEntry;
    public int? NpcRewardOffer;
    public Dictionary<uint, Item> NpcRewardItems = [];
    // NPCs created on this connection by object id (town NPCs, /npc).
    public readonly Dictionary<uint, SpawnedNpc> Npcs = [];

    /// <summary>The hideout doodads created in this area (the map device), by object id.</summary>
    public readonly Dictionary<uint, HideoutCatalog.Doodad> HideoutDoodads = [];

    /// <summary>The open map's portals created in this area, by object id: where each stands.</summary>
    public readonly Dictionary<uint, (int X, int Y, int Index)> MapPortals = [];
    public MapDevice.OpenMap? MapPortalRun;
    public string? InstanceOwner { get; init; }

    /// <summary>Counts the map portal sequences started (SpawnMapPortals); a newer one stops the older.</summary>
    public int MapPortalGeneration;

    /// <summary>In a map's area: the portal back to the hideout (object id and cell), or null.</summary>
    public (uint Id, int X, int Y)? ReturnPortal;
    /// <summary>The portal back to the character's open town portal, in its town or hideout (SpawnBackPortal).</summary>
    public (uint Id, int X, int Y)? BackPortal;
    /// <summary>The gem skill being channelled (Cyclone: C→S 0x197 until 0x199), 0 for none, and when it next hits.
    /// Leaving a moving channel (<see cref="ChannelMovement"/>) gives the walk its usual speed back.</summary>
    public ushort ChannelSkill
    {
        get => _channelSkill;
        set
        {
            ushort was = _channelSkill;
            _channelSkill = value;
            if (was == value || ChannelMovement.Skill == 0 || was != ChannelMovement.Skill) return;
            ChannelMovement = default;
            ApplyMovementSpeed();
        }
    }
    ushort _channelSkill;
    /// <summary>A channel that walks the player toward its aimed point (Cyclone) and its own final movement modifier
    /// (cyclone_movement_speed_+%_final): the walk goes at it while <see cref="ChannelSkill"/> is that skill.</summary>
    public (ushort Skill, int Percent) ChannelMovement;
    public int ChannelGeneration;
    /// <summary>The channelled skill's action sequence, so its 0x1af hit result names the action it belongs to.</summary>
    public ushort ChannelSkillSequence;
    public double NextChannelHitAt;
    /// <summary>When the channel last heard from the client (its start, or a C→S 0x197 update; Clock seconds).</summary>
    public double LastChannelUpdateAt;
    /// <summary>Whether the next lockstep frame owes a moving channel a cursor update.</summary>
    public bool LockstepChannelAimPending;
    /// <summary>The cursor and frame time last applied to the current moving channel.</summary>
    public ((int X, int Y) Aim, double At)? LastLockstepChannelAim;
    /// <summary>The instance this connection is in (its number), for a portal back to it.</summary>
    public ulong InstanceNumber { get; init; }
    /// <summary>Arrive at this cell instead of the spawn (back through a town portal).</summary>
    public (int X, int Y)? ArriveAt;
    /// <summary>Where ReturnPortal leads: 0 the hideout (a map's own portal, or a Portal Scroll's in a map), else a town's
    /// WorldAreas HASH16 (a Portal Scroll's anywhere else).</summary>
    public ushort ReturnPortalTo;

    /// <summary>The 0x8f entries' model byte (/map_portal sets it for experiments).</summary>
    public byte MapPortalStyle;
    /// <summary>The area the player is in: its instance's (every connection in it sees and hits the same monsters), else
    /// the player's own (a test's, a tool's). The area owns the monsters, not the player.</summary>
    public GameArea Area => Frames.World(this);
    // Last byte delivered to this client's monsters-remaining overlay, -1 before its first count. In the area's frame.
    public int SentMonstersRemaining = -1;
    // A shared dormant body enters this client's collision view with its admitted create, not merely with the
    // instance's roster entry. Connections without an instance retain their local body contract.
    // In ascending id. Both rosters are sorted by id, so they are merged, not sorted again.
    IEnumerable<KeyValuePair<uint, SpawnedMonster>> CollisionMonsters => Merged(Area.Monsters, Area.DormantMonsters);
    /// <summary>The monsters that stand in paths, the dormant ones too (the area's movement reads them). In the area's frame.</summary>
    public IEnumerable<KeyValuePair<uint, SpawnedMonster>> CollisionMonstersView => CollisionMonsters;

    IEnumerable<KeyValuePair<uint, SpawnedMonster>> Merged(SortedDictionary<uint, SpawnedMonster> awake, SortedDictionary<uint, SpawnedMonster> dormant)
    {
        using var others = dormant.GetEnumerator();
        bool NextBody()
        {
            return others.MoveNext();
        }
        bool more = NextBody();
        foreach (var pair in awake)
        {
            for (; more && others.Current.Key < pair.Key; more = NextBody()) yield return others.Current;
            yield return pair;
        }
        for (; more; more = NextBody()) yield return others.Current;
    }
    // Items on the ground by object id (dropped with C→S 0x29).
    /// <summary>The area instance's state shared across connections: its ground items.</summary>
    public InstanceState Instance { get; init; } = null!;
    /// <summary>This connection placed or restored the instance's monsters, so it saves them when it closes.</summary>
    public bool MonstersPlaced;
    public volatile bool AiEnabled = true;
    volatile bool closed;
    public bool Closed
    {
        get => closed;
        set { closed = value; if (value) Connection.WorldOutput.Close(); }
    }
    // A lockstep client ends an action on a frame, up to two after the server's clock, and loses an order that comes first.
    public double LockstepActionTail => Connection.LockstepClient ? .066 : 0;
    (double Start, double End)? LockstepFrameOf(double at) =>
        Connection.LockstepClient && Connection.LockstepFrame is { } frame ? (Math.Min(at, frame.Start), frame.End) : null;
    public readonly GameServer.Core.World.LockstepHold LockstepHold = new();
    public ushort LockstepSequence;
    // The object use (C→S 0x18f) the next frame starts.
    public InstanceTransfer.UseSkillOnObject? LockstepUse;
    public uint? LockstepInteractionTarget;
    public readonly GameServer.Core.World.LockstepSkillHold LockstepSkill = new();
    // Until when the skill started last keeps the player (Clock seconds).
    public double LockstepBusyUntil;
    public GameServer.Core.World.LockstepCastWindow? LockstepCastWindow;
    // A skill on a monster whose walk-up or swing is still to come on a lockstep client, null for none
    // (Game.LockstepAttackStart): the next use of a held skill waits for it, and the step pass runs its walk-up.
    public LockstepAttack? LockstepAttack;
    public bool ArriveAtWaypoint;

    /// <summary>A walk estimate for a server object on this area's walk map.</summary>
    public WalkEstimator NewWalk(int x, int y, double speed) =>
        new(x, y, Math.Max(1, speed), Layout.Walkable, Layout.GridW > 0 ? Layout.GridW : 4096, Layout.GridH > 0 ? Layout.GridH : 4096);

    /// <summary>The nearest cell to spawn on, clear of other bodies: a walk that starts on a body goes through bodies
    /// (PlayerWalk.WalksThrough), so monsters spawned on one cell would stay stacked wherever they went.</summary>
    public (int X, int Y) SpawnNear((int X, int Y) at)
    {
        const int Size = 2, Step = 3, Reach = 90;
        var near = Layout.Near(at);
        var bodies = Blockers;
        for (int r = 0; r <= Reach; r += Step)
            for (int a = 0, n = Math.Max(1, r * 2); a < n; a++)
            {
                int x = near.X + (int)Math.Round(r * Math.Cos(a * Math.Tau / n)), y = near.Y + (int)Math.Round(r * Math.Sin(a * Math.Tau / n));
                if (Layout.WalkableFor(x, y, Size) && !ClientPathfinder.ObjectBlocks(bodies, x, y, Size + 1, Size + 4)) return (x, y);
            }
        return near;
    }
}

/// <summary>A worn pet the server walks after the player (StepPetFollow): its walk estimate, which side of the player it
/// keeps (+1 or −1) and where it was last sent.</summary>
public sealed class SpawnedPet(WalkEstimator walk, int side)
{
    public WalkEstimator Walk { get; } = walk;
    public int Side { get; } = side;
    public (int X, int Y)? Target { get; set; }
}
