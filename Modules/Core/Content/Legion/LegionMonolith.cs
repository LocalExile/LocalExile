using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Legion;

/// <summary>
/// The Timeless Monolith: the room <c>legion_monolith.arm</c> only marks a point (<see cref="LocatorType"/>), so the server
/// creates the clickable <see cref="InitiatorType"/> and its paired <see cref="FlashLightType"/> there. Using it charges and
/// shatters the monolith (its 7 StateMachine states and LegionFlashLight trigger), shifts the area environment (0xdf
/// LegionLeagueWorld), plays the pulse and starts the Legion timer and LegionRules (0x2c9); when the timer ends or 90% break
/// out, broken members wake into battle (0xdf LegionLeagueWorldBattle), broken war chests become openable chests with
/// floating reward symbols, and after the battle clears the monolith settles through post-battle fade-out to finished.
/// The encounter is the instance's, kept in <c>Instance.ContentState</c>; its objects are the area's, each record sent once
/// (LegionOutput), and a client that enters later is given them by the area as they are.
/// </summary>
static partial class LegionMonolith
{
    /// <summary>The generic locator of the Legion room the generator adds when an area's Legion chance rolls (maps).</summary>
    public const string LocatorType = "Metadata/Terrain/Leagues/Legion/Objects/Legion_EncounterLocator";
    /// <summary>The locators that get a monolith: the generic one and the campaign's fixed ones, which sit inside the areas' own
    /// rooms (chance 0 there): The Climb (1_1_6, flag_hill_1.arm) and The Dried Lake (1_4_2, nightwane_boss.arm). An exact set:
    /// LegionEndgame_EncounterLocator belongs to the endgame (Domain), not to this encounter.</summary>
    static readonly HashSet<string> LocatorTypes =
    [
        LocatorType,
        "Metadata/Terrain/Leagues/Legion/Objects/FirstTimeLegion_EncounterLocator",
        "Metadata/Terrain/Leagues/Legion/Objects/DriedLake_EncounterLocator",
    ];
    public const string InitiatorType = "Metadata/Terrain/Leagues/Legion/Objects/LegionInitiator";
    public const string FlashLightType = "Metadata/Terrain/Leagues/Legion/Objects/LegionFlashLight";
    const string ChestBaseType = "Metadata/Chests/LegionChests/LegionChestBase";
    /// <summary>Official-capture base-data variable hashes: monolith -> General (0xec72e38c), flashlight -> monolith
    /// (0x40f0fc9c, 0x7700c879), and openable chest -> primary floating reward symbol (0xf3a80181).</summary>
    const uint GeneralPropertyKey = 0xec72e38c, FlashLightProp1 = 0x40f0fc9c, FlashLightProp2 = 0x7700c879, ChestSymbolPropertyKey = 0xf3a80181;
    /// <summary>MiscAnimated 3270 LegionMonolithPulse.</summary>
    const string PulseModel = "Metadata/Effects/Spells/monsters_effects/essence/monolith_pulse/monolith_pulse.ao";
    const string SaveKey = "Legion";
    /// <summary>The seconds between the build-up (charging) and the shatter: 45 frames at 30 fps (monolith_fx.ao build_up clip and official captures).</summary>
    const double ChargeSeconds = 45.0 / 30;
    /// <summary>Official-capture post-timer timings at 30 fps: 16 frames from freeze to battle state, 6.5 s after last woken
    /// monster dies (capped at 120 s), 8 frames to fade out battle environment, 76 frames to fade out post-battle environment.</summary>
    const double BattleAfterFreezeSeconds = 16.0 / 30, EndDelaySeconds = 6.5, BattleMaxSeconds = 120;
    const double BattleEnvOutSeconds = 8.0 / 30, PostBattleEnvOutSeconds = 76.0 / 30;
    /// <summary>The cells the player may stand from the monolith to use it.</summary>
    const int Reach = 12;
    /// <summary>GUESS (live check): how long an unbroken general stands fleeing before it is removed.</summary>
    const double FleeSeconds = 2;

    /// <summary>Encounter phase; numeric values 0..3 stay compatible with older ContentState saves.</summary>
    enum Phase { Idle = 0, Charging = 1, Running = 2, Finished = 3, Frozen = 4, Battle = 5, Ending = 6 }

    sealed record Saved(uint Id, int X, int Y, int Level, int Phase, double ChargeEnd, double EndsAt, uint Pulse,
        float Rotation = 0, float? Z = null, LegionArmy.ArmyPlan? Plan = null, LegionStasis.Member[]? Army = null,
        Box[]? Boxes = null, float PulseScale = 1, int FrozenDamageTakenPct = 0,
        uint FlashLight = 0, uint GeneralId = 0, double StageAt = 0, double? ClearedAt = null,
        bool BattleOutSent = false, uint[]? ReleasedIds = null, byte RuleStage = 0, byte RuleStep = 0,
        bool RuleActivated = false, ushort[]? Environments = null,
        bool AlertMode = false, int AlertGauge = 0, int AlertMax = 1000, bool MultipleBreakout = false,
        int ChestSpreadChancePct = 0, int LifePerRewardPct = 0, int SextantLifePct = 0, int SergeantRarityPct = 0);

