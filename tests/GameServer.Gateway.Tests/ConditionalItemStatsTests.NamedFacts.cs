using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Qualified_named_facts_reach_life_and_ignite_consumers(ClientState conn, Func<string, byte, InventoryItem> wear)
    {
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        const string burning = "always_ignite_while_burning";
        const string noLife = "maximum_life_+_if_no_life_modifiers_on_equipment_except_body_armour";
        var unknown = new StatConditions.Subject(Value: _ => 0);
        Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of(burning, unknown));
        Assert.Empty(StatConditions.FoldAll(burning, Program.Stats.Has));
        foreach (bool enabled in new[] { false, true })
        {
            var flagsOnly = unknown with { Flags = new Dictionary<string, bool> { [Game.BurningItemCondition] = enabled } };
            Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of(burning, flagsOnly));
            Assert.Empty(StatConditions.FoldAll(burning, Program.Stats.Has, flagsOnly));
            var subject = flagsOnly with { NamedConditions = Game.NamedItemConditionSuffixes };
            Assert.Equal(enabled ? StatConditions.State.Holds : StatConditions.State.Fails, StatConditions.Of(burning, subject));
            Assert.Equal(["always_ignite"], StatConditions.FoldAll(burning, Program.Stats.Has, subject));
        }
        var inventory = Game.ItemsOf(conn); var saved = inventory.EquipmentSnapshot();
        using var wire = new Wire();
        Dictionary<uint, int> stats = [];
        int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        void Read() => stats = Game.CharacterPlayerStats(conn);
        void Rebuild() { conn.Pools = Game.PoolsFor(conn, 100); conn.PlayerLife = conn.PlayerMaxLife; conn.PlayerMana = conn.Pools.Mana; Read(); }
        try
        {
            Assert.Null(conn.RighteousFire); conn.ElementalAilments.Clear();
            foreach (var (row, item) in saved) inventory.Remove(row, item.Id);
            wear("Astramentis", 5); wear("Le Heup of All", 6); wear("Le Heup of All", 7);
            var shield = wear("Glitterdisc", PlayerInventories.Offhand1);
            Item Gem(string name) => ItemCatalog.Create(Program.Items.FindGem(name)!.Value.Type);
            inventory.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
            { Sockets = [new(SocketColour.White, Gem("Fireball")), new(SocketColour.White, Gem("Righteous Fire"))], Links = [1, 1] });
            Rebuild(); Assert.False(Game.IsInactive(conn, shield.Id)); Assert.Equal(0, Get("always_ignite"));
            ushort fireball = Game.GemSkillEffects(conn).Single(g => Game.SkillsFor(conn).EffectId(g.Effect) == "Fireball").Id;
            MonsterIgnites.Settings Ignite()
            {
                var merged = Game.SupportedStats(conn, fireball);
                return MonsterIgnites.Settings.From(Game.SupportedAilmentRows(conn, fireball, "spell"), "spell",
                    n => Program.Stats.Key(n) is uint k ? merged.GetValueOrDefault(k) : 0);
            }
            Assert.Equal(25, Ignite().Chance);
            double now = conn.Clock.Elapsed.TotalSeconds;
            conn.ElementalAilments.Inflict(PlayerElementalAilments.Kind.Ignite, 10, 4, now, Get, new PhasingRoll(99));
            Read(); Assert.Equal(1, Get("always_ignite")); Assert.Equal(100, Ignite().Chance);
            conn.ElementalAilments.Clear();
            conn.ElementalAilments.Inflict(PlayerElementalAilments.Kind.Ignite, 10, 4, now - 5, Get, new PhasingRoll(99));
            Read(); Assert.Equal(0, Get("always_ignite")); Assert.Equal(25, Ignite().Chance);
            conn.ElementalAilments.Clear();
            var rf = Game.GemSkillEffects(conn).Single(g => Game.SkillsFor(conn).EffectId(g.Effect) == "RighteousFire");
            Game.ActivateRighteousFire("named-facts", wire.Stream, wire.Ciphers, conn, rf.Id, rf.Effect, rf.Level, rf.Quality, rf.Gem).GetAwaiter().GetResult();
            Assert.NotNull(conn.RighteousFire); Read(); Assert.Equal(1, Get("always_ignite")); Assert.Equal(100, Ignite().Chance);
            inventory.Remove(PlayerInventories.Offhand1, shield.Id); Read(); Assert.Equal(0, Get("always_ignite"));
            var restored = inventory.Add(PlayerInventories.Offhand1, 0, 0, shield.Item); Read(); Assert.Equal(1, Get("always_ignite"));
            Game.EndRighteousFire("named-facts", wire.Stream, wire.Ciphers, conn, "test owner end").GetAwaiter().GetResult();
            Read(); Assert.Equal(0, Get("always_ignite")); Assert.Equal(25, Ignite().Chance);
            conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
            Assert.True(Game.IsInactive(conn, restored.Id)); Read(); Assert.Equal(0, Get("always_ignite"));
            conn.Experience = CharacterStats.ExperienceFor(100);
            foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);

            wear("Astramentis", 5); wear("Le Heup of All", 6); wear("Le Heup of All", 7);
            // Current catalogue internal name; the official/PoB name is Utula's Hunger. The stat id owns this rule.
            var strength = Program.Items.Mods.All.First(m => m.Domain == 1 && m.GenerationType == 2
                && !m.Tags.Contains("life") && m.Stats.Count == 1 && m.Stats[0].Id == "additional_strength"
                && m.Stats[0].Min >= 38 && m.Level <= 70);
            var belt = ItemCatalog.Create(Program.Items.Find("Heavy Belt")!);
            inventory.Add(PlayerInventories.Belt, 0, 0, belt with
                { Mods = [.. belt.Mods, new(ModSection.Explicit, strength.Hash, [strength.Stats[0].Min])] });
            var body = wear("Kitava's Hunger", PlayerInventories.BodyArmour); Rebuild(); Assert.False(Game.IsInactive(conn, body.Id));
            int coefficient = ItemModStats.Sum([body.Item], Program.Items.Mods).GetValueOrDefault(noLife); Assert.InRange(coefficient, 700, 1000);
            uint fullLife = conn.Pools.Life; int fullFlat = Get("base_maximum_life");
            var control = body.Item with { Mods = [.. body.Item.Mods.Where(m => Program.Items.Mods.ByHash(m.Hash)?.Stats.All(s => s.Id != noLife) != false)] };
            Assert.NotNull(inventory.Change(PlayerInventories.BodyArmour, body.Id, _ => control)); Rebuild();
            int baseline = Get("base_maximum_life"); Assert.Equal(coefficient, fullFlat - baseline); Assert.True(fullLife > conn.Pools.Life);
            Assert.NotNull(inventory.Change(PlayerInventories.BodyArmour, body.Id, _ => body.Item)); Rebuild(); Assert.Equal(fullLife, conn.Pools.Life);
            var ring = inventory.Items(6).Single(); inventory.Remove(6, ring.Id);
            var flat = Program.Items.Mods.All.First(m => m.Domain == 1 && m.GenerationType is 1 or 2 && m.Tags.Contains("life")
                && m.Stats.Count == 1 && m.Stats[0].Id == "base_maximum_life");
            var regen = Program.Items.Mods.All.First(m => m.Domain == 1 && m.GenerationType is 1 or 2 && m.Tags.Contains("life")
                && m.Stats.Count == 1 && m.Stats[0].Id == "base_life_regeneration_rate_per_minute");
            var plain = ItemCatalog.Create(Program.Items.Find("Iron Ring")!);
            foreach (var (row, section, zero) in new[] { (flat, ModSection.Explicit, true), (flat, ModSection.Implicit, false),
                (regen, ModSection.Hidden, false), (flat, ModSection.Enchant, false) })
            {
                var blocker = inventory.Add(6, 0, 0, plain with { Identified = false,
                    Mods = [new(section, row.Hash, [.. row.Stats.Select(s => zero ? 0 : s.Min)])] });
                Rebuild(); Assert.True(Game.IsInactive(conn, body.Id) == false);
                Assert.False(Game.ItemConditionFlags(conn, conn.Clock.Elapsed.TotalSeconds)[Game.NoOtherLifeModifiersCondition]);
                var noBonus = Game.CharacterStatSources(conn).Stats;
                Assert.Equal(0, noBonus.GetValueOrDefault(Key(noLife)));
                Assert.True(conn.Pools.Life < fullLife);
                inventory.Remove(6, blocker.Id); Rebuild();
                Assert.True(Game.ItemConditionFlags(conn, conn.Clock.Elapsed.TotalSeconds)[Game.NoOtherLifeModifiersCondition]);
            }
            var inactive = inventory.Add(6, 0, 0, ItemCatalog.Create(Program.Items.Find("Steel Ring")!) with
            { Mods = [new(ModSection.Explicit, flat.Hash, [flat.Stats[0].Min])] });
            conn.Experience = CharacterStats.ExperienceFor(70); conn.Pools = Game.PoolsFor(conn, 70); Read();
            Assert.True(Game.IsInactive(conn, inactive.Id)); Assert.False(Game.IsInactive(conn, body.Id));
            Assert.True(Game.ItemConditionFlags(conn, conn.Clock.Elapsed.TotalSeconds)[Game.NoOtherLifeModifiersCondition]);
            conn.Experience = CharacterStats.ExperienceFor(100); Rebuild(); Assert.False(Game.IsInactive(conn, inactive.Id));
            Assert.False(Game.ItemConditionFlags(conn, conn.Clock.Elapsed.TotalSeconds)[Game.NoOtherLifeModifiersCondition]);
            inventory.Remove(6, inactive.Id); inventory.Remove(PlayerInventories.BodyArmour, body.Id); Rebuild();
            Assert.Equal(0, Get(noLife)); Assert.Empty(conn.PlayerStats);
        }
        finally
        {
            if (conn.RighteousFire is not null) Game.EndRighteousFire("named-facts", wire.Stream, wire.Ciphers, conn, "test cleanup").GetAwaiter().GetResult();
            conn.ElementalAilments.Clear(); conn.Experience = CharacterStats.ExperienceFor(100);
            foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);
            foreach (var (row, item) in saved) inventory.Add(row, item.X, item.Y, item.Item);
            conn.Pools = Game.PoolsFor(conn, 100); conn.PlayerLife = conn.PlayerMaxLife; conn.PlayerMana = conn.Pools.Mana;
        }
    }
}
