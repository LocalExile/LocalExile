using System.Collections.Concurrent;

public static partial class Game
{
    sealed record RampageTransfer(string Character, DateTime At, RampageState.Carried State);
    static readonly ConcurrentDictionary<uint, RampageTransfer> CarriedRampage = new();

    public static void CarryRampage(Player conn, uint ticket)
    {
        lock (conn.Monsters)
        {
            if (!CurrentRampageWork || conn.Closed || conn.RageRetired) return;
            CharacterPlayerStats(conn);
            double at = conn.RampageTransferAt ?? ProjectileWorldTime(conn);
            var state = conn.Rampage.Carry(at);
            if (conn.OwnedCombatState is RampageWorld world) world.Disable();
            foreach (var (old, entry) in CarriedRampage)
                if (DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) CarriedRampage.TryRemove(old, out _);
            CarriedRampage.TryAdd(ticket, new(CharacterName(conn), DateTime.UtcNow, state));
        }
    }

    public static void RestoreRampage(Player conn)
    {
        lock (conn.Monsters)
        {
            if (!CurrentRampageWork || conn.Closed || conn.RageRetired || !CarriedRampage.TryGetValue(conn.Ticket, out var entry)
                || entry.Character != CharacterName(conn)
                || !CarriedRampage.TryRemove(new KeyValuePair<uint, RampageTransfer>(conn.Ticket, entry))) return;
            if (DateTime.UtcNow - entry.At <= TimeSpan.FromMinutes(5) && conn.PlayerLife > 0 && RampageSource(conn))
                conn.Rampage.Restore(entry.State, ProjectileWorldTime(conn));
        }
    }
}
