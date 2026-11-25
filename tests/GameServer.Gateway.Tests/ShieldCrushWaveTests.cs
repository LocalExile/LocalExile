using GameServer.Core.Accounts;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ShieldCrushWaveTests
{
    [Theory]
    [InlineData("Shield Crush", CharacterClass.Marauder, -1)]
    [InlineData("Shield Crush of the Chieftain", CharacterClass.Templar, 1)]
    public async Task Shield_crush_hits_overlap_twice_and_only_the_centre_reaches_farther(string name,
        CharacterClass characterClass, int lastSideDirection)
    {
        var clock = new WaveClock();
        using var f = new TriggeredSummonTests.Fixture(clock);
        var player = f.Player;
        player.Character.ClassRow = Program.Skills.RowOfClass((int)characterClass);
        player.ProjectileRuntimeState = f.Session;
        player.Walk.Teleport(100, 100, clock.At);
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
        var hits = new Dictionary<SpawnedMonster, List<double>>();
        using var accuracy = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
        {
            if (!ReferenceEquals(owner, player)) return stats;
            stats[Stat.GlobalAlwaysHit] = 1;
            stats[Stat.AttackMinimumAddedFireDamage] = 20;
            stats[Stat.AttackMaximumAddedFireDamage] = 20;
            return stats;
        });
        using var damage = GameEvents.OutgoingDamage.Add("Game", (owner, _, monster, packet) =>
        {
            if (ReferenceEquals(owner, player))
            {
                if (!hits.TryGetValue(monster, out var times)) hits[monster] = times = [];
                times.Add(clock.At);
            }
            return packet;
        });
        try
        {
            f.Equip((name, 1));
            var items = Game.ItemsOf(player);
            var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
            items.Remove(PlayerInventories.Weapon1, host.Id);
            var shield = ItemCatalog.Create(Program.Items.Find("Splintered Tower Shield")!) with
            {
                Sockets = host.Item.Sockets, Links = host.Item.Links
            };
            items.Add(PlayerInventories.Offhand1, 0, 0, shield);
            ushort skill = Assert.Single(Game.GemSkillEffects(player)).Id;

            SpawnedMonster Target(uint id, int x, int y)
            {
                var monster = new SpawnedMonster("Shield Crush target", new(x, y, 0), 0, 0, 1_000_000);
                player.Area.Monsters.Add(id, monster);
                player.Area.Grid.Set(new(id), (x, y));
                return monster;
            }

            var overlap = Target(901, 108, 100);
            var centreOnly = Target(902, 130, 100);
            var behind = Target(903, 92, 100);
            var lastSide = Target(904, 104, 100 + lastSideDirection * 12);
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (108, 100)));
            Assert.Equal(2, hits[overlap].Count);
            Assert.Equal(hits[overlap][1], Assert.Single(hits[centreOnly]));
            Assert.True(hits[overlap][0] < hits[overlap][1]);
            Assert.True(hits[overlap][1] < Assert.Single(hits[lastSide]));
            Assert.True(overlap.Life < overlap.MaxLife);
            Assert.True(centreOnly.Life < centreOnly.MaxLife);
            Assert.False(hits.ContainsKey(behind));
        }
        finally
        {
            Game.AreaUpdateContext.Value = previous;
        }
    }

    sealed class WaveClock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            At += milliseconds / 1000d;
            return Task.CompletedTask;
        }
    }
}
