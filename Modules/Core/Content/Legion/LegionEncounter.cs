using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;

namespace GameServer.Modules.Legion;

/// <summary>
/// An area instance's Legion encounter: the Timeless Monolith, the two armies in stasis, the fight and the reward chests.
/// It belongs to the instance (not to a connection): it is rolled and planned once when the instance's monsters are placed,
/// stepped by the connection that runs the instance's monsters, driven by a click from any player, kept in the instance's
/// ContentState so a module reload carries on, and shown again to whoever enters (the ruleset row, its latest message and the
/// environment override; the monolith and the army are the area's own objects and monsters). The units are ordinary monsters
/// of their LegionMonsterVarieties row at the area's level, held in stasis by a LifePhaseGate at 1% (a killing blow stops
/// there and breaks the unit out) and BusyUntil = +inf, with their StateMachine carrying present, breakout, activated and
/// owning_initiator = the monolith. The chests' object paths come from LegionChestTypes -> Chests.
/// </summary>
public sealed class LegionEncounter
{
    const string StateKey = "Legion.Encounter";
    const int RollSalt = 0x4c65676e;

    public enum Stage { Hidden, Idle, Charging, Active, Frozen, Battle, Ending, Over }

    public enum UnitState { Stasis, Broken, Released, Gone, Dead }

    public sealed class Unit
    {
        public uint Id { get; set; }
        public string Faction { get; set; } = "";
        public LegionTables.Rank Rank { get; set; }
        public bool IsHoard { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public UnitState State { get; set; } = UnitState.Stasis;
        public string Rarity { get; set; } = "";
        /// <summary>The MonsterVarieties row this unit is spawned from (-1: the pool has none).</summary>
        public int VarietyRow { get; set; } = -1;
        /// <summary>A hoard's chest object path from LegionChestTypes -> Chests ("" when none).</summary>
        public string ChestPath { get; set; } = "";
        /// <summary>A hoard's reward type: a LegionRewardTypeVisuals Id, uniform over its rows.</summary>
        public string RewardType { get; set; } = "";
    }

    /// <summary>The instance's one monolith.</summary>
    public sealed class State
    {
        public uint Monolith { get; set; }
        public uint FlashLight { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public double Angle { get; set; }
        public string[] Factions { get; set; } = [];
        public int Level { get; set; } = 68;
        public Stage Stage { get; set; } = Stage.Hidden;
        public double StageAt { get; set; }
        public bool BattleOutSent { get; set; }
        public double? ClearedAt { get; set; }
        public bool Done { get; set; }
        public List<Unit> Units { get; set; } = [];
        /// <summary>The ruleset's latest state message (stage, step; 0: none yet) and whether the activated message went out.</summary>
        public int RuleStage { get; set; }
        public int RuleStep { get; set; }
        public bool Activated { get; set; }
        /// <summary>The Environments rows overriding the area now (what an entering client is sent, at once).</summary>
        public List<ushort> Environment { get; set; } = [];
        [JsonIgnore] internal SemaphoreSlim Gate { get; } = new(1, 1);
        [JsonIgnore] internal bool Dirty { get; set; }
        [JsonIgnore] internal double NextRevealTest { get; set; }
    }

    readonly ConditionalWeakTable<InstanceState, State> _states = new();
    readonly JsonSerializerOptions _json = new();

    /// <summary>The module unloads: the instances keep their encounter as text, for the next load.</summary>
    public void Clear() => _states.Clear();

    /// <summary>The instance's encounter, or null when it has none.</summary>
    public State? Of(InstanceState? instance)
    {
        if (instance is null) return null;
        if (_states.TryGetValue(instance, out var known)) return known;
        if (!instance.ContentState.TryGetValue(StateKey, out var text)) return null;
        return _states.GetValue(instance, _ => JsonSerializer.Deserialize<State>(text, _json) ?? new());
    }

    void Save(InstanceState instance, State state) => instance.ContentState[StateKey] = JsonSerializer.Serialize(state, _json);

    static bool Current(Player player) => player.Connection.PlayerSent && !player.LeavingArea && !player.Closed;

    static List<Player> Members(Player player) => [.. player.Instance.PlayersExcept(player.Id).Prepend(player)
        .Where(c => Current(c) && ReferenceEquals(c.Instance, player.Instance)).Distinct()];

    static byte AreaLevel(Player player) =>
        (byte)Math.Clamp(MapRunOf(player) is { Area.Level: > 0 } map ? map.Area.Level : ServerLevels.AreaLevel(player.Layout.Id), 1, 100);

    // ---- the roll and the plan ---------------------------------------------------------------------------------

