using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using static GameServer.Modules.Breach.BreachTables;

namespace GameServer.Modules.Breach;

/// <summary>
/// Breach Boss controllers:
/// <list type="bullet">
/// <item><b>Wild Breach Boss</b>: Vruun, Marshal of Xesht (<c>Metadata/Monsters/Breach/WildBreachBoss</c>) with
/// <c>BellBreach</c> (Flesh Bell) and <c>HandPortal</c> (Titan Hand Slam daemon) combat mechanics.</item>
/// <item><b>Pinnacle Boss (Hive Colony, <c>ChayulaLeagueTowerBoss</c>)</b>: opened via <c>MapDeviceRecipes#72</c>
/// (Hivebrain Gland) — dual-boss controller for <b>It That Was Tul</b> (<c>FallenKnight</c>) and <b>It That Was Esh</b>
/// (<c>HandMage</c>) with <c>EshTulDialogueTimerController</c>, <c>BirthingSack</c>, <c>BossForceFieldDoor</c>,
/// <c>VolatileOrbChaos</c>/<c>VolatileOrbLightning</c>, <c>BreachBossLifeScalingPerLevel</c>,
/// <c>BreachlordDescensionObject</c> (Breachlord Bloodline), return exit portal (<c>BrequelPortalBoss</c>),
/// and <c>world.db</c> <c>drop_boss_items</c> + Wombgift/Hiveblood drops.</item>
/// </list>
/// </summary>
public sealed class BreachBosses
{
    const string PinnacleStateKey = "Breach.PinnacleBoss";
    const string WildStateKey = "Breach.WildBoss";
    const string DefeatedFlag = "ChayulaTowerBossDefeated";

    // ---- Wild Breach Boss (Vruun, Marshal of Xesht) ------------------------------------------------------------------

    public sealed class WildBossRecord
    {
        public uint BossId { get; set; }
        public int SpawnX { get; set; }
        public int SpawnY { get; set; }
        public byte Level { get; set; }
        public double NextMechanicAt { get; set; }
        public List<uint> Adds { get; set; } = [];
        public bool Dead { get; set; }
    }

    public sealed class WildBossState
    {
        public List<WildBossRecord> Bosses { get; set; } = [];
        [JsonIgnore] internal SemaphoreSlim Gate { get; } = new(1, 1);
        [JsonIgnore] internal BreachAtlas? Atlas;
    }

    static readonly ConditionalWeakTable<InstanceState, WildBossState> WildStates = new();
    static readonly JsonSerializerOptions Json = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    static WildBossState? WildOf(InstanceState instance)
    {
        if (WildStates.TryGetValue(instance, out var known)) return known;
        if (!instance.ContentState.TryGetValue(WildStateKey, out var text)) return null;
        return WildStates.GetValue(instance, _ => JsonSerializer.Deserialize<WildBossState>(text, Json) ?? new());
    }

    static void SaveWild(InstanceState instance, WildBossState state) =>
        instance.ContentState[WildStateKey] = JsonSerializer.Serialize(state, Json);

    /// <summary>Spawns Vruun, Marshal of Xesht (<c>Metadata/Monsters/Breach/WildBreachBoss</c>) at <paramref name="at"/>.</summary>
    public static async Task<uint> SpawnWildBoss(Player runner, (int X, int Y) at, byte level, BreachAtlas atlas, Random rng)
    {
        if (runner.Instance is not { } instance) return 0;
        if (Monsters.Find(WildBossId) is not { Supported: true } variety)
        {
            Console.WriteLine($"[breach] Wild boss {WildBossId} unsupported in catalog");
            return 0;
        }
        var extra = atlas.BuildMonsterMods(level, isBoss: true);
        uint id = await SpawnMonster(runner, BreachEncounter.Risen(variety), level, MonsterRarity.Unique, at.X, at.Y,
            alone: false, extraMods: extra);
        if (id == 0) return 0;

        await SetMonsterStates(runner, id, ("minimap_icon", 1), ("boss_life_bar", 1), ("spawned_in_breach", 1), ("breachenraged", 1));
        var state = WildOf(instance) ?? WildStates.GetValue(instance, _ => new WildBossState { Atlas = atlas });
        await state.Gate.WaitAsync();
        try
        {
            state.Bosses.Add(new WildBossRecord
            {
                BossId = id,
                SpawnX = at.X,
                SpawnY = at.Y,
                Level = level,
                NextMechanicAt = AreaTime(runner) + 6.0,
            });
            SaveWild(instance, state);
        }
        finally { state.Gate.Release(); }
        foreach (var m in Members(runner))
            await PlayerOf(m).Tell("Vruun, Marshal of Xesht emerges from the Breach!");
        Console.WriteLine($"[breach] Spawned Vruun 0x{id:x} at ({at.X}, {at.Y}), level {level}");
        return id;
    }

