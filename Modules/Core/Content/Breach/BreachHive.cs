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
/// In-map Established Breach Hive encounter (<c>BrequelHiveEncounter</c>, <c>brequel_established.arm</c>,
/// <c>Rulesets#165 EstablishedBrequelEncounterRules</c> &amp; <c>#166 BrequelBarrierRules</c>):
/// Ailith defense (<c>ChayulaFarmerWild</c> / <c>BrequelStabiliser</c>), destructible Hive walls (<c>BrequelBlocker</c>),
/// breakable spawners (<c>BrequelSpawner</c> + <c>BrequelSpawnerCover</c>), wave progression (<c>brequelfort-*</c>),
/// all 10 Ailith encounter skills (<c>BrequelEncounterSkills</c> + <c>BrequelSkillObject</c>), Hive chests,
/// Hiveblood/Wombgift/Hivebrain Gland rewards, and Breach Scarabs (Hive &amp; Incensed Swarm).
/// </summary>
public sealed class BreachHive
{
    const string StateKey = "Breach.Hive";
    const uint RollSalt = 0x48495645;
    const int BaseWaves = 5;
    const int MaxAilithHits = 24;
    const double WaveDurationSeconds = 18.0;
    const double WaveSpawnInterval = 2.5;
    const double AilithHitInterval = 1.5;

    public const int PhaseIdle = 0, PhaseRunning = 1, PhaseSucceeded = 2, PhaseFailed = 3;

    public sealed class WallObj
    {
        public uint Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public float Rotation { get; set; }
        public bool Destroyed { get; set; }
    }

    public sealed class SpawnerObj
    {
        public uint SpawnerId { get; set; }
        public uint CoverId { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public float Rotation { get; set; }
        public bool Broken { get; set; }
    }

    public sealed class SkillPad
    {
        public uint Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int SkillRow { get; set; }
        public bool Ready { get; set; } = true;
        public bool Activated { get; set; }
        public double CooldownUntil { get; set; }
    }

    public sealed class HiveChest
    {
        public uint Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public float Rotation { get; set; }
        public string Model { get; set; } = BrequelChestModels[0];
        public bool Opened { get; set; }
    }

    public sealed class HiveState
    {
        public int CentreX { get; set; }
        public int CentreY { get; set; }
        public int AilithX { get; set; }
        public int AilithY { get; set; }
        public uint StabiliserId { get; set; }
        public uint AilithId { get; set; }
        public int Phase { get; set; }
        public int Wave { get; set; }
        public int TotalWaves { get; set; } = BaseWaves;
        public int HitsTaken { get; set; }
        public int MaxHits { get; set; } = MaxAilithHits;
        public int BrokenSpawners { get; set; }
        public int Kills { get; set; }
        public bool EnragedWave { get; set; }
        public double ShieldUntil { get; set; }
        public double AuraUntil { get; set; }
        public double DecoyUntil { get; set; }
        public int DecoyX { get; set; }
        public int DecoyY { get; set; }
        public double WaveStartedAt { get; set; }
        public double NextSpawnAt { get; set; }
        public double NextAilithHitAt { get; set; }
        public double NextAuraTickAt { get; set; }
        public bool EnvironmentOn { get; set; }
        public List<WallObj> Walls { get; set; } = [];
        public List<SpawnerObj> Spawners { get; set; } = [];
        public List<SkillPad> Skills { get; set; } = [];
        public List<HiveChest> Chests { get; set; } = [];
        public List<uint> Monsters { get; set; } = [];
        public HashSet<uint> CreditedDeaths { get; set; } = [];
        [JsonIgnore] internal SemaphoreSlim Gate { get; } = new(1, 1);
        [JsonIgnore] internal BreachAtlas? Atlas;
        [JsonIgnore] internal bool ObjectsCreated;
        [JsonIgnore] internal HashSet<uint> AllMonsterIds { get; } = [];
    }

    readonly ConditionalWeakTable<InstanceState, HiveState> _hives = new();
    readonly JsonSerializerOptions _json = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    public void Clear() => _hives.Clear();

    public HiveState? Of(InstanceState instance)
    {
        if (_hives.TryGetValue(instance, out var known)) return known;
        if (!instance.ContentState.TryGetValue(StateKey, out var text)) return null;
        return _hives.GetValue(instance, _ =>
        {
            var restored = JsonSerializer.Deserialize<HiveState>(text, _json) ?? new();
            foreach (uint id in restored.Monsters) restored.AllMonsterIds.Add(id);
            return restored;
        });
    }

    public bool IsActivePause(Player player) =>
        player.Instance is { } instance && _hives.TryGetValue(instance, out var hive) && hive.Phase == PhaseRunning;

    void Save(InstanceState instance, HiveState hive) =>
        instance.ContentState[StateKey] = JsonSerializer.Serialize(hive, _json);

    static BreachAtlas AtlasOf(Player player, HiveState hive) => hive.Atlas ??= BreachAtlas.From(player);

