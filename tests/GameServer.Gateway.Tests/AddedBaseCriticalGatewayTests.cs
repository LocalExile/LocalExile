using System.Reflection;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    // Actual native modifiers on isolated carriers; these combinations are not legal acquisition claims.
    static ItemMod AddedCritMod(ItemCatalog catalog, string id, params int[] values)
    {
        var mod = catalog.Mods.ById(id)!;
        Assert.Equal(1, mod.Domain);
        Assert.Equal(id, catalog.Mods.ByHash(mod.Hash)!.Id);
        Assert.Equal(values.Length, mod.Stats.Count);
        for (int i = 0; i < values.Length; i++) Assert.InRange(values[i], mod.Stats[i].Min, mod.Stats[i].Max);
        return new(ModSection.Explicit, mod.Hash, values);
    }

    static PlayerInventories AddedCritInventory(GatewayCharacter game) => ProgramValue<PlayerInventories>("ItemsOf", game.State);
    static ItemCatalog AddedCritCatalog(GatewayCharacter game) => Program.Items;
    static uint AddedCritKey(string name) => Program.Stats.Key(name)!.Value;
    static GatewayCharacter AddedCritCharacter(bool ascendant = false) => ascendant
        ? new(level: 100, classRow: 2, baseClass: 0, choice: "Ascendant", family: AscendancyFamily.Normal)
        : LevelPanelCharacter(100);

    static ushort AddedCritSkill(GatewayCharacter game)
    {
        var gems = ((IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)typeof(Program)
            .GetMethod("GemSkillEffects", BindingFlags.Static | BindingFlags.NonPublic, binder: null,
                types: [typeof(ClientState)], modifiers: null)!.Invoke(null, [game.State])!).ToArray();
        return Assert.Single(gems).Id;
    }

    static InventoryItem AddedCritSocket(GatewayCharacter game, bool attack, bool support, bool linked = true,
        string? gemName = null)
    {
        var catalog = AddedCritCatalog(game); var inventory = AddedCritInventory(game);
        Item Gem(string id)
        {
            var type = catalog.Find("Metadata/Items/Gems/" + id)!;
            return ItemCatalog.Create(type) with { GemExperience = catalog.Gem(type)!.ExperienceFor(1), GemFlags = 4, Quality = 0 };
        }
        var active = Gem(gemName ?? (attack ? "SkillGemBurningArrow" : "SkillGemFireball"));
        var sockets = support
            ? new ItemSocket[] { new(SocketColour.White, active), new(SocketColour.White, Gem("SupportGemIncreasedCriticalStrikes")) }
            : [new(SocketColour.White, active)];
        byte[] links = support ? linked ? [2] : [1, 1] : [1];
        var current = inventory.Items(PlayerInventories.BodyArmour).SingleOrDefault();
        if (current is not null)
            return inventory.Change(PlayerInventories.BodyArmour, current.Id, item => item with { Sockets = sockets, Links = links })!;
        return inventory.Add(PlayerInventories.BodyArmour, 0, 0,
            ItemCatalog.Create(catalog.Find("Plate Vest")!) with { Sockets = sockets, Links = links });
    }

    static InventoryItem AddedCritEquip(GatewayCharacter game, bool attack, bool support = false, params ItemMod[] weaponMods)
    {
        var catalog = AddedCritCatalog(game); var inventory = AddedCritInventory(game);
        inventory.Add(5, 0, 0, ItemCatalog.Create(catalog.Find("Coral Amulet")!) with
            { Mods = [AddedCritMod(catalog, "AllAttributesUniqueAmulet8", 100)] });
        var weapon = inventory.Add(PlayerInventories.Weapon1, 0, 0,
            ItemCatalog.Create(catalog.Find("Crude Bow")!) with { Quality = 0, ItemLevel = 100, Mods = weaponMods });
        Assert.Equal(500, catalog.WeaponCritical(catalog.Find("Crude Bow")!));
        AddedCritSocket(game, attack, support);
        return weapon;
    }

    static void AddedCritRefresh(GatewayCharacter game) => game.State.Pools = Program.PoolsFor(game.State, 100);
    static void AddedCritAssertSelected(GatewayCharacter game, bool attack, int expected)
    {
        AddedCritRefresh(game); ushort skill = AddedCritSkill(game);
        Assert.Equal(expected, ProgramValue<int>("SupportedCriticalChance", game.State, skill));
        var actor = ProgramValue<IReadOnlyList<GrantedSkill>>("PlayerSkills", game.State).Single(s => s.Id == skill);
        var values = actor.Stats!.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal(expected, values[AddedCritKey(attack ? "main_hand_critical_strike_chance" : "spell_critical_strike_chance")]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Added_base_native_typed_item_sources_isolate_actor_families_and_ICS_own200_once(bool attack)
    {
        using var game = AddedCritCharacter(); var catalog = AddedCritCatalog(game);
        AddedCritEquip(game, attack);
        var inventory = AddedCritInventory(game);
        string matching = attack ? "V2IncreasedAtackCriticalStrikeCorruption" : "V2IncreasedSpellCriticalStrikeCorruption";
        string other = attack ? "V2IncreasedSpellCriticalStrikeCorruption" : "V2IncreasedAtackCriticalStrikeCorruption";
        var ring = inventory.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring", AddedCritMod(catalog, other, 50)));
        AddedCritAssertSelected(game, attack, 500);
        Assert.NotNull(inventory.Change(6, ring.Id, item => item with { Mods = [AddedCritMod(catalog, other, 50), AddedCritMod(catalog, matching, 50)] }));
        AddedCritAssertSelected(game, attack, 550);
        AddedCritSocket(game, attack, support: true);
        AddedCritAssertSelected(game, attack, 1200); // (500 + actor50 + own200) * ICS1.6.
        Assert.NotNull(inventory.Remove(6, ring.Id));
        AddedCritAssertSelected(game, attack, 1120);
        AddedCritSocket(game, attack, support: false);
        AddedCritAssertSelected(game, attack, 500);
    }

    [Theory]
    [InlineData(false, 1485, 750)]
    [InlineData(true, 1485, 750)]
    public async Task Added_base_real_Ascendant_Assassin_uses_native_mode_stats_and_link_changes_once(        bool attack, int supported, int unlinked)
    {
        using var game = AddedCritCharacter(ascendant: true);
        // Connected native Ascendant path including the paid Shadow choice and its free Assassin option.
        Assert.Null((await game.Change([45403, 42546, 772, 43122], allocate: true)).Error);
        var tree = Program.Passives;
        Assert.Equal("Assassin", tree.Find(43122)!.Name);
        Assert.Equal(75, tree.Stats([43122]).GetValueOrDefault(AddedCritKey(AddedBaseCriticalStrike.Generic)));
        var catalog = AddedCritCatalog(game); var inventory = AddedCritInventory(game);
        AddedCritEquip(game, attack, support: true);
        inventory.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring", AddedCritMod(catalog,
            attack ? "V2IncreasedAtackCriticalStrikeCorruption" : "V2IncreasedSpellCriticalStrikeCorruption", 50)));
        game.State.PlayerStats[AddedCritKey("critical_strike_chance_+%")] = 20;
        AddedCritAssertSelected(game, attack, supported);
        Assert.Equal(unlinked, attack ? game.State.Pools.AttackCriticalStrikeChance : game.State.Pools.SpellCriticalStrikeChance);
        AddedCritSocket(game, attack, support: true, linked: false);
        AddedCritAssertSelected(game, attack, unlinked);
        AddedCritSocket(game, attack, support: true);
        AddedCritAssertSelected(game, attack, supported);
        AddedCritSocket(game, attack, support: false);
        AddedCritAssertSelected(game, attack, unlinked);
    }

    [Fact]
    public void Added_base_native_weapon_local_flat_precedes_local_INC_then_override_actor_and_support()
    {
        using var game = AddedCritCharacter(); var catalog = AddedCritCatalog(game); var inventory = AddedCritInventory(game);
        var localBase = AddedCritMod(catalog, "WeaponTreeLocalCritChanceCritsDealNoExtraDamage1", 400, 1);
        var localInc = AddedCritMod(catalog, "LocalCriticalStrikeChance3", 20);
        var weapon = AddedCritEquip(game, attack: true, support: true, localBase, localInc);
        // The entire actual native recipe is retained, including no-extra-critical-damage. These assertions claim chance only.
        inventory.Add(6, 0, 0, LevelPanelCarrier(catalog, "Iron Ring",
            AddedCritMod(catalog, "V2IncreasedAtackCriticalStrikeCorruption", 50)));
        Assert.Equal(1080, Program.WeaponProfileFor(game.State).Critical);
        AddedCritAssertSelected(game, attack: true, 2128);
        Assert.Equal(1130, game.State.Pools.AttackCriticalStrikeChance);
        Assert.Equal(1, Program.CharacterPlayerStats(game.State)[AddedCritKey("no_critical_strike_multiplier")]);
        game.State.PlayerStats[AddedCritKey(AttackBaseCriticalStrike.WeaponOverrideStat)] = 800;
        AddedCritAssertSelected(game, attack: true, 1680);
        game.State.PlayerStats[AddedCritKey(AttackBaseCriticalStrike.WeaponOverrideStat)] = 0;
        Assert.NotNull(inventory.Change(PlayerInventories.Weapon1, weapon.Id, item => item with { Mods = [localBase] }));
        Assert.Equal(900, Program.WeaponProfileFor(game.State).Critical);
        AddedCritAssertSelected(game, attack: true, 1840);
        Assert.NotNull(inventory.Change(PlayerInventories.Weapon1, weapon.Id, item => item with { Mods = [] }));
        AddedCritAssertSelected(game, attack: true, 1200);
    }

    [Fact]
    public async Task Added_base_real_set_stat_refreshes_both_pool_families_and_explicit_zero_replaces_its_source()
    {
        using var game = AddedCritCharacter(); AddedCritEquip(game, attack: false);
        Assert.Null((await game.Command("/set_stat additional_base_critical_strike_chance 75")).Error);
        Assert.Equal(575, game.State.Pools.AttackCriticalStrikeChance); Assert.Equal(575, game.State.Pools.SpellCriticalStrikeChance);
        Assert.Null((await game.Command("/set_stat attack_additional_critical_strike_chance_permyriad 50")).Error);
        Assert.Equal(625, game.State.Pools.AttackCriticalStrikeChance); Assert.Equal(575, game.State.Pools.SpellCriticalStrikeChance);
        Assert.Null((await game.Command("/set_stat spell_additional_critical_strike_chance_permyriad 80")).Error);
        Assert.Equal(625, game.State.Pools.AttackCriticalStrikeChance); Assert.Equal(655, game.State.Pools.SpellCriticalStrikeChance);
        foreach (string stat in new[] { AddedBaseCriticalStrike.Generic, AddedBaseCriticalStrike.Attack, AddedBaseCriticalStrike.Spell })
            Assert.Null((await game.Command($"/set_stat {stat} 0")).Error);
        Assert.Equal(500, game.State.Pools.AttackCriticalStrikeChance); Assert.Equal(500, game.State.Pools.SpellCriticalStrikeChance);
        AddedCritAssertSelected(game, attack: false, 500);
    }

    [Fact]
    public void Added_base_hitless_and_missing_selected_skills_never_gain_a_critical_chance()
    {
        using var game = AddedCritCharacter(); AddedCritEquip(game, attack: false);
        game.State.PlayerStats[AddedCritKey(AddedBaseCriticalStrike.Generic)] = 10000;
        AddedCritSocket(game, attack: false, support: true, gemName: "SkillGemRighteousFire");
        AddedCritAssertSelected(game, attack: false, 0);
        Assert.Equal(0, ProgramValue<int>("SupportedCriticalChance", game.State, ushort.MaxValue));
        AddedCritSocket(game, attack: false, support: true);
        AddedCritAssertSelected(game, attack: false, 10000);
    }
}