    public async Task StepWild(Player runner, double seconds)
    {
        if (runner.Instance is not { } instance || WildOf(instance) is not { } state || !Current(runner) || runner.Paused
            || !IsMonsterRunner(runner)) return;
        if (!state.Bosses.Any(b => !b.Dead)) return;
        if (!await state.Gate.WaitAsync(0)) return;
        try
        {
            double now = AreaTime(runner);
            var rng = AreaRng(runner);
            bool dirty = false;
            foreach (var rec in state.Bosses)
            {
                if (rec.Dead) continue;
                if (!runner.Area.Monsters.TryGetValue(rec.BossId, out var boss) || boss.Dead || boss.Life == 0)
                {
                    await CleanupWildBoss(runner, rec, state.Atlas ?? BreachAtlas.From(runner));
                    dirty = true;
                    continue;
                }
                if (now < rec.NextMechanicAt) continue;
                rec.NextMechanicAt = now + 7.5;
                // Trim dead adds
                rec.Adds.RemoveAll(id => !runner.Area.Monsters.TryGetValue(id, out var m) || m.Dead || m.Life == 0);
                if (rec.Adds.Count >= 3) continue;

                var pos = boss.Walk.Position(now);
                string addId = rng.Next(2) == 0 ? WildBossBellId : WildBossHandId;
                if (Monsters.Find(addId) is { Supported: true } addVar)
                {
                    double a = rng.NextDouble() * Math.Tau;
                    var spot = runner.Layout.Near(((int)Math.Round(pos.X + Math.Cos(a) * 14),
                        (int)Math.Round(pos.Y + Math.Sin(a) * 14)));
                    uint aid = await SpawnMonster(runner, addVar, rec.Level, MonsterRarity.Normal, spot.X, spot.Y, alone: false);
                    if (aid != 0) rec.Adds.Add(aid);
                }
                dirty = true;
            }
            if (dirty) SaveWild(instance, state);
        }
        finally { state.Gate.Release(); }
    }

    static async Task CleanupWildBoss(Player runner, WildBossRecord rec, BreachAtlas atlas)
    {
        rec.Dead = true;
        var members = Members(runner);
        foreach (uint addId in rec.Adds)
        {
            if (runner.Area.Monsters.Remove(addId, out var m)) { m.Dead = true; m.Life = 0; m.ActionGeneration++; }
            runner.Instance.World.MonsterOrigins.TryRemove(addId, out _);
            foreach (var member in members) await SendWorldRemoval(member, addId, null);
        }
        rec.Adds.Clear();

        var rng = AreaRng(runner);
        int baseBlood = atlas.BossHivebloodInsteadOfGold ? 240 : 140;
        foreach (var member in members)
        {
            await GiveHiveblood(member, atlas.ScaleHiveblood(member, baseBlood));
            // Wombgift (1 guaranteed + Atlas BossAdditionalWombgiftChancePct for a 2nd)
            int gifts = 1 + (atlas.BossAdditionalWombgiftChancePct > 0 && rng.Next(100) < atlas.BossAdditionalWombgiftChancePct ? 1 : 0);
            for (int i = 0; i < gifts; i++)
            {
                string giftId = atlas.RollWombgiftBaseId(member, rng);
                if (MakeWombgift(member, giftId, rec.Level, rng) is { } gift)
                    await DropGroundItem(member, (rec.SpawnX, rec.SpawnY), gift, "Vruun Wombgift");
            }
            // Hivebrain Gland & Breach ring / gems drop via world.db drop_boss_items on Vruun's kill (OnDrops upgrades RingB1..5 to Rare).
            if (TryRollBodyPart?.Invoke(member, rec.Level, rng) is { } bodyPart)
                await DropGroundItem(member, (rec.SpawnX, rec.SpawnY), bodyPart, "Vruun quest body part");
        }
    }

