using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    /// <summary>Distance beyond which an open NPC, stash or bench interaction closes on movement.
    /// Always exceeds <see cref="GameServer.Core.World.PlayerWalk.InWalkUpRange"/> (2 * size + 3) so
    /// walking up to an object or standing within interaction reach never closes it prematurely.</summary>
    public static int InteractionCloseRange(int objectSize) => Math.Max(22, 2 * Math.Max(0, objectSize) + 12);

    static bool BeyondCloseRange((int X, int Y) here, (int X, int Y)? movingTo, (int X, int Y) anchor, int objectSize)
    {
        double limit = InteractionCloseRange(objectSize);
        double limitSq = limit * limit;
        return DistanceSq(here, anchor) > limitSq || (movingTo is { } dest && DistanceSq(dest, anchor) > limitSq);

        static double DistanceSq((int X, int Y) a, (int X, int Y) b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }
    }

    /// <summary>Fast check for whether any NPC, stash, crafting bench, or module window interaction is open.</summary>
    public static bool HasOpenInteraction(Player player) =>
        player.TalkingTo != 0 || player.Sale is not null || player.Shop is not null
        || player.StashOpen || player.GuildStashOpen || player.TradeStashOpen || player.BenchOpen
        || player.ModuleWindow is not null;

    /// <summary>Closes any open NPC dialogue/shop/sale, stash, crafting bench, or module window state once the player
    /// moves beyond interaction reach. NPC sales close with S→C 0x00b9 + 0x0084 object 0 and return offered
    /// items; NPC dialogue and shops close with S→C 0x0084 object 0. Personal/guild stash, crafting bench and
    /// module windows have no close-only S→C 0x7b packet (0x140dcae70 toggles kind 1; 0x140dcb010 and 0x140dd8e80 only open),
    /// so their server state and bench balance inventory are cleared cleanly while the client closes locally.</summary>
    public static async Task CloseInteractionsIfMovingAway(Player player, (int X, int Y)? movingTo, double at)
    {
        if (player.Closed || player.LeavingArea || !player.Connection.PlayerSent) return;
        if (!HasOpenInteraction(player)) return;
        if (Interlocked.CompareExchange(ref player.InteractionClosing, 1, 0) != 0) return;
        try
        {
            var here = player.Walk.Position(at);
            bool closeNpc = false, hadSale = false;
            lock (player.Npcs)
            {
                bool hasNpcUi = player.Sale is not null || player.Shop is not null || player.TalkingTo != 0;
                if (hasNpcUi)
                {
                    var npc = player.Sale?.Npc ?? player.Shop?.Npc
                        ?? (player.TalkingTo != 0 ? player.Npcs.GetValueOrDefault(player.TalkingTo) : null);
                    int size = npc is null ? 0 : Math.Max(0, StationaryCollision.Resolve(npc.Npc.Id, npc.Npc.Model).Size);
                    if (npc is null || BeyondCloseRange(here, movingTo, (npc.X, npc.Y), size))
                    {
                        closeNpc = true;
                        hadSale = player.Sale is not null;
                        player.TalkingTo = 0;
                        player.InNpcTalk = false;
                        player.NpcFinishFlag = null;
                        player.NpcFinishLeagueFlag = null;
                        player.NpcTalkScript = null;
                        player.NpcRewardOffer = null;
                        player.Shop = null;
                    }
                }
            }
            if (closeNpc)
            {
                await GameEvents.RunNpcDialogueClosing(player);
                if (!hadSale)
                    await Send(player, InstanceNpcs.Talk(0),
                        "0x0084 object 0: npc interaction closed (moved out of range)");
            }
            if (player.StashOpen && BeyondCloseRange(here, movingTo, player.StashAt, Player.StashBlockerSize))
            {
                player.StashOpen = false;
                Console.WriteLine($"[gateway] {player.Connection.Remote} personal stash closed (moved out of range)");
            }
            if (player.GuildStashOpen && BeyondCloseRange(here, movingTo, player.GuildStashAt, Player.StashBlockerSize))
            {
                player.GuildStashOpen = false;
                Console.WriteLine($"[gateway] {player.Connection.Remote} guild stash closed (moved out of range)");
            }
            if (player.TradeStashOpen && BeyondCloseRange(here, movingTo, player.TradeStashAt, Player.StashBlockerSize))
            {
                player.TradeStashOpen = false;
                Console.WriteLine($"[gateway] {player.Connection.Remote} async-trade stash closed (moved out of range)");
            }
            if (player.BenchOpen
                && BeyondCloseRange(here, movingTo, player.CraftingBenchAt, player.StationaryInteractionSize(player.CraftingBenchObject)))
            {
                player.BenchOpen = false;
                await RefreshBenchBalance(player, clear: true);
                Console.WriteLine($"[gateway] {player.Connection.Remote} crafting bench closed (moved out of range)");
            }
            if (player.ModuleWindow is { } openModule)
            {
                var anchor = player.ModuleWindowAnchor is { } existing && existing.Owner == openModule
                    ? (existing.X, existing.Y)
                    : (player.ModuleWindowAnchor = (openModule, here.X, here.Y)) is { } created
                        ? (created.X, created.Y)
                        : here;
                if (BeyondCloseRange(here, movingTo, anchor, 0))
                {
                    player.ModuleWindow = null;
                    player.ModuleWindowAnchor = null;
                    Console.WriteLine($"[gateway] {player.Connection.Remote} module window '{openModule}' closed (moved out of range)");
                }
            }
        }
        finally
        {
            Volatile.Write(ref player.InteractionClosing, 0);
        }
    }
}
