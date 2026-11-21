using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class GamblingTests
{
    [Fact]
    public void Gamble_prices_item_levels_and_affix_tiers_follow_the_3_29_measurements()
    {
        // poewiki "Analysis of gold gambling", 3.29 price table: level → heavy, light, quiver, belt, ring, amulet.
        foreach (var (level, prices) in new (int, uint[])[] {
            (2, [28, 23, 51, 86, 109, 160]), (20, [111, 101, 107, 179, 227, 335]), (35, [283, 267, 214, 357, 452, 667]),
            (55, [820, 788, 537, 895, 1134, 1672]), (68, [1543, 1493, 976, 1627, 2061, 3037]), (100, [3089, 3005, 1944, 3241, 4105, 6050]) })
            for (int group = 0; group < prices.Length; group++)
                Assert.InRange(Gambling.Price((Gambling.PriceGroup)group, level), prices[group] - 1, prices[group] + 1);
        var random = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            Assert.InRange(Gambling.ItemLevel(2, random), 4, 6);
            Assert.InRange(Gambling.ItemLevel(50, random), 50, 52);
            Assert.InRange(Gambling.ItemLevel(100, random), 83, 86);
        }
        // The lowest eligible tier rolled, from the same page: 7th of 8 at item level 49, 7th of 10 at 69, 7th of 12 at 83.
        Assert.Equal(0, Gambling.ExcludedTiers(6, 37, ItemRarity.Rare));
        Assert.Equal(1, Gambling.ExcludedTiers(8, 50, ItemRarity.Rare));
        Assert.Equal(3, Gambling.ExcludedTiers(10, 70, ItemRarity.Rare));
        Assert.Equal(5, Gambling.ExcludedTiers(12, 84, ItemRarity.Rare));
        Assert.Equal(6, Gambling.ExcludedTiers(12, 84, ItemRarity.Magic));
        Assert.Equal(0, Gambling.ExcludedTiers(1, 84, ItemRarity.Rare));
        // poewiki 3.29 minimum price for alts: min(main_level - 20, 68), taking effect at main level 25 (body armour).
        Assert.Equal(28u, Gambling.Price(Gambling.PriceGroup.Heavy, Gambling.PriceLevel(2, 24)));
        Assert.Equal(32u, Gambling.Price(Gambling.PriceGroup.Heavy, Gambling.PriceLevel(2, 25)));
        Assert.Equal(211u, Gambling.Price(Gambling.PriceGroup.Heavy, Gambling.PriceLevel(2, 50)));
        Assert.Equal(1543u, Gambling.Price(Gambling.PriceGroup.Heavy, Gambling.PriceLevel(2, 88)));
        Assert.Equal(1543u, Gambling.Price(Gambling.PriceGroup.Heavy, Gambling.PriceLevel(2, 100)));
        Assert.Equal(75, Gambling.PriceLevel(75, 100));
        Assert.Equal(40, Gambling.PriceLevel(40, 50));
    }

    [Fact]
    public void Gamblers_offer_every_random_item_and_roll_identified_items_of_its_kind()
    {
        if (!ClientTables.Has("NPCShop")) return;
        Assert.True(Gambling.Gambles("Metadata/NPC/Act1/Bestel"));
        Assert.True(Gambling.Gambles("Metadata/NPC/League/Kalguur/VillageFaustusHideout"));
        Assert.False(Gambling.Gambles("Metadata/NPC/Act6/Bestel"));
        Assert.False(Gambling.Gambles("Metadata/NPC/Act1/Nessa"));
        var conn = new ClientState(new(1, "gamble", 100, 100, 100, 100, null, []), 1, 1);
        var offers = Gambling.CoreOffers(conn, 70);
        Assert.Equal(Gambling.Kinds.Length, offers.Count);
        Assert.All(offers, o => Assert.Equal("HiddenItem", o.Shown.Class));
        var items = Game.ItemsFor(conn);
        var random = new Random(3);
        foreach (var (kind, offer) in Gambling.Kinds.Zip(offers))
            for (int i = 0; i < 5; i++)
            {
                var item = offer.Roll(random);
                Assert.NotNull(item);
                var type = items.ByHash(item.TypeHash)!;
                Assert.Equal(kind.Class, type.Class);
                Assert.True(item.Identified);
                Assert.Contains(item.Rarity, new[] { ItemRarity.Magic, ItemRarity.Rare, ItemRarity.Unique });
                Assert.InRange(item.ItemLevel, 69, 72);
                if (item.Rarity != ItemRarity.Unique) Assert.InRange(item.Sockets.Count, 0, 6);
            }
        // Alt price floor through Gambling.Window: same-league higher-level character raises gold prices to its floor
        // while keeping rolled item levels at the alt's level; another league's character has no effect.
        TestEnvironment.OpenOwnSaves("gambling-alt-floor");
        try
        {
            var store = new GameServer.Storage.SqliteAccountStore(Saves.Db);
            var account = new GameServer.Core.Accounts.Account(Guid.NewGuid(), "gamble-alt-acct", "", "", DateTimeOffset.UtcNow);
            store.AddAccount(account);
            store.AddCharacter(new(Guid.NewGuid(), account.Id, "GambleMainStd", GameServer.Core.Accounts.CharacterClass.Witch, 100, "Standard", "1_1_town", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            store.AddCharacter(new(Guid.NewGuid(), account.Id, "GambleAltStd", GameServer.Core.Accounts.CharacterClass.Witch, 20, "Standard", "1_1_town", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            store.AddCharacter(new(Guid.NewGuid(), account.Id, "GambleAltHc", GameServer.Core.Accounts.CharacterClass.Witch, 20, "Hardcore", "1_1_town", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            uint nextId = 1;
            var stdAlt = new ClientState(new(1, "gamble", 100, 100, 100, 100, null, []), 1, 1) { Account = account.Name, Character = "GambleAltStd", League = "Standard" };
            stdAlt.Pools = stdAlt.Pools with { Level = 20 };
            var (_, stdOffers) = Gambling.Window(stdAlt, "Metadata/NPC/Act1/Bestel", () => nextId++, []);
            var stdRing = stdOffers.Values.Single(o => o.Shown.Id == "Metadata/Items/Rings/RingGamble");
            Assert.Equal(Gambling.Price(Gambling.PriceGroup.Ring, 68), stdRing.Gold);
            var rolledStd = stdRing.Roll(new Random(5));
            Assert.NotNull(rolledStd);
            Assert.InRange(rolledStd.ItemLevel, 20, 22);
            var hcAlt = new ClientState(new(1, "gamble", 100, 100, 100, 100, null, []), 1, 1) { Account = account.Name, Character = "GambleAltHc", League = "Hardcore" };
            hcAlt.Pools = hcAlt.Pools with { Level = 20 };
            var (_, hcOffers) = Gambling.Window(hcAlt, "Metadata/NPC/Act1/Bestel", () => nextId++, []);
            var hcRing = hcOffers.Values.Single(o => o.Shown.Id == "Metadata/Items/Rings/RingGamble");
            Assert.Equal(Gambling.Price(Gambling.PriceGroup.Ring, 20), hcRing.Gold);
        }
        finally { TestEnvironment.ReleaseOwnSaves(); }
    }
}
