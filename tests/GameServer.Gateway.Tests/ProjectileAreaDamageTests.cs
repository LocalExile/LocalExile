using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class SpellNoDamageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_root_capture_retains_area_snapshot_for_later_area_stages(bool childArea)
    {
        using var f = new Fixture("Spark", targetX: 60);
        await using var runtime = new ProjectileRuntimeFixture(f.Conn);
        f.Set("area_damage_+%", 10000);
        var gems = (IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)
            Method("GemSkillEffects").Invoke(null, [f.Conn.Character])!;
        var use = (ProjectileUse)Method("CaptureGemProjectile").Invoke(null,
            [f.Conn, runtime.Session, f.Skill, gems.Single(g => g.Id == f.Skill), (100, 32)])!;
        Assert.Null(use.Profile.EndArea);
        Assert.Null(use.Profile.ContactArea);
        Assert.Null(use.Profile.Pulse);
        var offense = Assert.IsType<ProjectileOffense>(use.Payload);
        Assert.NotNull(offense.AreaHit);
        Assert.True(offense.AreaHit.MinTotal > offense.Hit.MaxTotal);

        // A provider may add stages after capture; emissions must inherit the frozen area packet.
        var area = use.Profile with
        {
            Count = 1, Speed = 100, Range = 100, Radius = .1, Pierce = 0,
            EndArea = new("later-area", 5)
        };
        use = use with { Profile = childArea
            ? area with { DirectDamage = false, Range = 10, EndArea = null, EndEmission = new(area) }
            : area };
        f.Set("area_damage_+%", 0); // Contact must use release stats, not rebuild the packet.
        uint before = f.Target.Life;
        runtime.Release(use);
        runtime.Advance(1);
        Assert.InRange(before - f.Target.Life, (uint)Math.Floor(offense.AreaHit.MinTotal),
            (uint)Math.Ceiling(offense.AreaHit.MaxTotal));
        Assert.True(before - f.Target.Life > offense.Hit.MaxTotal);
    }

    [Theory]
    [InlineData("Fireball", null)]
    [InlineData("KineticBlast", "Driftwood Wand")]
    public void Projectile_area_context_applies_increases_and_concentrated_support_only_to_area(string effect, string? weapon)
    {
        using var basic = new Fixture(effect, weapon);
        using var linked = new Fixture(effect, weapon, "Concentrated Effect Support");
        foreach (var fixture in new[] { basic, linked })
            foreach (string stat in new[] { "damage_+%", "attack_damage_+%", "physical_damage_+%",
                "projectile_damage_+%", "spell_damage_+%", "elemental_damage_+%", "fire_damage_+%" })
                fixture.Set(stat, 0);
        double baseline = Program.SupportedProjectileHit(basic.Conn, basic.Skill, area: false).MinTotal;
        Assert.True(baseline > 0);
        basic.Set("area_damage_+%", 100);
        Assert.Equal(baseline, Program.SupportedProjectileHit(basic.Conn, basic.Skill, area: false).MinTotal, 5);
        Assert.Equal(baseline * 2, Program.SupportedProjectileHit(basic.Conn, basic.Skill, area: true).MinTotal, 5);
        Assert.Equal(baseline, Program.SupportedProjectileHit(linked.Conn, linked.Skill, area: false).MinTotal, 5);
        var gem = Method("SupportedSkill").Invoke(null, [linked.Conn, linked.Skill])!;
        var supports = (IReadOnlyList<GemSupports.Applied>)gem.GetType().GetProperty("Supports")!.GetValue(gem)!;
        int more = supports.SelectMany(s => s.Stats)
            .Single(s => s.Key == Program.Stats.Key("support_area_concentrate_area_damage_+%_final")).Value;
        Assert.True(more > 0);
        linked.Set("area_damage_+%", 100);
        Assert.Equal(baseline * 2 * (1 + more / 100d),
            Program.SupportedProjectileHit(linked.Conn, linked.Skill, area: true).MinTotal, 5);
    }
}

public class ProjectileAreaDamageTests
{
    [Fact]
    public async Task Gateway_selects_area_snapshot_and_multiplies_area_and_projectile_factors_once()
    {
        await using var f = new ProjectileRuntimeFixture();
        var target = ProjectileRuntimeFixture.Monster(40, 10);
        f.Conn.Monsters.Add(42, target);
        var offense = new ProjectileOffense(DamagePacket.Of(DamageType.Fire, 10, 10), true, 0, 0, 150, "stages")
            { AreaHit = DamagePacket.Of(DamageType.Fire, 20, 20) };
        var profile = new ProjectileProfile("Metadata/Projectiles/Fireball")
        {
            Speed = 100, Range = 100, Radius = 1,
            EndArea = new("terminal", 10, Multiplier: .5)
        };
        f.Session.Runtime.Interactions.Add(new(1, ProjectileInteractionKind.Augment, new(20, 0), new(20, 20))
            { DamageMultiplier = 2 });
        f.Release(f.Shot(profile, offense));
        f.Advance(1);
        Assert.Equal(80u, target.Life);
    }
}
