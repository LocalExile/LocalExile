using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// Scorch, Brittle and Sap (MonsterAlternateAilments): inflicted only from a source, their effect from the hit's damage of
// their element against the ailment threshold, kept beside the others of their kind on a monster every player shares.
public class AlternateAilmentTests
{
    const int Scorch = (int)Game.AlternateKind.Scorch, Brittle = (int)Game.AlternateKind.Brittle, Sap = (int)Game.AlternateKind.Sap;

    static Func<uint, int> With(params (uint Stat, int Value)[] stats) =>
        name => stats.Where(s => s.Stat == name).Sum(s => s.Value);

    static Dictionary<uint, int> Raw(params (uint Name, int Value)[] stats) =>
        stats.ToDictionary(s => s.Name, s => s.Value);

    static DamagePacket Taken(DamageType type, double amount) => DamagePacket.Of(type, amount, amount);

    // A monster with a threshold (maximum life) of 100: a hit dealing 10% of it scorches for floor(50 × 0.1^0.4) = 19,
    // inflicts floor(10 × 0.1^0.4) = 3.98% Brittle and floor(100/3 × 0.1^0.4) = 13% Sap, each for 4 s.
    [Fact]
    public void Magnitude_duration_strongest_and_effects_follow_the_source_stats()
    {
        var m = new SpawnedMonster("scorched", new(105, 100, 0), 0, 0, 100);
        List<(Game.AlternateAilment A, RuleAilments.Instance Instance)> Hit(DamagePacket damage, Func<uint, int> attacker, double now,
            bool critical = false) => Game.AlternateAilmentsOnHitLocked(m, damage, critical, now, attacker, new Random(1));

        // No source, no Scorch: a critical strike alone does not inflict it.
        Assert.Empty(Hit(Taken(DamageType.Fire, 10), With(), 0, critical: true));
        int unscorched = Game.MonsterHitResistance(m, DamageType.Fire, default, now: 1).Value;

        var always = With((Stat.ChanceToScorchPct, 100), (Stat.VirtualScorchDurationPlusPct, 50));
        var scorch = Assert.Single(Hit(Taken(DamageType.Fire, 10), always, 0)).Instance;
        Assert.Equal((Scorch, 19.0, 6.0), (scorch.Kind, scorch.Magnitude, scorch.Until));
        // Its effect modifiers, the cap of 30, and only the fire part counts.
        Assert.Equal(29, Assert.Single(Hit(Taken(DamageType.Fire, 10),
            With((Stat.ChanceToScorchPct, 100), (Stat.VirtualScorchEffectPlusPct, 50)), 0)).Instance.Magnitude);
        Assert.Equal(30, Assert.Single(Hit(Taken(DamageType.Fire, 100), always, 0)).Instance.Magnitude);
        Assert.Empty(Hit(Taken(DamageType.Cold, 10), always, 0));
        // A weaker, longer one (floor(50 × 0.01^0.4) = 7, until 7 s) is kept beside the strongest, which applies until it ends.
        m = new SpawnedMonster("scorched", new(105, 100, 0), 0, 0, 100);
        Hit(Taken(DamageType.Fire, 10), With((Stat.ChanceToScorchPct, 100)), 0);
        Assert.Equal(7, Assert.Single(Hit(Taken(DamageType.Fire, 1), always, 1)).Instance.Magnitude);
        Assert.Equal(19, m.RuleAilments.Magnitude(Scorch, 3.9));
        Assert.Equal(7, m.RuleAilments.Magnitude(Scorch, 4.5));
        Assert.Equal(0, m.RuleAilments.Magnitude(Scorch, 7));
        // Scorch lowers the resistances every hit and damage over time faces; chaos and a call without a time are untouched.
        Assert.Equal(unscorched - 19, Game.MonsterHitResistance(m, DamageType.Fire, default, now: 2).Value);
        Assert.Equal(Game.MonsterHitResistance(m, DamageType.Lightning, default).Value - 19,
            Game.MonsterHitResistance(m, DamageType.Lightning, default, now: 2).Value);
        Assert.Equal(Game.MonsterHitResistance(m, DamageType.Chaos, default).Value,
            Game.MonsterHitResistance(m, DamageType.Chaos, default, now: 2).Value);
        Assert.Equal(unscorched - 7, Game.MonsterHitResistance(m, DamageType.Fire, default, now: 5).Value);

        // Secrets of Suffering: a critical strike inflicts all three (no chance needed) and no chill or shock.
        using (MonsterElementalAilments.HandOverInflictProhibitions(Game.SecretsOfSufferingProhibitions))
        {
            var secrets = With((Game.SecretsOfSuffering, 1));
            var hit = Taken(DamageType.Fire, 10).Add(DamageType.Cold, new(10, 10)).Add(DamageType.Lightning, new(10, 10));
            Assert.Equal([(Scorch, 19.0), (Brittle, 398.0), (Sap, 13.0)],
                Hit(hit, secrets, 10, critical: true).Select(e => (e.Instance.Kind, e.Instance.Magnitude)));
            Assert.Empty(Hit(hit, secrets, 10, critical: false));
            Assert.Empty(m.Ailments.Inflict(hit, critical: true, 100, 10, secrets, n => m.Effects[n], new Random(1)));
        }
        // Brittle adds to the critical strike chance against it, Sap lessens its hits; immunity keeps one off.
        Assert.Equal(398, Game.BrittleCriticalChance(m, 11));
        Assert.Equal(.87, Game.SappedDamageFactor(m, 11), 9);
        Assert.Equal(1, Game.SappedDamageFactor(m, 15), 9);
        var immune = new RuleAilments();
        Assert.Empty(Game.AlternateAilmentsOnHit(immune, Taken(DamageType.Lightning, 10), false, 100, 0,
            With((Stat.ChanceToInflictSappedPct, 100)), With((Stat.ImmuneToSap, 1)), new Random(1)));
        // From no hit (a block's): Sap's set 6% for 4 s.
        Assert.Equal((6.0, 24.0), Game.AlternateAilmentWithoutHit(immune, Game.AlternateKind.Sap, 20, With(), With()) is { } blockSap
            ? (blockSap.Magnitude, blockSap.Until) : default);

        // The buff: scorched (1243) with base_resist_all_elements_% negative, on its fixed instance, which NextBuff skips.
        var scorchRule = Game.AlternateAilmentOf(Game.AlternateKind.Scorch);
        Assert.Equal(InstanceEntities.AddBuff(5, Player.FixedBuffInstances, 1243, 0, u82: 0,
            values: [unchecked((uint)-19)]), Game.AlternateAilmentBuff(5, scorchRule, 19));
        Assert.True(Game.AlternateAilments.All(a => a.Instance >= Player.FixedBuffInstances
            && a.Instance < MonsterElementalAilments.FirstInstance - 2));
    }

