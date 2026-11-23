using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Data.Terrain.Generation;
using GameServer.Modules.Harvest;

namespace GameServer.Gateway.Tests;

public sealed class HarvestCropRotationTests
{
    [Fact]
    public void Crop_rotation_starts_at_tier_1_and_upgrades_other_colours_by_level_values()
    {
        var tables = HarvestTables.Current;
        var level83 = Assert.IsType<HarvestTables.LevelValues>(tables.Level(83));
        var level80 = Assert.IsType<HarvestTables.LevelValues>(tables.Level(80));
        Assert.Equal((2500, 2000, 320), (level83.Upgrade1, level83.Upgrade2, level83.Upgrade3));
        Assert.Equal(0, level80.Upgrade3);

        // Only atlas_keystone_crop_rotation activates Crop Rotation; the map load's harvest league block stats stay ignored.
        var leagueBlockOnly = HarvestStats.From(new MapMods(new Dictionary<uint, int>
        {
            [Stat.MapHarvestSeedsAreTier1] = 1,
            [Stat.MapHarvestSeedsOfOtherColoursHaveChanceToUpgradeOnCompletingPlot] = 1,
        }));
        Assert.False(leagueBlockOnly.CropRotation);

        var cropRotation = HarvestStats.From(new MapMods(new Dictionary<uint, int>
        {
            [Stat.AtlasKeystoneCropRotation] = 1,
            [Stat.MapHarvestAlwaysHighTierSeeds] = 1,
            [Stat.MapHarvestSeedsAreAtLeastT2] = 1,
            [Stat.MapHarvestSeedT2UpgradePctChance] = 100,
            [Stat.MapHarvestSeedT3UpgradePctChance] = 100,
            [Stat.MapHarvestPlotsPctChanceToSpawnTier4Seed] = 100,
        }));
        Assert.True(cropRotation.CropRotation);

        // Normal grove under Crop Rotation starts strictly Tier 1, overriding Cornucopia and natural tier mods.
        for (int seed = 0; seed < 12; seed++)
        {
            var crops = GroveCrops.Roll(new Random(seed), tables, level83, cropRotation, sites: 7);
            Assert.NotEmpty(crops);
            foreach (var crop in crops)
            {
                Assert.Equal(GroveCrops.SeedsPerCrop, crop.Seeds.Sum(s => s.Count));
                Assert.All(crop.Seeds, s => Assert.Equal(1, tables.Seeds[s.Row].Tier));
            }
        }

        // Memory-line groves ignore Atlas keystones like Crop Rotation.
        var memoryStats = cropRotation with { BigHarvest = true, MixedColours = true, MemoryLinePlots = 5 };
        var memoryCrops = GroveCrops.RollMemoryLine(new Random(7), tables, level83, memoryStats, sites: 5);
        Assert.Equal(5, memoryCrops.Count);
        Assert.All(memoryCrops, c => Assert.All(c.Seeds, s => Assert.InRange(tables.Seeds[s.Row].Tier, 2, 4)));

        // Single upgrade step from all-Tier-1 advances each seed at most +1 tier (only T1 and T2, never T3/T4).
        IReadOnlyList<GroveCrops.SeedCount> initial = [new(tables.ByColourTier[HarvestTables.Red][1][0], 12),
            new(tables.ByColourTier[HarvestTables.Red][1][1], 11)];
        var afterOne = Assert.IsType<List<GroveCrops.SeedCount>>(
            GroveCrops.Upgrade(new Random(1), tables, level83, HarvestTables.Red, initial));
        Assert.Equal(GroveCrops.SeedsPerCrop, afterOne.Sum(s => s.Count));
        Assert.Contains(afterOne, s => tables.Seeds[s.Row].Tier == 2);
        Assert.DoesNotContain(afterOne, s => tables.Seeds[s.Row].Tier >= 3);
        Assert.True(afterOne.Zip(afterOne.Skip(1)).All(p => tables.Seeds[p.First.Row].Tier >= tables.Seeds[p.Second.Row].Tier));

        // Below area level 81 (Upgrade3 == 0), Tier 3 seeds never upgrade to Tier 4.
        IReadOnlyList<GroveCrops.SeedCount> allTier3 = [new(tables.ByColourTier[HarvestTables.Green][3][0], GroveCrops.SeedsPerCrop)];
        Assert.Null(GroveCrops.Upgrade(new Random(2), tables, level80, HarvestTables.Green, allTier3));

        // At level 83, repeated completions reach T2, T3, and multiple T4 bosses in one plot (no per-crop boss cap).
        bool sawMultipleBosses = false;
        for (int seed = 0; seed < 200 && !sawMultipleBosses; seed++)
        {
            if (GroveCrops.Upgrade(new Random(seed), tables, level83, HarvestTables.Blue,
                [new(tables.ByColourTier[HarvestTables.Blue][3][0], GroveCrops.SeedsPerCrop)]) is { } up)
            {
                Assert.Equal(GroveCrops.SeedsPerCrop, up.Sum(s => s.Count));
                if (up.Where(s => tables.Seeds[s.Row].Tier == 4).Sum(s => s.Count) >= 2) sawMultipleBosses = true;
            }
        }
        Assert.True(sawMultipleBosses);

        // Newly upgraded T2->T3 seeds roll FaridunChance into Corpse-Grown T3, which can later upgrade to T4.
        var allTier2 = new List<GroveCrops.SeedCount> { new(tables.ByColourTier[HarvestTables.Red][2][0], GroveCrops.SeedsPerCrop) };
        var toFaridun = Assert.IsType<List<GroveCrops.SeedCount>>(
            GroveCrops.Upgrade(new Random(5), tables, level83, HarvestTables.Red, allTier2, faridunChance: 100));
        var faridunSeeds = toFaridun.Where(s => tables.Seeds[s.Row].Faridun).ToList();
        Assert.NotEmpty(faridunSeeds);
        Assert.All(faridunSeeds, s => Assert.Equal((HarvestTables.Red, 3), (tables.Seeds[s.Row].Colour, tables.Seeds[s.Row].Tier)));
        Assert.DoesNotContain(toFaridun, s => tables.Seeds[s.Row].Tier == 3 && !tables.Seeds[s.Row].Faridun);

        var guaranteedUpgrade = level83 with { Upgrade3 = 10000 };
        var faridunToBoss = Assert.IsType<List<GroveCrops.SeedCount>>(
            GroveCrops.Upgrade(new Random(9), tables, guaranteedUpgrade, HarvestTables.Red, faridunSeeds));
        Assert.All(faridunToBoss, s => Assert.Equal((HarvestTables.Red, 4, false),
            (tables.Seeds[s.Row].Colour, tables.Seeds[s.Row].Tier, tables.Seeds[s.Row].Faridun)));
        Assert.Null(GroveCrops.Upgrade(new Random(9), tables, guaranteedUpgrade, HarvestTables.Red, faridunToBoss));
    }

