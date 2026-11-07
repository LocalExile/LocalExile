using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

/// <summary>Shared stances and distance-dependent hit effects.</summary>
public class FleshAndStoneAuraTests
{
    [Fact]
    public void FleshAndStone_is_supported_and_its_buff_follows_the_stance()
    {
        Assert.True(AuraCatalog.Supported("FleshAndStone"));
        Assert.Equal(AuraCatalog.FleshAndStoneBloodRow, AuraCatalog.BuffRow("FleshAndStone"));
        Assert.Equal(AuraCatalog.FleshAndStoneBloodRow, AuraCatalog.BuffRow("FleshAndStone", "Blood"));
        Assert.Equal(AuraCatalog.FleshAndStoneSandRow, AuraCatalog.BuffRow("FleshAndStone", "Sand"));

        // The skill alternates on each activation.
        Assert.Equal("Sand", AuraCatalog.OtherStance("Blood"));
        Assert.Equal("Blood", AuraCatalog.OtherStance("Sand"));
        Assert.Equal("Sand", AuraCatalog.OtherStance(null));
        Assert.True(AuraCatalog.Supported("BloodAndSand"));
        Assert.Equal(AuraCatalog.FleshAndStoneSandRow, AuraCatalog.BuffRow("BloodAndSand", "Sand"));
        Assert.Equal(1, AuraCatalog.DistanceEffect(0, 40));
        Assert.Equal(.5, AuraCatalog.DistanceEffect(20, 40));
        Assert.Equal(0, AuraCatalog.DistanceEffect(40, 40));
        Assert.Equal(0, AuraCatalog.DistanceEffect(80, 40));
        var skills = Program.Skills;
        var haste = new AuraCatalog.Aura(1, skills.EffectRow("Haste"), "Haste", "Haste", 1, 152, 0, 5000, Level: 20);
        var stronger = haste with { SkillId = 2, Level = 1, ExternalEffectPct = 100 };
        var own = AuraCatalog.Bonus([haste], 0, skills, Program.Stats);
        var external = AuraCatalog.Bonus([stronger], 0, skills, Program.Stats);
        var together = AuraCatalog.Bonus([haste, stronger], 0, skills, Program.Stats);
        Assert.Equal(Math.Max(own.AttackSpeedPct, external.AttackSpeedPct), together.AttackSpeedPct);
        Assert.Equal(Math.Max(own.MovementVelocityPct, external.MovementVelocityPct), together.MovementVelocityPct);
        uint cast = Program.Stats.Key("cast_speed_+%")!.Value;
        Assert.Equal(30, AuraCatalog.RawStats([haste, stronger], 0, skills, Program.Stats).Single(p => p.Key == cast).Value);
    }

    [Fact]
    public void Sand_reduces_damage_taken_and_Blood_raises_physical_damage_taken()
    {
        var skills = new SkillCatalog();
        int effect = skills.EffectRow("FleshAndStone");
        Assert.True(effect >= 0);
        var sand = new AuraCatalog.Aura(0x999, effect, "FleshAndStone", "Flesh and Stone", 1,
            AuraCatalog.BuffRow("FleshAndStone", "Sand"), 0, 2500, Level: 20, Stance: "Sand");
        var blood = sand with { Stance = "Blood", BuffRow = AuraCatalog.FleshAndStoneBloodRow };

        int reduction = AuraCatalog.FleshAndStoneDamageReduction(sand, 0, skills, Program.Stats);
        Assert.NotEqual(0, reduction);
        uint? takenKey = Program.Stats.Key("base_damage_taken_+%");
        Assert.NotNull(takenKey);
        var sandStats = AuraCatalog.RawStats([sand], 0, skills, Program.Stats).ToDictionary(x => x.Key, x => x.Value);
        Assert.DoesNotContain(takenKey!.Value, sandStats.Keys); // An enemy-distance reduction is never a global reduced-taken stat.
        Assert.DoesNotContain(AuraCatalog.RawStats([blood], 0, skills, Program.Stats), x => x.Key == takenKey);

        // The Blood Stance value is data-driven, and only the Blood tick applies it (EnemyDebuffsTick's stance check).
        Assert.True(AuraCatalog.FleshAndStonePhysicalTaken(blood, 0, skills, Program.Stats) > 0);

        // The Blood Stance share reaches the Physical damage a monster takes and nothing else.
        var monster = new SpawnedMonster("flesh and stone", new(0, 0, 0), 0, 0, 1000);
        var physical = DamagePacket.Of(DamageType.Physical, 100, 100);
        var fire = DamagePacket.Of(DamageType.Fire, 100, 100);
        Assert.Equal(100, Game.MonsterTakesMitigated(monster, physical, spell: false));
        monster.TakenFromPlayer = (0, 0, 50);
        Assert.Equal(150, Game.MonsterTakesMitigated(monster, physical, spell: false));
        Assert.Equal(100, Game.MonsterTakesMitigated(monster, fire, spell: false));
        monster.PridePhysicalMore = 20;
        Assert.Equal(180, Game.MonsterTakesMitigated(monster, physical, spell: false));
        Assert.Equal(1.2, Game.MonsterDotTaken(monster, DamageType.Physical)); // Flesh and Stone affects hits only.
    }
}
