using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Qualified_endurance_mana_efficiency_reaches_payment(ClientState conn, Func<string, byte, InventoryItem> wear)
    {
        var inventory = Game.ItemsOf(conn);
        var saved = inventory.EquipmentSnapshot();
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        var charges = Game.ChargesOf(conn);
        uint minimum = Key("base_minimum_endurance_charges");
        try
        {
            Assert.False(conn.PetrifiedBlood.Active);
            foreach (var (row, item) in saved.Where(e => e.Row is not (6 or 7))) inventory.Remove(row, item.Id);
            wear("Astramentis", 5);
            var source = wear("Wyrmsign", PlayerInventories.Gloves);
            Item Gem(string name, int level)
            {
                var type = Program.Items.Find(name)!;
                return ItemCatalog.Create(type) with { GemExperience = Program.Items.Gem(type)!.ExperienceFor(level) };
            }
            Item fireball = Gem("Fireball", 17), faster = Gem("Faster Casting Support", 1), pb = Gem("Petrified Blood", 1);
            var host = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
            {
                Sockets = [new(SocketColour.White, fireball), new(SocketColour.White, faster), new(SocketColour.White, pb)],
                Links = [2, 1],
            };
            var weapon = inventory.Add(PlayerInventories.Weapon1, 0, 0, host);
            ushort Skill(string effect) => Game.GemSkillEffects(conn).Single(g => Game.SkillsFor(conn).EffectId(g.Effect) == effect).Id;
            ushort skill = Skill("Fireball");
            Dictionary<uint, int> stats = [];
            int Get(string name) => stats.GetValueOrDefault(Key(name));
            void Count(int count)
            {
                stats = Game.CharacterPlayerStats(conn);
                charges.Admit(Game.ResourceEffectsTime(conn), [new ChargeEvent.Set(ChargeType.Endurance, count)], Get,
                    () => Game.WeaponProfileFor(conn), true, new Random(1));
                stats = Game.CharacterPlayerStats(conn);
            }
            int Cost() => Game.SupportedStats(conn, skill)[Key("mana_cost")];
            int PanelCost() => Game.PlayerSkills(conn).Single(s => s.Id == skill).Stats!.Single(s => s.Key == Key("mana_cost")).Value;
            Count(0);
            int coefficient = Get(ItemChargeCosts.ManaEfficiencyPerEndurance);
            Assert.Equal(10, coefficient);
            int baseline = Cost(); Assert.True(baseline > 10);
            foreach (int count in new[] { 0, 1, 3, 0 })
            {
                Count(count);
                Assert.Equal(count * coefficient, Get(ItemChargeCosts.ManaEfficiency));
                int expected = (int)Math.Round(baseline * 100m / (100 + count * coefficient), MidpointRounding.AwayFromZero);
                Assert.Equal(expected, Cost()); Assert.Equal(expected, PanelCost());
                conn.PlayerMana = (uint)(expected - 1); conn.PlayerLife = conn.PlayerMaxLife;
                Assert.False(Game.PayMana(conn, skill, out _, out _)); Assert.Equal((uint)(expected - 1), conn.PlayerMana);
                conn.PlayerMana = conn.Pools.Mana;
                uint before = conn.PlayerMana;
                Assert.True(Game.PayMana(conn, skill, out uint paid, out uint life));
                Assert.Equal(((uint)expected, 0u, before - (uint)expected), (paid, life, conn.PlayerMana));
            }
            conn.PlayerStats[minimum] = 2; Count(0);
            Assert.Equal(2, Get("current_endurance_charges")); Assert.Equal(20, Get(ItemChargeCosts.ManaEfficiency));
            conn.PlayerStats.Remove(minimum); Count(3);
            var converted = wear("Arn's Anguish", PlayerInventories.Belt);
            stats = Game.CharacterPlayerStats(conn);
            Assert.Equal(0, Get("current_endurance_charges")); Assert.Equal(3, Get("current_brutal_charges"));
            Assert.Equal(0, Get(ItemChargeCosts.ManaEfficiency)); Assert.Equal(baseline, Cost());
            inventory.Remove(PlayerInventories.Belt, converted.Id); Count(0);

            // The actual independently socketed Petrified Blood owns the additional Life cost.
            var petrified = Game.GemSkillEffects(conn).Single(g => g.Id == Skill(PetrifiedBlood.EffectId));
            using var wire = new Wire();
            bool ToggleBlood() => Game.TogglePetrifiedBlood("wyrmsign-test", wire.Stream, wire.Ciphers, conn,
                petrified.Id, petrified.Effect, petrified.Name, Program.Skills.Reservation(petrified.Effect, petrified.Level)!.Value,
                petrified.Level, petrified.Quality).GetAwaiter().GetResult();
            conn.PlayerMana = conn.Pools.Mana; conn.PlayerLife = conn.PlayerMaxLife;
            Assert.True(ToggleBlood()); Assert.True(conn.PetrifiedBlood.Active);
            int extraLife = PetrifiedBlood.LifeCost(conn, baseline); Assert.True(extraLife > 0);
            foreach (int count in new[] { 0, 3 })
            {
                Count(count); conn.PlayerMana = (uint)Game.UnreservedMana(conn); conn.PlayerLife = conn.PlayerMaxLife;
                Assert.True(Game.PayMana(conn, skill, out uint paid, out uint life));
                Assert.Equal((uint)Cost(), paid); Assert.Equal((uint)extraLife, life);
            }
            Assert.True(ToggleBlood()); Assert.False(conn.PetrifiedBlood.Active);
            var magic = wear("Malachai's Simula", PlayerInventories.Helmet);
            Count(0); int bloodMagicCost = Cost();
            Count(3); Assert.Equal(bloodMagicCost, Cost());
            conn.PlayerLife = conn.PlayerMaxLife;
            Assert.True(Game.PayMana(conn, skill, out uint manaPaid, out uint lifePaid));
            Assert.Equal((0u, (uint)bloodMagicCost), (manaPaid, lifePaid));
            inventory.Remove(PlayerInventories.Helmet, magic.Id);
            Assert.NotNull(inventory.Change(PlayerInventories.Weapon1, weapon.Id, item => item with
            {
                Sockets = [new(SocketColour.White, fireball), new(SocketColour.White, Gem("Lifetap Support", 1)),
                    new(SocketColour.White, pb)], Links = [2, 1],
            }));
            skill = Skill("Fireball"); Count(0); int lifetapCost = Cost();
            Count(3); Assert.Equal(lifetapCost, Cost());
            conn.PlayerLife = conn.PlayerMaxLife;
            Assert.True(Game.PayMana(conn, skill, out manaPaid, out lifePaid));
            Assert.Equal((0u, (uint)lifetapCost), (manaPaid, lifePaid));
            // Native Life costs keep their own resource scope after the upstream Reap integration.
            Assert.NotNull(inventory.Change(PlayerInventories.Weapon1, weapon.Id, item => item with
            {
                Sockets = [new(SocketColour.White, Gem("Reap", 1))], Links = [1],
            }));
            skill = Game.GemSkillEffects(conn).Single().Id;
            foreach (int blood in new[] { 0, 5 })
            {
                conn.BloodCharges = blood;
                int expectedLife = blood == 0 ? 25 : 50;
                foreach (int endurance in new[] { 0, 3 })
                {
                    Count(endurance); conn.PlayerLife = conn.PlayerMaxLife; conn.PlayerMana = conn.Pools.Mana;
                    uint beforeLife = conn.PlayerLife, beforeMana = conn.PlayerMana;
                    Assert.True(Game.PayMana(conn, skill, out manaPaid, out lifePaid));
                    Assert.Equal((0u, (uint)expectedLife), (manaPaid, lifePaid));
                    Assert.Equal(beforeLife - (uint)expectedLife, conn.PlayerLife); Assert.Equal(beforeMana, conn.PlayerMana);
                    conn.PlayerLife = (uint)expectedLife;
                    Assert.False(Game.CanPaySkillCost(conn, skill)); Assert.False(Game.PayMana(conn, skill, out _, out _));
                    Assert.Equal((uint)expectedLife, conn.PlayerLife);
                }
            }
            conn.BloodCharges = 0;
            conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
            Assert.True(Game.IsInactive(conn, source.Id)); stats = Game.CharacterPlayerStats(conn);
            Assert.Equal(0, Get(ItemChargeCosts.ManaEfficiencyPerEndurance));
            conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100); Count(3);
            Assert.Equal(30, Get(ItemChargeCosts.ManaEfficiency));
            inventory.Remove(PlayerInventories.Gloves, source.Id); stats = Game.CharacterPlayerStats(conn);
            Assert.Equal(0, Get(ItemChargeCosts.ManaEfficiency));
            var inputs = new Dictionary<string, int>
            {
                [ItemChargeCosts.ManaEfficiency] = 30, ["support_inspiration_mana_cost_+%_final"] = -40,
                ["skill_mana_cost_+"] = -3,
            };
            Assert.Equal(11, ManaRecoveryRules.Cost(20, inputs.GetValueOrDefault, false, more: 1.5));
            Assert.Equal(15, ManaRecoveryRules.Cost(20, inputs.GetValueOrDefault, false, 1.5, applyManaEfficiency: false));
            Assert.Empty(conn.PlayerStats);
        }
        finally
        {
            conn.PlayerStats.Remove(minimum); conn.PetrifiedBlood.Suspend(); charges.Die(); conn.BloodCharges = 0;
            foreach (var (row, item) in inventory.EquipmentSnapshot()) inventory.Remove(row, item.Id);
            foreach (var (row, item) in saved) inventory.Add(row, item.X, item.Y, item.Item);
            conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.PlayerMaxLife; conn.PlayerMana = conn.Pools.Mana;
        }
    }
}