    [Fact]
    public async Task Sacred_grove_completion_upgrades_idle_other_colours_persists_and_re_presents_changed_objects()
    {
        var tables = HarvestTables.Current;
        var level = SyntheticGroveLevel(siteCount: 4);
        var layout = new AreaLayout(1, "MapWorldsStrand", 300, 300, 10, 10, (x, y) => x is >= 0 and < 300 && y is >= 0 and < 300, [])
        {
            Level = level,
        };
        var mods = new MapMods(new Dictionary<uint, int>
        {
            [Stat.AtlasKeystoneCropRotation] = 1,
            [Stat.MapHarvestNumAdditionalPlots] = 1,
        });
        var instance = new InstanceState();
        instance.OpenedWith(new("CropOwner", new(layout.Hash, layout.Id, "Strand", 16) { Level = 83 }, mods));

        // Pick a deterministic seed whose 4 pairs include a colour appearing at least twice AND at least one other colour.
        uint chosenSeed = 0;
        for (uint s = 1; s <= 100; s++)
        {
            var probe = GroveCrops.Roll(new Random(unchecked((int)(s ^ 0x48525654u))), tables, tables.Level(83)!,
                HarvestStats.From(mods), sites: 4);
            var byColour = probe.GroupBy(c => c.Colour).ToDictionary(g => g.Key, g => g.Count());
            if (byColour.Any(kv => kv.Value >= 2) && byColour.Count >= 2)
            {
                chosenSeed = s;
                break;
            }
        }
        Assert.NotEqual(0u, chosenSeed);

        await using var enterWire = new CaptureWire();
        var player = NewConn(layout, instance, chosenSeed, enterWire);
        await SacredGrove.Enter(player);
        enterWire.CloseWriter();
        var enterPackets = ParsePackets(await enterWire.ReadAllAsync());
        Assert.Contains(enterPackets, p => p.Id == InstanceProtocol.AddRulesetId);

        var initial = ReadGrove(instance);
        Assert.Equal(8, initial.Crops.Count);
        Assert.All(initial.Crops, c => Assert.All(c.Seeds, s => Assert.Equal(1, tables.Seeds[s.Row].Tier)));

        // Choose a crop whose colour appears on another site so completion tests same-colour idle, other-colour idle, and withered.
        var chosen = initial.Crops.First(c =>
            initial.Crops.Any(other => other.Site != c.Site && other.Colour == c.Colour)
            && initial.Crops.Any(other => other.Site != c.Site && other.Colour != c.Colour));
        var chosenExtractor = initial.Objects[chosen.Extractor];

        // Stand directly at the Extractor so SacredGrove.Use starts immediately with zero walk delay.
        player.Walk.Teleport(chosenExtractor.X, chosenExtractor.Y, 0);
        await using var startWire = new CaptureWire();
        player.Connection.Session = new("harvest-start", startWire.Stream, startWire.Ciphers);
        Assert.True(await SacredGrove.Use(player, new(chosen.Extractor, 0, 1, 0), 0));
        startWire.CloseWriter();
        await startWire.ReadAllAsync();

        var running = ReadGrove(instance);
        var startedCrop = running.Crops.Single(c => c.Extractor == chosen.Extractor);
        Assert.Equal(SacredGrove.Phase.Running, startedCrop.Phase);
        var witheredPair = running.Crops.Single(c => c.Site == startedCrop.Site && c.Slot != startedCrop.Slot);
        Assert.Equal(SacredGrove.Phase.Withered, witheredPair.Phase);
        var sameColourIdle = running.Crops.Where(c => c.Phase == SacredGrove.Phase.Idle && c.Colour == startedCrop.Colour).ToList();
        var otherColourIdle = running.Crops.Where(c => c.Phase == SacredGrove.Phase.Idle && c.Colour != startedCrop.Colour).ToList();
        Assert.NotEmpty(sameColourIdle);
        Assert.NotEmpty(otherColourIdle);

        var beforeByExtractor = running.Crops.ToDictionary(c => c.Extractor, c => c.Seeds.ToList());
        var beforeSeedRows = otherColourIdle.SelectMany(c => c.SeedObjects)
            .ToDictionary(id => id, id => running.Objects[id].Seeds.Single().Row);

        // Kill all hatched monsters and complete the running crop on Tick, capturing every emitted wire packet.
        foreach (uint mid in startedCrop.Monsters)
            if (player.Area.Monsters.TryGetValue(mid, out var m)) { m.Dead = true; m.Life = 0; }
        await using var tickWire = new CaptureWire();
        player.Connection.Session = new("harvest-tick", tickWire.Stream, tickWire.Ciphers);
        await SacredGrove.Tick(player);
        tickWire.CloseWriter();
        var tickPackets = ParsePackets(await tickWire.ReadAllAsync());

        var after = ReadGrove(instance);
        var doneCrop = after.Crops.Single(c => c.Extractor == chosen.Extractor);
        Assert.Equal(SacredGrove.Phase.Done, doneCrop.Phase);
        Assert.Equal(4, after.Objects[doneCrop.Extractor].States["current_state"]);

        // Withered pair and idle same-colour crops stay completely untouched.
        Assert.Equal(beforeByExtractor[witheredPair.Extractor], after.Crops.Single(c => c.Extractor == witheredPair.Extractor).Seeds);
        foreach (var same in sameColourIdle)
            Assert.Equal(beforeByExtractor[same.Extractor], after.Crops.Single(c => c.Extractor == same.Extractor).Seeds);

        // Idle crops of other colours upgrade; the Extractor, the Irrigator and the seed objects hold the same seeds.
        var upgradedCrops = otherColourIdle.Select(c => after.Crops.Single(a => a.Extractor == c.Extractor))
            .Where(a => !a.Seeds.SequenceEqual(beforeByExtractor[a.Extractor])).ToList();
        Assert.NotEmpty(upgradedCrops);
        var recreated = new HashSet<uint>();
        foreach (var up in upgradedCrops)
        {
            Assert.Equal(GroveCrops.SeedsPerCrop, up.Seeds.Sum(s => s.Count));
            Assert.Contains(up.Seeds, s => tables.Seeds[s.Row].Tier == 2);
            Assert.Equal(up.Seeds, after.Objects[up.Extractor].Seeds);
            Assert.Equal(up.Seeds, after.Objects[up.Irrigator].Seeds);
            Assert.Equal(0, after.Objects[up.Extractor].States["in_combat"]);
            Assert.Equal(0, after.Objects[up.Irrigator].States["in_combat"]);
            var rows = up.SeedObjects.Select(id => after.Objects[id].Seeds.Single().Row).ToList();
            Assert.Equal(up.Seeds.SelectMany(s => Enumerable.Repeat(s.Row, s.Count)).Order(), rows.Order());
            Assert.All(up.SeedObjects, id => Assert.Equal(tables.Seeds[after.Objects[id].Seeds.Single().Row].Model, after.Objects[id].Model));
            // Only the seeds that rose change: every other seed object keeps its seed.
            var changed = up.SeedObjects.Where(id => beforeSeedRows[id] != after.Objects[id].Seeds.Single().Row).ToList();
            Assert.Equal(up.Seeds.Sum(s => Math.Max(0, s.Count - beforeByExtractor[up.Extractor].Where(b => b.Row == s.Row).Sum(b => b.Count))),
                changed.Count);
            recreated.UnionWith([up.Extractor, up.Irrigator, .. changed]);
        }

        // Wire: the completed Extractor gets 0x2bc; exactly the changed crops' Extractors and Irrigators and the changed
        // seed objects are removed (0x2bd) and created again (0x2bb) with their new seeds.
        Assert.Contains(tickPackets, p => p.Id == InstanceEntities.ComponentUpdateId && p.EntityId == doneCrop.Extractor);
        Assert.Equal(recreated, tickPackets.Where(p => p.Id == InstanceProtocol.RemoveObjectId).Select(p => p.EntityId).ToHashSet());
        foreach (uint id in recreated)
        {
            int removeAt = tickPackets.FindIndex(p => p.Id == InstanceProtocol.RemoveObjectId && p.EntityId == id);
            int created = tickPackets.FindIndex(p => p.Id == InstanceEntities.CreateObjectId && p.EntityId == id);
            Assert.True(created > removeAt, $"object 0x{id:x} must be removed before it is created again");
            Assert.Equal(BlobOf(after.Objects[id]), tickPackets[created].Blob);
        }

        var sampleUp = upgradedCrops[0];
        var expectedBlob = BlobOf(after.Objects[sampleUp.Extractor]);

        // Simulated module reload / re-entry: fresh InstanceState with persisted ContentState restores the upgraded objects.
        var reloadedInstance = new InstanceState();
        reloadedInstance.OpenedWith(new("CropOwner", new(layout.Hash, layout.Id, "Strand", 16) { Level = 83 }, mods));
        reloadedInstance.ContentState["Harvest/grove"] = instance.ContentState["Harvest/grove"];
        await using var reloadWire = new CaptureWire();
        var reloadedConn = NewConn(layout, reloadedInstance, chosenSeed, reloadWire);
        await SacredGrove.Enter(reloadedConn);
        reloadWire.CloseWriter();
        var reloadPackets = ParsePackets(await reloadWire.ReadAllAsync());
        var reloadedExtractor = reloadPackets.Single(p => p.Id == InstanceEntities.CreateObjectId && p.EntityId == sampleUp.Extractor);
        Assert.Equal(expectedBlob, reloadedExtractor.Blob);
    }

