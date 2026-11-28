using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Terrain.Generation;
using static GameServer.Modules.Breach.BreachTables;

namespace GameServer.Modules.Breach;

/// <summary>
/// Hive Fortress sub-area (<c>BrequelSkyHiveEncounter</c> -&gt; <c>ChayulaLeagueTower</c>):
/// wired via <see cref="GameEvents.FeatureFilled"/> (same sub-area link pattern as <c>SacredGrove</c>),
/// with map-side <c>BrequelStalkFortress</c> beanstalk, tower corridor guards &amp; Fortress Chests,
/// core Ailith defense (<c>BrequelStabiliserFortress</c> + <c>BrequelBlocker</c> walls + Ailith skills + waves),
/// fortress explosion &amp; timed escape sequence (<see cref="InstanceTimers.MissionTimerType.Brequel"/>),
/// and Hivebrain Gland + Wombgift + Hiveblood rewards.
/// </summary>
public sealed class BreachFortress
{
    const string StateKey = "Breach.Fortress";
    const uint RollSalt = 0x464f5254;
    const int BaseFortressWaves = 6;
    const int MaxAilithHits = 28;
    const double WaveDurationSeconds = 16.0;
    const double WaveSpawnInterval = 2.2;
    const uint EscapeSeconds = 45;

    public const int PhaseIdle = 0, PhaseDefense = 1, PhaseEscaping = 2, PhaseComplete = 3, PhaseFailed = 4;

    public sealed class FortressWall
    {
        public uint Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public float Rotation { get; set; }
        public bool Destroyed { get; set; }
    }

    public sealed class FortressChest
    {
        public uint Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public float Rotation { get; set; }
        public string Model { get; set; } = BrequelChestModels[0];
        public bool Opened { get; set; }
        public bool Bonus { get; set; }
    }

    public sealed class FortressSkill
    {
        public uint Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int SkillRow { get; set; }
        public bool Ready { get; set; } = true;
        public double CooldownUntil { get; set; }
    }

    public sealed class FortressState
    {
        public bool WholeArea { get; set; }
        public int SubX0 { get; set; }
        public int SubY0 { get; set; }
        public int SubX1 { get; set; }
        public int SubY1 { get; set; }
        public int CoreX { get; set; }
        public int CoreY { get; set; }
        public int AilithX { get; set; }
        public int AilithY { get; set; }
        public int ExitX { get; set; }
        public int ExitY { get; set; }
        public uint StalkId { get; set; }
        public int StalkX { get; set; }
        public int StalkY { get; set; }
        public float StalkRot { get; set; }
        public uint StabiliserId { get; set; }
        public uint AilithId { get; set; }
        public int Phase { get; set; }
        public int Wave { get; set; }
        public int TotalWaves { get; set; } = BaseFortressWaves;
        public int HitsTaken { get; set; }
        public int MaxHits { get; set; } = MaxAilithHits;
        public int Kills { get; set; }
        public bool Enraged { get; set; }
        public double ShieldUntil { get; set; }
        public double WaveStartedAt { get; set; }
        public double NextSpawnAt { get; set; }
        public double NextHitAt { get; set; }
        public double EscapeEndsAt { get; set; }
        public bool EnvironmentOn { get; set; }
        public List<FortressWall> Walls { get; set; } = [];
        public List<FortressChest> Chests { get; set; } = [];
        public List<FortressSkill> Skills { get; set; } = [];
        public List<uint> Monsters { get; set; } = [];
        public HashSet<uint> CreditedDeaths { get; set; } = [];
        [JsonIgnore] internal SemaphoreSlim Gate { get; } = new(1, 1);
        [JsonIgnore] internal BreachAtlas? Atlas;
        [JsonIgnore] internal bool ObjectsCreated;
        [JsonIgnore] internal HashSet<uint> AllMonsterIds { get; } = [];
    }

    readonly ConditionalWeakTable<InstanceState, FortressState> _fortresses = new();
    readonly JsonSerializerOptions _json = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    public void Clear() => _fortresses.Clear();

    public FortressState? Of(InstanceState instance)
    {
        if (_fortresses.TryGetValue(instance, out var known)) return known;
        if (!instance.ContentState.TryGetValue(StateKey, out var text)) return null;
        return _fortresses.GetValue(instance, _ =>
        {
            var restored = JsonSerializer.Deserialize<FortressState>(text, _json) ?? new();
            foreach (uint id in restored.Monsters) restored.AllMonsterIds.Add(id);
            return restored;
        });
    }

    public bool IsActivePause(Player player) =>
        player.Instance is { } instance && _fortresses.TryGetValue(instance, out var f)
        && f.Phase is PhaseDefense or PhaseEscaping;

    void Save(InstanceState instance, FortressState state) =>
        instance.ContentState[StateKey] = JsonSerializer.Serialize(state, _json);

    static BreachAtlas AtlasOf(Player player, FortressState state) => state.Atlas ??= BreachAtlas.From(player);