    // ---- Pinnacle Boss: Hive Colony (ChayulaLeagueTowerBoss — It That Was Tul & It That Was Esh) ---------------------

    public sealed class PinnacleState
    {
        public byte Level { get; set; } = 83;
        public int ArenaX { get; set; }
        public int ArenaY { get; set; }
        public int SackX { get; set; }
        public int SackY { get; set; }
        public int DoorX { get; set; }
        public int DoorY { get; set; }
        public float DoorRot { get; set; }
        public int AltarX { get; set; }
        public int AltarY { get; set; }
        public float AltarRot { get; set; }
        public uint DialogueControllerId { get; set; }
        public uint BirthingSackId { get; set; }
        public uint DoorId { get; set; }
        public uint DescensionAltarId { get; set; }
        public uint ExitPortalId { get; set; }
        public uint TulId { get; set; }
        public uint EshId { get; set; }
        public bool Started { get; set; }
        public bool TulDead { get; set; }
        public bool EshDead { get; set; }
        public bool EnragedSurvivor { get; set; }
        public int LootRolled;
        public int Phase { get; set; } // 0=idle, 1=opening (100..70%), 2=mid (70..35%), 3=final (<35% or one dead), 4=victory
        public double NextOrbAt { get; set; }
        public double NextDialogueAt { get; set; }
        public List<uint> Orbs { get; set; } = [];
        [JsonIgnore] internal SemaphoreSlim Gate { get; } = new(1, 1);
        [JsonIgnore] internal bool ObjectsCreated;
    }

    readonly ConditionalWeakTable<InstanceState, PinnacleState> _pinnacle = new();

    public void Clear()
    {
        _pinnacle.Clear();
    }

    PinnacleState? PinnacleOf(InstanceState instance)
    {
        if (_pinnacle.TryGetValue(instance, out var known)) return known;
        if (!instance.ContentState.TryGetValue(PinnacleStateKey, out var text)) return null;
        return _pinnacle.GetValue(instance, _ => JsonSerializer.Deserialize<PinnacleState>(text, Json) ?? new());
    }

    static void SavePinnacle(InstanceState instance, PinnacleState state) =>
        instance.ContentState[PinnacleStateKey] = JsonSerializer.Serialize(state, Json);

    public async Task AreaGenerated(Player player)
    {
        if (player.Layout.Id != BossAreaId || player.Instance is not { } instance || PinnacleOf(instance) is not null) return;
        var level = player.Layout.Level;
        var roomObjs = level?.RoomObjects ?? [];

        var mid = roomObjs.FirstOrDefault(o => o.Type == "Metadata/MiscellaneousObjects/ArenaMiddle");
        var sack = roomObjs.FirstOrDefault(o => o.Type == BirthingSackType);
        var door = roomObjs.FirstOrDefault(o => o.Type == BossDoorType);
        var altar = roomObjs.FirstOrDefault(o => o.Type == DescensionObjectType);

        var arenaCentre = mid is not null ? (mid.X, mid.Y)
            : sack is not null ? (sack.X, sack.Y - 40)
            : player.Layout.Near((player.Layout.GridW / 2, player.Layout.GridH / 2));

        var state = new PinnacleState
        {
            Level = AreaLevel(player),
            ArenaX = arenaCentre.Item1,
            ArenaY = arenaCentre.Item2,
            SackX = sack?.X ?? arenaCentre.Item1,
            SackY = sack?.Y ?? (arenaCentre.Item2 + 40),
            DoorX = door?.X ?? (arenaCentre.Item1 + 40),
            DoorY = door?.Y ?? (arenaCentre.Item2 - 50),
            DoorRot = door?.Rotation ?? 0.76f,
            AltarX = altar?.X ?? (arenaCentre.Item1 - 45),
            AltarY = altar?.Y ?? (arenaCentre.Item2 + 30),
            AltarRot = altar?.Rotation ?? 0.99f,
            DialogueControllerId = instance.World.TakeObjectId(),
            BirthingSackId = instance.World.TakeObjectId(),
            DoorId = door is not null ? instance.World.TakeObjectId() : 0,
            DescensionAltarId = instance.World.TakeObjectId(),
        };

        _pinnacle.AddOrUpdate(instance, state);
        SavePinnacle(instance, state);
        await PresentPinnacleObjects(player, state);
        Console.WriteLine($"[breach] Hive Colony (ChayulaLeagueTowerBoss) ready at ({state.ArenaX}, {state.ArenaY}), level {state.Level}");
    }