    static byte[] BlobOf(SacredGrove.GroveObject o)
    {
        var type = HarvestTables.Current.Objects[o.Type];
        var states = type.States!;
        foreach (var (name, value) in o.States) states = states.Set(name, value);
        return new QuestObject(o.X, o.Y, type.Components, o.Rotation, Model: o.Model)
        {
            StateMachine = states,
            HarvestSeeds = [.. o.Seeds.Select(s => ((byte)s.Row, (byte)s.Count))],
        }.Blob();
    }

    static SacredGrove.Grove ReadGrove(InstanceState instance) =>
        JsonSerializer.Deserialize<SacredGrove.Grove>(instance.ContentState["Harvest/grove"])!;

    static Player NewConn(AreaLayout layout, InstanceState instance, uint seed, CaptureWire wire) => new(layout, 1, seed)
    {
        Instance = instance,
        Character = { Name = "CropTester" },
        PlayerLife = 1000,
        LeagueModule = new(new League("crop-test", "Standard", false, false), new HashSet<string> { "Game", "Harvest" }),
        Connection = { LockstepClient = false, Session = new("harvest-test", wire.Stream, wire.Ciphers) },
    };

    static ServerLevels.Level SyntheticGroveLevel(int siteCount)
    {
        var objects = new List<PlacedTileObject>();
        for (int s = 0; s < siteCount; s++)
        {
            int bx = 50 + s * 60, by = 120;
            objects.Add(new(SacredGrove.ObjectRoot + "Pole", "", bx, by, 0, 0));
            objects.Add(new(SacredGrove.ObjectRoot + "StorageTank", "", bx, by + 4, 0, 0));
            objects.Add(new(SacredGrove.ObjectRoot + "Extractor", "", bx - 10, by - 10, 0, 0));
            objects.Add(new(SacredGrove.ObjectRoot + "Irrigator", "", bx - 10, by - 21, 0, 0));
            objects.Add(new(SacredGrove.ObjectRoot + "Extractor", "", bx + 10, by + 10, 0, 0));
            objects.Add(new(SacredGrove.ObjectRoot + "Irrigator", "", bx + 10, by + 21, 0, 0));
        }
        var record = new DoodadRecord(0, 0, 14, 14, [], "", []) { FeatureRow = "HarvestEncounter" };
        return new(300, 300, [], [], [], [], [], [], []) { RoomObjects = objects, Records = [record] };
    }

