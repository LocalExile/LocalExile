using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Modules.Mercenary;

/// <summary>Rolled equipment, inventory placement mapping, exact <c>CanMercenaryEquipItem</c> (<c>0x142480d70</c>)
/// validation, visual reconstruction, and live stat computation for mercenaries.</summary>
public static class MercenaryEquipment
{
    public sealed record Worn(byte Slot, Item Item, VisibleItem Look);

    /// <summary>Resolved live stats for a mercenary (for both server combat and client <c>0x1b3</c> Defence/Loadout sync).</summary>
    public sealed record ComputedStats(
        MonsterModSet Mods,
        IReadOnlyDictionary<uint, int> StatValues,
        uint MaxLife,
        uint MaxEnergyShield,
        int EquippedUniqueCount,
        IReadOnlyList<(uint Key, int Value)> ClientStats);

    static readonly HashSet<string> TwoHandClasses = new(
        ["Two Hand Sword", "Two Hand Axe", "Two Hand Mace", "Staff", "Warstaff", "Bow", "FishingRod"],
        StringComparer.OrdinalIgnoreCase);

    public static List<Worn> Roll(MercenaryTables.Class kind, MercenaryTables.Build build, int level, Random rng,
        bool allUnique, UniqueCatalog uniques)
    {
        var result = new List<Worn>();
        var looks = MercenaryGear.Of(kind, build, level, rng);
        ushort? arrow = Program.Items.Visual("DefaultArrow");
        foreach (var look in looks)
        {
            if (look.Slot == MercenaryGear.MainHand && look.VisualIdentity == arrow) continue;
            int row = look.Type is > 0 and < 255 ? look.Type - 1
                : Program.Items.All.FirstOrDefault(t => t.VisualIdentity == look.VisualIdentity)?.ClassRow ?? -1;
            var pool = Program.Items.All.Where(t => t.Supported && t.CanBeMirrored && t.ClassRow == row && t.DropLevel <= level
                && !t.Id.Contains("Royale", StringComparison.Ordinal) && !t.Id.Contains("Demigod", StringComparison.Ordinal)
                && !t.Id.Contains("Talisman", StringComparison.Ordinal)
                && (!t.Components.Contains("Armour") || kind.AttributeTagIds.Count == 0
                    || t.Tags.Any(tag => kind.AttributeTagIds.Contains(tag))))
                .OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
            if (pool.Length == 0)
            {
                pool = Program.Items.All.Where(t => t.Supported && t.CanBeMirrored && t.ClassRow == row && t.DropLevel <= level
                    && !t.Id.Contains("Royale", StringComparison.Ordinal) && !t.Id.Contains("Demigod", StringComparison.Ordinal)
                    && !t.Id.Contains("Talisman", StringComparison.Ordinal)).OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
            }
            if (pool.Length == 0) continue;
            Item item;
            if (allUnique)
            {
                var candidates = uniques.All.Where(d => !d.EncounterOnly && !d.Corrupted && !d.Mirrored
                    && !MercenaryTables.IsDisabledUnique(d.Name)
                    && Program.Items.Find(d.BaseTypeId) is { } t && pool.Any(p => p.Row == t.Row)).OrderBy(d => d.Name, StringComparer.Ordinal).ToArray();
                item = null!;
                while (candidates.Length > 0 && item is null)
                {
                    int pick = rng.Next(candidates.Length);
                    item = uniques.Create(candidates[pick], rng)!;
                    candidates = [.. candidates.Where((_, i) => i != pick)];
                }
                if (item is null) throw new InvalidOperationException($"No mercenary unique for equipment class {row}");
            }
            else item = Program.Rolls.Roll(ItemCatalog.Create(pool[rng.Next(pool.Length)]) with { ItemLevel = (byte)level }, ItemRarity.Rare, rng);
            var wornLook = look with { VisualIdentity = item.VisualIdentity };
            result.Add(new(look.Slot, item with { ItemLevel = (byte)level }, wornLook));
        }
        return result;
    }