    public async Task Populate(AreaPopulated area)
    {
        var player = area.Player;
        if (player.Instance is not { } instance || Of(instance) is not null) return;
        var layout = player.Layout;
        if (layout.Id is FortressAreaId or BossAreaId or MonasteryAreaId) return;
        if (!MapDevices.IsMapArea(layout.Hash) || layout.Walkable is null) return;

        var atlas = BreachAtlas.From(player);
        var level = layout.Level;
        DoodadRecord? hiveRecord = level?.Records.FirstOrDefault(r => r.FeatureRow == HiveFeatureRow);
        bool skyHive = level?.Records.Any(r => r.FeatureRow == SkyHiveFeatureRow) == true;
        if (hiveRecord is null)
        {
            // Only fall back to manual placement if map explicitly forced a ground Hive (not a Sky Fortress). Dimensional
            // Foothold (OnlyBreachHives) only turns a Breach that rolled into a Hive (UnstableBreaches); it adds none.
            if (skyHive || atlas.HiveIsAlwaysSkyFortress || !atlas.ContainsBreachHive)
                return;
        }

        var rng = new Random(unchecked(area.Seed ^ (int)RollSalt));
        (int X, int Y) centre;
        (int X, int Y) ailithCell;

        if (hiveRecord is { } rec && level is not null)
        {
            int x0 = rec.X * 23, y0 = rec.Y * 23, x1 = (rec.X + rec.W) * 23, y1 = (rec.Y + rec.H) * 23;
            var stabObj = level.RoomObjects.FirstOrDefault(o =>
                (o.Type == StabiliserType || o.Type == StabiliserFortressType)
                && o.X >= x0 && o.X < x1 && o.Y >= y0 && o.Y < y1);
            var npcObj = level.RoomObjects.FirstOrDefault(o =>
                (o.Type == ChayulaFarmerWildMonsterId || o.Type == ChayulaFarmerNpcType)
                && o.X >= x0 && o.X < x1 && o.Y >= y0 && o.Y < y1);
            centre = stabObj is not null ? (stabObj.X, stabObj.Y) : layout.Near(((x0 + x1) / 2, (y0 + y1) / 2));
            ailithCell = npcObj is not null ? (npcObj.X, npcObj.Y) : layout.Near((centre.X, centre.Y + 12));
        }
        else
        {
            if (AreaPopulation.EncounterSpot(area, area.Taken.ToList(), rng) is not { } spot) return;
            centre = spot;
            ailithCell = layout.Near((spot.X, spot.Y + 12));
        }

        int totalWaves = Math.Clamp(BaseWaves + atlas.HivesAdditionalWaves, 3, 12);
        var hive = new HiveState
        {
            CentreX = centre.X,
            CentreY = centre.Y,
            AilithX = ailithCell.X,
            AilithY = ailithCell.Y,
            StabiliserId = instance.World.TakeObjectId(),
            AilithId = instance.World.TakeObjectId(),
            TotalWaves = totalWaves,
            Atlas = atlas,
        };

        // Plan Hive walls (BrequelBlocker) around perimeter
        int wallCount = 10;
        for (int i = 0; i < wallCount; i++)
        {
            double angle = i * Math.Tau / wallCount + rng.NextDouble() * 0.15;
            double dist = 26 + rng.NextDouble() * 14;
            var at = layout.Near(((int)Math.Round(centre.X + Math.Cos(angle) * dist),
                (int)Math.Round(centre.Y + Math.Sin(angle) * dist)));
            hive.Walls.Add(new WallObj
            {
                Id = instance.World.TakeObjectId(),
                X = at.X,
                Y = at.Y,
                Rotation = (float)(angle + Math.PI / 2),
            });
        }

        // Plan Breakable Spawners (BrequelSpawner + BrequelSpawnerCover), scaled by map_breach_hive_number_of_breakable_spawners_+%
        int spawnerCount = Math.Clamp((int)Math.Round(4 * Math.Max(0.5, 1.0 + atlas.BreakableSpawnersPct / 100.0)), 3, 10);
        for (int i = 0; i < spawnerCount; i++)
        {
            double angle = (i + 0.5) * Math.Tau / spawnerCount;
            double dist = 42 + rng.NextDouble() * 18;
            var at = layout.Near(((int)Math.Round(centre.X + Math.Cos(angle) * dist),
                (int)Math.Round(centre.Y + Math.Sin(angle) * dist)));
            hive.Spawners.Add(new SpawnerObj
            {
                SpawnerId = instance.World.TakeObjectId(),
                CoverId = instance.World.TakeObjectId(),
                X = at.X,
                Y = at.Y,
                Rotation = (float)angle,
            });
        }

        // Plan Ailith encounter skill objects (BrequelSkillObject), scaled by Protracted Siege & Incensed Swarm
        int skillCount = Math.Clamp((int)Math.Round(4 * Math.Max(0.5, 1.0 + atlas.HiveNumberOfSkillsPct / 100.0)), 2, 10);
        var pool = EncounterSkills.Count > 0 ? EncounterSkills.Select(s => s.Row).ToList() : [.. Enumerable.Range(0, 10)];
        for (int i = 0; i < skillCount; i++)
        {
            double angle = i * Math.Tau / skillCount + 0.3;
            double dist = 16 + rng.NextDouble() * 10;
            var at = layout.Near(((int)Math.Round(centre.X + Math.Cos(angle) * dist),
                (int)Math.Round(centre.Y + Math.Sin(angle) * dist)));
            // ScarabBreachNew5 (Incensed Swarm): all Ailith skills become Enrage Hiveborn (row 9)
            int skillRow = atlas.HiveSkillsAreAllEnrageMonsters ? 9 : pool[rng.Next(pool.Count)];
            hive.Skills.Add(new SkillPad
            {
                Id = instance.World.TakeObjectId(),
                X = at.X,
                Y = at.Y,
                SkillRow = skillRow,
                Ready = true,
            });
        }

        _hives.AddOrUpdate(instance, hive);
        Save(instance, hive);
        await PresentHiveObjects(player, hive);
        Console.WriteLine($"[breach] Hive placed in {layout.Id} at ({centre.X}, {centre.Y}): {totalWaves} waves, {spawnerCount} spawners, {skillCount} Ailith skills");
    }

    public IEnumerable<(string Log, byte[] Message)> EnterMessages(Player player)
    {
        if (player.Instance is not { } instance || Of(instance) is not { } hive) yield break;
        if (EstablishedRulesetRow is { } est)
            yield return ($"0x001f ruleset EstablishedBrequelEncounterRules (row {est})", InstanceProtocol.AddRuleset(est));
        if (BarrierRulesetRow is { } bar)
            yield return ($"0x001f ruleset BrequelBarrierRules (row {bar})", InstanceProtocol.AddRuleset(bar));
    }

