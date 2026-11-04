using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// Green support gems whose stats had no consumer: the cull below a share of maximum Life for normal and magic enemies
// (Cull the Weak), the hits that cannot kill (Pacifism), additional poisons on an unpoisoned enemy (Lethal Dose), more
// hit damage per poison (Vile Toxins), the freeze chance against chilled enemies (Hypothermia), less ailment duration
// (Sadism) and the more-chains multiplier (Greater Chain).
public sealed class GreenSupportMechanicsTests
{
    static Dictionary<uint, int> With(params (string Name, int Value)[] stats) =>
        stats.ToDictionary(s => Program.Stats.Key(s.Name) ?? throw new InvalidOperationException(s.Name), s => s.Value);

    static Func<string, int> Stats(params (string Name, int Value)[] stats) =>
        name => stats.Where(s => s.Name == name).Sum(s => s.Value);

    // Cull the Weak's share applies only to normal and magic enemies; Culling Strike's own share keeps applying to all.
    // Pacifism's hit leaves one Life and cancels the cull.
    [Fact]
    public void Cull_the_weak_and_pacifism_cull_and_spare_by_rarity()
    {
        var weak = Game.Cull.Of(null, With(("kill_normal_or_magic_enemy_on_hit_if_under_x%_life", 47)),
            critical: false, spell: false);
        Assert.Equal(new Game.Cull(0, 0, 47), weak);

        var conn = new ClientState(new(1, "cull", 300, 300, 100, 100, null, []), 1, 1)
        {
            Pools = new(1, 1000, 100, 2, 0), PlayerLife = 500,
        };
        SpawnedMonster Monster(MonsterRarity rarity) =>
            new("cull target", new(100, 100, 0), 0, 0, 1000) { Rarity = rarity };

        // 500 Life is above 47% of 1000, so the hit does not cull; 470 is at the share.
        Assert.Equal(500u, Game.TakeOwnerHitLocked(conn, Monster(MonsterRarity.Normal), 500, weak).Life);
        Assert.Equal(0u, Game.TakeOwnerHitLocked(conn, Monster(MonsterRarity.Magic), 530, weak).Life);
        Assert.Equal(470u, Game.TakeOwnerHitLocked(conn, Monster(MonsterRarity.Rare), 530, weak).Life);
        Assert.Equal(470u, Game.TakeOwnerHitLocked(conn, Monster(MonsterRarity.Unique), 530, weak).Life);

        // With Culling Strike linked the larger share still applies to the rarities it belongs to.
        var both = Game.Cull.Of(null, With(("kill_enemy_on_hit_if_under_10%_life", 1),
            ("kill_normal_or_magic_enemy_on_hit_if_under_x%_life", 47)), critical: false, spell: false);
        Assert.Equal(new Game.Cull(10, 0, 47), both);

        var pacifism = Game.Cull.Of(null, With(("hits_cannot_kill_enemies", 1)), critical: false, spell: false);
        Assert.Equal(new Game.Cull(0, 0, 0, true), pacifism);
        Assert.Equal(1u, Game.TakeOwnerHitLocked(conn, Monster(MonsterRarity.Normal), 5000, pacifism).Life);
    }

    // Lethal Dose's extra poisons, Vile Toxins' per-poison damage, Hypothermia's freeze chance, Sadism's duration and
    // Greater Chain's chain multiplier.
    [Fact]
    public void Poison_freeze_duration_and_chain_support_stats_apply()
    {
        var conn = new ClientState(new(1, "supports", 300, 300, 100, 100, null, []), 1, 1);
        var physical = DamagePacket.Of(DamageType.Physical, 100, 100);
        var poison = new MonsterPoisons.Settings(100, Min: 10, Max: 10, CriticalMin: 20, CriticalMax: 20, Seconds: 2);
        var poisoner = With(("base_chance_to_poison_on_hit_%", 100),
            ("additional_poisons_+_to_apply_vs_non_poisoned_enemies", 2));
        var target = new SpawnedMonster("poisoned", new(105, 100, 0), 0, 0, 1000);

        // An unpoisoned enemy takes the hit's own poison and the two additional ones; a poisoned one only the own.
        Game.PoisonOnHitLocked(conn, target, physical, spell: false, critical: false, poisoner, null, poison, now: 10, new Random(1));
        Assert.Equal(3, target.Poisons.Stacks(10.5));
        Game.PoisonOnHitLocked(conn, target, physical, spell: false, critical: false, poisoner, null, poison, now: 10, new Random(1));
        Assert.Equal(4, target.Poisons.Stacks(10.5));

        // Vile Toxins: 5% more damage per poison, capped at the support's maximum of 8.
        var toxin = With(("support_debilitate_hit_damage_+%_final_per_poison_stack", 5),
            ("support_debilitate_hit_damage_max_poison_stacks", 8));
        Assert.Equal(1.15, Game.ConditionalSupportMore(toxin, attack: true, chilled: false, lowLife: false,
            fullLife: false, lifetap: false, poisonStacks: 3), 9);
        Assert.Equal(1.4, Game.ConditionalSupportMore(toxin, attack: true, chilled: false, lowLife: false,
            fullLife: false, lifetap: false, poisonStacks: 20), 9);
        Assert.Equal(1, Game.ConditionalSupportMore(toxin, attack: true, chilled: false, lowLife: false,
            fullLife: false, lifetap: false), 9);

        // Hypothermia's extra freeze chance counts an enemy chilled by an earlier hit, not the hit's own chill.
        var cold = DamagePacket.Of(DamageType.Cold, 50, 50);
        var frost = Stats(("additional_chance_to_freeze_chilled_enemies_%", 100));
        var chilled = new MonsterElementalAilments();
        Assert.DoesNotContain(chilled.Inflict(cold, critical: false, threshold: 100, now: 0, Stats(), Stats(), new Random(1)),
            e => e.Kind == MonsterElementalAilments.Kind.Freeze);
        Assert.Contains(chilled.Inflict(cold, critical: false, threshold: 100, now: 1, frost, Stats(), new Random(1)),
            e => e.Kind == MonsterElementalAilments.Kind.Freeze);
        Assert.DoesNotContain(new MonsterElementalAilments().Inflict(cold, critical: false, threshold: 100, now: 0,
            frost, Stats(), new Random(1)), e => e.Kind == MonsterElementalAilments.Kind.Freeze);

        // Sadism: its "70% less Duration" shortens the supported skill's damaging ailments.
        Assert.Equal(1.2, MonsterIgnites.Settings.From([],
            "main_hand", Stats(("support_faster_ailments_ailment_duration_+%_final", -70))).Seconds, 6);
        Assert.Equal(4, MonsterIgnites.Settings.From([], "main_hand", Stats()).Seconds, 6);

        // Greater Chain: "50% more times" on the chain count, one chain including its own.
        static ProjectileProfile Resolve(Func<string, int> get) =>
            GatewayProjectileModifiers.Resolve(new ProjectileProfile("test"), get, attack: true);
        Assert.Equal(2, Resolve(Stats(("number_of_chains", 1), ("support_chain_count_+%_final", 50))).Chain);
        Assert.Equal(3, Resolve(Stats(("number_of_chains", 2), ("support_chain_count_+%_final", 50))).Chain);
        Assert.Equal(2, Resolve(Stats(("number_of_chains", 2))).Chain);
    }
}