    public async Task Enter(Player player)
    {
        if (player.Layout.Id != BossAreaId || player.Instance is not { } instance || PinnacleOf(instance) is not { } state) return;
        await state.Gate.WaitAsync();
        try
        {
            if (!state.ObjectsCreated) await PresentPinnacleObjects(player, state);
            if (state.Phase == 4 && state.ExitPortalId != 0)
                await EnsureExitPortal(player, state);
        }
        finally { state.Gate.Release(); }
    }

    async Task PresentPinnacleObjects(Player player, PinnacleState state)
    {
        state.ObjectsCreated = true;
        await CreateWorldObject(player, state.DialogueControllerId, DialogueControllerType, state.ArenaX, state.ArenaY,
            log: $"0x02bb EshTulDialogueTimerController 0x{state.DialogueControllerId:x}",
            states: [("talk_cooldown", 0)]);
        await CreateWorldObject(player, state.BirthingSackId, BirthingSackType, state.SackX, state.SackY,
            log: $"0x02bb BirthingSack 0x{state.BirthingSackId:x}",
            states: [("emerge", state.Started ? 1 : 0)]);
        if (state.DoorId != 0)
            await CreateWorldObject(player, state.DoorId, BossDoorType, state.DoorX, state.DoorY, state.DoorRot,
                blocked: state.Started && state.Phase < 4,
                transitionState: (byte)(state.Started && state.Phase < 4 ? 1 : 2));
        await CreateWorldObject(player, state.DescensionAltarId, DescensionObjectType, state.AltarX, state.AltarY, state.AltarRot,
            log: $"0x02bb BreachlordDescensionObject 0x{state.DescensionAltarId:x}",
            states: [("visible", state.Phase == 4 ? 1 : 0), ("enabled", state.Phase == 4 ? 1 : 0)]);
    }

