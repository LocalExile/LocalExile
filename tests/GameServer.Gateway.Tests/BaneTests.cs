using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class BaneTests
{
    [Fact]
    public void Bane_applies_only_linked_hexes_and_keeps_its_damage_separate_from_contagion()
    {
        using var fixture = new AnimatedAdmissionTests.Fixture();
        var player = fixture.Player;
        var items = Game.ItemsOf(player);
        items.Remove(PlayerInventories.Weapon1, Assert.Single(items.Items(PlayerInventories.Weapon1)).Id);
        var gems = new[] { "Bane", "Despair", "Assassin's Mark", "Temporal Chains" }.Select(name =>
            new ItemSocket(SocketColour.White, ItemCatalog.Create(Program.Items.Find(name)!))).ToArray();
        items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Gnarled Branch")!, gems, [3, 1]));
        var effects = Game.GemSkillEffects(player).ToDictionary(g => g.Name);
        var skill = effects["Bane"];
        var despair = effects["Despair"];
        Assert.Equal("this curse is triggered by Bane", Game.GemSkillInstanceDisabled(player, despair.Effect, despair.Id));
        var unlinked = effects["Temporal Chains"];
        Assert.Null(Game.GemSkillInstanceDisabled(player, unlinked.Effect, unlinked.Id));
        var supported = Game.SupportedSkill(player, skill.Id);
        var uncursed = new SpawnedMonster("hexproof", new(104, 100, 0), 100, 8, 10000)
        {
            Hostile = true,
            Mods = new([], [], new(new Dictionary<uint, int> { [Stat.Hexproof] = 1 }))
        };
        var target = new SpawnedMonster("near", new(108, 100, 0), 100, 8, 10000) { Hostile = true };
        var far = new SpawnedMonster("far", new(180, 100, 0), 100, 8, 10000) { Hostile = true };
        player.Area.Monsters[501] = target;
        player.Area.Monsters[502] = uncursed;
        player.Area.Monsters[503] = far;
        target.Contagion.Apply(10, new(100, 3, 12, Caster: player));
        var before = target.Contagion.Active;

        Game.BaneContactLocked(player, skill.Id, supported, (100, 100), 1, 10);

        Assert.Same(before, target.Contagion.Active);
        var curse = Assert.Single(target.Curses.All.Values);
        Assert.Equal("Despair", curse.Effect);
        int unscaled = Game.SupportedSkill(player, despair.Id).Stats[Stat.BaseChaosDamageResistancePct];
        Assert.Equal((int)Math.Truncate(unscaled * .75), curse.Stats[Stat.BaseChaosDamageResistancePct]);
        Assert.Empty(uncursed.Curses.All);
        Assert.Null(far.Bane);
        var withCurse = Assert.IsType<MonsterContagion.Cast>(target.Bane?.Active);
        var withoutCurse = Assert.IsType<MonsterContagion.Cast>(uncursed.Bane?.Active);
        Assert.Equal(1.28 * withoutCurse.DamagePerSecond, withCurse.DamagePerSecond, 6);
        Assert.Equal(2 * withoutCurse.Seconds, withCurse.Seconds, 6);
        Assert.Equal(0, withCurse.SpreadMore);

        // Bane's quality raises the effect of the curses it applies (its support half's curse_effect_+%).
        var branch = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Change(PlayerInventories.Weapon1, branch.Id, item => item with
        {
            Sockets = [item.Sockets[0] with { Gem = item.Sockets[0].Gem! with { Quality = 20 } }, .. item.Sockets.Skip(1)]
        });
        Game.BaneContactLocked(player, skill.Id, Game.SupportedSkill(player, skill.Id), (100, 100), 1, 11);
        Assert.Equal((int)Math.Truncate(unscaled * 1.2 * .75), target.Curses.All.Values.Single().Stats[Stat.BaseChaosDamageResistancePct]);
    }
}
