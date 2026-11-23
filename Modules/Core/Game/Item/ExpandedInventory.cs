using GameServer.Core.Protocol.Instance;

static partial class Game
{
    internal const uint ExpandedInventoryStat = Stat.PrimalistAscendancyExpandedMainInventory;

    // The grant is the real source value, never the presentation-only recovery flag below.
    internal static bool ExpandedInventoryGranted(Player player)
    {
        if (!(ExpandedInventoryStat is var key)) return false;
        long value = StatSourceValues(player).GetValueOrDefault(key);
        if (GameEvents.Stats.Any)
            foreach (var (name, amount) in GameEvents.RunStats(player))
                if (name == ExpandedInventoryStat) value += amount;
        return value > 0;
    }

    internal static int ExpandedInventoryMode(bool granted, bool occupied) => granted ? 1 : occupied ? 2 : 0;

    internal static int ExpandedInventoryMode(Player player) =>
        ExpandedInventoryMode(ExpandedInventoryGranted(player), ItemsOf(player).Items(PlayerInventories.ExpandedInventory).Count > 0);

    // Keep saved contents accessible after a mod unload/removal. This does not grant permission to deposit.
    internal static void KeepExpandedInventoryVisible(Player player, IDictionary<uint, int> stats)
    {
        if (ExpandedInventoryStat is var key
            && ItemsOf(player).Items(PlayerInventories.ExpandedInventory).Count > 0
            && (!stats.TryGetValue(key, out int value) || value <= 0)) stats[key] = 1;
    }

    // A stat presentation owns publication: enabling/disabling a stat mod needs no relog or mod-owned timer.
    internal static async Task SyncExpandedInventory(Player player)
    {
        if (!CurrentGameRules || player.Closed || player.LeavingArea || !player.Connection.PlayerSent) return;
        int mode = ExpandedInventoryMode(player);
        if (player.ExpandedInventoryMode == mode) return;
        if (!(ExpandedInventoryStat is var key)) return;
        await Send(player, [], null, atGate: () =>
        {
            if (!CurrentGameRules || player.Closed || player.LeavingArea) return null;
            player.ExpandedInventoryMode = mode;
            SetStats(player, player.Id, [(key, mode == 0 ? 0 : 1)]);
            return null;
        });
        if (mode == 2 && player.Commands is { } commands)
            await commands.Reply("Extra backpack is in recovery mode: take its items out; deposits are disabled until its enabling mod is active again.");
    }
}
