using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.World;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ForbiddenJewelTests
{
    [Fact]
    public void Native_variants_pair_by_node_deduplicate_and_reject_invalid_sources()
    {
        using var mod = new JewelModFixture();
        Assert.False(JewelGrantPolicy.Current.Enabled);
        Assert.Equal(100, new SkillDamageContext(SkillHitKind.Spell, new Dictionary<uint, int>
            { [Program.Stats.Key("berserker_damage_+%_final")!.Value] = 40 })
            .Hit(DamagePacket.Of(DamageType.Physical, 100, 100))[DamageType.Physical].Min);
        Assert.True(mod.Host.SetEnabled("forbiddenfreedom", true));
        mod.Host.Reload("forbiddenfreedom");
        Assert.True(JewelGrantPolicy.Current.Enabled);
        var items = Program.Items;
        Assert.NotEmpty(ForbiddenJewelCatalogue.All);
        var uniques = new UniqueCatalog(items);
        foreach (string name in new[] { ForbiddenJewelCatalogue.Flame, ForbiddenJewelCatalogue.Flesh })
        {
            var definition = Assert.Single(uniques.All, entry => entry.Name == name);
            Assert.True(definition.Corrupted);
            Assert.Equal((byte)85, definition.ItemLevel);
            Assert.NotNull(ForbiddenJewelCatalogue.Read(uniques.Create(name, new Random(1))!, items.Mods));
        }
        foreach (var node in ForbiddenJewelCatalogue.All)
        {
            var flame = ForbiddenJewelCatalogue.Create(items, "flame", node.Id)!;
            var flesh = ForbiddenJewelCatalogue.Create(items, "flesh", node.Id)!;
            Assert.True(flame.Corrupted && flesh.Corrupted);
            Assert.Equal(ItemRarity.Unique, flame.Rarity);
            Assert.Equal((true, node.Id), ForbiddenJewelCatalogue.Read(flame, items.Mods));
            Assert.Equal((false, node.Id), ForbiddenJewelCatalogue.Read(flesh, items.Mods));
            Assert.Empty(ForbiddenJewelCatalogue.Granted([flame, flame], items.Mods));
            Assert.Equal(new[] { node.Id }, ForbiddenJewelCatalogue.Granted([flame, flesh, flame, flesh], items.Mods));
            var saved = JsonSerializer.Deserialize<Item>(JsonSerializer.Serialize(flame))!;
            Assert.Equal(flame.Payload(), saved.Payload());
            Assert.Equal((true, node.Id), ForbiddenJewelCatalogue.Read(saved, items.Mods));
            Assert.Null(ForbiddenJewelCatalogue.Read(flame with { Rarity = ItemRarity.Rare }, items.Mods));
            Assert.Null(ForbiddenJewelCatalogue.Read(flame with { Identified = false }, items.Mods));
            Assert.Null(ForbiddenJewelCatalogue.Read(flame with { TypeHash = flesh.TypeHash }, items.Mods));
            Assert.Null(ForbiddenJewelCatalogue.Read(flame with { Mods = [flame.Mods[0] with { Values = [65536] }] }, items.Mods));
            Assert.Null(ForbiddenJewelCatalogue.Read(flame with { Mods = [flame.Mods[0], flame.Mods[0]] }, items.Mods));
            // Upstream retains native identity stats in the raw view; node activation still uses the paired catalogue.
            var raw = ItemModStats.Global([flame, flesh], items.Mods, Program.Stats);
            Assert.Equal((int)node.Id, raw[Program.Stats.Key("unique_jewel_grants_notable_hash_part_1")!.Value]);
            Assert.Equal((int)node.Id, raw[Program.Stats.Key("unique_jewel_grants_notable_hash_part_2")!.Value]);
        }
        var first = ForbiddenJewelCatalogue.All[0]; var second = ForbiddenJewelCatalogue.All[1];
        var a = ForbiddenJewelCatalogue.Create(items, "flame", first.Id)!;
        var b = ForbiddenJewelCatalogue.Create(items, "flesh", second.Id)!;
        Assert.Empty(ForbiddenJewelCatalogue.Granted([a, b], items.Mods));
        Assert.Null(ForbiddenJewelCatalogue.Create(items, "flame", 0));
        Assert.Null(ForbiddenJewelCatalogue.Create(items, "invalid", first.Id));
        Assert.NotNull(ForbiddenJewelCatalogue.Find("Nine Lives"));
        Assert.NotNull(ForbiddenJewelCatalogue.Find("Harness the Void"));
        Assert.True(mod.Host.SetEnabled("forbiddenfreedom", false));
        Assert.False(JewelGrantPolicy.Current.Enabled);
        Assert.Equal(100, new SkillDamageContext(SkillHitKind.Spell, new Dictionary<uint, int>
            { [Program.Stats.Key("berserker_damage_+%_final")!.Value] = 40 })
            .Hit(DamagePacket.Of(DamageType.Physical, 100, 100))[DamageType.Physical].Min);
        Assert.Null(uniques.Find(ForbiddenJewelCatalogue.Flame));
    }

    [Fact]
    public void Production_sources_cross_classes_stack_distinct_pairs_and_follow_socket_lifecycle()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-forbidden-{Guid.NewGuid():N}.db");
        string character = "Forbidden" + Guid.NewGuid().ToString("N");
        var previous = Program.GameLogicOrNull;
        Saves.Open(path);
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        using var policy = JewelGrantPolicy.Use(new(true, true, 0));
        var items = Program.Items;
        var conn = new ClientState(new(1, "forbidden-test", 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = character, PlayerLife = 1000, Instance = new(),
            Pools = new(100, 1000, 1000, int.MaxValue, 0),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        try
        {
            CharacterSaves.SetInventories(character, [], null);
            var inventory = Game.ItemsOf(conn);
            var nodes = new[] { "Unstoppable", "Aspect of Carnage", "Augury of Penitence", "Unwavering Crusade" }
                .Select(n => ForbiddenJewelCatalogue.Find(n)!.Id).ToArray();
            var sockets = Enumerable.Range(0, 21).Where(i => Program.Passives.JewelNodeForSlot(i) is not null).Take(10).Select(i => (byte)i).ToArray();
            conn.Passives = sockets.Select(i => Program.Passives.JewelNodeForSlot(i)!.Value).ToList();
            var original = conn.Passives.ToArray();
            uint speed = Program.Stats.Key("base_movement_velocity_+%")!.Value;
            int baseline = Game.CharacterStatSources(conn).Stats.GetValueOrDefault(speed);
            var halves = new List<InventoryItem>();
            for (int i = 0; i < nodes.Length; i++)
            {
                halves.Add(inventory.Add(PlayerInventories.PassiveJewels, sockets[2 * i], 0,
                    ForbiddenJewelCatalogue.Create(items, "flame", nodes[i])!));
                Assert.DoesNotContain(nodes[i], Game.ForbiddenPassiveNodes(conn));
                halves.Add(inventory.Add(PlayerInventories.PassiveJewels, sockets[2 * i + 1], 0,
                    ForbiddenJewelCatalogue.Create(items, "flesh", nodes[i])!));
            }
            Assert.Equal(nodes.Order(), Game.ForbiddenPassiveNodes(conn));
            Assert.Equal(baseline + 10, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(speed));
            Assert.Equal(40, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(Program.Stats.Key("berserker_damage_+%_final")!.Value));
            var hit = DamagePacket.Of(DamageType.Physical, 100, 100);
            foreach (var kind in new[] { SkillHitKind.Attack, SkillHitKind.Spell })
                Assert.Equal(140, new SkillDamageContext(kind, Game.CharacterStatSources(conn).Stats).Hit(hit)[DamageType.Physical].Min);
            Assert.Equal(100, new SkillDamageContext(SkillHitKind.Spell, new Dictionary<uint, int>()).Hit(hit)[DamageType.Physical].Min);
            Assert.Equal(140, Game.AttackHit(new SkillCatalog.Unarmed(833, 100, 100, 8),
                new Dictionary<uint, int> { [Program.Stats.Key("berserker_damage_+%_final")!.Value] = 40 }, true)[DamageType.Physical].Min);
            Assert.Equal(140, new SkillDamageContext(SkillHitKind.Spell, Game.CharacterStatSources(conn).Stats)
                .OverTime(new DamageOverTime(DamageType.Chaos, 100, 1)).DamagePerSecond);
            Assert.NotEmpty(PassiveBuffAuras.For([nodes[2]]));
            Assert.All(PassiveBuffAuras.For([nodes[2]]), aura => Assert.Contains(aura, PassiveBuffAuras.For(Game.PassiveEffectNodes(conn))));
            Assert.Contains(new PassiveGrantedSkills(Program.Skills).For(Game.PassiveEffectNodes(conn)), g => g.Node == nodes[3]);
            inventory.Add(PlayerInventories.PassiveJewels, sockets[8], 0, ForbiddenJewelCatalogue.Create(items, "flame", nodes[0])!);
            var extra = inventory.Add(PlayerInventories.PassiveJewels, sockets[9], 0, ForbiddenJewelCatalogue.Create(items, "flesh", nodes[0])!);
            Assert.Equal(baseline + 10, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(speed));
            inventory.Remove(PlayerInventories.PassiveJewels, halves[1].Id);
            Assert.Contains(nodes[0], Game.ForbiddenPassiveNodes(conn)); // another complete pair still owns it
            inventory.Remove(PlayerInventories.PassiveJewels, extra.Id);
            Assert.DoesNotContain(nodes[0], Game.ForbiddenPassiveNodes(conn));
            Assert.Equal(baseline, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(speed));
            inventory.Add(PlayerInventories.MainInventory, 0, 0, extra.Item);
            Assert.DoesNotContain(nodes[0], Game.ForbiddenPassiveNodes(conn)); // backpack is inactive
            PlayerInventories.Forget(character);
            Assert.Equal(nodes.Skip(1).Order(), Game.ForbiddenPassiveNodes(conn)); // inventory persistence, no passive save
            conn.Passives.Remove(Program.Passives.JewelNodeForSlot(sockets[3])!.Value);
            Assert.DoesNotContain(nodes[1], Game.ForbiddenPassiveNodes(conn));
            Assert.Equal(0, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(Program.Stats.Key("berserker_damage_+%_final")!.Value));
            Assert.Equal(100, new SkillDamageContext(SkillHitKind.Attack, Game.CharacterStatSources(conn).Stats).Hit(hit)[DamageType.Physical].Min);
            conn.Passives = original.ToList();
            Assert.Equal(nodes.Skip(1).Order(), Game.ForbiddenPassiveNodes(conn));
            Assert.Empty(conn.MainAscendancy.Allocated);
            Assert.Equal(0, conn.MainAscendancy.EarnedPoints);
            Assert.Equal(original, conn.Passives);

            // A legitimate paid main-ascendancy node and its jewel grant are the same effect, with independent ownership.
            var ascendancies = new AscendancyCatalog();
            var occultist = ascendancies.Find(new(AscendancyFamily.Normal, "Occultist", AscendancyCatalog.Revision))!;
            ushort bloom = ForbiddenJewelCatalogue.Find("Profane Bloom")!.Id;
            var parents = new Dictionary<ushort, ushort>(); var visited = new HashSet<ushort> { occultist.StartNode };
            var queue = new Queue<ushort>(); queue.Enqueue(occultist.StartNode);
            while (queue.TryDequeue(out var at))
                foreach (ushort next in occultist.Nodes[at].Adjacent.Where(occultist.Nodes.ContainsKey))
                    if (visited.Add(next)) { parents[next] = at; queue.Enqueue(next); }
            var paid = new List<ushort>();
            for (ushort at = bloom; at != occultist.StartNode; at = parents[at]) paid.Add(at);
            conn.MainAscendancy = new(occultist.Identity, paid, [.. AscendancyRules.AwardKeys]);
            var rules = new AscendancyRules(ascendancies);
            rules.Validate(3, conn.MainAscendancy);
            int spent = rules.SpentPoints(3, conn.MainAscendancy);
            inventory = Game.ItemsOf(conn);
            var fire = inventory.Add(PlayerInventories.PassiveJewels, sockets[1], 0, ForbiddenJewelCatalogue.Create(items, "flame", bloom)!);
            var flesh = inventory.Add(PlayerInventories.PassiveJewels, sockets[9], 0, ForbiddenJewelCatalogue.Create(items, "flesh", bloom)!);
            uint bloomStat = Program.Stats.Key("explode_cursed_enemies_for_25%_life_as_chaos_on_kill_chance_%")!.Value;
            Assert.Equal(1, Game.PassiveEffectNodes(conn).Count(n => n == bloom));
            Assert.Equal(50, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(bloomStat));
            inventory.Remove(PlayerInventories.PassiveJewels, fire.Id);
            inventory.Remove(PlayerInventories.PassiveJewels, flesh.Id);
            Assert.Equal(50, Game.CharacterStatSources(conn).Stats.GetValueOrDefault(bloomStat));
            Assert.Equal(spent, rules.SpentPoints(3, conn.MainAscendancy));
        }
        finally
        {
            PlayerInventories.Forget(character);
            Program.SetGameLogic(previous!);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    sealed class JewelModFixture : IDisposable
    {
        readonly string _folder = Path.Combine(Path.GetTempPath(), "localexile-forbiddenfreedom-" + Guid.NewGuid().ToString("N"));
        internal ModHost Host { get; }
        internal JewelModFixture()
        {
            string mod = Path.Combine(_folder, "forbiddenfreedom");
            Directory.CreateDirectory(mod);
            File.WriteAllText(Path.Combine(mod, "mod.json"), """{"id":"forbiddenfreedom","enabled":false}""");
            File.WriteAllText(Path.Combine(mod, "main.cs"), """
                public class TestMod : IMod
                {
                    public void Load(IModContext mod) => JewelGrantPolicy.Use(mod.Manifest.Id, new(true, true, 0));
                }
                """);
            Host = new ModHost(_folder, _ => { });
            Host.Start(watch: false);
        }
        public void Dispose()
        {
            Host.Dispose();
            Directory.Delete(_folder, recursive: true);
        }
    }

}
