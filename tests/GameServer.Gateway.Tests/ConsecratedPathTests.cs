using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class ConsecratedPathTests
{
    [Fact]
    public async Task Ordinary_path_slams_in_place_or_warps_to_an_eligible_enemy_at_contact()
    {
        using var trial = new Trial("Consecrated Path");
        await trial.Cast((139, 100));
        Assert.Equal(Trial.Origin, trial.Position);
        trial.AssertGround((107, 100));

        trial.Advance();
        var nearby = trial.AddMonster(901, (112, 100));
        await trial.Cast((112, 100), nearby.ObjectId);
        Assert.Equal(Trial.Origin, trial.Position);
        Assert.True(nearby.Life < nearby.MaxLife);
        trial.AssertGround((107, 100));

        trial.Advance();
        var enemy = trial.AddMonster(902, (127, 100));
        var decoy = trial.AddMonster(903, (130, 100), false);
        await trial.Cast((130, 100));
        Assert.Equal((120, 100), trial.Position);
        Assert.True(enemy.Life < enemy.MaxLife);
        Assert.Equal(decoy.MaxLife, decoy.Life);
        trial.AssertGround((127, 100));

        trial.Advance();
        trial.BlockedX = 114;
        await trial.Cast((127, 100), enemy.ObjectId);
        Assert.Equal(Trial.Origin, trial.Position);
        trial.AssertGround((107, 100));
    }

    [Fact]
    public async Task Endurance_bypass_requires_payment_and_uses_the_same_contact()
    {
        using var trial = new Trial("Consecrated Path of Endurance");
        var player = trial.Player;
        var skill = Game.SupportedSkill(player, trial.Skill);
        var plan = Game.CooldownForSkill(player, trial.Skill);
        Assert.NotNull(plan);
        var charges = Game.ChargesOf(player);
        charges.State.Set(ChargeType.Endurance, 2, Game.AreaTime(player), charges.Limits(ChargeType.Endurance));
        var enemy = trial.AddMonster(901, (127, 100));

        bool Admit(out CooldownUseTicket? ticket) =>
            Game.TryBeginSkillCooldownUse(player, trial.Skill, skill.Effect, plan, false, () => { }, out ticket);

        Assert.True(Admit(out var first));
        await trial.Cast((127, 100), enemy.ObjectId, first);
        Assert.Equal((120, 100), trial.Position);
        Assert.Equal(2, charges.Effective(ChargeType.Endurance));
        trial.AssertGround((127, 100));

        trial.Advance();
        player.PlayerMana = 0;
        Assert.False(Admit(out _));
        Assert.Equal(2, charges.Effective(ChargeType.Endurance));
        player.PlayerMana = 100;
        Assert.True(Admit(out var bypass));
        Assert.Equal(1, charges.Effective(ChargeType.Endurance));
        uint previousLife = enemy.Life;
        await trial.Cast((127, 100), enemy.ObjectId, bypass);
        Assert.Equal((120, 100), trial.Position);
        Assert.True(enemy.Life < previousLife);
        trial.AssertGround((127, 100));
    }

    sealed class Trial : IDisposable
    {
        internal static readonly (int X, int Y) Origin = (100, 100);
        readonly ViperStrikeMambaTests.AttackClock clock = new();
        readonly TriggeredSummonTests.Fixture fixture;
        readonly PlayerUpdatesState? previous;
        readonly IDisposable stats;

        public Player Player => fixture.Player;
        public ushort Skill { get; }
        public (int X, int Y) Position => Player.Walk.Position(clock.At);
        public int BlockedX { get; set; } = -1;

        public Trial(string name)
        {
            fixture = new(clock);
            Player.Use(new(1, "holy-path-test", 400, 400, Origin.X, Origin.Y, (x, _) => x != BlockedX, [])
                { Sight = (x, _) => (byte)(x == BlockedX ? 0 : 2) });
            Player.Walk.Teleport(Origin.X, Origin.Y, clock.At);
            Player.Connection.LockstepClient = false;
            Player.Connection.PlayerObjectCreated = true;
            Player.ProjectileRuntimeState = fixture.Session;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
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
            Skill = Assert.Single(fixture.Equip((name, 1)));
            previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
            Player.PlayerMana = 100;
        }

        public SpawnedMonster AddMonster(uint id, (int X, int Y) point, bool hostile = true)
        {
            var monster = new SpawnedMonster("Holy Path witness", new(point.X, point.Y, 0), 0, 0, 1_000_000)
                { ObjectId = id, Hostile = hostile, NoRewards = true };
            Player.Area.Monsters[id] = monster;
            Player.Area.Grid.Set(new(id), point);
            return monster;
        }

        public async Task Cast((int X, int Y) aim, uint targetId = 0, CooldownUseTicket? cooldown = null)
        {
            Player.Walk.Teleport(Origin.X, Origin.Y, clock.At);
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            Player.PlayerMana = 100;
            Assert.True(await Game.CastGemSkill(Player, Skill, Origin, aim, targetId, cooldownUse: cooldown));
        }

        public void Advance() => clock.At += .45;

        public void AssertGround((int X, int Y) at)
        {
            var ground = Player.FlaskGrounds.Grounds[^1];
            Assert.Equal(FlaskGrounds.Kind.ConsecratedGround, ground.Kind);
            Assert.Equal(at, (ground.X, ground.Y));
            Assert.Equal(15, ground.Radius);
            Assert.InRange(ground.Ends - clock.At, 3.95, 4.05);
        }

        public void Dispose()
        {
            Game.AreaUpdateContext.Value = previous;
            stats.Dispose();
            fixture.Dispose();
        }
    }
}