    public async Task Step(Player runner, double seconds)
    {
        if (runner.Layout.Id != BossAreaId || runner.Instance is not { } instance || PinnacleOf(instance) is not { } state
            || !Current(runner) || runner.Paused || !IsMonsterRunner(runner)) return;
        if (state.Phase == 4) return;
        if (!await state.Gate.WaitAsync(0)) return;
        try
        {
            double now = AreaTime(runner);
            var members = Members(runner);
            bool dirty = false;

            // Trigger dual-boss fight when any living player enters the arena
            if (!state.Started)
            {
                bool inArena = members.Any(m => { if (m.PlayerLife == 0 || InGracePeriod(m)) return false; var (mx, my) = m.Walk.Position(now); return double.Hypot(mx - state.ArenaX, my - state.ArenaY) <= 95; });
                if (!inArena) return;
                await StartPinnacleFight(runner, members, state);
                SavePinnacle(instance, state);
                return;
            }

            // Track Tul and Esh life & states
            SpawnedMonster? tul = null, esh = null;
            bool tulAlive = state.TulId != 0 && runner.Area.Monsters.TryGetValue(state.TulId, out tul) && !tul.Dead && tul.Life > 0;
            bool eshAlive = state.EshId != 0 && runner.Area.Monsters.TryGetValue(state.EshId, out esh) && !esh.Dead && esh.Life > 0;

            if (!tulAlive && !state.TulDead) { state.TulDead = true; dirty = true; }
            if (!eshAlive && !state.EshDead) { state.EshDead = true; dirty = true; }

            // Both defeated -> Victory!
            if (state.TulDead && state.EshDead)
            {
                await CompletePinnacleFight(runner, members, state);
                SavePinnacle(instance, state);
                return;
            }

            // Enrage surviving twin when the other falls first
            if ((state.TulDead ^ state.EshDead) && !state.EnragedSurvivor)
            {
                state.EnragedSurvivor = true;
                state.Phase = 3;
                uint survivorId = state.TulDead ? state.EshId : state.TulId;
                string survivorName = state.TulDead ? "It That Was Esh" : "It That Was Tul";
                await SetMonsterStates(runner, survivorId, ("finalphasedialogue", 1), ("epk", 1), ("ultimate", 1));
                if (runner.Area.Monsters.TryGetValue(survivorId, out var surv) && !surv.Dead)
                {
                    uint heal = surv.MaxLife / 7;
                    surv.Life = Math.Min(surv.MaxLife, surv.Life + heal);
                    await SendWorldRecord(runner, survivorId, InstanceProtocol.LifeUpdate(survivorId, surv.Life), null);
                }
                foreach (var m in members)
                    await PlayerOf(m).Tell($"{survivorName} absorbs their fallen twin's essence and becomes Enraged!");
                dirty = true;
            }

            // Combined HP phase checks (70% -> Phase 2, 35% -> Phase 3)
            double tulPct = tulAlive && tul is not null ? (double)tul.Life / Math.Max(1u, tul.MaxLife) : 0;
            double eshPct = eshAlive && esh is not null ? (double)esh.Life / Math.Max(1u, esh.MaxLife) : 0;
            double avgPct = (tulPct + eshPct) * 0.5;
            if (state.Phase < 2 && avgPct <= 0.70)
            {
                state.Phase = 2;
                if (tulAlive) await SetMonsterStates(runner, state.TulId, ("epk", 1), ("ultimate", 1));
                if (eshAlive) await SetMonsterStates(runner, state.EshId, ("epk", 1), ("music_EnergyEruption", 1));
                dirty = true;
            }
            else if (state.Phase < 3 && avgPct <= 0.35)
            {
                state.Phase = 3;
                if (tulAlive) await SetMonsterStates(runner, state.TulId, ("finalphasedialogue", 1), ("ultimate", 1));
                if (eshAlive) await SetMonsterStates(runner, state.EshId, ("finalphasedialogue", 1), ("ultimate", 1));
                dirty = true;
            }

            // Dialogue controller cooldown pulse
            if (now >= state.NextDialogueAt)
            {
                state.NextDialogueAt = now + 15.0;
                await SetObjectStates(runner, state.DialogueControllerId, DialogueControllerType, ("talk_cooldown", 1));
                await SetObjectStates(runner, state.DialogueControllerId, DialogueControllerType, ("talk_cooldown", 0));
            }

            // Periodic Volatile Orbs (Chaos / Lightning) while Esh is alive
            if (eshAlive && now >= state.NextOrbAt)
            {
                state.NextOrbAt = now + (state.Phase >= 3 ? 6.5 : 10.0);
                state.Orbs.RemoveAll(id => !runner.Area.Monsters.TryGetValue(id, out var o) || o.Dead || o.Life == 0);
                if (state.Orbs.Count < 4)
                {
                    var rng = AreaRng(runner);
                    string orbId = rng.Next(2) == 0 ? VolatileOrbChaosId : VolatileOrbLightningId;
                    if (Monsters.Find(orbId) is { Supported: true } orbVar)
                    {
                        double a = rng.NextDouble() * Math.Tau;
                        var spot = runner.Layout.Near(((int)Math.Round(state.ArenaX + Math.Cos(a) * 28),
                            (int)Math.Round(state.ArenaY + Math.Sin(a) * 28)));
                        uint oid = await SpawnMonster(runner, orbVar, state.Level, MonsterRarity.Normal, spot.X, spot.Y, alone: false);
                        if (oid != 0) state.Orbs.Add(oid);
                    }
                    dirty = true;
                }
            }

            if (dirty) SavePinnacle(instance, state);
        }
        finally { state.Gate.Release(); }
    }

