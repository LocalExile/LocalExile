using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using static Program;

static class TattoosFeature
{
    /// <summary>
    /// Applies a tattoo to an allocated passive. This is the server-side pilot path used before the client's
    /// tattoo-use packet is traced: the client command names the tattoo and the passive graph id. The tattoo is
    /// saved independently of the allocation, so refunding the passive keeps it.
    /// </summary>
    internal static async Task<string?> ApplyTattoo(Player player, string name, ushort node, byte? consumeRow = null, uint consumeItemId = 0,
        ushort? chosenKeystone = null)
    {
        var tree = PassivesFor(player);
        if (TattooCatalogue.Find(name) is not { } entry) return $"no tattoo \"{name}\"";
        if (tree.Find(node) is not { } passive) return $"unknown passive {node}";
        if (!player.Character.Passives.Contains(node) && !player.Character.MainAscendancy.Allocated.Contains(node))
            return $"{passive.Name} ({node}) is not allocated";
        if (!TargetMatches(passive, entry.Target)) return $"{entry.Name} replaces {entry.Target}, not {passive.Name} ({node})";
        if (player.Character.Tattoos.GetValueOrDefault(node) == entry.Name) return $"{passive.Name} ({node}) already has {entry.Name}";
        bool forbidden = ForbiddenTattoo.IsForbidden(entry.Name);
        if (!forbidden && TattooStats.For(entry.Name) is null) return $"{entry.Name} has no server effect yet";
        int totalLimit = ItemsFor(player).Limits.Get("tattoo_total", SystemSettings.Default.TattooTotalLimit);
        if (!entry.IsRunegraft && !player.Character.Tattoos.ContainsKey(node) && totalLimit > 0
            && player.Character.Tattoos.Values.Count(name => TattooCatalogue.Find(name)?.IsRunegraft != true) >= totalLimit)
            return $"a character can have at most {totalLimit} tattoos";
        if (entry.EventOnly && !forbidden) return $"{entry.Name} is event-only and is not supported yet";

        var next = new Dictionary<ushort, string>(player.Character.Tattoos) { [node] = entry.Name };
        if (TattooLimitRefusal(next, node, entry, ItemsFor(player).Limits) is { } limitError) return limitError;
        var nextKeystones = new Dictionary<ushort, ushort>(player.Character.TattooKeystones);
        bool journeyMakanui = entry.Name == "Journey Tattoo of Makanui";
        JourneyMakanuiCatalogue.Entry? chosen = null;
        ushort? grantedNotable = null;
        if (journeyMakanui)
        {
            if (chosenKeystone is ushort requested)
            {
                chosen = JourneyMakanuiCatalogue.FindByGraphId(requested);
                if (chosen is null) return $"unknown keystone {requested}";
            }
            else chosen = JourneyMakanuiCatalogue.Pick(node, Game.AreaRng(player));
            if (chosen is null) return "no random keystone available";
            nextKeystones[node] = chosen.GraphId;
        }
        else if (forbidden)
        {
            // "Grants a random <Class> Ascendancy Notable": one of the class's non-multiple-choice notables other than
            // the replaced one and the character's other allocated notables (a duplicate would count its stats twice);
            // the pick is saved so a relog cannot reroll it.
            grantedNotable = ForbiddenTattoo.Pick(entry.Name, node, player.Character.MainAscendancy.Allocated, Game.AreaRng(player));
            if (grantedNotable is not ushort notable) return "no random ascendancy notable available";
            nextKeystones[node] = notable;
        }
        else nextKeystones.Remove(node);
        var items = ItemsOf(player);
        PlayerInventories.Consumption? taken = null;
        if (consumeRow is byte row)
        {
            var used = Saves.Atomically(() =>
            {
                if (items.ConsumeOne(row, consumeItemId) is not { } consumed) return null;
                CharacterSaves.SetTattoos(CharacterName(player), next, nextKeystones);
                return consumed;
            });
            if (used is not { } consumed) return "the tattoo item disappeared";
            taken = consumed;
        }
        else CharacterSaves.SetTattoos(CharacterName(player), next, nextKeystones);
        player.Character.Tattoos = next;
        player.Character.TattooKeystones = nextKeystones;
        player.Character.TattooRevision++;
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.ApplyMovementSpeed();
        if (taken is { } usedStack && consumeRow is byte usedRow)
        {
            uint inventory = PlayerInventories.InventoryId(usedRow);
            await Send(player, InstanceItems.InventoryChange(inventory, items.NextRevision(usedRow), [consumeItemId],
                usedStack.Left is { } left ? [left] : []),
                $"0x00bf inventory {inventory}: tattoo used" + (usedStack.Left is { } now ? $", {now.Item.StackSize} left" : ", stack empty"));
        }
        await SendCharacterStats(player, PlayerStatChange.Build);
        await SendPassives(player);
        Console.WriteLine($"[gateway] {player.Connection.Remote} tattoo {entry.Name} on {node} {passive.Name} ({player.Character.Tattoos.Count} applied" +
            (chosen is { } chosenEntry ? $", keystone {chosenEntry.Name})"
                : grantedNotable is ushort grantedId && tree.Find(grantedId) is { } granted ? $", notable {granted.Name})" : ")"));
        return null;
    }

