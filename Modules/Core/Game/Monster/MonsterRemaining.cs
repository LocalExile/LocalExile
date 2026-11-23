using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    // The overlay counts enemies still in the area, including those waiting to emerge, never friendly summons.
    public static bool MonsterLiving(SpawnedMonster m) => m.Hostile && !m.Dead && m.Life > 0;

    public static int LivingMonstersLocked(Player player) =>
        player.Area.Monsters.Values.Count(MonsterLiving) + player.Area.DormantMonsters.Values.Count(MonsterLiving)
        + HeldBackLocked(player).Count;

    sealed class RemainingDelivery
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Holds;
    }
    static readonly RuleTable<object, RemainingDelivery> RemainingDeliveries = new();
    static RemainingDelivery RemainingOf(Player player) =>
        RemainingDeliveries.GetValue((object?)player.Instance ?? player, _ => new());

    // Population is shared by the instance. A second entrant cannot force a partial count while it is being built.
    public static IDisposable HoldMonstersRemaining(Player player)
    {
        var delivery = RemainingOf(player);
        delivery.Holds++;
        return new RemainingHold(delivery);
    }

    sealed class RemainingHold(RemainingDelivery delivery) : IDisposable
    {
        bool disposed;
        public void Dispose()
        {
            if (!disposed) { disposed = true; delivery.Holds--; }
        }
    }

    public static async Task SendRemainingMonsters(Player player, bool force = false)
    {
        var delivery = RemainingOf(player);
        // Concurrent deaths and arrivals share this gate, so an older snapshot cannot overwrite a newer count.
        List<Player>? cleared = null;
        await delivery.Gate.WaitAsync();
        try
        {
            int living;
            Player[] recipients;
            if (!CurrentGameWork || player.Closed || player.LeavingArea || !player.Connection.PlayerSent || delivery.Holds > 0) return;
            living = Math.Min(byte.MaxValue, LivingMonstersLocked(player));
            recipients = [player, .. (player.Instance?.Presence.PlayersExcept(player.Id) ?? [])
                .Where(p => !ReferenceEquals(p, player))];
            foreach (var recipient in recipients)
            {
                bool own = ReferenceEquals(recipient, player);
                if (!CurrentGameWork || recipient.Closed || recipient.LeavingArea || !recipient.Connection.PlayerSent
                    || !ReferenceEquals(recipient.Instance, player.Instance)
                    || (!(force && own) && recipient.SentMonstersRemaining == living)) continue;
                int previousLiving = recipient.SentMonstersRemaining;
                // Encryption mutates the bytes. Construct plaintext independently for every recipient.
                byte[] bytes = InstanceProtocol.MonstersRemaining(living);
                if (own)
                    await Send(player, bytes, $"0x01a0 monsters remaining {living}");
                else if (recipient.Connection.Outbox is { } outbox)
                {
                    try { await outbox(bytes, null); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { continue; }
                }
                else continue;
                recipient.SentMonstersRemaining = living;
                if (previousLiving > 0 && living == 0) (cleared ??= []).Add(recipient);
            }
        }
        finally { delivery.Gate.Release(); }
        if (cleared is not null)
            foreach (var recipient in cleared)
                await CheckFullClearAchievements(recipient);
    }
}