    public async Task Enter(Player player)
    {
        if (player.Instance is not { } instance || Of(instance) is not { } hive) return;
        await hive.Gate.WaitAsync();
        try
        {
            if (EstablishedRulesetRow is { } est)
                await Send(player, InstanceProtocol.AddRuleset(est), $"0x001f ruleset EstablishedBrequelEncounterRules (row {est})");
            if (BarrierRulesetRow is { } bar)
                await Send(player, InstanceProtocol.AddRuleset(bar), $"0x001f ruleset BrequelBarrierRules (row {bar})");
            if (!hive.ObjectsCreated) await PresentHiveObjects(player, hive);
            if (hive.EnvironmentOn && EnvRow is { } env)
                await Send(player, InstanceEnvironments.Data((env, EnvSeconds)), "0x00df Hive environment override");
            await SendHiveRulesetUpdate([player], hive);
        }
        finally { hive.Gate.Release(); }
    }

    async Task PresentHiveObjects(Player player, HiveState hive)
    {
        hive.ObjectsCreated = true;
        var members = Members(player);
        if (!members.Contains(player)) members.Insert(0, player);
        if (EstablishedRulesetRow is { } est)
            await Broadcast(members, InstanceProtocol.AddRuleset(est), $"0x001f ruleset EstablishedBrequelEncounterRules (row {est})");
        if (BarrierRulesetRow is { } bar)
            await Broadcast(members, InstanceProtocol.AddRuleset(bar), $"0x001f ruleset BrequelBarrierRules (row {bar})");

        await CreateWorldObject(player, hive.StabiliserId, StabiliserType, hive.CentreX, hive.CentreY,
            blocked: hive.Phase < PhaseSucceeded,
            log: $"0x02bb Hive Stabiliser 0x{hive.StabiliserId:x} at ({hive.CentreX}, {hive.CentreY})",
            states: [("interaction_state", hive.Phase == PhaseIdle ? 0 : 1), ("bomb_defence", hive.Phase)]);

        foreach (var wall in hive.Walls)
        {
            if (wall.Destroyed) continue;
            await CreateWorldObject(player, wall.Id, BlockerType, wall.X, wall.Y, wall.Rotation,
                blocked: true, states: [("destroyed", 0), ("breakable_by_players", 1)]);
        }

        foreach (var sp in hive.Spawners)
        {
            await CreateWorldObject(player, sp.SpawnerId, SpawnerType, sp.X, sp.Y, sp.Rotation,
                states: [("spawning", sp.Broken ? 2 : 1), ("spawned", 0)]);
            await CreateWorldObject(player, sp.CoverId, SpawnerCoverType, sp.X, sp.Y, sp.Rotation,
                blocked: !sp.Broken,
                states: [("destroyed", sp.Broken ? 2 : 0), ("minimap_icon_visible", 1)]);
        }

        foreach (var pad in hive.Skills)
        {
            await CreateWorldObject(player, pad.Id, SkillObjectType, pad.X, pad.Y,
                states: [
                    ("activated", pad.Activated ? 1 : 0),
                    ("brequel_skill_row_index_plus_one", pad.SkillRow + 1),
                    ("ready", pad.Ready && hive.Phase == PhaseRunning ? 1 : 0),
                ]);
        }

        await SendHiveRulesetUpdate(members, hive);
    }

    public async Task<bool> OnUse(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (player.Instance is not { } instance || Of(instance) is not { } hive) return false;

        bool isStabiliser = use.TargetId == hive.StabiliserId;
        var skillPad = hive.Skills.FirstOrDefault(s => s.Id == use.TargetId);
        var spawner = hive.Spawners.FirstOrDefault(s => s.CoverId == use.TargetId || s.SpawnerId == use.TargetId);
        var chest = hive.Chests.FirstOrDefault(c => c.Id == use.TargetId);
        var wall = hive.Walls.FirstOrDefault(w => w.Id == use.TargetId);
        if (!isStabiliser && skillPad is null && spawner is null && chest is null && wall is null) return false;
        if (player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;

        (int X, int Y) cell = isStabiliser ? (hive.CentreX, hive.CentreY)
            : skillPad is not null ? (skillPad.X, skillPad.Y)
            : spawner is not null ? (spawner.X, spawner.Y)
            : chest is not null ? (chest.X, chest.Y)
            : (wall!.X, wall.Y);
        int reach = isStabiliser ? 15 : 8;
        if (!TryBeginObjectWalk(player, requestedAt, cell.X, cell.Y, reach, out var walk)) return true;
        var (action, now, here, _, arrive) = walk;
        if (!use.Started)
            await SendWorldRecord(player, player.Id,
                InstanceProtocol.ActionStarted(player.Id, here.X, here.Y, cell.X, cell.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId), null,
                still: () => PlayerActionCurrent(player, action));
        if (arrive > now) await AreaDelay(player, arrive - now);
        await hive.Gate.WaitAsync();
        try
        {
            if (!PlayerActionCurrent(player, action) || player.PlayerLife == 0 || player.Closed || player.LeavingArea) return true;
            if (isStabiliser && hive.Phase == PhaseIdle)
                await StartDefense(player, hive);
            else if (skillPad is not null && hive.Phase == PhaseRunning)
                await ExecuteAilithSkill(player, hive, skillPad);
            else if (spawner is not null && !spawner.Broken)
                await BreakSpawner(player, hive, spawner);
            else if (wall is not null && !wall.Destroyed)
                await DestroyWall(player, hive, wall);
            else if (chest is not null && !chest.Opened)
                await OpenHiveChest(player, hive, chest);
            Save(instance, hive);
        }
        finally
        {
            hive.Gate.Release();
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionEnded(player.Id), null, still: () => PlayerActionCurrent(player, action));
        }
        return true;
    }