    /// <summary>The client's tattoo-on-passive packet (C→S 0x77): consume one tattoo from the inventory and install
    /// it on the named allocated passive. The packet carries the inventory row, the item id and the passive graph id.</summary>
    internal static async Task UseTattooOnPassive(Player player, InstanceItems.UseItemOnPassive use, string hex)
    {
        if (PlayerInventories.Row(use.InventoryId) is not byte row)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0077 tattoo use: unknown inventory {use.InventoryId}: {hex}");
            return;
        }
        var items = ItemsOf(player);
        if (items.Items(row).FirstOrDefault(i => i.Id == use.ItemId) is not { } item)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0077 tattoo use: no item {use.ItemId} in inventory {use.InventoryId}: {hex}");
            return;
        }
        if (ItemsFor(player).ByHash(item.Item.TypeHash) is not { } type)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0077 unknown item type {use.ItemId}: {hex}");
            return;
        }
        // The same 0x77 packet is used for an Orb of Scouring on a tattooed passive: the item decides whether the
        // passive is tattooed or restored.
        if (type.Id == CurrencyEffects.OrbOfScouring)
        {
            string? removed = await RemoveTattoo(player, use.Node, row, use.ItemId);
            if (removed is not null)
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0077 scouring passive {use.Node} refused: {removed}: {hex}");
            return;
        }
        if (TattooCatalogue.Find(type.Name) is not { } entry)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0077 item {use.ItemId} is not a tattoo: {hex}");
            return;
        }
        string? error = await ApplyTattoo(player, entry.Name, use.Node, row, use.ItemId);
        if (error is not null)
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0077 tattoo {entry.Name} on {use.Node} refused: {error}: {hex}");
    }

    static readonly byte[] TattooRows = [PlayerInventories.MainInventory, PlayerInventories.ExpandedInventory];

    /// <summary>C→S 0x78: repeat / Shift-click use of a Tattoo or Orb of Scouring named by BaseItemTypes HASH32.
    /// Locates the first matching stack in player inventories (main inventory first) and delegates to 0x77.</summary>
    internal static async Task UseTattooTypeOnPassive(Player player, InstanceItems.UseItemTypeOnPassive use, string hex)
    {
        var items = ItemsOf(player);
        foreach (byte row in TattooRows)
        {
            if (items.Items(row).FirstOrDefault(i => i.Item.TypeHash == use.TypeHash && items.RowOf(i.Id) == row) is { } stack)
            {
                await UseTattooOnPassive(player,
                    new(PlayerInventories.InventoryId(row), stack.Id, use.Node), hex);
                return;
            }
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0078 passive item type 0x{use.TypeHash:x8} on node {use.Node}: none left in inventory");
    }

    /// <summary>Removes the tattoo from a passive and restores the passive's own stats.</summary>
    internal static async Task<string?> RemoveTattoo(Player player, ushort node, byte? consumeRow = null, uint consumeItemId = 0)
    {
        if (PassivesFor(player).Find(node) is not { } passive) return $"unknown passive {node}";
        if (!player.Character.Tattoos.ContainsKey(node)) return $"{passive.Name} ({node}) has no tattoo";
        var next = new Dictionary<ushort, string>(player.Character.Tattoos);
        next.Remove(node);
        var nextKeystones = new Dictionary<ushort, ushort>(player.Character.TattooKeystones);
        nextKeystones.Remove(node);
        var items = ItemsOf(player);
        PlayerInventories.Consumption? taken = null;
        if (consumeRow is byte row)
        {
            var used = Saves.Atomically(() =>
            {
                if (items.ConsumeOne(row, consumeItemId) is not { } consumed) return null;
                CharacterSaves.SetTattoos(CharacterName(player), next, nextKeystones);
                return consumed;
            });
            if (used is not { } consumed) return "the currency item disappeared";
            taken = consumed;
        }
        else CharacterSaves.SetTattoos(CharacterName(player), next, nextKeystones);
        player.Character.Tattoos = next;
        player.Character.TattooKeystones = nextKeystones;
        player.Character.TattooRevision++;
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.ApplyMovementSpeed();
        if (taken is { } usedStack && consumeRow is byte usedRow)
        {
            uint inventory = PlayerInventories.InventoryId(usedRow);
            await Send(player, InstanceItems.InventoryChange(inventory, items.NextRevision(usedRow), [consumeItemId],
                usedStack.Left is { } left ? [left] : []),
                $"0x00bf inventory {inventory}: tattoo removed" + (usedStack.Left is { } now ? $", {now.Item.StackSize} left" : ", stack empty"));
        }
        await SendCharacterStats(player, PlayerStatChange.Build);
        await SendPassives(player);
        Console.WriteLine($"[gateway] {player.Connection.Remote} tattoo removed from {node} {passive.Name} ({player.Character.Tattoos.Count} applied)");
        return null;
    }

}
