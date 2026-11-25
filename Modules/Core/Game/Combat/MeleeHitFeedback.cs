using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // The native hit AO owns the impact mesh, particles and sound layers. A predictive pathing attack need not
    // remain in Actor+1b0 when its result arrives. Explicit presentation uses an unbound immediate result so a
    // retained action cannot also play this skill's impact (0x141e977f0 / 0x141e97e20).
    internal static string? MeleeImpactArt(string? effect) => effect is "HeavyStrike" or "HeavyStrikeAltX" or "HeavyStrikeAltY"
        ? "Metadata/Effects/Spells/heavy_strike_02/heavy_strike_hit.ao" : null;

    internal static byte[] MeleeHitFeedback(uint attacker, ushort sequence, uint target, byte code, byte angle,
        bool critical, string? impactArt, uint orderKey = 0, ushort checkpoint = 0,
        SpawnedMonster.NativeKnockback? knockback = null)
    {
        ushort flags = (ushort)(0x4200 | HitTypeFlag(code) | (critical ? 0x20 : 0));
        byte kind = 3;
        ApplyNativeKnockback(knockback, ref flags, ref kind, ref angle, out ushort u3c, out ushort u3e);
        return InstanceProtocol.ActionHit(attacker, impactArt is null ? sequence : (ushort)0xffff, target,
            flags: flags, kind: kind, code: code, angle: angle, u3c: u3c, u3e: u3e,
            orderKey: orderKey, checkpoint: checkpoint);
    }

    static async Task PresentMeleeImpact(Player player, string art, (int X, int Y) from, (int X, int Y) at, float scale = 1)
    {
        var session = ProjectilesFor(player);
        uint id;
        if (!session.Active || player.LeavingArea) return;
        id = player.TakeObjectId();
        player.TrackProjectileObject(id);
        var effect = new EffectObject(at.X, at.Y, art)
        {
            AnimatedFlags = 9, Flags0 = EffectObject.FixedRotation, Scale = scale,
            Rotation = (float)Math.Atan2(at.X - from.X, from.Y - at.Y)
        };
        bool created = await SendWorldCreate(player, id,
            () => session.Active ? InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), effect.Blob()) : null,
            "native melee impact", wake: true);
        if (!created) { player.UntrackProjectileObject(id); return; }
        if (!session.Active) return;
        // Existing connection scheduler/outbox: no per-hit task, timer or worker.
        long until = Math.Max(session.Runtime.Now, ProjectileRuntime.Time(AreaTime(player)))
            + ProjectileRuntime.Time(2);
        session.Runtime.Schedule(until, () => session.WorldRecords(id, () =>
        {
            player.UntrackProjectileObject(id);
            return [InstanceProtocol.RemoveObject(id)];
        }, remove: true));
    }
}
