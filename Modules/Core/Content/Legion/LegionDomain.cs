using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Legion;

/// <summary>
/// Phase 3: Domain of Timeless Conflict (<c>WorldAreas</c> <c>LegionLeague</c>..<c>LegionLeague4</c>, 2–5 emblems).
/// Resolves 2–5 Timeless Emblems in the Map Device (<see cref="ResolveOpening"/>), claims the
/// <c>"Legion"</c> scarab content tag for standard maps, places the central <see cref="InitiatorType"/> (ControlZone)
/// and <see cref="RewardCrystalType"/> on <see cref="GameEvents.PlayerEntered"/> (Domain areas have 0 MonsterPacks),
/// drives the endless radial armies as LegionRevive ghosts (<see cref="LegionRevive"/>) with per-rank revival cooldowns
/// (<c>MinLevel = 101</c>: 6 s / 16 s / 24 s) and revival waves while a player stands in the control zone (<see cref="PickWave"/>),
/// per-revival monster scaling, reward accumulation, and the
/// end-of-timer Timeless Jewel / reward burst around the central crystal.
/// </summary>
static class LegionDomain
{
    public const string LocatorType = "Metadata/Terrain/Leagues/Legion/Objects/LegionEndgame_EncounterLocator";
    public const string InitiatorType = "Metadata/Terrain/Leagues/Legion/Objects/LegionEndlessInitiator";
    public const string RewardCrystalType = "Metadata/Terrain/Leagues/Legion/Objects/LegionDomainRewardCrystal";
    public const string EnteredQuestFlag = "EnteredDomainOfTimelessConflict";
    const string PulseModel = "Metadata/Effects/Spells/monsters_effects/essence/monolith_pulse/monolith_pulse.ao";
    /// <summary>MiscAnimated 3357 LegionSoulercoaster1; 3337 LegionControlZone (endgame/control_zone.ao) and 3338
    /// LegionControlZonePerimeter (standhere.ao), which no client code creates: the server places both at the centre.</summary>
    const int SoulercoasterKey = 3357, ControlZoneKey = 3337, PerimeterKey = 3338;
    /// <summary>control_zone.ao's rig (control_zone/rig.amd): clip 0 idle (loop), 5 revival_wave (1333 ms, once; BigHit and RevWave
    /// sounds, the pillar blast, a screen shake), played with S->C 0x1b2 kind 0 at each wave.</summary>
    const uint ZoneIdleClip = 0, ZoneWaveClip = 5;
    const double ZoneWaveSeconds = 1.333;
    /// <summary>The initiator's obelisk_state at the end (R13: 1-4 active, 5 the `inactive` clip; 3 was ours).</summary>
    const int EndedObeliskState = 5;
    /// <summary>GUESS, to check live: SmallCentralCrystal01.ao's views are unlit, lit_up, explode (2 s, shatter at 1.35 s); the clip
    /// index of explode is taken as their order (a wrong index is ignored by the client).</summary>
    const uint CrystalExplodeClip = 2;
    const string SaveKey = "LegionDomain";
    /// <summary>GUESS: how near LegionEndlessInitiator a player starts the Domain (walking there, or using the monolith).</summary>
    const double StartRadius = 28.0;
    /// <summary>The control zone: revival waves need a living player this near the monolith. The client's own check (0x141629580,
    /// the counter.epk it shows) is a grid distance squared below 3601: 60 cells.</summary>
    const double ZoneRadius = 60.0;
    /// <summary>GUESS: the shortest time between two revival waves, map_legion_endless_monolith_pulse_time_ms (20000 on the Domain's
    /// area mod 21261); this when the area has no such stat.</summary>
    const double DefaultWaveSeconds = 20.0;
    /// <summary>A player in the zone with nothing ready: the slots are looked at again this often, not every frame.</summary>
    const double WaveRetrySeconds = 1.0;
    /// <summary>A wave is due and nobody is in the zone: looked at again this often (the client's own zone tick, DAT_1436adba4 = 0.25 s).</summary>
    const double ZoneCheckSeconds = 0.25;
    const double EffectLifetimeSeconds = 2.5;
    /// <summary>Base timer length (client 0x1416290a0: LegionBalancePerLevel MinLevel 101 = 15 s) before adding 60 s per emblem.</summary>
    const double BaseTimerSeconds = 15.0;

    static readonly HashSet<string> AreaIds = new(StringComparer.Ordinal)
    {
        "LegionLeague", "LegionLeague2", "LegionLeague3", "LegionLeague4",
    };

    static readonly string[] DefaultFactions = ["Karui", "EternalEmpire", "Vaal", "Templar", "Maraketh"];

    enum DomainPhase { Waiting = 0, Active = 1, Finished = 2 }

    /// <summary>A soldier of the Domain: its plan, where it stands (where it last fell), its fighting monster (CurrentId, Alive) and,
    /// while it does not fight, its ghost (GhostId, running LegionRevive with Sequence); RiseAt and RiseStep follow a revival in
    /// progress (1: state 5 sent, 2: state 1 sent).</summary>
    public sealed record FighterSlot(int Index, string FactionId, int Tier, int MonsterRow, int Rarity,
        int X, int Y, float Facing, int RevivalMs, int[]? Rewards,
        uint CurrentId = 0, bool Alive = false, double DeadAt = double.NegativeInfinity, int RevivalCount = 0,
        uint GhostId = 0, ushort Sequence = 0, double RiseAt = double.NegativeInfinity, int RiseStep = 0);

    sealed record SavedDomain(uint InitiatorId, uint CrystalId, int X, int Y, float? Z, int Level, int Phase,
        string[] Factions, double TimerSeconds, double WaveSeconds,
        double StartedAt, double EndsAt, double LastWaveAt,
        int Waves, int TotalKills, Dictionary<string, int> GeneralKills, List<int> BankedRewards,
        FighterSlot[] Slots, uint ZoneId = 0, uint PerimeterId = 0, uint[]? Crystals = null, double RewardProgress = 0);

    sealed class DomainState(SavedDomain s)
    {
        public uint InitiatorId = s.InitiatorId, CrystalId = s.CrystalId, ZoneId = s.ZoneId, PerimeterId = s.PerimeterId;
        /// <summary>The ids of the 100 reward-tracker crystals (<see cref="CrystalCount"/>; empty when the type is unsupported).
        /// CrystalId is the old single crystal of earlier saves and is no longer used.</summary>
        public uint[] Crystals = s.Crystals ?? [];
        /// <summary>Reward units banked, times the emblems' progress bonus (<see cref="CrystalCount"/>).</summary>
        public double RewardProgress = s.RewardProgress;
        /// <summary>How many crystals the clients have been told to light (not saved: from the progress on a reload, as the creates carry it).</summary>
        public int LitSent = LitCrystals(s.RewardProgress);
        /// <summary>The emblems' reward-progress bonus in percent, read from the area's stats on the first kill (-1: not read yet; not saved).</summary>
        public int RewardBonusPct = -1;
        public int X = s.X, Y = s.Y, Level = s.Level;
        public float? Z = s.Z;
        public DomainPhase Phase = (DomainPhase)s.Phase;
        public string[] Factions = s.Factions;
        public double TimerSeconds = s.TimerSeconds, WaveSeconds = s.WaveSeconds > 0 ? s.WaveSeconds : DefaultWaveSeconds;
        public double StartedAt = s.StartedAt, EndsAt = s.EndsAt;
        public double LastWaveAt = s.LastWaveAt;
        /// <summary>Not before this is the zone's next wave looked for (not saved).</summary>
        public double NextWaveCheck;
        public int Waves = s.Waves, TotalKills = s.TotalKills;
        public Dictionary<string, int> GeneralKills = new(s.GeneralKills, StringComparer.Ordinal);
        public List<int> BankedRewards = [.. s.BankedRewards];
        public FighterSlot[] Slots = s.Slots;
        /// <summary>True when individual kill updates have not yet been flushed to ContentState JSON.</summary>
        public bool Dirty;
        public double LastFlushAt;
        public readonly Dictionary<string, double> DefeatSaid = [];
        /// <summary>Cached MonsterVarieties lookups per MonsterRow so spawns and revivals avoid repeated string allocations.</summary>
        public readonly Dictionary<int, MonsterCatalog.Variety?> VarietyCache = [];
        /// <summary>Fast lookup from live SpawnedMonster.ObjectId -> slot index (rebuilt on restore).</summary>
        public Dictionary<uint, int> LiveByMonster = BuildLiveIndex(s.Slots);
        /// <summary>Kills RecordKill counted whose drop roll has not yet asked for its slot (MonsterKilled runs before Drops for
        /// hit kills); bounded, not saved.</summary>
        public readonly Dictionary<uint, FighterSlot> RecentKills = [];
        /// <summary>Slots being spawned right now (not ready), and slots whose variety cannot spawn (never ready); not saved.</summary>
        public readonly HashSet<int> Spawning = [], NoSpawn = [];
        /// <summary>Slots standing up (RiseStep 1 or 2), fighters that fell and wait for their ghost (the corpse's id, 0 after a reload),
        /// and ghosts that wait to be told to fall (state 3) once the client has posed them: small sets, so the tick never scans every slot.</summary>
        public readonly HashSet<int> Rising = BuildRising(s.Slots);
        public readonly Queue<(int Slot, uint Corpse)> Fallen = BuildFallen(s.Slots, s.Phase);
        public readonly List<(int Slot, double At)> Falls = [];
        /// <summary>Generals' lines waiting for the tick to say them (variety Id, category, delay ms), and when each faction's general last
        /// had a BattleCry or Dismount line (the tick's time); not saved.</summary>
        public readonly List<(string Variety, string Category, uint DelayMs)> Speech = [];
        public readonly List<(string Variety, string Category)> Chatter = [];
        public double LastChatter = double.NegativeInfinity;

        static HashSet<int> BuildRising(FighterSlot[] slots)
        {
            var set = new HashSet<int>();
            for (int i = 0; i < slots.Length; i++)
                if (slots[i].RiseStep > 0 && slots[i].GhostId != 0) set.Add(i);
            return set;
        }

        /// <summary>After a reload: a revived slot that fell and has no ghost yet gets one where it stands.</summary>
        static Queue<(int, uint)> BuildFallen(FighterSlot[] slots, int phase)
        {
            var queue = new Queue<(int, uint)>();
            if (phase == (int)DomainPhase.Active)
                for (int i = 0; i < slots.Length; i++)
                    if (slots[i] is { Alive: false, GhostId: 0 } s && s.Sequence != 0) queue.Enqueue((i, 0));
            return queue;
        }

        static Dictionary<uint, int> BuildLiveIndex(FighterSlot[] slots)
        {
            var map = new Dictionary<uint, int>();
            for (int i = 0; i < slots.Length; i++)
                if (slots[i].Alive && slots[i].CurrentId != 0) map[slots[i].CurrentId] = i;
            return map;
        }

