using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ProjectileSkillProfileTests
{
    public ProjectileSkillProfileTests() => TestSaves.Ensure();
    static Func<string, int> Stats(params (string Name, int Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;
    static ProjectileProfile Profile(string effect, params (string Name, int Value)[] values) =>
        GatewayProjectileSkillProfiles.Resolve(effect,
            effect == "Fireball" ? "Metadata/Projectiles/Fireball" : "Metadata/Projectiles/WandProjectile",
            Stats(values), 100, effect != "Fireball") with { Radius = 1 };
    static ProjectileEvent[] Areas(ProjectileRuntimeProbe w) => w.Events.Where(e => e.Kind == ProjectileEventKind.Area).ToArray();

    [Theory]
    [InlineData("KineticBlast", 4, 20)]
    public void KB_uses_fixed_scaled_circle_and_thirtieth_second_area_slots(string effect, int count, int placement)
    {
        var w = new ProjectileRuntimeProbe();
        w.Release(Profile(effect, ("skill_area_of_effect_+%", 300)) with { Range = 20 });
        w.Advance(.2 - 1d / ProjectileRuntime.QuantaPerSecond); Assert.Empty(Areas(w));
        w.Advance(.2); Assert.Single(Areas(w));
        w.Advance(.2 + 1d / 30 - 1d / ProjectileRuntime.QuantaPerSecond); Assert.Single(Areas(w));
        w.Advance(.2 + 1d / 30); Assert.Equal(2, Areas(w).Length);
        w.Advance(1);
        var areas = Areas(w); Assert.Equal(count, areas.Length);
        Assert.Equal(Enumerable.Range(0, count).Select(i => ProjectileRuntime.Time(.2) + i * 4000), areas.Select(e => e.At));
        Assert.All(areas, e => Assert.Equal(placement * 2, (e.Position - new ProjectileVector(30, 10)).Length, 7));
        double previous = Math.Atan2(areas[0].Position.Y - 10, areas[0].Position.X - 30), turn = 0;
        foreach (var area in areas.Skip(1))
        {
            double angle = Math.Atan2(area.Position.Y - 10, area.Position.X - 30);
            double delta = (angle - previous + Math.Tau) % Math.Tau;
            Assert.InRange(delta, double.Epsilon, 2 * Math.Tau / count); turn += delta; previous = angle;
        }
        Assert.True(turn < Math.Tau);
    }

}

public partial class OwnAuraGatewayTests
{
}
