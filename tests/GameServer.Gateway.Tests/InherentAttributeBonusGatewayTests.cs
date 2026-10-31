using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    // These mode-loaded native catalogue vectors are supplied-source controls, not weapon-tree admission,
    // legal cross-base acquisition, native-client evidence or completion of an attribute/defence row.
    sealed record InherentGatewaySource(string Stat, string Mod, int Row, ushort Hash, uint Key,
        string? Companion = null);
    sealed record InherentGatewayExpected(int Strength, int Dexterity, int Intelligence, uint Life, uint Mana,
        uint EnergyShield, int Accuracy, int Evasion, int Melee, int EnergyShieldPct, int EvasionPct);

    static readonly InherentGatewaySource[] InherentGatewaySources =
    [
        new(InherentAttributeBonuses.NoLifeFromStrength, "NoMaximumLifePerStrengthUnique__1", 16635, 57170, 7964),
        new(InherentAttributeBonuses.NoManaFromIntelligence, "NoMaximumManaPerIntelligenceUnique__1", 16637, 24103, 7965),
        new(InherentAttributeBonuses.NoStrengthBonuses, "WeaponTreeStrengthAndNoInherentBonusFromStrength1",
            35358, 18252, 15532, "additional_strength"),
        new(InherentAttributeBonuses.NoDexterityBonuses, "WeaponTreeDexterityAndNoInherentBonusFromDexterity1",
            35350, 16930, 18387, "additional_dexterity"),
        new(InherentAttributeBonuses.NoIntelligenceBonuses, "WeaponTreeIntelligenceAndNoInherentBonusFromIntelligence1",
            35354, 51377, 18388, "additional_intelligence"),
    ];

    static readonly InherentGatewayExpected InherentGatewayOrdinary =
        new(114, 114, 132, 1295, 700, 124, 428, 22, 22, 24, 48);

    static InherentGatewayExpected InherentGatewayExpectedFor(int source, bool prohibited) => source switch
    {
        0 => InherentGatewayOrdinary with { Life = prohibited ? 1238u : 1295u },
        1 => InherentGatewayOrdinary with { Mana = prohibited ? 634u : 700u },
        // The full native broad vector retains +60 Strength: 174/2=87 Life, 174/5=34 melee,
        // and its separate item ES coefficient still adds17% even while both inherent grants are prohibited.
        2 => new(174, 114, 132, prohibited ? 1238u : 1325u, 700, 130, 428, 22,
            prohibited ? 0 : 34, 30, 48),
        // D174 supplies348 accuracy and34% EV. The item Int132 coefficient still supplies26% EV:
        // round(15*1.26)=19 when the inherent Dexterity terms are prohibited, not zero Evasion.
        3 => new(114, 174, 132, 1295, 700, 124, prohibited ? 200 : 548,
            prohibited ? 19 : 24, 22, 24, prohibited ? 26 : 60),
        // I192 supplies96 Mana and19% ES; the item Strength114 coefficient still supplies11% ES.
        // Its separate item Evasion term still reads all192 Intelligence: 38%+22%=60%, EV24.
        4 => new(114, 114, 192, 1295, prohibited ? 634u : 730u, prohibited ? 111u : 130u,
            428, 24, 22, prohibited ? 11 : 30, 60),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    static ItemMod InherentGatewayNative(ItemCatalog catalog, int index)
    {
        var source = InherentGatewaySources[index];
        var row = catalog.Mods.ById(source.Mod)!;
        Assert.NotNull(row);
        Assert.Equal((source.Row, source.Hash, 1), (row.Row, row.Hash, row.Domain));
        Assert.Equal(source.Mod, catalog.Mods.ByHash(source.Hash)!.Id);
        Assert.Equal(source.Key, AttributeDefenceKey(source.Stat));
        Assert.Equal(source.Companion is null ? 3 : 31, row.GenerationType);
        Assert.Equal(source.Companion is null ? 1 : 2, row.Stats.Count);
        if (source.Companion is { } companion)
            Assert.Equal((companion, 60, 60, false),
                (row.Stats[0].Id, row.Stats[0].Min, row.Stats[0].Max, row.Stats[0].Local));
        var flag = row.Stats[^1];
        Assert.Equal((source.Stat, 1, 1, false), (flag.Id, flag.Min, flag.Max, flag.Local));
        // Keep the complete actual vector. In particular, broad Dexterity/Intelligence are never [1] substitutes.
        return new(ModSection.Explicit, row.Hash, source.Companion is null ? [1] : [60, 1]);
    }

    static PlayerInventories InherentGatewayItems(GatewayCharacter game) =>
        ProgramValue<PlayerInventories>("ItemsOf", game.State);

    static void InherentGatewayFoundation(GatewayCharacter game)
    {
        var catalog = Program.Items;
        AttributeDefenceFoundation(game); // Complete native +100 all attributes and independent100 flat ES.
        InherentGatewayItems(game).Add(7, 0, 0,
            LevelPanelCarrier(catalog, "Iron Ring", AttributeDefencePair(catalog)) with { ItemLevel = 100 });
    }

    static Dictionary<string, int> InherentGatewayFinalFields(InherentGatewayExpected expected) => new()
    {
        ["strength"] = expected.Strength, ["dexterity"] = expected.Dexterity,
        ["intelligence"] = expected.Intelligence,
        ["virtual_strength_for_requirements"] = expected.Strength,
        ["virtual_dexterity_for_requirements"] = expected.Dexterity,
        ["virtual_intelligence_for_requirements"] = expected.Intelligence,
        ["maximum_life"] = (int)expected.Life, ["maximum_mana"] = (int)expected.Mana,
        ["maximum_energy_shield"] = (int)expected.EnergyShield,
        ["accuracy_rating"] = expected.Accuracy, ["main_hand_accuracy_rating"] = expected.Accuracy,
        ["evasion_rating"] = expected.Evasion, ["evasion_rating_+%"] = expected.EvasionPct,
        ["maximum_energy_shield_+%"] = expected.EnergyShieldPct,
        ["melee_physical_damage_+%"] = expected.Melee,
    };

    static void InherentGatewayAssert(GatewayCharacter game, InherentGatewayExpected expected)
    {
        // Check the command's existing pool object before calling any publication helper. Rebuilding here would
        // hide a missing /set_stat source selector/rebuild, especially for broad Dexterity's accuracy/Evasion.
        var pools = game.State.Pools;
        Assert.Equal((expected.Strength, expected.Dexterity, expected.Intelligence),
            (pools.Strength, pools.Dexterity, pools.Intelligence));
        Assert.Equal((expected.Life, expected.Mana, expected.EnergyShield), (pools.Life, pools.Mana, pools.EnergyShield));
        Assert.Equal((expected.Accuracy, expected.Evasion, expected.Melee, expected.EnergyShieldPct, expected.EvasionPct),
            (pools.Accuracy, pools.Evasion, pools.MeleePhysicalDamagePct, pools.EnergyShieldPct, pools.EvasionPct));
        Assert.Equal(expected.EnergyShield, game.State.EnergyShield.Maximum);
        var raw = Program.CharacterPlayerStats(game.State);
        var published = LevelPanelPublished(game);
        foreach (var (stat, value) in InherentGatewayFinalFields(expected))
        {
            Assert.Equal(value, raw[AttributeDefenceKey(stat)]);
            Assert.Equal(value, published[AttributeDefenceKey(stat)]);
        }
        Assert.Equal(1, raw[4994]); Assert.Equal(2, raw[4998]);
        Assert.Equal(1, published[4994]); Assert.Equal(2, published[4998]);
    }

    static void InherentGatewayRefresh(GatewayCharacter game, InherentGatewayExpected expected)
    {
        game.State.Pools = Program.PoolsFor(game.State, 100);
        InherentGatewayAssert(game, expected);
    }

    static void InherentGatewayRaw(GatewayCharacter game, uint key, int expected)
    {
        Assert.Equal(expected, Program.CharacterPlayerStats(game.State).GetValueOrDefault(key));
        Assert.Equal(expected, LevelPanelPublished(game).GetValueOrDefault(key));
    }

    static void InherentGatewayWire(byte[] bytes, InherentGatewayExpected expected, string flag, int raw)
    {
        // Reuse the count-bounded leading Player0x1b3 decoder; an Actor occurrence cannot satisfy these assertions.
        foreach (var (stat, value) in InherentGatewayFinalFields(expected))
            AttributeDefenceStatPacket(bytes, stat, value);
        AttributeDefenceStatPacket(bytes, flag, raw);
        AttributeDefenceStatPacket(bytes, AttributeDefenceEs, 1);
        AttributeDefenceStatPacket(bytes, AttributeDefenceEv, 2);
    }

    static void InherentGatewayPacket(byte[] bytes, byte[] packet) =>
        Assert.True(bytes.AsSpan().IndexOf(packet) >= 0, "Expected the actual command's changed resource publication.");

    static void InherentGatewayReservation(GatewayCharacter game)
    {
        // A controlled active aura, as in the existing pool tests; no gem activation/acquisition claim.
        var skills = Program.Skills;
        game.State.Auras[0x103] = new(0x103, skills.EffectRow("Haste"), "Haste", "Haste",
            1, AuraCatalog.BuffRow("Haste"), 0, 5000);
    }

    static void InherentGatewayFill(GatewayCharacter game, bool reserve)
    {
        game.State.PlayerLife = game.State.Pools.Life;
        game.State.PlayerMana = reserve ? (uint)ProgramValue<int>("UnreservedMana", game.State) : game.State.Pools.Mana;
        game.State.EnergyShield.SetMaximum(game.State.Pools.EnergyShield, refill: true);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task InherentAttribute_native_full_sources_and_actual_flag_commands_preserve_attributes_and_other_terms(
        int sourceIndex)
    {
        using var game = LevelPanelCharacter(100);
        var source = InherentGatewaySources[sourceIndex];
        var catalog = Program.Items; var items = InherentGatewayItems(game);
        InherentGatewayFoundation(game);
        var carrier = items.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring",
            InherentGatewayNative(catalog, sourceIndex)) with { ItemLevel = 100 });
        string original = AttributeDefencePayload(catalog, carrier.Item);
        bool reserve = sourceIndex is 1 or 4;
        if (reserve) InherentGatewayReservation(game);
        var blocked = InherentGatewayExpectedFor(sourceIndex, true);
        var enabled = InherentGatewayExpectedFor(sourceIndex, false);
        InherentGatewayRefresh(game, blocked);
        AttributeDefenceQualified(game, carrier);
        InherentGatewayRaw(game, source.Key, 1);
        InherentGatewayFill(game, reserve);
        uint life = game.State.PlayerLife, mana = game.State.PlayerMana, es = game.State.EnergyShield.Current;

        var zero = await game.Command($"/set_stat {source.Stat} 0");
        Assert.Null(zero.Error);
        Assert.Equal(0, game.State.PlayerStats[source.Key]);
        InherentGatewayAssert(game, enabled);
        InherentGatewayWire(zero.Bytes, enabled, source.Stat, 0);
        if (source.Companion is { } companion)
            AttributeDefenceStatPacket(zero.Bytes, companion, 60);
        Assert.Equal(life, game.State.PlayerLife); Assert.Equal(mana, game.State.PlayerMana);
        Assert.Equal(es, game.State.EnergyShield.Current); // Restored capacity does not refill an existing pool.
        if (reserve)
        {
            int reserved = sourceIndex == 1 ? 350 : 365;
            Assert.Equal(reserved, ProgramValue<int>("ReservedMana", game.State));
            InherentGatewayPacket(zero.Bytes, InstanceProtocol.LifeUpdate(Program.PlayerId, mana,
                (uint)reserved, pool: Program.ManaPool));
        }

        // Publish the restored full resource through the real heal path before the next decrease. This also
        // advances the ES sender's cache, so a later clamped pool2 packet is a changed client-visible value.
        var healed = await game.Command("/heal");
        Assert.Null(healed.Error);
        Assert.Equal(enabled.EnergyShield, game.State.EnergyShield.Current);
        if (sourceIndex == 4)
            InherentGatewayPacket(healed.Bytes, InstanceProtocol.LifeUpdate(Program.PlayerId, 130, pool: 2));
        var one = await game.Command($"/set_stat {source.Stat} 1");
        Assert.Null(one.Error);
        InherentGatewayAssert(game, blocked);
        InherentGatewayWire(one.Bytes, blocked, source.Stat, 1);
        if (source.Companion is { } retainedCompanion)
            AttributeDefenceStatPacket(one.Bytes, retainedCompanion, 60);
        Assert.Equal(blocked.Life, game.State.PlayerLife);
        Assert.Equal(reserve ? 317u : blocked.Mana, game.State.PlayerMana);
        Assert.Equal(blocked.EnergyShield, game.State.EnergyShield.Current);
        if (sourceIndex is 0 or 2)
            InherentGatewayPacket(one.Bytes, InstanceProtocol.LifeUpdate(Program.PlayerId, 1238));
        if (reserve)
        {
            Assert.Equal(317, ProgramValue<int>("ReservedMana", game.State));
            InherentGatewayPacket(one.Bytes, InstanceProtocol.LifeUpdate(Program.PlayerId, 317, 317, pool: Program.ManaPool));
        }
        if (sourceIndex == 4)
            InherentGatewayPacket(one.Bytes, InstanceProtocol.LifeUpdate(Program.PlayerId, 111, pool: 2));

        Assert.True(game.State.PlayerStats.Remove(source.Key)); // Test-fixture override removal, no public remove command.
        InherentGatewayRefresh(game, blocked);
        InherentGatewayRaw(game, source.Key, 1);
        Assert.Equal(original, AttributeDefencePayload(catalog, Assert.Single(items.Items(6)).Item));
        Assert.NotNull(items.Remove(6, carrier.Id));
        InherentGatewayRefresh(game, InherentGatewayOrdinary);
        InherentGatewayRaw(game, source.Key, 0);
        Assert.DoesNotContain(source.Key, game.State.SentStatKeys);
        if (sourceIndex is 0 or 2) Assert.Equal(1238u, game.State.PlayerLife);
        if (reserve) Assert.Equal(317u, game.State.PlayerMana);
        if (sourceIndex == 4) Assert.Equal(111u, game.State.EnergyShield.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InherentAttribute_duplicate_and_overlapping_native_flags_are_whole_sources_not_double_subtractions(
        bool mana)
    {
        using var game = LevelPanelCharacter(100);
        var catalog = Program.Items; var items = InherentGatewayItems(game);
        int narrowIndex = mana ? 1 : 0, broadIndex = mana ? 4 : 2;
        var narrow = InherentGatewaySources[narrowIndex]; var broad = InherentGatewaySources[broadIndex];
        InherentGatewayFoundation(game);
        var mods = new List<ItemMod> { InherentGatewayNative(catalog, narrowIndex), InherentGatewayNative(catalog, broadIndex) };
        if (!mana) mods.Add(LevelPanelMod(catalog, "NoMaximumLifePerStrengthUnique__2", narrow.Stat, 1));
        var carrier = items.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring", mods.ToArray()) with { ItemLevel = 100 });
        if (mana) InherentGatewayReservation(game);
        var blocked = InherentGatewayExpectedFor(broadIndex, true);
        var enabled = InherentGatewayExpectedFor(broadIndex, false);
        InherentGatewayRefresh(game, blocked);
        InherentGatewayRaw(game, narrow.Key, mana ? 1 : 2);
        InherentGatewayRaw(game, broad.Key, 1);
        InherentGatewayFill(game, mana);

        var narrowZero = await game.Command($"/set_stat {narrow.Stat} 0");
        Assert.Null(narrowZero.Error);
        InherentGatewayAssert(game, blocked); // The other independently sourced prohibition still applies.
        AttributeDefenceStatPacket(narrowZero.Bytes, narrow.Stat, 0);
        AttributeDefenceStatPacket(narrowZero.Bytes, broad.Stat, 1);
        var broadZero = await game.Command($"/set_stat {broad.Stat} 0");
        Assert.Null(broadZero.Error);
        InherentGatewayAssert(game, enabled);
        InherentGatewayWire(broadZero.Bytes, enabled, broad.Stat, 0);
        Assert.Equal(blocked.Life, game.State.PlayerLife);
        Assert.Equal(mana ? 317u : blocked.Mana, game.State.PlayerMana);
        Assert.Equal(blocked.EnergyShield, game.State.EnergyShield.Current);

        Assert.Null((await game.Command("/heal")).Error);
        var narrowOne = await game.Command($"/set_stat {narrow.Stat} 1");
        Assert.Null(narrowOne.Error);
        var narrowOnly = mana ? enabled with { Mana = 634 } : enabled with { Life = 1238 };
        InherentGatewayAssert(game, narrowOnly); // Narrow Mana retains inherent ES; narrow Life retains melee.
        InherentGatewayWire(narrowOne.Bytes, narrowOnly, narrow.Stat, 1);
        InherentGatewayPacket(narrowOne.Bytes, mana
            ? InstanceProtocol.LifeUpdate(Program.PlayerId, 317, 317, pool: Program.ManaPool)
            : InstanceProtocol.LifeUpdate(Program.PlayerId, 1238));
        Assert.True(game.State.PlayerStats.Remove(narrow.Key));
        InherentGatewayRefresh(game, narrowOnly);
        InherentGatewayRaw(game, narrow.Key, mana ? 1 : 2);
        Assert.True(game.State.PlayerStats.Remove(broad.Key));
        InherentGatewayRefresh(game, blocked);
        InherentGatewayRaw(game, broad.Key, 1);

        if (!mana)
        {
            Assert.NotNull(items.Change(6, carrier.Id, item => item with { Mods = mods.Skip(1).ToArray() }));
            InherentGatewayRefresh(game, blocked);
            InherentGatewayRaw(game, narrow.Key, 1); // Removing one of two native sources does not clear the flag.
        }
        Assert.NotNull(items.Change(6, carrier.Id, item => item with { Mods = [InherentGatewayNative(catalog, broadIndex)] }));
        InherentGatewayRefresh(game, blocked);
        InherentGatewayRaw(game, narrow.Key, 0); // Broad flag alone still prohibits the shared term.
        Assert.NotNull(items.Remove(6, carrier.Id));
        InherentGatewayRefresh(game, InherentGatewayOrdinary);
        InherentGatewayRaw(game, broad.Key, 0);
    }

    [Fact]
    public void InherentAttribute_full_Dexterity_and_Intelligence_vectors_only_affect_the_active_qualified_weapon_set()
    {
        using var game = LevelPanelCharacter(100);
        var catalog = Program.Items; var items = InherentGatewayItems(game);
        InherentGatewayFoundation(game);
        var weapon = items.Add(PlayerInventories.Weapon2, 0, 0,
            LevelPanelCarrier(catalog, "Rusted Sword", InherentGatewayNative(catalog, 3), InherentGatewayNative(catalog, 4))
            with { ItemLevel = 100 });
        string original = AttributeDefencePayload(catalog, weapon.Item);
        InherentGatewayRefresh(game, InherentGatewayOrdinary);
        InherentGatewayRaw(game, 18387, 0); InherentGatewayRaw(game, 18388, 0);
        items.SwapWeaponSets();
        InherentGatewayRefresh(game, new(114, 174, 192, 1295, 634, 111, 200, 21, 22, 11, 38));
        InherentGatewayRaw(game, 18387, 1); InherentGatewayRaw(game, 18388, 1);
        AttributeDefenceQualified(game, weapon);
        Assert.Equal(original, AttributeDefencePayload(catalog, Assert.Single(items.Items(PlayerInventories.Weapon1)).Item));
        items.SwapWeaponSets();
        InherentGatewayRefresh(game, InherentGatewayOrdinary);
        InherentGatewayRaw(game, 18387, 0); InherentGatewayRaw(game, 18388, 0);
    }
}
