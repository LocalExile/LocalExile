namespace GameServer.Gateway.Tests;

public class IgniteProliferationTests
{
    static Func<string, int> With(params (string Stat, int Value)[] stats) =>
        name => stats.Where(s => s.Stat == name).Sum(s => s.Value);

    static SpawnedMonster At(int x, params (string, int)[] stats) => new($"at {x}", new(x, 100, 1), 0, 0, 100)
        { Mods = new([], [], new(stats.ToDictionary(s => s.Item1, s => s.Item2))) };

    // The radius is the largest source (the main hand's only for attacks in it). A spreading ignite burns every living,
    // ignitable monster in its radius at its own damage per second; the copy does not spread and is not a new ignite.
    [Fact]
    public void Spreading_ignite_burns_monsters_in_its_radius()
    {
        var get = With(("support_ignite_proliferation_radius", 14), ("gloves_ignite_proliferation_radius", 17),
            ("main_hand_ignite_proliferation_radius", 20));
        Assert.Equal((17, 20), (Game.IgniteProliferationRadius(get, "spell"), Game.IgniteProliferationRadius(get, "main_hand")));

        var conn = new ClientState(new(1, "ignite", 300, 300, 100, 100, null, []), 1, 1);
        var (source, near, far, immune) = (At(100), At(110), At(116), At(105, ("immune_to_ignite", 1)));
        uint id = 0;
        foreach (var m in new[] { source, near, far, immune }) conn.Monsters[++id] = m;
        Assert.True(source.Ignites.Add(0, 100, 4, spread: 15));

        var packets = Game.MonsterAilmentChangesLocked(conn, now: .25);
        Assert.True(near.Ignites.Burning);
        Assert.False(far.Ignites.Burning);
        Assert.False(immune.Ignites.Burning);
        Assert.Empty(near.Ignites.Spreads(.25));
        Assert.Equal(0, near.Ignites.LastApplied);
        Assert.NotNull(near.Ignites.Buff);
        Assert.Contains(packets, p => p.Log.Contains("ignited by proliferation"));
        Assert.Equal(100, near.Ignites.Advance(1.25, 1));
    }

    // A corpse keeps spreading its ignite until that ends; the copy ends with it. A shattered corpse stops at once.
    [Fact]
    public void Corpse_spreads_its_ignite_until_it_ends()
    {
        var conn = new ClientState(new(1, "corpse", 300, 300, 100, 100, null, []), 1, 1);
        var (source, near) = (At(100), At(110));
        conn.Monsters[1] = source;
        conn.Monsters[2] = near;
        source.Ignites.Add(0, 100, 4, spread: 15);
        Game.MonsterAilmentChangesLocked(conn, now: .25);
        Assert.Equal(100, near.Ignites.Advance(1.25, 1));

        source.Dead = true;
        source.Life = 0;
        source.Ignites.Clear();
        Assert.Single(source.Ignites.Spreads(1.5));
        Game.MonsterAilmentChangesLocked(conn, now: 1.5);
        Assert.Equal(125, near.Ignites.Advance(2.5, 1));

        Game.MonsterAilmentChangesLocked(conn, now: 4.5);
        Assert.Equal(150, near.Ignites.Advance(4.5, 1));
        Assert.False(near.Ignites.Burning);

        var (shattered, close) = (At(200), At(210));
        conn.Monsters[3] = shattered;
        conn.Monsters[4] = close;
        shattered.Ignites.Add(4.5, 100, 4, spread: 15);
        shattered.Dead = true;
        shattered.Life = 0;
        shattered.Ignites.Clear();
        shattered.Ailments.Shatter();
        Game.MonsterAilmentChangesLocked(conn, now: 4.75);
        Assert.False(close.Ignites.Burning);
    }
}