    /// <summary>The test chance <c>Game_LEAGUE_CHANCES</c> gives the Legion row (<see cref="LeagueChances"/> reads it for the rows it
    /// rolls), or null.</summary>
    public static int? ForcedChance(string? setting)
    {
        foreach (var entry in (setting ?? "").Split(',', StringSplitOptions.TrimEntries))
            if (entry.Split('=') is [var id, var value] && id.Equals("Legion", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int c))
                return c;
        return null;
    }

    /// <summary>Whether the area has a monolith: with Game_LEAGUE_CHANCES naming Legion, that chance; else the client's own roll for
    /// CoreLeagues row 15 over the area's stats (<see cref="ExtraTerrainFeatures.LeagueCount"/>: map_disable_leagues,
    /// atlas_keystone_no_extra_content, map_nuke_everything, the row's disable and chance stats, the area's WorldAreaLeagueChances
    /// chance). A row's most per area is one.</summary>
    static bool Rolled(AreaPopulated area, Random rng)
    {
        var player = area.Player;
        if (ForcedChance(Environment.GetEnvironmentVariable("Game_LEAGUE_CHANCES")) is not null)
            return LeagueChances.Current.Count(player.Layout.Id, LeagueChances.Legion, rng) > 0;
        AreaInfo info;
        try { info = AreaInfo.Load(GamePack.Default.TryRead, player.Layout.Id, AreaLevel(player)); }
        catch (Exception e) when (e is ArgumentException or IOException or KeyNotFoundException) { return false; }
        var stats = (player.Instance.KeptAreaStats ?? AreaLeagueStats(player)).ToDictionary(s => (int)s.Key, s => s.Value);
        return ExtraTerrainFeatures.LeagueCount(info with { Stats = stats }, new LevelRandom(unchecked((uint)area.Seed ^ (uint)RollSalt), false),
            LeagueChances.Legion) > 0;
    }

    /// <summary>
    /// The plan: two distinct factions weighted by LegionFactions.SpawnWeight, and the units of each rank in their annulus on
    /// the lattice. The captures carry no faction value, so which two is drawn from the seed. The Random comes from the caller.
    /// </summary>
    public static (string[] Factions, bool HasGeneral, List<Unit> Units) Plan((int X, int Y) cell, double angle, int level, Random rng)
    {
        var pool = LegionTables.Factions.Where(f => f.Weight > 0).ToList();
        var factions = new List<string>();
        while (factions.Count < 2 && pool.Count > 0)
        {
            int x = rng.Next(pool.Sum(f => f.Weight)), acc = 0;
            for (int i = 0; i < pool.Count; i++)
            {
                acc += pool[i].Weight;
                if (x < acc) { factions.Add(pool[i].Id); pool.RemoveAt(i); break; }
            }
        }
        bool hasGeneral = rng.Next(100) < LegionTables.GeneralChancePct(level);
        int generalSide = rng.Next(2);
        var rarityWeights = LegionTables.RarityWeights(level);
        var units = new List<Unit>();
        for (int fi = 0; fi < factions.Count; fi++)
        {
            int side = fi == 0 ? +1 : -1;
            for (int rank = 0; rank < 4; rank++)
            {
                var (min, max) = LegionTables.Counts(factions[fi], (LegionTables.Rank)rank, level);
                int n = rank == (int)LegionTables.Rank.General ? (hasGeneral && fi == generalSide ? 1 : 0) : rng.Next(min, max + 1);
                var (lo, hi) = LegionTables.AnnulusPct(factions[fi], (LegionTables.Rank)rank, level);
                int[] rows = LegionTables.VarietyRows(factions[fi], (LegionTables.Rank)rank);
                for (int k = 0; k < n; k++)
                {
                    var at = LatticeCell(cell, angle, side, lo, hi, rng);
                    units.Add(new Unit
                    {
                        Faction = factions[fi], Rank = (LegionTables.Rank)rank, X = at.X, Y = at.Y,
                        Rarity = RarityOf(rank, rng, rarityWeights),
                        VarietyRow = rows.Length == 0 ? -1 : rows[rng.Next(rows.Length)],
                    });
                }
            }
            // Hoards: 0 to 2 chests from the rank-0 row, a War Hoard with its level's chance from the rank-1 row. A hoard is a
            // monster (its MonsterChest variety) that leaves its chest object behind.
            if (LegionTables.ChestType(factions[fi], epic: false) is { } chest)
            {
                var (chestMin, chestMax) = LegionTables.ChestRange(level);
                for (int k = rng.Next(chestMin, chestMax + 1); k > 0; k--)
                    units.Add(Hoard(factions[fi], chest, LegionTables.Rank.Soldier, LegionTables.ChestAnnulusPct[0], cell, angle, side, rng));
            }
            if (rng.Next(100) < LegionTables.WarHoardChancePct(factions[fi], level)
                && LegionTables.ChestType(factions[fi], epic: true) is { } warChest)
                units.Add(Hoard(factions[fi], warChest, LegionTables.Rank.Elite, LegionTables.ChestAnnulusPct[1], cell, angle, side, rng));
        }
        return ([.. factions], hasGeneral, units);
    }

