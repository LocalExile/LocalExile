using GameServer.Core.World;
using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed partial class ReservationCurseSummonTests
{
    static async Task SpiderAspectFixture(ClientState template, ModuleLifetime lifetime)
    {
        string character = "SpiderRuntime" + Guid.NewGuid().ToString("N");
        PlayerInventories.SetClass(character, 1);
        CharacterSaves.Write(character, new(new Dictionary<byte, List<CharacterSaves.SavedPlacement>>(),
            Experience: CharacterStats.ExperienceFor(100)));
        var schedule = new CatClockSchedule { At = 100 };
        var updates = new PlayerUpdatesState(lifetime, schedule);
        var previousContext = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = updates;
        using var wire = new Wire();
        var conn = new ClientState(template.Layout, 0x5B10, 1)
        {
            Character = character, CharacterId = Guid.NewGuid(), Account = "spider-runtime",
            League = "Standard", LeagueForm = "Standard", ClassRow = 1, Instance = new(),
            PlayerSent = true, PlayerObjectCreated = true, PlayerUpdatesState = updates,
            Experience = CharacterStats.ExperienceFor(100), LeagueModule = template.LeagueModule,
            Wire = ("spider", wire.Stream, wire.Ciphers), Pools = new(100, 1, 1, 0, 0), PlayerLife = 1,
        };
        try
        {
            var inventory = Game.ItemsOf(conn);
            foreach (byte row in new byte[] { PlayerInventories.Weapon1, PlayerInventories.Offhand1, PlayerInventories.Helmet,
                         PlayerInventories.BodyArmour, PlayerInventories.Boots, PlayerInventories.Gloves, 5, 6, 7, 10 })
                foreach (var old in inventory.Items(row)) inventory.Remove(row, old.Id);
            var uniques = new UniqueCatalog(Program.Items);
            inventory.Add(5, 0, 0, uniques.Create("Astramentis", new Random(504))!);
            inventory.Add(6, 0, 0, uniques.Create("Le Heup of All", new Random(504))!);
            inventory.Add(7, 0, 0, uniques.Create("Le Heup of All", new Random(505))!);
            inventory.Add(PlayerInventories.Helmet, 0, 0, uniques.Create("Fenumus' Toxins", new Random(504))!);
            inventory.Add(PlayerInventories.Boots, 0, 0, uniques.Create("Fenumus' Spinnerets", new Random(504))!);
            var shroud = inventory.Add(PlayerInventories.BodyArmour, 0, 0, uniques.Create("Fenumus' Shroud", new Random(504))!);
            var weave = inventory.Add(PlayerInventories.Gloves, 0, 0, uniques.Create("Fenumus' Weave", new Random(504))!);
            conn.Pools = Game.PoolsFor(conn, 100);
            conn.PlayerLife = conn.Pools.Life;
            conn.PlayerMana = conn.Pools.Mana;
            conn.EnergyShield.SetMaximum(conn.Pools.EnergyShield);
            conn.EnergyShield.Empty();
            conn.Walk.Teleport(100, 100, schedule.At);
            var target = new SpawnedMonster("spider target", new(101, 100, 1), 100, 8, 10000);
            var distant = new SpawnedMonster("spider distant", new(300, 100, 1), 100, 8, 10000)
                { Mods = MonsterModSet.None with { Effects = new(new Dictionary<string, int> { ["damage_+%"] = 50 }) } };
            conn.Monsters[0x5B11] = target;
            conn.Monsters[0x5B12] = distant;
            int idlePackets = conn.Minions.OutputQueue.Count;
            Game.SpiderAspectTickLocked(conn, schedule.At);
            Assert.Equal(idlePackets, conn.Minions.OutputQueue.Count);
            Assert.False(Game.IsInactive(conn, shroud.Id));
            var source = Assert.Single(Game.GemSkillEffects(conn), g => Program.Skills.EffectId(g.Effect) == "AspectOfTheSpider");
            Assert.Equal(1042, AuraCatalog.BuffRow("AspectOfTheSpider"));
            var cost = Game.SupportedReservationCost(conn, source.Id)!.Value;
            Assert.Equal(2500, cost.ManaPercent);
            Assert.True(await Game.ToggleAura("spider", wire.Stream, wire.Ciphers, conn, source.Id, source.Effect,
                source.Name, cost, source.Level, source.Quality));
            Assert.Equal(100.5, conn.Reservations.NextSpiderWeb, 6);
            schedule.At = 100.499;
            Game.SpiderAspectTickLocked(conn, schedule.At);
            Assert.Equal(0, target.SpiderWebs.Count(schedule.At));
            schedule.At = 100.5;
            Game.SpiderAspectTickLocked(conn, schedule.At);
            Assert.Equal(1, target.SpiderWebs.Count(schedule.At));
            Assert.Equal(0, target.SpiderWebs.Taken(schedule.At - .001));
            Assert.Equal(0, Game.SpiderHinder(target, schedule.At - .001));
            Assert.Equal(5, target.SpiderWebs.Taken(schedule.At));
            Assert.Equal(-30, Game.MaimedMovementFactor(target, schedule.At).Pct);
            Assert.Equal(.7, Game.MaimedMovementFactor(target, schedule.At).Factor, 6);
            Assert.NotEqual(0, target.SpiderWebs.PresentedBuff);
            Assert.NotEqual(0, target.SpiderWebs.HinderBuff);
            Assert.NotEqual(0, target.SpiderWebs.ShroudBuff);
            for (int i = 2; i <= 6; i++)
            { schedule.At = 100 + i * .5; Game.SpiderAspectTickLocked(conn, schedule.At); }
            Assert.Equal(4, target.SpiderWebs.Count(schedule.At));
            Assert.Equal(0, distant.SpiderWebs.Count(schedule.At));
            Assert.Empty(target.Curses.All);
            Assert.Equal(20, target.SpiderWebs.Taken(schedule.At));
            Assert.Equal(-10, Game.MonsterHitResistance(target, DamageType.Chaos, default, schedule.At).Value);
            Assert.Equal(120, Game.MonsterTakesRolled(target, DamagePacket.Of(DamageType.Physical, 100, 100), false, now: schedule.At));
            Assert.Equal(120, Game.MonsterImpaleDamage(target, 100, schedule.At));
            Assert.Equal(132, Game.MonsterTakesRolled(target, DamagePacket.Of(DamageType.Chaos, 100, 100), false, now: schedule.At));
            Assert.Equal(1.32, Game.MonsterDotTaken(target, DamageType.Chaos, schedule.At), 6);
            Assert.Equal(.9, Game.SpiderOutgoingFactor(target, schedule.At), 6);
            Assert.Equal(90, Game.MonsterModHit(target, DamagePacket.Of(DamageType.Physical, 100, 100),
                100, 100, new AspectRoll(99), out _, now: schedule.At).MinTotal);
            target.MaimedUntil = schedule.At + 1;
            Assert.Equal(-60, Game.MaimedMovementFactor(target, schedule.At).Pct);
            target.MaimedUntil = 0;
            int gain = Game.CharacterPlayerStats(conn)[Program.Stats.Key("energy_shield_gain_when_you_hit_enemy_affected_by_spiders_web")!.Value];
            Assert.InRange(gain, 15, 20);
            var previousRuntime = conn.ProjectileRuntimeState;
            var hitSession = new Game.ProjectileSession("spider-contact-clock", wire.Stream, wire.Ciphers, conn);
            try
            {
                conn.ProjectileRuntime = hitSession;
                hitSession.Runtime.AdvanceTo(ProjectileRuntime.Time(schedule.At));
                Assert.Equal(schedule.At, Game.ProjectileWorldTime(conn), 6);
                Assert.NotNull(Game.ApplyMonsterHitLocked(conn, target, 1));
                Assert.Equal((uint)gain, conn.EnergyShield.Current);
                Assert.NotNull(Game.ApplyMonsterHitLocked(conn, distant, 1));
                Assert.Equal((uint)gain, conn.EnergyShield.Current);
            }
            finally
            {
                conn.ProjectileRuntimeState = previousRuntime;
                hitSession.Invalidate();
                await hitSession.OutputCompleted;
            }
            double earnedUntil = target.SpiderWebs.Until(schedule.At);
            inventory.Remove(PlayerInventories.Gloves, weave.Id);
            Game.ReconcileOwnAuras(conn, validateSkills: true);
            Game.SpiderAspectTickLocked(conn, schedule.At);
            Assert.Null(conn.Reservations.SpiderOwner);
            Assert.Equal(4, target.SpiderWebs.Count(schedule.At));
            Assert.Equal(earnedUntil, target.SpiderWebs.Until(schedule.At));
            schedule.At = earnedUntil;
            Game.SpiderAspectTickLocked(conn, schedule.At);
            Assert.Equal(0, target.SpiderWebs.Count(schedule.At));
            Assert.Equal(0, target.SpiderWebs.PresentedBuff);
            Assert.Equal(0, target.SpiderWebs.HinderBuff);
            Assert.Equal(0, target.SpiderWebs.ShroudBuff);
            Assert.Equal(100, Game.MonsterTakesRolled(target, DamagePacket.Of(DamageType.Chaos, 100, 100), false, now: schedule.At));
            Assert.Equal(0, Game.MaimedMovementFactor(target, schedule.At).Pct);
            Assert.Empty(conn.PlayerStats);
        }
        finally
        {
            conn.Closed = true;
            Game.AreaUpdateContext.Value = previousContext;
            PlayerInventories.Forget(character);
        }
    }
}