        public SavedDomain Snapshot() => new(InitiatorId, CrystalId, X, Y, Z, Level, (int)Phase,
            Factions, TimerSeconds, WaveSeconds, StartedAt, EndsAt, LastWaveAt,
            Waves, TotalKills, new(GeneralKills, StringComparer.Ordinal), [.. BankedRewards], [.. Slots], ZoneId, PerimeterId, Crystals, RewardProgress);
    }

    /// <summary>The reward tracker (3.29.0): 100 small crystals at the centre light up, one per 3 reward units; progress past 100 crystals
    /// still counts. A reward unit is one reward icon of a killed monster (one entry of its Rewards), times (100 + the emblems' reward
    /// progress)/100 (20% per emblem, stat map_legion_monster_reward_chance_+%, and uber_domain_monster_reward_chance_+% if a mod carries it), so fractions add up across kills.</summary>
    const int CrystalCount = 100, RewardsPerCrystal = 3;
    /// <summary>GUESS: the ring of crystals around the initiator, in cells (at 18 the 100 points fall on 100 distinct cells; at 12 only about 70) (the real positions are the server's; no room places them).</summary>
    const double CrystalRingRadius = 18.0;

    static int LitCrystals(double progress) => progress <= 0 ? 0 : (int)Math.Min(CrystalCount, progress / RewardsPerCrystal);

    static readonly ConditionalWeakTable<object, DomainState> Domains = new();
    static readonly JsonSerializerOptions Json = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    static QuestObjectTypes.Info? InitiatorInfo, CrystalInfo;
    static StateMachineData? InitiatorDefaults, CrystalDefaults;
    static int InitiatorStateIndex = -1, CrystalStateIndex = -1;

    static bool InitiatorSupported =>
        InitiatorInfo is not null && InitiatorDefaults is not null && InitiatorStateIndex >= 0
        && QuestObject.CanSerialize(InitiatorInfo.Components, true);

    static bool CrystalSupported =>
        CrystalInfo is not null && CrystalDefaults is not null && CrystalStateIndex >= 0
        && QuestObject.CanSerialize(CrystalInfo.Components, true);

    public static bool IsDomainArea(string? areaId) => areaId is not null && AreaIds.Contains(areaId);

    /// <summary>Eagerly loads Domain object types and StateMachine defaults from client.db.</summary>
    public static void LoadData()
    {
        var shared = ObjectTypes.SharedStates();
        StateMachineData? MachineOf(string path) =>
            ObjectTypes.All().Where(t => t.Type == path).Select(t => (ClientRow?)t.Row).FirstOrDefault() is { } row
                ? ObjectTypes.StateMachine(row, shared) : null;
        InitiatorInfo = QuestObjectTypes.Find(InitiatorType);
        InitiatorDefaults = MachineOf(InitiatorType);
        InitiatorStateIndex = InitiatorInfo?.Components.ToList().IndexOf("StateMachine") ?? -1;
        CrystalInfo = QuestObjectTypes.Find(RewardCrystalType);
        CrystalDefaults = MachineOf(RewardCrystalType);
        CrystalStateIndex = CrystalInfo?.Components.ToList().IndexOf("StateMachine") ?? -1;
    }