    static Unit Hoard(string faction, (int MonsterRow, string ChestPath) chest, LegionTables.Rank rank, (double Lo, double Hi) annulus,
        (int X, int Y) cell, double angle, int side, Random rng)
    {
        var at = LatticeCell(cell, angle, side, annulus.Lo, annulus.Hi, rng);
        return new()
        {
            Faction = faction, Rank = rank, IsHoard = true, X = at.X, Y = at.Y,
            Rarity = "unique", VarietyRow = chest.MonsterRow, ChestPath = chest.ChestPath,
            RewardType = LegionTables.RewardSymbols.Count == 0 ? "" : LegionTables.RewardSymbols[rng.Next(LegionTables.RewardSymbols.Count)].Id,
        };
    }

    static string RarityOf(int rank, Random rng, (int Normal, int Magic, int Rare, int Unique)[] weights)
    {
        var (normal, magic, rare, unique) = weights[rank];
        int x = rng.Next(normal + magic + rare + unique);
        return x < normal ? "normal" : x < normal + magic ? "magic" : x < normal + magic + rare ? "rare" : "unique";
    }

    static MonsterRarity ParseRarity(string rarity) => rarity switch
    {
        "magic" => MonsterRarity.Magic,
        "rare" => MonsterRarity.Rare,
        "unique" => MonsterRarity.Unique,
        _ => MonsterRarity.Normal,
    };

    /// <summary>One lattice cell for a unit of a side: rows along the axis, 12 apart in a row, rows 10 apart, every other row
    /// shifted by 6, inside the annulus, drawn with weight 1/r.</summary>
    static (int X, int Y) LatticeCell((int X, int Y) cell, double angle, int side, double loPct, double hiPct, Random rng)
    {
        double ax = Math.Cos(angle), ay = Math.Sin(angle);
        double sx = Math.Cos(angle + side * Math.PI / 2), sy = Math.Sin(angle + side * Math.PI / 2);
        double lo = loPct / 100 * LegionTables.Radius, hi = hiPct / 100 * LegionTables.Radius;
        var pts = new List<(double Dx, double Dy, double R)>();
        for (int j = 0; ; j++)
        {
            double v = LegionTables.LatticeRow * j + LegionTables.LatticeRow / 2;
            if (v > LegionTables.Radius) break;
            double shift = j % 2 == 0 ? 0 : LegionTables.LatticeShift;
            for (double u = -LegionTables.Radius; u <= LegionTables.Radius; u += LegionTables.LatticeAlong)
            {
                double r = double.Hypot(u + shift, v);
                if (r >= Math.Max(LegionTables.Clear, lo) && r <= hi) pts.Add((u + shift, v, r));
            }
        }
        if (pts.Count == 0) return cell;
        double x = rng.NextDouble() * pts.Sum(p => 1.0 / Math.Max(1.0, p.R));
        var pick = pts[^1];
        foreach (var p in pts)
        {
            x -= 1.0 / Math.Max(1.0, p.R);
            if (x < 0) { pick = p; break; }
        }
        return ((int)Math.Round(cell.X + pick.Dx * ax + pick.Dy * sx), (int)Math.Round(cell.Y + pick.Dx * ay + pick.Dy * sy));
    }

    // ---- the unit's hold -----------------------------------------------------------------------------------------

    /// <summary>The StateMachine's index in the variety's own component list, or -1 when the type has none.</summary>
    public static int StateMachineIndex(MonsterCatalog.Variety variety) => variety.ComponentsOrBase.ToList().IndexOf("StateMachine");

    /// <summary>The unit's StateMachine values over the variety's own defaults: present, breakout, activated, and
    /// owning_initiator = the monolith. Null when the variety has no StateMachine.</summary>
    public static StateMachineData? StasisStates(MonsterCatalog.Variety variety, uint monolith, int present = 0, int breakout = 0, int activated = 0) =>
        variety.StateMachine is null || StateMachineIndex(variety) < 0 ? null : WithStasis(variety.StateMachine, monolith, present, breakout, activated);

    public static StateMachineData WithStasis(StateMachineData states, uint monolith, int present, int breakout, int activated) =>
        states.Set("present", present).Set("breakout", breakout).Set("activated", activated).Set("owning_initiator", (int)monolith);