    async Task StartPinnacleFight(Player runner, List<Player> members, PinnacleState state)
    {
        double now = AreaTime(runner);
        state.Started = true;
        state.Phase = 1;
        state.NextOrbAt = now + 8.0;
        state.NextDialogueAt = now + 12.0;

        await SetObjectStates(runner, state.BirthingSackId, BirthingSackType, ("emerge", 1));
        if (state.DoorId != 0)
            await SetObjectBlocked(runner, state.DoorId, BossDoorType, blocked: true);

        int bossScale = BossLifeScale(state.Level);
        var extra = bossScale > 0
            ? new MonsterModSet([], [], new MonsterModEffects(new Dictionary<uint, int> { [MonsterModCatalog.EncounterLifeStat] = bossScale }))
            : null;

        var tulSpot = runner.Layout.Near((state.ArenaX - 18, state.ArenaY + 14));
        var eshSpot = runner.Layout.Near((state.ArenaX + 18, state.ArenaY + 14));

        if (Monsters.Find(FallenKnightId) is { Supported: true } tulVar)
        {
            state.TulId = await SpawnMonster(runner, tulVar, state.Level, MonsterRarity.Unique, tulSpot.X, tulSpot.Y,
                alone: false, extraMods: extra);
            if (state.TulId != 0)
                await SetMonsterStates(runner, state.TulId, ("boss_life_bar", 1), ("global_first_aggro", 1), ("epk", 1));
        }
        if (Monsters.Find(HandMageId) is { Supported: true } eshVar)
        {
            state.EshId = await SpawnMonster(runner, eshVar, state.Level, MonsterRarity.Unique, eshSpot.X, eshSpot.Y,
                alone: false, extraMods: extra);
            if (state.EshId != 0)
                await SetMonsterStates(runner, state.EshId, ("boss_life_bar", 1), ("global_first_aggro", 1), ("epk", 1), ("music_state", 1));
        }

        foreach (var m in members)
            await PlayerOf(m).Tell("It That Was Tul and It That Was Esh descend upon the Hive Colony!");
        Console.WriteLine($"[breach] Pinnacle dual-boss started: Tul=0x{state.TulId:x}, Esh=0x{state.EshId:x} (level {state.Level}, scale=+{bossScale}%)");
    }

    async Task CompletePinnacleFight(Player runner, List<Player> members, PinnacleState state)
    {
        state.Phase = 4;
        if (state.DoorId != 0)
            await SetObjectBlocked(runner, state.DoorId, BossDoorType, blocked: false);

        // Clean up leftover Volatile Orbs
        foreach (uint oid in state.Orbs)
        {
            if (runner.Area.Monsters.Remove(oid, out var m)) { m.Dead = true; m.Life = 0; m.ActionGeneration++; }
            runner.Instance.World.MonsterOrigins.TryRemove(oid, out _);
            foreach (var member in members) await SendWorldRemoval(member, oid, null);
        }
        state.Orbs.Clear();

        // Reveal Breachlord Bloodline Descension Altar
        await SetObjectStates(runner, state.DescensionAltarId, DescensionObjectType, ("visible", 1), ("enabled", 1));

        // Spawn exit portal (BrequelPortalBoss / AreaTransition return to hideout)
        state.ExitPortalId = runner.Instance.World.TakeObjectId();
        foreach (var member in members)
            await EnsureExitPortal(member, state);

        // Award Pinnacle Hiveblood, guaranteed Wombgifts (including Ancient), and quest flags
        var atlas = BreachAtlas.From(runner);
        var rng = AreaRng(runner);
        foreach (var member in members)
        {
            await SetQuestFlags(member, [DefeatedFlag]);
            await GiveHiveblood(member, atlas.ScaleHiveblood(member, 500));
            if (MakeWombgift(member, AncientWombgift, state.Level, rng) is { } ancient)
                await DropGroundItem(member, (state.ArenaX, state.ArenaY), ancient, "Hive Colony Ancient Wombgift");
            for (int i = 0; i < 2; i++)
            {
                string giftId = atlas.RollWombgiftBaseId(member, rng);
                if (MakeWombgift(member, giftId, state.Level, rng) is { } gift)
                    await DropGroundItem(member, (state.ArenaX + (i == 0 ? 6 : -6), state.ArenaY), gift, "Hive Colony Wombgift");
            }
            if (TryRollBodyPart?.Invoke(member, state.Level, rng) is { } bodyPart)
                await DropGroundItem(member, (state.ArenaX, state.ArenaY + 6), bodyPart, "Hive Colony quest body part");
            await PlayerOf(member).Tell("It That Was Tul and It That Was Esh have been defeated! The Breachlord Altar and Exit Portal have appeared.");
        }
        Console.WriteLine($"[breach] Hive Colony pinnacle encounter defeated!");
    }

