using System.Diagnostics;
using GameServer.Core.Protocol.Instance;
using WalkEstimator = GameServer.Core.World.WalkEstimator;
using PlayerWalk = GameServer.Core.World.PlayerWalk;
using ClientPathfinder = GameServer.Core.World.ClientPathfinder;
using DamageType = GameServer.Core.World.DamageType;
using DamagePacket = GameServer.Core.World.DamagePacket;

/// <param name="ArriveAt">Arrive at this cell (back through a town portal).</param>
sealed record PendingTransfer(ushort Area, ulong Instance, bool New, bool AtSpawn = false, bool AtWaypoint = false, (int X, int Y)? ArriveAt = null)
{
    /// <summary>Called with Auth's answer to the transfer (a map portal records the map's new instance).</summary>
    public Action<ControlResponse>? Done { get; init; }

    /// <summary>With <see cref="New"/>: the new instance's seed (0 = Auth's choice; the capture tour sets it).</summary>
    public uint Seed { get; init; }
}

// A monster this connection spawned: label for /mobs, the server's estimate of its walk, and its AI state.
// A monster's attack: its skill id, attack time, reach (cells, centre to centre) and damage per hit.
/// <summary>How a monster's projectile skill releases its shot, from the rig and stat set the client itself plays
/// (Program.MonsterReleaseFor): the clip, its contact keyframe as a fraction of the attack, the keyframe's position when
/// the skill spawns the shot there (<c>projectile_uses_contact_position</c>), and whether the monster turns its back for
/// it (<c>action_should_face_away_from_target</c>: the Sand Spitter spits from its rear).</summary>
sealed record MonsterRelease(string Clip, double Fraction, ContactPoint? Point, bool FacesAway);

sealed record MonsterAttack(ushort SkillId, double Seconds, double Range, DamagePacket Damage)
{
    public bool Ranged { get; init; }
    /// <summary>When and from where a projectile skill releases its shot; null: half the attack time, 1.5 cells ahead at
    /// the default height (no rig data for the variety).</summary>
    public MonsterRelease? Release { get; init; }
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

    /// <summary>The attack's final stats (the client derives none, 11-skills.md §4): the swing time, reach and damage.
    /// These also go on the monster's Stats component.</summary>
    public IReadOnlyList<(uint, int)> Stats()
    {
        int ms = (int)Math.Round(Seconds * 1000);
        var physical = Damage[DamageType.Physical];
        var chaos = Damage[DamageType.Chaos];
        int min = (int)Math.Floor(physical.Min), max = (int)Math.Floor(physical.Max);
        int chaosMin = (int)Math.Floor(chaos.Min), chaosMax = (int)Math.Floor(chaos.Max);
        return
        [
            (StatKeys.MainHandMinPhysical, min), (StatKeys.MainHandMaxPhysical, max),
            (StatKeys.MainHandMinChaos, chaosMin), (StatKeys.MainHandMaxChaos, chaosMax),
            (StatKeys.MainHandMinTotal, (int)Math.Floor(Damage.MinTotal)),
            (StatKeys.MainHandMaxTotal, (int)Math.Floor(Damage.MaxTotal)),
            (StatKeys.MainHandAttackDurationMs, ms), (StatKeys.MainHandTotalAttackDurationMs, ms),
            (StatKeys.HundredTimesAttacksPerSecond, (int)Math.Round(100 / Seconds)),
            (StatKeys.MainHandBaseMaxAttackDistance, (int)Range), (StatKeys.MainHandMaxAttackDistance, (int)Range),
        ];
    }

    /// <summary>An actor whose actions a controller plays (KingInMists.cs): every skill starts at once, none walks it up.</summary>
    public bool Scripted { get; init; }

    /// <summary>Plays where the actor stands. A plain melee attack instead walks the monster up (07 §5).</summary>
    public bool InPlace => Ranged || Spell || Scripted;

    /// <summary>The skill's stats. A melee attack keeps the client's pathing step: the action walks the monster up to its
    /// target and waits there until S→C 0x1ac starts the swing (07 §5). Anything else plays where it stands.</summary>
    public IReadOnlyList<(uint, int)> SkillStats() => InPlace ? InPlaceSkillStats() : Stats();

    /// <summary>An attack's <see cref="Stats"/> or a spell's cast time, plus skill_is_non_pathing: the action starts at once
    /// where the actor stands (the Melee action's begin, 0x141938830). Only on the skill: on the monster's Stats component
    /// it also stops its Move action from pathing (live 2026-10-17: the monsters stood still).</summary>
    public IReadOnlyList<(uint, int)> InPlaceSkillStats() => [.. Spell ? SpellStats() : Stats(), (StatKeys.SkillIsNonPathing, 1)];

    IReadOnlyList<(uint, int)> SpellStats()
    {
        int ms = Math.Max(1, (int)Math.Round(Seconds * 1000));
        return
        [
            (StatKeys.BaseSpellCastTimeMs, ms), (StatKeys.VirtualBaseSpellCastTimeMs, ms),
            (StatKeys.HundredTimesCastsPerSecond, (int)Math.Round(100_000.0 / ms)),
            (StatKeys.CastingSpell, 1),
        ];
    }
}

sealed record SpawnedNpc(NpcCatalog.Npc Npc, int X, int Y)
{
    /// <summary>Its Transitionable state as the client has it (NPCTalk scripts step it: Game.StepNpcTransition).</summary>
    public byte State { get; set; } = Npc.TransitionState;
}

// An open sell window: the NPC, the two offer inventories (manager 1), the offered items and the NPC's offer.
sealed class NpcSale(SpawnedNpc npc, uint mine, uint theirs)
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
sealed class OpenShop(SpawnedNpc npc, Dictionary<uint, (uint Tab, InventoryItem Item)> items)
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

