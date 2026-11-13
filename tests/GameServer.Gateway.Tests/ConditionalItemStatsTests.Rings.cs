using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Source_relative_ring_conditions_use_qualified_counterparts()
    {
        var catalog = Program.Items;
        var uniques = new UniqueCatalog(catalog);
        Item left = uniques.Create("Mark of the Elder", new Random(504))!;
        Item right = uniques.Create("Mark of the Shaper", new Random(504))!;
        Item veil = uniques.Create("Viridi's Veil", new Random(504))!;
        Item plain = ItemCatalog.Create(catalog.Find("Metadata/Items/Rings/Ring1")!);
        var rows = new List<(byte Row, InventoryItem Item)>();
        Dictionary<string, int> Read(params Item[] active)
        {
            var slots = Game.ConditionalRings(rows, active);
            return ItemModStats.Sum(active, catalog.Mods,
                subject: new(ItemValue: slots.Value), statExists: Program.Stats.Has);
        }
        void Wear(Item? a, Item? b)
        {
            rows.Clear();
            if (a is not null) rows.Add((6, new(1, 0, 0, a)));
            if (b is not null) rows.Add((7, new(2, 0, 0, b)));
        }
        int Raw(Item item, string name) => ItemModStats.Sum([item], catalog.Mods, local: true).GetValueOrDefault(name);
        string attack = "attack_damage_+%_if_other_ring_is_shaper_item";
        string spell = "spell_damage_+%_if_other_ring_is_elder_item";
        int attackBonus = Raw(left, attack), spellBonus = Raw(right, spell);
        Assert.True(attackBonus > 0 && spellBonus > 0, $"Elder={attackBonus}, Shaper={spellBonus}; "
            + string.Join(",", left.Mods.Concat(right.Mods).Where(m => catalog.Mods.ByHash(m.Hash)?.Id.Contains("ItemUnique") == true)
                .Select(m => $"{catalog.Mods.ByHash(m.Hash)?.Id}:{string.Join('/', m.Values)}:{string.Join('/', catalog.Mods.ByHash(m.Hash)!.Stats.Select(s => s.Id))}")));
        left = left with { Influences = ItemInfluence.Shaper };
        Wear(left, null);
        Assert.Equal(0, Read(left).GetValueOrDefault("attack_damage_+%")); // Its own influence is insufficient.
        Wear(left, plain);
        Assert.Equal(0, Read(left, plain).GetValueOrDefault("attack_damage_+%"));
        var partner = plain with { Influences = ItemInfluence.Shaper | ItemInfluence.Elder };
        Wear(left, partner);
        var positive = Read(left, partner);
        Assert.Equal(attackBonus, positive.GetValueOrDefault("attack_damage_+%"));
        Assert.Equal(1, positive.GetValueOrDefault("cannot_be_stunned_by_attacks"));
        Assert.Equal(0, PlayerHitRecovery.Chance(1000, 1000, n => positive.GetValueOrDefault(n)));
        Assert.Equal(0, Read(left).GetValueOrDefault("attack_damage_+%")); // Physically worn but unqualified counterpart.
        Wear(partner, left);
        Assert.Equal(attackBonus, Read(left, partner).GetValueOrDefault("attack_damage_+%"));
        left = left with { Influences = ItemInfluence.Elder };
        right = right with { Influences = ItemInfluence.Shaper };
        Wear(left, right);
        var paired = Read(left, right);
        Assert.Equal(attackBonus, paired.GetValueOrDefault("attack_damage_+%"));
        Assert.Equal(spellBonus, paired.GetValueOrDefault("spell_damage_+%"));
        Assert.Equal(0, paired.GetValueOrDefault("cannot_be_stunned_by_attacks"));
        Assert.Equal(0, paired.GetValueOrDefault("cannot_be_stunned_by_spells"));
        Wear(right, left);
        Assert.Equal(spellBonus, Read(left, right).GetValueOrDefault("spell_damage_+%"));
        right = right with { Influences = ItemInfluence.Elder };
        partner = partner with { Influences = ItemInfluence.Shaper };
        Wear(right, partner);
        Assert.Equal(1, Read(right, partner).GetValueOrDefault("cannot_be_stunned_by_spells"));
        Assert.Equal(0, Read(right).GetValueOrDefault("spell_damage_+%"));
        var magic = plain with { Rarity = ItemRarity.Magic };
        Wear(magic, plain);
        var leftMagic = Read(veil, magic, plain);
        Assert.Equal(1, leftMagic.GetValueOrDefault("self_take_no_extra_damage_from_critical_strikes"));
        Assert.Equal(100, MonsterModEffects.CriticalMultiplierOnPlayer(Game.SelfCriticalReduction(n => leftMagic.GetValueOrDefault(n))));
        Assert.Equal(0, leftMagic.GetValueOrDefault("hexproof"));
        Wear(plain, magic);
        var rightMagic = Read(veil, magic, plain);
        Assert.Equal(0, rightMagic.GetValueOrDefault("self_take_no_extra_damage_from_critical_strikes"));
        Assert.Equal(MonsterModEffects.CriticalMultiplier,
            MonsterModEffects.CriticalMultiplierOnPlayer(Game.SelfCriticalReduction(n => rightMagic.GetValueOrDefault(n))));
        var criticalMod = catalog.Mods.ById("MonsterModAlwaysCrit")!;
        var attacker = new SpawnedMonster("actual critical modifier", new(100, 100, 0), 0, 0, 100)
        {
            Mods = MonsterModSet.None with { Effects = new(criticalMod.Stats.ToDictionary(s => s.Id, s => s.Min)) },
        };
        var incoming = DamagePacket.Of(DamageType.Physical, 100, 100);
        Assert.Equal(100, Game.MonsterModHit(attacker, incoming, 1000, 1000, new FixedRandom(0), out bool leftCrit,
            () => Game.SelfCriticalReduction(n => leftMagic.GetValueOrDefault(n))).MinTotal);
        Assert.True(leftCrit);
        Assert.Equal(130, Game.MonsterModHit(attacker, incoming, 1000, 1000, new FixedRandom(0), out bool rightCrit,
            () => Game.SelfCriticalReduction(n => rightMagic.GetValueOrDefault(n))).MinTotal);
        Assert.True(rightCrit);
        Assert.Equal(1, rightMagic.GetValueOrDefault("hexproof"));
        Assert.NotEqual(Game.ConditionalRings(rows).Flags["_if_left_ring_is_magic_item"],
            Game.ConditionalRings(rows).Flags["_if_right_ring_is_magic_item"]);
        Wear(null, magic);
        Assert.Equal(1, Read(veil, magic).GetValueOrDefault("hexproof"));
        Assert.Equal(0, Read(veil).GetValueOrDefault("hexproof"));
        Assert.Equal(0, Read(magic).GetValueOrDefault("hexproof"));
        var disabled = magic with { Corrupted = true, Influences = ItemInfluence.Elder | ItemInfluence.Shaper };
        Wear(plain, disabled);
        var counts = Game.EquipmentConditionValues(rows, catalog, [plain]);
        Assert.Equal(0, counts["magic_ring_equipped"]);
        Assert.Equal(0, counts["equipped_magic"]);
        Assert.Equal(0, counts["equipped_corrupted"]);
        Assert.Equal(0, counts["equipped_elder"]);
        Assert.Equal(0, counts["equipped_unique_influence_amoung_equipped_non_amulet"]);
        var enabledCounts = Game.EquipmentConditionValues(rows, catalog, [plain, disabled]);
        Assert.Equal(1, enabledCounts["magic_ring_equipped"]);
        Assert.Equal(1, enabledCounts["equipped_elder"]);
        Assert.Equal(1, enabledCounts["equipped_corrupted"]);
        Assert.Equal(2, enabledCounts["equipped_unique_influence_amoung_equipped_non_amulet"]);
        Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of(attack, default));
        Assert.Equal(attackBonus, ItemModStats.Sum([left], catalog.Mods, local: true).GetValueOrDefault(attack));
        Assert.Equal(0, ItemModStats.Sum([left], catalog.Mods).GetValueOrDefault(attack));
        Ring_conditions_rebuild_the_actual_stat_cache_on_same_item_slot_moves();
    }

    void Ring_conditions_rebuild_the_actual_stat_cache_on_same_item_slot_moves()
    {
        string path = Path.Combine(Path.GetTempPath(), $"localexile-ring-conditions-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        string character = "RingConditions" + Guid.NewGuid().ToString("N");
        var priorLogic = Program.GameLogicOrNull;
        var priorLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        try
        {
            PlayerInventories.SetClass(character, 1);
            var conn = new ClientState(AreaLayouts.For(AreaLayouts.TheCoast, 1), 1, 1)
            {
                Character = character, ClassRow = 1, Instance = new(), PlayerLife = 1000,
                PlayerSent = true, PlayerObjectCreated = true, Pools = new(100, 1000, 1000, 0, 0),
                Experience = CharacterStats.ExperienceFor(100),
                LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false),
                    new HashSet<string>(["Game"])),
            };
            var inventory = Game.ItemsOf(conn);
            foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);
            var uniques = new UniqueCatalog(Program.Items);
            var astramentis = inventory.Add(5, 0, 0, uniques.Create("Astramentis", new Random(504))!);
            var elder = inventory.Add(6, 0, 0, uniques.Create("Mark of the Elder", new Random(504))!);
            var shaper = inventory.Add(7, 0, 0, uniques.Create("Mark of the Shaper", new Random(504))!);
            int Stat(string n) => Game.CharacterPlayerStats(conn).GetValueOrDefault(Program.Stats.Key(n)!.Value);
            Assert.Equal(ItemInfluence.Elder, elder.Item.Influences);
            Assert.Equal(ItemInfluence.Shaper, shaper.Item.Influences);
            conn.Pools = Game.PoolsFor(conn, 100);
            Assert.Equal(75, Stat("attack_damage_+%"));
            Assert.Equal(75, Stat("spell_damage_+%"));
            inventory.Remove(7, shaper.Id);
            Assert.Equal(0, Stat("attack_damage_+%"));
            inventory.Remove(6, elder.Id);
            inventory.Put(6, shaper); inventory.Put(7, elder);
            Assert.Equal(75, Stat("attack_damage_+%"));
            Assert.Equal(75, Stat("spell_damage_+%"));
            inventory.Remove(6, shaper.Id); inventory.Remove(7, elder.Id);
            var helmet = inventory.Add(PlayerInventories.Helmet, 0, 0, uniques.Create("Viridi's Veil", new Random(504))!);
            var magic = inventory.Add(6, 0, 0,
                ItemCatalog.Create(Program.Items.Find("Metadata/Items/Rings/Ring1")!) with { Rarity = ItemRarity.Magic });
            Assert.Equal(1, Stat("self_take_no_extra_damage_from_critical_strikes"));
            Assert.Equal(0, Stat("hexproof"));
            var before = conn.StatSourcesCache;
            inventory.Remove(6, magic.Id); inventory.Put(7, magic);
            Assert.Equal(0, Stat("self_take_no_extra_damage_from_critical_strikes"));
            Assert.Equal(1, Stat("hexproof"));
            Assert.NotSame(before, conn.StatSourcesCache);
            inventory.Remove(7, magic.Id);
            Assert.Equal(0, Stat("hexproof"));
            ItemMod Attribute(string id) => Program.Items.Mods.ById(id)!.Roll(ModSection.Explicit, new Random(504)) with { Values = [43] };
            inventory.Add(6, 0, 0, ItemCatalog.Create(Program.Items.Find("Metadata/Items/Rings/Ring1")!)
                with { Mods = [Attribute("Strength8"), Attribute("Intelligence8")] });
            ItemMod Minimum(string id) => Program.Items.Mods.ById(id)!.Roll(ModSection.Explicit, new Random(504))
                with { Values = [Program.Items.Mods.ById(id)!.Stats[0].Min] };
            inventory.Add(PlayerInventories.Belt, 0, 0, ItemCatalog.Create(Program.Items.Find("Metadata/Items/Belts/Belt1")!)
                with { Rarity = ItemRarity.Rare, Mods = [Minimum("Strength5"), Minimum("Intelligence3")] });
            var fireballType = Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!;
            var fireball = ItemCatalog.Create(fireballType) with { GemExperience = Program.Items.Gem(fireballType)!.ExperienceFor(17) };
            var socketed = inventory.Add(7, 0, 0, ItemCatalog.Create(Program.Items.Find("Metadata/Items/Rings/Ring15")!)
                with { Rarity = ItemRarity.Magic, Sockets = [new(SocketColour.Blue, fireball)] });
            Assert.Equal(1, Stat("hexproof"));
            Assert.Equal(-1, Stat("base_enemy_extra_damage_rolls"));
            Assert.False(Game.IsInactive(conn, socketed.Id));
            inventory.Remove(5, astramentis.Id);
            Assert.Equal(0, Stat("hexproof"));
            Assert.Equal(0, Stat("base_enemy_extra_damage_rolls"));
            Assert.True(Game.IsInactive(conn, socketed.Id));
            Assert.False(Game.IsInactive(conn, helmet.Id));
            inventory.Put(5, astramentis);
            Assert.Equal(1, Stat("hexproof"));
            Assert.Equal(-1, Stat("base_enemy_extra_damage_rolls"));
            Assert.Empty(conn.PlayerStats);
        }
        finally
        {
            PlayerInventories.Forget(character); Game.InactiveItems.TryRemove(character, out _);
            lifetime.Stop(ex => throw ex); Game.CombatLifetime = priorLifetime;
            Program.SetGameLogic(priorLogic!); TestEnvironment.ReleaseSaves(path);
        }
    }
}
