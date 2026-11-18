using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
namespace GameServer.Gateway.Tests;
public partial class ConditionalItemStatsTests
{

    void Ring_conditions_rebuild_the_actual_stat_cache_on_same_item_slot_moves()
    {
        // A recorded event answers its "haven't recently" form; one no production commit records stays unmodelled.
        var noEvents = new StatConditions.Subject(Recent: new HashSet<string>(), KnownRecent: Game.TrackedItemRecentEvents);
        Assert.Equal(StatConditions.State.Holds, StatConditions.Of("attack_speed_+%_if_havent_cast_dash_recently", noEvents));
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("lose_power_charge_each_second_if_not_detonated_mines_recently", noEvents));
        Assert.Equal(StatConditions.State.NotModelled,
            StatConditions.Of("damage_taken_+%_if_taunted_an_enemy_recently", noEvents));
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
