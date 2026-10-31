using System.Net.Sockets;
using System.Collections.Frozen;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class CommandsRules
{
    // Dev commands (/item_mod, /rarity, /sockets): changes the held item and replaces it on the cursor (0xbf removing and adding
    // its id). The item needs the component the change is about.
    internal static async Task ChangeHeldItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, Func<Item, Item> change, string what,
        string component = "Mods")
    {
        var items = ItemsOf(conn);
        if (items.Items(PlayerInventories.Cursor) is not [var held, ..]) { await conn.Commands!.Reply("Hold an item first"); return; }
        if (!held.Item.Components.Contains(component)) { await conn.Commands!.Reply($"{ItemsFor(conn).ByHash(held.Item.TypeHash)?.Name} has no {component} component"); return; }
        if (items.Change(PlayerInventories.Cursor, held.Id, change) is not { } changed) return;
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [held.Id], [changed]), $"0x00bf cursor: item {held.Id} {what}");
        await conn.Commands!.Reply(what);
    }

    internal static ModSection? ParseSection(string text) => text.ToLowerInvariant() switch
    {
        "implicit" or "imp" => ModSection.Implicit,
        "explicit" or "exp" => ModSection.Explicit,
        "enchant" or "ench" => ModSection.Enchant,
        "scourge" or "s10" => ModSection.Scourge,
        "hidden" or "s11" => ModSection.Hidden,
        _ => null,
    };
}