    public static void UnloadData()
    {
        InitiatorInfo = CrystalInfo = null;
        InitiatorDefaults = CrystalDefaults = null;
        InitiatorStateIndex = CrystalStateIndex = -1;
    }

    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }

    public static void Register(IModuleContext module)
    {
        string id = module.Manifest.Id;
        if (InitiatorInfo is null) LoadData();
        module.Lifetime.Own(new Cleanup(() => Domains.Clear()));
        GameEvents.MapOpening.Add(id, ResolveOpening);
        GameEvents.EnterMessages.Add(id, EnterMessages);
        GameEvents.PlayerEntered.Add(id, player => Enter(module, player));
        GameEvents.AreaTick.Add(id, player => IsDomainArea(player.Layout.Id) ? (p, _) => Tick(module, p) : null);
        GameEvents.ObjectUse.Add(id, (player, use, requestedAt) => Use(module, player, use, requestedAt));
        GameEvents.Drops.Add(id, Drops);
        GameEvents.MonsterKilled.Add(id, Killed);
    }

    // ---- Map Device Opening (2-5 Timeless Emblems + Legion Scarab claim) ----

    /// <summary>GameEvents.MapOpening: claims "Legion" scarab handling on normal maps, and resolves 2–5 distinct
    /// Timeless Emblems into the matching Domain of Timeless Conflict area (<c>LegionLeague</c>..<c>LegionLeague4</c>).</summary>
    public static void ResolveOpening(MapOpening request)
    {
        if (request.Campaign) return;
        // Claim Legion scarabs so MapScarabContent does not report them unhandled on normal maps.
        request.HandledContent.Add("Legion");

        if (request.Slots.Any(s => s.IsMap)) return;
        var data = LegionData.Current;
        var emblemIndices = new List<int>(5);
        var emblems = new List<LegionData.EmblemInfo>(5);
        int otherCount = 0;
        for (int i = 0; i < request.Slots.Count; i++)
        {
            var slot = request.Slots[i];
            if (data.EmblemsByHash.TryGetValue(slot.TypeHash, out var info)
                || (slot.BaseRow >= 0 && data.EmblemsByRow.TryGetValue(slot.BaseRow, out info)))
            {
                emblemIndices.Add(i);
                emblems.Add(info);
            }
            else otherCount++;
        }

        if (emblemIndices.Count == 0)
        {
            if (request.Target != 0 && request.Target == data.DomainAtlasNodeHash)
            {
                request.Opening = null;
                request.Reason = "Place 2 to 5 different Timeless Emblems in the Map Device.";
            }
            return;
        }
        if (otherCount > 0)
        {
            request.Opening = null;
            request.Reason = "Only Timeless Emblems open the Domain of Timeless Conflict: take the other items (maps, scarabs, fragments) out of the Map Device.";
            return;
        }
        if (emblems.Count < 2)
        {
            request.Opening = null;
            request.Reason = "Place 2 to 5 different Timeless Emblems to open the Domain of Timeless Conflict.";
            return;
        }
        if (emblems.Select(e => e.FactionId).Distinct(StringComparer.Ordinal).Count() != emblems.Count)
        {
            request.Opening = null;
            request.Reason = "Each Timeless Emblem must belong to a different Legion faction.";
            return;
        }
        if (emblems.Count > 5 || data.DomainAreas.Count < 4)
        {
            request.Opening = null;
            request.Reason = "The Domain of Timeless Conflict arenas are not available in client data.";
            return;
        }

        var domainArea = data.DomainAreas[Math.Clamp(emblems.Count - 2, 0, data.DomainAreas.Count - 1)];
        int areaLevel = AreaLevelFor(domainArea.BaseLevel, emblems.Count);
        var mods = ComposeDomainMods(domainArea, emblems, Program.Items.Mods);
        var mapArea = new MapDevice.MapArea(domainArea.Hash16, domainArea.Id, domainArea.Name, 0)
        {
            Level = areaLevel,
            UsesAtlas = false,
        };
        request.Opening = new MapDevice.Opening(mapArea, mods, request.Slots[emblemIndices[0]].BaseRow)
            { PortalVariation = MapDevices.RecipePortalVariation(domainArea.Row) };
        request.ConsumeSlots = emblemIndices;
        request.Reason = "";
        request.ReturnToSource = true;
    }

    /// <summary>3.22.0: the Domain's area level is at least 80, +1 with 4 emblems and +2 with 5.</summary>
    public static int AreaLevelFor(int baseLevel, int emblemCount) =>
        Math.Clamp(Math.Max(80, baseLevel) + Math.Max(0, emblemCount - 3), 1, 100);

    static MapMods ComposeDomainMods(LegionData.DomainArea area, IReadOnlyList<LegionData.EmblemInfo> emblems, ModCatalog catalog)
    {
        var implicitStats = new Dictionary<uint, int>();
        var fragmentStats = new Dictionary<uint, int>();
        AddModRows(implicitStats, area.ModRows, catalog);
        foreach (var e in emblems) AddModRows(fragmentStats, e.ModRows, catalog);
        var sum = new Dictionary<uint, int>(implicitStats);
        foreach (var (k, v) in fragmentStats) sum[k] = sum.GetValueOrDefault(k) + v;
        return new MapMods(sum)
        {
            Implicit = implicitStats,
            Fragments = fragmentStats,
            FragmentCount = emblems.Count,
        };
    }

    static void AddModRows(Dictionary<uint, int> sum, IReadOnlyList<int> rows, ModCatalog catalog)
    {
        foreach (int row in rows)
            if (catalog.Row(row) is { } mod)
                foreach (var stat in mod.Stats)
                    if (stat.Min != 0) sum[stat.Id] = sum.GetValueOrDefault(stat.Id) + stat.Min;
    }

    /// <summary>Domain timer length in seconds: 15 s base + map_legion_endless_mode_+_s (60 s per emblem -> 135..315 s).</summary>
    public static double TimerSecondsFor(IReadOnlyDictionary<uint, int> stats, int factionCount)
    {
        int bonus = stats.GetValueOrDefault(Stat.MapLegionEndlessModePlusS);
        if (bonus <= 0) bonus = Math.Clamp(factionCount, 2, 5) * 60;
        return Math.Max(30.0, BaseTimerSeconds + bonus);
    }

    /// <summary>GUESS, to check in the client: the stacks of the per-revival stats stop at this many revivals (the tables give no cap; uncapped, a
    /// common monster revived every pulse of a 315 s Domain reaches about 45 and could not be killed).</summary>
    const int MaxRevivalStacks = 10;

    /// <summary>The monster's toughness (less damage taken) gained per revival: the emblems' uber_domain_monster_tankiness_+%_per_revival
    /// (every Timeless Emblem carries 10).</summary>
    public static int RevivalToughness(IReadOnlyDictionary<uint, int> areaStats, int revivalCount) =>
        revivalCount <= 0 ? 0 : areaStats.GetValueOrDefault(Stat.UberDomainMonsterTankinessPlusPctPerRevival) * Math.Min(MaxRevivalStacks, revivalCount);

    /// <summary>Per-revival EncounterStats of the emblems used (summed over them, times the revival count) for a monster revived
    /// in a Domain of Timeless Conflict. Only the Timeless emblems' own stats are here (the Unrelenting mods are gone); the consumers today are the phys-as-chaos gain (hit
    /// damage), the rest is carried for when monsters read them. Tankiness is applied as toughness (<see cref="RevivalToughness"/>).</summary>
    public static Dictionary<uint, int>? RevivalStats(IReadOnlyDictionary<uint, int> areaStats, int revivalCount)
    {
        if (revivalCount <= 0) return null;
        int stacks = Math.Min(MaxRevivalStacks, revivalCount);
        var stats = new Dictionary<uint, int>();
        void Add(uint areaStat, uint monsterStat)
        {
            int per = areaStats.GetValueOrDefault(areaStat);
            if (per != 0) stats[monsterStat] = stats.GetValueOrDefault(monsterStat) + per * stacks;
        }
        Add(Stat.UberDomainMonsterAttackAndCastSpeedPlusPctPerRevival, Stat.AttackAndCastSpeedPlusPct);
        Add(Stat.UberDomainMonsterCriticalStrikeChancePlusPctPerRevival, Stat.CriticalStrikeChancePlusPct);
        Add(Stat.UberDomainMonsterCriticalStrikeMultiplierPlusPctPerRevival, Stat.BaseCriticalStrikeMultiplierPlus);
        Add(Stat.UberDomainMonsterAccuracyPlusPctPerRevival, Stat.AccuracyRatingPlusPct);
        Add(Stat.UberDomainMonsterPenetrateAllResistancesPctPerRevival, Stat.BasePenetrateElementalResistancesPct);
        Add(Stat.UberDomainMonsterPhysicalDamagePctToAddAsChaosPerRevival, Stat.PhysicalDamagePctToAddAsChaos);
        Add(Stat.UberDomainMonsterAreaOfEffectPlusPctPerRevival, Stat.SkillAreaOfEffectPlusPct);
        return stats.Count > 0 ? stats : null;
    }

    // ---- Object Serialization Helpers ----

    internal static QuestObject? BuildInitiatorObject(int x, int y, float? z, int obeliskState, bool checkingControlZone)
    {
        if (!InitiatorSupported) return null;
        var sm = InitiatorDefaults!.Set("obelisk_state", obeliskState).Set("checking_control_zone", checkingControlZone ? 1 : 0);
        return new QuestObject(x, y, InitiatorInfo!.Components, 0, z, InitiatorInfo.Model) { StateMachine = sm };
    }

    internal static QuestObject? BuildCrystalObject(int x, int y, float? z, bool active)
    {
        if (!CrystalSupported) return null;
        var sm = CrystalDefaults!.Set("active", active ? 1 : 0);
        return new QuestObject(x, y, CrystalInfo!.Components, 0, z, CrystalInfo.Model) { StateMachine = sm };
    }

    // ---- Domain Lifecycle ----

    static IEnumerable<(string Log, byte[] Message)> EnterMessages(Player player)
    {
        if (!IsDomainArea(player.Layout.Id)) yield break;
        if (LegionRules.Add() is { } add) yield return ("0x001f ruleset LegionRules (domain enter)", add);
    }

    static DomainState? StateOf(Player player)
    {
        if (player.Instance is not { } instance || !IsDomainArea(player.Layout.Id)) return null;
        if (Domains.TryGetValue(instance, out var existing)) return existing;
        if (instance.ContentState.TryGetValue(SaveKey, out string? text) && !string.IsNullOrWhiteSpace(text)
            && JsonSerializer.Deserialize<SavedDomain>(text, Json) is { } saved)
            return Domains.GetValue(instance, _ => new DomainState(saved));
        return null;
    }

    const double DomainFlushSeconds = 0.5;
    const double DefeatLineSeconds = 60;

    static void Keep(Player player, DomainState state, bool flush = true)
    {
        if (!flush) { state.Dirty = true; return; }
        state.Dirty = false;
        state.LastFlushAt = global::Game.AreaTime(player);
        if (player.Instance is { } instance)
            instance.ContentState[SaveKey] = JsonSerializer.Serialize(state.Snapshot(), Json);
    }

    static string[] ResolveFactions(IReadOnlyDictionary<uint, int> stats, string areaId)
    {
        var list = new List<string>(5);
        if (stats.GetValueOrDefault(Stat.MapLegionFragmentFactionKarui) > 0) list.Add("Karui");
        if (stats.GetValueOrDefault(Stat.MapLegionFragmentFactionEternal) > 0) list.Add("EternalEmpire");
        if (stats.GetValueOrDefault(Stat.MapLegionFragmentFactionVaal) > 0) list.Add("Vaal");
        if (stats.GetValueOrDefault(Stat.MapLegionFragmentFactionTemplar) > 0) list.Add("Templar");
        if (stats.GetValueOrDefault(Stat.MapLegionFragmentFactionMaraketh) > 0) list.Add("Maraketh");
        if (list.Count >= 2) return [.. list];
        int fallbackCount = areaId switch { "LegionLeague2" => 3, "LegionLeague3" => 4, "LegionLeague4" => 5, _ => 2 };
        return [.. DefaultFactions.Take(fallbackCount)];
    }

    static async Task Enter(IModuleContext module, Player player)
    {
        if (!IsDomainArea(player.Layout.Id) || player.Instance is not { } instance) return;
        if (!global::Game.ClientHasQuestFlag(player, EnteredQuestFlag))
            await global::Game.SetQuestFlags(player, [EnteredQuestFlag]);

        var state = StateOf(player);
        if (state is null)
        {
            var stats = LegionMonolith.AreaStats(player);
            string[] factions = ResolveFactions(stats, player.Layout.Id);
            int mapLevel = player.Instance.Map?.Area.Level ?? 0;
            int level = Math.Clamp(mapLevel > 0 ? mapLevel : AreaLevelFor(80, factions.Length), 1, 100);
            double timerSec = TimerSecondsFor(stats, factions.Length);
            int pulseMs = stats.GetValueOrDefault(Stat.MapLegionEndlessMonolithPulseTimeMs);
            double waveSec = pulseMs >= 3000 ? pulseMs / 1000.0 : DefaultWaveSeconds;

            var locator = player.Layout.Level?.RoomObjects.FirstOrDefault(o => o.Type == LocatorType);
            int cx = locator is not null ? locator.X : Math.Clamp(333, 1, Math.Max(1, player.Layout.GridW - 2));
            int cy = locator is not null ? locator.Y : Math.Clamp(333, 1, Math.Max(1, player.Layout.GridH - 2));
            float? cz = locator is not null && !float.IsNaN(locator.Z) ? locator.Z : null;
            var near = player.Layout.Near((cx, cy));
            // The army is planned now: its soldiers lie on the arena before the start.
            var rng = global::Game.AreaRng(player);
            int packSizePct = stats.GetValueOrDefault(Stat.MapLegionEndlessPackSizePlusPct);
            var plan = LegionArmy.PlanEndless(LegionData.Current, factions, level, packSizePct, rng);
            var slots = BuildRadialSlots(player, near.X, near.Y, ArenaRadius(player.Layout, near.X, near.Y), plan, rng);
            var saved = new SavedDomain(player.TakeObjectId(), 0,
                near.X, near.Y, cz, level, (int)DomainPhase.Waiting, factions,
                timerSec, waveSec, 0, 0, double.NegativeInfinity,
                0, 0, [], [], slots, player.TakeObjectId(), player.TakeObjectId(), TakeCrystalIds(player));
            state = Domains.GetValue(instance, _ => new DomainState(saved));
            lock (state) Keep(player, state);
            module.Log($"{PlayerOf(player).Character}: Domain of Timeless Conflict ({player.Layout.Id}, L{level}) ready at ({state.X}, {state.Y}) — {string.Join("+", factions)}, timer {timerSec:0}s, {slots.Length} soldiers planned");
        }

        else
        {
            // A Domain saved before its army was planned on entry: plan it now.
            var stats = LegionMonolith.AreaStats(player);
            var rng = global::Game.AreaRng(player);
            // The arena's radius walks the floor (thousands of cells): measured outside the lock, only when an army is to be planned.
            bool unplanned;
            int sx, sy;
            lock (state) (unplanned, sx, sy) = (state.Phase == DomainPhase.Waiting && state.Slots.Length == 0, state.X, state.Y);
            double arena = unplanned ? ArenaRadius(player.Layout, sx, sy) : 0;
            lock (state)
            {
                // A Domain saved with the old single crystal: the tracker's ids are taken now.
                if (state.Crystals.Length == 0 && CrystalSupported) { state.Crystals = TakeCrystalIds(player); Keep(player, state); }
                if (unplanned && state.Phase == DomainPhase.Waiting && state.Slots.Length == 0)
                {
                    var plan = LegionArmy.PlanEndless(LegionData.Current, state.Factions, state.Level,
                        stats.GetValueOrDefault(Stat.MapLegionEndlessPackSizePlusPct), rng);
                    state.Slots = BuildRadialSlots(player, state.X, state.Y, arena, plan, rng);
                    Keep(player, state);
                }
            }
        }

        // A Domain whose timer ran out while nobody was in it (the player died and left, or left): the end runs now, once, for whoever
        // comes in (a running one for the players already in it is the tick's). The area has no frames without a player. It is taken
        // first, so the objects and the rules are created in their final state (the end then does not repeat them).
        bool due;
        lock (state) due = TakeEnd(player, state, global::Game.AreaTime(player));
        await ShowObjects(player, state);
        await SyncRulesToPlayer(player, state);
        if (due) { await FinishDomain(module, player, state, announced: true); return; }
        await SpawnWaitingGhosts(module, player, state);
    }

    static uint[] TakeCrystalIds(Player player)
    {
        if (!CrystalSupported) return [];
        var ids = new uint[CrystalCount];
        for (int i = 0; i < ids.Length; i++) ids[i] = player.TakeObjectId();
        return ids;
    }

    /// <summary>Crystal i of the ring: evenly spaced angles, each on the nearest walkable cell.</summary>
    static (int X, int Y) CrystalAt(Player player, DomainState state, int i)
    {
        double angle = Math.Tau * i / CrystalCount;
        var at = (X: state.X + (int)Math.Round(Math.Cos(angle) * CrystalRingRadius), Y: state.Y + (int)Math.Round(Math.Sin(angle) * CrystalRingRadius));
        // Near never returns the cell it is given: only a blocked ring point moves.
        return player.Layout.Walkable?.Invoke(at.X, at.Y) != false ? at : player.Layout.Near(at);
    }

    /// <summary>Before the start: every reviving slot without a ghost gets one, lying ready (state 4).</summary>
    static async Task SpawnWaitingGhosts(IModuleContext module, Player player, DomainState state)
    {
        var missing = new List<int>();
        lock (state)
        {
            if (state.Phase != DomainPhase.Waiting) return;
            for (int i = 0; i < state.Slots.Length; i++)
                if (state.Slots[i] is { GhostId: 0, Alive: false } && !state.NoSpawn.Contains(i) && state.Spawning.Add(i))
                    missing.Add(i);
        }
        int made = 0, processed = 0;
        var lateWake = new List<int>();
        try
        {
            foreach (int i in missing)
            {
                processed++;
                if (await SpawnGhost(player, state, i, fall: false) == 0) continue;
                made++;
                lock (state)
                    if (state.Phase == DomainPhase.Active && state.Slots[i] is { GhostId: not 0, RiseStep: 0 })
                        lateWake.Add(i);
            }
        }
        finally
        {
            if (processed < missing.Count)
                lock (state)
                    for (int k = processed; k < missing.Count; k++) state.Spawning.Remove(missing[k]);
        }
        // Saved now: the tick flushes only a running Domain, and a reload before the start would lose every ghost's id.
        if (made > 0) lock (state) Keep(player, state);
        if (missing.Count > 0) module.Log($"Domain revive: {made}/{missing.Count} ghosts created lying before the start (state 4, the client poses them)");
        // If a player started the Domain while waiting ghosts were still spawning, wake every ghost StartDomain skipped.
        if (lateWake.Count > 0)
        {
            int woken = await StartRising(module, player, state, lateWake, revival: false);
            if (woken > 0) lock (state) Keep(player, state);
        }
    }

    static MonsterCatalog.Variety? VarietyOf(DomainState state, int monsterRow)
    {
        lock (state)
        {
            if (!state.VarietyCache.TryGetValue(monsterRow, out var variety))
                state.VarietyCache[monsterRow] = variety = Monsters.Find(monsterRow.ToString());
            return variety;
        }
    }

    /// <summary>Seconds after a ghost's create before its 0x2c8 state 3: the client must first have posed it (state 4's first frame),
    /// so the fall replays the death clip that pose chose.</summary>
    const double FallDelaySeconds = 0.15;

    /// <summary>Spawns the slot's ghost where the slot stands (the caller put the slot in Spawning), lying ready; with <paramref name="fall"/>
    /// (a fighter that fell) its 0x2c8 state 3 follows (<see cref="FallDelaySeconds"/>) with the rank's cooldown as its timer.</summary>
    static async Task<uint> SpawnGhost(Player player, DomainState state, int i, bool fall)
    {
        uint id = 0;
        try
        {
            FighterSlot slot; byte level;
            lock (state)
            {
                if (state.Phase == DomainPhase.Finished) return 0;
                (slot, level) = (state.Slots[i], (byte)Math.Clamp(state.Level, 1, 100));
            }
            if (VarietyOf(state, slot.MonsterRow) is not { Supported: true } variety)
            {
                lock (state) state.NoSpawn.Add(i);
                return 0;
            }
            ushort sequence = LegionRevive.NextSequence();
            id = await LegionRevive.SpawnGhost(player, variety, level, (MonsterRarity)slot.Rarity, slot.X, slot.Y, slot.Facing, sequence);
            if (id == 0) return 0;
            double now = global::Game.AreaTime(player);
            lock (state)
            {
                state.Slots[i] = state.Slots[i] with { GhostId = id, Sequence = sequence, RiseAt = double.NegativeInfinity, RiseStep = 0 };
                if (fall && state.Phase == DomainPhase.Active) state.Falls.Add((i, now + FallDelaySeconds));
                Keep(player, state, flush: false);
            }
            return id;
        }
        finally { lock (state) state.Spawning.Remove(i); }
    }

    /// <summary>Starts the revival of ghosts: 0x2c8 state 5 (vanish 0.8 s, then the client stands it up as its state 1). The tick sends
    /// state 1 and swaps the ghost for its fighter (<see cref="StepRising"/>).</summary>
    static async Task<int> StartRising(IModuleContext module, Player player, DomainState state, IReadOnlyList<int> slots, bool revival)
    {
        var send = new List<(int Slot, uint Ghost, ushort Sequence)>(slots.Count);
        double now = global::Game.AreaTime(player);
        lock (state)
        {
            if (state.Phase != DomainPhase.Active) return 0;
            foreach (int i in slots)
            {
                var s = state.Slots[i];
                if (s.GhostId == 0 || s.RiseStep != 0) continue;
                state.Slots[i] = s with { RiseAt = now, RiseStep = 1, RevivalCount = s.RevivalCount + (revival ? 1 : 0) };
                state.Rising.Add(i);
                send.Add((i, s.GhostId, s.Sequence));
            }
            Keep(player, state, flush: false);
        }
        byte[] payload = LegionRevive.RevivingPayload();
        var batch = new List<(uint, ushort, byte[])>(send.Count);
        foreach (var (_, ghost, sequence) in send)
        {
            LegionRevive.SetOrigin(player, ghost, sequence, LegionRevive.Reviving, LegionRevive.VanishSeconds, revive: true);
            batch.Add((ghost, sequence, payload));
        }
        await LegionRevive.SendBatch(player, batch);
        if (send.Count > 0)
            module.Log($"Domain revive: {send.Count} ghosts -> state 5 (vanish {LegionRevive.VanishSeconds} s){(send.Count == 1 ? $", 0x{send[0].Ghost:x} slot {send[0].Slot}" : "")}");
        return send.Count;
    }

    /// <summary>When state 1 follows state 5 (the client's own stand-up begins at 0.8 s; state 1 must not reach it earlier, or the
    /// ghost would skip the stand-up), and when the stood-up ghost (the client's state 2 at about 1.8 s) is swapped for its fighter.</summary>
    const double RevivedAfterSeconds = 1.0, SwapAfterSeconds = 2.0;
    /// <summary>Monster spawns per tick (a fighter for a stood-up ghost, a ghost for a fallen fighter, together), so the start's wave of
    /// every soldier spreads over a few frames.</summary>
    const int MaxSpawnsPerTick = 6;   // a spawn costs about 4 ms in the live log (24 per tick took 100-270 ms); GUESS: 6 keeps a tick near 30 ms

    /// <summary>What one tick takes to do; made only when something is due.</summary>
    sealed class ReviveWork
    {
        public readonly List<(int Slot, uint Ghost, ushort Sequence)> Revived = [];
        public readonly List<int> Swaps = [];
        public readonly List<(int Slot, uint Corpse)> Fallen = [];
        public readonly List<(int Slot, uint Ghost, ushort Sequence, float Timer)> Falls = [];
    }

    /// <summary>The tick's share of the revivals, deaths and falls (small sets; under lock(state)); null when nothing is due.</summary>
    static ReviveWork? TakeReviveWork(DomainState state, double now)
    {
        ReviveWork? work = null;
        int budget = MaxSpawnsPerTick;
        foreach (int i in state.Rising)
        {
            var s = state.Slots[i];
            if (s.RiseStep == 1 && now >= s.RiseAt + RevivedAfterSeconds)
            {
                state.Slots[i] = s with { RiseStep = 2 };
                (work ??= new()).Revived.Add((i, s.GhostId, s.Sequence));
            }
            else if (s.RiseStep == 2 && now >= s.RiseAt + SwapAfterSeconds && budget > 0)
            {
                (work ??= new()).Swaps.Add(i);
                budget--;
            }
        }
        if (work is not null)
            foreach (int i in work.Swaps) { state.Rising.Remove(i); state.Spawning.Add(i); }
        // A slot still being spawned waits in the queue for a later tick.
        for (int n = state.Fallen.Count; n > 0 && budget > 0; n--)
        {
            var (i, corpse) = state.Fallen.Dequeue();
            if (!state.Spawning.Add(i)) { state.Fallen.Enqueue((i, corpse)); continue; }
            (work ??= new()).Fallen.Add((i, corpse));
            budget--;
        }
        for (int k = state.Falls.Count - 1; k >= 0; k--)
        {
            var (i, at) = state.Falls[k];
            if (now < at) continue;
            state.Falls.RemoveAt(k);
            var s = state.Slots[i];
            if (s.GhostId == 0 || s.RiseStep != 0) continue;
            (work ??= new()).Falls.Add((i, s.GhostId, s.Sequence, (float)Math.Max(0, s.DeadAt + s.RevivalMs / 1000.0 - now)));
        }
        return work;
    }

    /// <summary>Does the work <see cref="TakeReviveWork"/> took: state 1 to the standing ghosts, the swap of a stood-up ghost for its
    /// fighter, a fallen fighter's corpse for its ghost, and the ghosts' falls.</summary>
    static async Task StepRising(IModuleContext module, Player player, DomainState state, ReviveWork work)
    {
        var (revived, swaps, fallen, falls) = (work.Revived, work.Swaps, work.Fallen, work.Falls);
        int swapsHandled = 0, fallenHandled = 0;
        try
        {
            if (revived.Count > 0)
            {
                byte[] payload = LegionRevive.RevivedPayload(LegionRevive.StandSeconds);
                var batch = new List<(uint, ushort, byte[])>(revived.Count);
                foreach (var (_, ghost, sequence) in revived)
                {
                    LegionRevive.SetOrigin(player, ghost, sequence, LegionRevive.Revived, LegionRevive.StandSeconds, revive: false);
                    batch.Add((ghost, sequence, payload));
                }
                await LegionRevive.SendBatch(player, batch);
                module.Log($"Domain revive: {revived.Count} ghosts -> state 1 (revived, beam){(revived.Count == 1 ? $", 0x{revived[0].Ghost:x}" : "")}");
            }
            if (swaps.Count > 0)
            {
                var stats = LegionMonolith.AreaStats(player);
                var ghosts = new List<uint>(swaps.Count);
                int made = 0;
                try
                {
                    foreach (int i in swaps)
                    {
                        uint ghost;
                        lock (state) { ghost = state.Slots[i].GhostId; state.Spawning.Remove(i); }
                        uint fighter = await SpawnSlot(player, state, i, stats);
                        if (fighter == 0)
                        {
                            // Ended meanwhile: the stood-up ghost goes as the fighters did. A variety that cannot spawn (SpawnSlotCore put it in
                            // NoSpawn): the ghost stays where it stood and the revival is not counted. Else (the runner was leaving the area, a
                            // spawn that failed) the swap is tried again on the next tick.
                            bool ended;
                            lock (state)
                            {
                                var s = state.Slots[i];
                                ended = state.Phase != DomainPhase.Active;
                                if (ended) state.Slots[i] = s with { GhostId = 0, RiseStep = 0 };
                                else if (state.NoSpawn.Contains(i)) state.Slots[i] = s with { RevivalCount = Math.Max(0, s.RevivalCount - 1) };
                                else { state.Slots[i] = s with { RiseStep = 2 }; state.Rising.Add(i); }
                            }
                            if (ended) ghosts.Add(ghost);
                            swapsHandled++;
                            continue;
                        }
                        made++;
                        ghosts.Add(ghost);
                        lock (state) state.Slots[i] = state.Slots[i] with { GhostId = 0, RiseAt = double.NegativeInfinity, RiseStep = 0 };
                        swapsHandled++;
                        if (swaps.Count == 1) module.Log($"Domain revive: ghost 0x{ghost:x} slot {i} -> fighter 0x{fighter:x}");
                    }
                }
                finally
                {
                    if (ghosts.Count > 0) await LegionRevive.Remove(player, ghosts);
                }
                if (swaps.Count > 1) module.Log($"Domain revive: {made}/{swaps.Count} ghosts swapped for their fighters");
            }
            if (fallen.Count > 0)
            {
                double now = global::Game.AreaTime(player);
                var corpses = new List<uint>(fallen.Count);
                foreach (var (i, corpse) in fallen)
                {
                    if (corpse != 0 && player.Area.Monsters.TryGetValue(corpse, out var dead))
                    {
                        var at = dead.Walk.Position(now);
                        lock (state) state.Slots[i] = state.Slots[i] with { X = at.X, Y = at.Y, Facing = (float)Math.Atan2(state.X - at.X, -(state.Y - at.Y)) };
                        corpses.Add(corpse);
                    }
                }
                await LegionRevive.Remove(player, corpses);
                var made = new List<uint>(fallen.Count);
                foreach (var (i, _) in fallen)
                {
                    if (await SpawnGhost(player, state, i, fall: true) is uint ghost and not 0)
                    {
                        made.Add(ghost);
                        fallenHandled++;
                        continue;
                    }
                    // Not made (the runner was leaving the area, a spawn that failed): tried again where the slot now stands.
                    lock (state)
                        if (state.Phase == DomainPhase.Active && !state.NoSpawn.Contains(i)) state.Fallen.Enqueue((i, 0));
                    fallenHandled++;
                }
                module.Log($"Domain revive: {fallen.Count} fallen -> {made.Count} ghosts lying (state 4{(made.Count == 1 ? $", 0x{made[0]:x}" : "")}), state 3 in {FallDelaySeconds:0.##} s");
            }
            if (falls.Count > 0)
            {
                await LegionRevive.SendBatch(player, [.. falls.Select(f => (f.Ghost, f.Sequence, LegionRevive.FallenPayload(f.Timer)))]);
                module.Log($"Domain revive: {falls.Count} ghosts -> state 3 (fall, cooldown){(falls.Count == 1 ? $", 0x{falls[0].Ghost:x} for {falls[0].Timer:0.#} s" : "")}");
            }
        }
        finally
        {
            if (swapsHandled < swaps.Count || fallenHandled < fallen.Count)
                lock (state)
                {
                    for (int k = swapsHandled; k < swaps.Count; k++)
                    {
                        int idx = swaps[k];
                        state.Spawning.Remove(idx);
                        if (state.Phase == DomainPhase.Active && state.Slots[idx] is { GhostId: not 0, RiseStep: 2 })
                            state.Rising.Add(idx);
                    }
                    for (int k = fallenHandled; k < fallen.Count; k++)
                    {
                        var (idx, corpse) = fallen[k];
                        state.Spawning.Remove(idx);
                        if (state.Phase == DomainPhase.Active && !state.NoSpawn.Contains(idx) && state.Slots[idx] is { Alive: false, GhostId: 0 })
                            state.Fallen.Enqueue((idx, corpse));
                    }
                }
        }
    }

    static async Task ShowObjects(Player player, DomainState state)
    {
        DomainPhase phase;
        lock (state) phase = state.Phase;
        int obeliskState = phase switch { DomainPhase.Waiting => 0, DomainPhase.Active => 1, _ => EndedObeliskState };

        if (InitiatorSupported && !player.Area.Objects.Has(state.InitiatorId)
            && BuildInitiatorObject(state.X, state.Y, state.Z, obeliskState, phase == DomainPhase.Active) is { } initObj)
        {
            var create = InstanceEntities.CreateObject(state.InitiatorId, InstanceEntities.ObjectTypeHash(InitiatorType), initObj.Blob());
            await LegionOutput.ToArea(player, async p =>
            {
                if (await global::Game.SendWorldCreate(PlayerOf(p), state.InitiatorId, create, $"0x02bb LegionEndlessInitiator 0x{state.InitiatorId:x}"))
                    await global::Game.SendWorldRecord(PlayerOf(p), state.InitiatorId, InstanceProtocol.EnterWakeZone(state.InitiatorId));
            });
            if (InitiatorInfo!.Blocking)
                foreach (var m in LegionOutput.Members(player))
                    m.SetBlocker(state.InitiatorId, state.X, state.Y, Math.Max(1, InitiatorInfo.Size));
        }
        if (InitiatorSupported && InitiatorInfo!.Blocking)
            player.SetBlocker(state.InitiatorId, state.X, state.Y, Math.Max(1, InitiatorInfo.Size));

        if (phase != DomainPhase.Finished)
        {
            await ShowZoneEffect(player, state.ZoneId, ControlZoneKey, state.X, state.Y);
            await ShowZoneEffect(player, state.PerimeterId, PerimeterKey, state.X, state.Y);
        }

        if (state.Crystals.Length == CrystalCount && CrystalSupported && !player.Area.Objects.Has(state.Crystals[0]))
        {
            // The ring is created once for the area (a later player gets it from the area), the earned ones already lit.
            int lit; lock (state) lit = LitCrystals(state.RewardProgress);
            var creates = new (uint Id, byte[] Create)[CrystalCount];
            var hash = InstanceEntities.ObjectTypeHash(RewardCrystalType);
            for (int i = 0; i < CrystalCount; i++)
            {
                var at = CrystalAt(player, state, i);
                creates[i] = (state.Crystals[i], InstanceEntities.CreateObject(state.Crystals[i], hash, BuildCrystalObject(at.X, at.Y, state.Z, i < lit)!.Blob()));
            }
            await LegionOutput.ToArea(player, async p =>
            {
                foreach (var (id, create) in creates)
                    if (await global::Game.SendWorldCreate(PlayerOf(p), id, create, null))
                        await global::Game.SendWorldRecord(PlayerOf(p), id, InstanceProtocol.EnterWakeZone(id));
            });
        }
    }

    /// <summary>Creates a lasting MiscAnimated effect object at the centre (once for the area; a later player gets it from the area).</summary>
    static async Task ShowZoneEffect(Player player, uint id, int miscAnimated, int x, int y)
    {
        if (id == 0 || player.Area.Objects.Has(id) || LegionData.Current.MiscAnimatedModels.GetValueOrDefault(miscAnimated) is not { } model) return;
        var create = InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(x, y, model).Blob());
        await LegionOutput.ToArea(player, async p =>
        {
            if (await global::Game.SendWorldCreate(PlayerOf(p), id, create, $"0x02bb MiscAnimated {miscAnimated} 0x{id:x}"))
                await global::Game.SendWorldRecord(PlayerOf(p), id, InstanceProtocol.EnterWakeZone(id));
        });
    }

    /// <summary>Every reward crystal's explode clip at 00:00 (the lit ones lit up first), one batch for the area.</summary>
    static async Task PlayCrystalExplode(Player player, DomainState state)
    {
        var ids = state.Crystals;
        if (ids.Length == 0 || !player.Area.Objects.Has(ids[0])) return;
        await LegionOutput.ToArea(player, async p =>
        {
            foreach (uint id in ids)
                await global::Game.SendWorldRecord(p, id, InstanceProtocol.AnimationUpdate(id, 0, CrystalExplodeClip, 1f), null);
        });
    }

    /// <summary>Plays control_zone.ao's revival_wave once, then its idle again.</summary>
    static async Task PlayRevivalWave(Player player, DomainState state)
    {
        uint id = state.ZoneId;
        if (id == 0 || !player.Area.Objects.Has(id)) return;
        await LegionOutput.ToArea(player, p => global::Game.SendWorldRecord(p, id, InstanceProtocol.AnimationUpdate(id, 0, ZoneWaveClip, 1f), $"0x01b2 control zone 0x{id:x}: revival_wave"));
        var instance = player.Instance;
        global::Game.SendAfter(player, ZoneWaveSeconds, () =>
            ResolveRunner(player, instance) is { } runner && runner.Area.Objects.Has(id)
                ? LegionOutput.ToArea(runner, p => global::Game.SendWorldRecord(p, id, InstanceProtocol.AnimationUpdate(id, 0, ZoneIdleClip, 1f), null))
                : Task.CompletedTask);
    }

    static async Task SyncRulesToPlayer(Player player, DomainState state)
    {
        DomainPhase phase; double startedAt, endsAt, timerSec;
        lock (state) (phase, startedAt, endsAt, timerSec) = (state.Phase, state.StartedAt, state.EndsAt, state.TimerSeconds);
        double now = global::Game.AreaTime(player);
        float elapsed = phase == DomainPhase.Active ? (float)Math.Clamp(now - startedAt, 0, timerSec) : 0f;
        byte stage = phase switch { DomainPhase.Active => 1, DomainPhase.Finished => 2, _ => 0 };
        byte step = phase switch { DomainPhase.Active => 2, DomainPhase.Finished => 5, _ => 0 };
        if (LegionRules.Begin(state.X, state.Y, (uint)state.Level, state.InitiatorId, elapsed, (float)timerSec, stage, step, endless: true) is { } begin)
            await PlayerOf(player).Send(begin, "0x02c9 LegionRules mode 0 (domain)");
        if (phase == DomainPhase.Active)
        {
            if (LegionRules.Activated(state.InitiatorId) is { } act) await PlayerOf(player).Send(act, "0x02c9 LegionRules mode 1 (domain)");
            if (LegionRules.State(state.InitiatorId, 1, 2) is { } st) await PlayerOf(player).Send(st, "0x02c9 LegionRules state (1,2 domain)");
            double left = Math.Ceiling(endsAt - now);
            if (left > 0 && left < 3600)
                await PlayerOf(player).Send(InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Legion, (uint)left), $"0x0163 mission timer Legion domain: {left} s");
        }
        else if (phase == DomainPhase.Finished && LegionRules.State(state.InitiatorId, 2, 5) is { } done)
            await PlayerOf(player).Send(done, "0x02c9 LegionRules state (2,5 domain)");
    }

    static bool AnyPlayerNear(Player player, DomainState state, double now, double radius)
    {
        double r2 = radius * radius;
        foreach (var member in LegionOutput.Members(player))
        {
            if (member.PlayerLife == 0) continue;
            var (px, py) = member.Walk.Position(now);
            double dx = px - state.X, dy = py - state.Y;
            if (dx * dx + dy * dy <= r2) return true;
        }
        return false;
    }

    static async Task<bool> Use(IModuleContext module, Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (StateOf(player) is not { } state || use.TargetId != state.InitiatorId) return false;
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return true;
        if (!await WorldOf(player).WalkUpTo(state.X, state.Y, (int)StartRadius, requestedAt)) return true;
        bool start = false;
        lock (state)
            if (state.Phase == DomainPhase.Waiting) start = true;
        if (start) await StartDomain(module, player, state);
        return true;
    }

    static async Task Tick(IModuleContext module, Player player)
    {
        if (player.Closed || player.LeavingArea || StateOf(player) is not { } state) return;
        double now = global::Game.AreaTime(player);
        int action = 0; // 1 = start, 2 = finish, 3 = revival wave
        List<int>? wave = null;
        ReviveWork? work = null;
        int litFrom = 0, litTo = 0;
        (string Variety, string Category, uint DelayMs)[]? speech = null;
        lock (state)
        {
            if (state.Speech.Count > 0) { speech = [.. state.Speech]; state.Speech.Clear(); }
            if (state.Chatter.Count > 0)
            {
                // One BattleCry/Dismount per Domain per ChatterSeconds, at random among those revived together.
                if (now - state.LastChatter >= LegionSpeech.ChatterSeconds)
                {
                    var (v, c) = state.Chatter[Random.Shared.Next(state.Chatter.Count)];
                    speech = [.. speech ?? [], (v, c, 0u)];
                    state.LastChatter = now;
                }
                state.Chatter.Clear();
            }
            if (state.Phase == DomainPhase.Active)
            {
                // The tracker: only the crystals that newly earned their 3 units are sent.
                litTo = LitCrystals(state.RewardProgress);
                if (litTo > state.LitSent) { litFrom = state.LitSent; state.LitSent = litTo; }
                else litTo = 0;
            }
            if (state.Phase == DomainPhase.Waiting)
            {
                if (state.Dirty && now - state.LastFlushAt >= DomainFlushSeconds) Keep(player, state, flush: true);
                if (AnyPlayerNear(player, state, now, StartRadius)) action = 1;
            }
            else if (state.Phase == DomainPhase.Active)
            {
                if (TakeEnd(player, state, now)) action = 2;
                else
                {
                    if (state.Rising.Count > 0 || state.Fallen.Count > 0 || state.Falls.Count > 0)
                        work = TakeReviveWork(state, now);
                    // The JSON save waits for a tick with no spawns (the backlog drains in a few ticks).
                    if (work is null || work.Swaps.Count + work.Fallen.Count == 0)
                        if (state.Dirty && now - state.LastFlushAt >= DomainFlushSeconds) Keep(player, state, flush: true);
                    // 3.29: a wave only while a player stands in the zone (no wave without one), at most every WaveSeconds; the slots
                    // are scanned only then.
                    if (now - state.LastWaveAt >= state.WaveSeconds && now >= state.NextWaveCheck)
                    {
                        if (!AnyPlayerNear(player, state, now, ZoneRadius)) state.NextWaveCheck = now + ZoneCheckSeconds;
                        else
                        {
                            var ready = new List<(int Slot, int Tier)>();
                            for (int i = 0; i < state.Slots.Length; i++)
                                if (IsReady(state, i, now)) ready.Add((i, state.Slots[i].Tier));
                            if (ready.Count == 0) state.NextWaveCheck = now + WaveRetrySeconds;
                            else
                            {
                                wave = PickWave(ready, global::Game.AreaRng(player));
                                state.LastWaveAt = now;
                                state.Waves++;
                                Keep(player, state);
                                action = 3;
                            }
                        }
                    }
                }
            }
        }
        if (speech is not null)
            foreach (var (variety, category, delayMs) in speech) await LegionSpeech.Say(player, variety, category, delayMs);
        if (litTo > litFrom) await LightCrystals(player, state, litFrom, litTo);
        if (work is not null)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            await StepRising(module, player, state, work);
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (ms > 30) module.Log($"Domain revive tick slow: {ms:0} ms ({work.Swaps.Count} swaps, {work.Fallen.Count} fallen, {work.Revived.Count} revived)");
        }
        if (action == 1) await StartDomain(module, player, state);
        else if (action == 2) await FinishDomain(module, player, state);
        else if (action == 3) await Wave(module, player, state, wave!);
    }

    /// <summary>The 3.29 revival wave ("returning to the monolith respawns half the killed enemies, boss spawns are limited by a
    /// cooldown"): every ready sergeant and general (their rank's cooldown is their readiness), and half the ready normal soldiers,
    /// at least one, drawn at random.</summary>
    internal static List<int> PickWave(IReadOnlyList<(int Slot, int Tier)> ready, Random rng)
    {
        var wave = new List<int>();
        var normal = new List<int>();
        foreach (var (slot, tier) in ready)
            if (tier >= 2) wave.Add(slot); else normal.Add(slot);
        int count = normal.Count == 0 ? 0 : Math.Max(1, (normal.Count + 1) / 2);
        for (int i = 0; i < count; i++)
        {
            int pick = rng.Next(normal.Count);
            wave.Add(normal[pick]);
            normal.RemoveAt(pick);
        }
        return wave;
    }

    /// <summary>A revival wave: the picked ghosts stand up where they lie (StartRising).</summary>
    static async Task Wave(IModuleContext module, Player player, DomainState state, List<int> wave)
    {
        int bosses;
        lock (state) bosses = wave.Count(i => state.Slots[i].Tier >= 2);
        await PlayRevivalWave(player, state);
        int rising = await StartRising(module, player, state, wave, revival: true);
        module.Log($"Domain revival wave #{state.Waves}: {rising} ghosts rising ({bosses} sergeants/generals, {wave.Count - bosses} soldiers)");
    }

    /// <summary>A slot is ready to revive when it is dead long enough, is not being spawned right now and can be spawned at all.
    /// Call under lock(state).</summary>
    static bool IsReady(DomainState state, int i, double now)
    {
        var s = state.Slots[i];
        return s is { Alive: false, GhostId: not 0, RiseStep: 0 } && now - s.DeadAt >= s.RevivalMs / 1000.0
            && !state.Spawning.Contains(i) && !state.NoSpawn.Contains(i);
    }

    static async Task UpdateInitiatorState(Player player, DomainState state, int obeliskState, bool checkingControlZone)
    {
        if (!InitiatorSupported || !player.Area.Objects.Has(state.InitiatorId)) return;
        var sm = InitiatorDefaults!.Set("obelisk_state", obeliskState).Set("checking_control_zone", checkingControlZone ? 1 : 0);
        await LegionOutput.ToArea(player, p => global::Game.SendWorldRecord(PlayerOf(p), state.InitiatorId,
            InstanceEntities.StateMachineUpdate(state.InitiatorId, (byte)InitiatorStateIndex, sm), null));
    }

    /// <summary>Lights crystals [from, to) (active = 1): only the ones that changed are sent, in one batch.</summary>
    static async Task LightCrystals(Player player, DomainState state, int from, int to)
    {
        if (!CrystalSupported || state.Crystals.Length != CrystalCount || to <= from || !player.Area.Objects.Has(state.Crystals[0])) return;
        var sm = CrystalDefaults!.Set("active", 1);
        await LegionOutput.ToArea(player, async p =>
        {
            for (int i = from; i < to; i++)
                await global::Game.SendWorldRecord(PlayerOf(p), state.Crystals[i],
                    InstanceEntities.StateMachineUpdate(state.Crystals[i], (byte)CrystalStateIndex, sm), null);
        });
    }

    static async Task PlayEffect(Player player, string? model, int x, int y, float scale = 1f)
    {
        if (string.IsNullOrEmpty(model)) return;
        uint id = player.TakeObjectId();
        var instance = player.Instance;
        bool created = false;
        await LegionOutput.ToArea(player, async p =>
        {
            if (!await global::Game.SendWorldCreate(PlayerOf(p), id, InstanceEntities.CreateObject(id,
                InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(x, y, model) { Scale = scale }.Blob()), null)) return;
            created = true;
            await global::Game.SendWorldRecord(PlayerOf(p), id, InstanceProtocol.EnterWakeZone(id));
        });
        if (created)
            global::Game.SendAfter(player, EffectLifetimeSeconds, () =>
                ResolveRunner(player, instance) is { } runner ? LegionOutput.Remove(runner, id) : Task.CompletedTask);
    }

    static Player? ResolveRunner(Player fallback, InstanceState? instance)
    {
        if (!fallback.Closed && !fallback.LeavingArea && ReferenceEquals(fallback.Instance, instance)) return fallback;
        if (instance is null) return null;
        foreach (var p in instance.PlayersExcept(0))
            if (!p.Closed && !p.LeavingArea && ReferenceEquals(p.Instance, instance)) return p;
        return null;
    }

    static async Task StartDomain(IModuleContext module, Player player, DomainState state)
    {
        FighterSlot[] plannedSlots;
        var rising = new List<int>();
        double now = global::Game.AreaTime(player);
        lock (state)
        {
            if (state.Phase != DomainPhase.Waiting) return;
            plannedSlots = state.Slots;
            state.Phase = DomainPhase.Active;
            state.StartedAt = now;
            state.EndsAt = now + state.TimerSeconds;
            state.LastWaveAt = now;
            for (int i = 0; i < plannedSlots.Length; i++)
            {
                plannedSlots[i] = plannedSlots[i] with { DeadAt = now };
                rising.Add(i);
            }
            state.LiveByMonster.Clear();
            // Each general says its MapIntro once, a while after the start (the tick says it).
            uint introDelay = LegionSpeech.DomainIntroDelayMs;
            var introduced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in plannedSlots)
                if (s.Tier == 3 && introduced.Add(s.FactionId) && VarietyOf(state, s.MonsterRow) is { } general)
                {
                    state.Speech.Add((general.Id, "MapIntro", introDelay));
                    introDelay += LegionSpeech.DomainIntroStaggerMs;
                }
            Keep(player, state, flush: false);   // saved once, after the wake below (not twice: 165 slots a time)
        }
        long startedTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        foreach (var m in LegionOutput.Members(player))
        {
            if (LegionRules.Activated(state.InitiatorId) is { } act) await PlayerOf(m).Send(act, "0x02c9 LegionRules mode 1 (domain)");
            if (LegionRules.State(state.InitiatorId, 1, 2) is { } st) await PlayerOf(m).Send(st, "0x02c9 LegionRules state (1,2 domain)");
            await PlayerOf(m).Send(InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Legion, (uint)Math.Ceiling(state.TimerSeconds)),
                $"0x0163 mission timer Legion domain: {state.TimerSeconds:0} s");
        }
        // The client shows the zone (counter.epk on a player within 60 cells) while checking_control_zone is 1: from now to the end.
        await UpdateInitiatorState(player, state, obeliskState: 1, checkingControlZone: true);
        await PlayRevivalWave(player, state);

        // Every soldier wakes at once: its ghost stands up and becomes its fighter (StepRising).
        int woken = await StartRising(module, player, state, rising, revival: false);
        lock (state) Keep(player, state);
        module.Log($"Domain started: {string.Join("+", state.Factions)} ({woken}/{plannedSlots.Length} soldiers waking, timer {state.TimerSeconds:0}s) in {System.Diagnostics.Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds:0} ms");
    }

    /// <summary>GUESS (R17/R18: the army fills the disc out to the crystal lobes, about 3 character lengths round the monolith stay clear): the clear radius, in cells.</summary>
    const double DomainInnerClear = 18;
    /// <summary>GUESS: the arena radius is the one holding this share of the walkable cells within DomainSearchRadius of the centre.</summary>
    const double DomainArenaShare = 0.9;
    /// <summary>GUESS: how far the arena is looked for, in cells.</summary>
    const int DomainSearchRadius = 300;
    /// <summary>GUESS: the arena radius without a walk map, in cells (encounter_1.arm: 90 % of the floor within 196).</summary>
    const double DomainArenaFallback = 200;
    /// <summary>How many times a soldier's spot is drawn again (angle and radius) when it falls off the floor, before the nearest
    /// walkable cell is taken (Near drifts it towards the centre or a wall).</summary>
    const int DomainPlaceTries = 8;

    /// <summary>The radius holding DomainArenaShare of the walkable cells round (cx, cy), once per Domain (every 2nd cell is counted).</summary>
    static double ArenaRadius(AreaLayout layout, int cx, int cy)
    {
        if (layout.Walkable is not { } walk) return DomainArenaFallback;
        var byRadius = new int[DomainSearchRadius + 1];
        int total = 0;
        for (int dy = -DomainSearchRadius; dy <= DomainSearchRadius; dy += 2)
            for (int dx = -DomainSearchRadius; dx <= DomainSearchRadius; dx += 2)
            {
                int d2 = dx * dx + dy * dy;
                if (d2 > DomainSearchRadius * DomainSearchRadius || !walk(cx + dx, cy + dy)) continue;
                byRadius[(int)Math.Sqrt(d2)]++;
                total++;
            }
        if (total < 100) return DomainArenaFallback;
        int acc = 0;
        for (int r = 0; r <= DomainSearchRadius; r++)
            if ((acc += byRadius[r]) >= total * DomainArenaShare)
                return Math.Max(r, DomainInnerClear + 40);
        return DomainSearchRadius;
    }

    /// <summary>The Domain's soldiers on the arena's disc: each faction in its sector (the general on its middle line first), each squad's
    /// depth % mapped onto [DomainInnerClear, <paramref name="arena"/>] by area (dist = sqrt(lerp(inner^2, arena^2, frac)), so the outer
    /// rings get their share). A spot off the floor is drawn again (DomainPlaceTries), then moved to the nearest walkable cell.</summary>
    static FighterSlot[] BuildRadialSlots(Player player, int cx, int cy, double arena, LegionArmy.ArmyPlan plan, Random rng)
    {
        var list = new List<FighterSlot>();
        int fCount = Math.Max(1, plan.Factions.Length);
        double sectorSpan = Math.Tau / fCount;
        double baseAngle = rng.NextDouble() * Math.Tau;
        double inner2 = DomainInnerClear * DomainInnerClear, outer2 = Math.Max(arena, DomainInnerClear + 1) * Math.Max(arena, DomainInnerClear + 1);
        var walkable = player.Layout.Walkable;
        for (int f = 0; f < plan.Factions.Length; f++)
        {
            var faction = plan.Factions[f];
            double centerAngle = baseAngle + f * sectorSpan;
            foreach (var squad in faction.Squads.OrderByDescending(q => q.Tier))
            {
                for (int i = 0; i < squad.Count; i++)
                {
                    (int X, int Y) Draw(bool first)
                    {
                        double frac = squad.FromPct == squad.ToPct
                            ? squad.FromPct / 100.0
                            : (squad.FromPct + rng.NextDouble() * Math.Max(1, squad.ToPct - squad.FromPct)) / 100.0;
                        double dist = Math.Sqrt(inner2 + Math.Clamp(frac, 0, 1) * (outer2 - inner2));
                        double angle = squad.Tier == 3 && first
                            ? centerAngle
                            : centerAngle + (rng.NextDouble() - 0.5) * sectorSpan * (squad.Tier == 3 ? 0.25 : 0.78);
                        return (cx + (int)Math.Round(Math.Cos(angle) * dist), cy + (int)Math.Round(Math.Sin(angle) * dist));
                    }
                    var at = Draw(first: true);
                    for (int t = 1; t < DomainPlaceTries && walkable?.Invoke(at.X, at.Y) == false; t++) at = Draw(first: false);
                    if (walkable?.Invoke(at.X, at.Y) == false)
                    {
                        at = player.Layout.Near(at);
                        // Near can drift onto the central crystal: keep the clear ring.
                        if ((at.X - cx) * (at.X - cx) + (at.Y - cy) * (at.Y - cy) < inner2)
                        {
                            double away = Math.Atan2(at.Y - cy, at.X - cx);
                            at = player.Layout.Near((cx + (int)Math.Round(Math.Cos(away) * (DomainInnerClear + 2)), cy + (int)Math.Round(Math.Sin(away) * (DomainInnerClear + 2))));
                        }
                    }
                    float facing = (float)Math.Atan2(cx - at.X, -(cy - at.Y));
                    int rarity = squad.Rarities is { } rs && i < rs.Length ? rs[i] : LegionArmy.TierRarity(squad.Tier);
                    int[]? rewards = squad.Rewards is { } rw && i < rw.Length && rw[i].Length > 0 ? rw[i] : null;
                    int revivalMs = squad.RevivalMs > 0 ? squad.RevivalMs : squad.Tier switch { 3 => 24000, 2 => 16000, _ => 6000 };
                    list.Add(new FighterSlot(list.Count, faction.Id, squad.Tier, squad.Monster, rarity,
                        at.X, at.Y, facing, revivalMs, rewards));
                }
            }
        }
        return [.. list];
    }

    /// <summary>Spawns one slot; a slot already being spawned is left alone, and a failed spawn waits its revival time again.</summary>
    static async Task<uint> SpawnSlot(Player player, DomainState state, int slotIndex, IReadOnlyDictionary<uint, int> areaStats)
    {
        lock (state)
            if (!state.Spawning.Add(slotIndex)) return 0;
        uint id = 0;
        try { id = await SpawnSlotCore(player, state, slotIndex, areaStats); }
        finally
        {
            lock (state)
            {
                state.Spawning.Remove(slotIndex);
                if (id == 0 && slotIndex < state.Slots.Length && !state.Slots[slotIndex].Alive)
                    state.Slots[slotIndex] = state.Slots[slotIndex] with { DeadAt = global::Game.AreaTime(player) };
            }
        }
        return id;
    }

    static async Task<uint> SpawnSlotCore(Player player, DomainState state, int slotIndex, IReadOnlyDictionary<uint, int> areaStats)
    {
        FighterSlot slot;
        byte areaLevel;
        lock (state)
        {
            if (state.Phase != DomainPhase.Active || slotIndex < 0 || slotIndex >= state.Slots.Length) return 0;
            slot = state.Slots[slotIndex];
            if (slot.Alive) return slot.CurrentId;
            areaLevel = (byte)Math.Clamp(state.Level, 1, 100);
        }
        // A general that starts mounted rises mounted at every revival (Aukuna, E45; R18: she dismounts again in the Domain, an inference);
        // the slot keeps her form on foot, whose ghost lies where she falls.
        var variety = VarietyOf(state, LegionData.Current.SpawnRow(slot.MonsterRow));
        if (variety is not { Supported: true })
        {
            lock (state) state.NoSpawn.Add(slotIndex);   // never ready: no pulse, no revival count, no save for it
            return 0;
        }
        if (player.Closed || player.LeavingArea) return 0;
        uint id = await SpawnFighter(player, slot, variety, areaLevel, slot.X, slot.Y, slot.Facing, areaStats);
        if (id == 0) return 0;

        bool ended = false;
        lock (state)
        {
            if (state.Phase != DomainPhase.Active) ended = true;
            else
            {
                state.Slots[slotIndex] = slot with { CurrentId = id, Alive = true };
                state.LiveByMonster[id] = slotIndex;
                // A revived general says a BattleCry (Aukuna her Dismount line when her mount falls: LegionGenerals); one such line per
                // Domain per 30 s (GUESS).
                if (slot.Tier == 3 && slot.RevivalCount > 0) state.Chatter.Add((variety.Id, "BattleCry"));
            }
        }
        if (ended)
        {
            // Encounter ended while spawning — remove from both server state and all connected clients.
            await LegionRevive.Remove(player, [id]);
            return 0;
        }
        if (slot.Tier == 3) await LegionGenerals.Woken(player, id);
        return id;
    }

    /// <summary>A slot's fighting monster of <paramref name="variety"/> at (x, y), with the slot's reward icon and the per-revival toughness;
    /// 0 when it could not be made.</summary>
    static async Task<uint> SpawnFighter(Player player, FighterSlot slot, MonsterCatalog.Variety variety, byte areaLevel, int x, int y, float rotation,
        IReadOnlyDictionary<uint, int> areaStats)
    {
        IReadOnlyList<(uint, int)>? extraStats = LegionStasis.RewardIcon(slot.Rewards) is int icon
            ? [(Stat.MonsterMinimapIcon, icon)] : null;
        // The per-revival toughness goes in the spawn's mods, so the client's values are right from the create.
        int revivalToughness = RevivalToughness(areaStats, slot.RevivalCount);
        MonsterModSet? revivalMods = revivalToughness > 0
            ? new([], [], new MonsterModEffects(new Dictionary<uint, int> { [global::Game.MonsterToughness] = revivalToughness }))
            : null;
        // Its variety's own states: no stasis shape (the breakout / activated outlines are the map monolith's); in the Domain the
        // client hides the crystal shell itself (LegionMonsterBase.ot: IfMapStat map_is_legion_endgame_map, hide_segments).
        uint id = await SpawnMonster(player, variety, areaLevel, (MonsterRarity)slot.Rarity, x, y,
            friendly: false, unrisen: false, alone: false, instanceOwned: true, bulk: true, log: false,
            placement: MonsterPlacement.Established, rotation: rotation, extraStats: extraStats, extraMods: revivalMods);
        if (id == 0 || !player.Area.Monsters.TryGetValue(id, out var monster)) return 0;
        if (slot.RevivalCount > 0 && RevivalStats(areaStats, slot.RevivalCount) is { } revivalStats)
            monster.EncounterStats = revivalStats;
        // No reward symbols (buff 1328) over the Domain's monsters (3.29.0); their rewards still bank (RecordKill).
        return id;
    }

    /// <summary>Whether the monster is a running Domain's fighter (not removed by its end).</summary>
    public static bool Fights(Player player, uint id)
    {
        if (StateOf(player) is not { } state) return false;
        lock (state) return state.Phase == DomainPhase.Active && state.LiveByMonster.ContainsKey(id);
    }

    /// <summary>Aukuna's mount fell (LegionGenerals): her form on foot takes the slot's place where it fell and says her Dismount line (in the
    /// Domain's chatter). 0 when the Domain has ended or the mount is no longer the slot's fighter. Without a form on foot (null, or its
    /// spawn failed) the slot falls where the mount did, so its ghost lies there and revives.</summary>
    public static async Task<uint> Dismounted(Player player, uint mountId, MonsterCatalog.Variety? foot, int x, int y, float rotation)
    {
        if (StateOf(player) is not { } state) return 0;
        FighterSlot slot;
        byte areaLevel;
        lock (state)
        {
            if (state.Phase != DomainPhase.Active || !state.LiveByMonster.TryGetValue(mountId, out int idx)) return 0;
            slot = state.Slots[idx];
            areaLevel = (byte)Math.Clamp(state.Level, 1, 100);
        }
        uint id = foot is null ? 0 : await SpawnFighter(player, slot, foot, areaLevel, x, y, rotation, LegionMonolith.AreaStats(player));
        bool kept = false;
        lock (state)
        {
            int idx = -1;
            bool live = state.Phase == DomainPhase.Active && state.LiveByMonster.Remove(mountId, out idx);
            if (live && id != 0)
            {
                kept = true;
                state.LiveByMonster[id] = idx;
                state.Slots[idx] = state.Slots[idx] with { CurrentId = id, Alive = true };
                state.Chatter.Add((foot!.Id, "Dismount"));
                Keep(player, state, flush: false);
            }
            else if (live)
            {
                state.Slots[idx] = state.Slots[idx] with { CurrentId = 0, Alive = false, DeadAt = global::Game.AreaTime(player), X = x, Y = y };
                state.Fallen.Enqueue((idx, 0));
                Keep(player, state, flush: false);
            }
        }
        if (!kept && id != 0) await LegionRevive.Remove(player, [id]);
        return kept ? id : 0;
    }

    static bool RecordKill(Player player, DomainState state, uint monsterId, out FighterSlot? killedSlot)
    {
        killedSlot = null;
        if (LegionGenerals.IsMount(player, monsterId)) return false;   // the slot's general fights on, on foot (LegionGenerals)
        lock (state)
        {
            if (!state.LiveByMonster.Remove(monsterId, out int idx)) return false;
            var slot = state.Slots[idx];
            if (!slot.Alive) return false;
            killedSlot = slot;
            if (state.RecentKills.Count >= 256) state.RecentKills.Clear();
            state.RecentKills[monsterId] = slot;
            state.Slots[idx] = slot with { CurrentId = 0, Alive = false, DeadAt = global::Game.AreaTime(player) };
            // Its corpse becomes its ghost on the next tick (not here: the kill is still being committed).
            if (state.Phase == DomainPhase.Active) state.Fallen.Enqueue((idx, monsterId));
            state.TotalKills++;
            if (slot.Tier == 3)
            {
                state.GeneralKills[slot.FactionId] = state.GeneralKills.GetValueOrDefault(slot.FactionId) + 1;
                if (state.Phase == DomainPhase.Active && VarietyOf(state, slot.MonsterRow) is { } general) {
                    // One Defeat line per general per DefeatLineSeconds (GUESS: generals revive and die over and over); lines stay 2.5 s apart.
                    if (!state.DefeatSaid.TryGetValue(general.Id, out double said) || global::Game.AreaTime(player) - said >= DefeatLineSeconds)
                    {
                        state.DefeatSaid[general.Id] = global::Game.AreaTime(player);
                        state.Speech.Add((general.Id, "Defeat", 2500u * (uint)state.Speech.Count(l => l.Category == "Defeat")));
                    }
                }
            }
            if (slot.Rewards is { Length: > 0 })
            {
                state.BankedRewards.AddRange(slot.Rewards);
                if (state.RewardBonusPct < 0)
                {
                    var stats = LegionMonolith.AreaStats(player);
                    state.RewardBonusPct = Math.Max(0, stats.GetValueOrDefault(Stat.UberDomainMonsterRewardChancePlusPct)
                        + stats.GetValueOrDefault(Stat.MapLegionMonsterRewardChancePlusPct));
                }
                state.RewardProgress += slot.Rewards.Length * (100 + state.RewardBonusPct) / 100.0;
            }
            Keep(player, state, flush: false);
            return true;
        }
    }

    static void Drops(DropRoll drop)
    {
        if (!IsDomainArea(drop.Player.Layout.Id) || StateOf(drop.Player) is not { } state) return;
        // Whichever of Killed/Drops runs first records the kill; this one takes the slot it left, so the loot is cancelled either way.
        FighterSlot? slot;
        lock (state)
        {
            if (!state.RecentKills.Remove(drop.Monster.ObjectId, out var seen))
            {
                if (!RecordKill(drop.Player, state, drop.Monster.ObjectId, out slot) || slot is null) return;
                state.RecentKills.Remove(drop.Monster.ObjectId);
            }
            else slot = seen;
        }
        // Domain of Timeless Conflict holds all item rewards until 00:00; only Generals drop boosted gold during combat.
        drop.Cancel = true;
        if (slot.Tier == 3 && GameEvents.AllowsLoot(drop.Player) && Environment.GetEnvironmentVariable("Game_DROPS") != "0")
        {
            var stats = LegionMonolith.AreaStats(drop.Player);
            int generalGoldPct = stats.GetValueOrDefault(Stat.MapLegionEndlessGeneralsDropGoldPlusPct);
            double goldFactor = Program.LeagueFor(drop.Player).Rules.GoldMultiplier
                * SystemSettings.Default.GoldDropMultiplier
                * Math.Max(1.0, (100 + generalGoldPct) / 100.0);
            if (global::Game.DropsFor(drop.Player).Gold(drop.Monster.Level, MonsterRarity.Unique, global::Game.AreaRng(drop.Player), goldFactor) is { } gold)
                drop.Made.Add(gold.Item);
        }
    }

    static Task Killed(MonsterKill kill)
    {
        if (!IsDomainArea(kill.Player.Layout.Id) || StateOf(kill.Player) is not { } state) return Task.CompletedTask;
        RecordKill(kill.Player, state, kill.Monster.ObjectId, out _);
        return Task.CompletedTask;
    }

    /// <summary>When a running Domain's timer has run out: marks it finished (so the end pays once) and saves it; true for the one
    /// caller that must run <see cref="FinishDomain"/>. Call under lock(state).</summary>
    static bool TakeEnd(Player player, DomainState state, double now)
    {
        if (state.Phase != DomainPhase.Active || now < state.EndsAt) return false;
        state.Phase = DomainPhase.Finished;
        Keep(player, state);
        return true;
    }

    /// <param name="announced">The newcomer who runs the end was already created in the final state (the obelisk, the (2,5) rules state).</param>
    static async Task FinishDomain(IModuleContext module, Player player, DomainState state, bool announced = false)
    {
        var liveIds = new List<uint>();
        List<int> bankedRewards;
        Dictionary<string, int> generalKills;
        int totalKills, resets, factionCount, level;
        lock (state)
        {
            foreach (var (mid, idx) in state.LiveByMonster)
            {
                liveIds.Add(mid);
                state.Slots[idx] = state.Slots[idx] with { CurrentId = 0, Alive = false };
            }
            state.LiveByMonster.Clear();
            // Live monsters vanish at the end; lying ghosts stay (W8). A ghost that was standing up goes with the fighters.
            foreach (int i in state.Rising)
            {
                if (state.Slots[i].GhostId != 0) liveIds.Add(state.Slots[i].GhostId);
                state.Slots[i] = state.Slots[i] with { GhostId = 0, RiseAt = double.NegativeInfinity, RiseStep = 0 };
            }
            state.Rising.Clear();
            state.Fallen.Clear();
            state.Falls.Clear();
            state.RecentKills.Clear();
            bankedRewards = [.. state.BankedRewards];
            generalKills = new(state.GeneralKills, StringComparer.Ordinal);
            totalKills = state.TotalKills;
            resets = state.Waves;
            factionCount = state.Factions.Length;
            level = state.Level;
            Keep(player, state);
        }

        // Hide the timer and end the ruleset. The arena's own environment (Environments 1177) stays: it is the base one, not ours to fade.
        foreach (var member in LegionOutput.Members(player))
        {
            await PlayerOf(member).Send(InstanceTimers.HideMissionTimer(InstanceTimers.MissionTimerType.Legion), "0x0163 mission timer Legion domain: hidden");
            if (!announced && LegionRules.State(state.InitiatorId, 2, 5) is { } done) await PlayerOf(member).Send(done, "0x02c9 LegionRules state (2,5 domain ended)");
        }

        // Remove all surviving Domain fighters cleanly so no post-timer combat or loot continues.
        var removed = new List<uint>(liveIds.Count);
        foreach (uint mid in liveIds)
            if (player.Area.Monsters.Remove(mid, out var m))
            {
                m.Dead = true; m.Life = 0; m.ActionGeneration++;
                player.Instance.World.MonsterOrigins.TryRemove(mid, out _);
                removed.Add(mid);
            }
        if (removed.Count > 0)
        {
            var members = LegionOutput.Members(player).ToList();
            if (members.Count == 0)
                foreach (uint mid in removed) LegionOutput.ForgetComponents(player, mid);
            else
                await LegionOutput.ToArea(members, async p =>
                {
                    foreach (uint mid in removed) await global::Game.SendWorldRemoval(p, mid);
                });
        }

        await UpdateInitiatorState(player, state, obeliskState: EndedObeliskState, checkingControlZone: false);
        foreach (uint zone in new[] { state.ZoneId, state.PerimeterId })
            if (zone != 0 && player.Area.Objects.Has(zone)) await LegionOutput.Remove(player, zone);
        int litFrom, litTo;
        lock (state) { litFrom = state.LitSent; litTo = LitCrystals(state.RewardProgress); state.LitSent = Math.Max(litFrom, litTo); }
        await LightCrystals(player, state, litFrom, litTo);
        await PlayCrystalExplode(player, state);
        await PlayEffect(player, PulseModel, state.X, state.Y, scale: 6f);
        if (LegionData.Current.MiscAnimatedModels.GetValueOrDefault(SoulercoasterKey) is { } soulAo)
            await PlayEffect(player, soulAo, state.X, state.Y);

        int dropped = 0;
        if (GameEvents.AllowsLoot(player) && Environment.GetEnvironmentVariable("Game_DROPS") != "0" && (totalKills > 0 || bankedRewards.Count > 0))
        {
            var rng = global::Game.AreaRng(player);
            var loot = LegionRewards.RollDomainRewards(player, bankedRewards, generalKills, totalKills, factionCount, level, rng);
            var catalog = global::Game.ItemsFor(player);
            for (int i = 0; i < loot.Count; i++)
            {
                double angle = i * 2.399963229728653 + rng.NextDouble() * 0.2;
                double radius = 12.0 + 1.8 * Math.Sqrt(i);
                var at = player.Layout.Near((state.X + (int)Math.Round(Math.Cos(angle) * radius), state.Y + (int)Math.Round(Math.Sin(angle) * radius)));
                var ground = new WorldItemObject(at.X, at.Y, loot[i]);
                uint id = player.Instance.Ground.Add(ground, player);
                string name = catalog.ByHash(loot[i].TypeHash)?.Name ?? "item";
                await global::Game.SendGroundItem(player, id, ground, $"Domain reward: {name} x{loot[i].StackSize}", justDropped: true);
                dropped++;
            }
        }

        // The real game prints no summary line: the log only.
        int generalsSlain = generalKills.Values.Sum();
        string summary = $"Domain of Timeless Conflict complete: {totalKills} slain ({generalsSlain} Generals), {resets} revival waves, {dropped} rewards dropped.";
        module.Log(summary);
    }
}
