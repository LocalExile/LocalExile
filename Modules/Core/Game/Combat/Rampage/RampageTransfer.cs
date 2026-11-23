using System.Collections.Concurrent;

public static partial class Game
{
    sealed record RampageTransfer(string Character, DateTime At, RampageState.Carried State);
    static readonly ConcurrentDictionary<uint, RampageTransfer> CarriedRampage = new();

    public static void CarryRampage(Player player, uint ticket)
    {
        if (!CurrentRampageWork || player.Closed || player.RageRetired) return;
        CharacterPlayerStats(player);
        double at = player.RampageTransferAt ?? ProjectileWorldTime(player);
        var state = player.Rampage.Carry(at);
        if (player.OwnedCombatState is RampageWorld world) world.Disable();
        foreach (var (old, entry) in CarriedRampage)
            if (DateTime.UtcNow - entry.At > TimeSpan.FromMinutes(5)) CarriedRampage.TryRemove(old, out _);
        CarriedRampage.TryAdd(ticket, new(CharacterName(player), DateTime.UtcNow, state));
    }

    public static void RestoreRampage(Player player)
    {
        if (!CurrentRampageWork || player.Closed || player.RageRetired || !CarriedRampage.TryGetValue(player.Ticket, out var entry)
            || entry.Character != CharacterName(player)
            || !CarriedRampage.TryRemove(new KeyValuePair<uint, RampageTransfer>(player.Ticket, entry))) return;
        if (DateTime.UtcNow - entry.At <= TimeSpan.FromMinutes(5) && player.PlayerLife > 0 && RampageSource(player))
            player.Rampage.Restore(entry.State, ProjectileWorldTime(player));
    }
}
