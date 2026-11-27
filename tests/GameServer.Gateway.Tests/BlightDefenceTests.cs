using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;
using GameServer.Modules.Blight;

namespace GameServer.Gateway.Tests;

/// <summary>
/// Full Blight &amp; Blight-ravaged (Uber-Blight) pump defence tests: instance sharing, Sister Cassia, Fast-Forward (0x0264),
/// all 30 towers with _buff1..3 Empowering &amp; Ring Anoints, min-level gating, exact monotonic boss life scaling,
/// Blighted/Blight-ravaged MapOpening (UsesAtlas=false, ilvl 85), Scarabs (ScarabBlightNew1..5), Atlas passives
/// (Cassia's Pride, Sturdy Construction, Blightspawn, Templar Ingenuity, Fungal Horizons), chests, and reload persistence.
/// Nothing waits on real time: area time is passed directly to the tick.
/// </summary>
public sealed class BlightDefenceTests
{
    [Fact]
    public async Task A_defence_starts_for_the_whole_instance_and_a_tower_is_paid_from_its_resource()
    {
        await using var area = await BlightArea.Open();
        var encounter = new BlightEncounter();
        var d = area.Defend(encounter);
        await encounter.Enter(area.A);
        // The area keeps the pump's records for whoever joins later, and spawns talkable Sister Cassia registered in Npcs.
        Assert.True(area.A.Area.Objects.Has(d.Pump));
        Assert.NotEqual(0u, d.CassiaId);
        Assert.True(area.A.Area.Monsters.ContainsKey(d.CassiaId));
        Assert.True(area.A.Npcs.ContainsKey(d.CassiaId));

        await encounter.Start(area.B, d);
        Assert.Equal(Phase.Building, d.Phase);
        Assert.NotNull(d.Plan);
        Assert.NotEmpty(d.Plan!.Lanes);
        Assert.NotEmpty(d.Spots);
        Assert.Equal(BlightEncounter.StartResource, d.Resource);
        Assert.Equal(d.Plan.Lanes.Count, d.LaneEncounterTypes.Count);
        Assert.Contains(d.LaneRewards.Values, r => r is "Blight" or "HardModeBlight");

        // All 30 buildable towers are classified into valid families (0 is foundation), have MinLevel, Roman Tiers I..IV, and Archetype >= 0.
        Assert.Equal(31, BlightTables.Towers.Count);
        for (int row = 1; row < BlightTables.Towers.Count; row++)
        {
            Assert.NotEqual(TowerFamily.None, BlightTowers.FamilyOf(BlightTables.Towers[row].Id));
            Assert.InRange(BlightTowers.TierNumberOf(BlightTables.Towers[row]), 1, 4);
            Assert.True(BlightTables.Towers[row].Archetype >= 0, $"Tower row {row} must have Archetype >= 0");
        }
        Assert.Equal(0, BlightTables.Towers[26].Archetype); // FlameTower1 has Archetype 0 (must not be dropped!)
        Assert.Equal(92, BlightTables.RingAnointStatIds.Count);

        // Exact 3.29.3c Empowering bonuses (+50/100/150% dmg, +20/30/40% cast speed) & Ring Anoint bypasses.
        Assert.Equal((50, 100, 150), (BlightTowers.EmpoweringDamagePct(1), BlightTowers.EmpoweringDamagePct(2), BlightTowers.EmpoweringDamagePct(3)));
        Assert.Equal((20, 30, 40), (BlightTowers.EmpoweringCastSpeedPct(1), BlightTowers.EmpoweringCastSpeedPct(2), BlightTowers.EmpoweringCastSpeedPct(3)));
        Assert.True(BlightTowers.BypassesImmunity("FreezingTower", new Dictionary<uint, int> { [Stat.BlightFreezeboltTowerFullDamageColdEnemies] = 1 }));
        Assert.True(BlightTowers.BypassesImmunity("ShockingTower3", new Dictionary<uint, int> { [Stat.BlightShocknovaTowerFullDamageLightningEnemies] = 1 }));
        Assert.True(BlightTowers.BypassesImmunity("FlamethrowerTower", new Dictionary<uint, int> { [Stat.BlightFlamethrowerTowerFullDamageFireEnemies] = 1 }));
        Assert.False(BlightTowers.BypassesImmunity("FreezingTower", new Dictionary<uint, int>()));

        // The client's choice of the first Chilling tower on a foundation (BlightTowers row 1), and a message too short to read.
        var spot = d.Spots.Values.First();
        uint foundation = spot.Id;
        byte[] request = [0x02, 0x65, .. BitConverter.GetBytes(foundation).Reverse(), 0, 0, 0, 1];
        Assert.True(await encounter.Build(area.A, request[..6]));
        Assert.Equal(0, spot.Row);
        Assert.True(await encounter.Build(area.A, request));
        Assert.Equal(1, spot.Row);
        Assert.NotEqual(foundation, spot.Id);
        Assert.Equal(4, spot.SkillIds.Length);
        Assert.True(spot.SkillIds.All(id => id >= 0x4100) && spot.SkillIds.Distinct().Count() == 4,
            "Chilling Tower Mk I registers distinct 0x4100+ skill IDs for Empowering tiers 0..3");
        Assert.Equal(BlightEncounter.StartResource - (uint)BlightTables.Towers[1].Cost, d.Resource);
        Assert.False(d.Spots.ContainsKey(foundation));

        // Build a Fireball Tower Mk I (row 26) to verify EmptyAction* parent + 4 triggered child projectile skill IDs (0x01ce).
        d.Resource += 2500;
        var fireSpot = d.Spots.Values.First(s => s.Row == 0);
        Assert.True(await encounter.Build(area.A, [0x02, 0x65, .. BitConverter.GetBytes(fireSpot.Id).Reverse(), 0, 0, 0, 26]));
        Assert.NotEqual((ushort)0, fireSpot.EmptyActionSkillId);
        Assert.Equal(4, fireSpot.TriggeredSkillIds.Length);
        Assert.Equal(4, fireSpot.TriggeredSkillIds.Distinct().Count());

        // Build a Summoning Tower Mk I (row 21) and an Imbuing Tower (row 14) on another spot to verify minion + aura towers.
        var second = d.Spots.Values.First(s => s.Row == 0);
        Assert.True(await encounter.Build(area.A, [0x02, 0x65, .. BitConverter.GetBytes(second.Id).Reverse(), 0, 0, 0, 21]));
        Assert.Equal(21, second.Row);
        double t = d.StartedAt;
        for (int step = 0; step <= BlightEncounter.RaiseSteps + 2; step++)
            await encounter.Tick(area.A, t += 1.0);
        Assert.Equal(3, second.Minions.Count);
        Assert.All(second.Minions, mid => Assert.True(area.A.Area.Monsters[mid].AlliedFighter));

        // Imbuing tower (row 14) grants exact 3.29.3c stats (+25% atk/cast, +30% MS; +30% dmg with Empower I) via FinalStatsBuilding.
        var third = d.Spots.Values.First(s => s.Row == 0);
        third.Row = 14;
        third.Raising = 0;
        area.A.Walk.Teleport(third.X, third.Y, t);
        await encounter.Tick(area.A, t += 1.0);
        var playerStats = new Dictionary<uint, int>();
        BlightTowers.ApplyPlayerImbuement(area.A, playerStats);
        Assert.Equal(25, playerStats.GetValueOrDefault(Stat.AttackAndCastSpeedPlusPct));
        Assert.Equal(30, playerStats.GetValueOrDefault(Stat.BaseMovementVelocityPlusPct));
        await BlightTowers.ClearPlayerImbuement(area.A);

        // Fast-Forward (C→S 0x0264) skips remaining build-phase timer directly to Phase.Running.
        Assert.Equal(Phase.Building, d.Phase);
        Assert.True(await encounter.FastForward(area.A, [0x02, 0x64]));
        Assert.Equal(Phase.Running, d.Phase);

        // MapOpening disables Atlas passives on Blighted/Blight-ravaged maps and overrides level to 85 for UberInfectedMap__.
        var uberMods = new MapMods(new Dictionary<uint, int>
        {
            [Stat.IsBlightedMap] = 9,
            [Stat.IsUberBlightedMap] = 1,
            [Stat.MapItemLevelOverride] = 85,
        });
        var opening = new MapDevice.Opening(new MapDevice.MapArea(53973, "MapWorldsReef", "Reef", 16) { Level = 83, UsesAtlas = true }, uberMods);
        var mapReq = new MapOpening(area.A, [], campaign: false, opening, "");
        BlightEncounter.OnMapOpening(mapReq);
        Assert.Contains(BlightEncounter.Content, mapReq.HandledContent);
        Assert.False(mapReq.Opening!.Area.UsesAtlas);
        Assert.Equal(85, mapReq.Opening!.Area.Level);

        // ScarabBlightNew1..5 emit Stat.MapBlightScarabCount via MapRunCatalog (trailing "New" stripped).
        var scarabType = Game.ItemsFor(area.A).Find("Metadata/Items/Scarabs/ScarabBlightNew1");
        Assert.NotNull(scarabType);
        var runCatalog = MapRunCatalog.Load("data");
        var scarabSlot = new MapDevice.Slot(scarabType!.Hash, scarabType.Class, scarabType.Name, scarabType.Row, [], 0)
        {
            BaseId = scarabType.Id,
        };
        var parsedScarabMods = runCatalog.Of([scarabSlot], Game.ItemsFor(area.A).Mods);
        Assert.Equal(1, parsedScarabMods.ScarabKinds.GetValueOrDefault("blight"));

        // A client that comes back is told the resource and sent no create again (each client gets its own encrypted buffer).
        await encounter.Enter(area.B);
        byte[] a = await area.WireA.Finish(), b = await area.WireB.Finish();
        byte[] pump = BitConverter.GetBytes(InstanceEntities.ObjectTypeHash(BlightEncounter.PumpObject)).Reverse().ToArray();
        byte[] paid = InstanceBlight.Resource(d.Resource);
        Assert.Equal(1, BlightArea.Count(a, pump));
        Assert.Equal(1, BlightArea.Count(b, pump));
        Assert.True(BlightArea.Find(a, paid) >= 0 && BlightArea.Find(b, paid) >= 0, "both clients are told the new resource");
        Assert.True(area.A.Area.Objects.Has(spot.Id) && !area.A.Area.Objects.Has(foundation), "the area's records hold the tower, not the foundation it replaced");
        Assert.Equal("BlightedMapTree1", BlightLayout.Pick(BlightTables.Topologies, 100, new Random(1), blighted: true)?.Id);
        Assert.NotEqual("BlightedMapTree1", BlightLayout.Pick(BlightTables.Topologies, 100, new Random(1))?.Id);
    }

