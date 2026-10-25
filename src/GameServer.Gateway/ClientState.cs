using System.Diagnostics;
using GameServer.Core.Protocol.Instance;
using WalkEstimator = GameServer.Core.World.WalkEstimator;
using PlayerWalk = GameServer.Core.World.PlayerWalk;
using ClientPathfinder = GameServer.Core.World.ClientPathfinder;
using DamageType = GameServer.Core.World.DamageType;
using DamagePacket = GameServer.Core.World.DamagePacket;

/// <param name="AtSpawn">Arrive at the destination's spawn (Resurrect in Town) instead of next to the exit back.</param>
/// <param name="AtWaypoint">Waypoint travel: arrive next to the destination's waypoint.</param>
/// <summary>A character's open town portal: where it stands and where it leads (OpenTownPortal).</summary>
sealed record OpenPortal(ushort Area, ulong Instance, int X, int Y, ushort LeadsTo);

/// <param name="ArriveAt">Arrive at this cell (back through a town portal).</param>
sealed record PendingTransfer(ushort Area, ulong Instance, bool New, bool AtSpawn = false, bool AtWaypoint = false, (int X, int Y)? ArriveAt = null)
{
    /// <summary>Called with Auth's answer to the transfer (a map portal records the map's new instance).</summary>
    public Action<ControlResponse>? Done { get; init; }
}

// A monster this connection spawned: label for /mobs, the server's estimate of its walk, and its AI state.
// A monster's attack: its skill id, attack time, reach (cells, centre to centre) and damage per hit.
sealed record MonsterAttack(ushort SkillId, double Seconds, double Range, DamagePacket Damage)
{
    /// <summary>The attack's final stats (the client derives none, 11-skills.md §4): the swing time, reach and damage.
    /// These also go on the monster's Stats component.</summary>
    public IReadOnlyList<(uint, int)> Stats()
    {
        int ms = (int)Math.Round(Seconds * 1000);
        var physical = Damage[DamageType.Physical];
        int min = (int)Math.Floor(physical.Min), max = (int)Math.Floor(physical.Max);
        return
        [
            (StatKeys.MainHandMinPhysical, min), (StatKeys.MainHandMaxPhysical, max),
            (StatKeys.MainHandMinTotal, min), (StatKeys.MainHandMaxTotal, max),
            (StatKeys.MainHandAttackDurationMs, ms), (StatKeys.MainHandTotalAttackDurationMs, ms),
            (StatKeys.HundredTimesAttacksPerSecond, (int)Math.Round(100 / Seconds)),
            (StatKeys.MainHandBaseMaxAttackDistance, (int)Range), (StatKeys.MainHandMaxAttackDistance, (int)Range),
        ];
    }

    /// <summary>The skill's stats: <see cref="Stats"/> plus skill_is_non_pathing. The server walks the monster into range
    /// itself; without it the Melee action's begin (0x141938830) sends the attack through the client's own pathing step,
    /// which drops a server-driven monster's attack (no swing). Only on the skill: on the monster's Stats component it
    /// also stops its Move action from pathing (live 2026-10-17: the monsters stood still).</summary>
    public IReadOnlyList<(uint, int)> SkillStats() => [.. Stats(), (StatKeys.SkillIsNonPathing, 1)];
}

sealed record SpawnedNpc(NpcCatalog.Npc Npc, int X, int Y);

// An open sell window: the NPC, the two offer inventories (manager 1), the offered items and the NPC's offer.
sealed class NpcSale(SpawnedNpc npc, uint mine, uint theirs)
{
    public SpawnedNpc Npc { get; } = npc;
    public uint Mine { get; } = mine;
    public uint Theirs { get; } = theirs;
    // The offered items stay in the character's inventory cells until the sale ends (PlayerInventories.Offer).
    public List<InventoryItem> Reward = [];
    public uint MineRevision, TheirsRevision;
}

// An open shop tab: the NPC, its inventory id (manager 1), the items left by id and the tab's 0xbf revision.
sealed class OpenShop(SpawnedNpc npc, Dictionary<uint, (uint Tab, InventoryItem Item)> items)
{
    public SpawnedNpc Npc { get; } = npc;

    /// <summary>The items for sale by id, each with its tab's inventory id.</summary>
    public Dictionary<uint, (uint Tab, InventoryItem Item)> Items { get; } = items;

