using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public sealed class HarbingerTests
{
    static MapDevice.Slot Slot(string leaf)
    {
        var type = Assert.IsType<ItemCatalog.BaseType>(Program.Items.Find("Metadata/Items/Scarabs/" + leaf));
        return new(type.Hash, type.Class, type.Name, type.Row, [], 0) { BaseId = type.Id };
    }

    static (int, int, int) Wave(HarbingerCatalog.HarbingerKind kind, int level) =>
        HarbingerCatalog.Profile(kind, level) is var p ? (p.BaseCount, p.ExtraCountChancePct, p.Permyriad) : default;

    [Fact]
    public void Harbinger_catalog_opening_and_shard_drops_honor_scarabs_stats_and_329_exclusions()
    {
        // All 28 Harbingers rows load (19 normal + 9 strong), level-filtered properly.
        Assert.Equal(28, HarbingerCatalog.All.Count);
        Assert.Equal(19, HarbingerCatalog.All.Count(e => !e.Strong));
        Assert.Equal(9, HarbingerCatalog.All.Count(e => e.Strong));
        Assert.DoesNotContain(HarbingerCatalog.Pool(20), e => e.Strong);
        Assert.Contains(HarbingerCatalog.Pool(75), e => e.Strong);

        // Wave profiles are the client's HarbingerSummonMonsters / 2 / Atlas rows for the monster level.
        Assert.Equal((6, 25, 410), Wave(HarbingerCatalog.HarbingerKind.Normal, 60));
        Assert.Equal((6, 25, 400), Wave(HarbingerCatalog.HarbingerKind.Strong, 60));
        Assert.Equal((8, 61, 181), Wave(HarbingerCatalog.HarbingerKind.Strong, 75));
        var kingWave = HarbingerCatalog.Profile(HarbingerCatalog.HarbingerKind.King, 83);
        Assert.Equal((15, 15, 1), (kingWave.BaseCount, kingWave.MaxMagic, kingWave.MaxRare));
        var atlasShards = new MapMods(new Dictionary<string, int> { ["map_harbingers_drops_additional_currency_shards"] = 38 });
        Assert.Equal(6, HarbingerCatalog.RollShards(Program.Items, 75, HarbingerCatalog.NormalBaseTenths, 0, atlasShards, new Random(1)).Count);

        // Exactly 11 active 3.29 currency shards; disabled 3.29 shards (Horizon, Harbinger's, Engineer's) excluded.
        Assert.Equal(11, HarbingerCatalog.Shards.Count);
        string[] excluded =
        [
            "Metadata/Items/Currency/CurrencyRerollMapTypeShard",
            "Metadata/Items/Currency/CurrencyUpgradeMapTierShard",
            "Metadata/Items/Currency/CurrencyStrongboxQualityShard",
        ];
        foreach (string id in excluded)
            Assert.DoesNotContain(HarbingerCatalog.Shards, s => s.Id == id);
        foreach (var entry in HarbingerCatalog.Shards)
        {
            var shardType = Assert.IsType<ItemCatalog.BaseType>(Program.Items.Find(entry.Id));
            Assert.True(shardType.Supported);
            Assert.True(shardType.BecomesWhenFull);
            var fullOrb = Assert.IsType<ItemCatalog.BaseType>(Program.Items.Row(shardType.FullStackRow));
            Assert.True(fullOrb.Supported);
        }

        // All 4 active 3.29 Harbinger scarabs are claimed as handled in MapOpening with no unhandled content.
        var catalog = MapRunCatalog.Load("data");
        MapDevice.Slot[] slots =
        [
            Slot("ScarabHarbingerNew1"),
            Slot("ScarabHarbingerNew2"),
            Slot("ScarabHarbingerNew3"),
            Slot("ScarabHarbingerNew4"),
        ];
        var mods = catalog.Of(slots, Program.Items.Mods);
        Assert.Equal(3, mods.Get("map_num_extra_harbingers"));
        Assert.Equal(1, mods.Get("map_harbinger_scarab_empower"));
        Assert.Equal(50, mods.Get("map_harbingers_%_chance_to_be_replaced_as_atlas_boss"));
        Assert.Equal(100, mods.Get("map_harbinger_currency_shards_duplicate_chance"));

        var opening = new MapOpening(new(new(1, "harbinger", 100, 100, 10, 10, null, []), 1, 1), slots, false,
            new(new(1, "MapWorldsStrand", "Strand Map", 8) { Level = 75 }, mods), "");
        MapScarabContent.Describe(opening);
        MapHarbinger.Opening(opening);
        Assert.Contains(MapHarbinger.Content, opening.HandledContent);
        Assert.Empty(opening.UnhandledContent);

        // Warhoards duplication emits identical pairs; bonus tenths increase stack counts; single-type locks one shard.
        var warhoards = new MapMods(new Dictionary<string, int>
        {
            ["map_harbinger_currency_shards_duplicate_chance"] = 100,
            ["map_harbinger_additional_currency_shard_stack_tenths_to_drop"] = 8,
            ["map_harbinger_single_currency_type"] = 1,
        });
        var dupDrops = HarbingerCatalog.RollShards(Program.Items, 75, HarbingerCatalog.NormalBaseTenths, 0, warhoards, new Random(42));
        Assert.Equal(6, dupDrops.Count); // (22 + 8) / 10 = 3 stacks, duplicated -> 6 stacks
        Assert.Single(dupDrops.Select(d => d.Type.Id).Distinct());
        for (int i = 0; i < dupDrops.Count; i += 2)
            Assert.Equal((dupDrops[i].Type.Id, dupDrops[i].Item.StackSize), (dupDrops[i + 1].Type.Id, dupDrops[i + 1].Item.StackSize));

        // Full-currency upgrade stat converts rolled shard stacks into their full orb (StackSize 1, non-shard).
        var fullCurrencyMods = new MapMods(new Dictionary<string, int>
        {
            ["map_harbingers_have_chance_to_drop_full_currency"] = 3, // 75% per stack
            ["map_harbinger_lucky_shard_drops"] = 1,
        });
        var upgraded = HarbingerCatalog.RollShards(Program.Items, 75, HarbingerCatalog.KingBaseTenths, HarbingerCatalog.KingStackSizePct, fullCurrencyMods, new Random(7));
        Assert.InRange(upgraded.Count, 10, 11);
        Assert.Contains(upgraded, d => !d.Type.BecomesWhenFull && d.Item.StackSize == 1);
    }

    [GameFilesFact]
    public async Task Harbingers_spawn_summon_waves_lose_life_on_kills_spawn_obelisks_and_drop_duplicated_shards()
    {
        var catalog = MapRunCatalog.Load("data");
        MapDevice.Slot[] slots =
        [
            Slot("ScarabHarbingerNew1"), // +3 harbingers
            Slot("ScarabHarbingerNew2"), // Empowered Obelisks
            Slot("ScarabHarbingerNew4"), // Warhoards 100% duplicate shards
        ];
        var scarabMods = catalog.Of(slots, Program.Items.Mods);
        // Add 1 guaranteed King Harbinger + cooldown speed + bonus tenths via composed stats so both regular and King
        // encounters and obelisk shard drops run deterministically without flakiness.
        var combined = new Dictionary<string, int>(scarabMods.Stats, StringComparer.Ordinal)
        {
            ["map_contains_X_additional_uber_harbingers"] = 1,
            ["map_harbinger_cooldown_speed_+%"] = 100,
            ["map_harbinger_additional_currency_shard_stack_tenths_to_drop"] = 5,
        };
        var mods = new MapMods(combined);

        var area = AreaInfo.Load(GamePack.Default.TryRead, "MapWorldsStrand", 75);
        var instance = new InstanceState();
        var stats = instance.AreaStats(() => Game.ComposeMapAreaStats(mods, 8, new Dictionary<uint, int>(), new HashSet<string>(), -1, false));
        uint seed = ServerLevels.PlayableSeed(area.Id, 0x4841, 75, stats);
        Assert.IsType<ServerLevels.Level>(ServerLevels.For(area.Id, seed, 75));
        var layout = AreaLayouts.For(area.Hash16, seed);
        instance.OpenedWith(new("HarbingerOwner", new(layout.Hash, area.Id, "Strand", 8) { Level = 75 }, mods));

        TestEnvironment.OpenOwnSaves("harbinger");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        await using var owner = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        await using var peer = await ScarabEncounterTests.Wire.Open(instance, layout, seed);
        instance.World.Runner = owner.Conn;
        try
        {
            MapHarbinger.Encounter[] Encounters() => JsonSerializer.Deserialize<MapHarbinger.Encounter[]>(instance.ContentState["Harbinger/encounters"])!;

            await MapHarbinger.Enter(owner.Conn);
            var list = Encounters();
            Assert.Equal(4, list.Length); // 3 from scarab + 1 guaranteed King
            Assert.Contains(list, h => h.Kind == HarbingerCatalog.HarbingerKind.King && h.Variety == HarbingerCatalog.KingVariety);
            // Second entrant shares the same encounters without double-spawning.
            await MapHarbinger.Enter(peer.Conn);
            Assert.Equal(list.Select(h => h.Monster), Encounters().Select(h => h.Monster));

            var king = list.First(h => h.Kind == HarbingerCatalog.HarbingerKind.King);
            var normal = list.First(h => h.Kind != HarbingerCatalog.HarbingerKind.King);
            var kingBody = owner.Conn.Monsters[king.Monster];
            Assert.Equal((MonsterRarity.Unique, true, true, MapHarbinger.HarbingerMaxLife),
                (kingBody.Rarity, kingBody.Scripted, kingBody.NoRewards, kingBody.MaxLife));
            Assert.Equal(1, kingBody.EncounterStats!["base_cannot_be_damaged"]);

            // Non-runner peer standing on the King Harbinger does not activate it.
            peer.Conn.Walk.Teleport(king.X, king.Y, 0);
            await MapHarbinger.Tick(peer.Conn, 0.25);
            Assert.Equal(MapHarbinger.HarbingerState.Waiting, Encounters()[king.Index].State);

            // Runner approaches the King Harbinger: activates, spawns 3 invulnerable helpers and first wave of minions.
            owner.Conn.Walk.Teleport(king.X + 8, king.Y, 0);
            await MapHarbinger.Tick(owner.Conn, 0.25);
            king = Encounters()[king.Index];
            Assert.Equal(MapHarbinger.HarbingerState.Active, king.State);
            Assert.Equal(3, king.Helpers.Count);
            Assert.NotEmpty(king.Minions);
            Assert.All(king.Helpers, hid => Assert.Equal(1, owner.Conn.Monsters[hid].EncounterStats!["base_cannot_be_damaged"]));

            // Advance timer (zero real-time sleep): Empowered Obelisk spawns from Scarab of Obelisks.
            await MapHarbinger.Tick(owner.Conn, 5.0);
            king = Encounters()[king.Index];
            Assert.Single(king.Obelisks);
            uint obeliskId = king.Obelisks[0];
            var obeliskBody = owner.Conn.Monsters[obeliskId];
            Assert.Contains(obeliskBody.Variety!.Id, HarbingerCatalog.EmpoweredObeliskVarieties);

            // Killing the Empowered Obelisk drops duplicated currency shard stacks (Warhoards).
            int groundBeforeObelisk = instance.Ground.All().Count();
            lock (owner.Conn.Monsters) { obeliskBody.Life = 0; obeliskBody.Dead = true; }
            MapHarbinger.Kill(new(owner.Conn, obeliskBody, obeliskBody.Variety!.Id, (king.X, king.Y)));
            await MapHarbinger.Tick(owner.Conn, 0.25);
            var obeliskDrops = instance.Ground.All().Skip(groundBeforeObelisk).ToArray();
            Assert.True(obeliskDrops.Length >= 2 && obeliskDrops.Length % 2 == 0,
                $"Expected even (>=2) Warhoards-duplicated obelisk drops, got {obeliskDrops.Length}");

            // Now activate a Normal/Strong Harbinger, kill its summoned waves until defeated, and verify life-loss & shard drops.
            owner.Conn.Walk.Teleport(normal.X + 8, normal.Y, 0);
            await MapHarbinger.Tick(owner.Conn, 0.25);
            normal = Encounters()[normal.Index];
            Assert.Equal(MapHarbinger.HarbingerState.Active, normal.State);
            Assert.NotEmpty(normal.Minions);

            uint startLife = normal.Life;
            uint firstMinion = normal.Minions[0];
            var firstBody = owner.Conn.Monsters[firstMinion];
            lock (owner.Conn.Monsters) { firstBody.Life = 0; firstBody.Dead = true; }
            MapHarbinger.Kill(new(owner.Conn, firstBody, firstBody.Variety!.Id, (normal.X, normal.Y)));
            await MapHarbinger.Tick(owner.Conn, 0.25);
            Assert.True(Encounters()[normal.Index].Life < startLife, "Killing a summoned minion must reduce Harbinger life");

            int groundBeforeDefeat = instance.Ground.All().Count();
            for (int guard = 0; guard < 120 && Encounters()[normal.Index].State != MapHarbinger.HarbingerState.Defeated; guard++)
            {
                var cur = Encounters()[normal.Index];
                foreach (uint mid in cur.Minions.ToArray())
                {
                    if (!owner.Conn.Monsters.TryGetValue(mid, out var mm) || mm.Dead) continue;
                    lock (owner.Conn.Monsters) { mm.Life = 0; mm.Dead = true; }
                    MapHarbinger.Kill(new(owner.Conn, mm, mm.Variety!.Id, (cur.X, cur.Y)));
                }
                await MapHarbinger.Tick(owner.Conn, 3.5);
            }

            var defeated = Encounters()[normal.Index];
            Assert.Equal(MapHarbinger.HarbingerState.Defeated, defeated.State);
            Assert.Equal(0u, defeated.Life);
            Assert.Empty(defeated.Obelisks);
            Assert.False(owner.Conn.Monsters.ContainsKey(normal.Monster));
            var harbingerDrops = instance.Ground.All().Skip(groundBeforeDefeat).ToArray();
            Assert.True(harbingerDrops.Length >= 4 && harbingerDrops.Length % 2 == 0,
                $"Expected even (>=4) Warhoards-duplicated Harbinger shard stacks, got {harbingerDrops.Length}");

            // Kept-instance restore: a stray unmanaged Harbinger copy is replaced without duplicating encounters.
            var strayTarget = list.First(h => h.Index != king.Index && h.Index != normal.Index);
            uint oldMonster = strayTarget.Monster;
            uint strayId;
            lock (owner.Conn.Monsters)
            {
                var kept = owner.Conn.Monsters[oldMonster];
                owner.Conn.Monsters.Remove(oldMonster);
                strayId = owner.Conn.Instance.World.NewMonsterId();
                owner.Conn.Monsters[strayId] = kept;
            }
            MapHarbinger.Leave(owner.Conn);
            await MapHarbinger.Enter(owner.Conn);
            var restored = Encounters()[strayTarget.Index];
            Assert.NotEqual(oldMonster, restored.Monster);
            Assert.False(owner.Conn.Monsters.ContainsKey(strayId));
            Assert.True(owner.Conn.Monsters[restored.Monster].Scripted);
        }
        finally
        {
            PlayerInventories.Forget(owner.Conn.Character!);
            PlayerInventories.Forget(peer.Conn.Character!);
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }
}
