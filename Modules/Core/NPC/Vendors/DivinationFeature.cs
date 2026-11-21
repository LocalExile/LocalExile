using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static Program;

static class DivinationFeature
{
    const string Owner = "Vendors.Divination";
    const byte Row = InstanceDivination.InventoryRow;
    static readonly Store<Player, Session> Sessions = new();
    sealed class Session(uint id, SpawnedNpc npc)
    {
        public uint Id => id;
        public SpawnedNpc Npc => npc;
    }

    internal static void Load(IModuleContext module)
    {
        GameEventOwners.Own(module.Manifest.Id, new Cleanup(() =>
        {
            foreach (var (player, _) in Sessions) Close(player);
            Sessions.Clear();
        }));
        GameEvents.NpcOption.Add(module.Manifest.Id, async talk =>
        {
            if (talk.Category != "TradeDivinationCards") return false;
            await Open(talk.Player);
            return true;
        });
        GameEvents.NpcTalk.Add(module.Manifest.Id, (player, _, _) =>
        {
            Close(player);
            return Task.FromResult(false);
        });
        GameEvents.NpcDialogueClosing.Add(module.Manifest.Id, player =>
        {
            Close(player);
            return Task.CompletedTask;
        });
        GameEvents.InventoryPick.Add(module.Manifest.Id, (player, pick) =>
            Task.FromResult(pick.InventoryId == PlayerInventories.InventoryId(Row) && !Current(player)));
        GameEvents.InventoryPlace.Add(module.Manifest.Id, (player, place) =>
            Task.FromResult(place.InventoryId == PlayerInventories.InventoryId(Row) && !CanDeposit(player)));
        GameEvents.Packet(InstanceItems.ItemActionId).Add(module.Manifest.Id, (player, bytes) =>
        {
            var move = InstanceItems.ReadItemAction(bytes);
            return Task.FromResult(move.InventoryId == PlayerInventories.InventoryId(Row) && !CanDeposit(player));
        });
        GameEvents.Packet(InstanceDivination.TurnInId).Add(module.Manifest.Id, async (player, _) =>
        {
            var changed = TurnIn(player);
            if (changed is not null)
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(Row),
                    ItemsOf(player).NextRevision(Row), [changed.Id], [changed]), "divination card set exchanged");
            return true;
        });
        GameEvents.Packet(InstanceDivination.MoveId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            foreach (var change in Move(player, InstanceDivination.ReadMove(bytes)))
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(change.Row),
                    ItemsOf(player).NextRevision(change.Row), change.Removed, change.Added), "NPC divination Ctrl click");
            return true;
        });
    }

    internal static bool Current(Player player) => !player.Closed && !player.LeavingArea && player.PlayerLife > 0
        && player.Sale is null && player.Trade is null
        && player.ModuleWindow == Owner && Sessions.TryGetValue(player, out var session)
        && player.TalkingTo == session.Id && player.Npcs.TryGetValue(session.Id, out var npc)
        && ReferenceEquals(npc, session.Npc);

    static bool CanDeposit(Player player) => Current(player)
        && ItemsOf(player).Items(PlayerInventories.Cursor).FirstOrDefault() is { } held
        && ItemsFor(player).ByHash(held.Item.TypeHash)?.Class == "DivinationCard";

    static void Close(Player player)
    {
        Sessions.Remove(player);
        if (player.ModuleWindow == Owner) player.ModuleWindow = null;
    }

    internal static async Task Open(Player player)
    {
        Close(player);
        uint id = player.TalkingTo;
        if (!player.Npcs.TryGetValue(id, out var npc)
            || !Game.Npcs.TalksOf(npc.Npc).Any(t => t.Category == "TradeDivinationCards")) return;
        if (player.Sale is not null)
            await VendorsFeature.CloseSale(player, accepted: false);
        if (player.Closed || player.LeavingArea || !player.Npcs.TryGetValue(id, out var current)
            || !ReferenceEquals(npc, current)) return;
        player.TalkingTo = id;
        player.Shop = null;
        player.BenchOpen = false;
        player.InNpcTalk = false;
        player.ModuleWindow = Owner;
        Sessions.Add(player, new(id, npc));
        await Send(player, InstanceDivination.Open(), "open NPC divination window");
    }

    static Task Send(Player player, byte[] packet, string label) =>
        Program.Send(player, packet, label);

    sealed class Cleanup(Action run) : IDisposable { public void Dispose() => run(); }

    internal sealed record Change(byte Row, IReadOnlyList<uint> Removed, IReadOnlyList<InventoryItem> Added);

    internal static IReadOnlyList<Change> Move(Player player, InstanceDivination.Move request)
    {
        // Worked out before the transaction: the check builds the character's stats under conn.Monsters, and that lock
        // comes before the save lock (Saves.cs), never inside it.
        bool expandedAllowed = !request.Expanded || request.IntoWindow && Game.ExpandedInventoryGranted(player);
        return Saves.Atomically<IReadOnlyList<Change>>(() => MoveLocked(player, request, expandedAllowed));
    }

    static IReadOnlyList<Change> MoveLocked(Player player, InstanceDivination.Move request, bool expandedAllowed)
    {
        if (!Current(player) || !expandedAllowed) return [];
        var items = ItemsOf(player);
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
            if (ItemsFor(player).ByHash(clicked.Item.TypeHash) is not { Class: "DivinationCard" } type
                || clicked.Item.StackSize < 1 || clicked.Item.StackSize > type.MaxStack) return [];
            var existing = items.Items(Row);
            if (existing.Count > 1) return [];
            if (existing.FirstOrDefault() is { } slot)
            {
                if (ItemsFor(player).ByHash(slot.Item.TypeHash)?.Class == "DivinationCard")
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

    internal static InventoryItem? TurnIn(Player player, Random? random = null) => Saves.Atomically(() =>
    {
        if (!Current(player)) return null;
        var items = ItemsOf(player);
        var slot = items.Items(Row);
        if (slot.Count != 1 || items.Offered().Any(i => i.Id == slot[0].Id)
            || ItemsFor(player).ByHash(slot[0].Item.TypeHash) is not { Class: "DivinationCard" } card
            || DivinationRewards.Find(card.Id) is not { Enabled: true } reward
            || card.MaxStack != reward.Stack || slot[0].Item.StackSize != reward.Stack) return null;
        // Every refusal precedes the only persistent mutation. A reward with missing data never consumes the cards.
        var result = DivinationRewards.Create(reward, ItemsFor(player), UniquesFor(player), RollsFor(player),
            random ?? AreaRng(player), player.Pools.Level);
        return result is null ? null : items.Change(Row, slot[0].Id, _ => result);
    });
}
