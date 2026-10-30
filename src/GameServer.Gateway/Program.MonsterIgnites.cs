using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

partial class Program
{
    /// <summary>BuffDefinitions row 0 <c>ignited</c> ("You're on fire", BuffVisuals 2: the burning flames).</summary>
    internal const ushort IgnitedBuffRow = 0;

    static void ReconcileMonsterIgniteRate(ClientState conn, uint id, SpawnedMonster target, double now)
    {
        var curse = ActiveMonsterTemporalChains(conn, id, now);
        target.Ignites.ChangeRate(now, curse is null ? 1 : Math.Max(.25, 1 + curse.OtherEffects / 100.0),
            curse?.Clock.Deadline ?? double.PositiveInfinity, MonsterDotFactor(target, DamageType.Fire));
    }

    // Called under the combat lock at an accepted projectile hit that dealt fire damage and left the monster alive: the
    // shot's ignite (MonsterIgnites.TryApply), and the monster's ignited buff, whose timer runs to its last ignite's end.
    internal static void IgniteOnHitLocked(ProjectileSession session, uint id, SpawnedMonster target,
        MonsterIgnites.Settings settings, bool critical, double now)
    {
        var e = target.Mods.Effects;
        ReconcileMonsterIgniteRate(session.Conn, id, target, now);
        if (!target.Ignites.TryApply(settings, critical, now, e["avoid_ignite_%"],
                e["immune_to_ignite"] != 0 || e["cannot_be_ignited"] != 0, session.Conn.IgniteRandom)) return;
        if (target.Ignites.Buff is ushort old) session.Packet(InstanceEntities.RemoveBuff(id, old));
        ushort buff = session.Conn.NextBuff++;
        target.Ignites.Buff = buff;
        session.Packet(() => MonsterIgniteStartPacket(session, id, target, buff),
            $"monster 0x{id:x} ({target.Label}) ignited{(critical ? " by a critical hit" : "")}");
    }

    static byte[]? MonsterIgniteStartPacket(ProjectileSession session, uint id, SpawnedMonster target, ushort buff)
    {
        var conn = session.Conn;
        if (!session.Active || conn.LeavingArea || conn.RageRetired || conn.Paused
            || !conn.Monsters.TryGetValue(id, out var current) || !ReferenceEquals(current, target)
            || target.Dead || target.Life == 0 || target.Ignites.Buff != buff) return null;
        double now = Math.Max(ProjectileWorldTime(conn), conn.Clock.Elapsed.TotalSeconds);
        // Always include both recipient terms, including zeros after a late curse expiry/removal. Build the prefix
        // before the add and keep it when no duration remains; presentation never drains owed burn damage.
        var prefix = MonsterCurseStats(conn, id, now);
        double remaining = target.Ignites.Remaining(now);
        return remaining <= 0 ? prefix : [.. prefix, .. InstanceEntities.AddBuff(id, buff, IgnitedBuffRow, PlayerId,
            u68: BitConverter.SingleToUInt32Bits((float)remaining))];
    }

    // One tick of the ignited monsters (RunPlayerTick): the strongest ignite's fire damage through the monster's
    // resistance, the buff off when the last ignite ends, and kills, as Righteous Fire's burn (BurnAround) commits them.
    static async Task BurnIgnited(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var hits = new List<(uint Id, SpawnedMonster M, uint Life, int Damage)>();
        var ended = new List<(uint Id, ushort Buff)>();
        using (ProjectileMutation(conn))
        {
            double now = ProjectileWorldTime(conn);
            foreach (var (id, m) in conn.Monsters)
            {
                if (!m.Ignites.Burning && !m.Ignites.HasPendingDamage && m.Ignites.Buff is null) continue;
                int damage = 0;
                if (m.Dead || m.Life == 0) m.Ignites.Clear();
                else
                {
                    ReconcileMonsterIgniteRate(conn, id, m, now);
                    damage = m.Ignites.Advance(now, MonsterDotFactor(m, DamageType.Fire));
                }
                if (!m.Ignites.Burning && m.Ignites.Buff is ushort gone) { ended.Add((id, gone)); m.Ignites.Buff = null; }
                if (damage == 0) continue;
                (m.Life, bool stageStarts) = TakeDamageLocked(m, damage);
                m.Aggro = m.Life > 0;
                if (m.Life == 0)
                {
                    m.Dead = true;
                    CollectVaalSoulAtDeathLocked(conn, m);
                    m.Ignites.Clear();
                    if (m.Ignites.Buff is ushort buff) { ended.Add((id, buff)); m.Ignites.Buff = null; }
                }
                hits.Add((id, m, m.Life, damage));
                if (stageStarts) CommitBossStage(remote, stream, ciphers, conn, id, m, now);
            }
        }
        foreach (var (id, buff) in ended)
            await Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(id, buff), null);
        foreach (var (id, m, life, damage) in hits)
        {
            if (life == 0)
                await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]), null);
            await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(id, life),
                life == 0 ? $"0x01b1 monster 0x{id:x} ({m.Label}) burnt to death by ignite" : null);
            if (life == 0) await MonsterKilled(remote, stream, ciphers, conn, id, m);
        }
    }
}
