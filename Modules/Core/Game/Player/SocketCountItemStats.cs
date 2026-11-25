using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // "per Red / Green / Blue / White Socket" on the item that carries the line (The Pariah, Prismatic Eclipse and
    // their kin): the line's value times that item's sockets of the colour, as the plain stat.
    static readonly (uint Source, uint Gives, SocketColour Colour)[] OwnSocketStats =
    [
        (Stat.BaseMaximumLifePerRedSocketOnItem, Stat.BaseMaximumLife, SocketColour.Red),
        (Stat.BaseMaximumEnergyShieldPerBlueSocketOnItem, Stat.BaseMaximumEnergyShield, SocketColour.Blue),
        (Stat.BaseMaximumManaPerGreenSocketOnItem, Stat.BaseMaximumMana, SocketColour.Green),
        (Stat.ItemFoundRarityPlusPctPerWhiteSocketOnItem, Stat.BaseItemFoundRarityPlusPct, SocketColour.White),
        (Stat.ItemFoundQuantityPlusPctPerWhiteSocketOnItem, Stat.BaseItemFoundQuantityPlusPct, SocketColour.White),
        (Stat.GlobalWeaponPhysicalDamagePlusPctPerRedSocketOnItem, Stat.PhysicalDamagePlusPct, SocketColour.Red),
        (Stat.GlobalAttackSpeedPlusPctPerGreenSocketOnItem, Stat.AttackSpeedPlusPct, SocketColour.Green),
        (Stat.GlobalManaLeechFromPhysicalAttackDamagePermyriadPerBlueSocketOnItem, Stat.ManaLeechFromPhysicalAttackDamagePermyriad, SocketColour.Blue),
        (Stat.GlobalLifeLeechFromPhysicalAttackDamagePerRedSocketOnItemPermyriad, Stat.LifeLeechFromPhysicalAttackDamagePermyriad, SocketColour.Red),
        (Stat.GlobalMeleeRangePlusPerWhiteSocketOnItem, Stat.MeleeRangePlus, SocketColour.White),
        (Stat.GlobalCriticalStrikeMulitplierPlusPerGreenSocketOnItem, Stat.BaseCriticalStrikeMultiplierPlus, SocketColour.Green),
        (Stat.GlobalCriticalStrikeChancePlusPctPerBlueSocketOnItem, Stat.CriticalStrikeChancePlusPct, SocketColour.Blue),
        (Stat.GlobalDefencesPlusPctPerWhiteSocketOnItem, Stat.GlobalDefencesPlusPct, SocketColour.White),
        (Stat.BaseSkillAreaOfEffectPlusPctPerRedSocketOnItem, Stat.BaseSkillAreaOfEffectPlusPct, SocketColour.Red),
    ];

    // "for each Empty Red / Green / Blue / White Socket on any Equipped Item" (The Untouched Soul): the character's
    // value times the empty sockets of the colour over everything worn.
    static readonly (uint Source, uint Gives, SocketColour Colour)[] EmptySocketStats =
    [
        (Stat.MaximumLifePlusPerEmptyRedSocket, Stat.BaseMaximumLife, SocketColour.Red),
        (Stat.AccuracyRatingPlusPerEmptyGreenSocket, Stat.AccuracyRating, SocketColour.Green),
        (Stat.MaximumManaPlusPerEmptyBlueSocket, Stat.BaseMaximumMana, SocketColour.Blue),
        (Stat.ElementalResistancePctPerEmptyWhiteSocket, Stat.BaseResistAllElementsPct, SocketColour.White),
    ];

    // Once for each fresh sum of the worn items' stats, before pools and skills read it. Nothing is counted unless
    // one of the lines is worn.
    public static void ApplySocketCountStats(IReadOnlyList<Item> active, ItemCatalog catalog, Dictionary<uint, int> stats)
    {
        bool own = false, empty = false;
        for (int i = 0; i < OwnSocketStats.Length && !own; i++) own = stats.ContainsKey(OwnSocketStats[i].Source);
        for (int i = 0; i < EmptySocketStats.Length && !empty; i++) empty = stats.ContainsKey(EmptySocketStats[i].Source);
        if (!own && !empty) return;
        Span<int> emptyByColour = stackalloc int[8];
        Span<int> byColour = stackalloc int[8];
        for (int a = 0; a < active.Count; a++)
        {
            var item = active[a];
            if (item.Sockets.Count == 0) continue;
            byColour.Clear();
            for (int s = 0; s < item.Sockets.Count; s++)
            {
                int colour = (int)item.Sockets[s].Colour;
                if (colour is < 1 or > 4) continue;
                byColour[colour]++;
                if (item.Sockets[s].Gem is null) emptyByColour[colour]++;
            }
            if (!own) continue;
            var itemStats = PlainItemSum(item, catalog.Mods);
            for (int i = 0; i < OwnSocketStats.Length; i++)
            {
                var (source, gives, colour) = OwnSocketStats[i];
                if (byColour[(int)colour] > 0 && itemStats.TryGetValue(source, out int value) && value != 0)
                    GolemAdd(stats, gives, ActorScale.Saturate((long)value * byColour[(int)colour]));
            }
        }
        if (!empty) return;
        for (int i = 0; i < EmptySocketStats.Length; i++)
        {
            var (source, gives, colour) = EmptySocketStats[i];
            if (emptyByColour[(int)colour] > 0 && stats.TryGetValue(source, out int value) && value != 0)
                GolemAdd(stats, gives, ActorScale.Saturate((long)value * emptyByColour[(int)colour]));
        }
    }
}