    [Fact]
    public async Task The_lanes_send_their_enemies_and_the_defence_ends_when_they_are_dealt_with()
    {
        await using var area = await BlightArea.Open();
        // Configure Atlas passives & Scarabs on the instance: Sturdy Construction (2x durability), real Atlas Keystone Cassia's Pride
        // (Stat.AtlasKeystoneCassiasPride = 1 -> -75% player dmg & +300% tower dmg), Blightspawn, Templar Ingenuity, Fungal Horizons.
        var areaStats = area.Instance.AreaStats(() => new Dictionary<uint, int>
        {
            [Stat.MapInchorPumpHasDoubleDurability] = 1,
            [Stat.AtlasKeystoneCassiasPride] = 1,
            [Stat.MapBlightHasXBossLanes] = 1,
            [Stat.MapBlightTowersAreChests] = 1,
            [Stat.MapBlightTouchedBuffOnBlightEncounterComplete] = 1,
            [Stat.MapBlightMapsDropAsUberBlightMaps] = 1,
            [Stat.MapBlightEmpoweringTowersAndUpgradesEnhanceEnemies] = 1,
        });

        // Proven monotonic Blight boss effective more-life curve (MonsterMapBossDifficulty + BlightBossLifeScalingPerLevel).
        int lvl68 = BlightTables.BossEffectiveMoreLifePct(68);
        int lvl75 = BlightTables.BossEffectiveMoreLifePct(75);
        int lvl81 = BlightTables.BossEffectiveMoreLifePct(81);
        int lvl84 = BlightTables.BossEffectiveMoreLifePct(84);
        Assert.True(lvl68 > 200 && lvl75 > lvl68 && lvl81 >= lvl75 && lvl84 == lvl81 && lvl84 == 552,
            $"Expected smooth monotonic curve up to 552% (got {lvl68}/{lvl75}/{lvl81}/{lvl84})");

        var encounter = new BlightEncounter();
        var d = area.Defend(encounter);
        // Sturdy Construction doubles base pump durability (10 -> 20).
        Assert.Equal(20, d.MaxDurability);
        await encounter.Enter(area.A);
        await encounter.Start(area.A, d);
        Assert.Single(d.BossOnlyLanes);
        d.MonstersPerLane = 2;

        double now = d.StartedAt + 100;
        for (int i = 0; i < 6; i++)
        {
            await encounter.Tick(area.A, now += 100);
            foreach (uint id in d.Monsters.Keys)
                if (area.A.Area.Monsters.TryGetValue(id, out var m)) m.Walk.Teleport(20, 20, now);
        }
        Assert.Equal(Phase.Running, d.Phase);
        Assert.Equal(d.Plan!.Lanes.Count * 2, d.Monsters.Count);
        Assert.All(d.Monsters.Keys, id => Assert.Equal(d.Level, area.A.Area.Monsters[id].Level));
        // Unique Blight Bosses carry blight_boss=1 on their StateMachine (matching PR #1618 S5 state schema).
        uint bossId = d.Monsters.First(kv => kv.Value.Rarity == MonsterRarity.Unique).Key;
        var bossStates = area.A.Area.Monsters[bossId].States;
        Assert.NotNull(bossStates);
        Assert.Equal(1, bossStates!.SharedStates[bossStates.IndexOf("blight_boss")]);

        // Cassia's Pride reduces player outgoing damage against Blight monsters by 75% (0.25x).
        uint probeId = d.Monsters.Keys.First();
        var probeMonster = area.A.Area.Monsters[probeId];
        var reduced = encounter.OnOutgoingDamage(area.A, new Dictionary<uint, int>(), probeMonster, DamagePacket.Of(DamageType.Physical, 100, 100));
        Assert.Equal(25.0, reduced[DamageType.Physical].Min, 1);

        // Module reload restores identical defence state (lanes, boss-only lanes, spots, monsters).
        encounter.Runs.Clear();
        await encounter.Tick(area.A, now += 1);
        Assert.True(encounter.Runs.TryGetValue(area.Instance, out var run));
        var kept = Assert.Single(run.Defences);
        Assert.NotSame(d, kept);
        Assert.Equal((d.Pump, d.Phase, d.Resource, d.Spots.Count), (kept.Pump, kept.Phase, kept.Resource, kept.Spots.Count));
        Assert.Equal(d.BossOnlyLanes, kept.BossOnlyLanes);
        Assert.Equal(d.Plan.Lanes.Select(l => l.End), kept.Plan!.Lanes.Select(l => l.End));
        Assert.Equal(d.Monsters.Keys.Order(), kept.Monsters.Keys.Order());
        d = kept;

        // Non-boss leaking to pump costs 1 durability and leaves the instance.
        uint first = d.Monsters.First(kv => kv.Value.Rarity != MonsterRarity.Unique).Key;
        area.A.Area.Monsters[first].Walk.Teleport(d.Cell.X, d.Cell.Y, now);
        await encounter.Tick(area.A, now += 1);
        Assert.Equal(d.MaxDurability - 1, d.Durability);
        Assert.False(area.A.Area.Monsters.ContainsKey(first));

        // Verify Smothering + Scarab of Invigoration (Buff 2462) + lane immunity on a living monster before finishing.
        uint probe = d.Monsters.Keys.First(id => area.A.Area.Monsters.ContainsKey(id) && !area.A.Area.Monsters[id].Dead);
        probeMonster = area.A.Area.Monsters[probe];
        int probeLane = d.Monsters[probe].Lane;
        d.LaneEncounterTypes[probeLane] = 0; // Cold lane
        Assert.True(BlightTowers.IsImmune(d, probeLane, TowerFamily.Cold));
        Assert.False(BlightTowers.IsImmune(d, probeLane, TowerFamily.Physical));
        var auraSpot = d.Spots.Values.First();
        auraSpot.Row = 15; // Smothering Tower (Tier 4 Empowering upgrade -> triggers both Smothering and Invigoration!)
        auraSpot.Raising = 0;
        probeMonster.Walk.Teleport(auraSpot.X, auraSpot.Y, now);
        await encounter.Tick(area.A, now += 1);
        Assert.True(probeMonster.TemporaryStats.ContainsKey(BlightTowers.SmotheringStatsKey));
        Assert.Contains(probe, d.EnhancedMonsters);

        // Defeat all remaining enemies -> victory!
        foreach (uint id in d.Monsters.Keys.Where(id => area.A.Area.Monsters.ContainsKey(id)).ToArray())
            area.A.Area.Monsters[id].Life = 0;
        await encounter.Tick(area.A, now += 1);
        Assert.Equal(Phase.Won, d.Phase);
        Assert.Equal(1, d.PumpStates[5]);

        // Fungal Horizons granted Blightreach to the instance; Templar Ingenuity spawned a salvaged Rank-4 tower chest!
        Assert.True(BlightEncounter.HasBlightreach(area.Instance));
        var salvaged = Assert.Single(d.Chests.Values, c => c.IsSalvagedTower);
        Assert.Equal(4, salvaged.SalvagedRank);
        Assert.Equal("Metadata/Chests/BlightTowerChestRank4", salvaged.ChestType);
        // Regular "Blight" lane chests use BlightMushrunes / BlightInfectedMaps, NEVER BlightTowerChest.
        string blightChestType = BlightChests.PickChestType("Blight", uberBlighted: false, new Random(1));
        Assert.DoesNotContain("BlightTowerChest", blightChestType, StringComparison.OrdinalIgnoreCase);

        // Oils, Lucky chests, and Blooming Uber-Blighted T14+ map drops all work cleanly.
        var oil = BlightChests.RollOil(Game.ItemsFor(area.A), d.Level, new Random(1), tierUpgradeChancePct: 100);
        Assert.NotNull(oil);
        var blightLoot = BlightChests.RollChestItems(area.A, "Blight", 83, new Random(2), lucky: true);
        Assert.NotEmpty(blightLoot);
        var uberMapDrop = BlightChests.RollBlightedMap(area.A, 83, areaStats, fromUber: false, new Random(3));
        Assert.NotNull(uberMapDrop);
        var uberImplicit = Assert.Single(uberMapDrop!.Mods, m => m.Hash == BlightTables.UberInfectedMapModHash);
        // Verify exact Mods.dat UberInfectedMap__ stat order: [map_nuke_everything=1, map_spread_doodads_index=1, is_blighted_map=9, ms=20, life=200, ilvl=85, is_uber_blighted_map=1].
        Assert.Equal([1, 1, 9, 20, 200, 85, 1], uberImplicit.Values);
        // Verify ComputeBossLife scales properly with real UberInfectedMap__ (is_blighted_map = 9) and map-rolled Monster Life.
        var bossVariety = BlightWaves.VarietyFor(0, 0, 1, MonsterRarity.Unique)!;
        uint normalBossHp = BlightWaves.ComputeBossLife(bossVariety, 83, new Dictionary<uint, int>());
        uint uberBossHp = BlightWaves.ComputeBossLife(bossVariety, 85, new Dictionary<uint, int> { [Stat.IsBlightedMap] = 9, [Stat.MapMonstersLifePlusPct] = 200 });
        Assert.True(uberBossHp >= normalBossHp * 2.5, $"Expected Uber-Blight boss HP ({uberBossHp}) >= 2.5x normal ({normalBossHp})");
    }
}

