using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>
/// Eldritch "while a Pinnacle Boss is in your Presence" implicits: a mod carrying
/// <c>local_influence_mod_requires_celestial_boss_presence</c> (Stats 15802, IsLocal) only reaches the character
/// while a Pinnacle Boss is in presence; a mod without the condition stat is unchanged. The presence fact arrives
/// on the subject's flags (<see cref="StatConditions.CelestialBossInPresence"/>), published by
/// <c>Game.ItemConditionFlags</c> from <c>Game.CelestialBossInPresenceLocked</c> (PresenceRadius 80; the predicate
/// is the client's <c>PinnacleAtlasBoss</c> marker mod on the monster's variety).
/// </summary>
public class CelestialPresenceTests
{
    [Fact]
    public void Pinnacle_presence_refreshes_equipped_resistance_at_the_radius_boundary()
    {
        var player = new Player(new(1, "presence", 100, 100, 100, 100, null, []), 1, 1)
        {
            Character = { Name = "Presence" + Guid.NewGuid().ToString("N") }, PlayerLife = 100
        };
        player.Walk.Teleport(100, 100, 0);
        var items = Program.Items;
        var mod = items.Mods.All.Single(m => m.Id == "MaximumColdResistanceEldritchImplicitPinnaclePresence1");
        var helmet = ItemCatalog.Create(items.Find("Iron Hat")!) with
        {
            Mods = [new(ModSection.Implicit, mod.Hash, [3, 1])],
        };
        Game.ItemsOf(player).Put(PlayerInventories.Helmet, new(42, 0, 0, helmet));
        player.Pools = Game.PoolsFor(player, 100);
        Game.EnsureConditionalItemPools(player);
        Assert.Equal(75, player.Pools.ColdMaxResistance);

        var boss = new SpawnedMonster("The Shaper", new(180, 100, 0), 60, 6, 100)
        {
            Variety = Game.Monsters.Find("Metadata/Monsters/AtlasBosses/TheShaperBoss"), Life = 100
        };
        player.Area.Monsters[900] = boss;
        Game.EnsureConditionalItemPools(player);
        Assert.Equal(78, player.Pools.ColdMaxResistance);
        boss.Walk.Teleport(181, 100, 0);
        Game.EnsureConditionalItemPools(player);
        Assert.Equal(75, player.Pools.ColdMaxResistance);
    }

    [Fact]
    public void No_scan_when_no_worn_item_carries_the_pinnacle_presence_mod()
    {
        var player = new Player(new(1, "no-pinnacle-mod", 100, 100, 100, 100, null, []), 1, 1)
            { Character = { Name = "NoPinnacleMod" + Guid.NewGuid().ToString("N") } };
        double now = player.Clock.Elapsed.TotalSeconds;
        // No worn item at all: the flag is absent, not false - the monster scan never ran.
        Assert.DoesNotContain(StatConditions.CelestialBossInPresence, Game.ItemConditionFlags(player, now).Keys);
        // A worn item with an ordinary mod, and no pinnacle-presence mod, leaves the flag absent too.
        var items = Program.Items;
        var plain = items.Mods.All.First(m => m.Stats.Count == 1 && m.Stats[0].Id == Stat.BaseMaximumLife);
        var item = ItemCatalog.Create(items.Find("Iron Hat")!) with
        {
            Mods = [new(ModSection.Explicit, plain.Hash, [42])],
        };
        Assert.DoesNotContain(StatConditions.CelestialBossInPresence,
            Game.ItemConditionFlags(player, now, [item]).Keys);
    }
}
