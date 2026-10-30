using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    const string AttributeDefenceEs = "energy_shield_+%_per_10_strength";
    const string AttributeDefenceEv = "evasion_+%_per_10_intelligence";
    sealed record AttributeDefenceNativeSource(string Stat, string Mod, int Value, int Row, ushort Hash,
        uint Key, int Min, int Max);
    static readonly AttributeDefenceNativeSource[] AttributeDefenceNative =
    [
        new(AttributeDefenceEs, "EnergyShieldPerStrengthUnique__1", 1, 13590, 34083, 4994, 1, 1),
        new(AttributeDefenceEv, "EvasionRatingPerIntelligenceUnique__1", 2, 13594, 5007, 4998, 2, 2),
        new(AttributeDefenceEs, "DivergentEnergyShieldPerStrengthUnique__1", 1, 39699, 49507, 4994, 1, 1),
        new(AttributeDefenceEv, "DivergentEvasionRatingPerIntelligenceUnique__1", 2, 39701, 62841, 4998, 2, 2),
    ];
    static readonly AttributeDefenceNativeSource AttributeDefenceQualifier =
        new("additional_all_attributes", "AllAttributesUniqueAmulet8", 100, 282, 11855, 608, 80, 100);
    static readonly AttributeDefenceNativeSource AttributeDefenceFlatEs =
        new("base_maximum_energy_shield", "IncreasedEnergyShieldUniqueQuiver7", 100, 708, 35078, 135, 100, 120);

    static uint AttributeDefenceKey(string name) => Program.Stats.Key(name)!.Value;
    static PlayerInventories AttributeDefenceItems(GatewayCharacter game) => ProgramValue<PlayerInventories>("ItemsOf", game.State);
    static ItemMod AttributeDefenceMod(ItemCatalog catalog, AttributeDefenceNativeSource source)
    {
        var row = catalog.Mods.ById(source.Mod)!;
        Assert.Equal(source.Row, row.Row);
        Assert.Equal(source.Hash, row.Hash);
        Assert.Equal(3, row.GenerationType);
        Assert.Equal(source.Key, AttributeDefenceKey(source.Stat));
        var range = Assert.Single(row.Stats);
        Assert.Equal((source.Stat, source.Min, source.Max, false), (range.Id, range.Min, range.Max, range.Local));
        var mod = LevelPanelMod(catalog, source.Mod, source.Stat, source.Value);
        Assert.Equal(source.Value, Assert.Single(mod.Values));
        return mod;
    }

    static ItemMod[] AttributeDefencePair(ItemCatalog catalog, bool divergent = false) =>
        AttributeDefenceNative.Skip(divergent ? 2 : 0).Take(2).Select(s => AttributeDefenceMod(catalog, s)).ToArray();

    // These complete, in-range native vectors are source-isolation fixtures. Their placement on an amulet/ring
    // does not claim legal unique acquisition. The independent100 ES is nonlocal, not fabricated local item ES.
    static InventoryItem AttributeDefenceFoundation(GatewayCharacter game)
    {
        var catalog = Program.Items;
        return AttributeDefenceItems(game).Add(5, 0, 0, LevelPanelCarrier(catalog, "Coral Amulet",
            AttributeDefenceMod(catalog, AttributeDefenceQualifier), AttributeDefenceMod(catalog, AttributeDefenceFlatEs))
            with { ItemLevel = 100 });
    }

    static string AttributeDefencePayload(ItemCatalog catalog, Item item) =>
        JsonSerializer.Serialize(CharacterSaves.ToSaved(item, catalog.Mods));

    static void AttributeDefenceQualified(GatewayCharacter game, params InventoryItem[] carriers)
    {
        var catalog = Program.Items;
        foreach (var carrier in carriers)
        {
            Assert.DoesNotContain(carrier.Id, Program.InactiveItems[game.Character]);
            var type = catalog.ByHash(carrier.Item.TypeHash)!;
            Assert.InRange(ItemModStats.LevelRequirement(carrier.Item, type.DropLevel, catalog.Mods), 0, game.State.Pools.Level);
            var requirement = catalog.Requirements(type);
            Assert.True(game.State.Pools.Strength >= requirement.Str && game.State.Pools.Dexterity >= requirement.Dex
                && game.State.Pools.Intelligence >= requirement.Int);
        }
    }

    static void AttributeDefenceAssert(GatewayCharacter game, uint es, int evasion, int rawEs, int rawEv,
        int esIncrease, int evasionIncrease)
    {
        // Repeat complete builds and final publication: a derived increase must never become another raw source.
        for (int i = 0; i < 2; i++)
        {
            game.State.Pools = Program.PoolsFor(game.State, game.State.Pools.Level);
            var raw = Program.CharacterPlayerStats(game.State);
            var published = LevelPanelPublished(game);
            Assert.Equal(es, game.State.Pools.EnergyShield);
            Assert.Equal(es, game.State.EnergyShield.Maximum);
            Assert.Equal(evasion, game.State.Pools.Evasion);
            Assert.Equal(esIncrease, game.State.Pools.EnergyShieldPct);
            Assert.Equal(evasionIncrease, game.State.Pools.EvasionPct);
            Assert.Equal(rawEs, raw.GetValueOrDefault(AttributeDefenceKey(AttributeDefenceEs)));
            Assert.Equal(rawEv, raw.GetValueOrDefault(AttributeDefenceKey(AttributeDefenceEv)));
            Assert.Equal(rawEs, published.GetValueOrDefault(AttributeDefenceKey(AttributeDefenceEs)));
            Assert.Equal(rawEv, published.GetValueOrDefault(AttributeDefenceKey(AttributeDefenceEv)));
            Assert.Equal((int)es, published[AttributeDefenceKey("maximum_energy_shield")]);
            Assert.Equal(evasion, published[AttributeDefenceKey("evasion_rating")]);
            Assert.Equal(esIncrease, published[AttributeDefenceKey("maximum_energy_shield_+%")]);
            Assert.Equal(evasionIncrease, published[AttributeDefenceKey("evasion_rating_+%")]);
        }
    }

    [Fact]
    public void Attribute_defence_real_native_pairs_add_remove_and_publish_once_with_unchanged_payloads()
    {
        using var game = LevelPanelCharacter(100);
        var catalog = Program.Items; var items = AttributeDefenceItems(game);
        var foundation = AttributeDefenceFoundation(game);
        // Int132 supplies13% ES; each native Strength coefficient adds11% on the independent100 ES.
        AttributeDefenceAssert(game, 113, 18, 0, 0, 13, 22);
        Assert.Equal((114, 114, 132), (game.State.Pools.Strength, game.State.Pools.Dexterity, game.State.Pools.Intelligence));
        var primary = items.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring", AttributeDefencePair(catalog)) with { ItemLevel = 100 });
        string originalPrimary = AttributeDefencePayload(catalog, primary.Item);
        AttributeDefenceAssert(game, 124, 22, 1, 2, 24, 48);
        AttributeDefenceQualified(game, foundation, primary);
        game.State.EnergyShield.SetMaximum(124, refill: true);

        var divergent = items.Add(7, 0, 0, LevelPanelCarrier(catalog, "Iron Ring", AttributeDefencePair(catalog, divergent: true)) with { ItemLevel = 100 });
        string originalDivergent = AttributeDefencePayload(catalog, divergent.Item);
        AttributeDefenceAssert(game, 135, 26, 2, 4, 35, 74);
        AttributeDefenceQualified(game, primary, divergent);
        Assert.Equal(124u, game.State.EnergyShield.Current); // Increasing the maximum never refills the resource.
        Assert.Equal(52, ItemModStats.LevelRequirement(divergent.Item, catalog.ByHash(divergent.Item.TypeHash)!.DropLevel, catalog.Mods));
        Assert.Equal(originalPrimary, AttributeDefencePayload(catalog, Assert.Single(items.Items(6)).Item));
        Assert.Equal(originalDivergent, AttributeDefencePayload(catalog, Assert.Single(items.Items(7)).Item));
        Assert.NotNull(items.Remove(7, divergent.Id));
        AttributeDefenceAssert(game, 124, 22, 1, 2, 24, 48);
        Assert.NotNull(items.Remove(6, primary.Id));
        AttributeDefenceAssert(game, 113, 18, 0, 0, 13, 22);
        Assert.Equal(113u, game.State.EnergyShield.Current);
        items.Add(6, 0, 0, primary.Item);
        AttributeDefenceAssert(game, 124, 22, 1, 2, 24, 48);
        Assert.Equal(113u, game.State.EnergyShield.Current);
        Assert.DoesNotContain(AttributeDefenceKey(AttributeDefenceEs), game.State.PlayerStats.Keys);
        Assert.DoesNotContain(AttributeDefenceKey(AttributeDefenceEv), game.State.PlayerStats.Keys);
    }

    [Fact]
    public void Attribute_defence_native_sources_follow_active_weapon_set_and_actual_host_qualification()
    {
        using var game = LevelPanelCharacter(100);
        var catalog = Program.Items; var items = AttributeDefenceItems(game);
        items.Add(7, 0, 0, LevelPanelCarrier(catalog, "Iron Ring", AttributeDefenceMod(catalog, AttributeDefenceFlatEs)));
        var weapon = items.Add(PlayerInventories.Weapon2, 0, 0, LevelPanelCarrier(catalog, "Rusted Sword", AttributeDefencePair(catalog)));
        var body = items.Add(PlayerInventories.BodyArmour, 0, 0, LevelPanelCarrier(catalog, "Chestplate", AttributeDefencePair(catalog)));
        Assert.Equal((25, 0, 0), catalog.Requirements(catalog.Find("Chestplate")!));
        AttributeDefenceAssert(game, 103, 15, 0, 0, 3, 2);
        Assert.Contains(body.Id, Program.InactiveItems[game.Character]);
        var qualifier = items.Add(5, 0, 0, LevelPanelCarrier(catalog, "Coral Amulet", AttributeDefenceMod(catalog, AttributeDefenceQualifier)));
        AttributeDefenceAssert(game, 124, 22, 1, 2, 24, 48);
        AttributeDefenceQualified(game, qualifier, body);
        items.SwapWeaponSets();
        AttributeDefenceAssert(game, 135, 26, 2, 4, 35, 74);
        AttributeDefenceQualified(game, weapon, body);
        Assert.NotNull(items.Remove(5, qualifier.Id));
        AttributeDefenceAssert(game, 104, 16, 1, 2, 4, 8);
        Assert.Contains(body.Id, Program.InactiveItems[game.Character]);
        items.SwapWeaponSets();
        AttributeDefenceAssert(game, 103, 15, 0, 0, 3, 2);
        items.Add(5, 0, 0, qualifier.Item);
        AttributeDefenceAssert(game, 124, 22, 1, 2, 24, 48);
        AttributeDefenceQualified(game, body);
    }

    [Fact]
    public async Task Attribute_defence_divergent_native_pair_requires_its_real_level_and_returns_after_level_change()
    {
        using var game = LevelPanelCharacter(10);
        var catalog = Program.Items; var items = AttributeDefenceItems(game);
        AttributeDefenceFoundation(game);
        var carrier = items.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring", AttributeDefencePair(catalog, divergent: true)));
        Assert.Equal(52, ItemModStats.LevelRequirement(carrier.Item, catalog.ByHash(carrier.Item.TypeHash)!.DropLevel, catalog.Mods));
        AttributeDefenceAssert(game, 113, 18, 0, 0, 13, 22);
        Assert.Contains(carrier.Id, Program.InactiveItems[game.Character]);
        Assert.Null((await game.SetLevel(100)).Error);
        AttributeDefenceAssert(game, 124, 22, 1, 2, 24, 48);
        AttributeDefenceQualified(game, carrier);
        Assert.Null((await game.SetLevel(10)).Error);
        AttributeDefenceAssert(game, 113, 18, 0, 0, 13, 22);
        Assert.Contains(carrier.Id, Program.InactiveItems[game.Character]);
    }

    [Fact]
    public async Task Attribute_defence_actual_ES_source_commands_replace_zero_clamp_publish_and_never_refill()
    {
        using var game = LevelPanelCharacter(100);
        var catalog = Program.Items;
        AttributeDefenceFoundation(game);
        AttributeDefenceItems(game).Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring", AttributeDefencePair(catalog)));
        AttributeDefenceAssert(game, 124, 22, 1, 2, 24, 48);
        game.State.EnergyShield.SetMaximum(124, refill: true); // Controlled resource setup, not a recovery claim.
        uint life = game.State.PlayerLife, mana = game.State.PlayerMana;

        var zero = await game.Command($"/set_stat {AttributeDefenceEs} 0");
        Assert.Null(zero.Error);
        Assert.Equal(0, game.State.PlayerStats[4994]);
        AttributeDefenceAssert(game, 113, 22, 0, 2, 13, 48);
        Assert.Equal(113u, game.State.EnergyShield.Current);
        AttributeDefencePacket(zero.Bytes, InstanceProtocol.LifeUpdate(Program.PlayerId, 113, pool: 2));
        AttributeDefenceStatPacket(zero.Bytes, "maximum_energy_shield", 113);
        AttributeDefenceStatPacket(zero.Bytes, AttributeDefenceEs, 0);

        var two = await game.Command($"/set_stat {AttributeDefenceEs} 2");
        Assert.Null(two.Error);
        AttributeDefenceAssert(game, 135, 22, 2, 2, 35, 48);
        Assert.Equal(113u, game.State.EnergyShield.Current);
        AttributeDefenceStatPacket(two.Bytes, "maximum_energy_shield", 135);
        Assert.Equal(life, game.State.PlayerLife);
        Assert.Equal(mana, game.State.PlayerMana);
        Assert.True(game.State.PlayerStats.Remove(4994)); // Test fixture restoration, not a public remove command.
        AttributeDefenceAssert(game, 124, 22, 1, 2, 24, 48);
        Assert.Equal(113u, game.State.EnergyShield.Current);
    }

    [Fact]
    public async Task Attribute_defence_actual_EV_source_commands_replace_the_coefficient_without_refilling_other_resources()
    {
        using var game = LevelPanelCharacter(100);
        var catalog = Program.Items;
        AttributeDefenceFoundation(game);
        AttributeDefenceItems(game).Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring", AttributeDefencePair(catalog)));
        AttributeDefenceAssert(game, 124, 22, 1, 2, 24, 48);
        game.State.EnergyShield.SetMaximum(124, refill: true);
        Assert.Equal(0, game.State.EnergyShield.Damage(7, bypass: false, now: 0));
        game.State.PlayerLife = 1284; game.State.PlayerMana = 691;
        foreach (var (coefficient, evasion, increase) in new[] { (0, 18, 22), (1, 20, 35) })
        {
            var result = await game.Command($"/set_stat {AttributeDefenceEv} {coefficient}");
            Assert.Null(result.Error);
            Assert.Equal(coefficient, game.State.PlayerStats[4998]);
            AttributeDefenceAssert(game, 124, evasion, 1, coefficient, 24, increase);
            AttributeDefenceStatPacket(result.Bytes, "evasion_rating", evasion);
            AttributeDefenceStatPacket(result.Bytes, AttributeDefenceEv, coefficient);
            Assert.Equal(117u, game.State.EnergyShield.Current);
            Assert.Equal(1284u, game.State.PlayerLife);
            Assert.Equal(691u, game.State.PlayerMana);
        }
        Assert.True(game.State.PlayerStats.Remove(4998));
        AttributeDefenceAssert(game, 124, 22, 1, 2, 24, 48);
        Assert.Equal(117u, game.State.EnergyShield.Current);
    }

    [Fact]
    public void Attribute_defence_final_display_overrides_win_without_becoming_attribute_group_inputs()
    {
        using var game = LevelPanelCharacter(100);
        var catalog = Program.Items;
        AttributeDefenceFoundation(game);
        AttributeDefenceItems(game).Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring", AttributeDefencePair(catalog)));
        AttributeDefenceAssert(game, 124, 22, 1, 2, 24, 48);
        game.State.PlayerStats[AttributeDefenceKey("strength")] = 1000;
        game.State.PlayerStats[AttributeDefenceKey("intelligence")] = 1000;
        game.State.PlayerStats[AttributeDefenceKey("maximum_energy_shield")] = 0;
        game.State.PlayerStats[AttributeDefenceKey("evasion_rating")] = 777;
        for (int i = 0; i < 2; i++)
        {
            game.State.Pools = Program.PoolsFor(game.State, 100);
            var published = LevelPanelPublished(game);
            Assert.Equal((114, 132, 124u, 22), (game.State.Pools.Strength, game.State.Pools.Intelligence,
                game.State.Pools.EnergyShield, game.State.Pools.Evasion));
            Assert.Equal(1000, published[AttributeDefenceKey("strength")]);
            Assert.Equal(1000, published[AttributeDefenceKey("intelligence")]);
            Assert.Equal(0, published[AttributeDefenceKey("maximum_energy_shield")]);
            Assert.Equal(777, published[AttributeDefenceKey("evasion_rating")]);
            Assert.Equal(1, published[4994]); Assert.Equal(2, published[4998]);
        }
        foreach (string stat in new[] { "strength", "intelligence", "maximum_energy_shield", "evasion_rating" })
            Assert.True(game.State.PlayerStats.Remove(AttributeDefenceKey(stat)));
        AttributeDefenceAssert(game, 124, 22, 1, 2, 24, 48);
    }

    [Fact]
    public void Attribute_defence_strength_increase_scales_independent_ES_without_changing_converted_local_ES_life()
    {
        using var game = LevelPanelCharacter(100);
        var catalog = Program.Items; var items = AttributeDefenceItems(game);
        AttributeDefenceFoundation(game);
        var type = catalog.Find("Wool Gloves")!;
        Assert.Equal((1339, 3963300936u), (type.Row, type.Hash));
        var basis = catalog.Defences(type)!.Value;
        Assert.Equal((5, 7), (basis.EnergyShieldMin, basis.EnergyShieldMax));
        var conversion = new AttributeDefenceNativeSource("gain_maximum_life_instead_of_maximum_es_from_armour",
            "LifeFromEnergyShieldArmourUnique__1", 1, 36937, 20049, 19705, 1, 1);
        var gloves = items.Add(PlayerInventories.Gloves, 0, 0, ItemCatalog.Create(type, defenceBaseRoll: 0) with
            { Quality = 0, Mods = [AttributeDefenceMod(catalog, conversion)] });
        string original = AttributeDefencePayload(catalog, gloves.Item);
        Assert.Equal(5, ItemDefences.Calculate(gloves.Item, basis, catalog.Mods).EnergyShield);
        AttributeDefenceAssert(game, 113, 18, 0, 0, 13, 22);
        AttributeDefenceQualified(game, gloves);
        Assert.Equal(1300u, game.State.Pools.Life); //1295 ordinary Life plus the native local5, before global ES INC.
        var source = items.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring", AttributeDefenceMod(catalog, AttributeDefenceNative[0])));
        AttributeDefenceAssert(game, 124, 18, 1, 0, 24, 22);
        Assert.Equal(1300u, game.State.Pools.Life);
        Assert.Equal(700u, game.State.Pools.Mana);
        Assert.Equal(5, ItemDefences.Calculate(gloves.Item, basis, catalog.Mods).EnergyShield);
        Assert.Equal(original, AttributeDefencePayload(catalog, Assert.Single(items.Items(PlayerInventories.Gloves)).Item));
        Assert.NotNull(items.Remove(6, source.Id));
        AttributeDefenceAssert(game, 113, 18, 0, 0, 13, 22);
        Assert.Equal(1300u, game.State.Pools.Life);
        Assert.NotNull(items.Remove(PlayerInventories.Gloves, gloves.Id));
        AttributeDefenceAssert(game, 113, 18, 0, 0, 13, 22);
        Assert.Equal(1295u, game.State.Pools.Life);
    }

    static void AttributeDefencePacket(byte[] bytes, byte[] packet) =>
        Assert.True(bytes.AsSpan().IndexOf(packet) >= 0, "Expected the actual command's clamped ES resource packet.");

    static void AttributeDefenceStatPacket(byte[] bytes, string name, int value)
    {
        // /set_stat sends the full player snapshot first, before Actor/resource messages. Decode only that
        // count-bounded 0x01b3 block; a matching key/value in a later Actor payload cannot satisfy this assertion.
        var reader = new LoginReader(bytes);
        Assert.Equal(InstanceEntities.StatsUpdateId, reader.U16());
        Assert.Equal(Program.PlayerId, reader.U32());
        Assert.Equal(0u, reader.U32());
        Assert.Equal((ushort)0, reader.U16());
        uint count = AttributeDefenceCompact(ref reader, out _);
        Assert.False(reader.Short);
        Assert.InRange(count, 1u, (uint)((bytes.Length - reader.Position) / 2));
        var stats = new Dictionary<uint, int>();
        for (uint n = 0; n < count; n++)
        {
            uint key = AttributeDefenceCompact(ref reader, out _);
            uint raw = AttributeDefenceCompact(ref reader, out int bits);
            Assert.False(reader.Short);
            int decoded = bits == 32 ? unchecked((int)raw)
                : (raw & (1u << (bits - 1))) != 0 ? unchecked((int)(raw | (uint.MaxValue << bits))) : (int)raw;
            Assert.True(stats.TryAdd(key, decoded), "The player stat snapshot must not repeat a key.");
        }
        Assert.True(stats.TryGetValue(AttributeDefenceKey(name), out int actual),
            "Expected the actual command's player0x01b3 snapshot to include: " + name);
        Assert.Equal(value, actual);
    }

    // Native unsigned compact keys/counts and signed compact values share these prefix widths; signed values
    // are sign-extended by the caller, as in ResurrectionSuppressionTests' actual player-stat decoder.
    static uint AttributeDefenceCompact(ref LoginReader reader, out int bits)
    {
        byte first = reader.U8();
        int extra; uint value;
        if (first < 0x80) { extra = 0; bits = 7; value = first; }
        else if (first < 0xc0) { extra = 1; bits = 14; value = (uint)(first & 0x3f); }
        else if (first < 0xe0) { extra = 2; bits = 21; value = (uint)(first & 0x1f); }
        else if (first < 0xf0) { extra = 3; bits = 28; value = (uint)(first & 0x0f); }
        else { extra = 4; bits = 32; value = 0; }
        for (int n = 0; n < extra; n++) value = (value << 8) | reader.U8();
        return value;
    }
}
