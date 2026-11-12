using GameServer.Core.Protocol.Instance;

static partial class Game
{
    internal const string ExpandedInventoryStat = "primalist_ascendancy_expanded_main_inventory";

    // The grant is the real source value, never the presentation-only recovery flag below.
    internal static bool ExpandedInventoryGranted(ClientState conn)
    {
        if (Stats.Key(ExpandedInventoryStat) is not uint key) return false;
        long value = CharacterStatSources(conn).Stats.GetValueOrDefault(key);
        if (GameEvents.Stats.Any)
            foreach (var (name, amount) in GameEvents.RunStats(conn))
                if (name == ExpandedInventoryStat) value += amount;
        lock (conn.Monsters)
            return conn.PlayerStats.TryGetValue(key, out int replacement) ? replacement > 0 : value > 0;
    }

    internal static int ExpandedInventoryMode(bool granted, bool occupied) => granted ? 1 : occupied ? 2 : 0;

    internal static int ExpandedInventoryMode(ClientState conn) =>
        ExpandedInventoryMode(ExpandedInventoryGranted(conn), ItemsOf(conn).Items(PlayerInventories.ExpandedInventory).Count > 0);

    // Keep saved contents accessible after a mod unload/removal. This does not grant permission to deposit.
    internal static void KeepExpandedInventoryVisible(ClientState conn, IDictionary<uint, int> stats)
    {
        if (Stats.Key(ExpandedInventoryStat) is uint key
            && ItemsOf(conn).Items(PlayerInventories.ExpandedInventory).Count > 0
            && (!stats.TryGetValue(key, out int value) || value <= 0)) stats[key] = 1;
    }

    // The ordinary player tick owns publication: enabling/disabling a stat mod needs no relog or mod-owned timer.
    internal static async Task SyncExpandedInventory(ClientState conn)
    {
        if (!CurrentGameRules || conn.Closed || conn.LeavingArea || !conn.PlayerSent) return;
        int mode = ExpandedInventoryMode(conn);
        if (conn.ExpandedInventoryMode == mode) return;
        var wire = conn.Wire;
        if (Stats.Key(ExpandedInventoryStat) is not uint key) return;
        await Send(wire.Remote, wire.Stream, wire.Ciphers, [], null, atGate: () =>
        {
            lock (conn.Monsters)
            {
                if (!CurrentGameRules || conn.Closed || conn.LeavingArea) return null;
                conn.ExpandedInventoryMode = mode;
                return InstanceEntities.StatsUpdate(PlayerId, [(key, mode == 0 ? 0 : 1)]);
            }
        });
        if (mode == 2 && conn.Commands is { } commands)
            await commands.Reply("Extra backpack is in recovery mode: take its items out; deposits are disabled until its enabling mod is active again.");
    }
}
