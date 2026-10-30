using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    // Until the ES pool integration owns stun avoidance, exclude any known source, including raw aura/item ES and
    // active armour/shield bases which the standalone server does not yet fold into maximum_energy_shield.
    static bool PlayerHasEnergyShieldForRecovery(ClientState conn, Func<string, int> get)
    {
        if (PlayerHitRecovery.EnergyShieldPresent(get)) return true;
        return ItemModStats.EquipmentRows.SelectMany(ItemsOf(conn).Items)
            .Where(i => !IsInactive(conn, i.Id)).Any(i =>
                ItemsFor(conn).ByHash(i.Item.TypeHash) is { } type && ItemsFor(conn).Defences(type) is { EnergyShieldMin: > 0 }
                || ItemModStats.Local(i.Item, ItemsFor(conn).Mods).GetValueOrDefault("local_energy_shield") > 0);
    }

    static PlayerInventories ItemsOf(ClientState conn) => PlayerInventories.For(string.IsNullOrEmpty(conn.Character) ? "Exile" : conn.Character, ItemsFor(conn));

    // The local armour, evasion and energy shield of the equipped body armour, helmet, gloves, boots and shield: the base
    // type's ArmourTypes roll (its minimum, matching the item's zero base percentile) plus the item's local flat/increased
    // defence mods and quality (ItemDefences). Inactive items (requirements not met) give none.
    static (decimal Armour, decimal Evasion, int EnergyShield, decimal Ward) EquippedBaseDefences(ClientState conn, Func<string, int> get)
    {
        var items = ItemsOf(conn);
        decimal armour = 0, evasion = 0, ward = 0; int energyShield = 0;
        foreach (byte row in new[] { PlayerInventories.BodyArmour, PlayerInventories.Helmet, PlayerInventories.Gloves, PlayerInventories.Boots, PlayerInventories.Offhand1 })
            if (items.Items(row) is [var equipped, ..] && !IsInactive(conn, equipped.Id)
                && ItemsFor(conn).ByHash(equipped.Item.TypeHash) is { } type && ItemsFor(conn).Defences(type) is { } d)
            {
                var (a, e, s) = ItemDefences.Of(d, ItemModStats.Local(equipped.Item, ItemsFor(conn).Mods), equipped.Item.Quality);
                int percent = row == PlayerInventories.BodyArmour ? Math.Clamp(get("ward_instead_of_%_body_armour_and_evasion"), 0, 100) : 0;
                decimal armourWard = a * percent / 100m, evasionWard = e * percent / 100m;
                armour += a - armourWard;
                evasion += e - evasionWard;
                var local = ItemModStats.Local(equipped.Item, ItemsFor(conn).Mods);
                ward += armourWard + evasionWard
                    + (long)WardState.Max(d.WardMin + local.GetValueOrDefault("local_ward"), local.GetValueOrDefault("local_ward_+%"))
                        * (100 + Math.Clamp((int)equipped.Item.Quality, 0, 100)) / 100;
                energyShield += s;
            }
        return (armour, evasion, energyShield, ward);
    }

    // The equipped main/off hands in both weapon sets. Rows 2/3 are Weapon1 and rows 14/15 Weapon2.
    // The local stats of the active main hand and off hand items (the first weapon set, else the second), for the panel's
    // hand rows (PanelStats).
    // The item in the main hand (row 2, the active set) and its base type, unless its requirements are not met.
    static (Item Item, ItemCatalog.BaseType? Type)? ActiveMainHand(ClientState conn) => ActiveHand(conn, PlayerInventories.Weapon1);

    // The item in the off hand (row 3, the active set). The weapon swap trades the hands' items with rows 14/15
    // (SwapWeaponSets), so Offhand2 always holds the inactive set's off hand, which is never swung.
    static (Item Item, ItemCatalog.BaseType? Type)? ActiveOffHand(ClientState conn) => ActiveHand(conn, PlayerInventories.Offhand1);

    static (Item Item, ItemCatalog.BaseType? Type)? ActiveHand(ClientState conn, byte row) =>
        ItemsOf(conn).Items(row).Where(i => !IsInactive(conn, i.Id))
            .Select(i => ((Item, ItemCatalog.BaseType?)?)(i.Item, ItemsFor(conn).ByHash(i.Item.TypeHash))).FirstOrDefault();

    // The main hand's attack (2026-10-23): the weapon in the hand (row 2, the active set) by its WeaponTypes row, with the
    // item's local stats (ItemWeapons: physical damage and quality, attack speed, local added elemental/chaos damage); no
    // weapon, the class's unarmed attack.
    static SkillCatalog.Unarmed WeaponFor(ClientState conn) => WeaponOf(conn, ActiveMainHand(conn));

    static SkillCatalog.Unarmed WeaponOf(ClientState conn, (Item Item, ItemCatalog.BaseType? Type)? mainHand) =>
        WeaponIn(conn, mainHand) ?? SkillsFor(conn).UnarmedFor(conn.ClassRow);

    // A hand's weapon attack (WeaponOf); null when the hand holds no weapon (empty, a shield, a quiver).
    static SkillCatalog.Unarmed? WeaponIn(ClientState conn, (Item Item, ItemCatalog.BaseType? Type)? hand)
    {
        var unarmed = SkillsFor(conn).UnarmedFor(conn.ClassRow);
        if (hand is not var (item, type) || type is null || SkillsFor(conn).WeaponBase(type.Row) is not var (ms, min, max, range)) return null;
        return ItemWeapons.Of(ms, min, max, Math.Max(range, unarmed.Range), ItemModStats.Local(item, ItemsFor(conn).Mods), item.Quality)
            with { WieldingWand = type.Class == "Wand", Melee = type.Class is not ("Wand" or "Bow") };
    }

    // The off-hand weapon of a dual-wielded attack (the active set's Offhand1), with the same values WeaponIn gives the main
    // hand. Null when there is nothing to swing with there (empty, a shield, a quiver) or the main hand holds no weapon.
    static SkillCatalog.Unarmed? OffhandWeaponFor(ClientState conn) =>
        WeaponIn(conn, ActiveMainHand(conn)) is null ? null : WeaponIn(conn, ActiveOffHand(conn));

    static (Dictionary<string, int> Main, Dictionary<string, int> Off) HandLocalStats(ClientState conn)
    {
        var items = ItemsOf(conn);
        Dictionary<string, int> Local(byte row, byte swap) =>
            items.Items(row).Concat(items.Items(swap)).Where(i => !IsInactive(conn, i.Id)).Select(i => i.Item).FirstOrDefault() is { } item
                ? ItemModStats.Local(item, ItemsFor(conn).Mods) : [];
        return (Local(2, 14), Local(3, 15));
    }

    // activeSet: only the hands' rows (the active weapon set), for what the player swings now (the attack animation);
    // otherwise both sets, as the passive conditions read them.
    internal static WeaponProfile WeaponProfileFor(ClientState conn, bool activeSet = false)
    {
        var items = ItemsOf(conn);
        // An inactive weapon (requirements not met) is not there at all: the class is unarmed, as the default attack's
        // unarmed fallback (WeaponFor) is.
        ItemCatalog.BaseType? At(byte row) => items.Items(row).Where(i => !IsInactive(conn, i.Id))
            .Select(i => ItemsFor(conn).ByHash(i.Item.TypeHash)).FirstOrDefault(t => t is not null);
        static bool OneHandMelee(ItemCatalog.BaseType t) => t.Class is "Claw" or "Dagger" or "Rune Dagger"
            or "One Hand Sword" or "Thrusting One Hand Sword" or "One Hand Axe" or "One Hand Mace" or "Sceptre";
        static bool OneHand(ItemCatalog.BaseType t) => OneHandMelee(t) || t.Class == "Wand";
        static bool TwoHandMelee(ItemCatalog.BaseType t) => t.Class is "Two Hand Sword" or "Two Hand Axe"
            or "Two Hand Mace" or "Staff" or "Warstaff";
        static bool Pair(ItemCatalog.BaseType? main, ItemCatalog.BaseType? off) =>
            main is { } m && off is { } o && OneHand(m) && OneHand(o);
        var main1 = At(2);
        var off1 = At(3);
        var main2 = activeSet ? null : At(14);
        var off2 = activeSet ? null : At(15);
        var all = new[] { main1, off1, main2, off2 }.OfType<ItemCatalog.BaseType>().ToList();
        var dual = Pair(main1, off1) || Pair(main2, off2);
        var claws = (main1?.Class == "Claw" && off1?.Class == "Claw") || (main2?.Class == "Claw" && off2?.Class == "Claw");
        var unarmed = main1 is null && main2 is null;
        var mainItem = items.Items(2).Concat(activeSet ? [] : items.Items(14)).Where(i => !IsInactive(conn, i.Id)).Select(i => i.Item).FirstOrDefault();
        var local = mainItem is null ? [] : ItemModStats.Local(mainItem, ItemsFor(conn).Mods);
        return new WeaponProfile(
            Shield: activeSet ? off1?.Class == "Shield" : HasShield(conn),
            DualWield: dual,
            Staff: all.Any(t => t.Class is "Staff" or "Warstaff"),
            Bow: all.Any(t => t.Class == "Bow"),
            DualClaws: claws,
            Unarmed: unarmed,
            OneHand: all.Any(OneHand),
            TwoHand: all.Any(t => TwoHandMelee(t) || t.Class == "Bow"),
            Melee: unarmed || all.Any(t => OneHandMelee(t) || TwoHandMelee(t)),
            OneHandMelee: all.Any(OneHandMelee),
            TwoHandMelee: all.Any(TwoHandMelee),
            Axe: all.Any(t => t.Class is "One Hand Axe" or "Two Hand Axe"),
            Sword: all.Any(t => t.Class is "One Hand Sword" or "Thrusting One Hand Sword" or "Two Hand Sword"),
            Mace: all.Any(t => t.Class is "One Hand Mace" or "Two Hand Mace"),
            Claw: all.Any(t => t.Class == "Claw"),
            Dagger: all.Any(t => t.Class is "Dagger" or "Rune Dagger"),
            Wand: all.Any(t => t.Class == "Wand"),
            // The main hand's local mods: increased critical strike chance scales its base, attack speed and accuracy go
            // into PassiveBonus.
            Critical: (main1 ?? main2) is { } main ? ItemsFor(conn).WeaponCritical(main) * (100 + local.GetValueOrDefault("local_critical_strike_chance_+%")) / 100 : 0,
            LocalAttackSpeedPct: local.GetValueOrDefault("local_attack_speed_+%"),
            LocalAccuracy: local.GetValueOrDefault("local_accuracy_rating"));
    }

    // The equipped off hand(s): Weapon1's off hand is row 3, Weapon2's is row 15.
    static bool HasShield(ClientState conn) => OffhandTypes(conn).Any(t => t.Class == "Shield");

    static int ShieldBaseBlock(ClientState conn) => OffhandTypes(conn).Where(t => t.Class == "Shield")
        .Select(ItemsFor(conn).ShieldBlock).DefaultIfEmpty(0).Max();

    static IEnumerable<ItemCatalog.BaseType> OffhandTypes(ClientState conn)
    {
        var items = ItemsOf(conn);
        foreach (byte row in new byte[] { PlayerInventories.Offhand1, 15 })
            foreach (var item in items.Items(row))
                if (ItemsFor(conn).ByHash(item.Item.TypeHash) is { } type) yield return type;
    }

    static PlayerInventories.Shape? ShapeOf(Item i) => Items.ByHash(i.TypeHash) is { } t
        ? new PlayerInventories.Shape(t.Width, t.Height, t.Class, t.MaxStack,
            t.BecomesWhenFull && Items.Row(t.FullStackRow) is { Supported: true } full ? () => ItemCatalog.Create(full) : null)
        : null;

    // The weapon type of an equipment row's item (its BaseItemTypes row), or null: the active (not inactive) item.
    static ItemCatalog.BaseType? HandType(ClientState conn, byte row) =>
        ItemsOf(conn).Items(row).Where(i => !IsInactive(conn, i.Id))
            .Select(i => ItemsFor(conn).ByHash(i.Item.TypeHash)).FirstOrDefault();

    // The record's type is the item's ItemClasses row + 1: the client checks skills' weapon restrictions against it.
    static VisibleItem? LookOf(Item i) => Items.ByHash(i.TypeHash) is { } t
        ? new VisibleItem(0, t.VisualIdentity, t.Stance,
            t.ClassRow is >= 0 and < 255 ? (byte)(t.ClassRow + 1) : (byte)1) : null;

    // What the model wears: the equipped items, dressed in the worn cosmetics (CosmeticEffects). A skin replaces the item's
    // art in its entry's first reference and a weapon effect fills the second; attachments and character effects fill the
    // model slots no item uses, which go out empty when nothing is worn there so taking one off clears it.
    static IReadOnlyList<VisibleItem> Looks(ClientState conn, PlayerInventories items)
    {
        var looks = items.Equipped(LookOf).ToList();
        // A bow is held in the left hand, and its model slot is not the main hand's: BowSetup holds the record the live server
        // sends (the bow's art in slot 1, an arrow in slot 0 with the weapon type and stance, the quiver on the back in 7).
        if (HandType(conn, PlayerInventories.Weapon1) is { Class: "Bow" } bow)
        {
            int quiver = looks.FindIndex(l => l.Slot == 1 && l.VisualIdentity != 0);
            ushort quiverArt = quiver >= 0 ? looks[quiver].VisualIdentity : ItemsFor(conn).Visual("DefaultQuiver") ?? 0x328b;
            looks.RemoveAll(l => l.Slot is 0 or 1 or 7);
            looks.AddRange(BowSetup.Entries(bow, ItemsFor(conn).Visual("DefaultArrow") ?? 0xb2f6, quiverArt));
        }
        var cosmeticSlots = CosmeticEffects.CosmeticOnlySlots.ToDictionary(s => s, VisibleItem.Empty);
        foreach (var worn in WornCosmetics(conn))
        {
            if (MtxEffects.SlotOf(worn.Slot) is not { ModelSlot: byte model } slot || MtxEffects.Of(worn.MtxTypeHash) is not { } fx) continue;
            if (cosmeticSlots.ContainsKey(model))
            {
                if (fx.Visual != 0)
                    cosmeticSlots[model] = new VisibleItem(model, fx.Visual) { SkinVisual = fx.Visual, SkinHash = worn.MtxTypeHash };
                continue;
            }
            int at = looks.FindIndex(l => l.Slot == model);
            if (at < 0 || looks[at].VisualIdentity == 0) continue;   // nothing equipped there to dress
            if (slot.IsWeaponEffect && fx.WeaponEffect != 0)
                looks[at] = looks[at] with { EffectVisual = fx.WeaponEffect, EffectHash = worn.MtxTypeHash };
            else if (!slot.IsWeaponEffect && fx.Visual != 0)
                looks[at] = looks[at] with { SkinVisual = fx.Visual, SkinHash = worn.MtxTypeHash };
        }
        return [.. looks, .. cosmeticSlots.Values];
    }

    // The stats worn cosmetics give the character (footprints, apparitions, …), summed per stat. A pet is the stat the client
    // spawns it from: pet_slot1 for the first Pet slot (MicrotransactionSlot 18), pet_slot2 for the second (19).
    // Skill Gem Effect (slot 49) is left out: its skill_art_variation belongs to one skill's stats, not the character's.
    static IEnumerable<(uint Stat, int Value)> CosmeticStats(ClientState conn) =>
        WornCosmetics(conn).Where(w => w.Slot != 49).SelectMany(w => MtxEffects.Of(w.MtxTypeHash) is not { } fx ? [] :
                fx.Pet != 0 && Stats.Key(w.Slot == 19 ? "pet_slot2" : "pet_slot1") is uint pet ? [.. fx.Stats, (pet, fx.Pet)] : fx.Stats)
            .GroupBy(s => s.Stat, s => s.Value).Select(g => (g.Key,
                ActorScale.IsInput(g.Key) ? ActorScale.Saturate(g.Sum(v => (long)v)) : g.Sum()));

    static IEnumerable<(uint Stat, int Value)> CosmeticScaleSources(ClientState conn) =>
        CosmeticStats(conn).Where(s => ActorScale.IsInput(s.Stat));

    // What the player wears changed (an item went into or out of a visible equipment row): 0x2ba Inventories for the model,
    // then the Actor skill list again, since the gems socketed in the equipment grant skills.
    static async Task SendEquipment(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, PlayerInventories items)
    {
        RefreshVaalSouls(conn);
        var equipped = Looks(conn, items);
        await Send(remote, stream, ciphers, InstanceEntities.PlayerEquipmentUpdate(PlayerId, equipped),
            $"0x02ba player equipment: {string.Join(", ", equipped.Select(e => $"slot {e.Slot} visual 0x{e.VisualIdentity:x4} stance {e.Stance}"))}");
        // The socketed gems may have changed: the Cosmetics tab's skill slots follow them.
        await SendWornCosmetics(remote, stream, ciphers, conn, "equipment changed");
        // Under the Monsters lock, as every pools writer (a utility flask that ends recomputes under it too).
        lock (conn.Monsters) { RecomputePoolsAndSpeed(conn); conn.PlayerMana = Math.Min(conn.PlayerMana, (uint)UnreservedMana(conn)); }
        await SendSkills(remote, stream, ciphers, conn);
        await SendPlayerStatSnapshot(remote, stream, ciphers, conn,
            () => PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)),
            "0x01b3 player stats (equipment)");
        await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana} (equipment)");
    }

    // Dev commands (/mod, /rarity, /sockets): changes the held item and replaces it on the cursor (0xbf removing and adding
    // its id). The item needs the component the change is about.
    static async Task ChangeHeldItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, Func<Item, Item> change, string what,
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

    static ModSection? ParseSection(string text) => text.ToLowerInvariant() switch
    {
        "implicit" or "imp" => ModSection.Implicit,
        "explicit" or "exp" => ModSection.Explicit,
        "enchant" or "ench" => ModSection.Enchant,
        "scourge" or "s10" => ModSection.Scourge,
        "hidden" or "s11" => ModSection.Hidden,
        _ => null,
    };

    // Takes the held item off the cursor (0xbf); null when nothing is held.
    static async Task<InventoryItem?> TakeHeld(string remote, NetworkStream stream, ConnectionCiphers ciphers, PlayerInventories items, string why)
    {
        if (items.Items(PlayerInventories.Cursor) is not [var held, ..] || items.Remove(PlayerInventories.Cursor, held.Id) is null) return null;
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [held.Id], []), $"0x00bf cursor: remove item {held.Id} ({why})");
        return held;
    }
}