    readonly record struct Packet(ushort Id, uint EntityId, byte[] Blob);

    static List<Packet> ParsePackets(byte[] raw)
    {
        var list = new List<Packet>();
        int p = 0;
        while (p < raw.Length)
        {
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(p));
            p += 2;
            switch (id)
            {
                case InstanceProtocol.AddRulesetId:
                    p += 5;
                    list.Add(new(id, 0, []));
                    break;
                case InstanceProtocol.MonstersRemainingId:
                    p += 1;
                    list.Add(new(id, 0, []));
                    break;
                case InstanceProtocol.EnterWakeZoneId:
                case InstanceProtocol.RemoveObjectId:
                {
                    uint entity = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(p));
                    p += 10;
                    list.Add(new(id, entity, []));
                    break;
                }
                case InstanceEntities.ComponentUpdateId:
                {
                    uint entity = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(p));
                    int len = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(p + 10));
                    byte[] blob = raw[(p + 12)..(p + 12 + len)];
                    p += 12 + len;
                    list.Add(new(id, entity, blob));
                    break;
                }
                case InstanceEntities.CreateObjectId:
                {
                    uint entity = BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(p));
                    int len = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(p + 14));
                    byte[] blob = raw[(p + 16)..(p + 16 + len)];
                    p += 16 + len;
                    list.Add(new(id, entity, blob));
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unexpected S->C packet 0x{id:x4} at offset {p - 2}");
            }
        }
        return list;
    }

    sealed class CaptureWire : IAsyncDisposable
    {
        readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        readonly TcpClient _writer = new();
        readonly TcpClient _reader;
        readonly Task<byte[]> _readTask;
        public ConnectionCiphers Ciphers { get; }
        public NetworkStream Stream => _writer.GetStream();

        public CaptureWire()
        {
            byte[] key = new byte[64];
            Ciphers = new(key, isClient: false);
            var clientCipher = new ConnectionCiphers(key, isClient: true);
            _listener.Start();
            var accept = _listener.AcceptTcpClientAsync();
            _writer.Connect(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
            _reader = accept.GetAwaiter().GetResult();
            _readTask = Task.Run(async () =>
            {
                var stream = _reader.GetStream();
                using var ms = new MemoryStream();
                byte[] buf = new byte[4096];
                int n;
                while ((n = await stream.ReadAsync(buf)) > 0)
                {
                    clientCipher.Receive.Process(buf.AsSpan(0, n));
                    ms.Write(buf, 0, n);
                }
                return ms.ToArray();
            });
        }

        public void CloseWriter()
        {
            try { _writer.Client.Shutdown(SocketShutdown.Send); } catch { }
        }

        public Task<byte[]> ReadAllAsync() => _readTask;

        public async ValueTask DisposeAsync()
        {
            CloseWriter();
            try { await _readTask; } catch { }
            _reader.Dispose();
            _writer.Dispose();
            _listener.Stop();
        }
    }
}
