using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Legion;

/// <summary>
/// Phase 3: Domain of Timeless Conflict (<c>WorldAreas</c> <c>LegionLeague</c>..<c>LegionLeague4</c>, 2–5 emblems).
/// Resolves 2–5 normal or Unrelenting Timeless Emblems in the Map Device (<see cref="ResolveOpening"/>), claims the
/// <c>"Legion"</c> scarab content tag for standard maps, places the central <see cref="InitiatorType"/> (ControlZone)
/// and <see cref="RewardCrystalType"/> on <see cref="GameEvents.PlayerEntered"/> (Domain areas have 0 MonsterPacks),
/// drives the endless radial armies with per-rank revival cooldowns (<c>MinLevel = 101</c>: 6 s / 16 s / 24 s), manual
/// ControlZone resets and 20 s auto-pulses, Unrelenting per-revival monster scaling, reward accumulation, and the
/// end-of-timer Timeless Jewel / Stasis Prison / reward burst around the central crystal.
/// </summary>
static class LegionDomain
{
    public const string LocatorType = "Metadata/Terrain/Leagues/Legion/Objects/LegionEndgame_EncounterLocator";
    public const string InitiatorType = "Metadata/Terrain/Leagues/Legion/Objects/LegionEndlessInitiator";
    public const string RewardCrystalType = "Metadata/Terrain/Leagues/Legion/Objects/LegionDomainRewardCrystal";
    public const string EnteredQuestFlag = "EnteredDomainOfTimelessConflict";
    const string PulseModel = "Metadata/Effects/Spells/monsters_effects/essence/monolith_pulse/monolith_pulse.ao";
    /// <summary>MiscAnimated 3357 LegionSoulercoaster1.</summary>
    const int SoulercoasterKey = 3357;
    const string SaveKey = "LegionDomain";
    /// <summary>Radius around LegionEndlessInitiator that starts or resets the Domain encounter.</summary>
    const double ControlZoneRadius = 28.0;
    /// <summary>Minimum seconds between player-triggered ControlZone resets (prevents instant spam while standing inside).</summary>
    const double ManualResetCooldownSeconds = 4.0;
    const double DefaultAutoPulseSeconds = 20.0;
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
        string[] Factions, int UberCount, double TimerSeconds, double AutoPulseSeconds,
        double StartedAt, double EndsAt, double LastResetAt, double LastAutoPulseAt,
        int Resets, int TotalKills, Dictionary<string, int> GeneralKills, List<int> BankedRewards,
        FighterSlot[] Slots, bool ControlZoneReady = true);

    sealed class DomainState(SavedDomain s)
    {
        public uint InitiatorId = s.InitiatorId, CrystalId = s.CrystalId;
        public int X = s.X, Y = s.Y, Level = s.Level;
        public float? Z = s.Z;
        public DomainPhase Phase = (DomainPhase)s.Phase;
        public string[] Factions = s.Factions;
        public int UberCount = s.UberCount;
        public double TimerSeconds = s.TimerSeconds, AutoPulseSeconds = s.AutoPulseSeconds;
        public double StartedAt = s.StartedAt, EndsAt = s.EndsAt;
        public double LastResetAt = s.LastResetAt, LastAutoPulseAt = s.LastAutoPulseAt;
        public int Resets = s.Resets, TotalKills = s.TotalKills;
        public Dictionary<string, int> GeneralKills = new(s.GeneralKills, StringComparer.Ordinal);
        public List<int> BankedRewards = [.. s.BankedRewards];
        public FighterSlot[] Slots = s.Slots;
        public bool ControlZoneReady = s.ControlZoneReady;
        /// <summary>True when individual kill updates have not yet been flushed to ContentState JSON.</summary>
        public bool Dirty;
        public double LastFlushAt;
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
            Factions, UberCount, TimerSeconds, AutoPulseSeconds, StartedAt, EndsAt, LastResetAt, LastAutoPulseAt,
            Resets, TotalKills, new(GeneralKills, StringComparer.Ordinal), [.. BankedRewards], [.. Slots], ControlZoneReady);
    }

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

    // ---- Map Device Opening (2-5 Timeless / Unrelenting Emblems + Legion Scarab claim) ----

    /// <summary>GameEvents.MapOpening: claims "Legion" scarab handling on normal maps, and resolves 2–5 distinct
    /// Timeless / Unrelenting Emblems into the matching Domain of Timeless Conflict area (<c>LegionLeague</c>..<c>LegionLeague4</c>).</summary>
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
            request.Reason = "Place only 2 to 5 different Timeless Emblems to open the Domain of Timeless Conflict.";
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
        int uberCount = emblems.Count(e => e.Uber);
        int areaLevel = Math.Clamp(domainArea.BaseLevel + Math.Min(4, uberCount), 1, 100);
        var mods = ComposeDomainMods(domainArea, emblems, Program.Items.Mods);
        var mapArea = new MapDevice.MapArea(domainArea.Hash16, domainArea.Id, domainArea.Name, 0)
        {
            Level = areaLevel,
            UsesAtlas = false,
        };
        request.Opening = new MapDevice.Opening(mapArea, mods, request.Slots[emblemIndices[0]].BaseRow);
        request.ConsumeSlots = emblemIndices;
        request.Reason = "";
        request.ReturnToSource = true;
    }

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

    /// <summary>The increased maximum life gained per revival: the Unrelenting emblems' uber_domain_monster_maximum_life_+%_per_revival (10 each).
    /// GUESS: it stacks as increased (additive with the monster's other maximum_life_+%).</summary>
    public static int RevivalLifePct(IReadOnlyDictionary<uint, int> areaStats, int revivalCount) =>
        revivalCount <= 0 ? 0 : areaStats.GetValueOrDefault(Stat.UberDomainMonsterMaximumLifePlusPctPerRevival) * Math.Min(MaxRevivalStacks, revivalCount);

    /// <summary>Per-revival EncounterStats of the emblems used (summed over them, times the revival count) for a monster revived
    /// in a Domain of Timeless Conflict. The consumers today are damage_+% and the phys-as-chaos gain (hit damage); the rest is
    /// carried for when monsters read them. Tankiness is applied as toughness (<see cref="RevivalToughness"/>).</summary>
    public static Dictionary<uint, int>? UberRevivalStats(IReadOnlyDictionary<uint, int> areaStats, int revivalCount)
    {
        if (revivalCount <= 0) return null;
        int stacks = Math.Min(MaxRevivalStacks, revivalCount);
        var stats = new Dictionary<uint, int>();
        void Add(uint areaStat, uint monsterStat)
        {
            int per = areaStats.GetValueOrDefault(areaStat);
            if (per != 0) stats[monsterStat] = stats.GetValueOrDefault(monsterStat) + per * stacks;
        }
        Add(Stat.UberDomainMonsterDamagePlusPctPerRevival, Stat.DamagePlusPct);
        Add(Stat.UberDomainMonsterAttackAndCastSpeedPlusPctPerRevival, Stat.AttackAndCastSpeedPlusPct);
        Add(Stat.UberDomainMonsterMovementSpeedPlusPctPerRevival, Stat.BaseMovementVelocityPlusPct);
        Add(Stat.UberDomainMonsterCriticalStrikeChancePlusPctPerRevival, Stat.CriticalStrikeChancePlusPct);
        Add(Stat.UberDomainMonsterCriticalStrikeMultiplierPlusPctPerRevival, Stat.BaseCriticalStrikeMultiplierPlus);
        Add(Stat.UberDomainMonsterLifeRegenerationRatePerMinutePctPerRevival, Stat.LifeRegenerationRatePerMinutePct);
        Add(Stat.UberDomainMonsterAdditionalPhysicalDamageReductionPctPerRevival, Stat.BaseAdditionalPhysicalDamageReductionPct);
        Add(Stat.UberDomainMonsterAllResistancesPlusPctPerRevival, Stat.BaseResistAllElementsPct);
        Add(Stat.UberDomainMonsterAvoidStunPctPerRevival, Stat.BaseAvoidStunPct);
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
            int uberCount = Math.Max(0, stats.GetValueOrDefault(Stat.MapNumLegionUberFragments));
            int mapLevel = player.Instance.Map?.Area.Level ?? 0;
            int level = Math.Clamp(mapLevel > 0 ? mapLevel : 80 + Math.Min(4, uberCount), 1, 100);
            double timerSec = TimerSecondsFor(stats, factions.Length);
            int pulseMs = stats.GetValueOrDefault(Stat.MapLegionEndlessMonolithPulseTimeMs);
            double autoPulseSec = pulseMs >= 3000 ? pulseMs / 1000.0 : DefaultAutoPulseSeconds;

            var locator = player.Layout.Level?.RoomObjects.FirstOrDefault(o => o.Type == LocatorType);
            int cx = locator is not null ? locator.X : Math.Clamp(333, 1, Math.Max(1, player.Layout.GridW - 2));
            int cy = locator is not null ? locator.Y : Math.Clamp(333, 1, Math.Max(1, player.Layout.GridH - 2));
            float? cz = locator is not null && !float.IsNaN(locator.Z) ? locator.Z : null;
            var near = player.Layout.Near((cx, cy));
            // The army is planned now: its soldiers lie on the arena before the start.
            var rng = global::Game.AreaRng(player);
            int packSizePct = stats.GetValueOrDefault(Stat.MapLegionEndlessPackSizePlusPct);
            int uberRewardPct = stats.GetValueOrDefault(Stat.UberDomainMonsterRewardChancePlusPct)
                + stats.GetValueOrDefault(Stat.DisplayLegionUberFragmentImprovedRewardsPlusPct);
            var plan = LegionArmy.PlanEndless(LegionData.Current, factions, level, packSizePct, uberRewardPct, rng);
            var slots = BuildRadialSlots(player, near.X, near.Y, plan, rng);
            var saved = new SavedDomain(player.TakeObjectId(), CrystalSupported ? player.TakeObjectId() : 0,
                near.X, near.Y, cz, level, (int)DomainPhase.Waiting, factions, uberCount,
                timerSec, autoPulseSec, 0, 0, double.NegativeInfinity, double.NegativeInfinity,
                0, 0, [], [], slots);
            state = Domains.GetValue(instance, _ => new DomainState(saved));
            lock (state) Keep(player, state);
            module.Log($"{PlayerOf(player).Character}: Domain of Timeless Conflict ({player.Layout.Id}, L{level}) ready at ({state.X}, {state.Y}) — {string.Join("+", factions)} ({uberCount} Unrelenting), timer {timerSec:0}s, {slots.Length} soldiers planned");
        }

        else
        {
            // A Domain saved before its army was planned on entry: plan it now.
            var stats = LegionMonolith.AreaStats(player);
            var rng = global::Game.AreaRng(player);
            lock (state)
                if (state.Phase == DomainPhase.Waiting && state.Slots.Length == 0)
                {
                    var plan = LegionArmy.PlanEndless(LegionData.Current, state.Factions, state.Level,
                        stats.GetValueOrDefault(Stat.MapLegionEndlessPackSizePlusPct), stats.GetValueOrDefault(Stat.UberDomainMonsterRewardChancePlusPct), rng);
                    state.Slots = BuildRadialSlots(player, state.X, state.Y, plan, rng);
                    Keep(player, state);
                }
        }

        await ShowObjects(player, state);
        await SyncRulesToPlayer(player, state);
        await SpawnWaitingGhosts(module, player, state);
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
        int made = 0;
        foreach (int i in missing)
            if (await SpawnGhost(player, state, i, fall: false) != 0) made++;
        if (missing.Count > 0) module.Log($"Domain revive: {made}/{missing.Count} ghosts created lying before the start (state 4, the client poses them)");
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
            lock (state) (slot, level) = (state.Slots[i], (byte)Math.Clamp(state.Level, 1, 100));
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
                if (fall) state.Falls.Add((i, now + FallDelaySeconds));
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
        foreach (var (i, ghost, sequence) in send)
            await LegionRevive.Send(player, ghost, sequence, payload, null);
        if (send.Count > 0)
            module.Log($"Domain revive: {send.Count} ghosts -> state 5 (vanish {LegionRevive.VanishSeconds} s){(send.Count == 1 ? $", 0x{send[0].Ghost:x} slot {send[0].Slot}" : "")}");
        return send.Count;
    }

    /// <summary>When state 1 follows state 5 (the client's own stand-up begins at 0.8 s; state 1 must not reach it earlier, or the
    /// ghost would skip the stand-up), and when the stood-up ghost (the client's state 2 at about 1.8 s) is swapped for its fighter.</summary>
    const double RevivedAfterSeconds = 1.0, SwapAfterSeconds = 2.0;
    /// <summary>Swaps (a fighter's spawn each) per tick, so the start's wave of every soldier spreads over a few frames.</summary>
    const int MaxSwapsPerTick = 24;

    /// <summary>The tick's share of the revivals, deaths and falls (small sets; under lock(state)).</summary>
    static void TakeReviveWork(DomainState state, double now, List<(int Slot, uint Ghost, ushort Sequence)> revived,
        List<int> swaps, List<(int Slot, uint Corpse)> fallen, List<(int Slot, uint Ghost, ushort Sequence, float Timer)> falls)
    {
        foreach (int i in state.Rising)
        {
            var s = state.Slots[i];
            if (s.RiseStep == 1 && now >= s.RiseAt + RevivedAfterSeconds)
            {
                state.Slots[i] = s with { RiseStep = 2 };
                revived.Add((i, s.GhostId, s.Sequence));
            }
            else if (s.RiseStep == 2 && now >= s.RiseAt + SwapAfterSeconds && swaps.Count < MaxSwapsPerTick)
                swaps.Add(i);
        }
        foreach (int i in swaps) { state.Rising.Remove(i); state.Spawning.Add(i); }
        while (state.Fallen.Count > 0 && fallen.Count < MaxSwapsPerTick)
        {
            var (i, corpse) = state.Fallen.Dequeue();
            if (state.Spawning.Add(i)) fallen.Add((i, corpse));
        }
        for (int k = state.Falls.Count - 1; k >= 0; k--)
        {
            var (i, at) = state.Falls[k];
            if (now < at) continue;
            state.Falls.RemoveAt(k);
            var s = state.Slots[i];
            if (s.GhostId == 0 || s.RiseStep != 0) continue;
            falls.Add((i, s.GhostId, s.Sequence, (float)Math.Max(0, s.DeadAt + s.RevivalMs / 1000.0 - now)));
        }
    }

    /// <summary>Does the work <see cref="TakeReviveWork"/> took: state 1 to the standing ghosts, the swap of a stood-up ghost for its
    /// fighter, a fallen fighter's corpse for its ghost, and the ghosts' falls.</summary>
    static async Task StepRising(IModuleContext module, Player player, DomainState state,
        List<(int Slot, uint Ghost, ushort Sequence)> revived, List<int> swaps, List<(int Slot, uint Corpse)> fallen,
        List<(int Slot, uint Ghost, ushort Sequence, float Timer)> falls)
    {
        if (revived.Count > 0)
        {
            byte[] payload = LegionRevive.RevivedPayload(LegionRevive.StandSeconds);
            foreach (var (_, ghost, sequence) in revived) await LegionRevive.Send(player, ghost, sequence, payload, null);
            module.Log($"Domain revive: {revived.Count} ghosts -> state 1 (revived, beam){(revived.Count == 1 ? $", 0x{revived[0].Ghost:x}" : "")}");
        }
        if (swaps.Count > 0)
        {
            var stats = LegionMonolith.AreaStats(player);
            var ghosts = new List<uint>(swaps.Count);
            int made = 0;
            foreach (int i in swaps)
            {
                uint ghost;
                lock (state) { ghost = state.Slots[i].GhostId; state.Spawning.Remove(i); }
                uint fighter = await SpawnSlot(player, state, i, stats);
                if (fighter == 0)
                {
                    // Ended meanwhile: the stood-up ghost goes as the fighters did. Else it stays where it stood.
                    bool ended;
                    lock (state) { ended = state.Phase != DomainPhase.Active; if (!ended) state.NoSpawn.Add(i); else state.Slots[i] = state.Slots[i] with { GhostId = 0, RiseStep = 0 }; }
                    if (ended) ghosts.Add(ghost);
                    continue;
                }
                made++;
                ghosts.Add(ghost);
                lock (state) state.Slots[i] = state.Slots[i] with { GhostId = 0, RiseAt = double.NegativeInfinity, RiseStep = 0 };
                if (swaps.Count == 1) module.Log($"Domain revive: ghost 0x{ghost:x} slot {i} -> fighter 0x{fighter:x}");
            }
            await LegionRevive.Remove(player, ghosts);
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
                if (await SpawnGhost(player, state, i, fall: true) is uint ghost and not 0) made.Add(ghost);
            module.Log($"Domain revive: {fallen.Count} fallen -> {made.Count} ghosts lying (state 4{(made.Count == 1 ? $", 0x{made[0]:x}" : "")}), state 3 in {FallDelaySeconds:0.##} s");
        }
        if (falls.Count > 0)
        {
            foreach (var (i, ghost, sequence, timer) in falls)
                await LegionRevive.Send(player, ghost, sequence, LegionRevive.FallenPayload(timer), null);
            module.Log($"Domain revive: {falls.Count} ghosts -> state 3 (fall, cooldown){(falls.Count == 1 ? $", 0x{falls[0].Ghost:x} for {falls[0].Timer:0.#} s" : "")}");
        }
    }

    static async Task ShowObjects(Player player, DomainState state)
    {
        DomainPhase phase; bool zoneReady;
        lock (state) (phase, zoneReady) = (state.Phase, state.ControlZoneReady);
        int obeliskState = phase switch { DomainPhase.Waiting => 0, DomainPhase.Active => 1, _ => 3 };

        if (InitiatorSupported && !player.Area.Objects.Has(state.InitiatorId)
            && BuildInitiatorObject(state.X, state.Y, state.Z, obeliskState, phase != DomainPhase.Finished && zoneReady) is { } initObj)
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

        if (state.CrystalId != 0 && CrystalSupported && !player.Area.Objects.Has(state.CrystalId)
            && BuildCrystalObject(state.X, state.Y, state.Z, phase == DomainPhase.Finished) is { } crysObj)
        {
            var create = InstanceEntities.CreateObject(state.CrystalId, InstanceEntities.ObjectTypeHash(RewardCrystalType), crysObj.Blob());
            await LegionOutput.ToArea(player, async p =>
            {
                if (await global::Game.SendWorldCreate(PlayerOf(p), state.CrystalId, create, $"0x02bb LegionDomainRewardCrystal 0x{state.CrystalId:x}"))
                    await global::Game.SendWorldRecord(PlayerOf(p), state.CrystalId, InstanceProtocol.EnterWakeZone(state.CrystalId));
            });
        }
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
            if (LegionData.Current.EnvDomain != 0 && LegionRules.Environments((LegionData.Current.EnvDomain, 0.0)) is { } env)
                await PlayerOf(player).Send(env, "0x00df Domain environment (joiner)");
        }
        else if (phase == DomainPhase.Finished && LegionRules.State(state.InitiatorId, 2, 5) is { } done)
            await PlayerOf(player).Send(done, "0x02c9 LegionRules state (2,5 domain)");
    }

    static bool AnyPlayerInControlZone(Player player, DomainState state, double now)
    {
        double r2 = ControlZoneRadius * ControlZoneRadius;
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
        if (!await WorldOf(player).WalkUpTo(state.X, state.Y, (int)ControlZoneRadius, requestedAt)) return true;
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
        int action = 0; // 1 = start, 2 = finish, 3 = manual reset, 4 = auto pulse, 5 = restore control zone visual
        List<(int Slot, uint Ghost, ushort Sequence)>? revived = null;
        List<int>? swaps = null;
        List<(int Slot, uint Corpse)>? fallen = null;
        List<(int Slot, uint Ghost, ushort Sequence, float Timer)>? falls = null;
        lock (state)
        {
            if (state.Phase == DomainPhase.Waiting)
            {
                if (AnyPlayerInControlZone(player, state, now)) action = 1;
            }
            else if (state.Phase == DomainPhase.Active)
            {
                if (state.Dirty && now - state.LastFlushAt >= DomainFlushSeconds) Keep(player, state, flush: true);
                if (state.Rising.Count > 0 || state.Fallen.Count > 0 || state.Falls.Count > 0)
                {
                    revived = []; swaps = []; fallen = []; falls = [];
                    TakeReviveWork(state, now, revived, swaps, fallen, falls);
                }
                if (now >= state.EndsAt)
                {
                    state.Phase = DomainPhase.Finished;
                    state.ControlZoneReady = false;
                    Keep(player, state);
                    action = 2;
                }
                else
                {
                    bool cooldownDone = now - state.LastResetAt >= ManualResetCooldownSeconds;
                    if (!state.ControlZoneReady && cooldownDone)
                    {
                        state.ControlZoneReady = true;
                        action = 5;
                    }
                    bool anyReady = AnyReadyToRevive(state, now);
                    if (cooldownDone && anyReady && AnyPlayerInControlZone(player, state, now))
                    {
                        state.LastResetAt = now;
                        state.LastAutoPulseAt = now;
                        state.ControlZoneReady = false;
                        state.Resets++;
                        Keep(player, state);
                        action = 3;
                    }
                    else if (now - state.LastAutoPulseAt >= state.AutoPulseSeconds && anyReady)
                    {
                        state.LastAutoPulseAt = now;
                        state.Resets++;
                        Keep(player, state);
                        action = 4;
                    }
                }
            }
        }
        if (revived is not null) await StepRising(module, player, state, revived, swaps!, fallen!, falls!);
        if (action == 1) await StartDomain(module, player, state);
        else if (action == 2) await FinishDomain(module, player, state);
        else if (action is 3 or 4) await PulseReset(module, player, state, manual: action == 3);
        else if (action == 5) await UpdateInitiatorState(player, state, obeliskState: 1, checkingControlZone: true);
    }

    /// <summary>A slot is ready to revive when it is dead long enough, is not being spawned right now and can be spawned at all.
    /// Call under lock(state).</summary>
    static bool IsReady(DomainState state, int i, double now)
    {
        var s = state.Slots[i];
        return s is { Alive: false, GhostId: not 0, RiseStep: 0 } && now - s.DeadAt >= s.RevivalMs / 1000.0
            && !state.Spawning.Contains(i) && !state.NoSpawn.Contains(i);
    }

    static bool AnyReadyToRevive(DomainState state, double now)
    {
        lock (state)
            for (int i = 0; i < state.Slots.Length; i++)
                if (IsReady(state, i, now)) return true;
        return false;
    }

    static async Task UpdateInitiatorState(Player player, DomainState state, int obeliskState, bool checkingControlZone)
    {
        if (!InitiatorSupported || !player.Area.Objects.Has(state.InitiatorId)) return;
        var sm = InitiatorDefaults!.Set("obelisk_state", obeliskState).Set("checking_control_zone", checkingControlZone ? 1 : 0);
        await LegionOutput.ToArea(player, p => global::Game.SendWorldRecord(PlayerOf(p), state.InitiatorId,
            InstanceEntities.StateMachineUpdate(state.InitiatorId, (byte)InitiatorStateIndex, sm), null));
    }

    static async Task UpdateCrystalState(Player player, DomainState state, bool active)
    {
        if (state.CrystalId == 0 || !CrystalSupported || !player.Area.Objects.Has(state.CrystalId)) return;
        var sm = CrystalDefaults!.Set("active", active ? 1 : 0);
        await LegionOutput.ToArea(player, p => global::Game.SendWorldRecord(PlayerOf(p), state.CrystalId,
            InstanceEntities.StateMachineUpdate(state.CrystalId, (byte)CrystalStateIndex, sm), null));
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
            state.LastResetAt = now;
            state.LastAutoPulseAt = now;
            state.ControlZoneReady = false;
            for (int i = 0; i < plannedSlots.Length; i++)
            {
                plannedSlots[i] = plannedSlots[i] with { DeadAt = now };
                rising.Add(i);
            }
            state.LiveByMonster.Clear();
            Keep(player, state);
        }

        if (LegionData.Current.EnvDomain != 0 && LegionRules.Environments((LegionData.Current.EnvDomain, 0.5)) is { } env)
            foreach (var m in LegionOutput.Members(player))
                await PlayerOf(m).Send(env, "0x00df Domain environment (active)");
        foreach (var m in LegionOutput.Members(player))
        {
            if (LegionRules.Activated(state.InitiatorId) is { } act) await PlayerOf(m).Send(act, "0x02c9 LegionRules mode 1 (domain)");
            if (LegionRules.State(state.InitiatorId, 1, 2) is { } st) await PlayerOf(m).Send(st, "0x02c9 LegionRules state (1,2 domain)");
            await PlayerOf(m).Send(InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Legion, (uint)Math.Ceiling(state.TimerSeconds)),
                $"0x0163 mission timer Legion domain: {state.TimerSeconds:0} s");
        }
        await UpdateInitiatorState(player, state, obeliskState: 1, checkingControlZone: false);
        await PlayEffect(player, PulseModel, state.X, state.Y, scale: 6f);

        // Every soldier wakes at once: its ghost stands up and becomes its fighter (StepRising).
        int woken = await StartRising(module, player, state, rising, revival: false);
        lock (state) Keep(player, state);
        module.Log($"Domain started: {string.Join("+", state.Factions)} ({woken}/{plannedSlots.Length} soldiers waking, timer {state.TimerSeconds:0}s)");
    }

    static FighterSlot[] BuildRadialSlots(Player player, int cx, int cy, LegionArmy.ArmyPlan plan, Random rng)
    {
        var list = new List<FighterSlot>();
        int fCount = Math.Max(1, plan.Factions.Length);
        double sectorSpan = Math.Tau / fCount;
        double baseAngle = rng.NextDouble() * Math.Tau;
        for (int f = 0; f < plan.Factions.Length; f++)
        {
            var faction = plan.Factions[f];
            double centerAngle = baseAngle + f * sectorSpan;
            foreach (var squad in faction.Squads.OrderByDescending(q => q.Tier))
            {
                for (int i = 0; i < squad.Count; i++)
                {
                    double frac = squad.FromPct == squad.ToPct
                        ? squad.FromPct / 100.0
                        : (squad.FromPct + rng.NextDouble() * Math.Max(1, squad.ToPct - squad.FromPct)) / 100.0;
                    double dist = 34.0 + frac * 78.0;
                    double angle = squad.Tier == 3
                        ? centerAngle
                        : centerAngle + (rng.NextDouble() - 0.5) * sectorSpan * 0.78;
                    int tx = cx + (int)Math.Round(Math.Cos(angle) * dist);
                    int ty = cy + (int)Math.Round(Math.Sin(angle) * dist);
                    var at = player.Layout.Near((tx, ty));
                    // Keep monsters outside the immediate central crystal blocker.
                    if ((at.X - cx) * (at.X - cx) + (at.Y - cy) * (at.Y - cy) < 20 * 20)
                        at = player.Layout.Near((cx + (int)Math.Round(Math.Cos(angle) * 32), cy + (int)Math.Round(Math.Sin(angle) * 32)));
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
        var variety = VarietyOf(state, slot.MonsterRow);
        if (variety is not { Supported: true })
        {
            lock (state) state.NoSpawn.Add(slotIndex);   // never ready: no pulse, no revival count, no save for it
            return 0;
        }
        if (player.Closed || player.LeavingArea) return 0;

        IReadOnlyList<(uint, int)>? extraStats = LegionStasis.RewardIcon(slot.Rewards) is int icon
            ? [(Stat.MonsterMinimapIcon, icon)] : null;
        // The per-revival life and toughness go in the spawn's mods, so the life and the client's maximum are right from the create.
        int revivalLife = RevivalLifePct(areaStats, slot.RevivalCount), revivalToughness = RevivalToughness(areaStats, slot.RevivalCount);
        MonsterModSet? revivalMods = revivalLife > 0 || revivalToughness > 0
            ? new([], [], new MonsterModEffects(new Dictionary<uint, int>
            {
                [Stat.MaximumLifePlusPct] = revivalLife,
                [global::Game.MonsterToughness] = revivalToughness,
            }))
            : null;
        // Its variety's own states: no stasis shape (the breakout / activated outlines are the map monolith's); in the Domain the
        // client hides the crystal shell itself (LegionMonsterBase.ot: IfMapStat map_is_legion_endgame_map, hide_segments).
        uint id = await SpawnMonster(player, variety, areaLevel, (MonsterRarity)slot.Rarity, slot.X, slot.Y,
            friendly: false, unrisen: false, alone: false, instanceOwned: true, bulk: true, log: false,
            placement: MonsterPlacement.Established, rotation: slot.Facing, extraStats: extraStats, extraMods: revivalMods);
        if (id == 0 || !player.Area.Monsters.TryGetValue(id, out var monster)) return 0;

        if (slot.RevivalCount > 0)
        {
            if (UberRevivalStats(areaStats, slot.RevivalCount) is { } revivalStats)
                monster.EncounterStats = revivalStats;
        }
        // No reward symbols (buff 1328) over the Domain's monsters (3.29.0); their rewards still bank (RecordKill).

        lock (state)
        {
            if (state.Phase != DomainPhase.Active)
            {
                // Encounter ended while spawning — remove cleanly.
                if (player.Area.Monsters.Remove(id, out var gone)) { gone.Dead = true; gone.Life = 0; gone.ActionGeneration++; }
                player.Instance.World.MonsterOrigins.TryRemove(id, out _);
                LegionOutput.ForgetComponents(player, id);
                return 0;
            }
            state.Slots[slotIndex] = slot with { CurrentId = id, Alive = true };
            state.LiveByMonster[id] = slotIndex;
        }
        return id;
    }

    static async Task PulseReset(IModuleContext module, Player player, DomainState state, bool manual)
    {
        double now = global::Game.AreaTime(player);
        var rng = global::Game.AreaRng(player);
        var toRevive = new List<int>();
        var deadCommon = new List<int>();
        lock (state)
        {
            if (state.Phase != DomainPhase.Active) return;
            for (int i = 0; i < state.Slots.Length; i++)
            {
                if (!IsReady(state, i, now)) continue;
                var s = state.Slots[i];
                if (s.Tier >= 2) toRevive.Add(i);
                else deadCommon.Add(i);
            }
            // Per poewiki: reset revives half of dead soldiers/elites (at least 1 when any are ready) + all ready Sergeants & Generals.
            if (deadCommon.Count > 0)
            {
                int count = Math.Max(1, (deadCommon.Count + 1) / 2);
                for (int i = 0; i < count; i++)
                {
                    int pick = rng.Next(deadCommon.Count);
                    toRevive.Add(deadCommon[pick]);
                    deadCommon.RemoveAt(pick);
                }
            }
            Keep(player, state);
        }
        if (toRevive.Count == 0) return;

        await UpdateInitiatorState(player, state, obeliskState: 2, checkingControlZone: false);
        await PlayEffect(player, PulseModel, state.X, state.Y, scale: 5f);
        // The ghosts stand up where they lie; the client plays the beam (3377) itself at their state 1.
        int revived = await StartRising(module, player, state, toRevive, revival: true);
        lock (state) Keep(player, state);
        await UpdateInitiatorState(player, state, obeliskState: 1, checkingControlZone: false);
        module.Log($"Domain {(manual ? "manual reset" : "auto-pulse")} #{state.Resets}: revived {revived} fighters");
    }

    static bool RecordKill(Player player, DomainState state, uint monsterId, out FighterSlot? killedSlot)
    {
        killedSlot = null;
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
            if (slot.Tier == 3) state.GeneralKills[slot.FactionId] = state.GeneralKills.GetValueOrDefault(slot.FactionId) + 1;
            if (slot.Rewards is { Length: > 0 }) state.BankedRewards.AddRange(slot.Rewards);
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

    static async Task FinishDomain(IModuleContext module, Player player, DomainState state)
    {
        var liveIds = new List<uint>();
        List<int> bankedRewards;
        Dictionary<string, int> generalKills;
        int totalKills, resets, factionCount, uberCount, level;
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
            resets = state.Resets;
            factionCount = state.Factions.Length;
            uberCount = state.UberCount;
            level = state.Level;
            Keep(player, state);
        }

        // Hide timer, end ruleset, fade out Domain environment.
        foreach (var member in LegionOutput.Members(player))
        {
            await PlayerOf(member).Send(InstanceTimers.HideMissionTimer(InstanceTimers.MissionTimerType.Legion), "0x0163 mission timer Legion domain: hidden");
            if (LegionRules.State(state.InitiatorId, 2, 5) is { } done) await PlayerOf(member).Send(done, "0x02c9 LegionRules state (2,5 domain ended)");
        }
        if (LegionData.Current.EnvDomain != 0 && LegionRules.Environments((LegionData.Current.EnvDomain, -2.0)) is { } envOut)
            foreach (var member in LegionOutput.Members(player))
                await PlayerOf(member).Send(envOut, "0x00df Domain environment (out)");

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

        await UpdateInitiatorState(player, state, obeliskState: 3, checkingControlZone: false);
        await UpdateCrystalState(player, state, active: true);
        await PlayEffect(player, PulseModel, state.X, state.Y, scale: 6f);
        if (LegionData.Current.MiscAnimatedModels.GetValueOrDefault(SoulercoasterKey) is { } soulAo)
            await PlayEffect(player, soulAo, state.X, state.Y);

        int dropped = 0;
        if (GameEvents.AllowsLoot(player) && Environment.GetEnvironmentVariable("Game_DROPS") != "0" && (totalKills > 0 || bankedRewards.Count > 0))
        {
            var rng = global::Game.AreaRng(player);
            var loot = LegionRewards.RollDomainRewards(player, bankedRewards, generalKills, totalKills, factionCount, uberCount, level, rng);
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

        int generalsSlain = generalKills.Values.Sum();
        string summary = $"Domain of Timeless Conflict complete: {totalKills} slain ({generalsSlain} Generals), {resets} resets, {dropped} rewards dropped.";
        foreach (var member in LegionOutput.Members(player))
            await PlayerOf(member).Send(InstanceChat.ChatLine(summary), $"0x0017 chat line \"{summary}\"");
        module.Log(summary);
    }
}