    /// <summary>Holds a spawned monster in stasis: no AI order (BusyUntil = +inf), a killing blow stops at the gate's 1% floor
    /// instead of killing it, and no hit lands at all until the monolith activates.</summary>
    public static void HoldUnit(SpawnedMonster m)
    {
        m.BusyUntil = double.PositiveInfinity;
        m.LifePhases = new LifePhaseGate([1]);
        m.EntranceGrace = true;
    }

    /// <summary>Releases a broken-out unit into the fight: the gate is finished and cleared, its life is whole again and the
    /// AI may order it.</summary>
    public static void ReleaseUnit(SpawnedMonster m)
    {
        m.LifePhases?.Finish();
        m.LifePhases = null;
        m.Life = m.MaxLife;
        m.BusyUntil = 0;
        m.EntranceGrace = false;
        m.Aggro = true;
        m.LastTarget = null;
    }

    /// <summary>Whether this held monster took the blow that breaks it out: its life reached the gate's floor, so the gate arms
    /// and the monster takes no more damage until the timer ends.</summary>
    public static bool TryBreakOut(SpawnedMonster m) => m.LifePhases is { } gate && gate.Begin(m.Life, m.MaxLife);

    /// <summary>The unit's new StateMachine values: kept on the monster and in the instance's origin of it (so a client that
    /// joins later is created with them), then sent through the world output.</summary>
    async Task SendUnitStates(Player runner, State state, Unit u, int present, int breakout, int activated)
    {
        if (!runner.Area.Monsters.TryGetValue(u.Id, out var m) || m.Variety is not { } variety) return;
        var template = m.States ?? variety.StateMachine;
        int index = StateMachineIndex(variety);
        if (template is null || index < 0) return;
        m.States = WithStasis(template, state.Monolith, present, breakout, activated);
        if (runner.Instance.World.MonsterOrigins.TryGetValue(u.Id, out var origin))
            runner.Instance.World.MonsterOrigins[u.Id] = origin with { Object = origin.Object with { StateMachine = m.States } };
        await SendWorldRecord(runner, u.Id, InstanceEntities.StateMachineUpdate(u.Id, (byte)index, m.States),
            $"0x2bc Legion unit 0x{u.Id:x} StateMachine ({present},{breakout},{activated})");
    }

    /// <summary>Takes a living unit out of the instance as a removed one (no death, no drops, no experience): a corpse is left to
    /// the corpse's own way.</summary>
    static async Task RemoveUnit(Player runner, uint id)
    {
        var area = runner.Area;
        bool dormant = false;
        if (!area.Monsters.TryGetValue(id, out var monster))
        {
            if (!area.DormantMonsters.TryGetValue(id, out monster)) return;
            dormant = true;
        }
        if (monster.Dead || monster.Life == 0) return;
        monster.Life = 0;
        monster.Dead = true;
        monster.Aggro = false;
        monster.ActionGeneration++;
        if (dormant) area.DormantMonsters.Remove(id);
        else area.Monsters.Remove(id);
        runner.Instance.World.MonsterOrigins.TryRemove(id, out _);
        await SendWorldRemoval(runner, id, $"0x02bd Legion unit 0x{id:x} removed");
    }

    // ---- placement and replay ------------------------------------------------------------------------------------

    /// <summary>The instance's monsters are placed (GameEvents.AreaPopulated, once per instance): rolls the encounter and plans one
    /// monolith far from the arrival; the reveal waits for a player within <see cref="LegionTables.Reveal"/> cells. Returns why
    /// none was placed, null when one was.</summary>
    public async Task<string?> Place(AreaPopulated area)
    {
        var player = area.Player;
        var layout = player.Layout;
        if (player.Instance is not { } instance) return "no instance";
        if (Of(instance) is not null) return "the instance has its encounter already";
        if (LegionTables.RulesetRow is not { } row || LegionTables.Factions.Count == 0 || LegionPackets.MonolithMachine is null)
            return "the client data has no Legion ruleset, factions or monolith";
        if (!MapDevices.IsMapArea(layout.Hash)) return "not a map";
        if (layout.Walkable is not { } walkable) return "no walk map";
        // The encounter's own draws, apart from the packs' (the same instance gets the same encounter).
        var rng = new Random(area.Seed ^ RollSalt);
        if (!Rolled(area, rng)) return "the roll gave none";
        var transitions = layout.AreaTransitions.Select(t => (t.Transition.GridX, t.Transition.GridY)).ToList();
        var arrival = (layout.SpawnX, layout.SpawnY);
        // The official monoliths stand at least 400 cells from the arrival (the nearest of the captured ones is 422): take the
        // first of a few spots that far, else the last one.
        (int X, int Y)? spot = null;
        for (int tries = 0; tries < 4; tries++)
        {
            spot = AreaPopulation.EncounterSpot(layout.GridW, layout.GridH, walkable, arrival, transitions, area.Taken, rng);
            if (spot is { } s && double.Hypot(s.X - arrival.SpawnX, s.Y - arrival.SpawnY) >= LegionTables.EntryDist) break;
        }
        if (spot is not { } at) return "no open ground for a monolith";
        double angle = new[] { 0.0, Math.PI / 2, Math.PI, 3 * Math.PI / 2 }[rng.Next(4)];
        var (factions, _, units) = Plan(at, angle, area.Level, rng);
        var state = new State
        {
            X = at.X, Y = at.Y, Level = area.Level, Angle = angle, Factions = factions, Monolith = instance.World.TakeObjectId(),
            StageAt = AreaTime(player),
        };
        state.Units.AddRange(units);
        _states.AddOrUpdate(instance, state);
        Save(instance, state);
        var members = Members(player);
        if (!members.Contains(player)) members.Insert(0, player);
        // The ruleset first: the 0x2c9 messages for its row find no ruleset on the client without it.
        await Broadcast(members, InstanceProtocol.AddRuleset(row), $"0x001f ruleset LegionRules (row {row})");
        await Broadcast(members, Full(state), $"0x2c9 Legion monolith 0x{state.Monolith:x} at ({state.X}, {state.Y}), factions {string.Join("+", state.Factions)}");
        return null;
    }

