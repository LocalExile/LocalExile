using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // The overlay counts enemies still in the area, including those waiting to emerge, never friendly summons.
    public static bool MonsterLiving(SpawnedMonster m) => m.Hostile && !m.Dead && m.Life > 0;

    public static int LivingMonstersLocked(ClientState conn) =>
        conn.Monsters.Values.Count(MonsterLiving) + conn.DormantMonsters.Values.Count(MonsterLiving)
        + HeldBackLocked(conn).Count;

    sealed class RemainingDelivery
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Holds;
    }
    static readonly RuleTable<object, RemainingDelivery> RemainingDeliveries = new();
    static RemainingDelivery RemainingOf(ClientState conn) =>
        RemainingDeliveries.GetValue((object?)conn.Instance ?? conn, _ => new());

    // Population is shared by the instance. A second entrant cannot force a partial count while it is being built.
    public static IDisposable HoldMonstersRemaining(ClientState conn)
    {
        var delivery = RemainingOf(conn);
        lock (conn.Monsters) delivery.Holds++;
        return new RemainingHold(conn, delivery);
    }

    sealed class RemainingHold(ClientState conn, RemainingDelivery delivery) : IDisposable
    {
        bool disposed;
        public void Dispose()
        {
            lock (conn.Monsters)
                if (!disposed) { disposed = true; delivery.Holds--; }
        }
    }

    public static async Task SendRemainingMonsters(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, bool force = false)
    {
        var delivery = RemainingOf(conn);
        // Concurrent deaths and arrivals share this gate, so an older snapshot cannot overwrite a newer count.
        List<ClientState>? cleared = null;
        await delivery.Gate.WaitAsync();
        try
        {
            int living;
            ClientState[] recipients;
            lock (conn.Monsters)
            {
                if (!CurrentGameWork || conn.Closed || conn.LeavingArea || !conn.PlayerSent || delivery.Holds > 0) return;
                living = Math.Min(byte.MaxValue, LivingMonstersLocked(conn));
                recipients = [conn, .. (conn.Instance?.Presence.PlayersExcept(conn.PeerId) ?? [])
                    .Select(p => p.Owner).OfType<ClientState>().Where(p => !ReferenceEquals(p, conn))];
            }
            foreach (var recipient in recipients)
            {
                bool own = ReferenceEquals(recipient, conn);
                int previousLiving;
                lock (conn.Monsters)
                {
                    if (!CurrentGameWork || recipient.Closed || recipient.LeavingArea || !recipient.PlayerSent
                        || !ReferenceEquals(recipient.Instance, conn.Instance)
                        || (!(force && own) && recipient.SentMonstersRemaining == living)) continue;
                    previousLiving = recipient.SentMonstersRemaining;
                }
                // Encryption mutates the bytes. Construct plaintext independently for every recipient.
                byte[] bytes = InstanceProtocol.MonstersRemaining(living);
                if (own)
                    await Send(remote, stream, ciphers, bytes, $"0x01a0 monsters remaining {living}");
                else if (recipient.Outbox is { } outbox)
                {
                    try { await outbox(bytes, null); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { continue; }
                }
                else continue;
                lock (conn.Monsters) recipient.SentMonstersRemaining = living;
                if (previousLiving > 0 && living == 0) (cleared ??= []).Add(recipient);
            }
        }
        finally { delivery.Gate.Release(); }
        if (cleared is not null)
            foreach (var recipient in cleared)
                await CheckFullClearAchievements(recipient);
    }
}