    /// <summary>Handles C-&gt;S <c>BrequelActivateEncounterSkill</c> (ClientLeagueAction#44) when triggered via hotkey/UI.</summary>
    public async Task<bool> OnActivateEncounterSkill(Player player, uint targetOrIndex)
    {
        if (player.Instance is not { } instance || Of(instance) is not { } hive || hive.Phase != PhaseRunning) return false;
        await hive.Gate.WaitAsync();
        try
        {
            var pad = hive.Skills.FirstOrDefault(s => s.Ready && (s.Id == targetOrIndex || (uint)s.SkillRow == targetOrIndex))
                ?? hive.Skills.FirstOrDefault(s => s.Ready);
            if (pad is null) return false;
            await ExecuteAilithSkill(player, hive, pad);
            Save(instance, hive);
            return true;
        }
        finally { hive.Gate.Release(); }
    }

    async Task StartDefense(Player player, HiveState hive)
    {
        double now = AreaTime(player);
        hive.Phase = PhaseRunning;
        hive.Wave = 1;
        hive.WaveStartedAt = now;
        hive.NextSpawnAt = now + 0.5;
        hive.NextAilithHitAt = now + 2.0;

        var members = Members(player);
        if (!hive.EnvironmentOn && EnvRow is { } env)
        {
            await Broadcast(members, InstanceEnvironments.Data((env, EnvSeconds)), "0x00df Hive environment override on");
            hive.EnvironmentOn = true;
        }

        await SetObjectStates(player, hive.StabiliserId, StabiliserType, ("interaction_state", 1), ("bomb_defence", PhaseRunning));

        // Spawn Ailith (ChayulaFarmerWild) as defendable ally at the Hive core
        if (Monsters.Find(ChayulaFarmerWildMonsterId) is { Supported: true } ailithVariety)
        {
            uint aid = await SpawnMonster(player, ailithVariety, AreaLevel(player), MonsterRarity.Unique,
                hive.AilithX, hive.AilithY, alone: false, friendly: true, scripted: true,
                owned: (_, m) => { m.NoRewards = true; m.EntranceGrace = true; });
            if (aid != 0)
            {
                hive.AilithId = aid;
                await SetMonsterStates(player, aid, ("exists", 1), ("bomb_defence", PhaseRunning),
                    ("number_of_hits", 0), ("total_number_of_hits", hive.MaxHits), ("shielded", 0));
            }
        }

        // Enable all Ailith encounter skill pads
        foreach (var pad in hive.Skills)
        {
            pad.Ready = true;
            pad.Activated = false;
            await SetObjectStates(player, pad.Id, SkillObjectType,
                ("activated", 0), ("brequel_skill_row_index_plus_one", pad.SkillRow + 1), ("ready", 1));
        }

        // Activate spawners visually
        foreach (var sp in hive.Spawners)
            await SetObjectStates(player, sp.SpawnerId, SpawnerType, ("spawning", 2), ("spawned", 1));

        await SendHiveRulesetUpdate(members, hive);
        foreach (var m in members)
            await PlayerOf(m).Tell($"Defend Ailith! Wave 1/{hive.TotalWaves} has begun.");
        Console.WriteLine($"[breach] Hive defense started in {player.Layout.Id} ({hive.TotalWaves} waves)");
    }