    /// <summary>The instance's encounter. Fields change under the object's lock.</summary>
    sealed class Encounter(Saved saved)
    {
        public uint Id = saved.Id;
        public int X = saved.X, Y = saved.Y, Level = saved.Level;
        public Phase Phase = (Phase)saved.Phase;
        public double ChargeEnd = saved.ChargeEnd, EndsAt = saved.EndsAt;
        public uint Pulse = saved.Pulse;
        /// <summary>The scale the pulse ring was sent with (it reaches the army's farthest member).</summary>
        public float PulseScale = saved.PulseScale;
        public float Rotation = saved.Rotation;
        public float? Z = saved.Z;
        public LegionArmy.ArmyPlan? Plan = saved.Plan;
    /// <summary>The spawned army (kept in the save); null until it was spawned. Mutated in-place under lock(e) with O(1) ArmyIndex.</summary>
        public List<LegionStasis.Member>? Army = saved.Army is { } a ? [.. a] : null;
        /// <summary>O(1) lookup from frozen member object id to index in <see cref="Army"/>.</summary>
        public Dictionary<uint, int> ArmyIndex = BuildArmyIndex(saved.Army);
        /// <summary>True while BeginArmy still spawns: the early end waits for the whole army (not saved).</summary>
        public bool Spawning;
        /// <summary>True while Finish/ReleaseArmy wakes broken members and spawns chests: Frozen waits for it (not saved).</summary>
        public bool Releasing;
        /// <summary>True when hot per-monster updates (reveal/breakout) have not yet been flushed to Instance.ContentState JSON.</summary>
        public bool Dirty;
        /// <summary>Area time of the last ContentState JSON flush for this encounter.</summary>
        public double LastFlushAt;
        static Dictionary<uint, int> BuildArmyIndex(LegionStasis.Member[]? army)
        {
            var map = new Dictionary<uint, int>(army?.Length ?? 0);
            if (army is not null)
                for (int i = 0; i < army.Length; i++) map[army[i].Id] = i;
            return map;
        }
        public void AddMember(LegionStasis.Member m)
        {
            Army ??= [];
            ArmyIndex[m.Id] = Army.Count;
            Army.Add(m);
        }
        /// <summary>The chests the war chests became (kept in the save).</summary>
        public Box[]? Boxes = saved.Boxes;
        /// <summary>map_legion_frozen_monsters_damage_taken_+% captured at activation for the frozen army's EncounterStats.</summary>
        public int FrozenDamageTakenPct = saved.FrozenDamageTakenPct;
        /// <summary>Paired LegionFlashLight object id triggered at the shatter.</summary>
        public uint FlashLight = saved.FlashLight;
        /// <summary>Spawned General's entity id, linked to the monolith via dynamic property 0xec72e38c.</summary>
        public uint GeneralId = saved.GeneralId;
        /// <summary>Area time when the current post-charge stage began.</summary>
        public double StageAt = saved.StageAt;
        /// <summary>Area time when the last woken monster died during Battle (null while any still lives).</summary>
        public double? ClearedAt = saved.ClearedAt;
        public bool BattleOutSent = saved.BattleOutSent;
        /// <summary>Woken fighting monster ids tracked during Battle to detect encounter clear.</summary>
        public uint[]? ReleasedIds = saved.ReleasedIds;
        /// <summary>Latest LegionRules 0x2c9 stage/step and activation flag for late joiners.</summary>
        public byte RuleStage = saved.RuleStage, RuleStep = saved.RuleStep;
        public bool RuleActivated = saved.RuleActivated;
        /// <summary>Active 0xdf Environments rows for late joiners.</summary>
        public ushort[]? Environments = saved.Environments;
        /// <summary>Timeless Conflict Atlas keystone: replaces countdown timer with Alert gauge ending in Schism.</summary>
        public bool AlertMode = saved.AlertMode;
        public int AlertGauge = saved.AlertGauge, AlertMax = saved.AlertMax;
        /// <summary>Legion Scarab of Eternal Conflict: monsters can be broken out multiple times in stasis.</summary>
        public bool MultipleBreakout = saved.MultipleBreakout;
        /// <summary>Legion Scarab of Treasures: % chance for broken War Chests/Hoards to spread rewards onto freed soldiers.</summary>
        public int ChestSpreadChancePct = saved.ChestSpreadChancePct;
        /// <summary>Logistical Support (+50% more life per reward), sextant life multiplier, and Sergeant bonus IIR.</summary>
        public int LifePerRewardPct = saved.LifePerRewardPct, SextantLifePct = saved.SextantLifePct, SergeantRarityPct = saved.SergeantRarityPct;
        /// <summary>Latest saved snapshot updated under this encounter's lock so Keep never locks another encounter.</summary>
        public Saved Snapshot { get; private set; } = saved;
        public Saved ToSaved() => new(Id, X, Y, Level, (int)Phase, ChargeEnd, EndsAt, Pulse, Rotation, Z, Plan, Army is { } a ? [.. a] : null, Boxes, PulseScale, FrozenDamageTakenPct,
            FlashLight, GeneralId, StageAt, ClearedAt, BattleOutSent, ReleasedIds, RuleStage, RuleStep, RuleActivated, Environments,
            AlertMode, AlertGauge, AlertMax, MultipleBreakout, ChestSpreadChancePct, LifePerRewardPct, SextantLifePct, SergeantRarityPct);
        public void Commit() { Snapshot = ToSaved(); Dirty = false; }
    }

    /// <summary>Area stats for Legion encounters: uses the instance's composed map-owner Atlas/Scarab/MapMods stats
    /// (<see cref="InstanceState.KeptAreaStats"/>) when present, falling back to <see cref="Game.AreaLeagueStats"/>.</summary>
    public static IReadOnlyDictionary<uint, int> AreaStats(Player player) =>
        player.Instance?.KeptAreaStats ?? global::Game.AreaLeagueStats(player);

    static readonly ConditionalWeakTable<object, Encounter[]> Encounters = new();
    static readonly JsonSerializerOptions Json = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    static QuestObjectTypes.Info? Info, FlashLightInfo, ChestBaseInfo;
    static StateMachineData? Defaults, FlashLightDefaults, ChestBaseStates;
    static int StateIndex = -1, InteractionIndex = -1, FlashLightAnimatedIndex = -1;

    static bool FlashLightSupported =>
        FlashLightInfo is not null && FlashLightDefaults is not null && FlashLightAnimatedIndex >= 0
        && QuestObject.CanSerialize(FlashLightInfo.Components, true);

    sealed class Cleanup(Action clean) : IDisposable { public void Dispose() => clean(); }

    /// <summary>Eagerly caches monolith, flashlight and chest object definitions from client.db (called on module load and in tests).</summary>
    public static void LoadData()
    {
        var shared = ObjectTypes.SharedStates();
        StateMachineData? MachineOf(string path) =>
            ObjectTypes.All().Where(t => t.Type == path).Select(t => (ClientRow?)t.Row).FirstOrDefault() is { } row
                ? ObjectTypes.StateMachine(row, shared) : null;
        Info = QuestObjectTypes.Find(InitiatorType);
        Defaults = MachineOf(InitiatorType);
        StateIndex = Info?.Components.ToList().IndexOf("StateMachine") ?? -1;
        InteractionIndex = Info?.Components.ToList().IndexOf("InteractionAction") ?? -1;
        FlashLightInfo = QuestObjectTypes.Find(FlashLightType);
        FlashLightDefaults = MachineOf(FlashLightType);
        FlashLightAnimatedIndex = FlashLightInfo?.Components.ToList().IndexOf("Animated") ?? -1;
        ChestBaseInfo = QuestObjectTypes.Find(ChestBaseType);
        ChestBaseStates = MachineOf(ChestBaseType);
    }

    /// <summary>Clears cached object definitions on module unload.</summary>
    public static void UnloadData()
    {
        Info = FlashLightInfo = ChestBaseInfo = null;
        Defaults = FlashLightDefaults = ChestBaseStates = null;
        StateIndex = InteractionIndex = FlashLightAnimatedIndex = -1;
    }

    public static void Register(IModuleContext module)
    {
        string id = module.Manifest.Id;
        if (Info is null) LoadData();
        if (Info is null || Defaults is null || StateIndex < 0 || !QuestObject.CanSerialize(Info.Components, true))
        {
            module.Log($"the monolith's type {InitiatorType} is not in the client data or cannot be written: no monolith");
            return;
        }
        module.Lifetime.Own(new Cleanup(() =>
        {
            foreach (var (m, _) in Holds) { m.OnNonLethalZero = null; m.EntranceGrace = true; }
            Encounters.Clear(); Holds.Clear(); LegionStasis.ClearFighters();
        }));
        GameEvents.Drops.Add(id, drop => LegionRewards.Drops(drop));
        GameEvents.MonsterKilled.Add(id, LegionRewards.Killed);
        GameEvents.AreaPopulated.Add(id, area => Place(module, area));
        GameEvents.EnterMessages.Add(id, player => EnterMessages(module, player));
        GameEvents.PlayerEntered.Add(id, player => Enter(module, player));
        GameEvents.AreaTick.Add(id, player => HasLocator(player) ? (p, _) => Tick(module, p) : null);
        GameEvents.ObjectUse.Add(id, (player, use, requestedAt) => Use(module, player, use, requestedAt));
    }

