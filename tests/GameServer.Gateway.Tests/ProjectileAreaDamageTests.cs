using GameServer.Core.Projectiles;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class SpellNoDamageTests
{
    [Theory]
    [InlineData(false)]
    public async Task Direct_root_capture_retains_area_snapshot_for_later_area_stages(bool childArea)
    {
        using var f = new Fixture("Spark", targetX: 60);
        await using var runtime = new ProjectileRuntimeFixture(f.Conn);
        f.Set("area_damage_+%", 10000);
        var gems = (IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)
            Method("GemSkillEffects").Invoke(null, [f.Conn])!;
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

}

public class ProjectileAreaDamageTests
{
}
