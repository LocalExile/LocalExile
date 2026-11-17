namespace GameServer.Gateway.Tests;

// A party shares its monsters: a debuff that counts only for its owner's damage is kept per source on the monster.
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
        m.Ignites.Add(1, 50, 4);
        m.Poisons.Add(1, 20, 4);
        double Taken(ClientState conn) => Game.MonsterDamageTaken(m, spell: false, now: 2, source: conn.PeerId);

        // The wearer's step, then the non-wearer's: the second does not erase the first.
        Game.KeepPerAilmentTypeTakenLocked(wearer, m, 10);
        Game.KeepPerAilmentTypeTakenLocked(other, m, 0);
        Assert.Equal(1.2, Taken(wearer), 6);
        Assert.Equal(1.0, Taken(other), 6);
        Assert.Equal(1.0, Game.MonsterDamageTaken(m, spell: false, now: 2), 6);

        // Two wearers with different rolls each get their own.
        Game.KeepPerAilmentTypeTakenLocked(second, m, 5);
        Assert.Equal(1.2, Taken(wearer), 6);
        Assert.Equal(1.1, Taken(second), 6);

        // Unequipping (or dying) removes only the wearer's own entry, and so does leaving the instance.
        Game.KeepPerAilmentTypeTakenLocked(wearer, m, 0);
        Assert.Equal(1.0, Taken(wearer), 6);
        Assert.Equal(1.1, Taken(second), 6);
        Game.KeepPerAilmentTypeTakenLocked(wearer, m, 10);
        Game.ForgetSourceValues(second);
        Assert.Equal(1.0, Taken(second), 6);
        Assert.Equal(1.2, Taken(wearer), 6);
    }
}