    /// <summary>Each Infamous Mercenary's own Infamous modifiers (Mods domain 41, item level 68), by its build name without
    /// "Infamous ". The data gives each modifier only its item slot (its spawn tag); which Mercenary wears it is the wiki's
    /// "Infamous modifiers by mercenary class" table. A name missing here (Cardinal, Warpriest of the Ruckus) has none.</summary>
    static readonly Dictionary<string, string[]> InfamousModifiers = new(StringComparer.Ordinal)
    {
        ["Ripper"] = ["MercenaryModRageDecaySpeed"], ["Eruptor"] = ["MercenaryModAshWarcries"],
        ["Earthshaker"] = ["MercenaryModAddCritPerExert"], ["Striker"] = ["MercenaryModDebilitate"],
        ["Bastion"] = ["MercenaryModFortificationDamageAura"], ["Swiftblade"] = ["MercenaryModUnaffectedShock"],
        ["Bloodletter"] = ["MercenaryModLifeCostOnLowLife"], ["Combatant"] = ["MercenaryModProjSpeedVariance"],
        ["Manyshot"] = ["MercenaryModClonesInheritGloves"], ["Toxicologist"] = ["MercenaryModSingleProjAOE"],
        ["Sniper"] = ["MercenaryModImpaleEffectFromDistance"], ["Flamequiver"] = ["MercenaryModLGOHIgnitedEnemies"],
        ["Blade Ambusher"] = ["MercenaryModTrapMineChain"], ["Bladecaster"] = ["MercenaryModMaxBlades"],
        ["Bladebitter"] = ["MercenaryModMaimDOT"], ["Shock Ambusher"] = ["MercenaryModMineLifeReserve"],
        ["Frosthand"] = ["MercenaryModCurseChillingAreas"], ["Withertouch"] = ["MercenaryModCurseEffect"],
        ["Reanimator"] = ["MercenaryModMinionFireConvertToChaos"], ["Stormhand"] = ["MercenaryModLightningSkillManaCostWhileShocked"],
        ["Cruel Mistress"] = ["MercenaryModSkipSacrifice"], ["Storming Zealot"] = ["MercenaryModConsecratedChaosRes"],
        ["Fallen Reverend"] = ["MercenaryModRecentMinionCrit", "MercenaryModRecoupWhileFrozen"],
        ["Warpriest"] = ["MercenaryModMinionCDR"], ["Kineticist"] = ["MercenaryModSpellslingerReservation"],
        ["Sanguimancer"] = ["MercenaryModTravelSkillCrit"], ["Mysterious Diver"] = ["MercenaryModReverseChillEffectChilledGround"],
    };

    /// <summary>[ESTIMATE] The wiki says an Infamous Mercenary "almost always" has an item with its Infamous modifier.</summary>
    const int InfamousModifierChancePct = 90;

    /// <summary>Adds an Infamous Mercenary's Infamous modifier, from item level 68, to the non-unique item it wears in that
    /// modifier's slot; equipment is returned as it is otherwise.</summary>
    public static List<Worn> WithInfamousModifier(List<Worn> equipment, MercenaryTables.Build build, int level, Random rng)
    {
        if (!build.Infamous || level < 68 || rng.Next(100) >= InfamousModifierChancePct
            || !InfamousModifiers.TryGetValue(build.Name.StartsWith("Infamous ", StringComparison.Ordinal) ? build.Name[9..] : build.Name,
                out var ids)) return equipment;
        var mod = Program.Items.Mods.ById(ids[rng.Next(ids.Length)]);
        if (mod is null || mod.SpawnWeights.FirstOrDefault(w => w.Weight > 0).Tag is not { } slotTag) return equipment;
        int at = equipment.FindIndex(w => w.Item.Rarity != ItemRarity.Unique
            && Program.Items.ByHash(w.Item.TypeHash)?.Tags.Contains(slotTag) == true);
        if (at < 0) return equipment;
        var values = mod.Stats.Select((_, i) => mod.RangeAt(i)).Select(r => rng.Next(r.Min, r.Max + 1)).ToArray();
        var worn = equipment[at];
        int cap = worn.Item.Rarity switch { ItemRarity.Rare => 3, ItemRarity.Magic => 1, _ => int.MaxValue };
        var added = new ItemMod(ModSection.Explicit, mod.Hash, values);
        var mods = new List<ItemMod>(worn.Item.Mods);
        if (mod.GenerationType is 1 or 2)
        {
            List<int> sameGen = [];
            for (int i = 0; i < mods.Count; i++)
            {
                if (mods[i].Section == ModSection.Explicit
                    && Program.Items.Mods.ByHash(mods[i].Hash) is { } existing
                    && existing.GenerationType == mod.GenerationType)
                    sameGen.Add(i);
            }
            if (sameGen.Count >= cap) mods[sameGen[rng.Next(sameGen.Count)]] = added;
            else mods.Add(added);
        }
        else mods.Add(added);
        equipment[at] = worn with { Item = worn.Item with { Mods = mods } };
        return equipment;
    }

    public static IReadOnlyList<VisibleItem> Looks(IReadOnlyList<Worn> equipment, IReadOnlyList<VisibleItem> fallback) =>
        [.. fallback.Select(l => equipment.FirstOrDefault(e => e.Slot == l.Slot)?.Look ?? l)];

