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
    }

    internal static bool Current(ClientState conn) => !conn.Closed && !conn.LeavingArea && conn.PlayerLife > 0
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

    internal static InventoryItem? TurnIn(ClientState conn, Random? random = null) => Saves.Atomically(() =>
    {
        if (!Current(conn)) return null;
        var items = ItemsOf(conn);
        var slot = items.Items(Row);
        if (slot.Count != 1 || ItemsFor(conn).ByHash(slot[0].Item.TypeHash) is not { Class: "DivinationCard" } card
            || DivinationRewards.Find(card.Id) is not { Enabled: true } reward
            || card.MaxStack != reward.Stack || slot[0].Item.StackSize != reward.Stack) return null;
        // Every refusal precedes the only persistent mutation. A reward with missing data never consumes the cards.
        var result = DivinationRewards.Create(reward, ItemsFor(conn), UniquesFor(conn), RollsFor(conn),
            random ?? Random.Shared, conn.Pools.Level);
        return result is null ? null : items.Change(Row, slot[0].Id, _ => result);
    });
}
