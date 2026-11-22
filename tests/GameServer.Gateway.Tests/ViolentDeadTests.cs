using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class ViolentDeadTests
{
    [Fact]
    public void Violent_dead_threshold_only_changes_the_zombie_slam()
    {
        using var f = new MinionUtilityTests.Fixture();
        var conn = f.Conn;
        var catalog = Game.ItemsFor(conn);
        var jewel = Assert.IsType<Item>(Game.UniquesFor(conn).Create("Violent Dead", new Random(7)));
        var (radius, effects) = ThresholdJewels.Read(jewel, catalog.Mods);
        Assert.Equal(1200, radius);
        Assert.Equal(2, effects.Count);
        Assert.All(effects, effect => Assert.Equal(ThresholdJewels.Attributes.Intelligence, effect.Attributes));
        int required = effects.Max(effect => effect.Required);

        var tree = Game.PassivesFor(conn);
        uint intelligence = Program.Stats.Key("base_intelligence")!.Value;
        int IntelligenceAround(ushort node) => tree.NodesInRadius(node, radius).Select(tree.Find)
            .Where(n => n is { IsAscendancy: false, IsStart: false, IsJewelSocket: false, IsMastery: false,
                IsProxy: false, IsExpansion: false })
            .Sum(n => n!.Stats.Where(s => s.Key == intelligence).Sum(s => s.Value));
        int slot = Enumerable.Range(0, 57).First(i => tree.JewelNodeForSlot(i) is ushort node && IntelligenceAround(node) >= required);
        ushort socket = tree.JewelNodeForSlot(slot)!.Value;
        var definition = Game.SummonData.Find("RaiseZombie")!;
        var variety = Game.Monsters.Find(definition.Variety)!;
        var actor = new GolemState.Golem(1, null!, new GolemCatalog.Definition(Program.Skills.EffectRow("RaiseZombie"),
            "RaiseZombie", definition.Family, definition.Variety, 0, [], false, false, false), 1);

        var items = Game.ItemsOf(conn);
        var slotItem = items.Add(PlayerInventories.PassiveJewels, checked((byte)slot), 0, jewel);
        var before = Game.GolemAbilities(conn, variety, 70, actor, Game.CharacterPlayerStats(conn));
        conn.Passives = [socket];
        var active = Game.CharacterPlayerStats(conn);
        foreach (var effect in effects) Assert.Equal(effect.Value, active[effect.Key]);
        var after = Game.GolemAbilities(conn, variety, 70, actor, active);
        var slamBefore = before.Single(a => a.Effect == 5590);
        var slamAfter = after.Single(a => a.Effect == 5590);
        Assert.True(slamBefore.Cooldown > 0);
        Assert.Equal(slamBefore.Cooldown / 2, slamAfter.Cooldown, 8);
        Assert.True(slamAfter.Attack.Damage.MaxTotal > slamBefore.Attack.Damage.MaxTotal);

        // Retain the item's unconditional minion damage and remove only its threshold mod.
        items.Change(PlayerInventories.PassiveJewels, slotItem.Id,
            item => item with { Mods = item.Mods.Where(m => catalog.Mods.ByHash(m.Hash)?.Id != "RaiseZombieThresholdJewel1").ToArray() });
        var ordinary = Game.GolemAbilities(conn, variety, 70, actor, Game.CharacterPlayerStats(conn));
        Assert.Equal(slamBefore.Cooldown, ordinary.Single(a => a.Effect == 5590).Cooldown, 8);
        var bareSlam = ordinary.Single(a => a.Effect == 5590);
        Assert.Equal(slamBefore.Attack.Damage.MaxTotal * 0.30,
            slamAfter.Attack.Damage.MaxTotal - bareSlam.Attack.Damage.MaxTotal, 8);
        foreach (var attack in ordinary.Where(a => a.Effect != 5590))
            Assert.Equal(attack.Attack.Damage.MaxTotal, after.Single(a => a.Effect == attack.Effect).Attack.Damage.MaxTotal, 8);
    }
}
