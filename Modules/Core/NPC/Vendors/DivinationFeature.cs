using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static Program;

static class DivinationFeature
{
    const string Owner = "Vendors.Divination";
    const byte Row = InstanceDivination.InventoryRow;
    static readonly RuleTable<ClientState, Session> Sessions = new();
    sealed class Session(uint id, SpawnedNpc npc)
    {
        public uint Id => id;
        public SpawnedNpc Npc => npc;
    }

    internal static void Load(IModuleContext module)
    {
        GameEventOwners.Own(module.Manifest.Id, new Cleanup(() =>
        {
            foreach (var (conn, _) in Sessions) Close(conn);
            Sessions.Clear();
        }));
        GameEvents.NpcOption.Add(module.Manifest.Id, async talk =>
        {
            if (talk.Category != "TradeDivinationCards") return false;
            await Open(ConnOf(talk.Player));
            return true;
        });
        GameEvents.NpcTalk.Add(module.Manifest.Id, (conn, _, _) =>
        {
            Close(conn);
            return Task.FromResult(false);
        });
        GameEvents.NpcDialogueClosing.Add(module.Manifest.Id, conn =>
        {
            Close(conn);
            return Task.CompletedTask;
        });
        GameEvents.InventoryPick.Add(module.Manifest.Id, (conn, pick) =>
            Task.FromResult(pick.InventoryId == PlayerInventories.InventoryId(Row) && !Current(conn)));
        GameEvents.InventoryPlace.Add(module.Manifest.Id, (conn, place) =>
            Task.FromResult(place.InventoryId == PlayerInventories.InventoryId(Row) && !CanDeposit(conn)));
        GameEvents.Packet(InstanceItems.ItemActionId).Add(module.Manifest.Id, (conn, bytes) =>
        {
            var move = InstanceItems.ReadItemAction(bytes);
            return Task.FromResult(move.InventoryId == PlayerInventories.InventoryId(Row) && !CanDeposit(conn));
        });
        GameEvents.Packet(InstanceDivination.TurnInId).Add(module.Manifest.Id, async (conn, _) =>
        {
            var changed = TurnIn(conn);
            if (changed is not null)
                await Send(conn, InstanceItems.InventoryChange(PlayerInventories.InventoryId(Row),
                    ItemsOf(conn).NextRevision(Row), [changed.Id], [changed]), "divination card set exchanged");
            return true;
        });
        GameEvents.Packet(InstanceDivination.MoveId).Add(module.Manifest.Id, async (conn, bytes) =>
        {
            foreach (var change in Move(conn, InstanceDivination.ReadMove(bytes)))
                await Send(conn, InstanceItems.InventoryChange(PlayerInventories.InventoryId(change.Row),
                    ItemsOf(conn).NextRevision(change.Row), change.Removed, change.Added), "NPC divination Ctrl click");
            return true;
        });
    }

    internal static bool Current(ClientState conn) => !conn.Closed && !conn.LeavingArea && conn.PlayerLife > 0
        && conn.Sale is null && conn.Trade is null
        && conn.ModuleWindow == Owner && Sessions.TryGetValue(conn, out var session)
        && conn.TalkingTo == session.Id && conn.Npcs.TryGetValue(session.Id, out var npc)
        && ReferenceEquals(npc, session.Npc);

    static bool CanDeposit(ClientState conn) => Current(conn)
        && ItemsOf(conn).Items(PlayerInventories.Cursor).FirstOrDefault() is { } held
        && ItemsFor(conn).ByHash(held.Item.TypeHash)?.Class == "DivinationCard";

    static void Close(ClientState conn)
    {
        Sessions.Remove(conn);
        if (conn.ModuleWindow == Owner) conn.ModuleWindow = null;
    }

    internal static async Task Open(ClientState conn)
    {
        Close(conn);
        uint id = conn.TalkingTo;
        if (!conn.Npcs.TryGetValue(id, out var npc)
            || !Game.Npcs.Value.TalksOf(npc.Npc).Any(t => t.Category == "TradeDivinationCards")) return;
        if (conn.Sale is not null)
            await VendorsFeature.CloseSale(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, accepted: false);
        if (conn.Closed || conn.LeavingArea || !conn.Npcs.TryGetValue(id, out var current)
            || !ReferenceEquals(npc, current)) return;
        conn.TalkingTo = id;
        conn.Shop = null;
        conn.BenchOpen = false;
        conn.InNpcTalk = false;
        conn.ModuleWindow = Owner;
        Sessions.Add(conn, new(id, npc));
        await Send(conn, InstanceDivination.Open(), "open NPC divination window");
    }

    static Task Send(ClientState conn, byte[] packet, string label) =>
        Program.Send(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, packet, label);

    sealed class Cleanup(Action run) : IDisposable { public void Dispose() => run(); }

    internal sealed record Change(byte Row, IReadOnlyList<uint> Removed, IReadOnlyList<InventoryItem> Added);

