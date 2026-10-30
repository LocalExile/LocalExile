using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class TransmutationDataTests
{
    [Theory]
    [InlineData("Metadata/Items/Weapons/OneHandWeapons/Wands/Wand1")]
    [InlineData("Metadata/Items/Armours/BodyArmours/BodyInt17")]
    [InlineData("Metadata/Items/Rings/Ring2")]
    [InlineData("Metadata/Items/Jewels/JewelStr")]
    [InlineData("Metadata/Items/Maps/MapTier1_1")]
    [InlineData("Metadata/Items/Flasks/FlaskLife1")]
    public void Native_bases_roll_eligible_affixes_and_keep_other_properties(string id)
    {
        var type = Program.Items.Find(id)!;
        var random = new Random(81);
        foreach (byte level in new byte[] { 1, 84 })
            for (int i = 0; i < 30; i++)
            {
                var before = ItemCatalog.Create(type) with { ItemLevel = level, Quality = 17, Charges = 13 };
                var after = CurrencyEffects.Transmutation(before, Program.Rolls, random);
                Assert.NotNull(after);
                Assert.Equal(ItemRarity.Magic, after.Rarity);
                Assert.True(after.Identified);
                Assert.Equal(17, after.Quality);
                Assert.Equal(13, after.Charges);
                Assert.Same(before.Sockets, after.Sockets);
                Assert.Same(before.Links, after.Links);
                Assert.Empty(after.NameWords);
                Assert.Equal(before.Mods, after.Mods.Where(m => m.Section != ModSection.Explicit));
                var explicits = after.Mods.Where(m => m.Section == ModSection.Explicit).ToArray();
                Assert.InRange(explicits.Length, 1, 2);
                var mods = explicits.Select(m => Program.Items.Mods.ByHash(m.Hash)!).ToArray();
                Assert.Equal(mods.Length, mods.Select(m => m.GenerationType).Distinct().Count());
                var families = mods.SelectMany(m => m.Families).ToArray();
                Assert.Equal(families.Length, families.Distinct().Count());
                foreach (var rolled in explicits)
                {
                    var mod = Program.Items.Mods.ByHash(rolled.Hash)!;
                    Assert.Equal(type.ModDomain, mod.Domain);
                    Assert.InRange(mod.Level, 0, level);
                    Assert.False(mod.EssenceOnly);
                    Assert.True(mod.WeightFor(type.Tags) > 0);
                    Assert.Equal(mod.Stats.Count, rolled.Values.Count);
                    for (int s = 0; s < mod.Stats.Count; s++)
                        Assert.InRange(rolled.Values[s], Math.Min(mod.Stats[s].Min, mod.Stats[s].Max),
                            Math.Max(mod.Stats[s].Min, mod.Stats[s].Max));
                }
            }
    }

    [Fact]
    public void Level_one_wand_pool_agrees_with_the_pinned_coe_beta_probe()
    {
        // Independent CoE beta 3.29.3.1.2 pool observation; no external library needed to run this regression.
        // The observation's provenance and limits are in Agents/docs/re/transmutation-coe.md.
        var expected = new Dictionary<string, int>
        {
            ["Intelligence1"] = 333, ["IncreasedManaWeapon1"] = 1000,
            ["LocalIncreasedPhysicalDamagePercentAndAccuracyRating1"] = 500,
            ["LocalIncreasedPhysicalDamagePercent1"] = 500, ["LocalAddedFireDamage1"] = 250,
            ["LocalIncreasedAttackSpeed1"] = 500, ["LifeGainedFromEnemyDeath1"] = 500,
            ["ManaGainedFromEnemyDeath1"] = 500, ["LocalCriticalStrikeChance1"] = 500,
            ["LocalIncreasedAccuracyNew1"] = 500, ["SpellAddedFireDamage1"] = 800,
            ["SpellAddedColdDamage1"] = 800, ["SpellAddedLightningDamage1"] = 800,
        };
        var actual = Program.Rolls.Pool(Program.Items.Find("Driftwood Wand")!, 1);
        Assert.Equal(expected.OrderBy(p => p.Key), actual.ToDictionary(p => p.Mod.Id, p => p.Weight).OrderBy(p => p.Key));
        Assert.Equal(7483, actual.Sum(p => p.Weight));
    }

    [Fact]
    public void Level_one_flask_pool_agrees_with_coe_and_can_only_roll_a_prefix()
    {
        var type = Program.Items.Find("Small Life Flask")!;
        var pool = Program.Rolls.Pool(type, 1);
        Assert.Equal(new[] { "FlaskIncreasedRecoveryAmount1", "FlaskIncreasedRecoverySpeed1" }, pool.Select(p => p.Mod.Id).Order());
        Assert.All(pool, p => { Assert.Equal(600, p.Weight); Assert.Equal(ItemRolls.Prefix, p.Mod.GenerationType); });
        for (int i = 0; i < 30; i++)
        {
            var item = CurrencyEffects.Transmutation(ItemCatalog.Create(type), Program.Rolls, new Random(i))!;
            Assert.Single(item.Mods, m => m.Section == ModSection.Explicit);
        }
    }

    [Fact]
    public void Upstream_supported_map_keys_can_be_upgraded_and_serialized()
    {
        var type = Program.Items.Find("Metadata/Items/Maps/MapKeyTier1")!;
        Assert.Contains("MapKey", type.Components);
        Assert.True(type.Supported);
        var before = ItemCatalog.Create(type);
        var after = CurrencyEffects.Transmutation(before, Program.Rolls, new Random(1));
        Assert.NotNull(after);
        Assert.Equal(ItemRarity.Magic, after.Rarity);
        Assert.Equal(before.TypeHash, after.TypeHash);
        Assert.InRange(after.Mods.Count(m => m.Section == ModSection.Explicit), 1, 2);
        Assert.NotEmpty(after.Payload());
    }
}