    static bool HasLocator(Player player) => player.Layout.Level?.RoomObjects.Any(o => LocatorTypes.Contains(o.Type)) == true;

    /// <summary>Every Timeless Monolith in this instance (multiple when scarabs or mods add extra Legion rooms).
    /// Restores from <c>Instance.ContentState["Legion"]</c> after a hot reload, accepting both the array format and legacy single-object saves.</summary>
    static Encounter[] AllOf(IModuleContext module, Player player)
    {
        if (player.Instance is not { } instance) return [];
        LegionStasis.RestoreFighters(player);
        if (Encounters.TryGetValue(instance, out var existing)) return existing;
        if (!instance.ContentState.TryGetValue(SaveKey, out string? text) || string.IsNullOrWhiteSpace(text)) return [];
        Saved[]? saved = text.AsSpan().TrimStart().StartsWith("[")
            ? JsonSerializer.Deserialize<Saved[]>(text, Json)
            : JsonSerializer.Deserialize<Saved>(text, Json) is { } one ? [one] : null;
        if (saved is not { Length: > 0 }) return [];
        var restored = Encounters.GetValue(instance, _ => [.. saved.Select(s => new Encounter(s))]);
        foreach (var e in restored)
        {
            // The old module's holds were cleared on unload (leaving EntranceGrace true): re-arm holds and non-lethal floors.
            if (e.Army is { } army && e.Phase is Phase.Idle or Phase.Charging or Phase.Running)
                foreach (var m in army) Arm(module, player, e, m);
            // A reload during the reveal or the spawn after the shatter lost the pending steps: reveal what is still hidden.
            if (e.Phase == Phase.Running && e.Army is { } standing && standing.Where(m => !m.Revealed).ToList() is { Count: > 0 } hidden)
            {
                var inst = player.Instance;
                global::Game.SendAfter(player, 0, () =>
                    ResolveRunner(player, inst) is { } runner ? RevealMembers(module, runner, e, hidden) : Task.CompletedTask);
            }
        }
        return restored;
    }

    /// <summary>Coalesce interval (seconds) for persisting hot per-monster breakout/reveal changes to ContentState.</summary>
    const double DirtyFlushSeconds = 0.25;

    /// <summary>Marks <paramref name="e"/> dirty (caller holds <c>lock (e)</c>). When <paramref name="flush"/> is true
    /// (phase transitions, box opens, release), commits and serializes immediately; hot per-monster updates pass
    /// <c>flush: false</c> so <see cref="Tick"/> coalesces JSON serialization across all monoliths in the area.</summary>
    static void Keep(Player player, Encounter e, bool flush = true)
    {
        if (!flush) { e.Dirty = true; return; }
        e.Commit();
        e.LastFlushAt = global::Game.AreaTime(player);
        if (player.Instance is not { } instance) return;
        if (Encounters.TryGetValue(instance, out var all)) KeepAll(player, all);
        else instance.ContentState[SaveKey] = JsonSerializer.Serialize(new[] { e.Snapshot }, Json);
    }

    static void KeepAll(Player player, Encounter[] all)
    {
        lock (all)
            player.Instance.ContentState[SaveKey] = JsonSerializer.Serialize(all.Select(x => x.Snapshot).ToArray(), Json);
    }

    static IEnumerable<Player> Members(Player player) => LegionOutput.Members(player);

    /// <summary>Whether the area's clients have the object: the area keeps what it wrote of its objects until their removal,
    /// and gives it to each client that joins later (AreaObjectRecords).</summary>
    static bool InArea(Player player, uint id) => player.Area.Objects.Has(id);

    static bool OtherInPhases(IModuleContext module, Player player, Encounter current, Func<Phase, bool> match)
    {
        foreach (var other in AllOf(module, player))
        {
            if (ReferenceEquals(other, current)) continue;
            Phase p;
            lock (other) p = other.Phase;
            if (match(p)) return true;
        }
        return false;
    }

    static async Task BroadcastEnvironments(Player player, string what, params (ushort Row, double Seconds)[] entries)
    {
        if (LegionRules.Environments(entries) is not { } packet) return;
        foreach (var member in Members(player))
            await PlayerOf(member).Send(packet, $"0x00df Legion environment ({what})");
    }

    /// <summary>GameEvents.EnterMessages: registers the LegionRules ruleset (0x001f) before area objects arrive for a late joiner.</summary>
    static IEnumerable<(string Log, byte[] Message)> EnterMessages(IModuleContext module, Player player)
    {
        if (LegionRules.Add() is not { } add) yield break;
        if (!AllOf(module, player).Any(e => { lock (e) return e.Phase != Phase.Finished; })) yield break;
        yield return ("0x001f ruleset LegionRules (enter)", add);
    }

    /// <summary>GameEvents.AreaPopulated: places a monolith at every Legion locator in the generated layout.</summary>
    static async Task Place(IModuleContext module, AreaPopulated area)
    {
        var player = area.Player;
        if (player.Instance is not { } instance || AllOf(module, player).Length > 0) return;
        var locators = player.Layout.Level?.RoomObjects.Where(o => LocatorTypes.Contains(o.Type)).ToList();
        if (locators is not { Count: > 0 }) return;
        var all = new Encounter[locators.Count];
        for (int i = 0; i < locators.Count; i++)
        {
            var locator = locators[i];
            // The room's line has no fixed height (NaN): the object then takes the terrain's.
            float? z = float.IsNaN(locator.Z) ? null : locator.Z;
            all[i] = new Encounter(new(player.TakeObjectId(), locator.X, locator.Y, area.Level, 0, 0, 0, 0, locator.Rotation, z,
                FlashLight: FlashLightSupported ? player.TakeObjectId() : 0));
        }
        Encounters.Add(instance, all);
        KeepAll(player, all);   // saved first: a failure leaves no encounter that the client was not shown
        if (LegionRules.Add() is { } add)
            foreach (var member in Members(player)) await SendRules(member, add, "0x001f ruleset LegionRules");
        var areaStats = AreaStats(player);
        int durationPct = areaStats.GetValueOrDefault(Stat.MapLegionDurationPlusPct);
        bool alertMode = areaStats.GetValueOrDefault(Stat.AtlasKeystoneTimelessConflict) > 0
            || areaStats.GetValueOrDefault(Stat.MapLegionUsesAlertInsteadOfTimer) > 0;
        foreach (var e in all)
        {
            float baseSeconds = (float)(TimerMilliseconds(LegionData.Current, e.Level, durationPct) / 1000.0);
            foreach (var member in Members(player))
                await SendRules(member, LegionRules.Begin(e.X, e.Y, (uint)e.Level, e.Id, 0, baseSeconds, stage: 0, step: 0, angle: e.Rotation, alertMode: alertMode),
                    $"0x02c9 LegionRules mode 0 (monolith 0x{e.Id:x})");
            module.Log($"{PlayerOf(player).Character}: monolith 0x{e.Id:x} placed at ({e.X}, {e.Y}) in {player.Layout.Id}");
            await Show(player, e);
        }
    }

