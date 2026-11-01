using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public class FlaskOrbTests
{
    [Fact]
    public void Orb_pools_replace_one_enchantment_preserve_the_item_and_refuse_invalid_targets()
    {
        var catalog = new ItemCatalog("data");
        var type = catalog.All.First(t => t.Class == "UtilityFlask" && t.Supported);
        var original = ItemCatalog.Create(type) with { Quality = 20, Rarity = ItemRarity.Unique };
        var random = new Random(914);
        foreach (bool instilling in new[] { false, true })
        {
            var seen = new HashSet<ushort>();
            Item item = original;
            for (int i = 0; i < 1000; i++)
            {
                item = FlaskEnchantments.Apply(item, type, catalog.Mods, instilling, random)!;
                var enchant = Assert.Single(item.Mods, m => m.Section == ModSection.Enchant);
                var mod = catalog.Mods.ByHash(enchant.Hash)!;
                seen.Add(enchant.Hash);
                Assert.Equal(instilling ? 22 : 21, mod.GenerationType);
                Assert.Equal(original.Quality, item.Quality);
                Assert.Equal(original.Rarity, item.Rarity);
                Assert.Equal(original.Charges, item.Charges);
                Assert.All(item.Mods.Where(m => m.Section != ModSection.Enchant), m => Assert.Contains(m, original.Mods));
                for (int j = 0; j < enchant.Values.Count; j++)
                    Assert.InRange(enchant.Values[j], mod.RangeAt(j).Min, mod.RangeAt(j).Max);
            }
            Assert.Equal(instilling ? 15 : 5, seen.Count);
            var other = FlaskEnchantments.Apply(item, type, catalog.Mods, !instilling, random)!;
            Assert.Equal(instilling ? 21 : 22, catalog.Mods.ByHash(Assert.Single(other.Mods, m => m.Section == ModSection.Enchant).Hash)!.GenerationType);
            var overfilled = FlaskEnchantments.Apply(item with { Charges = 10000 }, type, catalog.Mods, true, random)!;
            Assert.Equal(type.MaxCharges, overfilled.Charges);
            Assert.Null(FlaskEnchantments.Apply(item with { Corrupted = true }, type, catalog.Mods, instilling, random));
            Assert.Null(FlaskEnchantments.Apply(item with { Mirrored = true }, type, catalog.Mods, instilling, random));
            foreach (string cls in new[] { "LifeFlask", "ManaFlask", "HybridFlask", "Tincture" })
            {
                var wrong = catalog.All.First(t => t.Class == cls && t.Supported);
                Assert.Null(FlaskEnchantments.Apply(ItemCatalog.Create(wrong), wrong, catalog.Mods, instilling, random));
            }
        }
    }

    [Fact]
    public void Enchants_drive_coefficients_charge_lock_and_all_enabled_trigger_conditions()
    {
        var catalog = new ItemCatalog("data");
        var type = catalog.All.First(t => t.Class == "UtilityFlask" && t.Name == "Quicksilver Flask");
        var plain = ItemCatalog.Create(type);
        var utility = catalog.UtilityFlaskOf(type)!;
        foreach (var mod in catalog.Mods.All.Where(m => m.GenerationType == 21))
        {
            var item = plain with { Mods = [.. plain.Mods, mod.With(ModSection.Enchant, mod.MaxValues(), new Random(1))] };
            var local = ItemModStats.Local(item, catalog.Mods);
            Assert.True(FlaskEnchantments.BlocksGain(local, active: true));
            Assert.False(FlaskEnchantments.BlocksGain(local, active: false));
            var charges = FlaskCharges.Of(utility.Charges, local);
            Assert.Equal(utility.MaxCharges + local.GetValueOrDefault("local_extra_max_charges"), charges.MaxCharges);
            Assert.Equal((int)Math.Floor(utility.ChargesPerUse * (100 + local.GetValueOrDefault("local_charges_used_+%")) / 100d), charges.ChargesPerUse);
            Assert.Equal(1 + local.GetValueOrDefault("local_flask_effect_+%") / 100d,
                FlaskEffectScaling.Multiplier(_ => 0, localIncrease: local.GetValueOrDefault("local_flask_effect_+%")));
        }
        var found = new HashSet<FlaskEnchantments.Trigger>();
        foreach (var mod in catalog.Mods.All.Where(m => m.GenerationType == 22 && m.WeightFor(type.Tags) > 0))
        {
            var item = plain with { Mods = [mod.Roll(ModSection.Enchant, new Random(1))] };
            var local = ItemModStats.Local(item, catalog.Mods);
            var trigger = Assert.Single(Enum.GetValues<FlaskEnchantments.Trigger>(), t => FlaskEnchantments.Matches(local, t, 2, 1, false));
            Assert.True(found.Add(trigger));
            if (trigger == FlaskEnchantments.Trigger.Adjacent)
            {
                Assert.False(FlaskEnchantments.Matches(local, trigger, 1, 1, false));
                Assert.False(FlaskEnchantments.Matches(local, trigger, 4, 0, false));
            }
            if (trigger == FlaskEnchantments.Trigger.RareHit) Assert.False(FlaskEnchantments.Matches(local, trigger, 2, 1, true));
        }
        Assert.Equal(15, found.Count);
    }
}