    public async Task Step(Player runner, double seconds)
    {
        if (runner.Instance is not { } instance || Of(instance) is not { } hive || !Current(runner) || runner.Paused
            || !IsMonsterRunner(runner)) return;
        if (hive.Phase is PhaseSucceeded or PhaseFailed && !hive.EnvironmentOn) return;
        if (!await hive.Gate.WaitAsync(0)) return;
        try
        {
            double now = AreaTime(runner);
            var members = Members(runner);
            var atlas = AtlasOf(runner, hive);
            bool dirty = false;

            // Allow players to burn walls & break spawner covers by walking right through them (matching 3.29 flare aura)
            if (hive.Phase is PhaseIdle or PhaseRunning)
            {
                foreach (var wall in hive.Walls)
                {
                    if (wall.Destroyed) continue;
                    if (members.Any(m => { if (m.PlayerLife == 0) return false; var (mx, my) = m.Walk.Position(now); return double.Hypot(wall.X - mx, wall.Y - my) <= 8; }))
                    {
                        await DestroyWall(runner, hive, wall);
                        dirty = true;
                    }
                }
                foreach (var sp in hive.Spawners)
                {
                    if (sp.Broken) continue;
                    if (members.Any(m => { if (m.PlayerLife == 0) return false; var (mx, my) = m.Walk.Position(now); return double.Hypot(sp.X - mx, sp.Y - my) <= 9; }))
                    {
                        await BreakSpawner(runner, hive, sp);
                        dirty = true;
                    }
                }
            }

            if (hive.Phase == PhaseRunning)
            {
                // Refresh skill pad cooldowns (except Wall Destruction which is permanently removed at Wave 4)
                foreach (var pad in hive.Skills)
                {
                    if (pad.SkillRow == 8 && hive.Wave >= 4)
                    {
                        if (pad.Ready)
                        {
                            pad.Ready = false;
                            await SetObjectStates(runner, pad.Id, SkillObjectType, ("ready", 0));
                            dirty = true;
                        }
                        continue;
                    }
                    if (!pad.Ready && pad.CooldownUntil > 0 && now >= pad.CooldownUntil)
                    {
                        pad.Ready = true;
                        pad.Activated = false;
                        await SetObjectStates(runner, pad.Id, SkillObjectType,
                            ("activated", 0), ("brequel_skill_row_index_plus_one", pad.SkillRow + 1), ("ready", 1));
                        dirty = true;
                    }
                }

                // Clear Ailith shield when expired
                if (hive.ShieldUntil > 0 && now >= hive.ShieldUntil)
                {
                    hive.ShieldUntil = 0;
                    if (hive.AilithId != 0) await SetMonsterStates(runner, hive.AilithId, ("shielded", 0));
                    await SendHiveRulesetUpdate(members, hive);
                    dirty = true;
                }

                // Enhanced Flame Aura periodic burn around players
                if (hive.AuraUntil > now && now >= hive.NextAuraTickAt)
                {
                    hive.NextAuraTickAt = now + 1.0;
                    int dmg = SkillDamage(AreaLevel(runner), atlas, baseDmg: 140);
                    foreach (var m in members)
                        await DamageHiveMonstersNear(runner, hive, m.Walk.Position(now), 28, dmg);
                }

                // Spawn wave packs on interval
                if (now >= hive.NextSpawnAt)
                {
                    hive.NextSpawnAt = now + WaveSpawnInterval;
                    await SpawnWavePack(runner, hive, atlas);
                    dirty = true;
                }

                // Check monsters hitting Ailith
                if (now >= hive.NextAilithHitAt)
                {
                    hive.NextAilithHitAt = now + AilithHitInterval;
                    bool shielded = hive.ShieldUntil > now;
                    bool decoyActive = hive.DecoyUntil > now;
                    var defendTarget = decoyActive ? (hive.DecoyX, hive.DecoyY) : (hive.AilithX, hive.AilithY);
                    int attackers = 0;
                    foreach (uint id in hive.Monsters)
                    {
                        if (!runner.Area.Monsters.TryGetValue(id, out var mon) || mon.Dead || mon.Life == 0) continue;
                        var pos = mon.Walk.Position(now);
                        if (double.Hypot(pos.X - defendTarget.Item1, pos.Y - defendTarget.Item2) <= 10)
                            attackers++;
                    }
                    if (attackers > 0 && !shielded && !decoyActive)
                    {
                        hive.HitsTaken += Math.Min(2, attackers);
                        if (hive.AilithId != 0)
                            await SetMonsterStates(runner, hive.AilithId, ("number_of_hits", hive.HitsTaken));
                        await SendHiveRulesetUpdate(members, hive);
                        dirty = true;
                        if (hive.HitsTaken >= hive.MaxHits)
                        {
                            await FailDefense(runner, members, hive);
                            dirty = true;
                        }
                    }
                }

                // Wave advancement
                if (hive.Phase == PhaseRunning && now - hive.WaveStartedAt >= WaveDurationSeconds)
                {
                    if (hive.Wave < hive.TotalWaves)
                    {
                        hive.Wave++;
                        hive.WaveStartedAt = now;
                        await SendHiveRulesetUpdate(members, hive);
                        foreach (var m in members)
                            await PlayerOf(m).Tell($"Breach Hive Wave {hive.Wave}/{hive.TotalWaves}");
                        dirty = true;
                    }
                    else
                    {
                        // Final wave done once remaining monsters are cleared or 12 s grace passes
                        int alive = hive.Monsters.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && !m.Dead && m.Life > 0);
                        if (alive == 0 || now - hive.WaveStartedAt >= WaveDurationSeconds + 12.0)
                        {
                            await SucceedDefense(runner, members, hive, atlas);
                            dirty = true;
                        }
                    }
                }
            }

            if (dirty) Save(instance, hive);
        }
        finally { hive.Gate.Release(); }
    }

    public async Task OnMonsterKilled(MonsterKill kill)
    {
        var player = kill.Player;
        if (player.Instance is not { } instance || Of(instance) is not { } hive) return;
        if (!hive.AllMonsterIds.Contains(kill.Monster.ObjectId)) return;
        await hive.Gate.WaitAsync();
        try
        {
            if (!hive.CreditedDeaths.Add(kill.Monster.ObjectId)) return;
            hive.Kills++;
            var atlas = AtlasOf(player, hive);
            int baseBlood = kill.Monster.Rarity switch
            {
                MonsterRarity.Unique => 45,
                MonsterRarity.Rare => 18,
                MonsterRarity.Magic => 6,
                _ => 2,
            };
            await GiveHiveblood(player, atlas.ScaleHiveblood(player, baseBlood, hive.BrokenSpawners, hive.EnragedWave));
            var rng = AreaRng(player);
            double giftChance = (kill.Monster.Rarity == MonsterRarity.Rare ? 0.16 : kill.Monster.Rarity == MonsterRarity.Magic ? 0.03 : 0.004)
                * atlas.WombgiftChanceFactor(hive.BrokenSpawners, enragedBonus: hive.EnragedWave);
            if (rng.NextDouble() < Math.Min(0.95, giftChance))
            {
                string giftId = atlas.RollWombgiftBaseId(player, rng);
                if (MakeWombgift(player, giftId, AreaLevel(player), rng) is { } gift)
                    await DropGroundItem(player, (kill.Monster.X, kill.Monster.Y), gift, "Hive monster Wombgift");
            }
            Save(instance, hive);
        }
        finally { hive.Gate.Release(); }
    }

