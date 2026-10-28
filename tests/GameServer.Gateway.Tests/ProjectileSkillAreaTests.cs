using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public partial class SpellNoDamageTests
{
    [Theory]
    [InlineData(null, 28, 40)]
    [InlineData("Concentrated Effect Support", 23, 33)]
    public async Task KB_production_release_scales_blast_and_placement_once_and_scopes_support_damage(
        string? support, int blastRadius, int placementRadius)
    {
        using var f = new Fixture("KineticBlast", "Driftwood Wand", support);
        await using var runtime = new ProjectileRuntimeFixture(f.Conn);
        foreach (string stat in new[] { "damage_+%", "attack_damage_+%", "physical_damage_+%",
            "projectile_damage_+%", "spell_damage_+%", "elemental_damage_+%", "fire_damage_+%" })
            f.Set(stat, 0);
        f.Set("base_skill_area_of_effect_+%", 300);
        f.Set("area_damage_+%", 100);
        var gems = (IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)
            Method("GemSkillEffects").Invoke(null, [f.Conn.Character])!;
        var use = (ProjectileUse)Method("CaptureGemProjectile").Invoke(null,
            [f.Conn, runtime.Session, f.Skill, gems.Single(g => g.Id == f.Skill), (100, 32)])!;
        var area = use.Profile.ContactArea!;
        Assert.Equal(blastRadius, area.Radius); Assert.Equal(placementRadius, area.Offset);
        Assert.Equal(.6, area.Multiplier);
        var offense = Assert.IsType<ProjectileOffense>(use.Payload);
        Assert.NotNull(offense.AreaHit);
        if (support is null) Assert.Equal(offense.Hit.MinTotal * 2, offense.AreaHit.MinTotal, 5);
        else Assert.True(offense.AreaHit.MinTotal > offense.Hit.MinTotal * 2);
        // Real gateway contact consumes the direct packet; explosions alone receive area/Concentrated bonuses.
        uint before = f.Target.Life;
        runtime.Release(use); runtime.Advance(1);
        Assert.InRange((double)(before - f.Target.Life), Math.Floor(offense.Hit.MinTotal), Math.Ceiling(offense.Hit.MaxTotal));
    }
}
