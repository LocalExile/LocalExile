using System.Reflection;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class InherentAttributeBonusTests
{
    static SkillCatalog.ClassInfo Attributes(int strength = 100, int dexterity = 100, int intelligence = 100) =>
        SkillCatalog.ClassInfo.Witch with { Strength = strength, Dexterity = dexterity, Intelligence = intelligence };

    static CharacterStats.Bonus Flags(int mask) => new(
        NoLifeFromStrength: (mask & 1) != 0, NoManaFromIntelligence: (mask & 2) != 0,
        NoStrengthBonuses: (mask & 4) != 0, NoDexterityBonuses: (mask & 8) != 0,
        NoIntelligenceBonuses: (mask & 16) != 0);

    [Theory]
    [InlineData(0, 100u, 90u, 202, 20, 110u, 120)]
    [InlineData(1, 50u, 90u, 202, 20, 110u, 120)]
    [InlineData(2, 100u, 40u, 202, 20, 110u, 120)]
    [InlineData(4, 50u, 90u, 202, 0, 110u, 120)]
    [InlineData(8, 100u, 90u, 2, 20, 110u, 100)]
    [InlineData(16, 100u, 40u, 202, 20, 100u, 120)]
    [InlineData(5, 50u, 90u, 202, 0, 110u, 120)]
    [InlineData(18, 100u, 40u, 202, 20, 100u, 120)]
    [InlineData(31, 50u, 40u, 2, 0, 100u, 100)]
    public void Each_prohibition_removes_only_its_inherent_terms_and_overlaps_apply_once(int mask,
        uint life, uint mana, int accuracy, int melee, uint energyShield, int evasion)
    {
        var pools = CharacterStats.For(Attributes(), 1, Flags(mask) with { EnergyShield = 100, Evasion = 85 });

        Assert.Equal((life, mana, accuracy, melee, energyShield, evasion),
            (pools.Life, pools.Mana, pools.Accuracy, pools.MeleePhysicalDamagePct, pools.EnergyShield, pools.Evasion));
        Assert.Equal((100, 100, 100), (pools.Strength, pools.Dexterity, pools.Intelligence));
        Assert.True(pools.AttributesResolved);
    }

    [Theory]
    [InlineData(1, 50u, 40u, 4, 0, 100u, 100)]
    [InlineData(2, 51u, 41u, 6, 0, 100u, 100)]
    [InlineData(3, 51u, 41u, 8, 0, 100u, 100)]
    [InlineData(4, 52u, 42u, 10, 0, 100u, 100)]
    [InlineData(5, 52u, 42u, 12, 1, 100u, 101)]
    [InlineData(9, 54u, 44u, 20, 1, 100u, 101)]
    [InlineData(10, 55u, 45u, 22, 2, 101u, 102)]
    [InlineData(19, 59u, 49u, 40, 3, 101u, 103)]
    [InlineData(20, 60u, 50u, 42, 4, 102u, 104)]
    public void Complete_half_fifth_and_tenth_attribute_boundaries_preserve_attributes_when_prohibited(int value,
        uint life, uint mana, int accuracy, int melee, uint energyShield, int evasion)
    {
        var source = new CharacterStats.Bonus(EnergyShield: 100, Evasion: 85);
        var ordinary = CharacterStats.For(Attributes(value, value, value), 1, source);
        Assert.Equal((life, mana, accuracy, melee, energyShield, evasion),
            (ordinary.Life, ordinary.Mana, ordinary.Accuracy, ordinary.MeleePhysicalDamagePct,
                ordinary.EnergyShield, ordinary.Evasion));

        var prohibited = CharacterStats.For(Attributes(value, value, value), 1, source with
        {
            NoStrengthBonuses = true, NoDexterityBonuses = true, NoIntelligenceBonuses = true,
        });
        Assert.Equal((50u, 40u, 2, 0, 100u, 100),
            (prohibited.Life, prohibited.Mana, prohibited.Accuracy, prohibited.MeleePhysicalDamagePct,
                prohibited.EnergyShield, prohibited.Evasion));
        Assert.Equal((value, value, value), (prohibited.Strength, prohibited.Dexterity, prohibited.Intelligence));
    }

    [Fact]
    public void Final_scaled_attributes_still_feed_every_item_group_and_independent_accuracy_source()
    {
        var pools = CharacterStats.For(Attributes(9, 4, 9), 1, new(
            Strength: 1, Dexterity: 1, Intelligence: 1,
            StrengthPct: 75, DexterityPct: 75, IntelligencePct: 75, AllAttributesPct: 25,
            NoStrengthBonuses: true, NoDexterityBonuses: true, NoIntelligenceBonuses: true,
            Life: 7, LifePct: 25, Mana: 9, ManaPct: 25,
            LifePerLevel: 2, ManaPerLevel: -1, ManaPerFourStrength: 8,
            LifePerTenDexterity: 2, LifePerTenIntelligence: 3, LifePerFourDexterity: 1, LifePerTwoIntelligence: 1,
            Accuracy: 7, AccuracyPct: 25, AccuracyPerLevel: 3,
            DexterityAccuracyTriple: true, AccuracyEqualsIntelligence: true, MeleePhysicalDamagePct: 7,
            EnergyShield: 100, EnergyShieldPct: 13, EnergyShieldPctPerTenStrength: 1,
            Evasion: 85, EvasionPct: 17, ColdOvercapEvasionPct: 11, EvasionPctPerTenIntelligence: 2));

        Assert.Equal((20, 10, 20), (pools.Strength, pools.Dexterity, pools.Intelligence));
        Assert.Equal(98u, pools.Life); // (38+12+7+2+2+6+2+10)*1.25 floors79*1.25.
        Assert.Equal(110u, pools.Mana); // (34+6+9-1+5*8)*1.25; inherent Int Mana alone is absent.
        Assert.Equal(40, pools.Accuracy); // (5 per level +7 flat +20 Int)*1.25; mastery3 per Dex is absent.
        Assert.Equal(7, pools.MeleePhysicalDamagePct);
        Assert.Equal((115u, 15, 132, 32),
            (pools.EnergyShield, pools.EnergyShieldPct, pools.Evasion, pools.EvasionPct));
    }

    [Theory]
    [InlineData(false, false, 178, 184, 155u)]
    [InlineData(true, false, 443, 0, 155u)]
    [InlineData(false, true, 145, 139, 119u)]
    [InlineData(true, true, 356, 0, 119u)]
    public void Prohibitions_preserve_item_global_shared_slot_and_more_terms_including_Iron_Reflexes(
        bool ironReflexes, bool noShield, int armour, int evasion, uint energyShield)
    {
        var source = new CharacterStats.Bonus(
            Armour: 100, Evasion: 85, EnergyShield: 100,
            ArmourPct: 20, EvasionPct: 20, EnergyShieldPct: 20, SharedDefencesPct: 20,
            ColdOvercapEvasionPct: 5, EnergyShieldPctPerTenStrength: 1, EvasionPctPerTenIntelligence: 2,
            ShieldArmour: 20, ShieldEvasion: 20, ShieldEnergyShield: 20,
            BodyArmour: 30, BodyEvasion: 30, BodyEnergyShield: 30,
            ShieldArmourPct: 20, ShieldEvasionPct: 30, ShieldEnergyShieldPct: 40, SharedShieldDefencesPct: 10,
            BodyEnergyShieldPct: 50, DoubleBodyArmour: true, ArmourMorePct: 10, EvasionMorePct: 20,
            IronReflexes: ironReflexes, NoShieldDefences: noShield,
            NoStrengthBonuses: true, NoDexterityBonuses: true, NoIntelligenceBonuses: true);
        var pools = CharacterStats.For(Attributes(), 1, source);

        // Ordinary EV INC45 and ES INC30 retain the item terms. IR removes no second Dexterity term and
        // converts with20 armour +45 ordinary EV -20 shared =45, including slot/MORE effects once.
        Assert.Equal((armour, evasion, energyShield), (pools.Armour, pools.Evasion, pools.EnergyShield));
        Assert.Equal((45, 30), (pools.EvasionPct, pools.EnergyShieldPct));
        if (ironReflexes)
        {
            var withDexterityAccuracy = CharacterStats.For(Attributes(), 1, source with { NoDexterityBonuses = false });
            Assert.Equal((pools.Armour, pools.Evasion, pools.EvasionPct, pools.EnergyShield),
                (withDexterityAccuracy.Armour, withDexterityAccuracy.Evasion,
                    withDexterityAccuracy.EvasionPct, withDexterityAccuracy.EnergyShield));
            Assert.Equal(2, pools.Accuracy);
            Assert.Equal(202, withDexterityAccuracy.Accuracy);
        }
    }

    [Fact]
    public void Extreme_attributes_remain_available_to_signed_wide_item_terms_after_all_inherent_grants_are_removed()
    {
        var pools = CharacterStats.For(Attributes(int.MaxValue, int.MaxValue, int.MaxValue), 1,
            Flags(31) with
            {
                LifePerTwoIntelligence = 1, ManaPerFourStrength = 8,
                EnergyShield = 100, EnergyShieldPctPerTenStrength = 1,
                Evasion = 85, EvasionPctPerTenIntelligence = 2, MeleePhysicalDamagePct = int.MinValue,
            });

        Assert.Equal((int.MaxValue, int.MaxValue, int.MaxValue), (pools.Strength, pools.Dexterity, pools.Intelligence));
        Assert.Equal(1_073_741_873u, pools.Life);
        Assert.Equal((uint)int.MaxValue, pools.Mana);
        Assert.Equal((214_748_464u, 429_496_828, 2, int.MinValue),
            (pools.EnergyShield, pools.Evasion, pools.Accuracy, pools.MeleePhysicalDamagePct));
    }

    [Theory]
    [InlineData(InherentAttributeBonuses.NoLifeFromStrength, 7964u, true, false, false)]
    [InlineData(InherentAttributeBonuses.NoManaFromIntelligence, 7965u, false, true, false)]
    [InlineData(InherentAttributeBonuses.NoStrengthBonuses, 15532u, true, false, false)]
    [InlineData(InherentAttributeBonuses.NoDexterityBonuses, 18387u, false, false, false)]
    [InlineData(InherentAttributeBonuses.NoIntelligenceBonuses, 18388u, false, true, true)]
    public void Exact_native_inputs_classify_only_the_resource_pools_they_can_change(
        string name, uint key, bool life, bool mana, bool energyShield)
    {
        Assert.Equal(key, new StatCatalog("data").Key(name)!.Value);
        Assert.True(InherentAttributeBonuses.IsInput(name));
        Assert.Equal(life, InherentAttributeBonuses.IsLifeInput(name));
        Assert.Equal(mana, InherentAttributeBonuses.IsManaInput(name));
        Assert.Equal(energyShield, InherentAttributeBonuses.IsEnergyShieldInput(name));
        Assert.False(InherentAttributeBonuses.IsInput("local_" + name));
    }

    static CharacterStats.Bonus SourceBonus(Dictionary<uint, int> raw) =>
        (CharacterStats.Bonus)typeof(Program).GetMethod("PassiveBonus", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [raw, default(WeaponProfile), AttributeModifiers.From(name =>
                Program.Stats.Key(name) is uint key ? raw.GetValueOrDefault(key) : 0)])!;

    [Fact]
    public void Unsourced_Dexterity_evasion_key_is_not_an_alias_for_broad_Dexterity_or_Iron_Reflexes()
    {
        const string evasionOnly = "gain_no_inherent_evasion_rating_+%_from_dexterity";
        Assert.Equal(18620u, new StatCatalog("data").Key(evasionOnly)!.Value);
        Assert.False(InherentAttributeBonuses.IsInput(evasionOnly));
        Assert.False(InherentAttributeBonuses.IsInput("keystone_iron_reflexes"));
        var source = SourceBonus(new() { [18620] = 1 }); // Raw isolation, not an invented native item source.
        Assert.False(source.NoDexterityBonuses);
        Assert.False(source.IronReflexes);
        var pools = CharacterStats.For(Attributes(), 1, source with { Evasion = 85 });
        Assert.Equal((202, 120, 0), (pools.Accuracy, pools.Evasion, pools.Armour));
    }

    static readonly Lazy<ItemCatalog> Ordinary = new(() => new("data"));
    static readonly Lazy<JsonDocument> NativeMods = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine("data", "tables", "Mods.json"))));
    static ItemCatalog Catalog() => Ordinary.Value;

    public static IEnumerable<object[]> NativeSources()
    {
        {
            yield return [16635, "NoMaximumLifePerStrengthUnique__1", 57170, InherentAttributeBonuses.NoLifeFromStrength, 7964u, "", 0u, 0, 3];
            yield return [16636, "NoMaximumLifePerStrengthUnique__2", 7062, InherentAttributeBonuses.NoLifeFromStrength, 7964u, "", 0u, 0, 3];
            yield return [16637, "NoMaximumManaPerIntelligenceUnique__1", 24103, InherentAttributeBonuses.NoManaFromIntelligence, 7965u, "", 0u, 0, 3];
            yield return [35358, "WeaponTreeStrengthAndNoInherentBonusFromStrength1", 18252, InherentAttributeBonuses.NoStrengthBonuses, 15532u, "additional_strength", 577u, 60, 31];
            yield return [35359, "WeaponTreeStrengthAndNoInherentBonusFromStrength2", 4210, InherentAttributeBonuses.NoStrengthBonuses, 15532u, "additional_strength", 577u, 80, 31];
            yield return [35360, "WeaponTreeStrengthAndNoInherentBonusFromStrength2h1", 8883, InherentAttributeBonuses.NoStrengthBonuses, 15532u, "additional_strength", 577u, 90, 31];
            yield return [35361, "WeaponTreeStrengthAndNoInherentBonusFromStrength2h2", 2451, InherentAttributeBonuses.NoStrengthBonuses, 15532u, "additional_strength", 577u, 120, 31];
            yield return [39310, "MutatedUniqueBodyStr7GainNoInherentBonusFromStrength", 44021, InherentAttributeBonuses.NoStrengthBonuses, 15532u, "", 0u, 0, 3];
            yield return [35350, "WeaponTreeDexterityAndNoInherentBonusFromDexterity1", 16930, InherentAttributeBonuses.NoDexterityBonuses, 18387u, "additional_dexterity", 583u, 60, 31];
            yield return [35351, "WeaponTreeDexterityAndNoInherentBonusFromDexterity2", 8354, InherentAttributeBonuses.NoDexterityBonuses, 18387u, "additional_dexterity", 583u, 80, 31];
            yield return [35352, "WeaponTreeDexterityAndNoInherentBonusFromDexterity2h1", 38614, InherentAttributeBonuses.NoDexterityBonuses, 18387u, "additional_dexterity", 583u, 90, 31];
            yield return [35353, "WeaponTreeDexterityAndNoInherentBonusFromDexterity2h2", 4318, InherentAttributeBonuses.NoDexterityBonuses, 18387u, "additional_dexterity", 583u, 120, 31];
            yield return [35354, "WeaponTreeIntelligenceAndNoInherentBonusFromIntelligence1", 51377, InherentAttributeBonuses.NoIntelligenceBonuses, 18388u, "additional_intelligence", 580u, 60, 31];
            yield return [35355, "WeaponTreeIntelligenceAndNoInherentBonusFromIntelligence2", 10817, InherentAttributeBonuses.NoIntelligenceBonuses, 18388u, "additional_intelligence", 580u, 80, 31];
            yield return [35356, "WeaponTreeIntelligenceAndNoInherentBonusFromIntelligence2h1", 54004, InherentAttributeBonuses.NoIntelligenceBonuses, 18388u, "additional_intelligence", 580u, 90, 31];
            yield return [35357, "WeaponTreeIntelligenceAndNoInherentBonusFromIntelligence2h2", 48986, InherentAttributeBonuses.NoIntelligenceBonuses, 18388u, "additional_intelligence", 580u, 120, 31];
        }
    }

    [Theory]
    [MemberData(nameof(NativeSources))]
    public void Sixteen_complete_native_vectors_survive_the_catalog_and_saved_roundtrips(
        int row, string id, int hash, string flag, uint flagKey,
        string attribute, uint attributeKey, int addedAttribute, int generation)
    {
        var catalog = Catalog(); var stats = new StatCatalog("data");
        var source = catalog.Mods.Row(row)!; var native = NativeMods.Value.RootElement[row];
        Assert.Equal(row, source.Row);
        Assert.Equal((id, (ushort)hash, 1, generation, 1),
            (source.Id, source.Hash, source.Domain, source.GenerationType, source.Level));
        Assert.Equal(id, catalog.Mods.ByHash((ushort)hash)!.Id);
        Assert.Equal(hash, native.GetProperty("HASH16").GetInt32());
        Assert.Equal(0, native.GetProperty("GameMode").GetInt32());
        Assert.False(native.GetProperty("HasHardmodeStatValues").GetBoolean());
        string[] names = addedAttribute == 0 ? [flag] : [attribute, flag];
        int[] values = addedAttribute == 0 ? [1] : [addedAttribute, 1];
        Assert.Equal(names, source.Stats.Select(s => s.Id));
        Assert.Equal(values, source.Stats.Select(s => s.Min));
        Assert.Equal(values, source.Stats.Select(s => s.Max));
        Assert.All(source.Stats, field => Assert.False(field.Local));
        Assert.Equal(flagKey, stats.Key(flag)!.Value);

        // Complete native source on a controlled carrier. Generation31 catalog coverage is not weapon-tree
        // acquisition/admission support, and this is not a claim that the cross-base item is legally obtainable.
        var item = ItemCatalog.Create(catalog.Find("Iron Ring")!) with
            { ItemLevel = 100, Quality = 0, Mods = [new(ModSection.Explicit, source.Hash, values)] };
        var saved = CharacterSaves.ToSaved(item, catalog.Mods);
        var savedMod = Assert.Single(saved.Mods!);
        Assert.Equal(id, savedMod.Id);
        Assert.Equal(ModSection.Explicit, savedMod.Section);
        Assert.Equal(values, savedMod.Values);
        string payload = JsonSerializer.Serialize(saved);
        var restored = CharacterSaves.FromSaved(JsonSerializer.Deserialize<CharacterSaves.SavedItem>(payload)!, catalog);
        Assert.Equal(payload, JsonSerializer.Serialize(CharacterSaves.ToSaved(restored, catalog.Mods)));
        var raw = ItemModStats.Global([restored], catalog.Mods, stats);
        Assert.Equal(names.Length, raw.Count);
        Assert.Equal(1, raw[flagKey]);
        if (addedAttribute != 0)
        {
            Assert.Equal(attributeKey, stats.Key(attribute)!.Value);
            Assert.Equal(addedAttribute, raw[attributeKey]);
        }
        var bonus = SourceBonus(raw);
        Assert.Equal(flag == InherentAttributeBonuses.NoLifeFromStrength, bonus.NoLifeFromStrength);
        Assert.Equal(flag == InherentAttributeBonuses.NoManaFromIntelligence, bonus.NoManaFromIntelligence);
        Assert.Equal(flag == InherentAttributeBonuses.NoStrengthBonuses, bonus.NoStrengthBonuses);
        Assert.Equal(flag == InherentAttributeBonuses.NoDexterityBonuses, bonus.NoDexterityBonuses);
        Assert.Equal(flag == InherentAttributeBonuses.NoIntelligenceBonuses, bonus.NoIntelligenceBonuses);
        var pools = CharacterStats.For(Attributes(), 1, bonus with { EnergyShield = 100, Evasion = 85 });
        Assert.Equal((100 + (attributeKey == 577 ? addedAttribute : 0),
            100 + (attributeKey == 583 ? addedAttribute : 0), 100 + (attributeKey == 580 ? addedAttribute : 0)),
            (pools.Strength, pools.Dexterity, pools.Intelligence));
        Assert.Equal(flag is InherentAttributeBonuses.NoLifeFromStrength or InherentAttributeBonuses.NoStrengthBonuses ? 50u : 100u, pools.Life);
        Assert.Equal(flag is InherentAttributeBonuses.NoManaFromIntelligence or InherentAttributeBonuses.NoIntelligenceBonuses ? 40u : 90u, pools.Mana);
        Assert.Equal(flag == InherentAttributeBonuses.NoStrengthBonuses ? 0 : 20, pools.MeleePhysicalDamagePct);
        Assert.Equal(flag == InherentAttributeBonuses.NoDexterityBonuses ? 2 : 202, pools.Accuracy);
        Assert.Equal(flag == InherentAttributeBonuses.NoDexterityBonuses ? 100 : 120, pools.Evasion);
        Assert.Equal(flag == InherentAttributeBonuses.NoIntelligenceBonuses ? 100u : 110u, pools.EnergyShield);
    }

    [Fact]
    public void Duplicate_real_Life_flags_sum_but_suppress_the_inherent_grant_only_once()
    {
        var catalog = Catalog(); var stats = new StatCatalog("data");
        var first = catalog.Mods.Row(16635)!; var second = catalog.Mods.Row(16636)!;
        var item = ItemCatalog.Create(catalog.Find("Iron Ring")!) with
            { Mods = [new(ModSection.Explicit, first.Hash, [1]), new(ModSection.Explicit, second.Hash, [1])] };
        var both = ItemModStats.Global([item], catalog.Mods, stats);
        Assert.Equal(2, both[7964]);
        Assert.Equal(50u, CharacterStats.For(Attributes(), 1, SourceBonus(both)).Life);
        var one = ItemModStats.Global([item with { Mods = [item.Mods[0]] }], catalog.Mods, stats);
        Assert.Equal(1, one[7964]);
        Assert.Equal(50u, CharacterStats.For(Attributes(), 1, SourceBonus(one)).Life);
        Assert.Equal(100u, CharacterStats.For(Attributes(), 1, SourceBonus([])).Life);
    }
}