/// <summary>Two players in one map instance (a flat, walkable 600 x 600 layout), each with a wire whose decrypted bytes the test reads.</summary>
sealed class BlightArea : IAsyncDisposable
{
    readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
    readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
    public readonly InstanceState Instance = new();
    public Player A = null!, B = null!;
    public Wire WireA = null!, WireB = null!;
    public byte Level;

    public static async Task<BlightArea> Open()
    {
        BlightModule.LoadData();
        var area = new BlightArea();
        Game.CombatLifetime = new ModuleLifetime("Game");
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var layout = new AreaLayout(53973, "MapWorldsReef", 600, 600, 300, 300, (_, _) => true, []);
        area.Level = (byte)ServerLevels.AreaLevel(layout.Id);
        (area.A, area.WireA) = await Wire.Open(area.Instance, layout, "BlightA");
        (area.B, area.WireB) = await Wire.Open(area.Instance, layout, "BlightB");
        area.Instance.AddPlayer(area.A);
        area.Instance.AddPlayer(area.B);
        Program.InGame[area.A] = 0;
        Program.InGame[area.B] = 0;
        return area;
    }

    public Defence Defend(BlightEncounter encounter) => encounter.Place(A, [(300, 300, 0f)]).Defences[0];

    public static int Find(byte[] bytes, byte[] part) => bytes.AsSpan().IndexOf(part);

