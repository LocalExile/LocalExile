using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public partial class SpellNoDamageTests
{
    // Live 2026-10-29: Tornado Shot had no projectile (its row is TornadoShotArrow, not named after the effect), so every
    // use was refused while the client had stopped to shoot, and the server's walk went on to the last click.
    [Fact]
    public async Task Tornado_Shot_fires_its_arrow_and_stops_the_walk()
    {
        using var f = new Fixture("TornadoShot", "Crude Bow");
        Assert.Equal(("Metadata/Projectiles/TornadoShotArrow", 2173), Program.Skills.ProjectileFor(f.Effect));
        var start = f.Conn.Walk.Position(f.Conn.Clock.Elapsed.TotalSeconds);
        var click = (start.X, Y: start.Y + 40);
        f.Conn.Walk.Order(click.X, click.Y, f.Conn.Clock.Elapsed.TotalSeconds);
        await Task.Delay(100);

        var use = new LoginWriter().U16(InstanceProtocol.UseSkillAtLocationId)
            .U32((uint)(click.X - 20)).U32((uint)click.Y).U16(f.Skill).U16(7).U16(0x408).ToArray();
        await Program.OnClientMessages("spell-no-damage-test", f.Stream, f.Ciphers,
            [(InstanceProtocol.UseSkillAtLocationId, use)], [], f.Conn).WaitAsync(TimeSpan.FromSeconds(5));

        double now = f.Conn.Clock.Elapsed.TotalSeconds;
        var stop = f.Conn.Walk.Destination;
        Assert.NotEqual(click, stop);
        Assert.Equal(stop, f.Conn.Walk.Position(now + 5));
    }
}
