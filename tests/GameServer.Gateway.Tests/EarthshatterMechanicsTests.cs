using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class EarthshatterMechanicsTests
{
    [Fact]
    public void Fissures_follow_the_native_angle_pool_lengths_and_terrain_stop()
    {
        var fissures = Game.EarthshatterFissures.Plan((336, 1325), (320, 1325), 32769, 536870916,
            detail: 0, repeatsLeft: 0, repeatVariant: 0, baseCount: 5, extraChance: 0, lengthPercent: 0,
            areaScale: 1, _ => true);
        Assert.All(fissures, fissure =>
        {
            Assert.Equal((326, 1325), fissure.Start);
            Assert.Equal(10, fissure.Width);
        });
        Assert.Equal(new[] { (350, 1333, 25), (325, 1348, 23), (309, 1330, 17), (317, 1312, 15), (338, 1309, 20) },
            fissures.Select(fissure => (fissure.End.X, fissure.End.Y, fissure.Length)).ToArray());
        Assert.Equal(0.225, fissures[0].Seconds, 6);

        var prominent = Assert.Single(Game.EarthshatterFissures.Plan((336, 1325), (320, 1325), 32771, 536870916,
            detail: 0, repeatsLeft: 0, repeatVariant: 0, baseCount: 1, extraChance: 0, lengthPercent: 0,
            areaScale: 1, _ => true));
        Assert.Equal((298, 1325), prominent.End);
        var blocked = Assert.Single(Game.EarthshatterFissures.Plan((336, 1325), (320, 1325), 32771, 536870916,
            detail: 0, repeatsLeft: 0, repeatVariant: 0, baseCount: 1, extraChance: 0, lengthPercent: 0,
            areaScale: 1, at => at.X > 308));
        Assert.Equal((309, 1325), blocked.End);
    }

    [Fact]
    public async Task Fissure_contact_is_shared_and_spike_damage_has_its_own_trigger_and_source()
    {
        using (var trial = new Trial("Earthshatter"))
        {
            var fissures = trial.Plan(count: 5);
            var contact = trial.Monster(901, fissures[0].Start);
            var end = fissures.MaxBy(f => f.Length).End;
            var distant = trial.Monster(902, end);
            var outside = trial.Monster(903, (185, 100));

            await trial.Cast(trial.Skill);
            uint atContact = contact.Life;
            Assert.True(atContact < contact.MaxLife);
            Assert.Equal(distant.MaxLife, distant.Life);
            trial.Advance(.30);
            Assert.Equal(atContact, contact.Life);
            Assert.True(distant.Life < distant.MaxLife);
            Assert.Equal(outside.MaxLife, outside.Life);

            var witness = trial.Monster(904, end);
            var ally = trial.Ally();
            await Game.WarcryReleased(ally, (185, 100), 5, ally.ProjectileEpoch);
            trial.Advance(.20);
            Assert.Equal(witness.MaxLife, witness.Life);

            await Game.WarcryReleased(ally, Trial.Origin, 90, ally.ProjectileEpoch);
            trial.Advance(.30);
            uint afterCry = witness.Life;
            Assert.True(afterCry < witness.MaxLife);
            await Game.WarcryReleased(ally, Trial.Origin, 90, ally.ProjectileEpoch);
            trial.Advance(.30);
            Assert.Equal(afterCry, witness.Life);
        }

        using (var trial = new Trial("Earthshatter of Fragility"))
        {
            var fissures = trial.Plan(count: 5);
            await trial.Cast(trial.Skill);
            trial.Advance(.30);
            var witness = trial.Monster(901, fissures[0].End);
            await Game.WarcryReleased(trial.Player, Trial.Origin, 90, trial.Player.ProjectileEpoch);
            trial.Advance(1.15);
            Assert.Equal(witness.MaxLife, witness.Life);
            trial.Advance(.50);
            Assert.True(witness.Life < witness.MaxLife);
        }

        using (var trial = new Trial("Earthshatter of Prominence"))
        {
            var endpoint = Assert.Single(trial.Plan(count: 1)).End;
            await trial.Cast(trial.Skill);
            trial.Advance(.70);
            // This witness is inside the spike's 25-cell radius but outside Ground Slam's cone.
            var witness = trial.Monster(901, (endpoint.X, endpoint.Y + 22));
            await trial.Cast(trial.Slam, (96, 100));
            trial.Advance(.30);
            Assert.True(witness.Life < witness.MaxLife);

            trial.Advance(.70);
            await trial.Cast(trial.Skill);
            trial.Advance(.30);
            var expiring = trial.Monster(902, endpoint);
            trial.Advance(6.10);
            await Game.WarcryReleased(trial.Player, Trial.Origin, 90, trial.Player.ProjectileEpoch);
            trial.Advance(.30);
            Assert.Equal(expiring.MaxLife, expiring.Life);
        }

        using (var exerted = new Trial("Earthshatter of Prominence", "Overexertion Support", "Seismic Cry"))
        {
            var start = Assert.Single(exerted.Plan(count: 1)).Start;
            var plainTarget = exerted.Monster(910, start);
            await exerted.Cast(exerted.Skill);
            uint plainFissure = plainTarget.MaxLife - plainTarget.Life;
            exerted.Advance(.30);
            var plainSpikeTarget = exerted.Monster(911, Assert.Single(exerted.Plan(count: 1)).End);
            await Game.WarcryReleased(exerted.Player, Trial.Origin, 90, exerted.Player.ProjectileEpoch);
            exerted.Advance(.30);
            uint plainSpike = plainSpikeTarget.MaxLife - plainSpikeTarget.Life;
            Assert.True(plainFissure > 0 && plainSpike > 0);

            exerted.Advance(.70);
            var cry = Game.GemSkillEffects(exerted.Player).Single(g => g.Name == "Seismic Cry");
            Game.GrantSeismicCryExertsLocked(exerted.Player, Game.SupportedSkill(exerted.Player, cry.Id).Stats, new Dictionary<uint, int>());
            var exertedTarget = exerted.Monster(912, start);
            await exerted.Cast(exerted.Skill);
            uint exertedFissure = exertedTarget.MaxLife - exertedTarget.Life;
            exerted.Advance(.40);
            var exertedSpikeTarget = exerted.Monster(913, exerted.Plan(count: 1, areaScale: (float)SkillArea.RadiusMultiplier(50)).Single().End);
            await Game.WarcryReleased(exerted.Player, Trial.Origin, 90, exerted.Player.ProjectileEpoch);
            exerted.Advance(.30);
            uint exertedSpike = exertedSpikeTarget.MaxLife - exertedSpikeTarget.Life;
            Assert.InRange(exertedFissure / (double)plainFissure, 1.16, 1.19);
            Assert.InRange(exertedSpike / (double)plainSpike, 1.16, 1.19);
        }
    }

    sealed class Trial : IDisposable
    {
        internal static readonly (int X, int Y) Origin = (100, 100), Aim = (84, 100);
        const ushort Sequence = 0x8001;
        readonly ViperStrikeMambaTests.AttackClock clock = new();
        readonly TriggeredSummonTests.Fixture fixture;
        readonly PlayerUpdatesState? prior;
        readonly IDisposable stats;

        public Player Player => fixture.Player;
        public ushort Skill { get; }
        public ushort Slam { get; }

        internal Trial(string gem, params string[] extraGems)
        {
            fixture = new(clock);
            Player.Use(new(1, "earthshatter-test", 300, 300, Origin.X, Origin.Y, (_, _) => true, [])
                { Sight = (_, _) => 2 });
            Player.Walk.Teleport(Origin.X, Origin.Y, clock.At);
            Player.Connection.LockstepClient = false;
            Player.Connection.PlayerObjectCreated = true;
            Player.ProjectileRuntimeState = fixture.Session;
            var items = Game.ItemsOf(Player);
            var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
            items.Change(PlayerInventories.Weapon1, host.Id,
                _ => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
            stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (ReferenceEquals(owner, Player))
                {
                    values[Stat.GlobalCannotCrit] = values[Stat.GlobalAlwaysHit] = 1;
                    values[Stat.AttackMinimumAddedPhysicalDamage] = values[Stat.AttackMaximumAddedPhysicalDamage] = 100;
                }
                return values;
            });
            fixture.Equip([(gem, 1), ("Ground Slam", 1), .. extraGems.Select(name => (name, 1))]);
            var effects = Game.GemSkillEffects(Player);
            Skill = effects.Single(g => g.Name == gem).Id;
            Slam = effects.Single(g => g.Name == "Ground Slam").Id;
            prior = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            Advance(0);
        }

        internal List<Game.EarthshatterFissures.Fissure> Plan(int count, float areaScale = 1) =>
            Game.EarthshatterFissures.Plan(Origin, Aim, Sequence, Player.Id, 0, 0, 0, count, 0, 0, areaScale, _ => true);

        internal SpawnedMonster Monster(uint id, (int X, int Y) point)
        {
            var monster = new SpawnedMonster("Earthshatter witness", new(point.X, point.Y, 0), 0, 0, 1_000_000)
                { ObjectId = id, Hostile = true, NoRewards = true };
            Player.Area.Monsters[id] = monster;
            Player.Area.Grid.Set(new(id), point);
            return monster;
        }

        internal Player Ally()
        {
            var ally = new Player(Player.Layout, 2, 1)
            {
                Instance = Player.Instance,
                LeagueModule = Player.LeagueModule,
                PlayerLife = 1000,
            };
            ally.Walk.Teleport(Origin.X, Origin.Y, clock.At);
            Assert.Same(Player.Area, ally.Area);
            return ally;
        }

        internal async Task Cast(ushort id, (int X, int Y)? origin = null)
        {
            Player.PlayerMana = 100;
            var from = origin ?? Origin;
            Player.Walk.Teleport(from.X, from.Y, clock.At);
            Assert.True(await Game.CastGemSkill(Player, id, from, Aim, sequence: Sequence));
        }

        internal void Advance(double seconds)
        {
            clock.At += seconds;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        }

        public void Dispose()
        {
            Game.AreaUpdateContext.Value = prior;
            stats.Dispose();
            fixture.Dispose();
        }
    }
}
