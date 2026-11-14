using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

// The map overlay's "X monsters remain" line, fed by S→C 0x1a0 (InstanceProtocol.MonstersRemaining has the client side:
// one byte, instance+0xd7cd, the three thresholds, and the /raw checks that pin the wire). The client never counts
// monsters itself, so this byte is the whole line: it is 0xff in every new instance and only changes when a server sends
// it again. Therefore: once when the player's area is entered, with its packs already placed, and then on every event
// that changes how many monsters of the area are alive — a kill (Game.MonsterKilled, and the two places that commit one
// themselves: MinionOutcomes' PlayerKill and ProjectileReactions.CommitProjectileKill), a monster added (SpawnMonster: a
// summon, a kept instance's monster), a buried one rising (Emerge.cs, it only then joins the roster) and a removal
// (SendWorldRemoval). Never on a timer: nothing here runs on its own.
public static partial class Game
{
    /// <summary>
    /// Whether a monster still counts for the overlay. A corpse does not: the roster keeps the dead (`SpawnedMonster.Dead`
    /// or its life at 0) until it is consumed or the area is left, so the count is not <c>Monsters.Count</c>. This is the
    /// living test the area's own checks use (<c>!m.Dead &amp;&amp; m.Life &gt; 0</c>: the area-clear and quest guards), the
    /// same one <c>/mobs</c> lists a monster as living by (that command is the gm-commands mod, outside this repository),
    /// so the overlay cannot disagree with them about what is dead.
    /// </summary>
    public static bool MonsterLiving(SpawnedMonster m) => !m.Dead && m.Life > 0;

    /// <summary>The instance's living monsters (<see cref="MonsterLiving"/>'s rule), under the Monsters lock.</summary>
    public static int LivingMonstersLocked(ClientState conn) => conn.Monsters.Values.Count(MonsterLiving);

    /// <summary>
    /// Holds the recount while an area's population is placed: PopulateArea puts a whole area into the roster one monster
    /// at a time (a kept instance's, a Sirus arena's, a Maven ring's), and neither the player nor the overlay wants every
    /// step of that — one count after the last one is what the entry's 0x1a0 sends.
    /// </summary>
    public static IDisposable HoldMonstersRemaining(ClientState conn)
    {
        lock (conn.Monsters) conn.MonstersRemainingHolds++;
        return new RemainingHold(conn);
    }

    sealed class RemainingHold(ClientState conn) : IDisposable
    {
        public void Dispose() { lock (conn.Monsters) conn.MonstersRemainingHolds--; }
    }

    /// <summary>
    /// Sends the map overlay's count (S→C 0x1a0) when it changed since this client's last one, and to the other clients
    /// of the instance, whose overlay shows the same roster: what one of them kills, all of them count. <paramref
    /// name="force"/> is for the area's entry, where the client's byte is 0xff again whatever the last area was sent.
    /// </summary>
    public static async Task SendRemainingMonsters(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        bool force = false)
    {
        int living, total;
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || conn.Closed || conn.LeavingArea || !conn.PlayerSent) return;
            if (conn.MonstersRemainingHolds > 0 && !force) return;
            living = LivingMonstersLocked(conn);
            total = conn.Monsters.Count;
            if (!force && conn.SentMonstersRemaining == living) return;
            conn.SentMonstersRemaining = living;
        }
        byte[] bytes = InstanceProtocol.MonstersRemaining(living);
        // Each send encrypts in place, so every recipient needs its own copy (Agents/docs/protocol/14-monster-packs.md §2,
        // "Shared delivery must copy plaintext per recipient"). Outbox is the other connection's own send (Program.Send).
        foreach (var peer in conn.Instance?.Presence.PlayersExcept(conn.PeerId) ?? [])
        {
            if (peer.Owner is not { Outbox: { } outbox } other || ReferenceEquals(other, conn)) continue;
            lock (other.Monsters)
            {
                if (other.Closed || other.LeavingArea || !other.PlayerSent || other.MonstersRemainingHolds > 0) continue;
                other.SentMonstersRemaining = living;
            }
            _ = outbox((byte[])bytes.Clone(), null);
        }
        await Send(remote, stream, ciphers, bytes, $"0x01a0 monsters remaining {living} of {total}");
    }
}
