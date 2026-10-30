using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// Live 2026-10-29 (Frida): the client spawns the projectiles of its own gem skill use itself, every one of a volley, so
// a server object for them showed a second fireball. The server keeps simulating them but sends no object.
public partial class SpellNoDamageTests
{
    // S→C 0x2b9 with this object type: u16 id, entity header (u32, u32, u16), then the u32 type hash (big-endian).
    static bool CreatesObject(byte[] wire, string metadata)
    {
        uint type = InstanceEntities.ObjectTypeHash(metadata);
        for (int i = 0; i + 16 <= wire.Length; i++)
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(wire.AsSpan(i)) == InstanceEntities.CreateObjectId
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(wire.AsSpan(i + 12)) == type) return true;
        return false;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_client_predicted_projectile_gets_no_server_object(bool predicted)
    {
        await using var runtime = new ProjectileRuntimeFixture();
        var offense = new ProjectileOffense(DamagePacket.Of(DamageType.Physical, 10, 10), true, int.MaxValue, 0, 150, "fixture")
            { ClientPredicted = predicted };
        runtime.Release(runtime.Shot(offense: offense));
        runtime.Advance(0.2);
        Assert.Equal(!predicted, CreatesObject(await runtime.Read(), "Metadata/Projectiles/DefaultArrow"));
    }

    // A spell's projectiles are the client's own; an attack gem's are not (Frida 2026-10-31: Burning Arrow's action created
    // no projectile, and without a server object none was seen).
    [Theory]
    [InlineData("Fireball", "Driftwood Wand", true)]
    [InlineData("BurningArrow", "Crude Bow", false)]
    public async Task Spell_gem_projectiles_are_client_predicted_and_attack_gem_projectiles_are_not(string skill, string weapon, bool predicted)
    {
        using var f = new Fixture(skill, weapon);
        await using var runtime = new ProjectileRuntimeFixture(f.Conn);
        var gems = (IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)
            Method("GemSkillEffects").Invoke(null, [f.Conn])!;
        // The use's critical roll is captured at paid admission (CastGemSkill) and travels with the projectile.
        var roll = Method("CaptureSkillCriticalRoll").Invoke(null, [f.Conn, f.Skill])!;
        var use = (GameServer.Core.Projectiles.ProjectileUse)Method("CaptureGemProjectile").Invoke(null,
            [f.Conn, runtime.Session, f.Skill, gems.Single(g => g.Id == f.Skill), (100, 32), roll])!;
        Assert.Equal(predicted, Assert.IsType<ProjectileOffense>(use.Payload).ClientPredicted);
    }
}
