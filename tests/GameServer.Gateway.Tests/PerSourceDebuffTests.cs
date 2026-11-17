namespace GameServer.Gateway.Tests;

// A party shares its monsters: each wearer keeps its own contribution to an enemy debuff, which every source of damage
// to that enemy then takes.
public sealed class PerSourceDebuffTests
{
    [Fact]
    public void Each_wearer_keeps_its_own_per_ailment_damage_taken_on_a_shared_monster()
    {
        var instance = new InstanceState();
        ClientState Player(string name) => new(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
            { Character = name, Instance = instance, PlayerLife = 1000 };
        var (wearer, other, second) = (Player("YokeA"), Player("YokeB"), Player("YokeC"));
        var m = new SpawnedMonster("shared", new(105, 100, 0), 0, 0, 1000);
        lock (wearer.Monsters) wearer.Monsters[1] = m;
        Assert.Same(m, other.Monsters[1]);
        // Two ailment types on the enemy (the wearer's ignite and poison).
        m.Ignites.Add(1, 50, 4);
        m.Poisons.Add(1, 20, 4);
        // Every hit and damage-over-time tick reads the same factor, whoever (or whatever) deals it.
        double Taken() => Game.MonsterDamageTaken(m, spell: false, now: 2);

        // The wearer's step, then the non-wearer's: the second does not erase the first, and the non-wearer's damage
        // and unowned damage take the wearer's increase.
        Game.KeepPerAilmentTypeTakenLocked(wearer, m, 10);
        Game.KeepPerAilmentTypeTakenLocked(other, m, 0);
        Assert.Equal(1.2, Taken(), 6);
        Assert.Equal(1.2, Game.MonsterDotTaken(m, GameServer.Core.World.DamageType.Fire, 2)
            / Game.MonsterDotTaken(new SpawnedMonster("plain", new(105, 100, 0), 0, 0, 1000), GameServer.Core.World.DamageType.Fire, 2), 6);

        // Two wearers' increases add: (10 + 5)% per type.
        Game.KeepPerAilmentTypeTakenLocked(second, m, 5);
        Assert.Equal(1.3, Taken(), 6);

        // Unequipping (or dying) removes only that wearer's part, and so does leaving the instance.
        Game.KeepPerAilmentTypeTakenLocked(wearer, m, 0);
        Assert.Equal(1.1, Taken(), 6);
        Game.KeepPerAilmentTypeTakenLocked(wearer, m, 10);
        Game.ForgetSourceValues(second);
        Assert.Equal(1.2, Taken(), 6);
    }
}