sealed class SpawnedMonster(string label, PlayerWalk walk, int aggroRange, int stopRange, uint maxLife)
{
    public MadnessExplosion? MadnessExplosion;
    public readonly SpiderWebState SpiderWebs = new();
    public double ReceivedActionFactor = 1;
    public double ReceivedActionUntil;
    public bool ReceivedActionFloor;
    public double RecipientDebuffRate = 1;
    /// <summary>Sent out of the wake zone (S→C 0x1aa): the client no longer ticks it.</summary>
    public bool Asleep;
    public MonsterAttack? Attack { get; set; }
    public bool Hostile { get; init; } = true;
    /// <summary>A controller drives every action (a Survivors gate boss): the ordinary AI neither chases nor swings.</summary>
    public bool Scripted { get; init; }
    public uint? OwnerId { get; init; }
    // The monster whose summoning skill brought it (MonsterAttack.Summon), whether its death gives no experience or items
    // (summoned_monsters_no_drops_or_experience), and when its own summoning skill is off cooldown.
    public uint? SummonerId;
    public bool NoRewards;
    public int Overkill;
    public readonly MonsterIgnites HeraldBurns = new();
    public double SummonReadyAt;
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
    // No damage, ailments, impale or hit reaction while a scripted grace runs.
    public bool Invulnerable => KingGrace || RitualGrace || FetishGrace || LifePhases?.Active == true;
    // What it is, for the experience and items its death gives (null for monsters made without a variety).
    public MonsterCatalog.Variety? Variety { get; init; }
    // Its known TakeHit clip lengths supply HitReaction's recovery duration. Native Actor TakeHit selects the actual
    // clip through its current ACT; rig indexes are retained as extraction/profile diagnostics.
    public IReadOnlyList<HitAnimation> HitAnimations { get; init; } = [];
    public byte Level { get; init; } = 1;
    public MonsterRarity Rarity { get; init; }
    // Its mods (MonsterMods.cs): the rarity's own and the rolled affixes, with what they change. Kept with the instance.
    public MonsterModSet Mods { get; set; } = MonsterModSet.None;
    public readonly MonsterCurses Curses = new();
    public MonsterModEffects Effects => Curses.Effects(Mods.Effects);
    // The client's speed roll for this object in percent (MonsterSpeedRoll.cs); Walk's speed already includes it.
    public int SpeedRoll { get; init; }
    // Damage over time not yet applied (fractions of a point between ticks).
    public double Burn;
    // Life regeneration not yet applied (fractions of a point between ticks; RegenerateMonsters).
    public double Regen;
    // When the player last hit it (Clock seconds): "enemies you've Hit Recently". Reset at the player's death.
    public double PlayerHitAt = double.NegativeInfinity;
    // When its attack last hit the player (Clock seconds; evaded and blocked attacks do not hit): "enemies that have Hit
    // you with an Attack Recently". Reset at the player's death. Guarded by the Monsters lock.
    public double HitPlayerAt = double.NegativeInfinity;
    // The attack time its Stats component carries on the client (SendAttackerSlows), when it differs from Attack.Seconds.
    public double? SwingSecondsSent;
    // Impales retained for consumption/expiry, including on a corpse (Impale.cs).
    public readonly ImpaleState Impale = new();
    // The player's ignites on it (MonsterIgnites.cs), under the Monsters lock.
    public readonly MonsterIgnites Ignites = new();
    // The player's bleeds on it, under the Monsters lock: the same rules as ignites (wiki Bleeding: only the strongest
    // deals damage, each keeps its own expiry).
    public readonly MonsterIgnites Bleeds = new();
    // The player's poisons on it (MonsterPoisons.cs), under the Monsters lock.
    public readonly MonsterPoisons Poisons = new();
    // The Ruptures its critical bleeding hits have put on it (MonsterRuptures.cs), under the Monsters lock.
    public readonly MonsterRuptures Ruptures = new();
    // Overpowered (Block Chance Reduction): the block chance its blocked supported hits take away until the deadline.
    public int OverpoweredPercent;
    public double OverpoweredUntil;
    // The chill, freeze and shock the player's hits have put on it (MonsterElementalAilments); kept on the corpse.
    public readonly MonsterElementalAilments Ailments = new();
    // All outgoing attacks share this defender's sequence, under the Monsters lock. A new spawn starts fresh.
    public readonly GameServer.Core.World.EvasionEntropy AttackEvasion = new();
    // While a swing runs (Clock seconds) the monster neither chases nor swings again.
    public double BusyUntil;
    // Every AI order owns a generation. A flinch invalidates queued echoes and contact work from that order.
    // SwingGeneration identifies ordinary attack recovery so canceling it does not clear an emerge or boss stage.
    public int ActionGeneration;
    public int? SwingGeneration;
    public double SwingOrderedAt;
    // The sequence of its last melee order, rising with the top bit set as GGG's do: the client picks the swing's clip by it.
    public ushort Sequence = 0x8000;
    public ushort NextSequence() => Sequence = (ushort)(0x8000 | ((Sequence + 1) & 0x7fff));
    // The player's debuffs on it (Game.EnemyDebuffsTick, under the Monsters lock): taunted by him until when (Clock
    // seconds) with the taunted buff shown and the end it shows, his passives' and Pride's enemy auras (BuffDefinitions
    // row → buff instance), and the increased damage taken they give (every type; fire, cold and lightning) that its
    // hits read.
    public double TauntedUntil = double.NegativeInfinity, TauntShownUntil;
    public ushort TauntBuff;
    // Intimidated and unnerved by his hits (Game.HitDebuffsOnHitLocked) until when, shown the same way as the taunt.
    public double IntimidatedUntil = double.NegativeInfinity, IntimidateShownUntil, UnnervedUntil = double.NegativeInfinity,
        UnnerveShownUntil;
    public ushort IntimidateBuff, UnnerveBuff;
    // Crushed by his hits (Awakened Brutality) until when, shown with its own crushed buff; Crush lowers its physical
    // damage reduction (Game.CrushReduction).
    public double CrushedUntil = double.NegativeInfinity, CrushShownUntil;
    public ushort CrushBuff;
    // Maimed by his hits until when (the sole movement owner) and the physical damage increase its support gave, whose
    // independent time plain Maim does not refresh; shown with its own maimed buff.
    public double MaimedUntil = double.NegativeInfinity, MaimShownUntil;
    public double MaimedPhysicalTakenUntil = double.NegativeInfinity;
    public ushort MaimBuff;
    public int MaimedPhysicalTakenPct;
    public int MaimedMovementShownPct;
    public bool MaimedMovementShown;
    public double WalkSpeedAtZeroFactor;
    // Hallowing Flame (Hallow Support): until when the next attack against it gains extra fire, and that share.
    public double HallowingFlameUntil = double.NegativeInfinity;
    public double HallowingFlamePct;
    // Excommunicate Support: until when its chaos damage is suppressed.
    public double ExcommunicatedUntil = double.NegativeInfinity;
    // "Killed Enemies drop X% increased Items" from the skill of the last player hit (Item Quantity Support).
    public int KilledWithItemQuantityPct;
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
    public uint Life = maxLife;
    // Until when its last flinch plays (Clock seconds; HitReaction): no new action or flinch before.
    // Guarded by the Monsters lock.
    public double FlinchUntil = double.NegativeInfinity;
    // A boss cannot be stunned again until then (HitReaction). Guarded by the Monsters lock.
    public double StunImmuneUntil = double.NegativeInfinity;
    public bool Aggro, Dead;
    /// <summary>The player the instance's monster acts for now (its chase, swings and damage run on that player's
    /// connection: SharedMonsters.cs MonsterTargetsLocked); null while it has none.</summary>
    public ClientState? Target;
    /// <summary>The player whose hit landed on it last: an ignite, bleed or poison that kills it is theirs (SharedKills.cs).</summary>
    public ClientState? LastHitBy;
    // Its corpse was consumed (Deathrattle's explosion) and its object removed. Guarded by the Monsters lock.
    public bool CorpseConsumed;
    // The Unhinged hit-kill decision captured at the lethal Life mutation, bound to the client that accepted the hit.
    // Kept on this exact monster instance until the kill publication consumes it.
    public UnhingedHitKillReceipt? UnhingedHitKill;
    public sealed record UnhingedHitKillReceipt(ClientState Owner, bool DestroyCorpse);
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
sealed class LockstepAttack(int action, uint targetId, SpawnedMonster? target, int reach, int tolerance, int precision,
    Func<double> hold)
{
    public int Action { get; } = action;
    public uint TargetId { get; } = targetId;
    public SpawnedMonster? Target { get; } = target;
    public (int X, int Y) GroundTarget { get; init; }
    public (int X, int Y) AimAt(double at) => Target?.Walk.Position(at) ?? GroundTarget;
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
    // Completed under the Monsters lock by the step pass: the attack's task must not run on there.
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
sealed record RighteousFireState(ushort Buff, ushort SelfAura, ushort Skill, int DamagePctPerMinute, int BurnPctPerMinute)
{
    /// <summary>The current ring object, replaced in place when the retained fire's radius changes.</summary>
    public uint? Ring { get; set; }
    /// <summary>The retained fire's current radius in cells.</summary>
    public int Radius { get; set; }
    /// <summary>The ring art selected on activation; null means this activation has no ring.</summary>
    public string? RingArt { get; init; }
    /// <summary>The current gem's neutral righteous_fire_spell_damage_+%_final value, refreshed from its own stats.</summary>
    public int SpellDamageMore { get; set; }
    public int EnergyShieldBurnPctPerMinute { get; init; }
    public int EnergyShieldDamagePctPerMinute { get; init; }
    /// <summary>The exact physical gem source that activated this fire, or null for legacy state.</summary>
    internal GemSourceLifetimes.SourceLifetime? SourceLifetime { get; init; }
    /// <summary>The instance that owned this activation; current-state checks must still name this exact instance.</summary>
    internal InstanceState Instance { get; init; } = null!;
    public readonly Dictionary<uint, ushort> Auras = [];
    /// <summary>The worn skill effect's righteous_fire_aura visual (BuffVisuals row; null: the row's own), on the enemies too.</summary>
    public ushort? AuraVisual { get; init; }
}

// The last instance list shown (0xae); 0xaf picks a row of it.
sealed record ChooserList(ushort Area, IReadOnlyList<ulong> Ids, bool AtWaypoint = false);

sealed partial class ClientState(AreaLayout layout, uint ticket, uint seed)
{
    internal bool Controlled, ControlledPrepared;
    internal double ControlledNextAction, ControlledDeadSince = double.NaN;
    public AreaLayout Layout { get; private set; } = layout;
    public uint Ticket => ticket;
    public uint Seed => seed;
    public string? Character { get; init; }
    public Guid? CharacterId { get; init; }
    public readonly Guid VitalsSessionId = Guid.NewGuid();
    /// <summary>The account the character belongs to (Auth's resolve): its stash (AccountStash) is shared by all of them.</summary>
    public string? Account { get; init; }
    public string? League { get; init; }
    public bool Hardcore { get; init; }
    public bool Ssf { get; init; }
    public string? LeagueForm { get; init; }
    /// <summary>The league this connection plays by (Program.LeagueFor); LeagueForm above is the character's league form, from Auth.</summary>
    internal LeagueRuntime? LeagueModule;
    /// <summary>The hideout scene furnishing this area, for the modules placing stations.</summary>
    internal HideoutScene? CurrentHideoutScene;
    /// <summary>The module whose native window is open (IPlayerWorld.OpenWindow).</summary>
    public string? ModuleWindow;
    /// <summary>What sends to this client, for what is reached only with the connection (the built-in mods).</summary>
    internal (string Remote, System.Net.Sockets.NetworkStream Stream, GameServer.Core.Protocol.Login.ConnectionCiphers Ciphers) Wire;
    public double GemExperienceCarry;
    public bool BossRecoveryHandled;
    public bool HardcoreDeathRecorded;
    /// <summary>The character's last town (HASH16, from Auth's resolve): Resurrect in Town goes there. 0 before the
    /// character has entered a town (the death screen then has no Resurrect in Town).</summary>
    public ushort LastTown { get; init; }
    /// <summary>The area the player left through an exit to come here, or null (login, Resurrect in Town).</summary>
    public ushort? CameFrom { get; init; }
    // The character's class: its Characters.dat row (the Witch: 1).
    public int ClassRow { get; init; } = SkillCatalog.WitchRow;
    /// <summary>The instance's game time (InstanceWorld.Clock), shared with everyone in it.</summary>
    public System.Diagnostics.Stopwatch Clock => Instance?.World.Clock ?? ownClock;
    readonly System.Diagnostics.Stopwatch ownClock = System.Diagnostics.Stopwatch.StartNew();
    public readonly ConnectionPerformance Performance = new();
    // The connection's last messages, both directions (PacketHistory), and why the server closed it (the DISCONNECT line).
    public readonly PacketHistory Packets = new();
    public volatile string? CloseReason;
    // The Escape menu's pause (C→S 0x1d4) stops the Clock: walks, cooldowns and flask effects stand still meanwhile.
    public bool Paused => !Clock.IsRunning;
    // A module-owned pause (the Survivors reward window) that must survive the client's own C→S 0x1d4 unpause: while
    // this is set, SetPaused(false) leaves the clock stopped and keeps the client's world paused.
    public bool PauseHeld;
    // A transfer invalidates flights even during the gap after PendingTransfer is consumed by Transfer.
    // Guarded by Monsters, like projectile impact commits and pause changes.
    public bool LeavingArea;
    // Auth accepted the destination; PendingTransfer is already consumed before this handoff.
    public bool TransferAccepted;
    public readonly RecentEnemyHits RecentEnemyHits = new();
    // The other "recently" events (issue #504): what happened in the last four seconds, for the stats that ask.
    public readonly RecentEvents RecentEvents = new();
    public readonly PlayerElementalAilments ElementalAilments = new();
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
    public int PresentedIncomingAilmentEpoch = -1;
    public int DeathNotificationEpoch = -1;
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
        lock (Monsters)
        {
            IncomingHitEpoch++;
            // The action state machine replaces the old swing epoch argument. Retired work cannot
            // resume after a failed transfer, even when admission reopens on this same connection.
            foreach (var monster in Monsters.Values)
            {
                monster.ActionGeneration++;
                monster.SwingGeneration = null;
            }
            if (clear)
            {
                RecentEnemyHits.Clear();
                RecentEvents.Clear();
                ElementalAilments.Clear();
                IncomingAilments.Clear();
                PresentedIncomingAilments = null;
                PresentedIncomingAilmentEpoch = IncomingHitEpoch;
            }
        }
    }
    public int ProjectileEpoch;
    public IProjectileRuntime? ProjectileRuntimeState;
    public readonly HashSet<uint> ProjectileObjects = [];

    public bool ProjectileActive(int epoch)
    {
        lock (Monsters) return !Closed && !LeavingArea && !RageRetired && ProjectileEpoch == epoch;
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
        lock (Monsters)
        {
            ResourceEffectsState?.Resume(Clock.Elapsed.TotalSeconds);
            LeavingArea = false;
        }
    }

    // Successful transfers retire all source-area workers independently of any particular carried mechanic.
    public void Retire() { lock (Monsters) RageRetired = true; }

    /// <summary>The <see cref="Clock"/> time of a Stopwatch timestamp in the past (a socket read); now when 0 or paused.</summary>
    public double ClockAt(long timestamp)
    {
        double now = Clock.Elapsed.TotalSeconds;
        double at = timestamp == 0 || Paused ? now : Math.Max(0, now - System.Diagnostics.Stopwatch.GetElapsedTime(timestamp).TotalSeconds);
        // A buffered input cannot rewrite motion in history already consumed by projectile collision.
        return Math.Max(at, ProjectileRuntimeState?.NowTicks / 120000d ?? 0);
    }
    // The server's copy of the client-predicted walk (reported in each 0x1ab): the client's own pathfinder and stepping.
    public PlayerWalk Walk => _walk ??= NewPlayerWalk();
    PlayerWalk? _walk;

    /// <summary>Switches to a layout with a walk map (before the player exists).</summary>
    public void Use(AreaLayout better)
    {
        Layout = better;
        _walk = NewPlayerWalk();
        ApplyMovementSpeed();
    }

    // The blocking objects the client's pathfinder walks around (Positioned blocking = true), with their Positioned size
    // (object_size − 1): NPCs (NPC.ot 5) and the stashes (Stash.ot, GuildStash.ot 3) by object id, and the living
    // hostile monsters where the server's estimate has them now (MonsterVarieties ObjectSize − 1).
    public const int NpcBlockerSize = 4, StashBlockerSize = 2, MapDeviceBlockerSize = 3;
    readonly Dictionary<uint, ClientPathfinder.Blocker> _blockers = [];
    volatile IReadOnlyList<ClientPathfinder.Blocker> _blockerList = [];
    public IReadOnlyList<ClientPathfinder.Blocker> Blockers
    {
        get
        {
            var fixedOnes = _blockerList;
            if (IsPhasing) return fixedOnes;   // phasing (Quartz Flask, Rampage threshold): monsters do not block
            double now = Clock.Elapsed.TotalSeconds;
            List<ClientPathfinder.Blocker>? all = null;
            lock (Monsters)
                foreach (var (id, m) in CollisionMonsters)
                {
                    if (MonsterBody(id, m, now) is { } body) (all ??= [.. fixedOnes]).Add(body);
                }
            foreach (var peer in Instance?.PlayersExcept(PeerId) ?? [])
                if (peer.Life > 0 && peer.Owner is not null) (all ??= [.. fixedOnes]).Add(new(peer.X, peer.Y, LivingCollision.PlayerSize));
            return all is null ? fixedOnes : new GameServer.Core.World.BlockerSet(all);
        }
    }

    public IReadOnlyList<ClientPathfinder.Blocker> StationaryBlockers => _blockerList;

    public int StationaryInteractionSize(uint id)
    {
        lock (_blockers) return _blockers.TryGetValue(id, out var b)
            ? Math.Max(b.Size, (int)Math.Ceiling(b.CollisionRadius ?? 0)) : 0;
    }

    public void SetBlocker(uint id, int x, int y, int size, double? collisionRadius = null)
    {
        lock (_blockers) { _blockers[id] = new(x, y, size) { CollisionRadius = collisionRadius }; _blockerList = new GameServer.Core.World.BlockerSet(_blockers.Values); }
    }

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
        Func<int, int, bool>? walkable = l.Walkable is { } w ? (cx, cy) => w(cx, cy) && !Barrier.Contains((cx, cy)) : null;
        return new(l.SpawnX, l.SpawnY, Program.WalkSpeed, walkable, l.GridW > 0 ? l.GridW : 4096, l.GridH > 0 ? l.GridH : 4096,
            () => Blockers, sight: l.Sight) { FrameOf = LockstepFrameOf };
    }
    public bool PlayerSent;
    // Server frames ended so far (0x2b5).
    public long ServerFrame;
    public byte[] Carry = [];
    /// <summary>Reads in a row that framed nothing (ClientInbox.UnframedRun).</summary>
    public int UnframedRun;
    /// <summary>The portal cosmetic: the MicrotransactionPortalVariations row every portal is listed with (0x008f +0x26),
    /// 0 for the base effect. Applied to portals already standing as well as to new ones.</summary>
    public ushort CosmeticPortalVariation;
    /// <summary>The portrait frame the character logged in with (Auth's ticket): what the client draws and other players
    /// are told. A pick reaches it at the next login, as the pick's message says.</summary>
    public ushort PortraitFrame { get; init; }
    /// <summary>The worn cosmetics as 0x1f3 lists them (WornCosmetics), read from the save on first use; null after a
    /// pick, so the next use reads it again.</summary>
    public List<InstanceMicrotransactions.Equipped>? WornCosmetics;
    /// <summary>The Cosmetics tab's last published skill targets, built inside the send gate with 0x1f3.
    /// A key lives only while its full target remains present; retired keys are never reassigned on this connection.</summary>
    public Dictionary<uint, InstanceMicrotransactions.SkillSlot> SkillSlots = [];
    public uint NextSkillCosmeticKey = InstanceMicrotransactions.SlotCount;
    /// <summary>The worn pets by object id (SpawnPets), replaced when a Pet slot changes; RunPetFollow walks them.</summary>
    public readonly Dictionary<uint, SpawnedPet> Pets = [];
    public bool PetFollowRunning;
    /// <summary>How map portals are turned around the vertical axis (degrees; Positioned rotation). Dev: /portal_rotation,
    /// which rebuilds the portals that already stand.</summary>
    public float PortalRotationDegrees;
    public float PortalRotationRadians => PortalRotationDegrees * MathF.PI / 180f;
    public long Ticks;
    public volatile PendingTransfer? PendingTransfer;
    /// <summary>The 0x29c preview request (Hinekora's Lock): its key, the currency/target it names and the computed
    /// preview answered by 0x29f. The next 0x71 on that target commits exactly this item.</summary>
    public (ushort Key, uint InventoryId, uint CurrencyId, uint TargetInventoryId, uint TargetId, uint Seed, Item? Preview)? ForesightRequest;
    public ChooserList? Chooser;
    public ChatCommands? Commands;
    // 0x1c's u32 is an HNC carrier and must decrypt to idle zero bytes, or the client throws "HNC Protocol Violated".
    public HncSender? Hnc;
    public InstanceKeyring Keyring = null!;
    // Server objects created after entering (monsters); the player is 0x1000 and area transitions start at 0x2000.
    public uint NextObjectId
    {
        get => Instance is { } i ? i.World.NextObjectId : ownNextObjectId;
        set { if (Instance is { } i) i.World.NextObjectId = value; else ownNextObjectId = value; }
    }
    uint ownNextObjectId = 0x3000;
    // Every 0x1ab action on this connection gets a fresh sequence. The official server increments it per action (the
    // King's first actions are 0x8011, 0x8012, …); reusing 0xffff for several actors left the client's channel object
    // without a current action and crashed StartChannelling (0xc0000005 at 0x19e0daf, live 2026-10-31).
    int _nextActionSequence = 0x8000;
    public ushort NextActionSequence() => (ushort)Interlocked.Increment(ref _nextActionSequence);
    // The flags of a server order to a monster (0x1ab → action +0x122; ActionStarted adds 0x80 with a target id). GGG's
    // are 0: none of 56 175 monster orders in four lockstep recordings carries 0x400 or 0x8. On a monster the client reads
    // 0x400 only in the offer (0x141e8cf10: a Move without it is refused while a non-Move action waits queued at +0x1c0)
    // and 0x8 nowhere (it is the client's own auto_equip bit on its requests, 0x14011e950). A predictive client keeps
    // 0x0408, the form it has always had.
    public ushort MonsterOrderFlags => LockstepClient ? (ushort)0 : (ushort)0x0408;
    // Room server objects a quest keeps in the area until its flag is set (The Twilight Strand's force wall), by id.
    public readonly Dictionary<uint, (string Flag, IReadOnlySet<(int X, int Y)> Cells)> QuestRoomObjects = [];
    // A Trial of Ascendancy's objects in this area (LabyrinthTrials), by id, and the cells its closed doors and risen
    // spike traps close, by id.
    public readonly Dictionary<uint, AreaPopulation.RoomObject> TrialObjects = [];
    public readonly Dictionary<uint, IReadOnlySet<(int X, int Y)>> DoorBarriers = [];
    // Cells server objects' Hull projectile shapes close to the server's projectiles, as the client stamps them into its
    // own projectile map (a trial's dart turrets). Changed and read under the Monsters lock.
    public readonly HashSet<(int X, int Y)> ProjectileWalls = [];
    // Cells quest walls and closed doors close to the player's walk estimate (QuestBarriers): the union of
    // QuestRoomObjects' and DoorBarriers' cells.
    public volatile IReadOnlySet<(int X, int Y)> Barrier = new HashSet<(int X, int Y)>();

