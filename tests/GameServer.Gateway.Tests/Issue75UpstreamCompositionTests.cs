using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class Issue75UpstreamCompositionTests
{
    [Theory]
    [InlineData(false, 761)]
    [InlineData(true, 689)]
    public void Mana_conversion_retains_per_level_sources_inherent_prohibitions_and_equipment_scopes(bool excludeShield, int armour)
    {
        var c = SkillCatalog.ClassInfo.Witch with { Strength = 16, Intelligence = 33 };
        var b = new CharacterStats.Bonus(Mana: 44, ManaPerLevel: 3, ManaPct: 25, ManaPerFourStrength: 8,
            Armour: 100, ArmourPct: 50, ArmourMorePct: 20, ShieldArmour: 30, ShieldArmourPct: 50,
            BodyArmour: 40, DoubleBodyArmour: true, NoShieldDefences: excludeShield,
            NoIntelligenceBonuses: true, MentalConditioning: true);
        var pools = CharacterStats.For(c, 4, b);
        // Mana base = 34 + 24 + 44 + 12 + 32 = 146. The inherent Int half is prohibited.
        // Converted armour = 146 * 1.6 * (1 + .25 + .50) * 1.2 = 490.56.
        Assert.Equal(37u, pools.Mana);
        Assert.Equal(armour, pools.Armour);
        Assert.Equal(33, pools.Intelligence);
        Assert.Equal(pools, CharacterStats.For(c, 4, b with { Mana = 56, ManaPerLevel = 0 }));
    }

    [Fact]
    public void Fractional_ward_conversion_leaves_fractional_body_defences_for_Iron_Reflexes_and_more()
    {
        var pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new(Armour: 9.5m, BodyArmour: 9.5m, Evasion: 7.5m, BodyEvasion: 7.5m,
                Ward: 17, WardPct: 40, ArmourPct: 40, EvasionPct: 40, SharedDefencesPct: 40,
                IronReflexes: true, DoubleBodyArmour: true, ArmourMorePct: 32));
        // Remaining body AR + converted body EV are doubled; the character's 15 EV and Ward are not.
        Assert.Equal(91, pools.Armour); // (19 + 15 + 15) * 1.4 * 1.32 = 90.552.
        Assert.Equal(0, pools.Evasion);
        Assert.Equal(23u, pools.Ward); // Ward retains its separate upstream floor projection.
    }

    sealed class CriticalDraws : Random
    {
        public int Calls;
        public override int Next(int maxValue) { Assert.Equal(10000, maxValue); Calls++; return 0; }
    }

    [Fact]
    public async Task Chronological_projectile_kill_keeps_impales_and_leech_excludes_reflected_damage()
    {
        var critical = new CriticalDraws();
        await using var f = new ProjectileRuntimeFixture(new(new(1, "combined kill", 300, 300, 10, 10, null, []), 1, 1)
            { PlayerLife = 100, CriticalRandom = critical });
        f.Conn.Pools = f.Conn.Pools with { Life = 1000 };
        f.Conn.PlayerStats[Program.Stats.Key("leech_%_is_instant")!.Value] = 100;
        var target = ProjectileRuntimeFixture.Monster(20, 10);
        target.Impale.Apply(100, now: 0); f.Conn.Monsters.Add(42, target);
        var shot = new ProjectileOffense(DamagePacket.Of(DamageType.Physical, 100, 100), false, 0, 10000, 200, "combined")
        {
            AccuracyProfile = new(0, CannotBeEvaded: true), CriticalRoll = new(0), ImpaleSettings = new(100),
            Leech = LeechSources.From(n => n == "life_leech_from_any_damage_permyriad" ? 100 : 0, true)
        };
        await f.HoldOutput(); f.Release(f.Shot(offense: shot)); f.Advance(2);
        Assert.True(target.Dead); Assert.Equal(0u, target.Life);
        Assert.Equal(102u, f.Conn.PlayerLife); // 1% of the 200 direct damage, excluding 10 reflected damage.
        Assert.Equal(2, target.Impale.Stacks); Assert.Equal(30, target.Impale.Bonus);
        Assert.Equal(1, critical.Calls); Assert.Equal(0, f.Available);
        f.Advance(12); Assert.Same(target, f.Conn.Monsters[42]); // Same corpse lifetime as ordinary melee.
        f.Session.Invalidate(); Assert.Empty(f.Session.Runtime.Projectiles);
    }

    [Fact]
    public async Task Catch_up_leech_uses_each_contact_time_before_later_recovery_is_consumed()
    {
        await using var f = new ProjectileRuntimeFixture();
        f.Conn.Pools = f.Conn.Pools with { Life = 1000 };
        foreach (var (id, x) in new[] { (42u, 20), (43u, 30) })
            f.Conn.Monsters.Add(id, new("leech target", new(x, 10, 0), 0, 0, 1000));
        var shot = new ProjectileOffense(DamagePacket.Of(DamageType.Physical, 100, 100), false, 0, 0, 150, "timed leech")
        {
            AccuracyProfile = new(0, CannotBeEvaded: true),
            Leech = LeechSources.From(n => n == "life_leech_from_any_damage_permyriad" ? 1000 : 0, true)
        };
        f.Release(f.Shot(new("Metadata/Projectiles/DefaultArrow") { Speed = 10, Range = 100, Radius = 1, Pierce = 1 }, shot));
        f.Advance(2);
        Assert.Equal(900u, f.Conn.Monsters[42].Life); Assert.Equal(900u, f.Conn.Monsters[43].Life);
        // Each hit grants 10 at 20/s. The first completed; the second has earned only .1 seconds.
        Assert.Equal(12, Program.LeechOf(f.Conn).TakeRecovery(2).Life, 3);
    }

    [Fact]
    public void Default_per_Brutal_Charge_coefficient_does_not_disable_ordinary_stun()
    {
        TestSaves.Ensure();
        var conn = new ClientState(new(ushort.MaxValue, "stun composition", 100, 100, 10, 10, null, []), 1, 1)
            { Character = "StunComposition" + Guid.NewGuid().ToString("N"), PlayerLife = 100 };
        var stats = Program.CharacterPlayerStats(conn);
        Assert.Equal(10, stats[Program.Stats.Key("stun_threshold_+%_per_brutal_charge")!.Value]);
        // The coefficient counts only converted charges (ConvertedChargesGatewayTests): none here, no threshold.
        Assert.Equal(0, stats.GetValueOrDefault(Program.Stats.Key("stun_threshold_+%")!.Value));
        Assert.NotNull(Program.CommitPlayerHitRecoveryLocked(conn, 10000, 0));
    }

    [Fact]
    public void Skill_spell_increase_transfer_preserves_large_signed_values()
    {
        int Get(string name) => name switch
        {
            "spell_damage_+%" => int.MaxValue,
            "active_skill_additive_spell_damage_modifiers_apply_to_attack_damage_at_%_value" => 150,
            _ => 0
        };
        Assert.Equal(int.MaxValue * 1.5, DamageStats.SpellAttackIncrease(Get));
    }
}