    static bool HasFortress(Player player, out DoodadRecord? subRecord, out bool wholeArea)
    {
        subRecord = player.Layout.Level?.Records.FirstOrDefault(r => r.FeatureRow == SkyHiveFeatureRow);
        wholeArea = player.Layout.Id == FortressAreaId && subRecord is null;
        return subRecord is not null || wholeArea;
    }

    bool InSubArea(FortressState f, (int X, int Y) pos) =>
        f.WholeArea || (pos.X >= f.SubX0 && pos.X < f.SubX1 && pos.Y >= f.SubY0 && pos.Y < f.SubY1);

    public IEnumerable<(string Log, byte[] Message)> EnterMessages(Player player)
    {
        if (player.Instance is not { } instance || Of(instance) is not { } f) yield break;
        if (EstablishedRulesetRow is { } est)
            yield return ($"0x001f ruleset EstablishedBrequelEncounterRules (row {est})", InstanceProtocol.AddRuleset(est));
        if (BarrierRulesetRow is { } bar)
            yield return ($"0x001f ruleset BrequelBarrierRules (row {bar})", InstanceProtocol.AddRuleset(bar));
    }

    public async Task AreaGenerated(Player player)
    {
        if (player.Instance is not { } instance || Of(instance) is not null) return;
        if (!HasFortress(player, out var subRec, out bool whole)) return;

        var layout = player.Layout;
        var level = layout.Level;
        var atlas = BreachAtlas.From(player);
        var rng = new Random(unchecked((int)(player.Seed ^ RollSalt)));

        int x0 = whole ? 0 : subRec!.X * 23;
        int y0 = whole ? 0 : subRec!.Y * 23;
        int x1 = whole ? layout.GridW : (subRec!.X + subRec.W) * 23;
        int y1 = whole ? layout.GridH : (subRec!.Y + subRec.H) * 23;

        bool InRect(PlacedTileObject o) => whole || (o.X >= x0 && o.X < x1 && o.Y >= y0 && o.Y < y1);
        var subObjs = level?.RoomObjects.Where(InRect).ToList() ?? [];

        var coreStab = subObjs.FirstOrDefault(o => o.Type is StabiliserFortressType or StabiliserType);
        var ailithObj = subObjs.FirstOrDefault(o => o.Type is ChayulaFarmerWildMonsterId or ChayulaFarmerNpcType);
        var core = coreStab is not null ? (coreStab.X, coreStab.Y) : layout.Near(((x0 + x1) / 2, (y0 + y1) / 2));
        var ailith = ailithObj is not null ? (ailithObj.X, ailithObj.Y) : layout.Near((core.X, core.Y + 14));

        // Find map-side BrequelStalkFortress beanstalk (from brequel_sky_fortress_entrance.arm)
        var stalkObj = level?.RoomObjects.FirstOrDefault(o => o.Type == StalkFortressType);
        // Find return/exit transition inside the sub-area
        var exitGate = layout.AreaTransitions.FirstOrDefault(t =>
            t.Transition.Feature == SkyHiveFeatureRow && InSubArea(new FortressState { WholeArea = whole, SubX0 = x0, SubY0 = y0, SubX1 = x1, SubY1 = y1 }, (t.Transition.GridX, t.Transition.GridY)));
        var exitCell = exitGate.Transition is not null ? (exitGate.Transition.GridX, exitGate.Transition.GridY) : (layout.SpawnX, layout.SpawnY);

        var state = new FortressState
        {
            WholeArea = whole,
            SubX0 = x0, SubY0 = y0, SubX1 = x1, SubY1 = y1,
            CoreX = core.X, CoreY = core.Y,
            AilithX = ailith.X, AilithY = ailith.Y,
            ExitX = exitCell.Item1, ExitY = exitCell.Item2,
            StabiliserId = instance.World.TakeObjectId(),
            AilithId = instance.World.TakeObjectId(),
            TotalWaves = Math.Clamp(BaseFortressWaves + atlas.HivesAdditionalWaves, 4, 12),
            Atlas = atlas,
        };

        if (stalkObj is not null)
        {
            state.StalkId = instance.World.TakeObjectId();
            state.StalkX = stalkObj.X;
            state.StalkY = stalkObj.Y;
            state.StalkRot = stalkObj.Rotation;
        }

        // Core BrequelBlocker walls from core_01..05.arm (cap at 24 so we keep creation snappy)
        var wallObjs = subObjs.Where(o => o.Type == BlockerType).Take(24).ToList();
        foreach (var w in wallObjs)
        {
            state.Walls.Add(new FortressWall
            {
                Id = instance.World.TakeObjectId(),
                X = w.X,
                Y = w.Y,
                Rotation = w.Rotation,
            });
        }

        // Fortress chests scattered along TowerPathNode rooms & corridors
        var pathNodes = subObjs.Where(o => o.Type.Contains("TowerPathNode", StringComparison.OrdinalIgnoreCase)).ToList();
        int chestCount = Math.Clamp(6 + atlas.AdditionalChests, 4, 12);
        for (int i = 0; i < chestCount; i++)
        {
            (int X, int Y) spot;
            if (pathNodes.Count > 0)
            {
                var node = pathNodes[rng.Next(pathNodes.Count)];
                spot = layout.Near((node.X + rng.Next(-10, 11), node.Y + rng.Next(-10, 11)));
            }
            else
            {
                double angle = i * Math.Tau / chestCount;
                spot = layout.Near(((int)Math.Round(core.X + Math.Cos(angle) * (40 + rng.Next(45))),
                    (int)Math.Round(core.Y + Math.Sin(angle) * (40 + rng.Next(45)))));
            }
            state.Chests.Add(new FortressChest
            {
                Id = instance.World.TakeObjectId(),
                X = spot.X,
                Y = spot.Y,
                Rotation = (float)(rng.NextDouble() * Math.Tau),
                Model = BrequelChestModels[i % BrequelChestModels.Length],
            });
        }

        // Ailith encounter skill objects around the fortress core
        int skillCount = Math.Clamp((int)Math.Round(4 * Math.Max(0.5, 1.0 + atlas.HiveNumberOfSkillsPct / 100.0)), 3, 10);
        var skillPool = EncounterSkills.Count > 0 ? EncounterSkills.Select(s => s.Row).ToList() : [.. Enumerable.Range(0, 10)];
        for (int i = 0; i < skillCount; i++)
        {
            double angle = i * Math.Tau / skillCount;
            var spot = layout.Near(((int)Math.Round(core.X + Math.Cos(angle) * 18),
                (int)Math.Round(core.Y + Math.Sin(angle) * 18)));
            state.Skills.Add(new FortressSkill
            {
                Id = instance.World.TakeObjectId(),
                X = spot.X,
                Y = spot.Y,
                SkillRow = atlas.HiveSkillsAreAllEnrageMonsters ? 9 : skillPool[rng.Next(skillPool.Count)],
                Ready = true,
            });
        }

        _fortresses.AddOrUpdate(instance, state);
        Save(instance, state);
        await PresentObjects(player, state);
        await PopulateCorridorGuards(player, state, pathNodes, atlas, rng);
        Console.WriteLine($"[breach] Hive Fortress built in {layout.Id}: core=({core.X},{core.Y}), walls={state.Walls.Count}, chests={state.Chests.Count}, waves={state.TotalWaves}");
    }

