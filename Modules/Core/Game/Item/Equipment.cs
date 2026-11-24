using System.Net.Sockets;
using System.Collections.Frozen;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    static bool IsTwoHandMeleeClass(string? itemClass) => itemClass is "Two Hand Sword" or "Two Hand Axe"
        or "Two Hand Mace" or "Staff" or "Warstaff";

    // Pools and published stats already include effective item ES. Raw bases would count suppressed/converted ES.
    public static bool PlayerHasEnergyShieldForRecovery(Player player, Func<uint, int> get) =>
        player.Pools.EnergyShield > 0 || PlayerHitRecovery.EnergyShieldPresent(get);

    public static PlayerInventories ItemsOf(Player player)
    {
        StatGraph.ReadInput((int)StatInput.Items);
        return ItemsOfUnread(player);
    }

    static PlayerInventories ItemsOfUnread(Player player) => PlayerInventories.For(string.IsNullOrEmpty(player.Character.Name) ? "Exile" : player.Character.Name, ItemsFor(player));

    // Active local item defences retain their base roll, local conversions and final native rounding.
    public static IEnumerable<(byte Row, (decimal Armour, decimal Evasion, int EnergyShield, decimal Ward) Defence)> EquippedArmourDefences(
        Player player, Func<uint, int>? get = null)
    {
        var items = ItemsOf(player);
        var catalog = ItemsFor(player);
        foreach (var (row, equipped) in EquipmentForStats(player))
            if (row is PlayerInventories.BodyArmour or PlayerInventories.Helmet or PlayerInventories.Gloves or PlayerInventories.Boots or PlayerInventories.Offhand1
                && !IsInactive(player, equipped.Id)
                && catalog.ByHash(equipped.Item.TypeHash) is { } type && catalog.Defences(type) is { } defences)
            {
                var (a, e, s) = ItemDefences.Calculate(equipped.Item, defences, catalog.Mods);
                int percent = row == PlayerInventories.BodyArmour
                    ? Math.Clamp(get?.Invoke(Stat.WardInsteadOfPctBodyArmourAndEvasion) ?? 0, 0, 100) : 0;
                decimal armourWard = a * percent / 100m, evasionWard = e * percent / 100m;
                decimal ward = armourWard + evasionWard
                    + ItemDefences.Ward(equipped.Item, defences, catalog.Mods);
                yield return (row, (a - armourWard, e - evasionWard, s, ward));
            }
    }

    // Active item-tooltip values become flat inputs to the character-wide pool calculation.
    public static (decimal Armour, decimal Evasion, int EnergyShield, decimal Ward) EquippedBaseDefences(Player player, Func<uint, int>? get = null)
    {
        decimal armour = 0, evasion = 0, ward = 0; long energyShield = 0;
        foreach (var (_, defence) in EquippedArmourDefences(player, get))
        {
            armour += defence.Armour;
            evasion += defence.Evasion;
            energyShield += defence.EnergyShield;
            ward += defence.Ward;
        }
        return (armour, evasion, (int)Math.Min(int.MaxValue, energyShield), ward);
    }

    // Native 0x14252a660: flag selects 100%, otherwise use the percentage; truncate each item's local ES separately.
    // The conversion removes all armour-item ES, even when the Life gained is only 50% of its value.
    public static (int Life, int EnergyShield) ConvertEquipmentEnergyShield(Player player, Func<uint, int> get, int energyShield)
    {
        int percent = get(Stat.GainMaximumLifeInsteadOfMaximumEsFromArmour) != 0 ? 100
            : Math.Max(0, get(Stat.GainMaximumLifeInsteadOfMaximumEsFromArmourAtXPctOfTheValue));
        if (percent == 0) return (0, energyShield);
        bool shieldExcluded = get(Stat.MinionsGetShieldStatsInsteadOfYou) != 0
            || get(Stat.GainNoDefencesFromEquippedShield) != 0;
        long life = 0;
        foreach (var (row, defence) in EquippedArmourDefences(player))
            if (row != PlayerInventories.Offhand1 || !shieldExcluded)
                life += (long)defence.EnergyShield * percent / 100;
        return ((int)Math.Min(int.MaxValue, life), 0);
    }

    // The item in the main hand (row 2, the active set) and its base type, unless its requirements are not met.
    public static (Item Item, ItemCatalog.BaseType? Type)? ActiveMainHand(Player player) => ActiveHand(player, PlayerInventories.Weapon1);

    // "While holding a Fishing Rod" (PoB Condition UsingFishing from weaponData1): the active set's main hand only, and
    // not a rod whose requirements are unmet. The client's calculators test main_hand_weapon_type 15 the same way.
    static bool HoldingFishingRod(Player player) => ActiveMainHand(player)?.Type?.Class == "FishingRod";

    // The item in the off hand (row 3, the active set). The weapon swap trades the hands' items with rows 14/15
    // (SwapWeaponSets), so Offhand2 always holds the inactive set's off hand, which is never swung.
    public static (Item Item, ItemCatalog.BaseType? Type)? ActiveOffHand(Player player) => ActiveHand(player, PlayerInventories.Offhand1);

    public static (Item Item, ItemCatalog.BaseType? Type)? ActiveHand(Player player, byte row) =>
        ActiveHandIn(ItemsOf(player).Items(row), id => IsInactive(player, id), ItemsFor(player));

    // ActiveHand from a row read once, with the items whose requirements are not met (the stat snapshot's tail inputs).
    public static (Item Item, ItemCatalog.BaseType? Type)? ActiveHandIn(IEnumerable<InventoryItem> row, Func<uint, bool> inactive,
        ItemCatalog catalog) =>
        row.Where(i => !inactive(i.Id))
            .Select(i => ((Item, ItemCatalog.BaseType?)?)(i.Item, catalog.ByHash(i.Item.TypeHash))).FirstOrDefault();

    // The active weapon's local typed damage, quality and duration; no weapon uses the class's unarmed attack.
    public static SkillCatalog.Unarmed WeaponFor(Player player) => WeaponOf(player, ActiveMainHand(player));

    public static SkillCatalog.Unarmed WeaponOf(Player player, (Item Item, ItemCatalog.BaseType? Type)? mainHand) =>
        WeaponIn(player, mainHand) ?? SkillsFor(player).UnarmedFor(player.Character.ClassRow);

    // A hand's weapon attack (WeaponOf); null when the hand holds no weapon (empty, a shield, a quiver).
    public static SkillCatalog.Unarmed? WeaponIn(Player player, (Item Item, ItemCatalog.BaseType? Type)? hand) =>
        WeaponIn(SkillsFor(player), ItemsFor(player), player.Character.ClassRow, hand) is { } weapon && hand is { } active
            ? weapon with { LocalSources = QualifiedHandLocalStats(player, active.Item, weapon.LocalSources!).ToFrozenDictionary() } : null;

    static Dictionary<uint, int> QualifiedHandLocalStats(Player player, Item item,
        IReadOnlyDictionary<uint, int>? weaponSources = null)
    {
        var result = new Dictionary<uint, int>(weaponSources ?? SupportedAttackDamage.WeaponSources(item, ItemsFor(player).Mods));
        var local = ItemModStats.Local(item, ItemsFor(player).Mods);
        if (!local.Keys.Any(name => StatConditions.PlainOf(Stats.Name(name)) is not null)) return result;
        var flags = player.StatSourcesCache?.Key.Flags ?? ItemConditionFlags(player, ResourceEffectsTime(player));
        foreach (uint name in local.Keys) result.Remove(name);
        foreach (var (name, value) in ItemModStats.Local(item, ItemsFor(player).Mods, new(Flags: flags), Stats.Has))
            result[name] = value;
        return result;
    }

    public static SkillCatalog.Unarmed? WeaponIn(SkillCatalog skills, ItemCatalog catalog, int classRow,
        (Item Item, ItemCatalog.BaseType? Type)? hand)
    {
        var unarmed = skills.UnarmedFor(classRow);
        if (hand is not var (item, type) || type is null || skills.WeaponBase(type.Row) is not var (ms, min, max, range)) return null;
        var local = ItemWeaponStats.Calculate(item, (ms, min, max, range), catalog.Mods);
        var physical = local.Damage[GameServer.Core.World.DamageType.Physical];
        var sources = SupportedAttackDamage.WeaponSources(item, catalog.Mods);
        // "+# to Weapon Range" is in the same tenths of a metre as WeaponTypes' range.
        range += sources.GetValueOrDefault(Stat.LocalWeaponRangePlus);
        // The weapon's class decides Arcane Might's wand-only grant and the melee context of its own components.
        return new SkillCatalog.Unarmed(local.AttackMs, (int)physical.Min, (int)physical.Max, Math.Max(range, unarmed.Range))
        {
            LocalDamage = local.Damage, LocalSources = sources,
            ItemClass = type.Class,
            WieldingWand = type.Class == "Wand", Bow = type.Class == "Bow", Melee = type.Class is not ("Wand" or "Bow"),
        };
    }

    // The off-hand weapon of a dual-wielded attack (the active set's Offhand1), with the same values WeaponIn gives the main
    // hand. Null when there is nothing to swing with there (empty, a shield, a quiver) or the main hand holds no weapon.
    public static SkillCatalog.Unarmed? OffhandWeaponFor(Player player) =>
        WeaponIn(player, ActiveMainHand(player)) is not null && WeaponIn(player, ActiveOffHand(player)) is { } off
            ? off with { OffHand = true } : null;

    public static (Dictionary<uint, int> Main, Dictionary<uint, int> Off) HandLocalStats(Player player)
    {
        Dictionary<uint, int> Local((Item Item, ItemCatalog.BaseType? Type)? hand) => hand is { } active
            ? QualifiedHandLocalStats(player, active.Item) : [];
        // Weapon swaps exchange the equipment rows; rows 14/15 remain inactive even when an active hand is empty.
        return (Local(ActiveMainHand(player)), Local(ActiveOffHand(player)));
    }

    // Current attack executors use the main hand. Read only its qualified active item; a shield, an inactive
    // weapon set or an unqualified weapon cannot supply local modifiers to this contact.
    public static IReadOnlyDictionary<uint, int>? MainHandLocalSources(Player player) =>
        WeaponFor(player).LocalSources;

    static bool OneHandMelee(ItemCatalog.BaseType type) => WeaponIdentity.OneHandMelee(type.Class);

    // Current actions and passive weapon conditions use only the hands' active rows. A normal weapon swap trades
    // these rows with 14/15 before recomputing pools; an explicit false is reserved for inspecting both stored sets.
    public static WeaponProfile WeaponProfileFor(Player player, bool activeSet = true)
    {
        var items = ItemsOf(player);
        return WeaponProfileFrom(row => items.Items(row), id => IsInactive(player, id), ItemsFor(player), activeSet, () => HasShield(player));
    }

    // Animation suffixes and contact geometry use the physical weapon, not its additional gameplay identities.
    public static WeaponProfile AnimationWeaponProfileFor(Player player)
    {
        var items = ItemsOf(player);
        return WeaponProfileFrom(row => items.Items(row), id => IsInactive(player, id), ItemsFor(player),
            activeSet: true, () => HasShield(player), expandedIdentities: false);
    }

    // WeaponProfileFor from rows read once (the stat snapshot's tail inputs hold rows 2 and 3, the active set's hands).
    public static WeaponProfile WeaponProfileFrom(Func<byte, IReadOnlyList<InventoryItem>> rowItems, Func<uint, bool> inactive,
        ItemCatalog catalog, bool activeSet, Func<bool> hasShield) =>
        WeaponProfileFrom(rowItems, inactive, catalog, activeSet, hasShield, expandedIdentities: true);

    static WeaponProfile WeaponProfileFrom(Func<byte, IReadOnlyList<InventoryItem>> rowItems, Func<uint, bool> inactive,
        ItemCatalog catalog, bool activeSet, Func<bool> hasShield, bool expandedIdentities)
    {
        // An inactive weapon (requirements not met) is not there at all: the class is unarmed, as the default attack's
        // unarmed fallback (WeaponFor) is.
        ItemCatalog.BaseType? At(byte row) => rowItems(row).Where(i => !inactive(i.Id))
            .Select(i => catalog.ByHash(i.Item.TypeHash)).FirstOrDefault(t => t is not null);
        static bool OneHand(ItemCatalog.BaseType t) => OneHandMelee(t) || t.Class == "Wand";
        static bool TwoHandMelee(ItemCatalog.BaseType t) => IsTwoHandMeleeClass(t.Class);
        static bool Pair(ItemCatalog.BaseType? main, ItemCatalog.BaseType? off) =>
            main is { } m && off is { } o && OneHand(m) && OneHand(o);
        var main1 = At(2);
        var off1 = At(3);
        var main2 = activeSet ? null : At(14);
        var off2 = activeSet ? null : At(15);
        // A loop, not LINQ: every stat-sources lookup reaches this through ItemConditionFlags.
        bool AllTypes(byte row)
        {
            if (!expandedIdentities) return false;
            var items = rowItems(row);
            for (int k = 0; k < items.Count; k++)
                if (!inactive(items[k].Id) && WeaponIdentity.AllOneHandMelee(items[k].Item, catalog.Mods)) return true;
            return false;
        }
        bool mainAll1 = AllTypes(2), offAll1 = AllTypes(3),
            mainAll2 = !activeSet && AllTypes(14), offAll2 = !activeSet && AllTypes(15);
        bool allTypes = mainAll1 || offAll1 || mainAll2 || offAll2;
        var all = new[] { main1, off1, main2, off2 }.OfType<ItemCatalog.BaseType>().ToList();
        var dual = Pair(main1, off1) || Pair(main2, off2);
        var claws = ((main1?.Class == "Claw" || mainAll1) && (off1?.Class == "Claw" || offAll1))
            || ((main2?.Class == "Claw" || mainAll2) && (off2?.Class == "Claw" || offAll2));
        var unarmed = main1 is null && main2 is null;
        var mainItem = rowItems(2).Concat(activeSet ? [] : rowItems(14)).Where(i => !inactive(i.Id)).Select(i => i.Item).FirstOrDefault();
        var local = mainItem is null ? [] : ItemModStats.Local(mainItem, catalog.Mods);
        return new WeaponProfile(
            Shield: activeSet ? off1?.Class == "Shield" : hasShield(),
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
            Axe: allTypes || all.Any(t => t.Class is "One Hand Axe" or "Two Hand Axe"),
            Sword: allTypes || all.Any(t => t.Class is "One Hand Sword" or "Thrusting One Hand Sword" or "Two Hand Sword"),
            Mace: allTypes || all.Any(t => t.Class is "One Hand Mace" or "Two Hand Mace"),
            Claw: allTypes || all.Any(t => t.Class == "Claw"),
            Dagger: allTypes || all.Any(t => t.Class is "Dagger" or "Rune Dagger"),
            Wand: all.Any(t => t.Class == "Wand"),
            // The main hand's local critical chance and accuracy feed its character calculations. Local attack speed
            // is item metadata here; ItemWeaponStats already includes it in that hand's duration.
            Critical: (main1 ?? main2) is { } main ? AddedBaseCriticalStrike.LocalWeaponChance(catalog.WeaponCritical(main),
                local.GetValueOrDefault(Stat.LocalCriticalStrikeChance), local.GetValueOrDefault(Stat.LocalCriticalStrikeChancePlusPct)) : 0,
            LocalAttackSpeedPct: local.GetValueOrDefault(Stat.LocalAttackSpeedPlusPct),
            LocalAccuracy: local.GetValueOrDefault(Stat.LocalAccuracyRating));
    }

    // Both stored offhands, used only by the explicit inventory-wide profile inspection above.
    public static bool HasShield(Player player) => OffhandTypes(player).Any(t => t.Class == "Shield");

    // Base block comes from the active, requirement-enabled shield, like its local defences and global modifiers.
    public static int ShieldBaseBlock(Player player) => ActiveOffHand(player)?.Type is { Class: "Shield" } shield
        ? ItemsFor(player).ShieldBlock(shield) : 0;

    public static IEnumerable<ItemCatalog.BaseType> OffhandTypes(Player player)
    {
        var items = ItemsOf(player);
        foreach (byte row in new byte[] { PlayerInventories.Offhand1, 15 })
            foreach (var item in items.Items(row))
                if (ItemsFor(player).ByHash(item.Item.TypeHash) is { } type) yield return type;
    }

    public static PlayerInventories.Shape? ShapeOf(Item i) => Items.ByHash(i.TypeHash) is { } t
        ? new PlayerInventories.Shape(t.Width, t.Height, t.Class, t.MaxStack,
            t.BecomesWhenFull && Items.Row(t.FullStackRow) is { Supported: true } full ? () => ItemCatalog.Create(full) : null,
            t.IsFossil)
        : null;

    // The weapon type of an equipment row's item (its BaseItemTypes row), or null: the active (not inactive) item.
    public static ItemCatalog.BaseType? HandType(Player player, byte row) =>
        ItemsOf(player).Items(row).Where(i => !IsInactive(player, i.Id))
            .Select(i => ItemsFor(player).ByHash(i.Item.TypeHash)).FirstOrDefault();

    // The record's type is the item's ItemClasses row + 1: the client checks skills' weapon restrictions against it.
    public static VisibleItem? LookOf(Item i) => Items.ByHash(i.TypeHash) is { } t
        ? new VisibleItem(0, t.VisualIdentity, t.Stance,
            t.ClassRow is >= 0 and < 255 ? (byte)(t.ClassRow + 1) : (byte)1) : null;

    // What the model wears: the equipped items, dressed in the worn cosmetics (CosmeticEffects). A skin replaces the item's
    // art in its entry's first reference and a weapon effect fills the second; attachments and character effects fill the
    // model slots no item uses, which go out empty when nothing is worn there so taking one off clears it.
    public static IReadOnlyList<VisibleItem> Looks(Player player, PlayerInventories items)
    {
        var looks = items.Equipped(LookOf).ToList();
        // A weapon whose requirements are not met is not in the hands (WeaponProfileFor, WeaponFor): the model leaves it out
        // and its slot goes out empty, so the client greys out the skills that need it (11-skills.md §10a) and the default
        // attack punches, while the inventory still shows the item where it is.
        foreach (var (row, slot) in new[] { (PlayerInventories.Weapon1, (byte)0), (PlayerInventories.Offhand1, (byte)1) })
            if (items.Items(row) is [var held, ..] && IsInactive(player, held.Id) && looks.FindIndex(l => l.Slot == slot) is int hand and >= 0)
                looks[hand] = VisibleItem.Empty(slot);
        // A bow is held in the left hand, and its model slot is not the main hand's: BowSetup holds the record the live server
        // sends (the bow's art in slot 1, an arrow in slot 0 with the weapon type and stance, the quiver on the back in 7).
        var weapon = HandType(player, PlayerInventories.Weapon1);
        // The ItemClasses row of the item in each model slot (LookOf's type is the row + 1): a skin or a weapon effect is
        // drawn only on an item of a class it lists.
        var classes = looks.Where(l => l.VisualIdentity != 0).GroupBy(l => l.Slot).ToDictionary(g => g.Key, g => g.First().Type - 1);
        if (weapon is { Class: "Bow" } bow)
        {
            int quiver = looks.FindIndex(l => l.Slot == 1 && l.VisualIdentity != 0);
            ushort quiverArt = quiver >= 0 ? looks[quiver].VisualIdentity : ItemsFor(player).Visual("DefaultQuiver") ?? 0x328b;
            // The bow moves to the left hand's entry and the worn quiver to the back; the default quiver is no item.
            classes.Remove(0);
            classes.Remove(7);
            if (quiver >= 0) classes[7] = looks[quiver].Type - 1;
            classes[1] = bow.ClassRow;
            looks.RemoveAll(l => l.Slot is 0 or 1 or 7);
            looks.AddRange(BowSetup.Entries(bow, ItemsFor(player).Visual("DefaultArrow") ?? 0xb2f6, quiverArt));
        }
        var cosmeticSlots = CosmeticEffects.CosmeticOnlySlots.ToDictionary(s => s, VisibleItem.Empty);
        foreach (var worn in WornCosmetics(player))
        {
            if (MtxEffects.SlotOf(worn.Slot) is not { ModelSlot: byte model } slot || MtxEffects.Of(worn.MtxTypeHash) is not { } fx) continue;
            byte tier = fx.ResolveTier(worn.Option);
            if (cosmeticSlots.ContainsKey(model))
            {
                if (fx.Visual != 0)
                    cosmeticSlots[model] = new VisibleItem(model, fx.Visual) { SkinVisual = fx.Visual, SkinHash = worn.MtxTypeHash, SkinTier = tier };
                continue;
            }
            // With a bow the main hand's cosmetics dress the bow, which is the left hand's entry, and the off hand's dress
            // the quiver on the back ("Changes the appearance of any equipped Quiver").
            if (weapon is { Class: "Bow" } && model is 0 or 1) model = model == 0 ? (byte)1 : (byte)7;
            int at = looks.FindIndex(l => l.Slot == model);
            if (at < 0 || looks[at].VisualIdentity == 0) continue;   // nothing equipped there to dress
            if (!classes.TryGetValue(model, out int itemClass) || !fx.Fits(itemClass)) continue;   // not an item it dresses
            if (slot.IsWeaponEffect && fx.WeaponEffect != 0)
                looks[at] = looks[at] with { EffectVisual = fx.WeaponEffect, EffectHash = worn.MtxTypeHash, EffectTier = tier };
            else if (!slot.IsWeaponEffect && fx.Visual != 0)
                looks[at] = looks[at] with { SkinVisual = fx.Visual, SkinHash = worn.MtxTypeHash, SkinTier = tier };
        }
        return [.. looks, .. cosmeticSlots.Values];
    }

    // The stats worn cosmetics give the character (footprints, apparitions, …), summed per stat. A pet is the stat the client
    // spawns it from: pet_slot1 for the first Pet slot (MicrotransactionSlot 18), pet_slot2 for the second (19).
    // Skill Gem Effect (slot 49) is left out: its skill_art_variation belongs to one skill's stats, not the character's.
    // A Flask Effect slot's flask_effect_variation belongs to its flask's buff (FlaskEffectVisual); its other stats stay.
    // The stats the client reads in place of a cosmetic's own join them (CosmeticDerivedStats): it does not work out a
    // derived stat for a player the server describes.
    public static IEnumerable<(uint Stat, int Value)> CosmeticStats(Player player)
    {
        var worn = WornCosmetics(player).Where(w => w.Slot != 49).SelectMany(w => MtxEffects.Of(w.MtxTypeHash) is not { } fx ? [] :
                (fx.Pet != 0 ? [.. fx.Stats, (w.Slot == 19 ? Stat.PetSlot2 : Stat.PetSlot1, fx.Pet)] : fx.Stats)
                    .Where(s => !IsFlaskEffectSlot(w.Slot) || s.Stat != Stat.FlaskEffectVariation))
            .GroupBy(s => s.Stat, s => s.Value).Select(g => (Stat: g.Key,
                Value: ActorScale.IsInput(g.Key) ? ActorScale.Saturate(g.Sum(v => (long)v)) : g.Sum())).ToList();
        return [.. worn, .. CosmeticDerivedStats(player, worn.ToDictionary(s => s.Stat, s => s.Value))];
    }

    public static IEnumerable<(uint Stat, int Value)> CosmeticScaleSources(Player player) =>
        CosmeticStats(player).Where(s => ActorScale.IsInput(s.Stat));

    // What the player wears changed (an item went into or out of a visible equipment row): 0x2bc Inventories for the model,
    // then the Actor skill list again, since the gems socketed in the equipment grant skills.
    public static async Task SendEquipment(Player player, PlayerInventories items, bool refreshCosmetics = true)
    {
        RefreshVaalSouls(player);
        // Refresh the canonical inactive qualification before reconciling grants: a swapped item may change another
        // worn item's requirements. Keep the same mutation lock as owned spawn commits.
        using (ProjectileMutation(player, advance: false, phase: "equipment output"))
        {
            RecomputePoolsAndSpeed(player);
            ReconcileBeastCompanionsLocked(player);
            player.PlayerMana = Math.Min(player.PlayerMana, (uint)UnreservedMana(player));
        }
        await SendModel(player, Looks(player, items), "equipment");
        // The socketed gems may have changed: the Cosmetics tab's skill slots follow them.
        if (refreshCosmetics) await SendWornCosmetics(player, "equipment changed");
        await SendSkills(player);
        await SendPlayerStatSnapshot(player,
            () => PlayerStatsOffFrames(player),
            "0x01b3 player stats (equipment)");
        await SendPlayerMana(player, $"0x01b1 player mana {player.PlayerMana} (equipment)");
        await CheckEquipmentAchievements(player, items);
    }

    public static async Task SendModel(Player player,
        IReadOnlyList<VisibleItem> equipped, string why)
    {
        player.ModelHands = HandsKey(equipped);
        await SendWorldRecord(player, player.Id, InstanceEntities.PlayerEquipmentUpdate(player.Id, equipped),
            $"0x02bc player equipment ({why}): {string.Join(", ", equipped.Select(e => $"slot {e.Slot} visual 0x{e.VisualIdentity:x4} stance {e.Stance}"))}");
    }

    // The hands' part of the model (main hand 0, off hand 1, a bow's quiver 7) as it was last sent.
    public static string HandsKey(IEnumerable<VisibleItem> equipped) => string.Join(",",
        equipped.Where(e => e.Slot is 0 or 1 or 7).OrderBy(e => e.Slot).Select(e => $"{e.Slot}:{e.VisualIdentity:x4}:{e.Type}"));

    // A weapon's requirements can start or stop being met while the equipment stays (a level, a passive, another item's
    // attributes): the model follows, so a weapon that became inactive leaves the hands and one that qualifies again returns.
    public static async Task SendModelIfHandsChanged(Player player)
    {
        var equipped = Looks(player, ItemsOf(player));
        if (player.ModelHands is { } sent && sent != HandsKey(equipped))
            await SendModel(player, equipped, "weapon requirements changed");
    }

    // Takes the held item off the cursor (0xbf); null when nothing is held.
    public static async Task<InventoryItem?> TakeHeld(Player player, PlayerInventories items, string why)
    {
        if (items.Items(PlayerInventories.Cursor) is not [var held, ..] || items.Remove(PlayerInventories.Cursor, held.Id) is null) return null;
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [held.Id], []), $"0x00bf cursor: remove item {held.Id} ({why})");
        return held;
    }
}
