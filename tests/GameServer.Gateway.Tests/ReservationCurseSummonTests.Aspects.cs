using System.Text.Json;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class ReservationCurseSummonTests
{
    static async Task AvianAspectFixture(ClientState template, ModuleLifetime lifetime)
    {
        string character = "AvianRuntime" + Guid.NewGuid().ToString("N");
        PlayerInventories.SetClass(character, 1);
        CharacterSaves.Write(character, new(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(),
            Experience: CharacterStats.ExperienceFor(100)));
        var schedule = new CatClockSchedule { At = 100 };
        var updates = new PlayerUpdatesState(lifetime, schedule);
        var previousContext = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = updates;
        using var wire = new Wire();
        var conn = new ClientState(template.Layout, 0xA710, 1)
        {
            Character = character, CharacterId = Guid.NewGuid(), Account = "avian-runtime",
            League = "Standard", LeagueForm = "Standard", ClassRow = 1, Instance = new(),
            PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
            Experience = CharacterStats.ExperienceFor(100), LeagueModule = template.LeagueModule,
            Wire = ("avian", wire.Stream, wire.Ciphers), Pools = new(100, 1, 1, 0, 0),
            PlayerLife = 1,
        };
        try
        {
            var inventory = Game.ItemsOf(conn);
            foreach (byte row in new byte[] { PlayerInventories.Weapon1, PlayerInventories.Helmet,
                         PlayerInventories.BodyArmour, PlayerInventories.Boots, PlayerInventories.Gloves, 5, 6, 7 })
                foreach (var old in inventory.Items(row)) inventory.Remove(row, old.Id);
            var uniques = new UniqueCatalog(Program.Items);
            inventory.Add(5, 0, 0, uniques.Create("Astramentis", new Random(504))!);
            inventory.Add(6, 0, 0, uniques.Create("Le Heup of All", new Random(504))!);
            inventory.Add(7, 0, 0, uniques.Create("Le Heup of All", new Random(505))!);
            var talons = inventory.Add(PlayerInventories.Boots, 0, 0, uniques.Create("Saqawal's Talons", new Random(504))!);
            var winds = inventory.Add(PlayerInventories.Gloves, 0, 0, uniques.Create("Saqawal's Winds", new Random(504))!);
            conn.Pools = Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.Pools.Life;
            conn.PlayerMana = conn.Pools.Mana;
            int Stat(string name) => Program.Stats.Key(name) is uint key
                ? Game.CharacterPlayerStats(conn).GetValueOrDefault(key) : 0;
            int Source(string name) => Program.Stats.Key(name) is uint key
                ? Game.CharacterStatSources(conn).Stats.GetValueOrDefault(key) : 0;
            var source = Assert.Single(Game.GemSkillEffects(conn), g => Program.Skills.EffectId(g.Effect) == "AspectOfTheAvian");
            Assert.True(Game.SupportedSkill(conn, source.Id).RequirementsMet);
            Assert.False(Program.Skills.IsAura(source.Effect));
            var cost = Game.SupportedReservationCost(conn, source.Id)!.Value;
            Assert.Equal(2500, cost.ManaPercent);
            int movementOff = conn.Pools.MovementVelocityPct;
            int regenOff = Stat("base_life_regeneration_rate_per_minute");
            int manaRegenOff = Stat("base_mana_regeneration_rate_per_minute");
            int addedOff = Stat("spell_minimum_added_cold_damage");
            var addedBefore = new Dictionary<bool, DamagePacket>
            {
                [false] = DamageStats.RawAdded(DamagePacket.Empty, Stat, spell: false),
                [true] = DamageStats.RawAdded(DamagePacket.Empty, Stat, spell: true),
            };
            Assert.True(await Game.ToggleAura("avian", wire.Stream, wire.Ciphers, conn,
                source.Id, source.Effect, source.Name, cost, source.Level, source.Quality));
            Assert.Equal(ReservationState.AvianPhase.Might, conn.Reservations.Avian);
            Assert.Equal(1040, conn.Reservations.PresentedAvianRow);
            Assert.Equal(10, Stat("chance_to_deal_double_damage_%"));
            Assert.Equal(10, Stat("minion_chance_to_deal_double_damage_%"));
            Assert.Equal(1, Stat("has_avians_might"));
            Assert.Equal(0, Stat("has_avians_flight"));
            Assert.Equal(addedOff, Stat("spell_minimum_added_cold_damage"));
            foreach (bool spell in new[] { false, true })
            {
                var added = DamageStats.RawAdded(DamagePacket.Empty, Stat, spell);
                foreach (var (element, type) in new[] { ("cold", DamageType.Cold), ("lightning", DamageType.Lightning) })
                {
                    Assert.Equal(addedBefore[spell][type].Min + Source($"minimum_added_{element}_damage_while_you_have_avians_might"), added[type].Min);
                    Assert.Equal(addedBefore[spell][type].Max + Source($"maximum_added_{element}_damage_while_you_have_avians_might"), added[type].Max);
                }
            }
            Assert.Equal(movementOff, conn.Pools.MovementVelocityPct);
            double mightEnd = 100 + (4000 + Source("avians_might_duration_ms_+")) / 1000d;
            Assert.Equal(mightEnd, conn.Reservations.AvianDeadline, 6);
            var golem = new GolemState.Golem(0xA711, null!,
                new(0, "", "Stone", "", 0, [], false, false, false), 1);
            var minionMods = Game.GolemModifiers(conn, golem, Game.CharacterPlayerStats(conn));
            Assert.Equal(10, minionMods.Effects["chance_to_deal_double_damage_%"]);
            var hit = DamagePacket.Of(DamageType.Physical, 100, 100);
            Assert.Equal(200, Game.GolemRollHit(hit, minionMods.Effects, false, new AspectRoll(9)).MinTotal);
            Assert.Equal(100, Game.GolemRollHit(hit, minionMods.Effects, true, new AspectRoll(10)).MinTotal);

            var nest = inventory.Add(PlayerInventories.BodyArmour, 0, 0, uniques.Create("Saqawal's Nest", new Random(504))!);
            Game.ReconcileOwnAuras(conn, validateSkills: true);
            Assert.Equal(20, Stat("chance_to_deal_double_damage_%"));
            // The local buff is not scaled by ordinary aura effect.
            Assert.Equal(100, Source("aspect_of_the_avian_buff_effect_+%"));
            schedule.At = mightEnd - .001;
            await Game.ReservationSkillsTick("avian", wire.Stream, wire.Ciphers, conn, schedule.At);
            Assert.Equal(ReservationState.AvianPhase.Might, conn.Reservations.Avian);
            schedule.At = mightEnd;
            await Game.ReservationSkillsTick("avian", wire.Stream, wire.Ciphers, conn, schedule.At);
            Assert.Equal(ReservationState.AvianPhase.Flight, conn.Reservations.Avian);
            Assert.Equal(1041, conn.Reservations.PresentedAvianRow);
            Assert.Equal(0, Stat("chance_to_deal_double_damage_%"));
            Assert.Equal(0, Stat("minion_chance_to_deal_double_damage_%"));
            Assert.Equal(20, Stat("minion_movement_speed_+%"));
            Assert.Equal(0, Stat("has_avians_might"));
            Assert.Equal(1, Stat("has_avians_flight"));
            Assert.Equal(6000 + regenOff, Stat("base_life_regeneration_rate_per_minute"));
            Assert.Equal(720 + manaRegenOff, Stat("base_mana_regeneration_rate_per_minute"));
            Assert.Equal(addedOff, Stat("spell_minimum_added_cold_damage"));
            foreach (bool spell in new[] { false, true })
            {
                var added = DamageStats.RawAdded(DamagePacket.Empty, Stat, spell);
                foreach (var type in new[] { DamageType.Cold, DamageType.Lightning })
                    Assert.Equal(addedBefore[spell][type], added[type]);
            }
            int withoutFlight = Game.PoolsFor(conn, 100).MovementVelocityPct - 20;
            Assert.Equal(withoutFlight + 20, conn.Pools.MovementVelocityPct);
            minionMods = Game.GolemModifiers(conn, golem, Game.CharacterPlayerStats(conn));
            Assert.Equal(20, minionMods.Effects.MovementPct);
            Assert.Equal(100, Game.GolemRollHit(hit, minionMods.Effects, false, new AspectRoll(0)).MinTotal);

            var buffs = GameServer.Core.ClientTables.Table("BuffDefinitions");
            foreach (int row in new[] { 1039, 1040, 1041 })
                Assert.False(buffs[row].Bool("IsRecovery"));
            inventory.Add(PlayerInventories.Helmet, 0, 0, uniques.Create("Farrul's Bite", new Random(504))!);
            Game.ReconcileOwnAuras(conn, validateSkills: true);
            var cat = Assert.Single(Game.GemSkillEffects(conn), g => Program.Skills.EffectId(g.Effect) == "AspectOfTheCat");
            Assert.False(await Game.ToggleAura("avian-exclusive", wire.Stream, wire.Ciphers, conn,
                cat.Id, cat.Effect, cat.Name, Game.SupportedReservationCost(conn, cat.Id)!.Value, cat.Level, cat.Quality));
            Assert.Single(conn.Auras);
            var owner = conn.Reservations.AvianOwner;
            schedule.At += 1000000;
            await Game.ReservationSkillsTick("avian", wire.Stream, wire.Ciphers, conn, schedule.At);
            Assert.Equal(owner, conn.Reservations.AvianOwner);
            Assert.True(conn.Reservations.AvianDeadline > schedule.At);
            inventory.Remove(PlayerInventories.Boots, talons.Id);
            Game.ReconcileOwnAuras(conn, validateSkills: true);
            Game.WithOwnAuraChanges(conn, () => []);
            Assert.False(Game.AvianActive(conn));
            Assert.Null(conn.Reservations.AvianOwner);
            Assert.Equal(0, conn.Reservations.PresentedAvianBuff);
            Assert.Empty(conn.Auras);
            Assert.Empty(conn.PlayerStats);
        }
        finally
        {
            conn.Closed = true;
            Game.AreaUpdateContext.Value = previousContext;
            PlayerInventories.Forget(character);
        }
    }

    static async Task CrabAspectFixture(ClientState template, ModuleLifetime lifetime)
    {
        string character = "CrabRuntime" + Guid.NewGuid().ToString("N");
        PlayerInventories.SetClass(character, 1);
        CharacterSaves.Write(character, new(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(),
            Experience: CharacterStats.ExperienceFor(100)));
        var schedule = new CatClockSchedule { At = 100 };
        var updates = new PlayerUpdatesState(lifetime, schedule);
        var previousContext = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = updates;
        using var wire = new Wire();
        var conn = new ClientState(template.Layout, 0xC2AB, 1)
        {
            Character = character, CharacterId = Guid.NewGuid(), Account = "crab-runtime",
            League = "Standard", LeagueForm = "Standard", ClassRow = 1, Instance = new(),
            PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
            Experience = CharacterStats.ExperienceFor(100), LeagueModule = template.LeagueModule,
            Wire = ("crab", wire.Stream, wire.Ciphers), Pools = new(100, 1, 1, 0, 0), PlayerLife = 1,
        };
        try
        {
            var inventory = Game.ItemsOf(conn);
            foreach (byte row in new byte[] { PlayerInventories.Weapon1, PlayerInventories.Helmet,
                         PlayerInventories.BodyArmour, PlayerInventories.Boots, PlayerInventories.Gloves, 5, 6, 7, 10 })
                foreach (var old in inventory.Items(row)) inventory.Remove(row, old.Id);
            var uniques = new UniqueCatalog(Program.Items);
            inventory.Add(5, 0, 0, uniques.Create("Astramentis", new Random(504))!);
            inventory.Add(6, 0, 0, uniques.Create("Le Heup of All", new Random(504))!);
            inventory.Add(7, 0, 0, uniques.Create("Le Heup of All", new Random(505))!);
            inventory.Add(10, 0, 0, uniques.Create("Meginord's Girdle", new Random(504))!);
            inventory.Add(PlayerInventories.Helmet, 0, 0, uniques.Create("Black Sun Crest", new Random(504))!);
            var shell = inventory.Add(PlayerInventories.BodyArmour, 0, 0, uniques.Create("Craiceann's Carapace", new Random(504))!);
            conn.Pools = Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.Pools.Life;
            conn.PlayerMana = conn.Pools.Mana;
            Assert.False(Game.IsInactive(conn, shell.Id));
            int Stat(string name) => Program.Stats.Key(name) is uint key
                ? Game.CharacterPlayerStats(conn).GetValueOrDefault(key) : 0;
            var source = Assert.Single(Game.GemSkillEffects(conn), g => Program.Skills.EffectId(g.Effect) == "AspectOfTheCrab");
            Assert.True(await Game.ToggleAura("crab", wire.Stream, wire.Ciphers, conn, source.Id, source.Effect,
                source.Name, Game.SupportedReservationCost(conn, source.Id)!.Value, source.Level, source.Quality));
            Assert.Equal(15, conn.Reservations.CrabMaximum);
            Assert.Equal(0, conn.Reservations.CrabBarriers);
            schedule.At = 100.499;
            await Game.ReservationSkillsTick("crab", wire.Stream, wire.Ciphers, conn, schedule.At);
            Assert.Equal(0, conn.Reservations.CrabBarriers);
            schedule.At = 100.5;
            await Game.ReservationSkillsTick("crab", wire.Stream, wire.Ciphers, conn, schedule.At);
            Assert.Equal(1, conn.Reservations.CrabBarriers);
            Assert.Equal(2, Game.PlayerDamageReduction(conn).Physical);
            schedule.At = 105;
            await Game.ReservationSkillsTick("crab", wire.Stream, wire.Ciphers, conn, schedule.At);
            Assert.Equal(10, conn.Reservations.CrabBarriers);
            Assert.Equal(20, Game.PlayerDamageReduction(conn).Physical);
            Assert.Equal(10, Stat("current_crab_aspect_stacks"));
            Assert.Equal(15, Stat("max_crab_aspect_stacks"));
            var stats = Game.CharacterPlayerStats(conn);
            Game.CrabPhysicalHitLocked(conn, 0, schedule.At, stats);
            Assert.Equal(10, conn.Reservations.CrabBarriers);
            // Exercise the actual resource allocator with physical damage absorbed by ES.
            conn.EnergyShield.SetMaximum(1000);
            conn.EnergyShield.Recover(1000);
            Assert.Equal(0, Game.DamagePlayerHitTyped(conn, 10, 0, 0, schedule.At, stats,
                DamagePacket.Of(DamageType.Physical, 10, 10)));
            Assert.Equal(0, conn.Reservations.CrabBarriers);
            Assert.Equal(0, Game.PlayerDamageReduction(conn).Physical);
            schedule.At = 112.5;
            await Game.ReservationSkillsTick("crab", wire.Stream, wire.Ciphers, conn, schedule.At);
            Assert.Equal(15, conn.Reservations.CrabBarriers);
            Assert.Equal(30, Game.PlayerDamageReduction(conn).Physical);
            var chitin = inventory.Add(PlayerInventories.Helmet, 0, 0,
                uniques.Create("Craiceann's Chitin", new Random(504))!);
            // Replace Black Sun through the actual inventory, preserving requirement checks.
            foreach (var old in inventory.Items(PlayerInventories.Helmet).Where(i => i.Id != chitin.Id).ToArray())
                inventory.Remove(PlayerInventories.Helmet, old.Id);
            var tracks = inventory.Add(PlayerInventories.Boots, 0, 0, uniques.Create("Craiceann's Tracks", new Random(504))!);
            Game.ReconcileOwnAuras(conn, validateSkills: true);
            Assert.False(Game.IsInactive(conn, tracks.Id));
            Assert.False(Game.IsInactive(conn, shell.Id));
            Assert.Equal(1, Stat("cannot_be_stunned"));
            int blockAt15 = conn.Pools.BlockChance;
            stats = Game.CharacterPlayerStats(conn);
            int loss = Stat("number_of_crab_charges_lost_when_hit");
            Game.CrabPhysicalHitLocked(conn, 1, schedule.At, stats);
            Assert.Equal(15 - loss, conn.Reservations.CrabBarriers);
            Game.CrabPhysicalHitLocked(conn, 1, schedule.At + 3.999, stats);
            Assert.Equal(15 - loss, conn.Reservations.CrabBarriers);
            // No clock advancement here: the next physical hit uses current state but does not accrue future barriers.
            Game.CrabPhysicalHitLocked(conn, 1, schedule.At + 4, stats);
            Assert.Equal(Math.Max(0, 15 - 2 * loss), conn.Reservations.CrabBarriers);
            Assert.True(Game.PoolsFor(conn, 100).BlockChance < blockAt15);
            inventory.Remove(PlayerInventories.BodyArmour, shell.Id);
            Game.ReconcileOwnAuras(conn, validateSkills: true);
            Game.WithOwnAuraChanges(conn, () => []);
            Assert.False(Game.CrabActive(conn));
            Assert.Equal(0, conn.Reservations.CrabBarriers);
            Assert.Equal(0, conn.Reservations.PresentedCrabBuff);
            Assert.Empty(conn.PlayerStats);
        }
        finally
        {
            conn.Closed = true;
            Game.AreaUpdateContext.Value = previousContext;
            PlayerInventories.Forget(character);
        }
    }

    sealed class AspectRoll(int value) : Random
    {
        public override int Next(int maxValue) => Math.Min(value, maxValue - 1);
        public override int Next(int minValue, int maxValue) => Math.Clamp(value, minValue, maxValue - 1);
    }
}
