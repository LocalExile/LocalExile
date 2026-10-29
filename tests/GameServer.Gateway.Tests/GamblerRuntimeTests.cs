using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class GamblerRuntimeTests
{
    static uint Key(string name) => Program.Stats.Key(name)!.Value;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Live_projectile_contacts_apply_less_and_triple_once_without_multiplying_stored_impale(bool spell)
    {
        await using var f = new ProjectileRuntimeFixture();
        var target = ProjectileRuntimeFixture.Monster(40, 10);
        f.Conn.Monsters.Add(42, target);
        if (!spell) target.Impale.Apply(100); // Stored reflection is 10 per hit.
        var offense = new ProjectileOffense(DamagePacket.Of(DamageType.Physical, 20, 20), spell, int.MaxValue, 0, 150, "gambler");
        f.Release(f.Shot(new("Metadata/Projectiles/DefaultArrow") { Speed = 100, Range = 100, Radius = 1 }, offense));
        // Both inputs are live contact policies, not baked into the launched packet.
        f.Conn.PlayerStats[Key("marauder_hidden_ascendancy_damage_+%_final")] = -30;
        f.Conn.PlayerStats[Key("base_chance_to_deal_triple_damage_%")] = 100;
        f.Advance(1);
        Assert.Equal(spell ? 58u : 48u, target.Life);
    }

    [Fact]
    public void Growing_Accumulator_scales_attack_spell_and_dot_but_never_triples_a_dot()
    {
        var stats = new Dictionary<uint, int> { [Key("marauder_hidden_ascendancy_damage_+%_final")] = -30,
            [Key("base_chance_to_deal_triple_damage_%")] = 100 };
        var packet = DamagePacket.Of(DamageType.Fire, 100, 100);
        foreach (var kind in new[] { SkillHitKind.Attack, SkillHitKind.Spell })
        {
            var context = new SkillDamageContext(kind, stats);
            Assert.Equal(70, context.Hit(packet).MinTotal);
            Assert.Equal(70, context.OverTime(new(DamageType.Fire, 100, 4)).DamagePerSecond);
        }
        var weapon = new SkillCatalog.Unarmed(1000, 100, 100, 10);
        Assert.Equal(70, Program.AttackHitConverted(weapon, stats, true, null).MinTotal);
    }

    [Fact]
    public void Reversed_Odds_composes_cap_penetration_and_critical_ignore_in_the_actual_monster_pass()
    {
        var target = new SpawnedMonster("resist", new(40, 10, 10), 0, 2, 100)
        { Mods = new([], [], new(new Dictionary<string, int> { ["base_fire_damage_resistance_%"] = 200 })) };
        var hit = DamagePacket.Of(DamageType.Fire, 100, 100);
        var pen = new Penetration.PerType(10, 0, 0, 0);
        Assert.Equal(185, Program.MonsterDamage(target, hit, false, true, default, pen, true).Damage);
        Assert.Equal(100, Program.MonsterDamage(target, hit, true, true, new(1, 0, 0, 0), pen, true).Damage);
        target = new SpawnedMonster("negative", new(40, 10, 10), 0, 2, 100)
        { Mods = new([], [], new(new Dictionary<string, int> { ["base_fire_damage_resistance_%"] = -200 })) };
        Assert.Equal(0, Program.MonsterDamage(target, hit, false, true, default, default, true).Damage);
        Assert.Equal(300, Program.MonsterDamage(target, hit, false, true, default).Damage);
    }

    [Fact]
    public void Extra_damage_rolls_are_bounded_and_opposing_block_luck_cancels()
    {
        Assert.Equal(80, RandomRolls.Next(new SequenceRandom(20, 80), 1, 101, int.MaxValue));
        Assert.Equal(20, RandomRolls.Next(new SequenceRandom(20, 80), 1, 101, int.MinValue));
        Assert.Equal(25, DamageModel.EffectiveBlockChance(50, unlucky: true));
        Assert.Equal(75, DamageModel.EffectiveBlockChance(50, lucky: true));
        Assert.Equal(50, DamageModel.EffectiveBlockChance(50, true, true));
    }

    sealed class SequenceRandom(params int[] values) : Random
    {
        readonly Queue<int> remaining = new(values);
        public override int Next(int minValue, int maxValue) => remaining.Dequeue();
    }
}
