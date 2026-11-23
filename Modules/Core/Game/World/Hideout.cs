using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using PlayerWalk = GameServer.Core.World.PlayerWalk;

public static partial class Game
{
    public static async Task RefreshBenchBalance(Player player, bool clear = false)
    {
        clear |= !LeagueFor(player).Rules.CraftingBench;
        if (clear && !player.BenchBalanceSent) return;
        IReadOnlyList<InventoryItem> snapshot = clear ? [] : Saves.Atomically(() => BenchBalance.Snapshot(PersonalStashOf(player), Items, Bench));
        byte[] packet;
        if (!player.BenchBalanceSent)
        {
            packet = InstanceInventories.AddInventory(InstanceBench.BalanceInventoryId, InstanceBench.BalanceSlot, 0, snapshot);
            player.BenchBalanceSent = true;
        }
        else
            packet = InstanceItems.InventoryChange(InstanceBench.BalanceInventoryId, ++player.BenchBalanceRevision,
                [.. player.BenchBalanceItems.Select(i => i.Id)], snapshot);
        player.BenchBalanceItems = snapshot;
        await Send(player, packet, $"bench stash balance: {snapshot.Count} currency stack(s), {snapshot.Sum(i => (long)i.Item.StackSize)} units");
    }

    // A click on the guild stash (C→S 0x24): walk there, then its tab list (S→C 0x136 kind 1) and its panel (0x7b kind 2).
    // While it is open the stash messages (0xc7, 0x28, 0x2a, Ctrl + click …) act on it (StashOf); the personal stash's open
    // switches back.
    public static async Task OpenGuildStash(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (player.Character.Ssf || player.GuildStashObject == 0) return;
        if (!TryBeginObjectWalk(player, requestedAt, player.GuildStashAt.X, player.GuildStashAt.Y, Player.StashBlockerSize, out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0024 guild stash ({use}) from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (!PlayerActionCurrent(player, action)) return;
        player.StashOpen = false;
        player.BenchOpen = false;
        player.GuildStashOpen = true;
        var guild = GuildStashOf(player);
        var tabs = guild.Records(null, player.Connection.GuildStashTabsSent, ItemsFor(player));
        await Send(player, InstanceStash.TabList(tabs, InstanceStash.Guild), $"0x0136 guild stash tab list: {tabs.Count} tab(s)");
        await Send(player, InstanceWaypoints.OpenUi(InstanceStash.OpenGuildStashUi, player.GuildStashObject, InstanceStash.NoAffinity),
            "0x007b open the guild stash", still: () => PlayerActionCurrent(player, action));
    }
}