    /// <summary>Maps a <see cref="MercenaryGear"/> visual slot to its <see cref="PlayerInventories"/> equipment row (1..10).</summary>
    public static byte? EquipmentRowOfVisualSlot(byte visualSlot, string? itemClass) => visualSlot switch
    {
        MercenaryGear.Body => PlayerInventories.BodyArmour,
        MercenaryGear.MainHand => PlayerInventories.Weapon1,
        MercenaryGear.OffHand => string.Equals(itemClass, "Bow", StringComparison.OrdinalIgnoreCase)
            ? PlayerInventories.Weapon1 : PlayerInventories.Offhand1,
        MercenaryGear.Back => PlayerInventories.Offhand1, // Quiver sits in Offhand1 slot
        MercenaryGear.Helmet => PlayerInventories.Helmet,
        MercenaryGear.Amulet => PlayerInventories.Amulet,
        MercenaryGear.Ring1 => PlayerInventories.Ring1,
        MercenaryGear.Ring2 => PlayerInventories.Ring2,
        MercenaryGear.Gloves => PlayerInventories.Gloves,
        MercenaryGear.Boots => PlayerInventories.Boots,
        MercenaryGear.Belt => PlayerInventories.Belt,
        _ => null,
    };

    /// <summary>Converts rolled <see cref="Worn"/> equipment into positioned <see cref="InventoryItem"/> entries at exact
    /// <c>MercenaryInventories</c> cell-X coordinates (<c>Y = 0</c>) for manager-1 encounter inventories or <c>Mercenary1..3</c>.</summary>
    public static List<InventoryItem> ToInventoryItems(IReadOnlyList<Worn> equipment, uint startingId = 1)
    {
        List<InventoryItem> items = [];
        HashSet<byte> occupiedRows = [];
        uint nextId = startingId;
        foreach (var worn in equipment)
        {
            string? cls = Program.Items.ByHash(worn.Item.TypeHash)?.Class;
            if (EquipmentRowOfVisualSlot(worn.Slot, cls) is not byte equipRow) continue;
            if (!occupiedRows.Add(equipRow)) continue;
            if (PlayerInventories.MercenarySlotForEquipmentRow(equipRow) is not { } spec) continue;
            items.Add(new InventoryItem(nextId++, spec.CellX, 0, worn.Item with { Identified = true }));
        }
        return items;
    }

    /// <summary>Builds <see cref="VisibleItem"/> entries for a mercenary entity's <c>Inventories</c> component from its
    /// equipped <c>Mercenary1..3</c>/<c>MercenaryTemporary</c> inventory items (with build armour defaults for empty slots).</summary>
    public static IReadOnlyList<VisibleItem> VisualsFromItems(MercenaryTables.Class kind, MercenaryTables.Build build,
        IReadOnlyList<InventoryItem> items)
    {
        var looks = new Dictionary<byte, VisibleItem>();
        // Fill default build armour art first; equipped items override each slot.
        foreach (var def in MercenaryGear.ArmourOf(build))
            looks[def.Slot] = def;

        ushort? arrow = Program.Items.Visual("DefaultArrow");
        foreach (var invItem in items)
        {
            if (PlayerInventories.MercenarySlotAtX(invItem.X) is not { } spec) continue;
            var baseType = Program.Items.ByHash(invItem.Item.TypeHash);
            if (baseType is null) continue;
            ushort vis = invItem.Item.VisualIdentity != 0 ? invItem.Item.VisualIdentity : baseType.VisualIdentity;
            byte type = baseType.ClassRow is >= 0 and < 255 ? (byte)(baseType.ClassRow + 1) : (byte)1;

            switch (spec.EquipmentRow)
            {
                case PlayerInventories.Weapon1:
                    if (string.Equals(baseType.Class, "Bow", StringComparison.OrdinalIgnoreCase))
                    {
                        if (arrow is ushort arrowVis)
                            looks[MercenaryGear.MainHand] = new(MercenaryGear.MainHand, arrowVis, baseType.Stance, type);
                        looks[MercenaryGear.OffHand] = new(MercenaryGear.OffHand, vis, BowSetup.NoStance, BowSetup.NoWeaponType);
                    }
                    else
                    {
                        looks[MercenaryGear.MainHand] = new(MercenaryGear.MainHand, vis, baseType.Stance, type);
                    }
                    break;
                case PlayerInventories.Offhand1:
                    if (string.Equals(baseType.Class, "Quiver", StringComparison.OrdinalIgnoreCase))
                        looks[MercenaryGear.Back] = new(MercenaryGear.Back, vis, BowSetup.NoStance, BowSetup.NoWeaponType);
                    else
                        looks[MercenaryGear.OffHand] = new(MercenaryGear.OffHand, vis, baseType.Stance, type);
                    break;
                case PlayerInventories.BodyArmour:
                    looks[MercenaryGear.Body] = new(MercenaryGear.Body, vis, BowSetup.NoStance, type);
                    break;
                case PlayerInventories.Helmet:
                    looks[MercenaryGear.Helmet] = new(MercenaryGear.Helmet, vis, BowSetup.NoStance, type);
                    break;
                case PlayerInventories.Gloves:
                    looks[MercenaryGear.Gloves] = new(MercenaryGear.Gloves, vis, BowSetup.NoStance, type);
                    break;
                case PlayerInventories.Boots:
                    looks[MercenaryGear.Boots] = new(MercenaryGear.Boots, vis, BowSetup.NoStance, type);
                    break;
                case PlayerInventories.Ring1:
                    looks[MercenaryGear.Ring1] = new(MercenaryGear.Ring1, vis, BowSetup.NoStance, type);
                    break;
                case PlayerInventories.Ring2:
                    looks[MercenaryGear.Ring2] = new(MercenaryGear.Ring2, vis, BowSetup.NoStance, type);
                    break;
                case PlayerInventories.Belt:
                    looks[MercenaryGear.Belt] = new(MercenaryGear.Belt, vis, BowSetup.NoStance, type);
                    break;
                case PlayerInventories.Amulet:
                    looks[MercenaryGear.Amulet] = new(MercenaryGear.Amulet, vis, BowSetup.NoStance, type);
                    break;
            }
        }
        return [.. looks.Values.OrderBy(v => v.Slot)];
    }

