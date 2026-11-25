using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class TravellingSlamContactTests
{
    [Theory]
    [InlineData("Ground Slam", false)]
    [InlineData("Volcanic Fissure", true)]
    public async Task Fissure_samples_each_segment_while_ground_slam_keeps_its_targets(string gemName, bool fissure)
    {
        var clock = new ViperStrikeMambaTests.AttackClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        var origin = (X: 100, Y: 100);
        var aim = (X: 160, Y: 100);
        player.Use(new(1, "wave-contact", 400, 400, origin.X, origin.Y, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(origin.X, origin.Y, clock.At);
        player.ProjectileRuntimeState = fixture.Session;
        fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
        var items = Game.ItemsOf(player);
        var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
        items.Change(PlayerInventories.Weapon1, host.Id, _ => ItemCatalog.Create(Program.Items.Find("Driftwood Club")!));
        ushort skill = Assert.Single(fixture.Equip((gemName, 1)));
        var gem = Game.GemSkillEffects(player).Single(g => g.Id == skill);
        using var stats = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (ReferenceEquals(owner, player)) values[Stat.GlobalAlwaysHit] = 1;
            return values;
        });
        const uint life = 100_000;
        SpawnedMonster Monster(uint id, int x, int y)
        {
            var m = new SpawnedMonster("wave witness", new(x, y, 0), 0, 0, life)
                { ObjectId = id, Hostile = true, NoRewards = true };
            player.Area.Monsters[id] = m;
            player.Area.Grid.Set(new(id), (x, y));
            return m;
        }
        var control = Monster(901, 105, 100);
        var leaving = fissure ? Monster(902, 140, 111) : Monster(902, 148, 127);
        var entering = fissure ? Monster(903, 145, 113) : Monster(903, 143, 125);
        var late = Monster(904, 100, 115);
        void Move(SpawnedMonster m, int x, int y)
        {
            m.Walk.Teleport(x, y, clock.At);
            player.Area.Grid.Set(new(m.ObjectId), (x, y));
        }
        var contacts = new List<uint>();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            await Game.AreaSkillHit(player, skill, gem, origin, aim, sequence: 0x8002, onHit: id =>
            {
                contacts.Add(id);
                if (id == (fissure ? entering : leaving).ObjectId) Move(late, 100, 100);
                if (id != control.ObjectId || contacts.Count != 1) return;
                if (fissure)
                {
                    Move(leaving, 140, 113);
                    Move(entering, 145, 111);
                }
                else
                {
                    Move(leaving, 148, 129);
                    Move(entering, 143, 123);
                }
            });
            Assert.Equal([control.ObjectId, (fissure ? entering : leaving).ObjectId], contacts);
            Assert.True(control.Life < life);
            Assert.Equal((!fissure, fissure), (leaving.Life < life, entering.Life < life));
            Assert.Equal(life, late.Life);
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }
}