    public async Task Enter(Player player)
    {
        if (player.Instance is not { } instance || Of(instance) is not { } state) return;
        await state.Gate.WaitAsync();
        try
        {
            if (EstablishedRulesetRow is { } est)
                await Send(player, InstanceProtocol.AddRuleset(est), $"0x001f ruleset EstablishedBrequelEncounterRules (row {est})");
            if (BarrierRulesetRow is { } bar)
                await Send(player, InstanceProtocol.AddRuleset(bar), $"0x001f ruleset BrequelBarrierRules (row {bar})");
            if (!state.ObjectsCreated) await PresentObjects(player, state);
            await SendFortressRulesetUpdate([player], state);
            if (state.Phase == PhaseEscaping)
            {
                double rem = Math.Max(1.0, state.EscapeEndsAt - AreaTime(player));
                await Send(player, InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Brequel, (uint)Math.Ceiling(rem)),
                    "0x0163 Hive Fortress escape timer");
            }
        }
        finally { state.Gate.Release(); }
    }

    async Task PresentObjects(Player player, FortressState state)
    {
        state.ObjectsCreated = true;
        if (state.StalkId != 0)
            await CreateWorldObject(player, state.StalkId, StalkFortressType, state.StalkX, state.StalkY, state.StalkRot,
                states: [("fortress_complete", state.Phase is PhaseEscaping or PhaseComplete ? 1 : 0)]);

        await CreateWorldObject(player, state.StabiliserId, StabiliserType, state.CoreX, state.CoreY,
            blocked: state.Phase < PhaseEscaping,
            log: $"0x02bb Hive Fortress Core Stabiliser 0x{state.StabiliserId:x} at ({state.CoreX}, {state.CoreY})",
            states: [
                ("interaction_state", state.Phase == PhaseIdle ? 0 : 1),
                ("bomb_defence", state.Phase switch { PhaseDefense => 1, PhaseEscaping or PhaseComplete => 2, PhaseFailed => 3, _ => 0 }),
            ]);

        foreach (var wall in state.Walls.Where(w => !w.Destroyed))
            await CreateWorldObject(player, wall.Id, BlockerType, wall.X, wall.Y, wall.Rotation,
                blocked: true, states: [("destroyed", 0), ("breakable_by_players", 1)]);

        foreach (var chest in state.Chests)
            await CreateWorldObject(player, chest.Id, ChestBaseType, chest.X, chest.Y, chest.Rotation,
                model: chest.Model, opened: chest.Opened, states: [("open", chest.Opened ? 1 : 0)]);

        foreach (var pad in state.Skills)
            await CreateWorldObject(player, pad.Id, SkillObjectType, pad.X, pad.Y,
                states: [
                    ("activated", 0),
                    ("brequel_skill_row_index_plus_one", pad.SkillRow + 1),
                    ("ready", pad.Ready && state.Phase == PhaseDefense ? 1 : 0),
                ]);
    }

    async Task PopulateCorridorGuards(Player player, FortressState state, List<PlacedTileObject> pathNodes, BreachAtlas atlas, Random rng)
    {
        if (FortPacks.Count == 0) return;
        byte level = AreaLevel(player);
        var extra = atlas.BuildMonsterMods(level, isBoss: false);
        var layout = player.Layout;
        bool Open(int x, int y) => x >= 0 && y >= 0 && x < layout.GridW && y < layout.GridH && layout.WalkableFor(x, y, 1);

        int guardPacks = Math.Min(10, Math.Max(4, pathNodes.Count / 4));
        using (HoldMonstersRemaining(player))
        {
            for (int i = 0; i < guardPacks; i++)
            {
                var anchor = pathNodes.Count > 0
                    ? (pathNodes[rng.Next(pathNodes.Count)].X, pathNodes[rng.Next(pathNodes.Count)].Y)
                    : layout.Near(((int)Math.Round(state.CoreX + Math.Cos(i * Math.Tau / guardPacks) * 55),
                        (int)Math.Round(state.CoreY + Math.Sin(i * Math.Tau / guardPacks) * 55)));
                var pack = FortPacks[rng.Next(FortPacks.Count)];
                var rarity = atlas.RollPackRarity(rng);
                var spawns = AreaPopulation.AtPacks([(pack, anchor.Item1, anchor.Item2, rarity)], Open,
                    v => Monsters.Find(v.ToString()) is { Supported: true }, rng.Next(), 1.0);
                foreach (var spawn in spawns)
                {
                    if (Monsters.Find(spawn.Variety.ToString()) is not { Supported: true } variety) continue;
                    uint id = await SpawnMonster(player, BreachEncounter.Risen(variety), level, spawn.Rarity, spawn.X, spawn.Y,
                        alone: false, extraMods: extra, bulk: true);
                    if (id != 0) { state.Monsters.Add(id); state.AllMonsterIds.Add(id); }
                }
            }
        }
        await SendRemainingMonsters(player);
    }

    public async Task<bool> OnUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (player.Instance is not { } instance || Of(instance) is not { } state) return false;
        bool isCore = use.TargetId == state.StabiliserId;
        var chest = state.Chests.FirstOrDefault(c => c.Id == use.TargetId);
        var skill = state.Skills.FirstOrDefault(s => s.Id == use.TargetId);
        var wall = state.Walls.FirstOrDefault(w => w.Id == use.TargetId);
        if (!isCore && chest is null && skill is null && wall is null) return false;
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;

        (int X, int Y) cell = isCore ? (state.CoreX, state.CoreY)
            : chest is not null ? (chest.X, chest.Y)
            : skill is not null ? (skill.X, skill.Y)
            : (wall!.X, wall.Y);
        int reach = isCore ? 15 : 8;
        if (!TryBeginObjectWalk(player, requestedAt, cell.X, cell.Y, reach, out var walk)) return true;
        var (action, now, here, _, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, cell.X, cell.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId), null,
                still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);
        await state.Gate.WaitAsync();
        try
        {
            if (!PlayerActionCurrent(player, action) || player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;
            if (isCore && state.Phase == PhaseIdle)
                await StartFortressDefense(player, state);
            else if (chest is not null && !chest.Opened)
                await OpenFortressChest(player, state, chest);
            else if (skill is not null && state.Phase == PhaseDefense)
                await ActivateFortressSkill(player, state, skill);
            else if (wall is not null && !wall.Destroyed)
            {
                wall.Destroyed = true;
                await SetObjectStates(player, wall.Id, BlockerType, ("destroyed", 1), ("breakable_by_players", 0));
                await SetObjectBlocked(player, wall.Id, BlockerType, blocked: false);
            }
            Save(instance, state);
        }
        finally
        {
            state.Gate.Release();
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    async Task StartFortressDefense(Player player, FortressState state)
    {
        double now = AreaTime(player);
        state.Phase = PhaseDefense;
        state.Wave = 1;
        state.WaveStartedAt = now;
        state.NextSpawnAt = now + 0.5;
        state.NextHitAt = now + 2.0;

        var members = Members(player);
        if (!state.EnvironmentOn && EnvRow is { } env)
        {
            await Broadcast(members, InstanceEnvironments.Data((env, EnvSeconds)), "0x00df Fortress environment override");
            state.EnvironmentOn = true;
        }

        await SetObjectStates(player, state.StabiliserId, StabiliserType, ("interaction_state", 1), ("bomb_defence", 1));
        if (Monsters.Find(ChayulaFarmerWildMonsterId) is { Supported: true } ailithVariety)
        {
            uint aid = await SpawnMonster(player, ailithVariety, AreaLevel(player), MonsterRarity.Unique, state.AilithX, state.AilithY,
                alone: false, friendly: true, scripted: true, owned: (_, m) => { m.NoRewards = true; m.EntranceGrace = true; });
            if (aid != 0)
            {
                state.AilithId = aid;
                await SetMonsterStates(player, aid, ("exists", 1), ("bomb_defence", 1), ("number_of_hits", 0), ("total_number_of_hits", state.MaxHits));
            }
        }
        foreach (var pad in state.Skills)
        {
            pad.Ready = true;
            await SetObjectStates(player, pad.Id, SkillObjectType,
                ("activated", 0), ("brequel_skill_row_index_plus_one", pad.SkillRow + 1), ("ready", 1));
        }
        await SendFortressRulesetUpdate(members, state);
        foreach (var m in members)
            await PlayerOf(m).Tell($"Defend Ailith in the Hive Fortress Core! Wave 1/{state.TotalWaves}");
        Console.WriteLine($"[breach] Hive Fortress core defense started ({state.TotalWaves} waves)");
    }

    public async Task Step(Player runner, double seconds)
    {
        if (runner.Instance is not { } instance || Of(instance) is not { } state || !Current(runner) || runner.Paused
            || !IsMonsterRunner(runner)) return;
        if (state.Phase is PhaseComplete or PhaseFailed) return;
        if (!await state.Gate.WaitAsync(0)) return;
        try
        {
            double now = AreaTime(runner);
            var members = Members(runner);
            var atlas = AtlasOf(runner, state);
            bool dirty = false;

            if (state.Phase is PhaseIdle or PhaseDefense)
            {
                // Auto-burn core walls on player proximity
                foreach (var wall in state.Walls.Where(w => !w.Destroyed))
                {
                    if (members.Any(m => m.PlayerLife > 0 && double.Hypot(wall.X - m.Walk.Position(now).X, wall.Y - m.Walk.Position(now).Y) <= 8))
                    {
                        wall.Destroyed = true;
                        await SetObjectStates(runner, wall.Id, BlockerType, ("destroyed", 1), ("breakable_by_players", 0));
                        await SetObjectBlocked(runner, wall.Id, BlockerType, blocked: false);
                        dirty = true;
                    }
                }
            }

            if (state.Phase == PhaseDefense)
            {
                foreach (var pad in state.Skills)
                {
                    if (!pad.Ready && pad.CooldownUntil > 0 && now >= pad.CooldownUntil)
                    {
                        pad.Ready = true;
                        await SetObjectStates(runner, pad.Id, SkillObjectType,
                            ("activated", 0), ("brequel_skill_row_index_plus_one", pad.SkillRow + 1), ("ready", 1));
                        dirty = true;
                    }
                }
                if (state.ShieldUntil > 0 && now >= state.ShieldUntil)
                {
                    state.ShieldUntil = 0;
                    if (state.AilithId != 0) await SetMonsterStates(runner, state.AilithId, ("shielded", 0));
                    await SendFortressRulesetUpdate(members, state);
                    dirty = true;
                }
                if (now >= state.NextSpawnAt)
                {
                    state.NextSpawnAt = now + WaveSpawnInterval;
                    await SpawnFortressWave(runner, state, atlas);
                    dirty = true;
                }
                if (now >= state.NextHitAt)
                {
                    state.NextHitAt = now + 1.5;
                    if (state.ShieldUntil <= now)
                    {
                        int near = state.Monsters.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && !m.Dead && m.Life > 0
                            && double.Hypot(m.Walk.Position(now).X - state.AilithX, m.Walk.Position(now).Y - state.AilithY) <= 10);
                        if (near > 0)
                        {
                            state.HitsTaken += Math.Min(2, near);
                            if (state.AilithId != 0) await SetMonsterStates(runner, state.AilithId, ("number_of_hits", state.HitsTaken));
                            if (state.HitsTaken >= state.MaxHits)
                            {
                                state.Phase = PhaseFailed;
                                await SetObjectStates(runner, state.StabiliserId, StabiliserType, ("bomb_defence", 3));
                                if (state.AilithId != 0) await SetMonsterStates(runner, state.AilithId, ("bomb_defence", 3));
                                foreach (var m in members) await PlayerOf(m).Tell("Failed to Defend Ailith in the Hive Fortress.");
                            }
                            await SendFortressRulesetUpdate(members, state);
                            dirty = true;
                        }
                    }
                }

                if (state.Phase == PhaseDefense && now - state.WaveStartedAt >= WaveDurationSeconds)
                {
                    if (state.Wave < state.TotalWaves)
                    {
                        state.Wave++;
                        state.WaveStartedAt = now;
                        await SendFortressRulesetUpdate(members, state);
                        foreach (var m in members) await PlayerOf(m).Tell($"Hive Fortress Wave {state.Wave}/{state.TotalWaves}");
                        // Final wave spawns Vruun or enraged champion pack!
                        if (state.Wave == state.TotalWaves && !atlas.NoBosses)
                        {
                            var rng = AreaRng(runner);
                            var bossSpot = runner.Layout.Near((state.CoreX + 20, state.CoreY));
                            await BreachBosses.SpawnWildBoss(runner, bossSpot, AreaLevel(runner), atlas, rng);
                        }
                        dirty = true;
                    }
                    else
                    {
                        int alive = state.Monsters.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && !m.Dead && m.Life > 0
                            && double.Hypot(m.Walk.Position(now).X - state.CoreX, m.Walk.Position(now).Y - state.CoreY) <= 100);
                        if (alive == 0 || now - state.WaveStartedAt >= WaveDurationSeconds + 10.0)
                        {
                            await TriggerExplosionAndEscape(runner, members, state, atlas);
                            dirty = true;
                        }
                    }
                }
            }
            else if (state.Phase == PhaseEscaping)
            {
                // Complete when all players have left the fortress sub-area or reached the exit gate
                bool anyoneInside = members.Any(m => InSubArea(state, m.Walk.Position(now))
                    && double.Hypot(m.Walk.Position(now).X - state.ExitX, m.Walk.Position(now).Y - state.ExitY) > 16);
                if (!anyoneInside || now >= state.EscapeEndsAt)
                {
                    state.Phase = PhaseComplete;
                    await SendFortressRulesetUpdate(members, state);
                    await Broadcast(members, InstanceTimers.HideMissionTimer(InstanceTimers.MissionTimerType.Brequel),
                        "0x0163 Hive Fortress escape timer cleared");
                    if (state.EnvironmentOn)
                    {
                        await Broadcast(members, InstanceEnvironments.Data(), "0x00df Fortress environment override cleared");
                        state.EnvironmentOn = false;
                    }
                    foreach (var m in members)
                        await PlayerOf(m).Tell(anyoneInside
                            ? "The Hive Fortress collapsed! You were thrown clear of the blast."
                            : "You escaped the collapsing Hive Fortress!");
                    dirty = true;
                }
            }

            if (dirty) Save(instance, state);
        }
        finally { state.Gate.Release(); }
    }

    async Task TriggerExplosionAndEscape(Player runner, List<Player> members, FortressState state, BreachAtlas atlas)
    {
        double now = AreaTime(runner);
        state.Phase = PhaseEscaping;
        state.EscapeEndsAt = now + EscapeSeconds;

        await SetObjectStates(runner, state.StabiliserId, StabiliserType, ("interaction_state", 1), ("bomb_defence", 2));
        await SetObjectBlocked(runner, state.StabiliserId, StabiliserType, blocked: false);
        if (state.AilithId != 0)
            await SetMonsterStates(runner, state.AilithId, ("bomb_defence", 2), ("shielded", 0));
        if (state.StalkId != 0)
            await SetObjectStates(runner, state.StalkId, StalkFortressType, ("fortress_complete", 1));

        foreach (var wall in state.Walls.Where(w => !w.Destroyed))
        {
            wall.Destroyed = true;
            await SetObjectStates(runner, wall.Id, BlockerType, ("destroyed", 2), ("breakable_by_players", 0));
            await SetObjectBlocked(runner, wall.Id, BlockerType, blocked: false);
        }
        foreach (var pad in state.Skills)
        {
            pad.Ready = false;
            await SetObjectStates(runner, pad.Id, SkillObjectType, ("ready", 0));
        }

        // Start 45s Brequel MissionTimer HUD countdown + native EstablishedBrequelEncounterRules state=2 (Escape the Hive Fortress)!
        await SendFortressRulesetUpdate(members, state);
        await Broadcast(members, InstanceTimers.MissionTimer(InstanceTimers.MissionTimerType.Brequel, EscapeSeconds),
            $"0x0163 Hive Fortress escape timer ({EscapeSeconds}s)");

        // Spawn 3 bonus Fortress reward chests near core
        var rng = AreaRng(runner);
        for (int i = 0; i < 3; i++)
        {
            double angle = i * Math.Tau / 3.0;
            var at = runner.Layout.Near(((int)Math.Round(state.CoreX + Math.Cos(angle) * 14),
                (int)Math.Round(state.CoreY + Math.Sin(angle) * 14)));
            var chest = new FortressChest
            {
                Id = runner.Instance.World.TakeObjectId(),
                X = at.X,
                Y = at.Y,
                Rotation = (float)angle,
                Model = BrequelChestModels[BrequelChestModels.Length - 1],
                Bonus = true,
            };
            state.Chests.Add(chest);
            await CreateWorldObject(runner, chest.Id, ChestBaseType, chest.X, chest.Y, chest.Rotation,
                model: chest.Model, opened: false, states: [("open", 0)]);
        }

        // Guaranteed Hivebrain Gland + 2 Wombgifts + big Hiveblood burst!
        byte level = AreaLevel(runner);
        int baseBlood = 280 + state.Wave * 30;
        foreach (var m in members)
        {
            await GiveHiveblood(m, atlas.ScaleHiveblood(m, baseBlood, enragedBonus: state.Enraged));
            for (int g = 0; g < 2; g++)
            {
                string giftId = atlas.RollWombgiftBaseId(m, rng);
                if (MakeWombgift(m, giftId, level, rng) is { } gift)
                    await DropGroundItem(m, (state.CoreX, state.CoreY), gift, "Hive Fortress Wombgift");
            }
            if (MakeHivebrainGland(m, level) is { } gland)
                await DropGroundItem(m, (state.CoreX, state.CoreY), gland, "Hive Fortress Hivebrain Gland");
            if (TryRollBodyPart?.Invoke(m, level, rng) is { } bodyPart)
                await DropGroundItem(m, (state.CoreX, state.CoreY), bodyPart, "Hive Fortress quest body part");
            await PlayerOf(m).Tell($"The Hive Fortress is destabilising! Loot the core and escape within {EscapeSeconds} seconds!");
        }
        Console.WriteLine($"[breach] Hive Fortress core destroyed; escape sequence started ({EscapeSeconds}s)");
    }

    async Task SpawnFortressWave(Player runner, FortressState state, BreachAtlas atlas)
    {
        if (FortPacks.Count == 0) return;
        int alive = state.Monsters.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && !m.Dead && m.Life > 0);
        if (alive >= 46) return;
        var rng = AreaRng(runner);
        double angle = rng.NextDouble() * Math.Tau;
        double dist = 32 + rng.NextDouble() * 24;
        var anchor = runner.Layout.Near(((int)Math.Round(state.CoreX + Math.Cos(angle) * dist),
            (int)Math.Round(state.CoreY + Math.Sin(angle) * dist)));
        var pack = FortPacks[rng.Next(FortPacks.Count)];
        var rarity = state.Wave >= state.TotalWaves - 1 && rng.Next(3) == 0 ? MonsterRarity.Rare : atlas.RollPackRarity(rng);
        byte level = AreaLevel(runner);
        var extra = atlas.BuildMonsterMods(level, isBoss: false, enraged: state.Enraged);
        var layout = runner.Layout;
        bool Open(int x, int y) => x >= 0 && y >= 0 && x < layout.GridW && y < layout.GridH && layout.WalkableFor(x, y, 1);
        var spawns = AreaPopulation.AtPacks([(pack, anchor.X, anchor.Y, rarity)], Open,
            v => Monsters.Find(v.ToString()) is { Supported: true }, rng.Next(), MapModsOf(runner)?.PackSizeFactor ?? 1.0);
        using (HoldMonstersRemaining(runner))
        {
            foreach (var spawn in spawns)
            {
                if (Monsters.Find(spawn.Variety.ToString()) is not { Supported: true } variety) continue;
                uint id = await SpawnMonster(runner, BreachEncounter.Risen(variety), level, spawn.Rarity, spawn.X, spawn.Y,
                    alone: false, extraMods: extra, bulk: true);
                if (id != 0) { state.Monsters.Add(id); state.AllMonsterIds.Add(id); }
            }
        }
        await SendRemainingMonsters(runner);
    }

    async Task ActivateFortressSkill(Player player, FortressState state, FortressSkill pad)
    {
        if (!pad.Ready) return;
        double now = AreaTime(player);
        var atlas = AtlasOf(player, state);
        pad.Ready = false;
        pad.CooldownUntil = now + 14.0;
        await SetObjectStates(player, pad.Id, SkillObjectType,
            ("activated", 1), ("brequel_skill_row_index_plus_one", pad.SkillRow + 1), ("ready", 0));

        switch (pad.SkillRow)
        {
            case 4: // Heal Ailith
                state.HitsTaken = Math.Max(0, state.HitsTaken - 7);
                if (state.AilithId != 0) await SetMonsterStates(player, state.AilithId, ("number_of_hits", state.HitsTaken));
                await SendFortressRulesetUpdate(Members(player), state);
                break;
            case 5: // Shield Ailith
                state.ShieldUntil = now + 10.0;
                if (state.AilithId != 0) await SetMonsterStates(player, state.AilithId, ("shielded", 1));
                await SendFortressRulesetUpdate(Members(player), state);
                break;
            case 8: // Wall Destruction
                foreach (var w in state.Walls.Where(w => !w.Destroyed))
                {
                    w.Destroyed = true;
                    await SetObjectStates(player, w.Id, BlockerType, ("destroyed", 1), ("breakable_by_players", 0));
                    await SetObjectBlocked(player, w.Id, BlockerType, blocked: false);
                }
                break;
            case 9: // Enrage Hiveborn
                state.Enraged = true;
                break;
            default:
                int dmg = (int)Math.Round(520 * (1.0 + Math.Max(0, AreaLevel(player) - 68) * 0.12) * Math.Max(0.25, 1.0 + atlas.HiveSkillDamagePct / 100.0));
                foreach (uint id in state.Monsters.ToList())
                {
                    if (!player.Area.Monsters.TryGetValue(id, out var m) || m.Dead || m.Life == 0) continue;
                    var pos = m.Walk.Position(now);
                    if (double.Hypot(pos.X - state.CoreX, pos.Y - state.CoreY) > 70) continue;
                    uint dealt = (uint)Math.Min(m.Life, (uint)Math.Max(1, dmg));
                    m.Life -= dealt;
                    await SendWorldRecord(player, id, InstanceProtocol.LifeUpdate(id, m.Life), null);
                    if (m.Life == 0)
                    {
                        m.Dead = true; m.Aggro = false; m.ActionGeneration++;
                    }
                }
                break;
        }
    }

    async Task OpenFortressChest(Player player, FortressState state, FortressChest chest)
    {
        if (chest.Opened) return;
        chest.Opened = true;
        if (Spec(ChestBaseType) is { ChestIndex: >= 0 } spec)
            await SendWorldRecord(player, chest.Id, InstanceEntities.ComponentUpdate(chest.Id, (byte)spec.ChestIndex, [1, 0, 0]), null);
        await SetObjectStates(player, chest.Id, ChestBaseType, ("open", 1));
        await DropBreachChestRewards(player, (chest.X, chest.Y), AreaLevel(player), AtlasOf(player, state), AreaRng(player),
            isLarge: chest.Bonus, isFortress: true);
    }

    public async Task<bool> OnActivateEncounterSkill(Player player, uint targetOrIndex)
    {
        if (player.Instance is not { } instance || Of(instance) is not { } state || state.Phase != PhaseDefense) return false;
        await state.Gate.WaitAsync();
        try
        {
            var pad = state.Skills.FirstOrDefault(s => s.Ready && (s.Id == targetOrIndex || (uint)s.SkillRow == targetOrIndex))
                ?? state.Skills.FirstOrDefault(s => s.Ready);
            if (pad is null) return false;
            await ActivateFortressSkill(player, state, pad);
            Save(instance, state);
            return true;
        }
        finally { state.Gate.Release(); }
    }

    public async Task OnMonsterKilled(MonsterKill kill)
    {
        var player = kill.Player;
        if (player.Instance is not { } instance || Of(instance) is not { } state) return;
        if (!state.AllMonsterIds.Contains(kill.Monster.ObjectId)) return;
        await state.Gate.WaitAsync();
        try
        {
            if (!state.CreditedDeaths.Add(kill.Monster.ObjectId)) return;
            state.Kills++;
            var atlas = AtlasOf(player, state);
            int baseBlood = kill.Monster.Rarity switch { MonsterRarity.Unique => 50, MonsterRarity.Rare => 20, MonsterRarity.Magic => 7, _ => 3 };
            await GiveHiveblood(player, atlas.ScaleHiveblood(player, baseBlood, enragedBonus: state.Enraged));
            Save(instance, state);
        }
        finally { state.Gate.Release(); }
    }

    static async Task SendFortressRulesetUpdate(List<Player> members, FortressState state)
    {
        uint wireState = state.Phase switch
        {
            PhaseDefense => BreachRulesets.EstablishedActive,
            PhaseEscaping => BreachRulesets.EstablishedFortressEscape,
            PhaseComplete => BreachRulesets.EstablishedSucceeded,
            PhaseFailed => BreachRulesets.EstablishedFailed,
            _ => BreachRulesets.EstablishedInactive,
        };
        if (BreachRulesets.EstablishedState(wireState, state.CoreX, state.CoreY, isSkyFortress: true) is { } st)
            await Broadcast(members, st, $"0x02c9 EstablishedBrequelEncounterRules (Fortress) state={wireState}");
        if (BreachRulesets.EstablishedWave(state.Phase == PhaseDefense ? state.Wave : 0, state.Phase == PhaseDefense ? state.TotalWaves : 0) is { } wv)
            await Broadcast(members, wv, null);
        int remaining = Math.Max(0, state.MaxHits - state.HitsTaken);
        bool shielded = members.Count > 0 && state.ShieldUntil > AreaTime(members[0]);
        if (BreachRulesets.EstablishedHealth(remaining, state.MaxHits, shielded) is { } hp)
            await Broadcast(members, hp, null);
        if (BreachRulesets.EstablishedOverlay(state.Phase is PhaseDefense or PhaseEscaping) is { } ov)
            await Broadcast(members, ov, null);
        if (state.Phase == PhaseDefense)
        {
            if (BreachRulesets.BarrierRadius(state.CoreX, state.CoreY, 70f, 70f, 1f, 1f) is { } br)
                await Broadcast(members, br, null);
            if (BreachRulesets.BarrierActive(true) is { } ba)
                await Broadcast(members, ba, null);
        }
        else if (state.Phase is PhaseEscaping or PhaseComplete or PhaseFailed)
        {
            if (BreachRulesets.BarrierActive(false) is { } ba)
                await Broadcast(members, ba, null);
        }
    }
}