    static byte[] Full(State state) =>
        LegionPackets.Full((state.X, state.Y), state.Level, state.Monolith, state.Angle, LegionTables.FreezeMs(state.Level) / 1000.0);

    /// <summary>The ruleset row for an entering client before the area's objects (GameEvents.EnterMessages).</summary>
    public IEnumerable<(string Log, byte[] Message)> EnterMessages(Player player) =>
        LegionTables.RulesetRow is { } row && Of(player.Instance) is { Done: false }
            ? [($"0x001f ruleset LegionRules (row {row})", InstanceProtocol.AddRuleset(row))] : [];

    /// <summary>A player entered the area (GameEvents.PlayerEntered: a party member, a re-entry through a portal or after a death):
    /// the monolith's first message, the latest state message and the environment override. The monolith and the army are the
    /// area's objects and monsters, which the area gives a later client as they are. A kept instance that came back without them
    /// (nobody was in it) starts the encounter over, hidden.</summary>
    public async Task Enter(Player player)
    {
        if (player.Instance is not { } instance || Of(instance) is not { Done: false } state) return;
        await state.Gate.WaitAsync();
        try
        {
            if (state.Stage != Stage.Hidden && !player.Area.Objects.Has(state.Monolith)) Reset(state);
            await Send(player, Full(state), $"0x2c9 Legion monolith 0x{state.Monolith:x}");
            if (state.Activated) await Send(player, LegionPackets.Activated(state.Monolith), "0x2c9 Legion activated");
            if (state.RuleStage > 0) await Send(player, LegionPackets.State(state.Monolith, state.RuleStage, state.RuleStep), $"0x2c9 Legion state ({state.RuleStage},{state.RuleStep})");
            if (state.Environment.Count > 0)
                await Send(player, LegionPackets.Environments([.. state.Environment.Select(r => (r, 0.0))]), "0xdf Legion environment");
        }
        finally { state.Gate.Release(); }
    }

    /// <summary>The army and the monolith are gone from the area (a kept instance's monsters and objects do not come back with
    /// the encounter's): it stands as planned, not yet revealed.</summary>
    static void Reset(State state)
    {
        state.Stage = Stage.Hidden;
        state.Activated = false;
        state.RuleStage = state.RuleStep = 0;
        state.Environment.Clear();
        state.BattleOutSent = false;
        state.ClearedAt = null;
        foreach (var u in state.Units) { u.Id = 0; u.State = UnitState.Stasis; }
        state.Dirty = true;
    }

    // ---- the frame -----------------------------------------------------------------------------------------------

    /// <summary>One frame of the area (GameEvents.AreaTick), on the connection that runs the instance's monsters.</summary>
    public async Task Step(Player runner)
    {
        if (runner.Instance is not { } instance || Of(instance) is not { Done: false } state || !Current(runner) || runner.Paused
            || !IsMonsterRunner(runner)) return;
        if (!await state.Gate.WaitAsync(0)) return;
        try
        {
            await Advance(runner, state);
            if (state.Dirty) { state.Dirty = false; Save(instance, state); }
        }
        finally { state.Gate.Release(); }
    }