    async Task SpawnWavePack(Player runner, HiveState hive, BreachAtlas atlas)
    {
        if (FortPacks.Count == 0 || hive.Spawners.Count == 0) return;
        int alive = hive.Monsters.Count(id => runner.Area.Monsters.TryGetValue(id, out var m) && !m.Dead && m.Life > 0);
        if (alive >= 42) return;
        var rng = AreaRng(runner);
        var spawner = hive.Spawners[rng.Next(hive.Spawners.Count)];
        var pack = FortPacks[rng.Next(FortPacks.Count)];
        var rarity = hive.Wave >= hive.TotalWaves && rng.Next(3) == 0 ? MonsterRarity.Rare : atlas.RollPackRarity(rng);
        byte level = AreaLevel(runner);
        var extra = atlas.BuildMonsterMods(level, isBoss: false, brokenSpawners: hive.BrokenSpawners, enraged: hive.EnragedWave);
        var layout = runner.Layout;
        bool Open(int x, int y) => x >= 0 && y >= 0 && x < layout.GridW && y < layout.GridH && layout.WalkableFor(x, y, 1);
        var spawns = AreaPopulation.AtPacks([(pack, spawner.X, spawner.Y, rarity)], Open,
            v => Monsters.Find(v.ToString()) is { Supported: true }, rng.Next(), MapModsOf(runner)?.PackSizeFactor ?? 1.0);
        using (HoldMonstersRemaining(runner))
        {
            foreach (var spawn in spawns)
            {
                if (Monsters.Find(spawn.Variety.ToString()) is not { Supported: true } variety) continue;
                uint id = await SpawnMonster(runner, BreachEncounter.Risen(variety), level, spawn.Rarity, spawn.X, spawn.Y,
                    alone: false, extraMods: extra, bulk: true);
                if (id == 0) continue;
                hive.Monsters.Add(id);
                hive.AllMonsterIds.Add(id);
                if (runner.Area.Monsters.TryGetValue(id, out var mon)) mon.SummonerId = hive.StabiliserId;
            }
        }
        await SendRemainingMonsters(runner);
    }

    async Task ExecuteAilithSkill(Player player, HiveState hive, SkillPad pad)
    {
        if (!pad.Ready) return;
        double now = AreaTime(player);
        var atlas = AtlasOf(player, hive);
        int level = AreaLevel(player);
        pad.Ready = false;
        pad.Activated = true;
        pad.CooldownUntil = now + 14.0;
        await SetObjectStates(player, pad.Id, SkillObjectType,
            ("activated", 1), ("brequel_skill_row_index_plus_one", pad.SkillRow + 1), ("ready", 0));

        var origin = (pad.X, pad.Y);
        int baseDmg = SkillDamage(level, atlas, baseDmg: 420);
        switch (pad.SkillRow)
        {
            case 0: // ChayulaFirestormDaemon: Bombard the Hiveborn
                await DamageHiveMonstersNear(player, hive, (hive.CentreX, hive.CentreY), 75, (int)(baseDmg * 1.4));
                await PlayerOf(player).Tell("Ailith bombards the Hiveborn with Chayula's Meteor Rain!");
                break;
            case 1: // ChayulaFlameWallDaemon: Conjure a wall of the Dreamer's Flame
                await DamageHiveMonstersNear(player, hive, origin, 50, baseDmg);
                await PlayerOf(player).Tell("Ailith conjures a wall of the Dreamer's Flame!");
                break;
            case 2: // ChayulaFreezePulseDaemon: Freeze and Hinder the Hiveborn
                await DamageHiveMonstersNear(player, hive, (hive.CentreX, hive.CentreY), 65, (int)(baseDmg * 0.8), slowSeconds: 6.0);
                await PlayerOf(player).Tell("Ailith releases a Chaos Ice Pulse, hindering the Hiveborn!");
                break;
            case 3: // ChayulaDecoyTotemDaemon: Distract the Hiveborn with a Decoy
                hive.DecoyX = pad.X;
                hive.DecoyY = pad.Y;
                hive.DecoyUntil = now + 10.0;
                await PlayerOf(player).Tell("Ailith summons a Decoy to distract the Hiveborn!");
                break;
            case 4: // ChayulaAilithHealDaemon: Heal Ailith
                hive.HitsTaken = Math.Max(0, hive.HitsTaken - 6);
                if (hive.AilithId != 0) await SetMonsterStates(player, hive.AilithId, ("number_of_hits", hive.HitsTaken));
                await SendHiveRulesetUpdate(Members(player), hive);
                await PlayerOf(player).Tell("Ailith restores her vitality!");
                break;
            case 5: // ChayulaAilithShieldDaemon: Grant Ailith a Shield
                hive.ShieldUntil = now + 10.0;
                if (hive.AilithId != 0) await SetMonsterStates(player, hive.AilithId, ("shielded", 1));
                await SendHiveRulesetUpdate(Members(player), hive);
                await PlayerOf(player).Tell("Ailith raises a protective shield!");
                break;
            case 6: // ChayulaEnhancedFlameAuraDaemon: Gain an Enhanced Flame
                hive.AuraUntil = now + 12.0;
                hive.NextAuraTickAt = now + 0.5;
                await PlayerOf(player).Tell("Enhanced Flame surges around you!");
                break;
            case 7: // ChayulaMinefieldDaemon: Summons Grasping Hands
                await DamageHiveMonstersNear(player, hive, origin, 55, (int)(baseDmg * 1.1), slowSeconds: 4.0);
                await PlayerOf(player).Tell("Grasping Hands erupt beneath the Hiveborn!");
                break;
            case 8: // ChayulaWallDestructionDaemon: Destroy the Hive walls around Ailith
                foreach (var w in hive.Walls.Where(w => !w.Destroyed))
                    await DestroyWall(player, hive, w);
                pad.CooldownUntil = double.MaxValue;
                await PlayerOf(player).Tell("Ailith shatters the surrounding Hive walls!");
                break;
            case 9: // ChayulaEnrageHivebornDameon: Enrage the Hiveborn
                hive.EnragedWave = true;
                await PlayerOf(player).Tell("The Hiveborn become Enraged — increased ferocity and rewards!");
                break;
        }
    }