    internal static IReadOnlyList<Change> Move(ClientState conn, InstanceDivination.Move request)
    {
        // Worked out before the transaction: the check builds the character's stats under conn.Monsters, and that lock
        // comes before the save lock (Saves.cs), never inside it.
        bool expandedAllowed = !request.Expanded || request.IntoWindow && Game.ExpandedInventoryGranted(conn);
        return Saves.Atomically<IReadOnlyList<Change>>(() => MoveLocked(conn, request, expandedAllowed));
    }

    static IReadOnlyList<Change> MoveLocked(ClientState conn, InstanceDivination.Move request, bool expandedAllowed)
    {
        if (!Current(conn) || !expandedAllowed) return [];
        var items = ItemsOf(conn);
        byte main = PlayerInventories.MainInventory;
        byte source = request.IntoWindow ? (request.Expanded ? PlayerInventories.ExpandedInventory : main) : Row;
        // An item offered in a sale or trade cannot move: the inventory refuses to remove it, so a move that went on would
        // copy it. That holds for the clicked card and for whatever lies in the window's slot.
        var offered = items.Offered().Select(i => i.Id).ToHashSet();
        if (items.Items(PlayerInventories.Cursor).Count != 0 || offered.Contains(request.ItemId)
            || items.Items(Row).Any(i => offered.Contains(i.Id))
            || items.Items(source).FirstOrDefault(i => i.Id == request.ItemId) is not { } clicked) return [];
        byte[] rows = [main, Row, PlayerInventories.ExpandedInventory];
        var before = rows.ToDictionary(r => r, items.Items);
        if (request.IntoWindow)
        {
            if (ItemsFor(conn).ByHash(clicked.Item.TypeHash) is not { Class: "DivinationCard" } type
                || clicked.Item.StackSize < 1 || clicked.Item.StackSize > type.MaxStack) return [];
            var existing = items.Items(Row);
            if (existing.Count > 1) return [];
            if (existing.FirstOrDefault() is { } slot)
            {
                if (ItemsFor(conn).ByHash(slot.Item.TypeHash)?.Class == "DivinationCard")
                {
                    if (slot.Item.TypeHash != clicked.Item.TypeHash || slot.Item.StackSize >= type.MaxStack) return [];
                    int take = Math.Min(clicked.Item.StackSize, type.MaxStack - slot.Item.StackSize);
                    items.SetStackSize(Row, slot.Id, slot.Item.StackSize + take);
                    if (take == clicked.Item.StackSize) items.Remove(source, clicked.Id);
                    else items.SetStackSize(source, clicked.Id, clicked.Item.StackSize - take);
                    return Changes();
                }
                // Returning the previous reward may use the cell freed by this card. Preview before either mutation.
                var preview = new PlayerInventories();
                foreach (var inMain in items.Items(main).Where(i => source != main || i.Id != clicked.Id)) preview.Put(main, inMain);
                if (Game.ShapeOf(slot.Item) is null || !preview.Fits(main, slot.Item, Game.ShapeOf)) return [];
                if (items.Remove(source, clicked.Id) is null || items.Remove(Row, slot.Id) is null)
                    throw new InvalidOperationException("Divination window item could not be removed");
                if (items.AddStacked(main, slot.Item, Game.ShapeOf).Left != 0)
                    throw new InvalidOperationException("Divination reward no longer fits after preview");
            }
            else if (items.Remove(source, clicked.Id) is null)
                throw new InvalidOperationException("Divination card could not be removed");
            items.Put(Row, clicked with { X = 0, Y = 0 });
            // The native slot listener submits 0x19a for a complete Ctrl-clicked set; moving alone never redeems it.
        }
        else
        {
            if (Game.ShapeOf(clicked.Item) is null) return [];
            var stacked = items.AddStacked(main, clicked.Item, Game.ShapeOf);
            if (!stacked.Any) return [];
            if (stacked.Left == 0) items.Remove(Row, clicked.Id);
            else items.SetStackSize(Row, clicked.Id, stacked.Left);
        }
        return Changes();

        IReadOnlyList<Change> Changes() => rows.Select(row =>
        {
            var after = items.Items(row);
            return new Change(row, before[row].Where(i => !after.Contains(i)).Select(i => i.Id).ToArray(),
                after.Where(i => !before[row].Contains(i)).ToArray());
        }).Where(c => c.Removed.Count + c.Added.Count > 0).ToArray();
    }

    internal static InventoryItem? TurnIn(ClientState conn, Random? random = null) => Saves.Atomically(() =>
    {
        if (!Current(conn)) return null;
        var items = ItemsOf(conn);
        var slot = items.Items(Row);
        if (slot.Count != 1 || items.Offered().Any(i => i.Id == slot[0].Id)
            || ItemsFor(conn).ByHash(slot[0].Item.TypeHash) is not { Class: "DivinationCard" } card
            || DivinationRewards.Find(card.Id) is not { Enabled: true } reward
            || card.MaxStack != reward.Stack || slot[0].Item.StackSize != reward.Stack) return null;
        // Every refusal precedes the only persistent mutation. A reward with missing data never consumes the cards.
        var result = DivinationRewards.Create(reward, ItemsFor(conn), UniquesFor(conn), RollsFor(conn),
            random ?? Random.Shared, conn.Pools.Level);
        return result is null ? null : items.Change(Row, slot[0].Id, _ => result);
    });
}
