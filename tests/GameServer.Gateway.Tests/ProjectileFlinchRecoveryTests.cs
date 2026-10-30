using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

// A projectile hit that flinches a monster commits the native Actor TakeHit recovery (0x1d1), as the melee path does.
// A rig-only clip (0x1b2) leaves the client monster walking while the server's estimate has stopped.
public class ProjectileFlinchRecoveryTests
{
    const uint TargetId = 42;

    static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    static byte[] ObjectHeader(ushort id, uint objectId) =>
        new GameServer.Core.Protocol.Login.LoginWriter().U16(id).U32(objectId).U32(0).U16(0).ToArray();

    // The fixture's target with one known TakeHit clip (0.6 s), so a flinch is possible and its duration is fixed.
    static SpawnedMonster Flinching(int x, int y) =>
        new("target", new(x, y, 10, width: 300, height: 300), 0, 2, 100) { HitAnimations = [new HitAnimation(7, 0.6)] };

    [Fact]
    public async Task A_flinching_projectile_hit_sends_native_take_hit_recovery_not_a_rig_clip()
    {
        await using var f = new ProjectileRuntimeFixture();
        var monster = Flinching(50, 10);
        f.Conn.Monsters.Add(TargetId, monster);

        f.Release();
        f.Advance(4.5);   // speed 10 from x=10: contact at x=50 after 4 s
        byte[] wire = await f.Read();

        Assert.True(IndexOf(wire, InstanceProtocol.LifeUpdate(TargetId, 90)) >= 0, "the hit lands for 10");
        Assert.True(IndexOf(wire, InstanceProtocol.ActorAnimation(TargetId, 0.6f, PlayerHitRecovery.TakeHitAnimation)) >= 0,
            "0x1d1 TakeHit recovery for the clip's 0.6 s");
        Assert.Equal(-1, IndexOf(wire, ObjectHeader(InstanceProtocol.AnimationUpdateId, TargetId)));
        Assert.True(monster.FlinchUntil > 4.0, "the server holds the monster's AI for the same recovery");
    }

    [Fact]
    public async Task A_scratch_does_not_flinch_or_send_any_animation()
    {
        await using var f = new ProjectileRuntimeFixture();
        var monster = Flinching(50, 10);
        f.Conn.Monsters.Add(TargetId, monster);

        f.Release(f.Shot(offense: new ProjectileOffense(DamagePacket.Of(DamageType.Physical, 5, 5), true, int.MaxValue, 0, 150, "scratch")));
        f.Advance(4.5);
        byte[] wire = await f.Read();

        Assert.True(IndexOf(wire, InstanceProtocol.LifeUpdate(TargetId, 95)) >= 0);
        Assert.Equal(-1, IndexOf(wire, ObjectHeader(InstanceProtocol.ActorAnimationId, TargetId)));
        Assert.Equal(-1, IndexOf(wire, ObjectHeader(InstanceProtocol.AnimationUpdateId, TargetId)));
        Assert.Equal(double.NegativeInfinity, monster.FlinchUntil);
    }
}