    async Task BreakSpawner(Player player, HiveState hive, SpawnerObj sp)
    {
        if (sp.Broken) return;
        sp.Broken = true;
        hive.BrokenSpawners++;
        var atlas = AtlasOf(player, hive);
        await SetObjectStates(player, sp.CoverId, SpawnerCoverType, ("destroyed", 1), ("minimap_icon_visible", 1));
        await SetObjectBlocked(player, sp.CoverId, SpawnerCoverType, blocked: false);
        await SetObjectStates(player, sp.SpawnerId, SpawnerType, ("spawning", 2), ("spawned", 1));
        await SetObjectStates(player, sp.CoverId, SpawnerCoverType, ("destroyed", 2), ("minimap_icon_visible", 1));

        // Explosive burn AoE on breaking a spawner (scaled by map_breach_hive_breakable_spawners_area_of_effect_+%)
        double radius = 25.0 * Math.Max(0.5, 1.0 + atlas.BreakableSpawnersAoePct / 100.0);
        int dmg = SkillDamage(AreaLevel(player), atlas, baseDmg: 300);
        await DamageHiveMonstersNear(player, hive, (sp.X, sp.Y), radius, dmg);
        await GiveHiveblood(player, atlas.ScaleHiveblood(player, 20, hive.BrokenSpawners));
    }

    async Task DestroyWall(Player player, HiveState hive, WallObj wall)
    {
        if (wall.Destroyed) return;
        wall.Destroyed = true;
        await SetObjectStates(player, wall.Id, BlockerType, ("destroyed", 1), ("breakable_by_players", 0));
        await SetObjectBlocked(player, wall.Id, BlockerType, blocked: false);
    }

    async Task SucceedDefense(Player runner, List<Player> members, HiveState hive, BreachAtlas atlas)
    {
        hive.Phase = PhaseSucceeded;
        await SetObjectStates(runner, hive.StabiliserId, StabiliserType, ("interaction_state", 1), ("bomb_defence", PhaseSucceeded));
        await SetObjectBlocked(runner, hive.StabiliserId, StabiliserType, blocked: false);
        if (hive.AilithId != 0)
            await SetMonsterStates(runner, hive.AilithId, ("bomb_defence", PhaseSucceeded), ("shielded", 0));

        // Explode all remaining walls and finish spawners
        foreach (var w in hive.Walls.Where(w => !w.Destroyed))
        {
            w.Destroyed = true;
            await SetObjectStates(runner, w.Id, BlockerType, ("destroyed", 2), ("breakable_by_players", 0));
            await SetObjectBlocked(runner, w.Id, BlockerType, blocked: false);
        }
        foreach (var sp in hive.Spawners)
            await SetObjectStates(runner, sp.SpawnerId, SpawnerType, ("spawning", 3));
        foreach (var pad in hive.Skills)
        {
            pad.Ready = false;
            await SetObjectStates(runner, pad.Id, SkillObjectType, ("ready", 0));
        }

        if (hive.EnvironmentOn)
        {
            await Broadcast(members, InstanceEnvironments.Data(), "0x00df Hive environment override cleared");
            hive.EnvironmentOn = false;
        }
        await SendHiveRulesetUpdate(members, hive);

        // Spawn 4 Hive reward chests around the core
        var rng = AreaRng(runner);
        int chestCount = Math.Clamp(4 + atlas.AdditionalChests, 3, 8);
        for (int i = 0; i < chestCount; i++)
        {
            double angle = i * Math.Tau / chestCount;
            var at = runner.Layout.Near(((int)Math.Round(hive.CentreX + Math.Cos(angle) * 16),
                (int)Math.Round(hive.CentreY + Math.Sin(angle) * 16)));
            var chest = new HiveChest
            {
                Id = runner.Instance.World.TakeObjectId(),
                X = at.X,
                Y = at.Y,
                Rotation = (float)angle,
                Model = BrequelChestModels[i % BrequelChestModels.Length],
            };
            hive.Chests.Add(chest);
            await CreateWorldObject(runner, chest.Id, ChestBaseType, chest.X, chest.Y, chest.Rotation,
                model: chest.Model, opened: false, states: [("open", 0)]);
        }

        // Rewards: big Hiveblood, Wombgifts, Hivebrain Gland roll, quest body part roll
        byte level = AreaLevel(runner);
        int baseBlood = 140 + hive.Wave * 25 + hive.BrokenSpawners * 20;
        foreach (var m in members)
        {
            await GiveHiveblood(m, atlas.ScaleHiveblood(m, baseBlood, hive.BrokenSpawners, hive.EnragedWave));
            int gifts = rng.NextDouble() < 0.5 * atlas.WombgiftChanceFactor(hive.BrokenSpawners, enragedBonus: hive.EnragedWave) ? 2 : 1;
            for (int g = 0; g < gifts; g++)
            {
                string giftId = atlas.RollWombgiftBaseId(m, rng);
                if (MakeWombgift(m, giftId, level, rng) is { } gift)
                    await DropGroundItem(m, (hive.CentreX, hive.CentreY), gift, "Breach Hive completion Wombgift");
            }
            // Hivebrain Gland drop chance (30% in T1-T13, 50% in T14+)
            double glandChance = level >= 81 ? 0.50 : 0.30;
            if (rng.NextDouble() < glandChance && MakeHivebrainGland(m, level) is { } gland)
                await DropGroundItem(m, (hive.CentreX, hive.CentreY), gland, "Breach Hive Hivebrain Gland");
            if (TryRollBodyPart?.Invoke(m, level, rng) is { } bodyPart)
                await DropGroundItem(m, (hive.CentreX, hive.CentreY), bodyPart, "Breach Hive quest body part");
            await PlayerOf(m).Tell("Breach Hive Successfully Destroyed!");
        }
        Console.WriteLine($"[breach] Hive succeeded in {runner.Layout.Id} (kills={hive.Kills}, brokenSpawners={hive.BrokenSpawners})");
    }

