using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Charge_presence_conditions_follow_real_counts_minima_and_conversion()
    {
        var cases = new[]
        {
            ("IncreasedDamageAtNoFrenzyChargesUnique__1", "damage_+%", "_while_no_frenzy_charges", 60),
            ("GlobalCriticalMultiplierWithNoFrenzyChargesUnique__1", "base_critical_strike_multiplier_+", "_while_you_have_no_frenzy_charges", 50),
            ("CriticalStrikeMultiplierFrenzyChargesUnique__1", "base_critical_strike_multiplier_+", "_while_have_any_frenzy_charges", 30),
            ("ReducedExtraDamageFromCritsWithNoPowerChargesUnique__1", "base_self_critical_strike_multiplier_-%", "_while_no_power_charges", -50),
            ("IncreasedAreaOfSkillsWithNoFrenzyChargesUnique__1_", "skill_area_of_effect_+%", "_while_no_frenzy_charges",
                Program.Items.Mods.ById("IncreasedAreaOfSkillsWithNoFrenzyChargesUnique__1_")!.Stats[0].Min),
        };
        foreach (var (mod, target, flag, amount) in cases)
        {
            var item = WithMod(mod, amount);
            var on = new StatConditions.Subject(Flags: new Dictionary<string, bool> { [flag] = true });
            var off = new StatConditions.Subject(Flags: new Dictionary<string, bool> { [flag] = false });
            var values = ItemModStats.Sum([item], Program.Items.Mods, subject: on, statExists: Program.Stats.Has);
            Assert.Equal(Math.Abs(amount), values.GetValueOrDefault(target));
            if (target == "skill_area_of_effect_+%")
                Assert.Equal(1 + amount / 100d, SkillArea.Modifier(n => values.GetValueOrDefault(n), spell: true));
            Assert.Equal(0, ItemModStats.Sum([item], Program.Items.Mods, subject: off, statExists: Program.Stats.Has).GetValueOrDefault(target));
            var source = Program.Items.Mods.ById(mod)!.Stats[0].Id;
            Assert.Equal(amount, ItemModStats.Sum([item], Program.Items.Mods, subject: on, statExists: _ => false).GetValueOrDefault(source));
        }
        string path = Path.Combine(Path.GetTempPath(), $"localexile-charge-presence-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        string character = "ChargePresence" + Guid.NewGuid().ToString("N");
        var priorLogic = Program.GameLogicOrNull; var priorLifetime = Game.CombatLifetime;
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
                LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false), new HashSet<string>(["Game"])),
            };
            var inventory = Game.ItemsOf(conn);
            foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);
            var uniques = new UniqueCatalog(Program.Items);
            inventory.Add(5, 0, 0, uniques.Create("Astramentis", new Random(504))!);
            ItemMod Minimum(string id) => Program.Items.Mods.ById(id)!.Roll(ModSection.Explicit, new Random(504))
                with { Values = [Program.Items.Mods.ById(id)!.Stats[0].Min] };
            foreach (byte row in new byte[] { 6, 7 })
                inventory.Add(row, 0, 0, ItemCatalog.Create(Program.Items.Find("Metadata/Items/Rings/Ring1")!)
                    with { Rarity = ItemRarity.Rare, Mods = [Minimum("Dexterity10"), Minimum("Strength10")] });
            int Stat(string n) => Game.CharacterPlayerStats(conn).GetValueOrDefault(Program.Stats.Key(n)!.Value);
            InventoryItem Wear(string name)
            {
                foreach (var old in inventory.Items(PlayerInventories.Weapon1)) inventory.Remove(PlayerInventories.Weapon1, old.Id);
                var placed = inventory.Add(PlayerInventories.Weapon1, 0, 0, uniques.Create(name, new Random(504))!);
                conn.Pools = Game.PoolsFor(conn, 100);
                Assert.False(Game.IsInactive(conn, placed.Id)); return placed;
            }
            void Gain(ChargeType type, double at) => Game.ChargesOf(conn).State.Gain(type, 1, at, Game.ChargesOf(conn).Limits(type));
            int Bonus(InventoryItem item, string mod) => item.Item.Mods.First(m => Program.Items.Mods.ByHash(m.Hash)?.Id == mod).Values[0];
            var passion = Wear("Daresso's Passion");
            int damage = Bonus(passion, "IncreasedDamageAtNoFrenzyChargesUnique__1");
            Assert.Equal(damage, Stat("damage_+%"));
            using (Game.PlayerStatsScope(conn))
            {
                Assert.Equal(damage, Stat("damage_+%"));
                Assert.Equal(damage, Stat("damage_+%"));
                Gain(ChargeType.Frenzy, 100);
                Assert.Equal(0, Stat("damage_+%"));
                Assert.Equal(1, Stat("current_frenzy_charges"));
                Gain(ChargeType.Frenzy, 100);
                Assert.Equal(2, Stat("current_frenzy_charges"));
            }
            Game.ChargesOf(conn).Refresh(111);
            Assert.Equal(damage, Stat("damage_+%"));
            var minimum = inventory.Add(PlayerInventories.Belt, 0, 0,
                ItemCatalog.Create(Program.Items.Find("Metadata/Items/Belts/Belt1")!) with { Mods = [Minimum("MinimumFrenzyChargeInfluence1")] });
            Assert.Equal(0, Stat("damage_+%")); // The first read uses this new minimum, not the ticker's previous limits.
            Assert.Equal(1, Stat("current_frenzy_charges"));
            inventory.Remove(PlayerInventories.Belt, minimum.Id);
            Assert.Equal(damage, Stat("damage_+%"));
            Gain(ChargeType.Frenzy, 112);
            using (Game.PlayerStatsScope(conn))
            {
                Assert.Equal(0, Stat("damage_+%"));
                Assert.Equal(0, Stat("damage_+%"));
                inventory.Add(PlayerInventories.Belt, 0, 0, uniques.Create("Olesya's Delight", new Random(504))!);
                Assert.Equal(damage, Stat("damage_+%")); // Affliction is not a standard Frenzy Charge.
                Assert.Equal(0, Stat("current_frenzy_charges"));
            }
            foreach (var belt in inventory.Items(PlayerInventories.Belt)) inventory.Remove(PlayerInventories.Belt, belt.Id);
            Game.ChargesOf(conn).Die();
            Wear("Ahn's Might");
            Assert.Equal(50, Stat("base_critical_strike_multiplier_+"));
            Gain(ChargeType.Frenzy, 120);
            Assert.Equal(0, Stat("base_critical_strike_multiplier_+"));
            var tide = Wear("The Gluttonous Tide");
            Assert.Equal(Bonus(tide, "CriticalStrikeMultiplierFrenzyChargesUnique__1"), Stat("base_critical_strike_multiplier_+"));
            Game.ChargesOf(conn).Refresh(131);
            Assert.Equal(0, Stat("base_critical_strike_multiplier_+"));
            var helmet = inventory.Add(PlayerInventories.Helmet, 0, 0, uniques.Create("Ahn's Contempt", new Random(504))!);
            Assert.Equal(50, Game.SelfCriticalReduction(conn));
            Assert.Equal(-50, Stat("extra_damage_taken_from_crit_+%"));
            Gain(ChargeType.Power, 140);
            Assert.Equal(0, Game.SelfCriticalReduction(conn));
            Game.ChargesOf(conn).Refresh(151);
            Assert.Equal(50, Game.SelfCriticalReduction(conn));
            inventory.Remove(PlayerInventories.Helmet, helmet.Id);
            Assert.Equal(0, Game.SelfCriticalReduction(conn));
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
