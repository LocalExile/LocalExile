using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    public const string BurningItemCondition = "_while_burning";
    public const string NoOtherLifeModifiersCondition = "_if_no_life_modifiers_on_equipment_except_body_armour";
    // The passive masteries' equipment conditions (PassiveSkillMasteryEffects Armour4, Evasion5, ArmourEnergyShield5,
    // ArmourEvasion6, Life2, Staff4, TwoHand3, EvasionEnergyShield3).
    const string AllPiecesHaveArmour = "_if_all_equipment_grants_armour";
    const string AllPiecesHaveEvasion = "_if_all_equipment_grants_evasion";
    const string ArmourAndEnergyShieldCondition = "_if_gloves_helmet_boots_have_armour_and_energy_shield";
    const string HelmetEvasionCondition = "_if_helmet_grants_higher_evasion_than_armour";
    const string HelmetArmourCondition = "_if_helmet_grants_higher_armour_than_evasion";
    const string NoBodyLifeModifiersCondition = "_if_no_life_tags_on_body_armour";
    const string StaffRedBlueCondition = "_if_red_and_blue_socket_on_staff";
    const string MainHandRedGreenCondition = "_if_red_and_green_socket_on_main_hand_weapon";
    const string RingsEvasionCondition = "_if_both_rings_have_evasion_mod";
    const string AmuletEvasionCondition = "_if_amulet_has_evasion_mod";
    // The Evasion and Energy Shield Mastery's "while you have Energy Shield" (ItemConditionFlags sets it).
    public const string HaveEnergyShieldCondition = "_while_you_have_energy_shield";
    public static readonly IReadOnlySet<string> NamedItemConditionSuffixes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            BurningItemCondition, NoOtherLifeModifiersCondition, AllPiecesHaveArmour, AllPiecesHaveEvasion,
            ArmourAndEnergyShieldCondition, HelmetEvasionCondition, HelmetArmourCondition, NoBodyLifeModifiersCondition,
            StaffRedBlueCondition, MainHandRedGreenCondition, RingsEvasionCondition, AmuletEvasionCondition,
            HaveEnergyShieldCondition,
        };

    // Item records are immutable. Rechecking a condition does not need to recalculate their tooltip defences or tags.
    // Life is the modifier's client tag, including implicit/enchant/hidden lists and zero-valued rolls; an evasion
    // modifier is an explicit one with the evasion tag.
    sealed record WornItemFacts(ItemCatalog? Catalog, decimal Armour, decimal Evasion, int EnergyShield,
        bool LifeModifier, bool EvasionModifier);
    static readonly RuleTable<Item, WornItemFacts> WornFacts = new();
    static readonly WornItemFacts NoItem = new(null, 0, 0, 0, false, false);

    static WornItemFacts FactsOf(Item item, ItemCatalog catalog)
    {
        if (WornFacts.TryGetValue(item, out var known) && ReferenceEquals(known.Catalog, catalog)) return known;
        var (armour, evasion, energyShield) = catalog.ByHash(item.TypeHash) is { } type && catalog.Defences(type) is { } defences
            ? ItemDefences.Calculate(item, defences, catalog.Mods) : (0m, 0m, 0);
        bool life = false, evasionModifier = false;
        foreach (var mod in item.Mods)
            if (catalog.Mods.ByHash(mod.Hash)?.Tags is { } tags)
            {
                life |= tags.Contains("life");
                evasionModifier |= mod.Section == ModSection.Explicit && tags.Contains("evasion");
            }
        var facts = new WornItemFacts(catalog, armour, evasion, energyShield, life, evasionModifier);
        WornFacts.AddOrUpdate(item, facts);
        return facts;
    }

    // Socketed gems, flasks and passive jewels are not ordinary equipped armour/accessories/weapons in this collection.
    public static IReadOnlyDictionary<string, bool> QualifiedEquipmentItemFlags(
        IReadOnlyDictionary<string, bool> captured, IReadOnlyList<(byte Row, InventoryItem Item)> equipment,
        ItemCatalog catalog, IReadOnlyCollection<Item>? active = null)
    {
        bool Active(Item item) => active is null || active.Any(worn => ReferenceEquals(worn, item));
        bool lifeModifier = false;
        WornItemFacts helmet = NoItem, body = NoItem, gloves = NoItem, boots = NoItem, amulet = NoItem,
            ring1 = NoItem, ring2 = NoItem;
        Item? mainHand = null;
        for (int i = 0; i < equipment.Count; i++)
        {
            var (row, worn) = equipment[i];
            if (!Active(worn.Item)) continue;
            var facts = FactsOf(worn.Item, catalog);
            if (row != PlayerInventories.BodyArmour) lifeModifier |= facts.LifeModifier;
            switch (row)
            {
                case PlayerInventories.Helmet: helmet = facts; break;
                case PlayerInventories.BodyArmour: body = facts; break;
                case PlayerInventories.Gloves: gloves = facts; break;
                case PlayerInventories.Boots: boots = facts; break;
                case PlayerInventories.Amulet: amulet = facts; break;
                case PlayerInventories.Ring1: ring1 = facts; break;
                case PlayerInventories.Ring2: ring2 = facts; break;
                case PlayerInventories.Weapon1: mainHand = worn.Item; break;
            }
        }
        bool Sockets(SocketColour a, SocketColour b)
        {
            if (mainHand is null) return false;
            bool first = false, second = false;
            for (int i = 0; i < mainHand.Sockets.Count; i++)
            {
                first |= mainHand.Sockets[i].Colour == a;
                second |= mainHand.Sockets[i].Colour == b;
            }
            return first && second;
        }
        bool staff = mainHand is not null && catalog.ByHash(mainHand.TypeHash)?.Class is "Staff" or "Warstaff";
        static bool ArmourAndEnergyShield(WornItemFacts piece) => piece.Armour > 0 && piece.EnergyShield > 0;
        return WithFlags(captured,
            (NoOtherLifeModifiersCondition, !lifeModifier),
            (AllPiecesHaveArmour, helmet.Armour > 0 && body.Armour > 0 && gloves.Armour > 0 && boots.Armour > 0),
            (AllPiecesHaveEvasion, helmet.Evasion > 0 && body.Evasion > 0 && gloves.Evasion > 0 && boots.Evasion > 0),
            (ArmourAndEnergyShieldCondition, ArmourAndEnergyShield(helmet) && ArmourAndEnergyShield(gloves) && ArmourAndEnergyShield(boots)),
            (HelmetEvasionCondition, helmet.Evasion > helmet.Armour),
            (HelmetArmourCondition, helmet.Armour > helmet.Evasion),
            (NoBodyLifeModifiersCondition, !body.LifeModifier),
            (StaffRedBlueCondition, staff && Sockets(SocketColour.Red, SocketColour.Blue)),
            (MainHandRedGreenCondition, Sockets(SocketColour.Red, SocketColour.Green)),
            (RingsEvasionCondition, ring1.EvasionModifier && ring2.EvasionModifier),
            (AmuletEvasionCondition, amulet.EvasionModifier));
    }
}