    static async Task EnsureExitPortal(Player player, PinnacleState state)
    {
        if (state.ExitPortalId == 0) return;
        ushort dest = MapRunOf(player)?.ReturnTo?.Area
            ?? AreaTransitionCatalog.HashOf(MonasteryAreaId)
            ?? player.Layout.Hash;
        var (ax, ay) = player.Layout.Near((state.ArenaX, state.ArenaY - 16));
        var gate = new AreaTransitionObject(ax, ay, dest, "Metadata/Terrain/Leagues/Breach/Doodads/EntranceBeanStalk01.ao") { Kind = 3 };
        if (!player.Layout.AreaTransitions.Any(t => t.Id == state.ExitPortalId))
            player.AddAreaTransition(state.ExitPortalId, gate);
        if (!player.Area.Objects.Has(state.ExitPortalId))
            await SendWorldCreate(player, state.ExitPortalId,
                () => InstanceEntities.CreateObject(state.ExitPortalId, InstanceEntities.ObjectTypeHash(AreaTransitionObject.MetadataPath), gate.Blob()),
                $"0x02bb BrequelPortalBoss exit 0x{state.ExitPortalId:x} at ({ax}, {ay})", wake: true, awaitBirth: false);
    }

    public async Task<bool> OnUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (player.Layout.Id != BossAreaId || player.Instance is not { } instance || PinnacleOf(instance) is not { } state) return false;
        bool isAltar = state.DescensionAltarId != 0 && use.TargetId == state.DescensionAltarId;
        bool isExit = state.ExitPortalId != 0 && use.TargetId == state.ExitPortalId;
        if (!isAltar && !isExit) return false;
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;