    /// <summary>The next revision of a tab's inventory (S→C 0xbf).</summary>
    public uint NextRevision(uint tab) => _revisions[tab] = _revisions.GetValueOrDefault(tab) + 1;
    readonly Dictionary<uint, uint> _revisions = [];
}

readonly record struct MonsterHit(uint Life, bool StageStarts);

sealed class SpawnedMonster(string label, WalkEstimator walk, int aggroRange, int stopRange, uint maxLife)
{
    public MonsterAttack? Attack { get; init; }
    public bool Hostile { get; init; } = true;
    // What it is, for the experience and items its death gives (null for monsters made without a variety).
    public MonsterCatalog.Variety? Variety { get; init; }
    // Its TakeHit clips (index in the rig's animation list, length) for 0x1b2: the flinch and its sound on a hit that
    // passes HitReaction's gates. One clip for most varieties; a few have a pool the server picks from.
    public IReadOnlyList<HitAnimation> HitAnimations { get; init; } = [];
    public byte Level { get; init; } = 1;
    public MonsterRarity Rarity { get; init; }
    // Damage over time not yet applied (fractions of a point between ticks).
    public double Burn;
    // The impales the player's attacks have left on it (Impale.cs); cleared when it dies.
    public readonly ImpaleState Impale = new();
    // While a swing runs (Clock seconds) the monster neither chases nor swings again.
    public double BusyUntil;
    public string Label => label;
    public WalkEstimator Walk { get; set; } = walk;
    public int AggroRange => aggroRange;
    public int StopRange => stopRange;
    public uint MaxLife { get; } = maxLife;
    public uint Life = maxLife;
    // Until when its last flinch plays (Clock seconds; HitReaction): no new flinch before. Guarded by the Monsters
    // lock.
    public double FlinchUntil = double.NegativeInfinity;
    public bool Aggro, Dead;
    public (int X, int Y)? LastTarget;
    // A boss's stage (BossFights) and whether a hit has started it.
    public BossFights.Stage? Stage { get; init; }
    public bool StageTaken, StageRunning;
    // Its StateMachine's shared states as last sent, and whether the big boss life bar is up.
    public StateMachineData? States;
    public bool BossBarShown;
}

// A running Righteous Fire: the player's buff instance, the skill, its damage (percent of maximum life per minute, to
// enemies and to the player), its radius in cells, and the aura buff instance on each monster inside it.
sealed record RighteousFireState(ushort Buff, ushort SelfAura, uint? Ring, ushort Skill, int DamagePctPerMinute, int BurnPctPerMinute, int Radius)
{
    public int EnergyShieldBurnPctPerMinute { get; init; }
    public int EnergyShieldDamagePctPerMinute { get; init; }
    public readonly Dictionary<uint, ushort> Auras = [];
    /// <summary>The worn skill effect's righteous_fire_aura visual (BuffVisuals row; null: the row's own), on the enemies too.</summary>
    public ushort? AuraVisual { get; init; }
    /// <summary>Where the ring was last sent (FollowRighteousFire).</summary>
    public (int X, int Y)? RingAt;
}

// The last instance list shown (0xae); 0xaf picks a row of it.
sealed record ChooserList(ushort Area, IReadOnlyList<ulong> Ids, bool AtWaypoint = false);

sealed class ClientState(AreaLayout layout, uint ticket, uint seed)
{
    public AreaLayout Layout { get; private set; } = layout;
    public uint Ticket => ticket;
    public uint Seed => seed;
    public string? Character { get; init; }
    /// <summary>The account the character belongs to (Auth's resolve): its stash (AccountStash) is shared by all of them.</summary>
    public string? Account { get; init; }
    public string? League { get; init; }
    public bool Hardcore { get; init; }
    /// <summary>The character's last town (HASH16, from Auth's resolve): Resurrect in Town goes there. 0 before the
    /// character has entered a town (the death screen then has no Resurrect in Town).</summary>
    public ushort LastTown { get; init; }
    /// <summary>The area the player left through an exit to come here, or null (login, Resurrect in Town).</summary>
    public ushort? CameFrom { get; init; }
    // The character's class: its Characters.dat row (the Witch: 1).
    public int ClassRow { get; init; } = SkillCatalog.WitchRow;
    public readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    // The Escape menu's pause (C→S 0x1d4) stops the Clock: walks, cooldowns and flask effects stand still meanwhile.
    public bool Paused => !Clock.IsRunning;
    // A transfer invalidates flights even during the gap after PendingTransfer is consumed by Transfer.
    // Guarded by Monsters, like projectile impact commits and pause changes.
    public bool LeavingArea;
    public int ProjectileEpoch;
    public readonly HashSet<uint> ProjectileObjects = [];