    /// <summary>Advances the encounter by one tick: reveals it within 400 cells of any player, charges 45 frames after the click,
    /// freezes after the level's timer, and ends when nothing released is left alive (or after the cap).</summary>
    async Task Advance(Player runner, State state)
    {
        double now = AreaTime(runner);
        // While the army stands in stasis a killing blow breaks a unit out.
        if (state.Stage is Stage.Idle or Stage.Charging or Stage.Active) await BreakOutPass(runner, state);
        switch (state.Stage)
        {
            case Stage.Hidden:
                // Hidden is most of a map's life: test the players' distance a few times a second, not every frame.
                if (now < state.NextRevealTest) break;
                state.NextRevealTest = now + 0.25;
                if (Members(runner).Any(m => m.Walk.Position(AreaTime(m)) is var at
                        && double.Hypot(at.X - state.X, at.Y - state.Y) <= LegionTables.Reveal))
                    await Reveal(runner, state);
                break;
            case Stage.Charging:
                if (now - state.StageAt >= LegionTables.ChargeSeconds) await Activate(runner, state, now);
                break;
            case Stage.Active:
                if (now - state.StageAt >= LegionTables.FreezeMs(state.Level) / 1000.0) await Freeze(runner, state, now);
                break;
            case Stage.Frozen:
                if (now - state.StageAt >= LegionTables.BattleAfterFreezeSeconds)
                {
                    await RuleState(runner, state, 2, 4);
                    await SetMonolith(runner, state, 4, 1, 2, 0, 0, 1, 0);
                    state.Stage = Stage.Battle;
                    state.StageAt = now;
                    state.Dirty = true;
                }
                break;
            case Stage.Battle:
                if (ReleasedAlive(runner, state) == 0 && state.ClearedAt is null) { state.ClearedAt = now; state.Dirty = true; }
                if ((state.ClearedAt is not null && now - state.ClearedAt >= LegionTables.EndDelaySeconds) || now - state.StageAt >= LegionTables.BattleMaxSeconds)
                    await EndBattle(runner, state, now);
                break;
            case Stage.Ending:
                // The battle environment goes out 8 frames in and the post-battle one at 76, as the captures time them.
                if (now - state.StageAt >= 8.0 / 30 && !state.BattleOutSent)
                {
                    await Override(runner, state, "0xdf battle out", (LegionTables.EnvBattle, -2.0), (LegionTables.EnvPostBattle, 0.2));
                    state.Environment = [LegionTables.EnvPostBattle];
                    state.BattleOutSent = true;
                    state.Dirty = true;
                }
                if (now - state.StageAt >= 76.0 / 30)
                {
                    await Override(runner, state, "0xdf post-battle out", (LegionTables.EnvPostBattle, -2.0));
                    state.Environment = [];
                    state.Stage = Stage.Over;
                    state.Done = true;
                    state.Dirty = true;
                }
                break;
        }
    }

    static int ReleasedAlive(Player runner, State state)
    {
        int n = 0;
        foreach (var u in state.Units)
        {
            if (u.State != UnitState.Released) continue;
            if (runner.Area.Monsters.TryGetValue(u.Id, out var m) && !m.Dead && m.Life > 0) n++;
            else u.State = UnitState.Dead;
        }
        return n;
    }

    /// <summary>A message that is about no object goes to every client of the area by itself, each its own copy: the writer
    /// encrypts the buffer it is given in place.</summary>
    static async Task Broadcast(List<Player> members, byte[] message, string log)
    {
        for (int i = 0; i < members.Count; i++) await Send(members[i], message.ToArray(), i == 0 ? log : null);
    }

    static Task Override(Player runner, State state, string log, params (ushort Row, double Seconds)[] entries) =>
        Broadcast(Members(runner), LegionPackets.Environments(entries), log);

    Task RuleState(Player runner, State state, int stage, int step)
    {
        state.RuleStage = stage;
        state.RuleStep = step;
        state.Dirty = true;
        return Broadcast(Members(runner), LegionPackets.State(state.Monolith, stage, step), $"0x2c9 Legion state ({stage},{step})");
    }

    /// <summary>The monolith's StateMachine (its seven shared states) through the world output.</summary>
    static Task SetMonolith(Player runner, State state, params int[] values) =>
        LegionPackets.Monolith(values) is { } states
            ? SendWorldRecord(runner, state.Monolith, InstanceEntities.StateMachineUpdate(state.Monolith, LegionPackets.MonolithStateMachineIndex, states),
                $"0x2bc monolith StateMachine {values[0]}")
            : Task.CompletedTask;

    // ---- the stages ----------------------------------------------------------------------------------------------