    // A party shares its monsters: two players' Scorches on one monster are both kept, the stronger applying whoever hit
    // last, and each ends at its own time; neither player's hit clears the other's.
    [Fact]
    public void Two_players_scorches_on_a_shared_monster_keep_the_stronger_until_it_ends()
    {
        var instance = new InstanceState();
        Player MakePlayer(string name) => new(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
            { Character = { Name = name }, Instance = instance, PlayerLife = 1000 };
        var (strong, weak) = (MakePlayer("ScorchA"), MakePlayer("ScorchB"));
        var m = new SpawnedMonster("shared", new(105, 100, 0), 0, 0, 100);
        strong.Area.Monsters[1] = m;
        Assert.Same(m, weak.Area.Monsters[1]);
        var scorches = Raw((Stat.ChanceToScorchPct, 100));
        void Hit(Player player, double fire, double now) =>
            Game.AilmentsOnHitLocked(player, m, Taken(DamageType.Fire, fire), spell: true, critical: false, 150,
                scorches, null, now, new Random(1));

        Hit(strong, 10, 0);      // 19% until 4 s
        Hit(weak, 1, 1);         // 7% until 5 s: a later, weaker hit does not replace it
        Assert.Equal(19, m.RuleAilments.Magnitude(Scorch, 2));
        Hit(weak, 1, 3.5);       // and does not refresh or clear it either
        Assert.Equal(19, m.RuleAilments.Magnitude(Scorch, 3.9));
        Assert.Equal(7, m.RuleAilments.Magnitude(Scorch, 4.1));
        Assert.Equal(0, m.RuleAilments.Magnitude(Scorch, 7.5));
        // What every client is shown follows the strongest: on, down to the weaker one, then off.
        Assert.Equal([(Scorch, 0, 19)], m.RuleAilments.Present(2));
        Assert.Equal([(Scorch, 19, 7)], m.RuleAilments.Present(4.1));
        Assert.Equal([(Scorch, 7, 0)], m.RuleAilments.Present(7.5));
        Assert.True(m.RuleAilments.Idle);
    }
}
