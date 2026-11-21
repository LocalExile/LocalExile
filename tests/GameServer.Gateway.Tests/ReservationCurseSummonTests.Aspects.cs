using System.Text.Json;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class ReservationCurseSummonTests
{
    static async Task AvianAspectFixture(Player template, ModuleLifetime lifetime)
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
        var player = new Player(template.Layout, 0xA710, 1)
        {
            Character = { Name = character, Id = Guid.NewGuid(), Account = "avian-runtime", League = "Standard", LeagueForm = "Standard", ClassRow = 1, Experience = CharacterStats.ExperienceFor(100) },
            Instance = new(),
            Connection = { PlayerSent = true, PlayerObjectCreated = true, Session = new("avian", wire.Stream, wire.Ciphers) },
            PlayerUpdatesState = updates,
            LeagueModule = template.LeagueModule,
            Pools = new(100, 1, 1, 0, 0),
            PlayerLife = 1
        };
        try
        {
            var inventory = Game.ItemsOf(player);
            foreach (byte row in new byte[] { PlayerInventories.Weapon1, PlayerInventories.Helmet,
                         PlayerInventories.BodyArmour, PlayerInventories.Boots, PlayerInventories.Gloves, 5, 6, 7 })
                foreach (var old in inventory.Items(row)) inventory.Remove(row, old.Id);
            var uniques = new UniqueCatalog(Program.Items);
            inventory.Add(5, 0, 0, uniques.Create("Astramentis", new Random(504))!);
            inventory.Add(6, 0, 0, uniques.Create("Le Heup of All", new Random(504))!);
            inventory.Add(7, 0, 0, uniques.Create("Le Heup of All", new Random(505))!);
            var talons = inventory.Add(PlayerInventories.Boots, 0, 0, uniques.Create("Saqawal's Talons", new Random(504))!);
            var winds = inventory.Add(PlayerInventories.Gloves, 0, 0, uniques.Create("Saqawal's Winds", new Random(504))!);
            player.Pools = Game.PoolsFor(player, 100);
            player.PlayerLife = player.Pools.Life;
            player.PlayerMana = player.Pools.Mana;
            int Stat(uint name) => name is var key
                ? Game.CharacterPlayerStats(player).GetValueOrDefault(key) : 0;
            int Source(uint name) => name is var key
                ? Game.CharacterStatSources(player).Stats.GetValueOrDefault(key) : 0;
            var source = Assert.Single(Game.GemSkillEffects(player), g => Program.Skills.EffectId(g.Effect) == "AspectOfTheAvian");
            Assert.True(Game.SupportedSkill(player, source.Id).RequirementsMet);
            Assert.False(Program.Skills.IsAura(source.Effect));
            var cost = Game.SupportedReservationCost(player, source.Id)!.Value;
            Assert.Equal(2500, cost.ManaPercent);
            int movementOff = player.Pools.MovementVelocityPct;
            int regenOff = Stat(global::Stat.BaseLifeRegenerationRatePerMinute);
            int manaRegenOff = Stat(global::Stat.BaseManaRegenerationRatePerMinute);
            int addedOff = Stat(global::Stat.SpellMinimumAddedColdDamage);
            var addedBefore = new Dictionary<bool, DamagePacket>
            {
                [false] = DamageStats.RawAdded(DamagePacket.Empty, Stat, spell: false),
                [true] = DamageStats.RawAdded(DamagePacket.Empty, Stat, spell: true),
            };
            Assert.True(await Game.ToggleAura(player,
                source.Id, source.Effect, source.Name, cost, source.Level, source.Quality));
            Assert.Equal(ReservationState.AvianPhase.Might, player.Reservations.Avian);
            Assert.True(player.Area.Buffs.Has(player.Id, 1040, player.Id));
            Assert.Equal(10, Stat(global::Stat.ChanceToDealDoubleDamagePct));
            Assert.Equal(10, Stat(global::Stat.MinionChanceToDealDoubleDamagePct));
            Assert.Equal(1, Stat(global::Stat.HasAviansMight));
            Assert.Equal(0, Stat(global::Stat.HasAviansFlight));
            Assert.Equal(addedOff, Stat(global::Stat.SpellMinimumAddedColdDamage));
            foreach (bool spell in new[] { false, true })
            {
                var added = DamageStats.RawAdded(DamagePacket.Empty, Stat, spell);
                foreach (var (element, type) in new[] { ("cold", DamageType.Cold), ("lightning", DamageType.Lightning) })
                {
                    Assert.Equal(addedBefore[spell][type].Min + Source(global::Stat.Of($"minimum_added_{element}_damage_while_you_have_avians_might")), added[type].Min);
                    Assert.Equal(addedBefore[spell][type].Max + Source(global::Stat.Of($"maximum_added_{element}_damage_while_you_have_avians_might")), added[type].Max);
                }
            }
            Assert.Equal(movementOff, player.Pools.MovementVelocityPct);
            double mightEnd = 100 + (4000 + Source(global::Stat.AviansMightDurationMsPlus)) / 1000d;
            Assert.Equal(mightEnd, player.Reservations.AvianDeadline, 6);
            var golem = new GolemState.Golem(0xA711, null!,
                new(0, "", "Stone", "", 0, [], false, false, false), 1);
            var minionMods = Game.GolemModifiers(player, golem, Game.CharacterPlayerStats(player));
            Assert.Equal(10, minionMods.Effects[global::Stat.ChanceToDealDoubleDamagePct]);
            var hit = DamagePacket.Of(DamageType.Physical, 100, 100);
            Assert.Equal(200, Game.GolemRollHit(hit, minionMods.Effects, false, new AspectRoll(9)).MinTotal);
            Assert.Equal(100, Game.GolemRollHit(hit, minionMods.Effects, true, new AspectRoll(10)).MinTotal);

            var nest = inventory.Add(PlayerInventories.BodyArmour, 0, 0, uniques.Create("Saqawal's Nest", new Random(504))!);
            Game.ReconcileOwnAuras(player, validateSkills: true);
            Assert.Equal(20, Stat(global::Stat.ChanceToDealDoubleDamagePct));
            // The local buff is not scaled by ordinary aura effect.
            Assert.Equal(100, Source(global::Stat.AspectOfTheAvianBuffEffectPlusPct));
            schedule.At = mightEnd - .001;
            await Game.ReservationSkillsTick(player, schedule.At);
            Assert.Equal(ReservationState.AvianPhase.Might, player.Reservations.Avian);
            schedule.At = mightEnd;
            await Game.ReservationSkillsTick(player, schedule.At);
            Assert.Equal(ReservationState.AvianPhase.Flight, player.Reservations.Avian);
            Assert.True(player.Area.Buffs.Has(player.Id, 1041, player.Id));
            Assert.Equal(0, Stat(global::Stat.ChanceToDealDoubleDamagePct));
            Assert.Equal(0, Stat(global::Stat.MinionChanceToDealDoubleDamagePct));
            Assert.Equal(20, Stat(global::Stat.MinionMovementSpeedPlusPct));
            Assert.Equal(0, Stat(global::Stat.HasAviansMight));
            Assert.Equal(1, Stat(global::Stat.HasAviansFlight));
            Assert.Equal(6000 + regenOff, Stat(global::Stat.BaseLifeRegenerationRatePerMinute));
            Assert.Equal(720 + manaRegenOff, Stat(global::Stat.BaseManaRegenerationRatePerMinute));
            Assert.Equal(addedOff, Stat(global::Stat.SpellMinimumAddedColdDamage));
            foreach (bool spell in new[] { false, true })
            {
                var added = DamageStats.RawAdded(DamagePacket.Empty, Stat, spell);
                foreach (var type in new[] { DamageType.Cold, DamageType.Lightning })
                    Assert.Equal(addedBefore[spell][type], added[type]);
            }
            int withoutFlight = Game.PoolsFor(player, 100).MovementVelocityPct - 20;
            Assert.Equal(withoutFlight + 20, player.Pools.MovementVelocityPct);
            minionMods = Game.GolemModifiers(player, golem, Game.CharacterPlayerStats(player));
            Assert.Equal(20, minionMods.Effects.MovementPct);
            Assert.Equal(100, Game.GolemRollHit(hit, minionMods.Effects, false, new AspectRoll(0)).MinTotal);

            var buffs = GameServer.Core.ClientTables.Table("BuffDefinitions");
            foreach (int row in new[] { 1039, 1040, 1041 })
                Assert.False(buffs[row].Bool("IsRecovery"));
            inventory.Add(PlayerInventories.Helmet, 0, 0, uniques.Create("Farrul's Bite", new Random(504))!);
            Game.ReconcileOwnAuras(player, validateSkills: true);
            var cat = Assert.Single(Game.GemSkillEffects(player), g => Program.Skills.EffectId(g.Effect) == "AspectOfTheCat");
            Assert.False(await Game.ToggleAura(player,
                cat.Id, cat.Effect, cat.Name, Game.SupportedReservationCost(player, cat.Id)!.Value, cat.Level, cat.Quality));
            Assert.Single(player.Auras);
            var owner = player.Reservations.AvianOwner;
            schedule.At += 1000000;
            await Game.ReservationSkillsTick(player, schedule.At);
            Assert.Equal(owner, player.Reservations.AvianOwner);
            Assert.True(player.Reservations.AvianDeadline > schedule.At);
            inventory.Remove(PlayerInventories.Boots, talons.Id);
            Game.ReconcileOwnAuras(player, validateSkills: true);
            Game.WithOwnAuraChanges(player, () => []);
            Assert.False(Game.AvianActive(player));
            Assert.Null(player.Reservations.AvianOwner);
            Assert.False(player.Area.Buffs.HasRow(player.Id, 1040) || player.Area.Buffs.HasRow(player.Id, 1041));
            Assert.Empty(player.Auras);
        }
        finally
        {
            player.Closed = true;
            Game.AreaUpdateContext.Value = previousContext;
            PlayerInventories.Forget(character);
        }
    }

    static async Task CrabAspectFixture(Player template, ModuleLifetime lifetime)
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
        var player = new Player(template.Layout, 0xC2AB, 1)
        {
            Character = { Name = character, Id = Guid.NewGuid(), Account = "crab-runtime", League = "Standard", LeagueForm = "Standard", ClassRow = 1, Experience = CharacterStats.ExperienceFor(100) },
            Instance = new(),
            Connection = { PlayerSent = true, PlayerObjectCreated = true, Session = new("crab", wire.Stream, wire.Ciphers) },
            PlayerUpdatesState = updates,
            LeagueModule = template.LeagueModule,
            Pools = new(100, 1, 1, 0, 0),
            PlayerLife = 1
        };
        try
        {
            var inventory = Game.ItemsOf(player);
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
            player.Pools = Game.PoolsFor(player, 100);
            player.PlayerLife = player.Pools.Life;
            player.PlayerMana = player.Pools.Mana;
            Assert.False(Game.IsInactive(player, shell.Id));
            int Stat(uint name) => name is var key
                ? Game.CharacterPlayerStats(player).GetValueOrDefault(key) : 0;
            var source = Assert.Single(Game.GemSkillEffects(player), g => Program.Skills.EffectId(g.Effect) == "AspectOfTheCrab");
            Assert.True(await Game.ToggleAura(player, source.Id, source.Effect,
                source.Name, Game.SupportedReservationCost(player, source.Id)!.Value, source.Level, source.Quality));
            Assert.Equal(15, player.Reservations.CrabMaximum);
            Assert.Equal(0, player.Reservations.CrabBarriers);
            schedule.At = 100.499;
            await Game.ReservationSkillsTick(player, schedule.At);
            Assert.Equal(0, player.Reservations.CrabBarriers);
            schedule.At = 100.5;
            await Game.ReservationSkillsTick(player, schedule.At);
            Assert.Equal(1, player.Reservations.CrabBarriers);
            Assert.Equal(2, Game.PlayerDamageReduction(player).Physical);
            schedule.At = 105;
            await Game.ReservationSkillsTick(player, schedule.At);
            Assert.Equal(10, player.Reservations.CrabBarriers);
            Assert.Equal(20, Game.PlayerDamageReduction(player).Physical);
            Assert.Equal(10, Stat(global::Stat.CurrentCrabAspectStacks));
            Assert.Equal(15, Stat(global::Stat.MaxCrabAspectStacks));
            var stats = Game.CharacterPlayerStats(player);
            Game.CrabPhysicalHitLocked(player, 0, schedule.At, stats);
            Assert.Equal(10, player.Reservations.CrabBarriers);
            // Exercise the actual resource allocator with physical damage absorbed by ES.
            player.EnergyShield.SetMaximum(1000);
            player.EnergyShield.Recover(1000);
            Assert.Equal(0, Game.DamagePlayerHitTyped(player, 10, 0, 0, schedule.At, stats,
                DamagePacket.Of(DamageType.Physical, 10, 10)));
            Assert.Equal(0, player.Reservations.CrabBarriers);
            Assert.Equal(0, Game.PlayerDamageReduction(player).Physical);
            schedule.At = 112.5;
            await Game.ReservationSkillsTick(player, schedule.At);
            Assert.Equal(15, player.Reservations.CrabBarriers);
            Assert.Equal(30, Game.PlayerDamageReduction(player).Physical);
            var chitin = inventory.Add(PlayerInventories.Helmet, 0, 0,
                uniques.Create("Craiceann's Chitin", new Random(504))!);
            // Replace Black Sun through the actual inventory, preserving requirement checks.
            foreach (var old in inventory.Items(PlayerInventories.Helmet).Where(i => i.Id != chitin.Id).ToArray())
                inventory.Remove(PlayerInventories.Helmet, old.Id);
            var tracks = inventory.Add(PlayerInventories.Boots, 0, 0, uniques.Create("Craiceann's Tracks", new Random(504))!);
            Game.ReconcileOwnAuras(player, validateSkills: true);
            Assert.False(Game.IsInactive(player, tracks.Id));
            Assert.False(Game.IsInactive(player, shell.Id));
            Assert.Equal(1, Stat(global::Stat.CannotBeStunned));
            int blockAt15 = player.Pools.BlockChance;
            stats = Game.CharacterPlayerStats(player);
            int loss = Stat(global::Stat.NumberOfCrabChargesLostWhenHit);
            Game.CrabPhysicalHitLocked(player, 1, schedule.At, stats);
            Assert.Equal(15 - loss, player.Reservations.CrabBarriers);
            Game.CrabPhysicalHitLocked(player, 1, schedule.At + 3.999, stats);
            Assert.Equal(15 - loss, player.Reservations.CrabBarriers);
            // No clock advancement here: the next physical hit uses current state but does not accrue future barriers.
            Game.CrabPhysicalHitLocked(player, 1, schedule.At + 4, stats);
            Assert.Equal(Math.Max(0, 15 - 2 * loss), player.Reservations.CrabBarriers);
            Assert.True(Game.PoolsFor(player, 100).BlockChance < blockAt15);
            inventory.Remove(PlayerInventories.BodyArmour, shell.Id);
            Game.ReconcileOwnAuras(player, validateSkills: true);
            Game.WithOwnAuraChanges(player, () => []);
            Assert.False(Game.CrabActive(player));
            Assert.Equal(0, player.Reservations.CrabBarriers);
            Assert.False(player.Area.Buffs.HasRow(player.Id, 1047));
        }
        finally
        {
            player.Closed = true;
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
