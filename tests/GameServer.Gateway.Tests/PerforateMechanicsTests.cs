using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class PerforateMechanicsTests
{
    [Fact]
    public void Seeded_steps_match_blood_and_sand_native_geometry()
    {
        var from = (336, 1325);
        var aim = (313, 1325);
        const uint owner = 536870914;
        var blood = Game.PerforateSteps(from, aim, 11, 7, sand: false, bloodshed: false, 97, 50, 32769, owner);
        Assert.Equal(new[] { (325, 1325), (326, 1327), (323, 1321), (324, 1331), (333, 1327), (318, 1317), (312, 1324) },
            blood.Select(step => step.Centre));
        Assert.All(blood, step => Assert.Equal(7, step.Radius));

        var sand = Game.PerforateSteps(from, aim, 11, 7, sand: true, bloodshed: false, 104, 50, 32773, owner);
        Assert.Equal(8, sand.Length);
        Assert.Null(sand[0].LineStart);
        Assert.Equal(new (int, int)?[] { (333, 1325), (330, 1331), (330, 1319), (323, 1333),
            (323, 1317), (318, 1328), (318, 1321) }, sand.Skip(1).Select(step => step.LineStart));
        Assert.Equal(new (int, int)?[] { (353, 1325), (342, 1347), (343, 1303), (318, 1352),
            (319, 1297), (300, 1337), (300, 1312) }, sand.Skip(1).Select(step => step.LineEnd));
        Assert.All(sand.Skip(1), step => Assert.Equal(9, step.LineHalfWidth));

        var bloodshed = Game.PerforateSteps(from, aim, 11, 6, sand: true, bloodshed: true, 104, 50, 32774, owner);
        Assert.Equal(new[] { (325, 1325), (326, 1320), (321, 1316), (331, 1339), (305, 1322), (300, 1323) },
            bloodshed.Select(step => step.Centre));
        Assert.All(bloodshed, step => { Assert.Null(step.LineStart); Assert.Equal(16, step.Radius); });
    }

    [Fact]
    public async Task Blood_spikes_repeat_damage_and_sand_lines_only_hit_each_target_once()
    {
        using (var blood = new Trial("Perforate"))
        {
            var central = blood.Monster(901, (89, 100));
            var outside = blood.Monster(902, (145, 130));

            await blood.Cast();
            blood.Advance(.03);
            uint afterFirst = central.Life;
            Assert.True(afterFirst < central.MaxLife);
            blood.Advance(.40);
            Assert.True(central.Life < afterFirst);
            Assert.Equal(outside.MaxLife, outside.Life);
        }

        using (var sand = new Trial("Perforate", sand: true))
        {
            var central = sand.Monster(901, (89, 100));
            var steps = Game.PerforateSteps(Trial.Origin, Trial.Aim, 11, 7, sand: true, bloodshed: false, 100, 50,
                Trial.Sequence, sand.Player.Id);
            var endpoint = steps[1].LineEnd!.Value;
            var distant = sand.Monster(902, endpoint);
            await sand.Cast();
            sand.Advance(.01);
            uint afterFirst = central.Life;
            Assert.True(afterFirst < central.MaxLife);
            Assert.Equal(distant.MaxLife, distant.Life);
            sand.Advance(.20);
            Assert.Equal(afterFirst, central.Life);
            Assert.True(distant.Life < distant.MaxLife);
        }

        using (var bloodshed = new Trial("Perforate of Bloodshed", sand: true))
        {
            var central = bloodshed.Monster(901, (89, 100));
            await bloodshed.Cast();
            bloodshed.Advance(.01);
            uint first = central.Life;
            Assert.True(first < central.MaxLife);
            bloodshed.Advance(.06);
            Assert.Equal(first, central.Life);
            bloodshed.Advance(.06);
            Assert.True(central.Life < first);
        }

        using (var exerted = new Trial("Perforate", sand: true, "Overexertion Support", "Seismic Cry"))
        {
            var plainTarget = exerted.Monster(910, (89, 100));
            await exerted.Cast();
            exerted.Advance(.01);
            uint plainDamage = plainTarget.MaxLife - plainTarget.Life;
            Assert.True(plainDamage > 0);
            exerted.Advance(.50);

            var cry = Game.GemSkillEffects(exerted.Player).Single(g => g.Name == "Seismic Cry");
            Game.GrantSeismicCryExertsLocked(exerted.Player, Game.SupportedSkill(exerted.Player, cry.Id).Stats, new Dictionary<uint, int>());
            exerted.Player.NextAttackAt = 0;
            var exertedTarget = exerted.Monster(911, (89, 100));
            await exerted.Cast();
            exerted.Advance(.01);
            uint exertedDamage = exertedTarget.MaxLife - exertedTarget.Life;
            Assert.InRange(exertedDamage / (double)plainDamage, 1.16, 1.19);
        }
    }

    sealed class Trial : IDisposable
    {
        internal static readonly (int X, int Y) Origin = (100, 100), Aim = (84, 100);
        internal const ushort Sequence = 0x8001;
        readonly ViperStrikeMambaTests.AttackClock clock = new();
        readonly TriggeredSummonTests.Fixture fixture;
        readonly PlayerUpdatesState? prior;
        readonly IDisposable stats;
        readonly ushort skill;

        internal Player Player => fixture.Player;

        internal Trial(string gem, bool sand = false, params string[] extraGems)
        {
            fixture = new(clock);
            Player.Use(new(1, "perforate-test", 300, 300, Origin.X, Origin.Y, (_, _) => true, []) { Sight = (_, _) => 2 });
            Player.Walk.Teleport(Origin.X, Origin.Y, clock.At);
            Player.Connection.LockstepClient = false;
            Player.Connection.PlayerObjectCreated = true;
            Player.ProjectileRuntimeState = fixture.Session;
            Player.Stance = sand ? StanceCatalog.SandSelection : StanceCatalog.BloodSelection;
            var items = Game.ItemsOf(Player);
            var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
            items.Change(PlayerInventories.Weapon1, host.Id, _ => ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
            stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
            {
                if (ReferenceEquals(owner, Player))
                {
                    values[Stat.GlobalCannotCrit] = values[Stat.GlobalAlwaysHit] = 1;
                    values[Stat.AttackMinimumAddedPhysicalDamage] = values[Stat.AttackMaximumAddedPhysicalDamage] = 100;
                }
                return values;
            });
            fixture.Equip([(gem, 1), .. extraGems.Select(name => (name, 1))]);
            skill = Game.GemSkillEffects(Player).Single(g => g.Name == gem).Id;
            prior = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            Advance(0);
        }

        internal SpawnedMonster Monster(uint id, (int X, int Y) at)
        {
            var monster = new SpawnedMonster("Perforate witness", new(at.X, at.Y, 0), 0, 0, 1_000_000)
                { ObjectId = id, Hostile = true, NoRewards = true };
            Player.Area.Monsters[id] = monster;
            Player.Area.Grid.Set(new(id), at);
            return monster;
        }

        internal async Task Cast()
        {
            Player.PlayerMana = 100;
            Assert.True(await Game.CastGemSkill(Player, skill, Origin, Aim, sequence: Sequence));
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