    /// <summary>Recomputes <see cref="Barrier"/> from the standing quest walls and closed doors; the player's walk estimate
    /// reads it on its next order (call under the Monsters lock).</summary>
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
    // The stats the server has set on the player after creating it (/set_stat, /set_speed): what the client computes
    // with, so the server can follow it (attack time).
    public readonly Dictionary<uint, int> PlayerStats = [];
    // Transient Rage belongs to this connection until its destination ticket takes ownership; guarded by Monsters.
    public readonly RageState Rage = new();
    public bool RageRetired;
    public ushort RageBuff;
    public RageState.Value? SentRage;
    // Reap's Blood Charges held (BloodCharges), the buff instance sent for them and the count it carried; guarded by Monsters.
    public int BloodCharges;
    public ushort BloodChargeBuff;
    public int SentBloodCharges;
    // Shared Vaal row budgets and native presentation belong to this connection; guarded by Monsters.
    public readonly VaalSoulState VaalSouls = new();
    public bool VaalSoulsRetired;
    public readonly Dictionary<uint, VaalSoulBudget> SentVaalSouls = [];
    // Running auras by skill id, with their buff instances and reservation. Guarded by the Monsters lock.
    public readonly Dictionary<ushort, AuraCatalog.Aura> Auras = [];
    // Only self-owned aura instances actually sent on this connection's current player object.
    public readonly Dictionary<ushort, AuraCatalog.Aura> PresentedAuras = [];
    public bool AuraStatsDirty;
    /// <summary>The stance the next Flesh and Stone activation starts in ("Blood" or "Sand"): the skill alternates on each
    /// use, and the stance flips when the aura starts (Game.ToggleAura), not when it ends.</summary>
    public string Stance = "Blood";
    public bool PlayerObjectCreated;
    // When each skill with a cooldown (GrantedEffects row) may be used again, in Clock seconds. Guarded by the Monsters lock.
    public readonly Dictionary<int, double> SkillReadyAt = [];
    // Eligible ordinary one-use cooldowns retain remaining base-time units through recovery-rate changes.
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
    public EnergyShield EnergyShield { get; } = new();
    CharacterStats.Pools pools = new(1, 50, 40, 2, 0);
    public CharacterStats.Pools Pools { get => pools; set { pools = value; EnergyShield.SetMaximum(value.EnergyShield); } }
    // The player stat keys the client holds (PlayerStatsWithCleared). Guarded by the Monsters lock.
    public HashSet<uint> SentStatKeys = [];
    // Dev-only forced lucky block, on top of the stats (Svalinn's "Chance to Block is Lucky").
    public bool ForceAttackBlockLucky, ForceSpellBlockLucky;
    // When the player last hit an enemy (attack or spell hit; Clock seconds), for "if you've hit an enemy recently".
    // Guarded by the Monsters lock.
    public double LastEnemyHitAt = double.NegativeInfinity;
    // PoE evasion entropy (0..99): every attack adds the attacker's hit chance, and 100+ means a hit. Shared by all
    // attackers and reset after 3.33 s without an attack. Guarded by the Monsters lock.
    public double EvasionEntropy = -1;
    public double LastEvasionAt = double.NegativeInfinity;
    // Dev-only forced lucky/unlucky Spell Suppression rolls, on top of the passive stat.
    public bool ForceSpellSuppressionLucky, ForceSpellSuppressionUnlucky;
    // The character's experience (the Player component's; the level follows from it) and its allocated passives
    // (PassiveSkillGraphId), both kept in its save (CharacterSaves). Changed under the Monsters lock.
    public uint Experience;
    public List<ushort> Passives = [];
    /// <summary>The three Atlas passive presets (PassiveSkillGraphId) and the points the character has; kept in its save.</summary>
    public List<ushort>[] AtlasPassives = [[], [], []];
    public ushort AtlasPassivePoints;
    /// <summary>The cluster jewel nodes allocated in the tree's expansion cells: the client's own small ids, sent with
    /// the expansion flag (C->S 0x34/0x36) and carried in 0x33's second list. Empty until a cluster jewel is socketed.
    /// </summary>
    public HashSet<ushort> ClusterPassives = [];
    public AscendancyProgress MainAscendancy = new();
    public CharacterSaves.PassiveProgressSnapshot? SavedPassiveProgress;
    // Selected mastery effects: mastery node (PassiveSkillGraphId) -> effect HASH16. Saved with the character.
    public Dictionary<ushort, ushort> MasteryEffects = [];
    // Applied tattoos: passive node (PassiveSkillGraphId) -> tattoo name. The tattoo stays on the node when the
    // passive is unallocated, so this map is independent of Passives. Saved with the character.
    public Dictionary<ushort, string> Tattoos = [];
    // Journey Tattoo of Makanui's chosen replacement keystone: passive node -> keystone PassiveSkillGraphId.
    public Dictionary<ushort, ushort> TattooKeystones = [];
    // Changes whenever Tattoos/TattooKeystones changes; part of the stat-source cache key.
    public int TattooRevision;
    // Extra passive points from /add_passive_points (the 0x33 extra_skill_points field), saved with the character.
    public ushort ExtraPassivePoints;
    // Refund points (0x33's first u16; Orbs of Regret), saved with the character; /add_refund_points grants them for now.
    public ushort RefundPoints;
    // /god (GodStats) and the running Righteous Fire, if any; buff instance ids (0x1c1) given out on this connection.
    public bool God;
    // /god immune: god mode that also takes no damage (hits and ailment ticks); only with God.
    public bool Immune;
    public readonly GracePeriod EntryProtection = new();
    public RighteousFireState? RighteousFire;
    /// <summary>The hands' part of the player model last sent (Program.HandsKey): null until the area's first model.</summary>
    public string? ModelHands;
    /// <summary>The flasks recovering life and mana (C→S 0x70), applied by the player tick.</summary>
    public Flasks.Effects FlaskEffects { get; } = new();
    /// <summary>The utility flasks whose effect runs (UtilityFlasks); their stats count in the pools until they end.</summary>
    public UtilityFlasks.Effects UtilityFlaskEffects { get; } = new();
    /// <summary>What Headhunter took from slain rares (StolenMods).</summary>
    public StolenMods StolenMods { get; } = new();
    /// <summary>The Headhunter buff on the client, 0 for none (Monsters lock).</summary>
    public ushort StolenModsBuff;
    /// <summary>The flask buffs Mageblood has on the client, by flask item id (Program.StepMagebloodBuffs; Monsters lock).</summary>
    public Dictionary<uint, (byte Slot, ushort Buff, ushort? Extra)> MagebloodBuffs { get; } = [];
    /// <summary>The flasks' partial charges and the crit charge cooldown (FlaskCharges).</summary>
    public FlaskCharges.Carry FlaskChargeCarry { get; } = new();
    /// <summary>One change of the flask bar and its S→C 0xbf at a time (Program.ChangeFlaskBar), so the revisions go out in
    /// order with the latest charges.</summary>
    public SemaphoreSlim FlaskBarGate { get; } = new(1, 1);
    /// <summary>The smoke clouds and consecrated ground of flasks (FlaskGrounds).</summary>
    public FlaskGrounds.State FlaskGrounds { get; } = new();
    /// <summary>Each Strike skill's last use, for Ancestral Echo (StrikeTargets.Memory). Guarded by the Monsters lock.</summary>
    public StrikeTargets.Memory StrikeLocations { get; } = new();
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
    /// <summary>The next buff instance (S→C 0x1c1). It wraps back to 1 below the monster ailments' fixed instances
    /// (MonsterElementalAilments.Instance) and never hands out 0.</summary>
    /// The instance's (InstanceWorld.NextBuff): one player's buff on a monster never shares an instance with another's, so a
    /// client's removal of its own never takes off one another client put there.
    public ushort NextBuff
    {
        get => Instance is { } i ? i.World.NextBuff : nextBuff;
        set
        {
            ushort v = value is 0 or >= MonsterElementalAilments.FirstInstance ? (ushort)1 : value;
            if (Instance is { } i) i.World.NextBuff = v; else nextBuff = v;
        }
    }
    ushort nextBuff = 1;
    public uint PlayerMaxLife => Pools.Life;
    /// <summary>The shared Life reservation owner's latest usable cap. Full maximum Life is unchanged.</summary>
    public int PlayerLifeCap { get; set; } = int.MaxValue;
    /// <summary>The last qualified item percentage. The qualification stage supplies this neutral value.</summary>
    public int QualifiedItemLifeReservationPercent { get; set; }
    public uint PlayerReservedLife
    {
        get
        {
            lock (Monsters) return (uint)PlayerLifeReservation.Reserved;
        }
    }
    public uint PlayerUnreservedLife
    {
        get
        {
            lock (Monsters) return (uint)PlayerLifeReservation.Unreserved;
        }
    }
    LifeReservation.Value PlayerLifeReservation
    {
        get
        {
            int itemPercent = QualifiedItemLifeReservationPercent;
            if (Program.Stats.Key(LifeReservation.ItemPercentStat) is uint itemPercentKey
                && PlayerStats.TryGetValue(itemPercentKey, out int actualItemPercent))
                itemPercent = actualItemPercent;
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
            lock (Monsters)
            {
                // Resource loss belongs to the actual death transition, never a delayed death notification.
                if (playerLife > 0 && value == 0)
                {
                    VaalSouls.ClearBalance(); VaalPreventionUntil = 0; VaalUseState = null;
                    Rage.Clear(); Rampage.Clear(); RetireOwnedCombat(); RetireResourceEffects();
                }
                playerLife = value;
            }
        }
    }
    // Defaults to the original shared stream; an isolated connection may supply deterministic critical draws.
    internal Random CriticalRandom { get; init; } = Random.Shared;
    internal Random AttackAccuracyRandom { get; set; } = Random.Shared;
    // The player's mana as the server keeps it: spent by gem skills (mana_cost), regenerated by RunPlayerTick.
    public uint PlayerMana;
    // When the server last sent the pool back after an attack-move, whose use the client pays for but the server only walks.
    public double LastManaResyncAt = double.NegativeInfinity;
    public uint? SentEnergyShield;
    public bool? SentRechargeActive;
    public int? SentEsRecoveryRate;
    // A trap's degeneration on the player (a trial roomba), points a second, energy shield first: RunResourceRecovery
    // deals it, and while it runs the client is sent no recovery rates to predict a rise with.
    public double TrapDegenPerSecond;
    // (MonsterVarieties row, level) pairs already registered with 0x20 on this connection.
    public readonly HashSet<(int Row, byte Level)> MonsterTypes = [];
    // The open sell window (Sell Items): what the player offered and what the NPC offers back.
    public NpcSale? Sale;
    // The shop tab opened last (Purchase Items), with what is left in it.
    public OpenShop? Shop;
    // The town's Stash object (0 = none here), and the stash tabs whose inventory this connection has sent (0xc0 + 0xc8).
    public uint StashObject;
    public readonly HashSet<int> StashTabsSent = [];
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
    public readonly HashSet<int> GuildStashTabsSent = [];
    public bool GuildStashOpen;
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
    // the room objects the sight rules watch, and the client's own blockers the server mirrors (Monsters lock).
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
    // The dialogue row whose script acts when its talk ends (DoNPCChat, FinishNPCChat, SendOpenScreen, AreaTransitionTo:
    // Game.EndTalkScript), or null.
    public NpcCatalog.Talk? NpcTalkScript;
    // The NPCs a quest decides in this instance (QuestNpc, Game's QuestNpcs.cs), created or not.
    public readonly List<QuestNpc> QuestNpcs = [];
    // The monster varieties that died in this instance, for NPCs a death frees (Quests.NpcRule.FreedBy); under QuestNpcs' lock.
    public readonly HashSet<string> KilledVarieties = new(StringComparer.Ordinal);
    // Where the later forms the area leaves out would stand (AreaPopulation.LaterFormPoints), by MonsterVarieties Id, for a
    // quest that forms one (Quests.FormsRule); under Monsters' lock.
    public readonly Dictionary<string, (int X, int Y)> LaterFormPoints = new(StringComparer.Ordinal);
    // The area transitions created on this instance's client (ids), and those a script showed before their quest flag
    // (Game.TransitionShown).
    public HashSet<uint> ShownTransitions = [];
    public HashSet<uint> ScriptShownTransitions = [];
    public int? NpcRewardOffer;
    public Dictionary<uint, Item> NpcRewardItems = [];
    // NPCs created on this connection by object id (town NPCs, /npc).
    public readonly Dictionary<uint, SpawnedNpc> Npcs = [];
    /// <summary>The character's quest flags (QuestFlags.dat Ids), saved in data/saves/.</summary>
    public HashSet<string> QuestFlags = [];
    /// <summary>Of <see cref="Quests.EveryCharacter"/> (given on login, in <see cref="QuestFlags"/> but for
    /// <see cref="Quests.EndgameOnly"/>), those the character's save does not have: granted, not earned. A quest event that
    /// sets one (Kitava's death: EndGameAvailable) saves it and takes it out of here; what is earned only through the quest
    /// (Kitava's Act 10 penalty, /state's flags) reads this, and Game.PlayerQuestFlags sends an unearned endgame flag only
    /// in a hideout or a map area.</summary>
    public HashSet<string> UnearnedQuestFlags = [];
    /// <summary>The chosen Pantheon gods (PantheonPanelLayout Ids, null = none), from the save (CharacterSaves.SetPantheon);
    /// the Pantheon module changes them under the Monsters lock (campaign/pantheon.md).</summary>
    public string? PantheonMajor, PantheonMinor;
    // QuestFlags is changed under the Monsters lock (SetQuestFlagsHeld); code that may run beside it (the player tick)
    // reads it through these, under the same lock.
    public HashSet<string> QuestFlagsSnapshot() { lock (Monsters) return [.. QuestFlags]; }
    public bool HasQuestFlag(string flag) { lock (Monsters) return QuestFlags.Contains(flag); }
    /// <summary>Whether the character earned the flag: it has it, and not only as one every character is given at login
    /// (<see cref="UnearnedQuestFlags"/>: EndGameAvailable before Kitava's Act 10 death).</summary>
    public bool EarnedQuestFlag(string flag) { lock (Monsters) return QuestFlags.Contains(flag) && !UnearnedQuestFlags.Contains(flag); }
    // One quest-flag change at a time per connection (SetQuestFlags, the quest item sync): each reads, changes and sends the whole
    // set, so two at once could send an older set last.
    public readonly SemaphoreSlim QuestGate = new(1, 1);

