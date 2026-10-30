using System.Net.Sockets;
using GameServer.Core.Protocol.Login;

partial class Program
{
    static double ChargeExpiryRate(ClientState conn, ChargeSession session)
    {
        // Charge category 3 ignores buff-only/debuff-only inputs. Raw generic sources follow normal stat observation;
        // current received curse/Focus membership is read at each charge step, without rebuilding caster skill stats.
        Dictionary<uint, int> raw = [];
        if (Stats.Key("buff_time_passed_+%") is uint key) raw[key] = session.GenericExpiryIncrease;
        return RecipientEffectRate(conn, raw, 3);
    }

    static Task<bool> SendChargeBuffs(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ChargeSession session) => SendGenerated(remote, stream, ciphers, () =>
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.Paused) return null;
            if (conn.PlayerLife == 0) session.Die();
            // A queued ticker publication may outlive its charge. Expire before deriving any current charge stats.
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (session.State.Expire(now).Count > 0)
            {
                conn.Pools = PoolsFor(conn, conn.Pools.Level);
                conn.ApplyMovementSpeed();
            }
            _ = CharacterPlayerStats(conn);
            now = conn.Clock.Elapsed.TotalSeconds;
            session.State.SetExpiryRate(now, ChargeExpiryRate(conn, session));
            bool changed = session.State.Expire(now).Count > 0;
            foreach (var type in Charges.Types) changed |= session.State.Clamp(type, session.Limits(type));
            if (changed)
            {
                conn.Pools = PoolsFor(conn, conn.Pools.Level);
                conn.ApplyMovementSpeed();
            }
            var changes = session.Presentation(now, () => conn.NextBuff++);
            if (changes.Count == 0) return null;
            return [.. PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)),
                .. changes.SelectMany(c => ChargeBuffs.Packets(c, PlayerId)).SelectMany(p => p.Packet)];
        }
    }, "current charge counts and recipient clocks");
}