    /// <summary>GameEvents.PlayerEntered: a later player, or a returning one. The area gives its client the monoliths, pulses,
    /// chests and armies as they are; each army's stasis, which the area does not keep, is sent to that player alone.</summary>
    static async Task Enter(IModuleContext module, Player player)
    {
        foreach (var e in AllOf(module, player))
        {
            await Show(player, e);
            await ShowRules(player, e);
            List<LegionStasis.Member>? army;
            lock (e) army = e.Army is { } live ? [.. live] : null;   // MarkBroken/AddMember change e.Army in place
            await LegionStasis.Present(player, army, e.Plan);
            await ShowBoxes(player, e);
        }
    }

    /// <summary>Builds the LegionInitiator's 7 shared states for <paramref name="phase"/> matching official captures
    /// (obelisk_state, charging, shattered, crystal, spawn_lightning, locked, active).</summary>
    static StateMachineData StatesOf(Phase phase, Encounter? e = null)
    {
        var states = Defaults!;
        if (phase == Phase.Idle) return states;
        int lightning = 0, chests = e?.Plan?.Factions.Sum(f => f.Chests.Length) ?? 1;
        if (e is not null)
            lock (e)
                if (e.Army is { } army)
                {
                    lightning = army.Any(m => m.Tier == 3 && !m.Chest && !m.Broken) ? 1 : 0;
                    chests = army.Count(m => m.Chest);
                }
        int crystal = Math.Clamp(chests, 1, 3);
        return phase switch
        {
            Phase.Charging => states.Set("obelisk_state", 0).Set("charging", 1).Set("shattered", 0).Set("crystal", 0).Set("spawn_lightning", lightning).Set("locked", 1).Set("active", 1),
            Phase.Running => states.Set("obelisk_state", 2).Set("charging", 1).Set("shattered", 1).Set("crystal", crystal).Set("spawn_lightning", lightning).Set("locked", 1).Set("active", 1),
            Phase.Frozen => states.Set("obelisk_state", 3).Set("charging", 1).Set("shattered", 2).Set("crystal", 0).Set("spawn_lightning", 0).Set("locked", 1).Set("active", 1),
            Phase.Battle => states.Set("obelisk_state", 4).Set("charging", 1).Set("shattered", 2).Set("crystal", 0).Set("spawn_lightning", 0).Set("locked", 1).Set("active", 0),
            _ => states.Set("obelisk_state", 5).Set("charging", 1).Set("shattered", 2).Set("crystal", 0).Set("spawn_lightning", 0).Set("locked", 1).Set("active", 0),
        };
    }

    /// <summary>Builds the LegionFlashLight creation blob matching official 3.29 captures (46 bytes with default height):
    /// two monolith-id base-data variables (0x40f0fc9c, 0x7700c879), Positioned with flags0 0x02 (parent lookup),
    /// StateMachine (foggy), Animated 0, no tasks.</summary>
    internal static byte[] FlashLightBlob(int x, int y, uint monolithId, float rotation = 0, float? z = null)
    {
        var w = new BlobWriter().U8(0).U8(2);
        EntityVariable.Entity(FlashLightProp1, monolithId).Write(w);
        EntityVariable.Entity(FlashLightProp2, monolithId).Write(w);
        foreach (string c in FlashLightInfo!.Components)
        {
            switch (c)
            {
                case "Positioned":
                    w.Positioned(x, y, rotation, fixedHeight: z is float h && !float.IsNaN(h) ? BlobWriter.FixedHeight(h) : null, flags0: 0x02);
                    break;
                case "StateMachine": FlashLightDefaults!.Write(w); break;
                case "Animated": w.U8(0); break;
                // BaseEvents: no data.
            }
        }
        return w.U8(0).ToArray();
    }

