using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Qualified_item_ignore_resistance_reaches_hit_mitigation(ClientState conn)
    {
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        var inventory = Game.ItemsOf(conn);
        var saved = inventory.EquipmentSnapshot().ToArray();
        foreach (var (row, item) in saved) inventory.Remove(row, item.Id);
        var unique = new UniqueCatalog(Program.Items);
        var amulet = unique.Create("Astramentis", new Random(504))!;
        var attributeRing = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Rings/Ring1")!) with
        {
            Rarity = ItemRarity.Rare,
            Mods = [.. new[] { "Strength10", "Dexterity10", "Intelligence10" }
                .Select(id => Program.Items.Mods.ById(id)!.Roll(ModSection.Explicit, new Random(504)))],
        };
        var ring = ItemCatalog.Create(Program.Items.Find("Steel Ring")!);
        var target = new SpawnedMonster("typed resistance", new(0, 0, 8), 10, 2, 10000)
        {
            Mods = new([], [], new(new Dictionary<string, int>
            {
                ["base_fire_damage_resistance_%"] = 50,
                ["base_chaos_damage_resistance_%"] = 50,
            })),
        };
        int Hit(HitResistances.Mods mods, DamageType type, bool attack = false) =>
            Game.MonsterHitDamage(target, DamagePacket.Of(type, 100, 100), false, attack,
                Game.ResourceEffectsTime(conn), mods).Damage;
        Assert.Equal(50, Hit(default, DamageType.Chaos));
        foreach (var (name, influence) in new[]
        {
            ("The Eternity Shroud", ItemInfluence.Shaper), ("Replica Eternity Shroud", ItemInfluence.Elder),
        })
        {
            string input = $"hits_ignore_enemy_chaos_resistance_if_all_{influence.ToString().ToLowerInvariant()}_items_equipped";
            var source = unique.Create(name, new Random(504))!;
            Assert.Equal(1, ItemModStats.Sum([source], Program.Items.Mods).GetValueOrDefault(input));
            Assert.True(source.Influences.HasFlag(influence));
            var neck = inventory.Add(5, 0, 0, amulet with { Influences = influence });
            var support = inventory.Add(7, 0, 0, attributeRing with { Influences = influence });
            var body = inventory.Add(PlayerInventories.BodyArmour, 0, 0, source);
            conn.Pools = Game.PoolsFor(conn, 100);
            var stats = Game.CharacterPlayerStats(conn);
            Assert.False(Game.IsInactive(conn, body.Id));
            Assert.Equal(1, stats.GetValueOrDefault(Key("virtual_hits_ignore_enemy_chaos_resistance")));
            var captured = Game.HitResistMods(stats);
            foreach (bool attack in new[] { false, true }) Assert.Equal(100, Hit(captured, DamageType.Chaos, attack));
            Assert.Equal(50, Hit(captured, DamageType.Fire));
            var counterpart = inventory.Add(6, 0, 0, ring);
            Assert.False(Game.HitResistMods(Game.CharacterPlayerStats(conn)).IgnoreChaos);
            Assert.True(captured.IgnoreChaos); // A released hit retains the qualified source snapshot.
            conn.Experience = CharacterStats.ExperienceFor(70); conn.Pools = Game.PoolsFor(conn, 70);
            Assert.True(Game.IsInactive(conn, counterpart.Id));
            Assert.False(Game.IsInactive(conn, body.Id));
            Assert.True(Game.HitResistMods(Game.CharacterPlayerStats(conn)).IgnoreChaos);
            conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
            Assert.False(Game.HitResistMods(Game.CharacterPlayerStats(conn)).IgnoreChaos);
            inventory.Remove(6, counterpart.Id);
            counterpart = inventory.Add(6, 0, 0, ring with { Influences = ItemInfluence.Shaper | ItemInfluence.Elder });
            Assert.True(Game.HitResistMods(Game.CharacterPlayerStats(conn)).IgnoreChaos);
            inventory.Remove(PlayerInventories.BodyArmour, body.Id);
            Assert.Equal(50, Hit(Game.HitResistMods(Game.CharacterPlayerStats(conn)), DamageType.Chaos));
            inventory.Remove(6, counterpart.Id); inventory.Remove(5, neck.Id); inventory.Remove(7, support.Id);
            Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of(input, StatConditions.Subject.Unknown));
        }
        var attributes = inventory.Add(5, 0, 0, amulet);
        var weapon = inventory.Add(PlayerInventories.Weapon1, 0, 0, unique.Create("Dreadsurge", new Random(504))!);
        conn.Pools = Game.PoolsFor(conn, 100);
        Assert.False(Game.IsInactive(conn, weapon.Id));
        conn.ElementalAilments.Clear();
        Assert.Equal(50, Hit(Game.HitResistMods(Game.CharacterPlayerStats(conn)), DamageType.Fire));
        double at = Game.ResourceEffectsTime(conn);
        conn.ElementalAilments.Inflict(PlayerElementalAilments.Kind.Ignite, 10, 4, at, _ => 0, new PhasingRoll(99));
        var ignited = Game.CharacterPlayerStats(conn);
        var fire = Game.HitResistMods(ignited);
        Assert.True(fire.IgnoreFire); Assert.False(fire.IgnoreChaos);
        Assert.Equal(100, Hit(fire, DamageType.Fire));
        Assert.Equal(50, Hit(fire, DamageType.Chaos));
        foreach (int resistance in new[] { -50, 50, 150 })
        {
            var resist = new DamageModel.ResistanceValue(resistance, Penetration: 20);
            foreach (bool critical in new[] { false, true })
            {
                var strike = new HitResistances.Strike(critical, false);
                Assert.Equal(0, HitResistances.FinalValue(DamageType.Fire, resist, strike, fire));
                Assert.Equal(0, HitResistances.FinalValue(DamageType.Chaos, resist, strike, new(0, 0, 0, 1, IgnoreChaos: true)));
                Assert.Equal(DamageModel.Capped(resistance, 75), HitResistances.FinalValue(DamageType.Fire, resist, strike,
                    fire with { CannotPenetrateOrIgnore = 1 }));
                Assert.Equal(DamageModel.Capped(resistance, 75) - 20, HitResistances.FinalValue(DamageType.Fire, resist,
                    strike with { OverTime = true }, fire));
            }
        }
        Assert.True(Game.HitResistMods(new Dictionary<uint, int>(), ignited).IgnoreFire);
        Assert.False(conn.ElementalAilments.Snapshot(at + 4).Ignite);
        conn.ElementalAilments.Advance(at + 3, at + 4, _ => 0, 0, 75, fireImmune: false);
        Assert.False(Game.HitResistMods(Game.CharacterPlayerStats(conn)).IgnoreFire);
        conn.ElementalAilments.Inflict(PlayerElementalAilments.Kind.Ignite, 10, 4, at, _ => 0, new PhasingRoll(99));
        Assert.True(Game.HitResistMods(Game.CharacterPlayerStats(conn)).IgnoreFire);
        inventory.Remove(PlayerInventories.Weapon1, weapon.Id);
        Assert.False(Game.HitResistMods(Game.CharacterPlayerStats(conn)).IgnoreFire);
        conn.ElementalAilments.Clear(); inventory.Remove(5, attributes.Id);
        foreach (var (row, item) in saved) inventory.Add(row, item.X, item.Y, item.Item);
        conn.Pools = Game.PoolsFor(conn, 100);
    }
}
