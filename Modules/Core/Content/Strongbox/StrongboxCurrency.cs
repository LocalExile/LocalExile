using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Strongbox;

/// <summary>
/// Currency used on a box (C→S 0x74, <see cref="InstanceItems.UseItemOnObject"/>): the orbs that change an item's rarity
/// and explicit mods change a closed, uncorrupted box's the same way; one leaves its stack only when the box changed,
/// and the box is created again under a new id with its new rarity and mods (as when it opens: ObjectMagicProperties
/// has no update message the server knows).
/// </summary>
public static partial class StrongboxManager
{
    const string ScrollOfWisdom = "Metadata/Items/Currency/CurrencyIdentification", EngineersOrb = "Metadata/Items/Currency/CurrencyStrongboxQuality";

    /// <summary>GameEvents.Packet(0x74): true when the target is one of this instance's boxes (handled, or refused with
    /// the reason logged); false for any other object, which this module leaves alone.</summary>
    public static async Task<bool> UseItemOn(ClientState conn, byte[] message)
    {
        if (message.Length < InstanceItems.UseItemOnObjectLength) return false;
        var use = InstanceItems.ReadUseItemOnObject(message);
        if (conn.Instance is not { } instance || await Find(conn, use.TargetObjectId) is not var (index, _)) return false;
        var (remote, stream, ciphers) = conn.Wire;
        string Log(string text) => $"[gateway] {remote} <= 0x0074 item {use.ItemId} (inventory {use.InventoryId}) on strongbox 0x{use.TargetObjectId:x}: {text}";
        var items = ItemsOf(conn);
        if (items.RowOf(use.ItemId) is not byte row || PlayerInventories.InventoryId(row) != use.InventoryId
            || items.Items(row).FirstOrDefault(i => i.Id == use.ItemId) is not { } used)
        {
            Console.WriteLine(Log("no such item in the character's inventories (currency from the stash is not handled)"));
            return true;
        }
        var currency = ItemsFor(conn).ByHash(used.Item.TypeHash);
        var area = Areas.GetOrCreateValue(instance);
        await area.Gate.WaitAsync();
        try
        {
            if (area.Boxes is not { } boxes || index >= boxes.Count || boxes[index].Id != use.TargetObjectId
                || StrongboxCatalog.ByRow(boxes[index].Row) is not { } type) return true;
            var box = boxes[index];
            string? refusal = box.Opened ? "the box is open" : box.Corrupted ? "the box is corrupted" : !type.Rollable ? "the box cannot be modified" : null;
            Box? changed = null;
            if (refusal is null) (changed, refusal) = Change(conn, type, box, currency?.Id);
            if (changed is null)
            {
                Console.WriteLine(Log($"{currency?.Name ?? $"0x{used.Item.TypeHash:x8}"} refused: {refusal}"));
                return true;
            }
            if (items.ConsumeOne(row, used.Id) is not { } taken)
            {
                Console.WriteLine(Log("the item is gone"));
                return true;
            }
            changed = changed with { Id = conn.TakeObjectId() };
            boxes[index] = changed;
            Save(instance, boxes);
            uint inventory = PlayerInventories.InventoryId(row);
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(inventory, items.NextRevision(row), [used.Id], taken.Left is { } left ? [left] : []),
                $"0x00bf inventory {inventory}: {currency!.Name} used" + (taken.Left is { } l ? $", {l.Item.StackSize} left" : ", stack gone"));
            Console.WriteLine(Log($"{currency.Name}: {Describe(conn, box)} → {Describe(conn, changed)}, now 0x{changed.Id:x}"));
            await Replace(instance, index, box, changed);
        }
        finally { area.Gate.Release(); }
        return true;
    }

    /// <summary>What the currency makes of a closed, uncorrupted box, or null with the reason.</summary>
    static (Box? Box, string? Refusal) Change(ClientState conn, StrongboxCatalog.Entry type, Box box, string? currency)
    {
        var catalog = ItemsFor(conn).Mods;
        var mods = box.Explicits;
        int level = AreaLevel(conn);
        var stat = StatOf(AreaStats(conn));
        var rng = Random.Shared;
        (Box?, string?) With(byte rarity, IReadOnlyList<ItemMod> rolled) => (box with { Rarity = rarity, Mods = Kept(rolled) }, null);
        static (Box?, string?) Fail(string why) => (null, why);
        switch (currency)
        {
            case CurrencyEffects.OrbOfTransmutation:
                return box.Rarity != 0 ? Fail("not a normal box") : With(1, StrongboxMods.Roll(catalog, type, 1, level, stat, rng));
            case CurrencyEffects.OrbOfAlchemy:
                return box.Rarity != 0 ? Fail("not a normal box") : With(2, StrongboxMods.Roll(catalog, type, 2, level, stat, rng));
            case CurrencyEffects.OrbOfAlteration:
                return box.Rarity != 1 ? Fail("not a magic box") : With(1, StrongboxMods.Roll(catalog, type, 1, level, stat, rng));
            case CurrencyEffects.ChaosOrb:
                return box.Rarity != 2 ? Fail("not a rare box") : With(2, StrongboxMods.Roll(catalog, type, 2, level, stat, rng));
            case CurrencyEffects.OrbOfAugmentation:
            {
                if (box.Rarity != 1) return Fail("not a magic box");
                var more = StrongboxMods.Add(catalog, type, mods, 1, 1, level, stat, rng);
                return more.Count > mods.Count ? With(1, more) : Fail("no room for another mod");
            }
            case CurrencyEffects.RegalOrb:
                return box.Rarity != 1 ? Fail("not a magic box") : With(2, StrongboxMods.Add(catalog, type, mods, 2, 1, level, stat, rng));
            case CurrencyEffects.ExaltedOrb:
            {
                if (box.Rarity != 2) return Fail("not a rare box");
                var more = StrongboxMods.Add(catalog, type, mods, 2, 1, level, stat, rng);
                return more.Count > mods.Count ? With(2, more) : Fail("no room for another mod");
            }
            case CurrencyEffects.OrbOfScouring:
                return box.Rarity == 0 ? Fail("already a normal box") : With(0, []);
            // A Vaal Orb corrupts the box: no currency changes it again (the Vaal Orb's own text), and its contents drop
            // corrupted (an official-forum account; StrongboxLoot). No outcome table for boxes is in the data.
            case CurrencyEffects.VaalOrb:
                return (box with { Corrupted = true, VaalOrb = true }, null);
            // Boxes are created identified (the map mod that makes them unidentified is not consumed): nothing to identify.
            case ScrollOfWisdom: return Fail("the box is identified");
            // CurrencyItems: "This item is no longer usable." / "Formerly improved the quality of a Strongbox".
            case EngineersOrb: return Fail("Engineer's Orb is no longer usable (CurrencyItems Directions)");
            default: return Fail("this currency does nothing to a strongbox");
        }
    }
}
