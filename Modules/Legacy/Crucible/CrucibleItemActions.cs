using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Crucible;

/// <summary>
/// Handles item-level Crucible actions:
/// - C→S <c>0x0116</c> Orb of Scouring unallocation of the deepest allocated weapon passive node.
/// - <see cref="GameEvents.CurrencyApply"/> for <c>Magmatic Ore</c> (<c>add_weapon_passive_tree_experience</c>).
/// - <see cref="GameEvents.VendorQuote"/> for Crucible <c>SellBonus</c> vendor rewards.
/// </summary>
public static class CrucibleItemActions
{
    /// <summary>Handles C→S <c>0x0116</c> (Orb of Scouring unallocation of a weapon passive tree node).</summary>
    public static async Task<bool> HandleUnallocate(Player player, byte[] bytes)
    {
        var req = InstanceCrucible.ReadWeaponPassiveUnallocate(bytes);
        string remote = player.Connection.Remote;
        var items = ItemsOf(player);
        var stash = StashOf(player);

        if (!TryLocateItem(items, stash, req.CurrencyInventoryId, req.CurrencyItemId, out var curTab, out var curRow, out var curItem)
            || !TryLocateItem(items, stash, req.TargetInventoryId, req.TargetItemId, out var tgtTab, out var tgtRow, out var tgtItem))
        {
            Console.WriteLine($"[crucible] {remote} <= 0x0116 unallocate ({req.Column},{req.Row}): currency or target item not found");
            return true;
        }

        var currencyType = ItemsFor(player).ByHash(curItem.Item.TypeHash);
        if (currencyType?.Id != CurrencyEffects.OrbOfScouring)
        {
            Console.WriteLine($"[crucible] {remote} <= 0x0116 unallocate: currency is {currencyType?.Id ?? "?"}, expected Orb of Scouring");
            return true;
        }

        var updated = CrucibleTreeEngine.UnallocateWithScouring(tgtItem.Item, (int)req.Column, (int)req.Row);
        if (updated is null)
        {
            Console.WriteLine($"[crucible] {remote} <= 0x0116 unallocate ({req.Column},{req.Row}) on item {tgtItem.Id}: refused (corrupted / mirrored / vestigial / not deepest allocated node)");
            return true;
        }

        if (curTab is int pubCur && stash.IsPublic(pubCur)) return true;

        if (ItemMoves.Use(items, stash,
            new(curTab, curRow, curItem.Id),
            new(tgtTab, tgtRow, tgtItem.Id),
            updated, held: null) is not var (leftCurrency, changedTarget, _) || changedTarget is null)
            return true;
        if (tgtItem.Item.Foreseeing && player.ForesightRequest?.TargetId == tgtItem.Id)
            player.ForesightRequest = null;

        async Task SendPlace(int? tab, byte row, List<uint> gone, List<InventoryItem> now, string what)
        {
            if (tab is int t)
                await SendTabChange(player, stash, t, gone, now, $"0x00bf stash tab {t}: {what}");
            else
                await Send(player,
                    InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), gone, now),
                    $"0x00bf inventory {PlayerInventories.InventoryId(row)}: {what}");
        }

        List<InventoryItem> currencyNow = leftCurrency is { } l ? [l] : [];
        if (curTab == tgtTab && (curTab is not null || curRow == tgtRow))
        {
            await SendPlace(tgtTab, tgtRow, [curItem.Id, tgtItem.Id], [.. currencyNow, changedTarget],
                $"Crucible node ({req.Column},{req.Row}) unallocated with Orb of Scouring");
        }
        else
        {
            await SendPlace(curTab, curRow, [curItem.Id], currencyNow, "Orb of Scouring consumed (Crucible unallocate)");
            await SendPlace(tgtTab, tgtRow, [tgtItem.Id], [changedTarget],
                $"Crucible node ({req.Column},{req.Row}) unallocated");
        }

        if (tgtTab is null && IsEquipmentRow(tgtRow))
            await WorldOf(player).RefreshStats();

        Console.WriteLine($"[crucible] {CharacterName(player)} unallocated node ({req.Column},{req.Row}) on item {tgtItem.Id} with Orb of Scouring");
        return true;
    }

    /// <summary><see cref="GameEvents.CurrencyApply"/> handler for <c>Magmatic Ore</c>.</summary>
    public static Item? ApplyCurrency(Player player, Item target, ItemCatalog.BaseType currencyType, Item? currencyItem, Random random)
    {
        if (!currencyType.Id.Equals(CrucibleTables.MagmaticOreId, StringComparison.OrdinalIgnoreCase))
            return null;
        var catalog = ItemsFor(player);
        if (catalog.ByHash(target.TypeHash) is not { } targetType) return null;
        return CrucibleTreeEngine.ApplyMagmaticOre(target, targetType, catalog.Mods, random, currencyItem?.StoredExperience ?? 0u);
    }

    /// <summary><see cref="GameEvents.VendorQuote"/> handler for Crucible <c>SellBonus</c> weapon passive nodes.</summary>
    public static void OnVendorQuote(VendorSaleQuote quote)
    {
        if (quote.BasicSale) return;
        var catalog = ItemsFor(quote.Player);
        var uniques = UniquesFor(quote.Player);
        bool addedAny = false;

        foreach (var offered in quote.Offered)
        {
            if (offered.Item.WeaponPassiveTree is not { HasTree: true, Allocation: not 0 } tree) continue;
            var allocated = new List<ItemMod>();
            foreach (var node in tree.AllocatedNodes())
                allocated.Add(new ItemMod(ModSection.Implicit, node.ModHash, node.Values));
            if (allocated.Count == 0) continue;

            var rewards = CrucibleSellRewards.Evaluate(allocated, catalog.ByHash(offered.Item.TypeHash), catalog, uniques, AreaRng(quote.Player));
            if (rewards.Count == 0) continue;
            quote.Rewards.AddRange(rewards);
            addedAny = true;
        }

        if (addedAny)
            quote.Recipes.Add("Crucible Sell Bonus");
    }

    /// <summary>Locates an item by <c>(inventoryId, itemId)</c> across player inventories and stash tabs.</summary>
    public static bool TryLocateItem(
        PlayerInventories items,
        AccountStash stash,
        uint inventoryId,
        uint itemId,
        out int? stashTab,
        out byte row,
        out InventoryItem found)
    {
        stashTab = stash.TabOf(inventoryId);
        row = 0;
        found = null!;
        if (stashTab is int tab)
        {
            var match = stash.Inventory(tab).Items.FirstOrDefault(i => i.Id == itemId);
            if (match is null) return false;
            found = match;
            return true;
        }

        if (PlayerInventories.Row(inventoryId) is byte directRow
            && items.Items(directRow).FirstOrDefault(i => i.Id == itemId) is { } direct)
        {
            row = directRow;
            found = direct;
            return true;
        }
        return false;
    }

    public static bool IsEquipmentRow(byte row) =>
        row is PlayerInventories.Weapon1 or PlayerInventories.Offhand1
            or PlayerInventories.Weapon2 or PlayerInventories.Offhand2
            or PlayerInventories.Helmet or PlayerInventories.BodyArmour;
}