    /// <summary>The character's league quest flags (client ids, Quests.League): every character's, and at login those the
    /// account saved in its league (AccountStash.LeagueQuestFlags, Game.SetLeagueQuestFlags).</summary>
    public HashSet<int> LeagueQuestFlags = [.. Quests.League.EveryCharacter, .. Program.Bench.UnlockFlags];

    /// <summary>The hideout doodads created in this area (the map device), by object id.</summary>
    public readonly Dictionary<uint, HideoutCatalog.Doodad> HideoutDoodads = [];

    /// <summary>The open map's portals created in this area, by object id: where each stands.</summary>
    public readonly Dictionary<uint, (int X, int Y, int Index)> MapPortals = [];

    /// <summary>Counts the map portal sequences started (SpawnMapPortals); a newer one stops the older.</summary>
    public int MapPortalGeneration;

    /// <summary>In a map's area: the portal back to the hideout (object id and cell), or null.</summary>
    public (uint Id, int X, int Y)? ReturnPortal;
    /// <summary>The portal back to the character's open town portal, in its town or hideout (SpawnBackPortal).</summary>
    public (uint Id, int X, int Y)? BackPortal;
    /// <summary>The gem skill being channelled (Cyclone: C→S 0x197 until 0x199), 0 for none, and when it next hits.</summary>
    public ushort ChannelSkill;
    public int ChannelGeneration;
    /// <summary>The last passive and equipment stat sum and its inputs (CharacterStatSources).</summary>
    public volatile object? StatSourcesCacheState;
    /// <summary>The channelled skill's action sequence, so its 0x1af hit result names the action it belongs to.</summary>
    public ushort ChannelSkillSequence;
    public double NextChannelHitAt;
    /// <summary>When the channel last heard from the client (its start, or a C→S 0x197 update; Clock seconds).</summary>
    public double LastChannelUpdateAt;
    /// <summary>The instance this connection is in (its number), for a portal back to it.</summary>
    public ulong InstanceNumber { get; init; }
    /// <summary>Arrive at this cell instead of the spawn (back through a town portal).</summary>
    public (int X, int Y)? ArriveAt;
    /// <summary>Where ReturnPortal leads: 0 the hideout (a map's own portal, or a Portal Scroll's in a map), else a town's
    /// WorldAreas HASH16 (a Portal Scroll's anywhere else).</summary>
    public ushort ReturnPortalTo;

