namespace GameServer.Gateway.Tests;

// A party shares its monsters: each wearer keeps its own contribution to an enemy debuff, counted over the ailments that
// wearer inflicted, and every source of damage to that enemy then takes the sum.
public sealed class PerSourceDebuffTests
{
    [Fact]
    public void Each_wearer_keeps_its_own_per_ailment_damage_taken_on_a_shared_monster()
    {
        var instance = new InstanceState();
        Player Player(string name) => new(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
            { Character = { Name = name }, Instance = instance, PlayerLife = 1000 };
        var (wearer, other, second) = (Player("YokeA"), Player("YokeB"), Player("YokeC"));
        var m = new SpawnedMonster("shared", new(105, 100, 0), 0, 0, 1000);
        wearer.Area.Monsters[1] = m;
        Assert.Same(m, other.Area.Monsters[1]);
        // Every hit and damage-over-time tick reads the same factor, whoever (or whatever) deals it.
        double Taken() => Game.MonsterDamageTaken(m, spell: false, now: 2);

        // A poison inflicted by the non-wearer alone does not count for the wearer's Yoke.
        m.Poisons.Add(1, 20, 4);
        Game.InflictedLocked(other, m, Game.AilmentType.Poison, 5);
        Game.KeepPerAilmentTypeTakenLocked(wearer, m, 10);
        Game.KeepPerAilmentTypeTakenLocked(other, m, 0);
        Assert.Equal(1.0, Taken(), 6);

        // The wearer's own ignite does; the non-wearer's update does not erase the wearer's part, and the non-wearer's
        // damage and unowned damage take it.
        m.Ignites.Add(1, 50, 4);
        Game.InflictedLocked(wearer, m, Game.AilmentType.Ignite, 5);
        Assert.Equal(1.1, Taken(), 6);
        Assert.Equal(1.1, Game.MonsterDotTaken(m, GameServer.Core.World.DamageType.Fire, 2)
            / Game.MonsterDotTaken(new SpawnedMonster("plain", new(105, 100, 0), 0, 0, 1000), GameServer.Core.World.DamageType.Fire, 2), 6);
        // After the wearer's ignite of its own runs out, the ignite still on the enemy is no longer the wearer's.
        Assert.Equal(0, Game.AilmentTypesInflicted(m, wearer.Id, 5));

        // Two wearers with different rolls each count their own ailments: A ignite (10), C ignite and poison (5 each).
        Game.KeepPerAilmentTypeTakenLocked(second, m, 5);
        Game.InflictedLocked(second, m, Game.AilmentType.Ignite, 5);
        Game.InflictedLocked(second, m, Game.AilmentType.Poison, 5);
        Assert.Equal(1.2, Taken(), 6);

        // Unequipping (or dying) removes only that wearer's part, and so does leaving the instance.
        Game.KeepPerAilmentTypeTakenLocked(wearer, m, 0);
        Assert.Equal(1.1, Taken(), 6);
        Game.KeepPerAilmentTypeTakenLocked(wearer, m, 10);
        Game.ForgetSourceValues(second);
        Assert.Equal(1.1, Taken(), 6);
        Assert.Equal(0, Game.AilmentTypesInflicted(m, second.Id, 2));
    }
}
