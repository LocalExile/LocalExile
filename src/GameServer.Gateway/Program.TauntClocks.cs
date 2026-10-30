using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    static Task<bool> SendTauntBuffs(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, FlaskGrounds.Ground ground) => SendGenerated(remote, stream, ciphers, () =>
    {
        lock (conn.Monsters)
        {
            if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.Paused || !conn.FlaskGrounds.Contains(ground)) return null;
            double now = conn.Clock.Elapsed.TotalSeconds;
            var packets = new List<byte>();
            foreach (var (id, buff) in ground.Taunted.ToArray())
            {
                var clock = ground.TauntClocks[id];
                var activeCurse = ActiveMonsterTemporalChains(conn, id, now);
                int other = activeCurse?.OtherEffects ?? 0;
                // One accepted source snapshot supplies both the server rate and the native prefix.
                clock.ChangeRate(now, Math.Max(.25, 1 + other / 100.0));
                bool shown = ground.PresentedTauntDeadlines.TryGetValue(id, out double deadline);
                if (!conn.Monsters.TryGetValue(id, out var monster) || monster.Dead || monster.Life == 0
                    || !ground.TauntRecipients.TryGetValue(id, out var recipient) || !ReferenceEquals(monster, recipient)
                    || clock.Remaining <= 0)
                {
                    if (shown) packets.AddRange(InstanceEntities.RemoveBuff(id, buff));
                    ground.Taunted.Remove(id);
                    if (monster is null || monster.Dead || monster.Life == 0
                        || !ground.TauntRecipients.TryGetValue(id, out var original) || !ReferenceEquals(monster, original))
                        ground.TauntClocks.Remove(id);
                    ground.TauntRecipients.Remove(id);
                    ground.PresentedTauntDeadlines.Remove(id);
                    continue;
                }
                if (shown && deadline == clock.Deadline) continue;
                if (shown) packets.AddRange(InstanceEntities.RemoveBuff(id, buff));
                ushort next = shown ? conn.NextBuff++ : buff;
                ground.Taunted[id] = next;
                // Only the recipient's elapsed input is published here; other monster stats retain their own owners.
                packets.AddRange(InstanceEntities.StatsUpdate(id,
                    [(Stats.Key("buff_time_passed_+%_other_than_temporal_chains")!.Value, other)]));
                // Recovery-marked row 408 requires its two GrantedStats words in table order.
                packets.AddRange(InstanceEntities.AddBuff(id, next, FlaskGrounds.TauntedBuffRow, PlayerId,
                    values: [PlayerId, 1], u68: BitConverter.SingleToUInt32Bits((float)clock.Remaining)));
                ground.PresentedTauntDeadlines[id] = clock.Deadline;
            }
            return packets.Count == 0 ? null : packets.ToArray();
        }
    }, "current taunt recipient clocks");
}
