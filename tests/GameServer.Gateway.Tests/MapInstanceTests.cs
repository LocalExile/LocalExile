using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public sealed class MapInstanceTests
{
    [Fact]
    public async Task A_map_instance_keeps_the_map_it_was_opened_with()
    {
        var layout = Game.MapDevices.Value.Atlas!.Find("Strand")!;
        var strand = new MapDevice.MapArea(layout.Hash16, layout.Id, "Strand Map", 3) { Level = 70 };
        var first = new MapMods(new Dictionary<string, int>
        {
            ["map_monsters_life_+%"] = 40, ["map_boss_maximum_life_+%"] = 30,
            ["map_boss_damage_+%"] = 30, ["map_boss_attack_and_cast_speed_+%"] = 20,
            [MonsterModCatalog.MapHiddenLifeStat] = 13, [MonsterModCatalog.MapHiddenDamageStat] = 2,
        });
        var second = new MapMods(new Dictionary<string, int> { ["map_monsters_damage_+%"] = 25 });
        var store = new InstanceStore(TimeProvider.System, TimeSpan.FromMinutes(8));
        const string owner = "MapInstanceRegressionOwner";
        try
        {
            // The first portal makes the instance: the transfer carries the map, Auth answers with instance 41.
            var open = MapDevice.Activate(owner, strand, first);
            var (taken, _) = MapDevice.TakePortal(owner, 0)!.Value;
            MapDevice.TicketArrivals[0xa1] = MapDevice.Run.Of(owner, taken);
            MapDevice.Entered(owner, strand, 41);
            var instance = store.Enter(41, 1);
            Assert.Same(first, MapDevice.Admit(instance, 0xa1, strand.Hash16, 41)!.Mods);
            Assert.False(MapDevice.TicketArrivals.ContainsKey(0xa1));

            // The owner opens another map of the same area: instance 41 still plays the first one, for a login inside it
            // and for a guest who joins it.
            MapDevice.Activate(owner, strand, second);
            Assert.Same(first, MapDevice.Admit(store.Enter(41, 1), 0xa2, strand.Hash16, 41)!.Mods);
            Assert.Equal(owner, instance.Map!.Owner);
            Assert.Equal(70, instance.Map.Area.Level);
            await CheckBossOnReentry(instance, strand);

            // Its last portal closes the device, but the player it takes in still gets the map, and so does a guest after.
            MapDevice.Entered(owner, strand, 42);
            MapDevice.Run? last = null;
            for (int i = 0; i < MapDevice.PortalCount; i++)
                if (MapDevice.TakePortal(owner, i) is var (map, _)) last = MapDevice.Run.Of(owner, map);
            Assert.Equal(0, MapDevice.OpenOf(owner)!.PortalsLeft); // Retained for completion and return routing.
            MapDevice.TicketArrivals[0xa3] = last!;
            var later = store.Enter(42, 1);
            Assert.Same(second, MapDevice.Admit(later, 0xa3, strand.Hash16, 42)!.Mods);
            Assert.Same(second, MapDevice.Admit(store.Enter(42, 1), 0xa4, strand.Hash16, 42)!.Mods);

            // An area the map does not open plays without it.
            Assert.Null(MapDevice.Admit(store.Enter(43, 1), 0xa5, 0x4321, 43));

            // The area stats are composed by the instance's first connection only: a later one (another atlas, another
            // map) gets the same list, so its level is the one already generated.
            int composed = 0;
            var mutable = new Dictionary<uint, int> { [1] = 40 };
            var stats = instance.AreaStats(() => { composed++; return mutable; });
            mutable[1] = 999;
            Assert.Equal(40, stats[1]);
            var again = instance.AreaStats(() => { composed++; return new Dictionary<uint, int> { [1] = 99 }; });
            Assert.Same(stats, again);
            Assert.Equal(1, composed);
            Assert.Equal(new Dictionary<uint, int> { [1] = 40, [2] = 5 },
                InstanceEvents.Merge(stats, new Dictionary<uint, int> { [2] = 5 }));
            var areaRule = new Dictionary<uint, int> { [1] = 7 };
            uint seedA = ServerLevels.PlayableSeed("MapRuleIsolationTest", 12345, 70, areaRule);
            areaRule[1] = 11;
            uint seedB = ServerLevels.PlayableSeed("MapRuleIsolationTest", 12345, 70, areaRule);
            Assert.NotEqual(seedA, seedB);
            Assert.Equal(seedA, ServerLevels.PlayableSeed("MapRuleIsolationTest", 12345, 70,
                new Dictionary<uint, int> { [1] = 7 }));
            Assert.Equal(seedB, ServerLevels.PlayableSeed("MapRuleIsolationTest", 12345, 70, areaRule));

            uint life = Program.Stats.Key("map_monsters_life_+%")!.Value;
            uint effect = Program.Stats.Key("map_mod_effect_+%_for_non_unique_maps")!.Value;
            var explicitStats = new Dictionary<string, int> { ["map_monsters_life_+%"] = 40 };
            var atlas = new Dictionary<uint, int> { [effect] = 50 };
            var unique = new MapMods(explicitStats) { MapRarity = GameServer.Core.Protocol.Instance.ItemRarity.Unique,
                ExplicitMods = [explicitStats] };
            Assert.Equal(40, Game.ComposeMapAreaStats(unique, 3, atlas, new HashSet<string>(), 0)[life]);
            var rare = new MapMods(explicitStats) { ExplicitMods = [explicitStats] };
            Assert.Equal(60, Game.ComposeMapAreaStats(rare, 3, atlas, new HashSet<string>(), 0)[life]);
            Assert.Equal(40, Game.ComposeMapAreaStats(first, 3, atlas, new HashSet<string>(), 0)[life]);
        }
        finally { MapDevice.Close(owner); }
    }

    static async Task CheckBossOnReentry(InstanceState instance, MapDevice.MapArea map)
    {
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        await using var wire = await ScarabEncounterTests.Wire.Open(instance, new(map.Hash16, map.Id, 100, 100, 20, 20, null, []), 1);
        var conn = wire.Conn;
        var (remote, stream, ciphers) = conn.Wire;
        try
        {
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
            var variety = Game.Monsters.Find(Game.Packs.BossesFor(map.Id)[0].ToString())!;
            Assert.False(variety.BossHealthBar);
            byte level = (byte)map.Level;
            conn.PartySize = 2;
            uint id = await Game.SpawnMonster(remote, stream, ciphers, conn, variety, level, MonsterRarity.Unique, 25, 25, bulk: true);
            var boss = conn.Monsters[id];
            Assert.Equal(170, boss.Mods.Effects["maximum_life_+%"]); // Map 40, boss 30, second player 100.
            Assert.Equal(30, boss.Mods.Effects["damage_+%"]);
            int baseLife = Game.Monsters.BaseLife(variety, level)!.Value;
            Assert.Equal(229, boss.Mods.Effects[MonsterModCatalog.MapHiddenLifeStat]); // At level 70, 216 + 13.
            Assert.Equal(2, boss.Mods.Effects[MonsterModCatalog.MapHiddenDamageStat]);
            Assert.Equal((uint)boss.Mods.Effects.MaxLife(baseLife), boss.MaxLife);
            var secondary = boss.Kit[1];
            var raw = BossKits.AttackFor(conn, variety, level, secondary.Effect, secondary.Attack.SkillId);
            var hit = boss.Mods.Effects.Hit(raw.Damage);
            Assert.True(hit.MinTotal > raw.Damage.MinTotal);
            Assert.Equal(hit[DamageType.Physical], secondary.Attack.Damage[DamageType.Physical]);
            Assert.Equal(boss.Mods.Effects.AttackSeconds(raw.Seconds), secondary.Attack.Seconds);

            boss.Life /= 2;
            boss.Mods = boss.Mods with { Effects = boss.Mods.Effects.Plus("damage_+%", 7) }; // Acquired after spawning.
            var saved = Assert.Single(SavedMonster.Save([boss], 0));
            conn.PartySize = 1;
            var restored = conn.Monsters[await Game.RestoreMonster(remote, stream, ciphers, conn, variety, saved, bulk: true)];
            Assert.Equal(70, restored.Mods.Effects["maximum_life_+%"]);
            Assert.Equal(37, restored.Mods.Effects["damage_+%"]);
            Assert.Equal((uint)restored.Mods.Effects.MaxLife(baseLife), restored.MaxLife);
            Assert.Equal(boss.Life, restored.Life);

            // An older instance can hold a wounded monster from before species modifiers were saved.
            var sanguine = Game.Monsters.Find("Metadata/Monsters/BloodElemental/BloodElementalBossInvasion")!;
            var legacy = new SavedMonster(sanguine.Row, level, MonsterRarity.Normal, true, 30, 30, 100);
            var recovered = conn.Monsters[await Game.RestoreMonster(remote, stream, ciphers, conn, sanguine, legacy, bulk: true)];
            var kept = Assert.Single(SavedMonster.Save([recovered], 0));
            var again = conn.Monsters[await Game.RestoreMonster(remote, stream, ciphers, conn, sanguine, kept, bulk: true)];
            Assert.Empty(again.Mods.Affixes);
            Assert.Equal(80, again.Effects["life_regeneration_rate_per_minute_%"]);
            Assert.Equal(100u, again.Life);
            Assert.Equal(kept.Mods.Mods, again.Mods.Mods);

            var valdoMods = new MapMods(new Dictionary<string, int>
            {
                ["map_contains_X_harvest_bear_bosses"] = 2,
                ["map_contains_X_harvest_cat_bosses"] = 1,
                ["map_contains_X_harvest_bird_bosses"] = 1,
                ["map_contains_evil_einhar"] = 2,
            });
            var empty = new PackCatalog.AreaPacks(map.Id, level, [], new Dictionary<string, PackCatalog.Pack>(), []);
            var field = new AreaLayout(map.Hash16, map.Id, 240, 240, 20, 20, (_, _) => true, []);
            var mapConn = new ClientState(field, 1, 71) { Instance = instance };
            var population = MapValdoUniques.Apply(mapConn, empty, valdoMods);
            var uniques = AreaPopulation.Plan(population, 240, 240, field.Walkable!, (20, 20), [], [], 71, _ => true, []);
            Assert.Equal(5, uniques.Count);
            Assert.Equal(5, uniques.Select(m => (m.X, m.Y)).Distinct().Count());
            Assert.All(uniques, m =>
            {
                Assert.Equal(MonsterRarity.Unique, m.Rarity);
                var unique = Game.Monsters.Find(m.Variety.ToString())!;
                Assert.Equal(unique.Id != "Metadata/Monsters/Masters/EinharSupporter", Game.IsMapBoss(mapConn, unique, m.Rarity));
            });
            Assert.Equal(new[] { "Einhar, Exilehunter", "Elida Blisterclaw", "Melur Thornmaul", "Melur Thornmaul", "Orvi Acidbeak" },
                uniques.Select(m => Game.Monsters.Find(m.Variety.ToString())!.Name).Order(StringComparer.Ordinal));
            Assert.Same(empty, MapValdoUniques.Apply(mapConn, empty, MapMods.None));

            var visitor = Game.Monsters.Find(Game.Packs.BossesFor("MapWorldsCourtyard")[0].ToString())!;
            var other = conn.Monsters[await Game.SpawnMonster(remote, stream, ciphers, conn, visitor, level, MonsterRarity.Unique, 40, 40, bulk: true)];
            Assert.Equal(40, other.Mods.Effects["maximum_life_+%"]);
            Assert.Equal(0, other.Mods.Effects["damage_+%"]);
            Assert.Equal(13, other.Mods.Effects[MonsterModCatalog.MapHiddenLifeStat]);
            Assert.Equal((uint)other.Mods.Effects.MaxLife(Game.Monsters.BaseLife(visitor, level)!.Value), other.MaxLife);
            Assert.Contains(5698, Game.Packs.BossesFor("2_7_5_map")); // Black Death in Act 7's quest map.
            Assert.Empty(Game.Packs.BossesFor("2_7_5_map", mapsOnly: true));
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            PlayerInventories.Forget(conn.Character!);
        }
    }
}
