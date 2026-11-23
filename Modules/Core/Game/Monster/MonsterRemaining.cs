using GameServer.Core.Protocol.Login;

// The map overlay's "X monsters remain" line is fed by one byte on the client (instance+0xd7cd), which the instance
// constructor starts at 0xff and which S->C 0x1a0 alone writes: the client never counts monsters itself, so with nothing
// sent every area reads "More than 50 monsters remain". This sends it: the area's living monsters (corpses do not count,
// the roster keeps them), clamped to 255. S->C 0x1a0 is the message id with one payload byte.
public static partial class Game
{
    const ushort MonstersRemainingId = 0x1a0;
    const int RemainingEveryMs = 400;

    // What this connection was last sent: the count, the instance it was for, and when. A new area gets its own send
    // whatever the count is, because the client's byte is 0xff again there.
    sealed class SentRemaining
    {
        public ulong Instance;
        public int Count = -1;
        /// <summary>The most living monsters this run had: a map whose count never rose above zero was never placed.</summary>
        public int Peak;
        public DateTime At = DateTime.MinValue;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ClientState, SentRemaining> SentRemainings = new();

    // A kill, a summon or an area entry: recount and send when it changed.
    internal static async Task SendMonstersRemaining(ClientState? conn)
    {
        if (conn is not { PlayerSent: true, Closed: false, LeavingArea: false }) return;
        var sent = SentRemainings.GetOrCreateValue(conn);
        int living;
        lock (conn.Monsters) living = conn.Monsters.Values.Count(m => !m.Dead && m.Life > 0);
        lock (sent)
        {
            sent.At = DateTime.UtcNow;
            if (sent.Instance != conn.InstanceNumber) (sent.Instance, sent.Peak) = (conn.InstanceNumber, 0);
            if (living > sent.Peak) sent.Peak = living;
            // The map device's open map, once its boss is dead: this run is done and the portals read " (Completed)"
            // (Portals.RunBossKilled). Read from the roster rather than from the kill, so a boss killed before this code
            // was loaded, or by a minion or a projectile, still counts as soon as the player is in the instance.
            if (MapDevice.OpenOf(CharacterName(conn))?.Area.Id is { } openArea && openArea == conn.Layout.Id && MapBossDead(conn))
                MarkRunBossKilled(conn, openArea);
            if (sent.Count == living && sent.Instance == conn.InstanceNumber) return;
            sent.Count = living;
        }
        byte[] bytes = new LoginWriter().U16(MonstersRemainingId).U8((byte)Math.Min(Math.Max(living, 0), 255)).ToArray();
        await Send(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, bytes, $"0x01a0 monsters remaining {living}");
    }

    // The tick: the entry's send, and anything a kill hook missed (a monster that left the roster, a summon). Cheap
    // enough at four recounts a second, and it sends nothing while the count holds.
    internal static async Task TickMonstersRemaining(ClientState? conn)
    {
        if (conn is not { PlayerSent: true, Closed: false, LeavingArea: false }) return;
        var sent = SentRemainings.GetOrCreateValue(conn);
        bool fresh;
        lock (sent) fresh = sent.Instance != conn.InstanceNumber || DateTime.UtcNow - sent.At > TimeSpan.FromMilliseconds(RemainingEveryMs);
        if (!fresh) return;
        await SendMonstersRemaining(conn);
    }
}
