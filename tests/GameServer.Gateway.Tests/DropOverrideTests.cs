using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

// The four hand-overs a mod uses to change a kill's loot (Agents/docs/dev/modding.md, "Hooks"): the equipment rarity
// chances the kill's roll starts from, whether a unique roll the rolled base has none of its own for takes one of the
// world's, what item level a unique drops at, and the quantity and currency multipliers. All of them are the mod's rule;
// the framework only applies what a subscriber set.
public sealed class DropOverrideTests
{
    static ItemDrops Drops()
    {
        var items = new ItemCatalog("data");
        // rolls is what makes an equipment drop roll a rarity at all (ItemDrops.RollOne does only when it is not null),
        // and uniques is what it can become; without them every equipment drop is normal whatever the chances say.
        return new ItemDrops(items, rolls: new ItemRolls(items), uniques: new UniqueCatalog(items));
    }

    // Every kill rolls the same way either way, so the same seed gives the same items: the currency multiplier takes no
    // draw of its own before the kill's own rolls, which is what lets these compare exactly instead of on average.
    static List<ItemDrops.Drop> Kills(ItemDrops drops, ItemDrops.Chances? chances = null, int level = 60,
        double quantityMultiplier = 1, double currencyMultiplier = 1, bool uniqueFromWorldPool = false, int? uniqueItemLevel = null)
    {
        var rng = new Random(20261113);
        var rolled = new List<ItemDrops.Drop>();
        for (int kill = 0; kill < 300; kill++)
            rolled.AddRange(drops.Roll(level, MonsterRarity.Normal, rng, chances: chances,
                quantityMultiplier: quantityMultiplier, currencyMultiplier: currencyMultiplier,
                uniqueFromWorldPool: uniqueFromWorldPool, uniqueItemLevel: uniqueItemLevel));
        return rolled;
    }

    static bool Equipment(ItemDrops.Drop drop) => ItemDrops.EquipmentClasses.Contains(drop.Type.Class);

    // A dropped currency item: the pool the roll's currency picks come from is the Metadata/Items/Currency bases.
    static bool Currency(ItemDrops.Drop drop) => drop.Type.Id.StartsWith("Metadata/Items/Currency/", StringComparison.Ordinal);

    static int CurrencyAmount(IEnumerable<ItemDrops.Drop> rolled) =>
        ItemDrops.Stacked(rolled).Where(Currency).Sum(d => d.Item.StackSize);

    static int CurrencyPiles(IEnumerable<ItemDrops.Drop> rolled) =>
        ItemDrops.Stacked(rolled).Count(Currency);

    // What the Easy Exile mod hands a kill: unique and normal exchanged, half the rare share moved to unique (a normal
    // monster: 73.5% unique, 2.5% rare, 23.7% magic, 0.3% white).
    static readonly ItemDrops.Chances Swapped = new(0.7352875, 0.0941589, 0.98746);

    [Fact]
    public void The_equipment_chances_and_the_world_pool_replace_the_monsters_own()
    {
        var drops = Drops();
        // All three chances zero: no equipment the kill rolls can come out magic, rare or unique.
        var plain = Kills(drops, new ItemDrops.Chances(0, 0, 0));
        int equipment = plain.Count(Equipment);
        Assert.True(equipment > 20, $"only {equipment} equipment drops to judge by");
        Assert.Equal(0, plain.Count(d => Equipment(d) && d.Item.Rarity != ItemRarity.Normal));
        // And the same kills on the monster's own chances do come out better, which is what makes that a test.
        int own = Kills(drops).Count(d => Equipment(d) && d.Item.Rarity != ItemRarity.Normal);
        Assert.True(own > 0, "the monster's own chances produced nothing better either");

        // Level 6, where most bases have no unique of their own. Without the world-pool hand-over a rolled unique falls
        // back to the rare the game makes of it (this is what made yellows everywhere); with it any of the world's
        // uniques may supply one, whatever the level, and the unique drops at the kill's own item level.
        var withPool = Kills(drops, Swapped, level: 6, uniqueFromWorldPool: true, uniqueItemLevel: 42).Where(Equipment).ToList();
        var fallback = Kills(drops, Swapped, level: 6).Where(Equipment).ToList();
        Assert.True(withPool.Count > 20, $"only {withPool.Count} equipment drops at level 6");
        var uniques = withPool.Where(d => d.Item.Rarity == ItemRarity.Unique).ToList();
        int fallen = fallback.Count(d => d.Item.Rarity == ItemRarity.Unique);
        Assert.True(uniques.Count > withPool.Count / 2, $"level 6 with the world pool: {uniques.Count}/{withPool.Count} unique");
        Assert.True(uniques.Count > fallen, $"the world pool: {uniques.Count} unique, the game's fallback: {fallen}");
        Assert.All(uniques, d => Assert.Equal(42, (int)d.Item.ItemLevel));
    }

    [Fact]
    public void The_item_and_currency_multipliers_add_drops_and_leave_the_odds_alone()
    {
        var drops = Drops();
        var plain = Kills(drops);

        // Twice as many items of every kind: this is the game's own quantity, so the mix keeps its own odds.
        var doubled = Kills(drops, quantityMultiplier: 2);
        Assert.InRange(doubled.Count, plain.Count * 8 / 5, plain.Count * 12 / 5);
        Assert.InRange(doubled.Count(Currency), plain.Count(Currency) * 8 / 5, plain.Count(Currency) * 12 / 5);

        // Three times as much currency, added rather than taken from anything else: the other kinds keep their own counts
        // (each run's own noise aside), currency's share of the drops roughly triples, and each extra pick is its own
        // item, so the ground gets more currency piles rather than one pile grown. The extra picks do take their own
        // draws, so the runs do not line up item for item — the odds are what stays put.
        var tripled = Kills(drops, currencyMultiplier: 3);
        int plainOther = plain.Count - plain.Count(Currency), tripledOther = tripled.Count - tripled.Count(Currency);
        Assert.InRange(tripledOther, plainOther * 4 / 5, plainOther * 6 / 5);
        int plainCurrency = plain.Count(Currency), tripledCurrency = tripled.Count(Currency);
        Assert.True(plainCurrency > 0, "no currency dropped to multiply");
        Assert.InRange(tripledCurrency, plainCurrency * 12 / 5, plainCurrency * 18 / 5);
        Assert.InRange(CurrencyAmount(tripled), CurrencyAmount(plain) * 12 / 5, CurrencyAmount(plain) * 18 / 5);
        // The share rises too — currency becomes the commonest thing on the ground — though not by three: the extra
        // picks are items of their own, so the total grows with them (3s / (1 + 2s) against s: 1.77x at the server's 35%).
        Assert.True(tripledCurrency * plain.Count > plainCurrency * tripled.Count * 3 / 2,
            $"currency share: {plainCurrency}/{plain.Count} -> {tripledCurrency}/{tripled.Count}");
        // And the ground does get more piles, not one pile grown. Not three times as many: the game's own stacking merges
        // one drop event's same-type currency (two Orb of Alteration rolls become a stack of two), so repeated picks of
        // the common ones grow a stack while the rarer ones add piles.
        Assert.True(CurrencyPiles(tripled) > CurrencyPiles(plain), $"currency piles: {CurrencyPiles(plain)} -> {CurrencyPiles(tripled)}");
    }
}
