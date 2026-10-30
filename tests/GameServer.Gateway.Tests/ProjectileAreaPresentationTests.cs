using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class ProjectileAreaPresentationTests
{
    [Theory]
    [InlineData(false)]
    public async Task Native_area_art_scales_to_the_detonation_radius_for_both_presentation_routes(bool callback)
    {
        const string ao = "Metadata/Effects/Spells/ri_fire_white/rifire_white.ao";
        await using var f = new ProjectileRuntimeFixture();
        var shot = (ProjectileOffense)f.Shot().Payload!;
        var presentation = new Program.ProjectilePresentation
        {
            EventEffect = callback ? value => value.Kind == ProjectileEventKind.Area ? ao : null : null
        };
        f.Release(f.Shot(new("synthetic-area") { Speed = 100, Range = 20, EndArea = new("burst", 40) },
            shot with { AreaVisual = callback ? null : ao, Presentation = presentation }));
        await f.Read();
        f.Advance(.2);
        byte[] bytes = await f.Read();
        float expectedScale = (float)(40 * (250d / 23) / 200);
        byte[] expected = new EffectObject(30, 10, ao) { Scale = expectedScale }.Blob();
        Assert.True(bytes.AsSpan().IndexOf(expected) >= 0);
    }

}