        (int X, int Y) cell = isAltar ? (state.AltarX, state.AltarY) : (state.ArenaX, state.ArenaY - 16);
        int reach = isAltar ? 6 : 8;
        if (!TryBeginObjectWalk(player, requestedAt, cell.X, cell.Y, reach, out var walk)) return true;
        var (action, now, here, _, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, cell.X, cell.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId), null,
                still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);
        try
        {
            if (!PlayerActionCurrent(player, action) || player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;
            if (isAltar && state.Phase == 4 && Descendancies.Find("Breachlord") is { } bloodline)
                await OpenDescendancySelector(player, bloodline.Index);
            else if (isExit && state.Phase == 4)
            {
                if (MapRunOf(player)?.ReturnTo is { } ret)
                    await ChangeArea(player, new PendingTransfer(ret.Area, ret.Instance, false, ArriveAt: (ret.X, ret.Y)), "BrequelPortalBoss return");
                else if (AreaTransitionCatalog.HashOf(MonasteryAreaId) is { } monastery)
                    await ChangeArea(player, new PendingTransfer(monastery, 0, true, AtSpawn: true), "BrequelPortalBoss to Monastery");
            }
        }
        finally
        {
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    public Task OnMonsterKilled(MonsterKill kill) => Task.CompletedTask;

    // ---- world.db drop_boss_items integration (Vruun, It That Was Tul, It That Was Esh) ------------------------------

    sealed record BossRewardTable(DropDataProvider.Snapshot Source, Dictionary<string, ((string Item, double Weight)[] Uniques, (string Item, string Kind, double Chance)[] Others)> ByBoss);
    static BossRewardTable? _rewards;
    static readonly string[] TrackedBossIds = [WildBossId, FallenKnightId, HandMageId];

    static BossRewardTable Rewards(DropDataProvider.Snapshot snapshot)
    {
        if (Volatile.Read(ref _rewards) is { } cached && ReferenceEquals(cached.Source, snapshot)) return cached;
        var map = new Dictionary<string, ((string, double)[], (string, string, double)[])>(StringComparer.Ordinal);
        foreach (string bossId in TrackedBossIds)
        {
            if (snapshot.Bosses.TryGetValue(bossId, out var row) && !row.Enabled) continue;
            var uniques = new List<(string, double)>();
            var others = new List<(string, string, double)>();
            foreach (var item in snapshot.BossItems.Values)
            {
                if (item.BossId != bossId || (item.OverrideChance ?? item.Chance) is not double chance || chance <= 0) continue;
                if (item.ItemKind == "unique") uniques.Add((item.ItemId, chance));
                else others.Add((item.ItemId, item.ItemKind, Math.Min(1.0, chance)));
            }
            if (uniques.Count > 0 || others.Count > 0)
                map[bossId] = ([.. uniques], [.. others]);
        }
        var built = new BossRewardTable(snapshot, map);
        Volatile.Write(ref _rewards, built);
        return built;
    }

    /// <summary>Aligns with <c>world.db</c> <c>drop_boss_items</c> for Vruun, It That Was Tul, and It That Was Esh:
    /// <list type="bullet">
    /// <item>Vruun (<c>WildBreachBoss</c>): lets <c>ItemDrops.BossDrops</c> roll his guaranteed Breach Ring (<c>RingB1..5</c>),
    /// Hivebrain Gland, and gems, and upgrades any dropped Breach Ring base to Rare via <c>drop.AddTransform</c>.</item>
    /// <item>Tul &amp; Esh (<c>FallenKnight</c> / <c>HandMage</c>): cancels generic + <c>EncounterJewels</c> drops on both
    /// kills so neither twin double-drops, and rolls <c>drop_boss_items</c> once when the second twin falls.</item>
    /// </list>
    /// </summary>
    public void OnDrops(DropRoll drop)
    {
        string bossId = drop.Monster.VarietyId;
        if (bossId == WildBossId)
        {
            var rolls = RollsFor(drop.Player);
            var rng = AreaRng(drop.Player);
            drop.AddTransform(list =>
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var d = list[i];
                    if (d.Type.Id.StartsWith("Metadata/Items/Rings/RingB", StringComparison.Ordinal)
                        && d.Item.Rarity == ItemRarity.Normal)
                    {
                        var rare = rolls.Roll(d.Item, ItemRarity.Rare, rng);
                        list[i] = new ItemDrops.Drop(rare, d.Type);
                    }
                }
            });
            return;
        }
        if (bossId is not (FallenKnightId or HandMageId)) return;

        // Dual-boss encounter: always suppress generic/EncounterJewels drops on both kills to prevent double-dropping.
        drop.Cancel = true;

        // Roll the shared pinnacle boss table once, on the final twin's death (when the other twin is already dead).
        if (drop.Player.Instance is { } instance && PinnacleOf(instance) is { } state)
        {
            uint otherId = drop.Monster.ObjectId == state.TulId ? state.EshId : state.TulId;
            bool otherStillAlive = otherId != 0
                && drop.Player.Area.Monsters.TryGetValue(otherId, out var other)
                && !other.Dead && other.Life > 0;
            if (otherStillAlive) return;
            if (Interlocked.Exchange(ref state.LootRolled, 1) != 0) return;
        }

        var table = Rewards(DropDataProvider.Default.Current);
        if (!table.ByBoss.TryGetValue(bossId, out var entry)) return;
        var bossRng = AreaRng(drop.Player);

        double total = 0;
        foreach (var (_, w) in entry.Uniques) total += w;
        if (total > 0)
        {
            double pick = bossRng.NextDouble() * total;
            string chosen = entry.Uniques[^1].Item;
            foreach (var (item, w) in entry.Uniques)
            {
                if (pick < w) { chosen = item; break; }
                pick -= w;
            }
            drop.Items.Add(chosen);
        }
        foreach (var (item, kind, chance) in entry.Others)
        {
            if (bossRng.NextDouble() >= chance) continue;
            if (kind == "currency") drop.Stacks.Add((item, 1));
            else drop.Items.Add(item);
        }
        Console.WriteLine($"[breach] Pinnacle boss final victory ({bossId}) drops: {string.Join(", ", drop.Items.Concat(drop.Stacks.Select(s => s.Item)))}");
    }
}