    /// <summary>The reveal: the monolith, its light, and every unit as an ordinary monster held in stasis. A variety that
    /// cannot be resolved, is not supported or is an Endless one is not created. The General is created first: the monolith's
    /// body carries its id.</summary>
    async Task Reveal(Player runner, State state)
    {
        byte level = state.Level < 1 ? (byte)1 : (byte)Math.Min(state.Level, 100);
        async Task<bool> SpawnUnit(Unit u)
        {
            if (u.VarietyRow < 0 || Monsters.Find(u.VarietyRow.ToString()) is not { Supported: true } variety
                || variety.Id.Contains("Endless", StringComparison.Ordinal)) return false;
            uint id = await SpawnMonster(runner, variety, level, ParseRarity(u.Rarity), u.X, u.Y, log: false, alone: false, bulk: true,
                unrisen: false, startStates: StasisStates(variety, state.Monolith));
            if (id == 0) return false;
            u.Id = id;
            if (runner.Area.Monsters.TryGetValue(id, out var m))
            {
                HoldUnit(m);
                // It goes with the encounter: an instance kept with nobody in it does not save it (SavedMonster.Save).
                m.SummonerId = state.Monolith;
            }
            return true;
        }

        var general = state.Units.FirstOrDefault(u => u.Rank == LegionTables.Rank.General);
        uint generalId = general is not null && await SpawnUnit(general) ? general.Id : 0;
        state.FlashLight = runner.Instance.World.TakeObjectId();
        await SendWorldCreate(runner, state.Monolith, () => InstanceEntities.CreateObject(state.Monolith, InstanceEntities.ObjectTypeHash(LegionTables.MonolithPath),
            LegionPackets.MonolithBody((state.X, state.Y), generalId)),
            $"0x02bb create Legion monolith 0x{state.Monolith:x} at ({state.X}, {state.Y})", wake: true, awaitBirth: false);
        await SendWorldCreate(runner, state.FlashLight, () => InstanceEntities.CreateObject(state.FlashLight, InstanceEntities.ObjectTypeHash(LegionTables.FlashLightPath),
            LegionPackets.FlashLightBody((state.X, state.Y), state.Monolith)),
            $"0x02bb create LegionFlashLight 0x{state.FlashLight:x}", wake: true, awaitBirth: false);
        foreach (var u in state.Units)
            if (u != general) await SpawnUnit(u);
        state.Stage = Stage.Idle;
        state.StageAt = AreaTime(runner);
        state.Dirty = true;
    }