    /// <summary>Exact server-side mirror of client <c>CanMercenaryEquipItem</c> (<c>0x142480d70</c>), plus extensible
    /// <see cref="GameEvents.CompanionRules"/> / <see cref="GameEvents.CompanionEquip"/> hooks for mods.
    /// Returns <c>null</c> when allowed, or a descriptive refusal string when rejected.</summary>
    public static string? CanEquip(Player player, MercenaryTables.Class kind, MercenaryTables.Build build,
        byte mercLevel, bool isPermanent, byte equipmentRow, Item item, IReadOnlyList<InventoryItem>? currentItems = null)
    {
        if (!isPermanent) return "temporary mercenary gear is locked";
        if (Program.Items.ByHash(item.TypeHash) is not { } baseType) return "unknown item type";

        // 1. Identified check (0x142480de1)
        if (!item.Identified) return "mercenary cannot equip unidentified item";

        // Check Phase 2 / mod hooks
        var playerStats = PlayerStatValues(player);
        var rules = GameEvents.ResolveCompanionRules(player, playerStats.GetValueOrDefault(Stat.CanHaveActiveMercenaryFollower) > 0);
        var equipCheck = GameEvents.ResolveCompanionEquip(player, equipmentRow, item, mercLevel, isPermanent);
        if (equipCheck.Refusal is not null) return equipCheck.Refusal;
        bool waiveUniqueSlots = rules.AllowAllUniqueSlots || equipCheck.AllowUnique;

        // 2. Unique checks (0x142480df3..0x142481026)
        if (item.Rarity == ItemRarity.Unique)
        {
            string? uniqueName = item.CustomName ?? UniqueNameOf(item);
            if (MercenaryTables.IsDisabledUnique(uniqueName))
                return "mercenary cannot equip disabled unique item";

            if (!waiveUniqueSlots)
            {
                if (equipmentRow == PlayerInventories.BodyArmour)
                    return "mercenary cannot equip unique body armour";
                uint? reqStat = equipmentRow switch
                {
                    PlayerInventories.Ring1 or PlayerInventories.Ring2 or PlayerInventories.Ring3 => Stat.MercenaryCanUseUniqueRing,
                    PlayerInventories.Amulet => Stat.MercenaryCanUseUniqueAmulet,
                    PlayerInventories.Belt => Stat.MercenaryCanUseUniqueBelt,
                    PlayerInventories.Helmet => Stat.MercenaryCanUseUniqueHelmet,
                    PlayerInventories.Boots => Stat.MercenaryCanUseUniqueBoots,
                    PlayerInventories.Gloves => Stat.MercenaryCanUseUniqueGloves,
                    PlayerInventories.Weapon1 or PlayerInventories.Offhand1 => Stat.MercenaryCanUseUniqueWeaponsShieldsAndQuivers,
                    _ => null,
                };
                if (reqStat is uint stat && playerStats.GetValueOrDefault(stat) <= 0)
                    return "mercenary cannot equip that type of unique item";
            }
        }

        // 3. Level requirement check (0x142480f9b): floor(reqLevel * 70 / 100) <= mercLevel
        int reqLevel = Item.LevelRequirementOf?.Invoke(item) ?? item.RequiredLevel ?? baseType.DropLevel;
        int effectiveReq = Math.Max(0, reqLevel) * 70 / 100;
        if (effectiveReq > mercLevel)
            return $"mercenary level {mercLevel} is too low (needs {effectiveReq})";

        // 4. Armour attribute tag check (0x142481078..0x1424811e7)
        if (baseType.Components.Contains("Armour") && kind.AttributeTagIds.Count > 0)
        {
            bool tagMatch = baseType.Tags.Any(t => kind.AttributeTagIds.Contains(t));
            if (!tagMatch)
                return $"mercenary ({kind.Attribute}) cannot wear that armour type";
        }

        // 5. Weapon / Offhand wieldable class, offhand mode, and 2H cross-slot check (0x1424811e7..0x142481260)
        if (equipmentRow is PlayerInventories.Weapon1 or PlayerInventories.Offhand1)
        {
            if (build.WieldableItemClasses.Length > 0 && !build.WieldableItemClasses.Contains(baseType.ClassRow))
                return $"mercenary build {build.Name} cannot wield {baseType.Class}";

            if (equipmentRow == PlayerInventories.Offhand1)
            {
                if (build.OffhandMode == 0)
                    return $"mercenary build {build.Name} cannot use an offhand item";
                if (build.OffhandMode == 3
                    && baseType.ClassRow != MercenaryTables.ShieldClassRow
                    && baseType.ClassRow != MercenaryTables.QuiverClassRow)
                    return $"mercenary build {build.Name} can only use a shield or quiver in the offhand";
            }

            if (currentItems is not null)
            {
                if (equipmentRow == PlayerInventories.Offhand1
                    && PlayerInventories.MercenarySlotForEquipmentRow(PlayerInventories.Weapon1) is { } mainSpec
                    && currentItems.FirstOrDefault(i => i.X == mainSpec.CellX) is { } mainItem
                    && Program.Items.ByHash(mainItem.Item.TypeHash) is { } mainBt)
                {
                    bool mainIsBow = string.Equals(mainBt.Class, "Bow", StringComparison.OrdinalIgnoreCase);
                    bool offIsQuiver = baseType.ClassRow == MercenaryTables.QuiverClassRow;
                    if (mainIsBow && !offIsQuiver)
                        return "only a quiver can be equipped with a bow";
                    if (!mainIsBow && TwoHandClasses.Contains(mainBt.Class))
                        return "cannot equip offhand with a two-handed weapon";
                }
                else if (equipmentRow == PlayerInventories.Weapon1
                    && TwoHandClasses.Contains(baseType.Class)
                    && PlayerInventories.MercenarySlotForEquipmentRow(PlayerInventories.Offhand1) is { } offSpec
                    && currentItems.FirstOrDefault(i => i.X == offSpec.CellX) is { } offItem
                    && Program.Items.ByHash(offItem.Item.TypeHash) is { } offBt)
                {
                    bool newIsBow = string.Equals(baseType.Class, "Bow", StringComparison.OrdinalIgnoreCase);
                    bool offIsQuiver = offBt.ClassRow == MercenaryTables.QuiverClassRow;
                    if (!newIsBow || !offIsQuiver)
                        return "unequip offhand before equipping a two-handed weapon";
                }
            }
        }

        // 6. Abyssal socket count check (0x142481261..0x1424812da)
        if (item.Sockets.Count > 0)
        {
            int abyssCount = item.Sockets.Count(s => s.Colour == SocketColour.Abyss);
            if (abyssCount > 0)
            {
                var localStats = ItemModStats.Sum([item], Program.Items.Mods, local: true);
                int maxAbyss = localStats.GetValueOrDefault(Stat.LocalHasXAbyssSockets);
                if (abyssCount > maxAbyss)
                    return "mercenary cannot equip item with excess abyssal sockets";
            }
        }

        return null;
    }