    /// <summary>The 0x8f entries' model byte (/map_portal sets it for experiments).</summary>
    public byte MapPortalStyle;
    // Spawned monsters by object id, in spawn order (for /mobs and /despawn).
    // The instance's (InstanceWorld.Monsters, also the instance's lock): every connection in it sees and hits the same ones.
    public SortedDictionary<uint, SpawnedMonster> Monsters => Instance?.World.Monsters ?? ownMonsters;
    readonly SortedDictionary<uint, SpawnedMonster> ownMonsters = [];
    /// <summary>Created, untargetable monsters shared by the instance. Their native bodies exist before emergence.
    /// Guarded by Monsters; rising transfers the same actor to the targetable roster.</summary>
    public SortedDictionary<uint, SpawnedMonster> DormantMonsters => Instance?.World.DormantMonsters ?? ownDormantMonsters;
    readonly SortedDictionary<uint, SpawnedMonster> ownDormantMonsters = [];
    // A shared dormant body enters this client's collision view with its admitted create, not merely with the
    // instance's roster entry. Connections without an instance retain their local body contract.
    IEnumerable<KeyValuePair<uint, SpawnedMonster>> CollisionMonsters =>
        Monsters.Concat(DormantMonsters.Where(pair => Instance is null || MonsterViews.ContainsKey(pair.Key)))
            .OrderBy(pair => pair.Key);
    // Items on the ground by object id (dropped with C→S 0x29).
    /// <summary>The area instance's state shared across connections: its ground items.</summary>
    public InstanceState Instance { get; init; } = null!;
    /// <summary>This connection placed or restored the instance's monsters, so it saves them when it closes.</summary>
    public bool MonstersPlaced;
    public volatile bool AiEnabled = true, Closed;
    // The client has shown it is in lockstep, by a lag byte in a tick or by a request. A predictive client's lag byte stays
    // 0, so it is never probed and keeps its own messages. The client alone picks its Network Mode (07 §6): every
    // connection serves both, and a player switches by changing it and entering again.
    public volatile bool LockstepClient;
    int _lockstepNoted;