    /// <summary>A click on the monolith by any player: the walk up to it, then InteractionAction 0x00 and the StateMachine to
    /// charging, then 45 frames to the activation.</summary>
    public async Task Click(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (player.Instance is not { } instance || Of(instance) is not { } state) return;
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea || InGracePeriod(player)) return;
        if (state.Stage != Stage.Idle || !TryBeginObjectWalk(player, requestedAt, state.X, state.Y, LegionTables.ClickReach, out var walk)) return;
        var (action, now, here, _, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, state.X, state.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: state.Monolith), null,
                still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);
        await state.Gate.WaitAsync();
        try
        {
            if (state.Stage != Stage.Idle || !PlayerActionCurrent(player, action) || player.PlayerLife == 0
                || !PlayerWalk.InWalkUpRange(player.Walk.Position(AreaTime(player)), (state.X, state.Y), LegionTables.ClickReach)) return;
            state.Stage = Stage.Charging;
            state.StageAt = AreaTime(player);
            state.Dirty = true;
            await SendWorldRecord(player, state.Monolith, InstanceEntities.ComponentUpdate(state.Monolith, LegionPackets.MonolithInteractionIndex, [0]),
                "0x2bc monolith interaction 00");
            await SetMonolith(player, state, 0, 1, 0, 0, 0, 1, 1);
            Save(instance, state);
        }
        finally
        {
            state.Gate.Release();
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, action));
        }
    }

    async Task Activate(Player runner, State state, double now)
    {
        state.Stage = Stage.Active;
        state.StageAt = now;
        state.Dirty = true;
        await Override(runner, state, "0xdf Legion world", (LegionTables.EnvWorld, 0.5));
        state.Environment = [LegionTables.EnvWorld];
        await RuleState(runner, state, 1, 1);
        await RuleState(runner, state, 1, 1);
        state.Activated = true;
        await Broadcast(Members(runner), LegionPackets.Activated(state.Monolith), "0x2c9 Legion activated");
        int crystal = LegionTables.Crystal(state.Units.Count(u => u.IsHoard));
        await SetMonolith(runner, state, 2, 1, 1, crystal, 0, 1, 1);
        await SendWorldRecord(runner, state.FlashLight, InstanceEntities.ComponentUpdate(state.FlashLight, LegionPackets.FlashLightAnimatedIndex, [0]),
            "0x2bc flashlight Animated 00");
        // Time stops: the army can be hit now (the 1% floor still holds a unit until it breaks out). Every unit's present = 1;
        // a broken-out one keeps breakout = 1.
        foreach (var u in state.Units.Where(u => u.Id != 0))
            if (runner.Area.Monsters.TryGetValue(u.Id, out var held)) held.EntranceGrace = false;
        foreach (var u in state.Units.Where(u => u.Id != 0 && u.State is UnitState.Stasis or UnitState.Broken))
            await SendUnitStates(runner, state, u, 1, u.State == UnitState.Broken ? 1 : 0, 0);
        await RuleState(runner, state, 1, 2);
    }

    /// <summary>The poll over the whole army while it stands: every unit in stasis whose life reached the gate's 1% floor
    /// breaks out, in one pass.</summary>
    async Task BreakOutPass(Player runner, State state)
    {
        List<Unit>? broken = null;
        foreach (var u in state.Units)
        {
            if (u.Id == 0 || u.State != UnitState.Stasis) continue;
            if (!runner.Area.Monsters.TryGetValue(u.Id, out var m) || !TryBreakOut(m)) continue;
            u.State = UnitState.Broken;
            (broken ??= []).Add(u);
        }
        if (broken is null) return;
        state.Dirty = true;
        foreach (var u in broken) await SendUnitStates(runner, state, u, 1, 1, 0);
    }

    /// <summary>Time stops: a broken-out unit is released into the fight, a broken-out hoard leaves its chest (and its reward
    /// symbol) where it stood, and every other unit is removed.</summary>
    async Task Freeze(Player runner, State state, double now)
    {
        state.Stage = Stage.Frozen;
        state.StageAt = now;
        state.Dirty = true;
        await RuleState(runner, state, 1, 3);
        await Override(runner, state, "0xdf Legion world out", (LegionTables.EnvWorld, -2.0));
        await Override(runner, state, "0xdf Legion battle", (LegionTables.EnvBattle, 0.0));
        state.Environment = [LegionTables.EnvBattle];
        await SetMonolith(runner, state, 3, 1, 2, 0, 0, 1, 1);
        foreach (var u in state.Units)
        {
            if (u.Id == 0) { u.State = UnitState.Gone; continue; }
            if (u.IsHoard)
            {
                if (u.State == UnitState.Broken && u.ChestPath.Length > 0) await LeaveChest(runner, u);
                await RemoveUnit(runner, u.Id);
                u.State = UnitState.Gone;
            }
            else if (u.State == UnitState.Broken)
            {
                await SendUnitStates(runner, state, u, 1, 1, 1);
                uint? maxLife = null;
                if (runner.Area.Monsters.TryGetValue(u.Id, out var m)) { ReleaseUnit(m); maxLife = m.MaxLife; }
                if (maxLife is not null)
                    await SendWorldRecord(runner, u.Id, InstanceProtocol.LifeUpdate(u.Id, maxLife.Value), $"0x1b1 Legion unit 0x{u.Id:x} life (released)");
                u.State = UnitState.Released;
            }
            else
            {
                await RemoveUnit(runner, u.Id);
                u.State = UnitState.Gone;
            }
        }
    }

    /// <summary>A broken-out hoard's chest object at its cell, with the symbol of its reward type above it: the chest's icon
    /// stat carries the symbol row's MinimapIconsKey + 1.</summary>
    async Task LeaveChest(Player runner, Unit u)
    {
        uint symbol = 0;
        int icon = 0;
        if (LegionTables.RewardSymbol(u.RewardType) is { } row)
        {
            symbol = runner.Instance.World.TakeObjectId();
            uint symbolId = symbol;
            await SendWorldCreate(runner, symbolId, () => InstanceEntities.CreateObject(symbolId, InstanceEntities.ObjectTypeHash(LegionTables.SymbolPath),
                LegionPackets.SymbolBody((u.X, u.Y), row.Ao, row.Scale)),
                $"0x02bb create Legion reward symbol 0x{symbol:x} at ({u.X}, {u.Y})", awaitBirth: false);
            icon = row.MinimapIcon;
        }
        uint chest = runner.Instance.World.TakeObjectId();
        await SendWorldCreate(runner, chest, () => InstanceEntities.CreateObject(chest, InstanceEntities.ObjectTypeHash(u.ChestPath),
            LegionPackets.ChestBody((u.X, u.Y), 0f, symbol, icon)),
            $"0x02bb create Legion chest 0x{chest:x} at ({u.X}, {u.Y})", wake: true, awaitBirth: false);
    }

    async Task EndBattle(Player runner, State state, double now)
    {
        state.Stage = Stage.Ending;
        state.StageAt = now;
        state.Dirty = true;
        await RuleState(runner, state, 2, 5);
        await SetMonolith(runner, state, 5, 1, 2, 0, 0, 1, 0);
        await Override(runner, state, "0xdf post-battle", (LegionTables.EnvBattle, 0.0), (LegionTables.EnvPostBattle, 0.2));
        state.Environment = [LegionTables.EnvBattle, LegionTables.EnvPostBattle];
    }
}
