using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

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

    /// <summary>Closes any open NPC dialogue/shop/sale, stash or crafting bench state once the player
    /// moves beyond interaction reach. NPC sales close with S→C 0x00b9 + 0x0084 object 0 and return offered
    /// items; NPC dialogue and shops close with S→C 0x0084 object 0. Personal/guild stash and crafting bench
    /// have no close-only S→C 0x7b packet (0x140dcae70 toggles kind 1; 0x140dcb010 and 0x140dd8e80 only open),
    /// so their server state and bench balance inventory are cleared cleanly while the client closes locally.</summary>
    public static async Task CloseInteractionsIfMovingAway(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, (int X, int Y)? movingTo, double at)
    {
        if (conn.Closed || conn.LeavingArea || !conn.PlayerSent) return;
        if (conn.TalkingTo == 0 && conn.Sale is null && conn.Shop is null
            && !conn.StashOpen && !conn.GuildStashOpen && !conn.BenchOpen) return;
        if (Interlocked.CompareExchange(ref conn.InteractionClosing, 1, 0) != 0) return;
        try
        {
            var here = conn.Walk.Position(at);
            bool closeNpc = false, hadSale = false;
            lock (conn.Npcs)
            {
                bool hasNpcUi = conn.Sale is not null || conn.Shop is not null || conn.TalkingTo != 0;
                if (hasNpcUi)
                {
                    var npc = conn.Sale?.Npc ?? conn.Shop?.Npc
                        ?? (conn.TalkingTo != 0 ? conn.Npcs.GetValueOrDefault(conn.TalkingTo) : null);
                    int size = npc is null ? 0 : Math.Max(0, StationaryCollision.Resolve(npc.Npc.Id, npc.Npc.Model).Size);
                    if (npc is null || BeyondCloseRange(here, movingTo, (npc.X, npc.Y), size))
                    {
                        closeNpc = true;
                        hadSale = conn.Sale is not null;
                        conn.TalkingTo = 0;
                        conn.InNpcTalk = false;
                        conn.NpcFinishFlag = null;
                        conn.NpcTalkScript = null;
                        conn.NpcRewardOffer = null;
                        conn.Shop = null;
                    }
                }
            }
            if (closeNpc)
            {
                await GameEvents.RunNpcDialogueClosing(conn);
                if (!hadSale)
                    await Send(remote, stream, ciphers, InstanceNpcs.Talk(0),
                        "0x0084 object 0: npc interaction closed (moved out of range)");
            }
            if (conn.StashOpen && BeyondCloseRange(here, movingTo, conn.StashAt, ClientState.StashBlockerSize))
            {
                conn.StashOpen = false;
                Console.WriteLine($"[gateway] {remote} personal stash closed (moved out of range)");
            }
            if (conn.GuildStashOpen && BeyondCloseRange(here, movingTo, conn.GuildStashAt, ClientState.StashBlockerSize))
            {
                conn.GuildStashOpen = false;
                Console.WriteLine($"[gateway] {remote} guild stash closed (moved out of range)");
            }
            if (conn.BenchOpen
                && BeyondCloseRange(here, movingTo, conn.CraftingBenchAt, conn.StationaryInteractionSize(conn.CraftingBenchObject)))
            {
                conn.BenchOpen = false;
                await RefreshBenchBalance(remote, stream, ciphers, conn, clear: true);
                Console.WriteLine($"[gateway] {remote} crafting bench closed (moved out of range)");
            }
        }
        finally
        {
            Volatile.Write(ref conn.InteractionClosing, 0);
        }
    }
}