    async Task FailDefense(Player runner, List<Player> members, HiveState hive)
    {
        hive.Phase = PhaseFailed;
        await SetObjectStates(runner, hive.StabiliserId, StabiliserType, ("interaction_state", 1), ("bomb_defence", PhaseFailed));
        if (hive.AilithId != 0)
            await SetMonsterStates(runner, hive.AilithId, ("bomb_defence", PhaseFailed), ("shielded", 0));
        foreach (var sp in hive.Spawners)
            await SetObjectStates(runner, sp.SpawnerId, SpawnerType, ("spawning", 3));
        foreach (var pad in hive.Skills)
        {
            pad.Ready = false;
            await SetObjectStates(runner, pad.Id, SkillObjectType, ("ready", 0));
        }
        if (hive.EnvironmentOn)
        {
            await Broadcast(members, InstanceEnvironments.Data(), "0x00df Hive environment override cleared");
            hive.EnvironmentOn = false;
        }
        await SendHiveRulesetUpdate(members, hive);
        await ClearMonsters(runner, hive.Monsters, hive.AllMonsterIds);
        foreach (var m in members)
            await PlayerOf(m).Tell("Failed to Defend Ailith.");
        Console.WriteLine($"[breach] Hive defense failed in {runner.Layout.Id} at wave {hive.Wave}/{hive.TotalWaves}");
    }

    async Task OpenHiveChest(Player player, HiveState hive, HiveChest chest)
    {
        if (chest.Opened) return;
        chest.Opened = true;
        if (Spec(ChestBaseType) is { ChestIndex: >= 0 } spec)
            await SendWorldRecord(player, chest.Id, InstanceEntities.ComponentUpdate(chest.Id, (byte)spec.ChestIndex, [1, 0, 0]), null);
        await SetObjectStates(player, chest.Id, ChestBaseType, ("open", 1));
        await DropBreachChestRewards(player, (chest.X, chest.Y), AreaLevel(player), AtlasOf(player, hive), AreaRng(player),
            brokenSpawners: hive.BrokenSpawners, isLarge: true);
    }

    static int SkillDamage(int areaLevel, BreachAtlas atlas, int baseDmg)
    {
        double levelScale = 1.0 + Math.Max(0, areaLevel - 68) * 0.12;
        double atlasScale = Math.Max(0.25, 1.0 + atlas.HiveSkillDamagePct / 100.0);
        return Math.Max(1, (int)Math.Round(baseDmg * levelScale * atlasScale));
    }

    static async Task DamageHiveMonstersNear(Player runner, HiveState hive, (int X, int Y) centre, double radius, int damage, double slowSeconds = 0)
    {
        double now = AreaTime(runner);
        foreach (uint id in hive.Monsters.ToList())
        {
            if (!runner.Area.Monsters.TryGetValue(id, out var m) || m.Dead || m.Life == 0) continue;
            var pos = m.Walk.Position(now);
            if (double.Hypot(pos.X - centre.X, pos.Y - centre.Y) > radius) continue;
            if (slowSeconds > 0) m.BusyUntil = Math.Max(m.BusyUntil, now + slowSeconds * 0.5);
            uint dealt = (uint)Math.Min(m.Life, (uint)Math.Max(1, damage));
            m.Life -= dealt;
            await SendWorldRecord(runner, id, InstanceProtocol.LifeUpdate(id, m.Life), null);
            if (m.Life == 0)
            {
                m.Dead = true;
                m.Aggro = false;
                m.ActionGeneration++;
            }
        }
    }

    /// <summary>Removes the encounter's living monsters from the area (a failed defense) and forgets their ids.</summary>
    internal static async Task ClearMonsters(Player runner, List<uint> monsters, HashSet<uint> allIds)
    {
        var area = runner.Area;
        using (HoldMonstersRemaining(runner))
        {
            foreach (uint id in monsters)
            {
                if (!area.Monsters.TryGetValue(id, out var m) || m.Dead || m.Life == 0) continue;
                m.Life = 0;
                m.Dead = true;
                m.Aggro = false;
                m.ActionGeneration++;
                area.Monsters.Remove(id);
                runner.Instance.World.MonsterOrigins.TryRemove(id, out _);
                await SendWorldRemoval(runner, id, null);
            }
        }
        monsters.Clear();
        allIds.Clear();
        await SendRemainingMonsters(runner);
    }

    static async Task SendHiveRulesetUpdate(List<Player> members, HiveState hive)
    {
        uint wireState = hive.Phase switch
        {
            PhaseRunning => BreachRulesets.EstablishedActive,
            PhaseSucceeded => BreachRulesets.EstablishedSucceeded,
            PhaseFailed => BreachRulesets.EstablishedFailed,
            _ => BreachRulesets.EstablishedInactive,
        };
        if (BreachRulesets.EstablishedState(wireState, hive.CentreX, hive.CentreY, isSkyFortress: false) is { } st)
            await Broadcast(members, st, $"0x02c9 EstablishedBrequelEncounterRules state={wireState}");
        if (BreachRulesets.EstablishedWave(hive.Phase == PhaseRunning ? hive.Wave : 0, hive.Phase == PhaseRunning ? hive.TotalWaves : 0) is { } wv)
            await Broadcast(members, wv, null);
        int remaining = Math.Max(0, hive.MaxHits - hive.HitsTaken);
        bool shielded = members.Count > 0 && hive.ShieldUntil > AreaTime(members[0]);
        if (BreachRulesets.EstablishedHealth(remaining, hive.MaxHits, shielded) is { } hp)
            await Broadcast(members, hp, null);
        if (BreachRulesets.EstablishedOverlay(hive.Phase == PhaseRunning) is { } ov)
            await Broadcast(members, ov, null);
        if (hive.Phase == PhaseRunning)
        {
            if (BreachRulesets.BarrierRadius(hive.CentreX, hive.CentreY, 60f, 60f, 1f, 1f) is { } br)
                await Broadcast(members, br, null);
            if (BreachRulesets.BarrierActive(true) is { } ba)
                await Broadcast(members, ba, null);
        }
        else if (hive.Phase is PhaseSucceeded or PhaseFailed)
        {
            if (BreachRulesets.BarrierActive(false) is { } ba)
                await Broadcast(members, ba, null);
        }
    }
}