    /// <summary>Marks the client as lockstep and, once per connection, logs that Predictive is the preferred mode.</summary>
    public void NoteLockstepClient(string remote)
    {
        LockstepClient = true;
        if (Interlocked.Exchange(ref _lockstepNoted, 1) == 0)
            Console.WriteLine($"[gateway] {remote} the client's Network Mode is lockstep (\"auto\" picks it on a low round trip): " +
                "served in lockstep, but lockstep is not currently the preferred networking mode; Predictive is " +
                "(Options > Game > Network Mode, then log out to the character screen and Play again)");
    }
    // What the default attack's last skill record was built for: a lockstep client (DefaultAttackRecordLockstep), and
    // the projectile whose count it carries (DefaultArrow or WandProjectile), which the client then draws itself
    // (Game.PlayerDefaultSkills); null when it carries none.
    public volatile bool DefaultAttackRecordLockstep;
    public volatile string? ClientDrawnShot;
    // The real round trip (LockstepLatency), its probe in flight (Stopwatch timestamp, 0 for none) and the next one's time.
    public LockstepLatency LockstepLatency = new();
    public long LockstepProbeSentAt;
    // Where the player was at each recent frame start (Game.LockstepClientCell).
    public readonly Queue<(double At, (int X, int Y) Cell)> LockstepTrail = new();
    public double LockstepNextProbeAt;
    // The frame now being sent, on Clock (PlayerWalk.FrameOf).
    public (double Start, double End)? LockstepFrame;
    // A lockstep client ends an action on a frame, up to two after the server's clock, and loses an order that comes first.
    public double LockstepActionTail => LockstepClient ? .066 : 0;
    (double Start, double End)? LockstepFrameOf(double at) =>
        LockstepClient && LockstepFrame is { } frame ? (Math.Min(at, frame.Start), frame.End) : null;
    public readonly GameServer.Core.World.LockstepHold LockstepHold = new();
    public ushort LockstepSequence;
    // The object use (C→S 0x18f) the next frame starts.
    public InstanceTransfer.UseSkillOnObject? LockstepUse;
    public readonly GameServer.Core.World.LockstepSkillHold LockstepSkill = new();
    // Until when the skill started last keeps the player (Clock seconds).
    public double LockstepBusyUntil;
    // A skill on a monster whose walk-up or swing is still to come on a lockstep client, null for none
    // (Game.LockstepAttackStart): the next use of a held skill waits for it, and the step pass runs its walk-up.
    public LockstepAttack? LockstepAttack;
    // The character's activated waypoints (WorldAreas Ids, kept in its save) and whether this connection was reached
    // by waypoint travel (the player then appears next to the area's waypoint).
    public readonly HashSet<string> Waypoints = [];
    // The areas the character has entered (WorldAreas Ids, kept in its save): their visit quest flags.
    public readonly HashSet<string> Visited = [];
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

/// <summary>A worn pet the server walks after the player (RunPetFollow): its walk estimate, which side of the player it
/// keeps (+1 or −1) and where it was last sent.</summary>
sealed class SpawnedPet(WalkEstimator walk, int side)
{
    public WalkEstimator Walk { get; } = walk;
    public int Side { get; } = side;
    public (int X, int Y)? Target { get; set; }
}
