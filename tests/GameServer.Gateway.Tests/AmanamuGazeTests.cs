using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class AmanamuGazeTests
{
    [Fact]
    public void Amanamu_scales_minion_poison_from_active_ghastly_jewels_without_boosting_the_hit()
    {
        using var f = new MinionUtilityTests.Fixture();
        var conn = f.Conn;
        var tree = Game.PassivesFor(conn);
        var slots = Enumerable.Range(0, 57).Where(i => tree.JewelNodeForSlot(i) is not null).Take(7).ToArray();
        conn.Passives = slots.Select(i => tree.JewelNodeForSlot(i)!.Value).ToList();

        var def = Game.SummonData.Find("SummonSkeletons")!;
        var native = new GolemCatalog.Definition(Program.Skills.EffectRow("SummonSkeletons"), def.Id,
            def.Family, def.Variety, 0, [], false, false, false);
        var actor = new GolemState.Golem(1, null!, native, 1);
        actor.Gear["global_minimum_added_chaos_damage"] = actor.Gear["global_maximum_added_chaos_damage"] = 100;
        actor.Gear["base_deal_no_physical_damage"] = 1;
        actor.Own[Program.Stats.Key("base_chance_to_poison_on_hit_%")!.Value] = 100;
        var catalogue = Game.ItemsFor(conn);
        var variety = Game.Monsters.Find(def.Variety)!;
        var items = Game.ItemsOf(conn);
        var applyAilment = typeof(Game).GetMethod("MinionAilmentsLocked", BindingFlags.Static | BindingFlags.NonPublic)!;
        (double Hit, double Poison) Result()
        {
            var stats = Game.CharacterPlayerStats(conn);
            var attacks = Game.GolemAbilities(conn, variety, 70, actor, stats);
            var attack = attacks.First(a => Program.Skills.IsMelee(a.Effect) && a.Attack.Damage.MaxTotal > 0);
            var enemy = new SpawnedMonster("target", new(105, 100, 0), 100, 3, 100000) { Hostile = true };
            applyAilment.Invoke(null, [conn, actor, attack, 998u, enemy, false, 1d]);
            return (attack.Attack.Damage.MaxTotal, enemy.Poisons.TotalDps(1));
        }

        var original = Result();
        Assert.True(original.Poison > 0);
        var gaze = Assert.IsType<Item>(Game.UniquesFor(conn).Create("Amanamu's Gaze", new Random(7)));
        var placed = items.Add(PlayerInventories.PassiveJewels, (byte)slots[0], 0, gaze);
        Item Eye(string kind) => ItemCatalog.Create(catalogue.Find("Metadata/Items/Jewels/JewelAbyss" + kind)!);
        items.Add(PlayerInventories.PassiveJewels, (byte)slots[1], 0, Eye("Melee"));
        var one = Result();
        Assert.Equal(original.Hit, one.Hit);
        Assert.Equal(original.Poison * 1.06, one.Poison, 6);

        foreach (int slot in slots.Skip(2))
            items.Add(PlayerInventories.PassiveJewels, (byte)slot, 0, Eye("Summoner"));
        var cap = Result();
        Assert.Equal(original.Hit, cap.Hit);
        Assert.Equal(original.Poison * 1.30, cap.Poison, 6);

        items.Remove(PlayerInventories.PassiveJewels, placed.Id);
        Assert.Equal(original, Result());
    }
}