    public bool ProjectileActive(int epoch)
    {
        lock (Monsters) return !Closed && !LeavingArea && ProjectileEpoch == epoch;
    }

    // Called inside the send gate. A queued terminal visual must survive pause and retry after resume;
    // its registry removal cannot commit until that packet is accepted.
    public bool TryCommitProjectileVisual(int epoch, Action? commit = null)
    {
        lock (Monsters)
        {
            if (!ProjectileActive(epoch) || Paused) return false;
            commit?.Invoke();
            return true;
        }
    }

    public bool TryBeginAreaChange(PendingTransfer transfer, out uint[] projectiles)
    {
        lock (Monsters)
        {
            projectiles = [];
            if (Closed || PendingTransfer is not null || LeavingArea) return false;
            PendingTransfer = transfer;
            LeavingArea = true;
            ProjectileEpoch++;
            projectiles = [.. ProjectileObjects];
            ProjectileObjects.Clear();
            return true;
        }
    }

    public void ResumeAfterFailedTransfer() { lock (Monsters) LeavingArea = false; }

    /// <summary>The <see cref="Clock"/> time of a Stopwatch timestamp in the past (a socket read); now when 0 or paused.</summary>
    public double ClockAt(long timestamp)
    {
        double now = Clock.Elapsed.TotalSeconds;
        return timestamp == 0 || Paused ? now : Math.Max(0, now - System.Diagnostics.Stopwatch.GetElapsedTime(timestamp).TotalSeconds);
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
    public const int NpcBlockerSize = 4, StashBlockerSize = 2;
    readonly Dictionary<uint, ClientPathfinder.Blocker> _blockers = [];
    volatile IReadOnlyList<ClientPathfinder.Blocker> _blockerList = [];
    public IReadOnlyList<ClientPathfinder.Blocker> Blockers
    {
        get
        {
            var fixedOnes = _blockerList;
            double now = Clock.Elapsed.TotalSeconds;
            List<ClientPathfinder.Blocker>? all = null;
            lock (Monsters)
                foreach (var m in Monsters.Values)
                {
                    if (m.Dead || !m.Hostile || m.Variety is not { } v) continue;
                    var (x, y) = m.Walk.Position(now);
                    (all ??= [.. fixedOnes]).Add(new(x, y, Math.Max(0, v.ObjectSize - 1)));
                }
            return all ?? fixedOnes;
        }
    }

    public void SetBlocker(uint id, int x, int y, int size)
    {
        lock (_blockers) { _blockers[id] = new(x, y, size); _blockerList = [.. _blockers.Values]; }
    }

    public void RemoveBlocker(uint id)
    {
        lock (_blockers) if (_blockers.Remove(id)) _blockerList = [.. _blockers.Values];
    }

    /// <summary>Applies the passive movement speed to the server's walk estimate; /set_speed is a dev override.</summary>
    public void ApplyMovementSpeed() =>
        Walk.SetSpeed(Math.Max(1, Program.WalkSpeed * (100 + Pools.MovementVelocityPct) / 100), Clock.Elapsed.TotalSeconds);

    /// <summary>Replaces an area transition of this connection's layout (dev changes; the walk estimate keeps going).</summary>
    public void ReplaceAreaTransition(uint id, AreaTransitionObject transition) =>
        Layout = Layout with { AreaTransitions = [.. Layout.AreaTransitions.Select(e => e.Id == id ? (e.Id, transition) : e)] };

    // The quest walls (Barrier) are read on every path, so a wall that opens or closes counts from the next order on.
    private PlayerWalk NewPlayerWalk()
    {
        var l = Layout;
        Func<int, int, bool>? walkable = l.Walkable is { } w ? (cx, cy) => w(cx, cy) && !Barrier.Contains((cx, cy)) : null;
        return new(l.SpawnX, l.SpawnY, Program.WalkSpeed, walkable, l.GridW > 0 ? l.GridW : 4096, l.GridH > 0 ? l.GridH : 4096,
            () => Blockers);
    }
    public bool PlayerSent;
    // Server frames ended so far (0x2b5).
    public long ServerFrame;
    public byte[] Carry = [];
    /// <summary>The portal cosmetic: the MicrotransactionPortalVariations row every portal is listed with (0x008f +0x26),
    /// 0 for the base effect. Applied to portals already standing as well as to new ones.</summary>
    public ushort CosmeticPortalVariation;
    /// <summary>The worn cosmetics as 0x1f3 lists them (WornCosmetics), read from the save on first use; null after a
    /// pick, so the next use reads it again.</summary>
    public List<InstanceMicrotransactions.Equipped>? WornCosmetics;
    /// <summary>The Cosmetics tab's skill slots last sent (SkillSlots): key (50 up, what 0x1fa names) → gem BaseItemTypes HASH32 and
    /// effect slot type.</summary>
    public Dictionary<uint, (uint Gem, byte SlotType)> SkillSlots = [];
    /// <summary>The worn pets by object id (SpawnPets), replaced when a Pet slot changes; RunPetFollow walks them.</summary>
    public readonly Dictionary<uint, SpawnedPet> Pets = [];
    public bool PetFollowRunning;
    /// <summary>How map portals are turned around the vertical axis (degrees; Positioned rotation). Dev: /portal_rotation,
    /// which rebuilds the portals that already stand.</summary>
    public float PortalRotationDegrees;
    public float PortalRotationRadians => PortalRotationDegrees * MathF.PI / 180f;
    public long Ticks;
    public volatile PendingTransfer? PendingTransfer;
    public ChooserList? Chooser;
    public ChatCommands? Commands;
    // 0x1c's u32 is an HNC carrier and must decrypt to idle zero bytes, or the client throws "HNC Protocol Violated".
    public HncSender? Hnc;
    public InstanceKeyring Keyring = null!;
    // Server objects created after entering (monsters); the player is 0x1000 and area transitions start at 0x2000.
    public uint NextObjectId = 0x3000;
    // Room server objects a quest keeps in the area until its flag is set (The Twilight Strand's force wall), by id.
    public readonly Dictionary<uint, (string Flag, IReadOnlySet<(int X, int Y)> Cells)> QuestRoomObjects = [];
    // Cells quest walls close to the player's walk estimate (QuestBarriers), the union of QuestRoomObjects' cells.
    public volatile IReadOnlySet<(int X, int Y)> Barrier = new HashSet<(int X, int Y)>();

    /// <summary>Recomputes <see cref="Barrier"/> from the standing quest walls; the player's walk estimate reads it on its
    /// next order (call under the Monsters lock).</summary>
    public void UpdateBarrier() =>
        Barrier = new HashSet<(int X, int Y)>(QuestRoomObjects.Values.SelectMany(o => o.Cells));
    // The player's attacks: the next time one may start (Clock seconds). The client repeats 0x24 while the button is held.
    public double NextAttackAt;
    // Counts the player's actions; an attack's hit lands only if no other action started since (moving cancels it).
    public int ActionCount;
    // The stats the server has set on the player after creating it (/set_stat, /set_speed): what the client computes
    // with, so the server can follow it (attack time).
    public readonly Dictionary<uint, int> PlayerStats = [];
    // Running auras by skill id, with their buff instances and reservation. Guarded by the Monsters lock.
    public readonly Dictionary<ushort, AuraCatalog.Aura> Auras = [];
    // When each skill with a cooldown (GrantedEffects row) may be used again, in Clock seconds. Guarded by the Monsters lock.
    public readonly Dictionary<int, double> SkillReadyAt = [];
    // The skill ids the client has on this connection's player (SendPlayer, SendSkills): the 0x40 skill list only adds,
    // so the ones that leave are removed by id.
    public HashSet<ushort> ClientSkillIds = [];
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
    // Selected mastery effects: mastery node (PassiveSkillGraphId) -> effect HASH16. Saved with the character.
    public Dictionary<ushort, ushort> MasteryEffects = [];
    // Extra passive points from /add_passive_points (the 0x33 extra_skill_points field), saved with the character.
    public ushort ExtraPassivePoints;
    // Refund points (0x33's first u16; Orbs of Regret), saved with the character; /add_refund_points grants them for now.
    public ushort RefundPoints;
    // /god (GodStats) and the running Righteous Fire, if any; buff instance ids (0x1c1) given out on this connection.
    public bool God;
    public RighteousFireState? RighteousFire;
    /// <summary>The flasks recovering life and mana (C→S 0x70), applied by the player tick.</summary>
    public Flasks.Effects FlaskEffects { get; } = new();
    public ushort NextBuff = 1;
    public uint PlayerMaxLife => Pools.Life;
    // The player's life as the server keeps it (PlayerObject's Life, full on creation); 0 = dead.
    public bool AuraDeathHandled;
    public uint PlayerLife;
    // The player's mana as the server keeps it: spent by gem skills (mana_cost), regenerated by RunPlayerTick.
    public uint PlayerMana;
    public uint? SentEnergyShield;
    public bool? SentRechargeActive;
    public int? SentEsRecoveryRate;
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
    public uint HorticraftingObject;
    public (int X, int Y) HorticraftingAt;
    public uint CraftingBenchObject;
    public bool BenchOpen;
    public bool BenchBalanceSent;
    public uint BenchBalanceRevision;
    public IReadOnlyList<InventoryItem> BenchBalanceItems = [];
    public HideoutScene? Furniture;
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
    public volatile uint TalkingTo;
    // Whether the open dialogue shows an NPCTalk row's talk (0x84 with its index) rather than the option list.
    public volatile bool InNpcTalk;
    public uint TutorialSceneId, TutorialChestId;
    public TutorialObjects.Placement? TutorialChest;
    public AreaPopulation.RoomObject? TutorialZombiePlacement;
    public bool TutorialSceneRunning;
    public string? NpcFinishFlag;
    public int? NpcRewardOffer;
    public Dictionary<uint, Item> NpcRewardItems = [];
    // NPCs created on this connection by object id (town NPCs, /npc).
    public readonly Dictionary<uint, SpawnedNpc> Npcs = [];
    /// <summary>The character's quest flags (QuestFlags.dat Ids), saved in data/saves/.</summary>
    public HashSet<string> QuestFlags = [];

    /// <summary>The character's league quest flags (client ids, Quests.League); not saved: every character has the same.</summary>
    public HashSet<int> LeagueQuestFlags = [.. Quests.League.EveryCharacter, .. Program.Bench.UnlockFlags];
    /// <summary>What the next Delve chart (S→C 0x240) carries; /delve_chart sets it (one cell at 0, 0 by default).</summary>
    public (uint A, ushort B, List<InstanceDelve.Cell> Cells) DelveChartProbe = (0, 0, [new(0, 0, 0, 0, 0)]);

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
    /// <summary>The channelled skill's action sequence, so its 0x1af hit result names the action it belongs to.</summary>
    public ushort ChannelSkillSequence;
    public double NextChannelHitAt;
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
    public readonly SortedDictionary<uint, SpawnedMonster> Monsters = [];
    // Items on the ground by object id (dropped with C→S 0x29).
    /// <summary>The area instance's state shared across connections: its ground items.</summary>
    public InstanceState Instance { get; init; } = null!;
    /// <summary>This connection placed or restored the instance's monsters, so it saves them when it closes.</summary>
    public bool MonstersPlaced;
    public volatile bool AiEnabled = true, Closed;
    // Whether the lockstep warning was shown on this connection.
    public bool WarnedLockstep;
    // The character's activated waypoints (WorldAreas Ids, kept in its save) and whether this connection was reached
    // by waypoint travel (the player then appears next to the area's waypoint).
    public readonly HashSet<string> Waypoints = [];
    // The areas the character has entered (WorldAreas Ids, kept in its save): their visit quest flags.
    public readonly HashSet<string> Visited = [];
    public bool ArriveAtWaypoint;

    /// <summary>A walk estimate for a server object on this area's walk map.</summary>
    public WalkEstimator NewWalk(int x, int y, double speed) =>
        new(x, y, Math.Max(1, speed), Layout.Walkable, Layout.GridW > 0 ? Layout.GridW : 4096, Layout.GridH > 0 ? Layout.GridH : 4096);

    public (int X, int Y) SpawnNear((int X, int Y) at) => Layout.Near(at);
}

/// <summary>A worn pet the server walks after the player (RunPetFollow): its walk estimate, which side of the player it
/// keeps (+1 or −1) and where it was last sent.</summary>
sealed class SpawnedPet(WalkEstimator walk, int side)
{
    public WalkEstimator Walk { get; } = walk;
    public int Side { get; } = side;
    public (int X, int Y)? Target { get; set; }
}