    /// <summary>The monolith and its flashlight for the area, created once (the area keeps them for players who enter later),
    /// and the player's own part: its blocker before the charge and the timer while it runs.</summary>
    static async Task Show(Player player, Encounter e)
    {
        Phase phase; uint pulse; double endsAt; uint generalId; uint flashLight;
        lock (e) (phase, pulse, endsAt, generalId, flashLight) = (e.Phase, e.Pulse, e.EndsAt, e.GeneralId, e.FlashLight);
        if (!InArea(player, e.Id))
        {
            var obj = new QuestObject(e.X, e.Y, Info!.Components, e.Rotation, e.Z, Info.Model)
            {
                StateMachine = StatesOf(phase, e),
                EntityVariables = generalId != 0 ? new Dictionary<uint, uint> { [GeneralPropertyKey] = generalId } : new Dictionary<uint, uint>(),
            };
            var create = InstanceEntities.CreateObject(e.Id, InstanceEntities.ObjectTypeHash(InitiatorType), obj.Blob());
            bool created = false;
            await LegionOutput.ToArea(player, async p =>
            {
                if (!await global::Game.SendWorldCreate(PlayerOf(p), e.Id, create, $"0x02bb Legion monolith 0x{e.Id:x} at ({e.X}, {e.Y}) phase {phase}")) return;
                created = true;
                await global::Game.SendWorldRecord(PlayerOf(p), e.Id, InstanceProtocol.EnterWakeZone(e.Id));
            });
            if (!created) return;
        }
        if (flashLight != 0 && FlashLightSupported && !InArea(player, flashLight))
        {
            var createLight = InstanceEntities.CreateObject(flashLight, InstanceEntities.ObjectTypeHash(FlashLightType),
                FlashLightBlob(e.X, e.Y, e.Id, e.Rotation, e.Z));
            await LegionOutput.ToArea(player, async p =>
            {
                if (await global::Game.SendWorldCreate(PlayerOf(p), flashLight, createLight, $"0x02bb LegionFlashLight 0x{flashLight:x}"))
                    await global::Game.SendWorldRecord(PlayerOf(p), flashLight, InstanceProtocol.EnterWakeZone(flashLight));
            });
        }
        // A server-placed blocking object blocks walking until it charges (the client drops its collision then).
        // The phase may have moved on while the create was sent: a pulse is made only while the encounter still runs.
        lock (e) (phase, pulse) = (e.Phase, e.Pulse);
        if (phase == Phase.Idle && Info!.Blocking) player.SetBlocker(e.Id, e.X, e.Y, Math.Max(1, Info.Size));
        if (phase == Phase.Running && pulse != 0) await ShowPulse(player, e, pulse);
        // The timer shows from the shatter: the charge is not part of the countdown (hidden under Timeless Conflict alert mode).
        bool alertMode;
        lock (e) alertMode = e.AlertMode;
        if (phase == Phase.Running && !alertMode)
        {
            double left = Math.Ceiling(endsAt - global::Game.AreaTime(player));
            if (left > 0 && left < 3600)
                await PlayerOf(player).Send(InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Legion, (uint)left), $"0x0163 mission timer Legion: {left} s (sync)");
        }
    }

    static async Task ShowPulse(Player player, Encounter e, uint id)
    {
        if (InArea(player, id)) return;
        var create = InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), new EffectObject(e.X, e.Y, PulseModel) { Scale = e.PulseScale }.Blob());
        await LegionOutput.ToArea(player, async p =>
        {
            if (await global::Game.SendWorldCreate(PlayerOf(p), id, create, $"0x02bb Legion monolith pulse 0x{id:x}"))
                await global::Game.SendWorldRecord(PlayerOf(p), id, InstanceProtocol.EnterWakeZone(id));
        });
    }

    /// <summary>The Legion timer's length: the LegionBalancePerLevel row with the highest MinLevel &lt;= level (the first row if
    /// none), DurationMs milliseconds, exact (30 s at level 1 to 15 s from 68), times map_legion_duration_+% (client 0x1416290a0).
    /// The endless-mode stat is ignored: endless is the Domain, phase 3.</summary>
    public static double TimerMilliseconds(LegionData data, int level, int durationPercent)
    {
        LegionData.Balance? row = null;
        foreach (var b in data.Balances)
            if (b.MinLevel <= level && (row is null || b.MinLevel > row.MinLevel)) row = b;
        row ??= data.Balances.Count > 0 ? data.Balances[0] : null;
        // Exact: the server's end time is not rounded; only the whole seconds the client's timer shows are (SecondsShown, up).
        return Math.Max(1000, (row?.DurationMs ?? 20000) * Math.Max(0, 100 + durationPercent) / 100.0);
    }

    static uint SecondsShown(double seconds) => (uint)Math.Max(1, Math.Ceiling(seconds));

    static async Task<bool> Use(IModuleContext module, Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        var all = AllOf(module, player);
        if (all.Length == 0) return false;
        var e = all.FirstOrDefault(x => x.Id == use.TargetId);
        if (e is null)
        {
            foreach (var other in all)
                if (await OpenBox(module, player, other, use.TargetId, requestedAt)) return true;
            return false;
        }
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0) return true;
        lock (e) if (e.Phase != Phase.Idle) return true;
        if (!await WorldOf(player).WalkUpTo(e.X, e.Y, Reach, requestedAt)) return true;
        double seconds;
        string plan;
        bool alertMode;
        lock (e)
        {
            if (e.Phase != Phase.Idle) return true;   // a second use does nothing
            var stats = AreaStats(player);
            int percent = stats.GetValueOrDefault(Stat.MapLegionDurationPlusPct);
            e.FrozenDamageTakenPct = stats.GetValueOrDefault(Stat.MapLegionFrozenMonstersDamageTakenPlusPct);
            e.AlertMode = stats.GetValueOrDefault(Stat.AtlasKeystoneTimelessConflict) > 0
                || stats.GetValueOrDefault(Stat.MapLegionUsesAlertInsteadOfTimer) > 0;
            e.AlertGauge = 0;
            e.AlertMax = LegionArmy.AlertMax(LegionData.Current, e.Level);
            e.MultipleBreakout = stats.GetValueOrDefault(Stat.MapLegionMultipleBreakout) > 0;
            e.ChestSpreadChancePct = stats.GetValueOrDefault(Stat.MapLegionPctChanceChestsBreakOutSpreadRewards);
            e.LifePerRewardPct = stats.GetValueOrDefault(Stat.MapLegionMonstersPlusPctLifePerRewardFinal);
            e.SextantLifePct = stats.GetValueOrDefault(Stat.MapLegionMonsterLifePlusPctFinalFromSextant);
            e.SergeantRarityPct = stats.GetValueOrDefault(Stat.MapLegionSergeantsDroppedItemRarityPlusPct);
            alertMode = e.AlertMode;
            seconds = TimerMilliseconds(LegionData.Current, e.Level, percent) / 1000.0;
            e.Plan = LegionArmy.Plan(LegionData.Current, e.Level, player.Layout.Id, stats, global::Game.AreaRng(player));
            plan = LegionArmy.Summary(e.Plan);
            double now = global::Game.AreaTime(player);
            double endsAt = e.AlertMode ? double.PositiveInfinity : now + ChargeSeconds + seconds;
            (e.Phase, e.StageAt, e.ChargeEnd, e.EndsAt) = (Phase.Charging, now, now + ChargeSeconds, endsAt);
            Keep(player, e);
        }
        if (player.Layout.Id is "1_1_6" or "1_4_2")
        {
            string flag = player.Layout.Id == "1_1_6" ? "ClimbLegionActivated" : "DriedLakeLegionActivated";
            if (!global::Game.ClientHasQuestFlag(player, flag))
                await global::Game.SetQuestFlags(player, [flag]);
        }
        module.Log($"{PlayerOf(player).Character}: monolith activated at ({e.X}, {e.Y}), {(alertMode ? $"Timeless Conflict alert cap {e.AlertMax}" : $"timer {seconds} s")}");
        module.Log($"{PlayerOf(player).Character}: {plan}");
        if (InteractionIndex >= 0 && InArea(player, e.Id))
            await LegionOutput.ToArea(player, p => global::Game.SendWorldRecord(PlayerOf(p), e.Id,
                InstanceEntities.ComponentUpdate(e.Id, (byte)InteractionIndex, [0]), $"0x02bc Legion monolith 0x{e.Id:x} interaction 00"));
        await ToAll(player, e, StatesOf(Phase.Charging, e), "charging=1, locked=1, active=1");
        foreach (var member in Members(player))
        {
            await SendRules(member, LegionRules.Add(), "0x001f ruleset LegionRules");
            await SendRules(member, LegionRules.Begin(e.X, e.Y, (uint)e.Level, e.Id, 0, (float)seconds, stage: 0, step: 0, angle: e.Rotation, alertMode: alertMode),
                "0x02c9 LegionRules mode 0 (sync duration)");
        }
        foreach (var member in Members(player)) member.RemoveBlocker(e.Id);
        await BeginArmy(module, player, e);
        return true;
    }

    /// <summary>GameEvents.AreaTick: steps every monolith through Charging -> Running -> Frozen -> Battle -> Ending -> Finished.</summary>
    static async Task Tick(IModuleContext module, Player player)
    {
        if (player.Closed || player.LeavingArea) return;
        foreach (var e in AllOf(module, player))
        {
            int step = 0;
            uint pulse = 0, flashLight = 0;
            bool battleOut = false, postOut = false;
            lock (e)
            {
                if (e.Phase is Phase.Idle or Phase.Finished) continue;
                double now = global::Game.AreaTime(player);
                if (e.Dirty && now - e.LastFlushAt >= DirtyFlushSeconds) Keep(player, e, flush: true);
                if (e.Phase == Phase.Charging && now >= e.ChargeEnd)
                {
                    e.Phase = Phase.Running;
                    e.StageAt = now;
                    e.RuleActivated = true;
                    e.RuleStage = 1;
                    e.RuleStep = 2;
                    e.Environments = LegionData.Current.EnvWorld != 0 ? [LegionData.Current.EnvWorld] : [];
                    pulse = player.TakeObjectId();   // e.Pulse is set with its scale in RevealArmy
                    flashLight = e.FlashLight;
                    step = 1;
                    Keep(player, e);
                }
                else if (e.Phase == Phase.Running && now >= e.EndsAt)
                {
                    e.Phase = Phase.Frozen;
                    e.Releasing = true;
                    e.StageAt = now;
                    e.RuleStage = 1;
                    e.RuleStep = 3;
                    pulse = e.Pulse;
                    step = 2;
                    Keep(player, e);
                }
                else if (e.Phase == Phase.Frozen && !e.Releasing && now - e.StageAt >= BattleAfterFreezeSeconds)
                {
                    e.Phase = Phase.Battle;
                    e.StageAt = now;
                    e.RuleStage = 2;
                    e.RuleStep = 4;
                    step = 3;
                    Keep(player, e);
                }
                else if (e.Phase == Phase.Battle)
                {
                    int alive = 0;
                    if (e.ReleasedIds is { Length: > 0 } ids)
                    {
                        foreach (uint rid in ids)
                            if (player.Area.Monsters.TryGetValue(rid, out var rm) && !rm.Dead && rm.Life > 0) alive++;
                    }
                    if (alive == 0 && e.ClearedAt is null) { e.ClearedAt = now; Keep(player, e); }
                    if ((e.ClearedAt is not null && now - e.ClearedAt >= EndDelaySeconds) || now - e.StageAt >= BattleMaxSeconds)
                    {
                        e.Phase = Phase.Ending;
                        e.StageAt = now;
                        e.BattleOutSent = false;
                        e.RuleStage = 2;
                        e.RuleStep = 5;
                        var envs = new List<ushort>(2);
                        if (LegionData.Current.EnvBattle != 0) envs.Add(LegionData.Current.EnvBattle);
                        if (LegionData.Current.EnvPostBattle != 0) envs.Add(LegionData.Current.EnvPostBattle);
                        e.Environments = [.. envs];
                        step = 4;
                        Keep(player, e);
                    }
                }
                else if (e.Phase == Phase.Ending)
                {
                    if (now - e.StageAt >= BattleEnvOutSeconds && !e.BattleOutSent)
                    {
                        e.BattleOutSent = true;
                        e.Environments = LegionData.Current.EnvPostBattle != 0 ? [LegionData.Current.EnvPostBattle] : [];
                        battleOut = true;
                    }
                    if (now - e.StageAt >= PostBattleEnvOutSeconds)
                    {
                        e.Phase = Phase.Finished;
                        e.Environments = [];
                        e.ReleasedIds = null;
                        postOut = true;
                    }
                    if (battleOut || postOut) { step = 5; Keep(player, e); }
                }
            }
            if (step == 1)
            {
                if (LegionData.Current.EnvWorld != 0)
                    await BroadcastEnvironments(player, "world", (LegionData.Current.EnvWorld, 0.5));
                foreach (var member in Members(player))
                {
                    await SendRules(member, LegionRules.State(e.Id, 1, 1), "0x02c9 LegionRules state (1,1)");
                    await SendRules(member, LegionRules.Activated(e.Id), "0x02c9 LegionRules mode 1 (activated)");
                }
                await ToAll(player, e, StatesOf(Phase.Running, e), "obelisk=2, shattered=1");
                if (flashLight != 0 && FlashLightAnimatedIndex >= 0 && InArea(player, flashLight))
                    await LegionOutput.ToArea(player, p => global::Game.SendWorldRecord(PlayerOf(p), flashLight,
                        InstanceEntities.ComponentUpdate(flashLight, (byte)FlashLightAnimatedIndex, [0]), $"0x02bc LegionFlashLight 0x{flashLight:x} Animated 00"));
                if (!e.AlertMode)
                {
                    double left = Math.Max(1, e.EndsAt - global::Game.AreaTime(player));
                    foreach (var member in Members(player))
                        await PlayerOf(member).Send(InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Legion, SecondsShown(left)), $"0x0163 mission timer Legion: {left:0.0} s (at the shatter)");
                }
                await RevealArmy(module, player, e, pulse);   // the pulse and the reveal start at the same instant
                foreach (var member in Members(player))
                    await SendRules(member, LegionRules.State(e.Id, 1, 2), "0x02c9 LegionRules state (1,2)");
                await EarlyEnd(module, player, e);   // a break-out during the charge could not end it yet
            }
            else if (step == 2) await Finish(module, player, e, pulse, "timer");
            else if (step == 3)
            {
                foreach (var member in Members(player))
                    await SendRules(member, LegionRules.State(e.Id, 2, 4), "0x02c9 LegionRules state (2,4 battle)");
                await ToAll(player, e, StatesOf(Phase.Battle, e), "obelisk=4, active=0");
            }
            else if (step == 4)
            {
                foreach (var member in Members(player))
                    await SendRules(member, LegionRules.State(e.Id, 2, 5), "0x02c9 LegionRules state (2,5 ending)");
                await ToAll(player, e, StatesOf(Phase.Ending, e), "obelisk=5, active=0");
                if (!OtherInPhases(module, player, e, p => p is Phase.Running or Phase.Frozen or Phase.Battle))
                    await BroadcastEnvironments(player, "post-battle", (LegionData.Current.EnvBattle, 0.0), (LegionData.Current.EnvPostBattle, 0.2));
            }
            else if (step == 5)
            {
                if (battleOut && !OtherInPhases(module, player, e, p => p is Phase.Running or Phase.Frozen or Phase.Battle))
                    await BroadcastEnvironments(player, "battle out", (LegionData.Current.EnvBattle, -2.0), (LegionData.Current.EnvPostBattle, 0.2));
                if (postOut && !OtherInPhases(module, player, e, p => p is Phase.Running or Phase.Frozen or Phase.Battle or Phase.Ending))
                    await BroadcastEnvironments(player, "post-battle out", (LegionData.Current.EnvPostBattle, -2.0));
            }
        }
    }

    /// <summary>The end of the timer phase, reached once: the caller moved the phase from Running to Frozen under the
    /// encounter's lock (the timer in Tick, or EarlyEnd). Hides the timer (when no other monolith is running), shifts to
    /// Frozen/Battle (or Ending when 0 monsters woke), plays the release pulse and releases the army.</summary>
    static async Task Finish(IModuleContext module, Player player, Encounter e, uint pulse, string reason)
    {
        try
        {
            if (!OtherInPhases(module, player, e, p => p == Phase.Running))
                foreach (var member in Members(player))
                    await PlayerOf(member).Send(InstanceTimers.HideMissionTimer(InstanceTimers.MissionTimerType.Legion), "0x0163 mission timer Legion: hidden");
            foreach (var member in Members(player))
                await SendRules(member, LegionRules.State(e.Id, 1, 3), "0x02c9 LegionRules state (1,3 frozen)");
            await ToAll(player, e, StatesOf(Phase.Frozen, e), "obelisk=3, shattered=2, locked=1");
            if (pulse != 0 && InArea(player, pulse)) await LegionOutput.Remove(player, pulse);
            // Second radial shockwave at release (00:00 / early end), matching real PoE visuals.
            if (InArea(player, e.Id))
            {
                uint releasePulse = player.TakeObjectId();
                await ShowPulse(player, e, releasePulse);
                var inst = player.Instance;
                global::Game.SendAfter(player, PulseSeconds + 0.5, () =>
                    ResolveRunner(player, inst) is { } runner ? LegionOutput.Remove(runner, releasePulse) : Task.CompletedTask);
            }
            module.Log($"{PlayerOf(player).Character}: monolith 0x{e.Id:x} timer ended ({reason})");
            uint[] wokenIds = await ReleaseArmy(module, player, e, reason);
            var data = LegionData.Current;
            double now = global::Game.AreaTime(player);
            if (wokenIds.Length > 0)
            {
                lock (e)
                {
                    e.StageAt = now;
                    e.ClearedAt = null;
                    e.ReleasedIds = wokenIds;
                    e.Environments = data.EnvBattle != 0 ? [data.EnvBattle] : [];
                    e.Releasing = false;
                    Keep(player, e);
                }
                if (!OtherInPhases(module, player, e, p => p == Phase.Running))
                {
                    if (data.EnvWorld != 0) await BroadcastEnvironments(player, "world out", (data.EnvWorld, -2.0));
                    if (data.EnvBattle != 0) await BroadcastEnvironments(player, "battle", (data.EnvBattle, 0.0));
                }
            }
            else
            {
                lock (e)
                {
                    e.Phase = Phase.Ending;
                    e.StageAt = now;
                    e.BattleOutSent = true;
                    e.RuleStage = 2;
                    e.RuleStep = 5;
                    e.Environments = data.EnvPostBattle != 0 ? [data.EnvPostBattle] : [];
                    e.Releasing = false;
                    Keep(player, e);
                }
                foreach (var member in Members(player))
                    await SendRules(member, LegionRules.State(e.Id, 2, 5), "0x02c9 LegionRules state (2,5 no woken)");
                await ToAll(player, e, StatesOf(Phase.Ending, e), "obelisk=5, active=0");
                if (!OtherInPhases(module, player, e, p => p is Phase.Running or Phase.Frozen or Phase.Battle))
                {
                    if (data.EnvWorld != 0) await BroadcastEnvironments(player, "world out", (data.EnvWorld, -2.0));
                    if (data.EnvPostBattle != 0) await BroadcastEnvironments(player, "post-battle", (data.EnvPostBattle, 0.2));
                }
            }
        }
        finally
        {
            lock (e) e.Releasing = false;
        }
    }

    /// <summary>After a break-out: at least 90% of the plain and elite monsters and every chest, sergeant and general broken out
    /// ends the encounter at once (wiki). One scan of the army per break-out; the phase moves from Running to Frozen under the
    /// lock, so the timer and this cannot both finish it.</summary>
    static async Task EarlyEnd(IModuleContext module, Player player, Encounter e)
    {
        uint pulse;
        lock (e)
        {
            // Eternal Conflict lets players keep breaking out monsters until timer/Schism; skip 90% auto-end so multi-breakouts work.
            if (e.Phase != Phase.Running || e.Spawning || e.MultipleBreakout || e.Army is not { Count: > 0 } army) return;
            int plain = 0, plainBroken = 0;
            foreach (var m in army)
            {
                if (m.Chest || m.Tier >= 2) { if (!m.Broken) return; }
                else { plain++; if (m.Broken) plainBroken++; }
            }
            if (plainBroken * 10 < plain * 9) return;
            e.Phase = Phase.Frozen;
            e.Releasing = true;
            e.StageAt = global::Game.AreaTime(player);
            e.RuleStage = 1;
            e.RuleStep = 3;
            pulse = e.Pulse;
            Keep(player, e);
        }
        await Finish(module, player, e, pulse, "early");
    }

    /// <summary>The monolith's states, once to every client in the area (the area keeps the latest for a client that joins).</summary>
    static async Task ToAll(Player player, Encounter e, StateMachineData states, string what)
    {
        if (!InArea(player, e.Id)) return;
        await LegionOutput.ToArea(player, p => global::Game.SendWorldRecord(PlayerOf(p), e.Id,
            InstanceEntities.StateMachineUpdate(e.Id, (byte)StateIndex, states), $"0x02bc Legion monolith 0x{e.Id:x}: {what}"));
    }

    /// <summary>The monolith was used and the timer runs: the two armies stand up frozen around it (LegionStasis.cs).</summary>
    static async Task BeginArmy(IModuleContext module, Player player, Encounter e)
    {
        LegionArmy.ArmyPlan? plan;
        int frozenDamageTakenPct, lifePerRewardPct, sextantLifePct;
        lock (e)
        {
            if (e.Army is not null || e.Plan is null) return;
            plan = e.Plan;
            frozenDamageTakenPct = e.FrozenDamageTakenPct;
            lifePerRewardPct = e.LifePerRewardPct;
            sextantLifePct = e.SextantLifePct;
            e.Army = [];
            e.ArmyIndex.Clear();
            e.Spawning = true;
        }
        // Each member joins e.Army as soon as it stands, so a timer that ends meanwhile (ReleaseArmy) removes it too;
        // once the encounter leaves Charging/Running a new member is refused and Spawn removes it itself.
        bool Keeps(LegionStasis.Member m)
        {
            lock (e)
            {
                if (e.Phase is not (Phase.Charging or Phase.Running)) return false;
                e.AddMember(m);
                Arm(module, player, e, m);
                return true;
            }
        }
        // A member that stands after the shatter is revealed at once (RevealArmy revealed the ones standing then).
        async Task Standing(LegionStasis.Member m)
        {
            bool reveal;
            lock (e) reveal = e.Phase == Phase.Running;
            if (reveal) await RevealMembers(module, player, e, [m], keep: false);
        }
        try { await LegionStasis.Spawn(module, player, plan, e.X, e.Y, e.Level, Keeps, Standing, frozenDamageTakenPct, e.Rotation, monolithId: e.Id, lifePerRewardPct: lifePerRewardPct, sextantLifePct: sextantLifePct); }
        finally { lock (e) e.Spawning = false; }
        Phase phase;
        uint generalId = 0;
        lock (e)
        {
            phase = e.Phase;
            if (e.Army?.FirstOrDefault(m => m.Tier == 3 && !m.Chest && !m.Broken) is { } gen)
            {
                e.GeneralId = gen.Id;
                generalId = gen.Id;
            }
            Keep(player, e);
        }
        await EarlyEnd(module, player, e);   // a break-out during the spawn could not end it yet
        // Link the General to the monolith via dynamic property 0xec72e38c so spawn_lightning aims its bolts directly at the General.
        if (generalId != 0 && InArea(player, e.Id))
            await LegionOutput.ToArea(player, p => global::Game.SendWorldRecord(PlayerOf(p), e.Id,
                InstanceEntities.BaseDataUpdate(e.Id, [EntityVariable.Entity(GeneralPropertyKey, generalId)]),
                $"0x02c1 Legion monolith 0x{e.Id:x} general=0x{generalId:x}"));
        lock (e) phase = e.Phase;
        if (phase is Phase.Charging or Phase.Running && generalId != 0)
            await ToAll(player, e, StatesOf(phase, e), "spawn_lightning=1");
    }

    /// <summary>The timer phase ended (timer or EarlyEnd). Broken-out monsters wake and fight (LegionStasis.Wake),
    /// broken-out war chests become clickable Chest objects (SpawnBox), unbroken members are removed, and an unbroken
    /// general flees and goes after <see cref="FleeSeconds"/>. Returns the woken fighting monster ids.</summary>
    static async Task<uint[]> ReleaseArmy(IModuleContext module, Player player, Encounter e, string reason)
    {
        if (reason == "early") await BreakRemaining(module, player, e);   // the 90% rule breaks out the rest (patch 3.29.0), not removes it
        List<LegionStasis.Member>? army;
        lock (e) { army = e.Army; e.Army = []; e.ArmyIndex.Clear(); Keep(player, e); }
        if (army is not { Count: > 0 }) return [];
        var remove = new List<LegionStasis.Member>(army.Count);
        var ended = new List<LegionStasis.Member>();
        var flee = new List<LegionStasis.Member>();
        var brokenChests = new List<(LegionStasis.Member Member, int X, int Y)>();
        var wokenIds = new List<uint>();
        double now = global::Game.AreaTime(player);
        int brokenCount = 0;
        bool generalFought = false;
        foreach (var m in army)
        {
            if (!player.Area.Monsters.TryGetValue(m.Id, out var monster) || monster.Dead) continue;
            bool broken = m.Broken;
            if (Holds.TryGetValue(monster, out var hold)) { lock (hold) broken |= hold.Broken; }
            monster.OnNonLethalZero = null;
            monster.EntranceGrace = true;
            Holds.Remove(monster);
            if (broken) brokenCount++;
            if (broken && m.Chest)
            {
                var (cx, cy) = monster.Walk.Position(now);
                brokenChests.Add((m, cx, cy));
                remove.Add(m);
            }
            else if (broken && !m.Chest)
            {
                // Defer shell removal & fighter persistence so ReleaseArmy removes every stasis shell and persists fighters once in a single batch.
                uint wokenId = await LegionStasis.Wake(player, m, monster, e.X, e.Y,
                    e.Plan is { } wp && m.Faction < wp.Factions.Length ? wp.Factions[m.Faction].Id : null,
                    monolithId: e.Id, lifePerRewardPct: e.LifePerRewardPct, sextantLifePct: e.SextantLifePct,
                    sergeantRarityPct: e.SergeantRarityPct, deferRemove: true, deferPersist: true);
                if (wokenId != 0) { wokenIds.Add(wokenId); generalFought |= m.Tier == 3; }
                remove.Add(m);
            }
            else if (m.Tier == 3 && !m.Chest) flee.Add(m);
            else remove.Add(m);
            if (m.Revealed && !broken) ended.Add(m);   // still frozen: the action ends (state 5: speed restored, props gone)
        }
        LegionStasis.FlushFighters(player);
        // The action's end (R10: state 5), before the members that are removed go; a hidden one needs none.
        if (ended.Count > 0)
            await LegionOutput.ToArea(player, async p =>
            {
                foreach (var m in ended) await LegionStasis.SendState(p, m.Id, m.Sequence, LegionStasis.Ended, 100, null);
            });
        await LegionStasis.Remove(player, remove);
        // Spawn clickable Chest objects for broken-out War Chests / War Hoards only now, after removing their stasis shells.
        foreach (var (chest, cx, cy) in brokenChests)
            await SpawnBox(module, player, e, chest, cx, cy);
        // Snapshot current viewers before forgetting fleeing generals so a player joining during FleeSeconds is never sent
        // a removal for an object id it never received a create for.
        var fleeViewers = flee.Count > 0 ? Members(player).ToList() : null;
        foreach (var g in flee)
        {
            if (!player.Area.Monsters.TryGetValue(g.Id, out var monster)) continue;
            await LegionSpeech.Say(player, monster.Variety?.Id, "Fleeing");
            Holds.Remove(monster);
            // LIVE CHECK: with the action ended (state 5, sent above) and the animation running again, the client plays its flee animation (fleeing=1).
            if (LegionStasis.SendFreezeBuff)
                await LegionOutput.ToArea(player, async p =>
                {
                    await global::Game.SendWorldRecord(p, g.Id, InstanceEntities.RemoveBuff(g.Id, g.Buff), null);
                    await global::Game.SendWorldRecord(p, g.Id, InstanceProtocol.AnimationUpdate(g.Id, 0, 0, 1), null);
                });
            await LegionStasis.SetState(player, g.Id, monster, "fleeing");
            // The server forgets it now (no leak if the timer below is dropped, and it gives no rewards); only the client's removal waits.
            if (player.Area.Monsters.Remove(g.Id, out var gone)) { gone.Dead = true; gone.Life = 0; gone.ActionGeneration++; }
            player.Instance.World.MonsterOrigins.TryRemove(g.Id, out _);
            LegionOutput.ForgetComponents(player, g.Id);
        }
        if (flee.Count > 0 && fleeViewers is { Count: > 0 })
        {
            var inst = player.Instance;
            global::Game.SendAfter(player, FleeSeconds, async () =>
            {
                foreach (var viewer in fleeViewers)
                {
                    if (viewer.Closed || viewer.LeavingArea || !ReferenceEquals(viewer.Instance, inst)) continue;
                    await LegionOutput.ToOne(viewer, async () =>
                    {
                        foreach (var g in flee) await global::Game.SendWorldRemoval(viewer, g.Id);
                    });
                }
            });
        }
        string general = flee.Count > 0 ? "fled" : generalFought ? "fought" : "none";
        module.Log($"army released ({reason}): {brokenCount} of {army.Count} broken out ({wokenIds.Count} woken, {brokenChests.Count} chests), general {general}");
        return [.. wokenIds];
    }
}