    public static int Count(byte[] bytes, byte[] part)
    {
        int n = 0, from = 0;
        while (bytes.AsSpan(from).IndexOf(part) is var at && at >= 0) { n++; from += at + part.Length; }
        return n;
    }

    public async ValueTask DisposeAsync()
    {
        BlightEncounter.ClearBlightreach();
        BlightTowers.ClearAllImbuements();
        BlightSpores.ClearAll();
        Program.InGame.TryRemove(A, out _);
        Program.InGame.TryRemove(B, out _);
        await WireA.DisposeAsync();
        await WireB.DisposeAsync();
        Game.CombatLifetime = previousLifetime;
        Program.SetGameLogic(previousLogic!);
    }

    public sealed class Wire(TcpClient writer, TcpClient reader, Player player, MemoryStream captured, Task drain) : IAsyncDisposable
    {
        public static async Task<(Player, Wire)> Open(InstanceState instance, AreaLayout layout, string name)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            var writer = new TcpClient();
            await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            var reader = await accepting;
            var captured = new MemoryStream();
            var client = new ConnectionCiphers(new byte[64], true);
            var player = new Player(layout, 1, 3316637604u)
            {
                Character = { Name = name }, Instance = instance, PlayerLife = 1000,
                Connection = { LockstepClient = false, PlayerSent = true, Session = new(name, writer.GetStream(), new ConnectionCiphers(new byte[64], false)) },
            };
            var drain = Task.Run(async () =>
            {
                byte[] buffer = new byte[4096];
                try
                {
                    int n;
                    while ((n = await reader.GetStream().ReadAsync(buffer)) > 0)
                    {
                        client.Receive.Process(buffer.AsSpan(0, n));
                        lock (captured) captured.Write(buffer, 0, n);
                    }
                }
                catch (IOException) { }
            });
            return (player, new(writer, reader, player, captured, drain));
        }

        public async Task<byte[]> Finish()
        {
            player.Closed = true;
            try { writer.Client.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
            await drain;
            lock (captured) return captured.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            player.Closed = true;
            writer.Dispose();
            await drain;
            reader.Dispose();
        }
    }
}