    static string? UniqueNameOf(Item item) =>
        item.NameWords.Count > 0 ? MercenaryTables.WordTextOf(item.NameWords[0]) : null;

    public static MonsterModSet Effects(IReadOnlyList<Worn> equipment, int difficulty = 0)
    {
        var stats = ItemModStats.Sum(equipment.Select(e => e.Item), Program.Items.Mods);
        stats[Stat.MaximumLifePlusPct] = stats.GetValueOrDefault(Stat.MaximumLifePlusPct) + difficulty;
        stats[Stat.DamagePlusPct] = stats.GetValueOrDefault(Stat.DamagePlusPct) + difficulty;
        return new([], [], new MonsterModEffects(stats));
    }

    /// <summary>Computes live server combat modifiers and client <c>0x1b3</c> Defence/Luminary stats for a hired mercenary.</summary>
    public static ComputedStats ComputeStats(Player player, MercenaryTables.Class kind, MercenaryTables.Build build,
        byte mercLevel, byte areaLevel, IReadOnlyList<InventoryItem> items, bool isPermanent,
        IReadOnlyList<MercenaryTables.RolledSkill>? skills = null)
    {
        var stats = MercenaryTables.EvaluateExtraStats(build, mercLevel, areaLevel);
        var equipped = items.Select(i => i.Item).ToArray();
        var modStats = ItemModStats.Sum(equipped, Program.Items.Mods);
        foreach (var (k, v) in modStats)
            stats[k] = ActorScale.Saturate((long)stats.GetValueOrDefault(k) + v);

        // Local armour / evasion / energy shield from equipped Armour items
        int gearArmour = 0, gearEvasion = 0, gearEs = 0, gearBlock = 0;
        foreach (var item in equipped)
        {
            if (Program.Items.ByHash(item.TypeHash) is not { } bt || !bt.Components.Contains("Armour")) continue;
            var local = ItemModStats.Sum([item], Program.Items.Mods, local: true);
            var (baseArm, baseEva, baseShield) = Program.Items.Defences(bt)?.AtRoll(item.DefenceBaseRoll) ?? (0, 0, 0);
            int quality = item.Quality;
            double armPct = 1.0 + (local.GetValueOrDefault(Stat.LocalPhysicalDamageReductionRatingPlusPct) + quality) / 100.0;
            double evaPct = 1.0 + (local.GetValueOrDefault(Stat.LocalEvasionRatingPlusPct) + quality) / 100.0;
            double esPct = 1.0 + (local.GetValueOrDefault(Stat.LocalEnergyShieldPlusPct) + quality) / 100.0;
            gearArmour += (int)Math.Round((baseArm + local.GetValueOrDefault(Stat.LocalBasePhysicalDamageReductionRating)) * armPct);
            gearEvasion += (int)Math.Round((baseEva + local.GetValueOrDefault(Stat.LocalBaseEvasionRating)) * evaPct);
            gearEs += (int)Math.Round((baseShield + local.GetValueOrDefault(Stat.LocalEnergyShield)) * esPct);
            gearBlock += Program.Items.ShieldBlock(bt) + local.GetValueOrDefault(Stat.LocalAdditionalBlockChancePct);
        }
        if (gearArmour > 0) stats[Stat.BasePhysicalDamageReductionRating] = stats.GetValueOrDefault(Stat.BasePhysicalDamageReductionRating) + gearArmour;
        if (gearEvasion > 0) stats[Stat.BaseEvasionRating] = stats.GetValueOrDefault(Stat.BaseEvasionRating) + gearEvasion;
        if (gearEs > 0) stats[Stat.BaseMaximumEnergyShield] = stats.GetValueOrDefault(Stat.BaseMaximumEnergyShield) + gearEs;
        if (gearBlock > 0) stats[Stat.MonsterBaseBlockPct] = stats.GetValueOrDefault(Stat.MonsterBaseBlockPct) + gearBlock;

        // Strength/Dex/Int totals (MercenaryBuildExtraStats uses BaseStrength/Dex/Int; gear uses Additional*) and inherent bonuses
        int allAttr = stats.GetValueOrDefault(Stat.AdditionalAllAttributes);
        int str = Math.Max(0, stats.GetValueOrDefault(Stat.Strength)
            + stats.GetValueOrDefault(Stat.BaseStrength)
            + stats.GetValueOrDefault(Stat.AdditionalStrength)
            + stats.GetValueOrDefault(Stat.AdditionalStrengthAndDexterity)
            + stats.GetValueOrDefault(Stat.AdditionalStrengthAndIntelligence)
            + stats.GetValueOrDefault(Stat.BaseStrengthAndDexterity)
            + stats.GetValueOrDefault(Stat.BaseStrengthAndIntelligence)
            + allAttr);
        int dex = Math.Max(0, stats.GetValueOrDefault(Stat.Dexterity)
            + stats.GetValueOrDefault(Stat.BaseDexterity)
            + stats.GetValueOrDefault(Stat.AdditionalDexterity)
            + stats.GetValueOrDefault(Stat.AdditionalStrengthAndDexterity)
            + stats.GetValueOrDefault(Stat.AdditionalDexterityAndIntelligence)
            + stats.GetValueOrDefault(Stat.BaseStrengthAndDexterity)
            + stats.GetValueOrDefault(Stat.BaseDexterityAndIntelligence)
            + allAttr);
        int intel = Math.Max(0, stats.GetValueOrDefault(Stat.Intelligence)
            + stats.GetValueOrDefault(Stat.BaseIntelligence)
            + stats.GetValueOrDefault(Stat.AdditionalIntelligence)
            + stats.GetValueOrDefault(Stat.AdditionalStrengthAndIntelligence)
            + stats.GetValueOrDefault(Stat.AdditionalDexterityAndIntelligence)
            + stats.GetValueOrDefault(Stat.BaseStrengthAndIntelligence)
            + stats.GetValueOrDefault(Stat.BaseDexterityAndIntelligence)
            + allAttr);
        stats[Stat.Strength] = str;
        stats[Stat.Dexterity] = dex;
        stats[Stat.Intelligence] = intel;
        stats[Stat.BaseMaximumLife] = stats.GetValueOrDefault(Stat.BaseMaximumLife) + str / 2;
        stats[Stat.MeleePhysicalDamagePlusPct] = stats.GetValueOrDefault(Stat.MeleePhysicalDamagePlusPct) + str / 5;
        stats[Stat.AccuracyRating] = stats.GetValueOrDefault(Stat.AccuracyRating) + dex * 2;
        stats[Stat.EvasionRatingPlusPct] = stats.GetValueOrDefault(Stat.EvasionRatingPlusPct) + dex / 5;
        stats[Stat.MaximumEnergyShieldPlusPct] = stats.GetValueOrDefault(Stat.MaximumEnergyShieldPlusPct) + intel / 5;

        int uniqueCount = equipped.Count(i => i.Rarity == ItemRarity.Unique);
        var bonuses = LuminaryBonusesOf(player);
        var clientStats = new List<(uint Key, int Value)>();

        // Permanent mercenary Luminary scaling & client mirror stats (temporary mercenaries never receive Luminary bonuses)
        if (isPermanent)
        {
            stats[Stat.IsPermanentMercenary] = 1;
            if (bonuses.MaxLifeIncreasedPct != 0)
                stats[Stat.MaximumLifePlusPct] = stats.GetValueOrDefault(Stat.MaximumLifePlusPct) + bonuses.MaxLifeIncreasedPct;
            if (bonuses.DamageIncreasedPct != 0)
                stats[Stat.DamagePlusPct] = stats.GetValueOrDefault(Stat.DamagePlusPct) + bonuses.DamageIncreasedPct;
            if (bonuses.DamageMoreFromLuminaryPct != 0)
                stats[Stat.DamagePlusPctFinalFromLuminary] = bonuses.DamageMoreFromLuminaryPct;
            if (bonuses.DamageMorePerUniquePct != 0)
                stats[Stat.DamagePlusPctFinalPerEquippedUnique] = bonuses.DamageMorePerUniquePct * uniqueCount;
            if (bonuses.TauntOnHitPct > 0)
                stats[Stat.ChanceToTauntOnHitPct] = stats.GetValueOrDefault(Stat.ChanceToTauntOnHitPct) + bonuses.TauntOnHitPct;

            clientStats.AddRange(LuminaryCompanionStats(player, uniqueCount, isPermanent: true));

            // Ensure all 7 unique-slot flags (0x59a1..0x59a7) are explicitly written (including 0s when unallocated, or 1s under Phase 2 mod override)
            var rules = GameEvents.ResolveCompanionRules(player, bonuses.CanHavePermanentFollower);
            foreach (var (uStat, allowed) in new (uint, bool)[]
            {
                (Stat.MercenaryCanUseUniqueRing, rules.AllowAllUniqueSlots || bonuses.CanUseUniqueRing),
                (Stat.MercenaryCanUseUniqueAmulet, rules.AllowAllUniqueSlots || bonuses.CanUseUniqueAmulet),
                (Stat.MercenaryCanUseUniqueBelt, rules.AllowAllUniqueSlots || bonuses.CanUseUniqueBelt),
                (Stat.MercenaryCanUseUniqueHelmet, rules.AllowAllUniqueSlots || bonuses.CanUseUniqueHelmet),
                (Stat.MercenaryCanUseUniqueBoots, rules.AllowAllUniqueSlots || bonuses.CanUseUniqueBoots),
                (Stat.MercenaryCanUseUniqueGloves, rules.AllowAllUniqueSlots || bonuses.CanUseUniqueGloves),
                (Stat.MercenaryCanUseUniqueWeaponsShieldsAndQuivers, rules.AllowAllUniqueSlots || bonuses.CanUseUniqueWeaponsShieldsAndQuivers),
            })
            {
                clientStats.RemoveAll(s => s.Key == uStat);
                clientStats.Add((uStat, allowed ? 1 : 0));
            }
        }
        else
        {
            clientStats.Add((Stat.IsPermanentMercenary, 0));
        }

        // Compute effective Life, ES, Armour, Evasion, and Resistances for client Defence tab (0x1405a8620)
        var variety = Monsters.Find(kind.AlliedPath) ?? Monsters.Find(kind.Path);
        int baseLife = (variety is not null ? Monsters.BaseLife(variety, mercLevel) : null) ?? Math.Max(100, mercLevel * 45);
        var modEffects = new MonsterModEffects(stats);
        uint maxLife = (uint)Math.Max(1, modEffects.MaxLife(baseLife, mercLevel));

        int baseEs = stats.GetValueOrDefault(Stat.BaseMaximumEnergyShield);
        int incEs = stats.GetValueOrDefault(Stat.MaximumEnergyShieldPlusPct);
        uint maxEs = (uint)Math.Max(0, (long)Math.Round(baseEs * Math.Max(0, 1 + incEs / 100.0)));

        int baseArmour = stats.GetValueOrDefault(Stat.BasePhysicalDamageReductionRating);
        int incArmour = stats.GetValueOrDefault(Stat.PhysicalDamageReductionRatingPlusPct)
            + stats.GetValueOrDefault(Stat.CombinedArmourAndEvasionRatingPlusPct);
        int finalArmour = Math.Max(0, (int)Math.Round(baseArmour * Math.Max(0, 1 + incArmour / 100.0)));

        int baseEvasion = (Monsters.BaseEvasion(mercLevel) ?? 0) + stats.GetValueOrDefault(Stat.BaseEvasionRating);
        int incEvasion = stats.GetValueOrDefault(Stat.EvasionRatingPlusPct)
            + stats.GetValueOrDefault(Stat.CombinedArmourAndEvasionRatingPlusPct);
        int finalEvasion = Math.Max(0, (int)Math.Round(baseEvasion * Math.Max(0, 1 + incEvasion / 100.0)));

        int allEle = stats.GetValueOrDefault(Stat.BaseResistAllElementsPct);
        int maxAllEle = stats.GetValueOrDefault(Stat.AdditionalMaximumAllElementalResistancesPct);
        int baseFireRes = HitMitigation.Resistance(Monsters, variety, mercLevel, DamageType.Fire).Value;
        int baseColdRes = HitMitigation.Resistance(Monsters, variety, mercLevel, DamageType.Cold).Value;
        int baseLightRes = HitMitigation.Resistance(Monsters, variety, mercLevel, DamageType.Lightning).Value;
        int baseChaosRes = HitMitigation.Resistance(Monsters, variety, mercLevel, DamageType.Chaos).Value;
        int uncappedFire = baseFireRes + allEle + stats.GetValueOrDefault(Stat.BaseFireDamageResistancePct);
        int uncappedCold = baseColdRes + allEle + stats.GetValueOrDefault(Stat.BaseColdDamageResistancePct);
        int uncappedLight = baseLightRes + allEle + stats.GetValueOrDefault(Stat.BaseLightningDamageResistancePct);
        int uncappedChaos = baseChaosRes + stats.GetValueOrDefault(Stat.BaseChaosDamageResistancePct);

        int maxFire = Math.Clamp(75 + maxAllEle + stats.GetValueOrDefault(Stat.BaseMaximumFireDamageResistancePct), -100, 90);
        int maxCold = Math.Clamp(75 + maxAllEle + stats.GetValueOrDefault(Stat.BaseMaximumColdDamageResistancePct), -100, 90);
        int maxLight = Math.Clamp(75 + maxAllEle + stats.GetValueOrDefault(Stat.BaseMaximumLightningDamageResistancePct), -100, 90);
        int maxChaos = Math.Clamp(75 + stats.GetValueOrDefault(Stat.BaseMaximumChaosDamageResistancePct), -100, 90);

        int cappedFire = Math.Min(uncappedFire, maxFire);
        int cappedCold = Math.Min(uncappedCold, maxCold);
        int cappedLight = Math.Min(uncappedLight, maxLight);
        int cappedChaos = Math.Min(uncappedChaos, maxChaos);

        clientStats.Add((Stat.IsHiredMercenary, 1));
        clientStats.Add((Stat.MaximumLife, (int)Math.Min(int.MaxValue, maxLife)));
        clientStats.Add((Stat.MaximumEnergyShield, (int)Math.Min(int.MaxValue, maxEs)));
        clientStats.Add((Stat.PhysicalDamageReductionRating, finalArmour));
        clientStats.Add((Stat.EvasionRating, finalEvasion));
        clientStats.Add((Stat.FireDamageResistancePct, cappedFire));
        clientStats.Add((Stat.ColdDamageResistancePct, cappedCold));
        clientStats.Add((Stat.LightningDamageResistancePct, cappedLight));
        clientStats.Add((Stat.ChaosDamageResistancePct, cappedChaos));
        clientStats.Add((Stat.UncappedFireDamageResistancePct, uncappedFire));
        clientStats.Add((Stat.UncappedColdDamageResistancePct, uncappedCold));
        clientStats.Add((Stat.UncappedLightningDamageResistancePct, uncappedLight));
        clientStats.Add((Stat.UncappedChaosDamageResistancePct, uncappedChaos));

        return new ComputedStats(new([], [], modEffects), stats, maxLife, maxEs, uniqueCount, clientStats);
    }
}
