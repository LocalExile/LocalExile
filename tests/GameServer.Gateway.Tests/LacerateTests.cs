using GameServer.Core.Accounts;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class LacerateTests
{
    [Fact]
    public async Task Both_contacts_hit_the_overlap_and_only_sand_reaches_the_outer_lobe()
    {
        await AssertWaves(StanceCatalog.BloodSelection, false);
        await AssertWaves(StanceCatalog.SandSelection, true);
    }

    [Fact]
    public Task Dual_wield_uses_the_main_weapon_first_then_the_off_hand() =>
        AssertWaves(StanceCatalog.BloodSelection, false, "Eternal Sword");

    static async Task AssertWaves(string stance, bool wide, string? offHand = null)
    {
        var clock = new WaveClock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.Character.ClassRow = Program.Skills.RowOfClass((int)CharacterClass.Marauder);
        player.ProjectileRuntimeState = fixture.Session;
        player.Walk.Teleport(100, 100, clock.At);
        player.Stance = stance;
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        var hits = new Dictionary<SpawnedMonster, List<(double At, double Damage)>>();
        using var accuracy = GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
        {
            if (ReferenceEquals(owner, player))
            {
                stats[Stat.GlobalAlwaysHit] = 1;
                stats[Stat.GlobalCannotCrit] = 1;
            }
            return stats;
        });
        using var damage = GameEvents.OutgoingDamage.Add("Game", (owner, _, monster, packet) =>
        {
            if (ReferenceEquals(owner, player))
            {
                if (!hits.TryGetValue(monster, out var times)) hits[monster] = times = [];
                times.Add((clock.At, packet.MaxTotal));
            }
            return packet;
        });
        try
        {
            fixture.Equip(("Lacerate", 1));
            var items = Game.ItemsOf(player);
            var host = Assert.Single(items.Items(PlayerInventories.Weapon1));
            items.Remove(PlayerInventories.Weapon1, host.Id);
            items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!) with
            {
                Sockets = host.Item.Sockets, Links = host.Item.Links,
            });
            if (offHand is not null)
                items.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find(offHand)!));
            ushort skill = Assert.Single(Game.GemSkillEffects(player)).Id;

            SpawnedMonster Target(uint id, int x, int y)
            {
                var monster = new SpawnedMonster("Lacerate target", new(x, y, 0), 0, 0, 1_000_000);
                player.Area.Monsters.Add(id, monster);
                player.Area.Grid.Set(new(id), (x, y));
                return monster;
            }

            var overlap = Target(901, 118, 100);
            var firstOnly = Target(902, 115, 109);
            var secondOnly = Target(903, 115, 91);
            var sandOnly = Target(904, 111, 116);
            var behind = Target(905, 94, 100);
            var own = Game.SupportedSkill(player, skill).Stats;
            Assert.Equal(wide ? 0 : 25, own.GetValueOrDefault(Stat.BleedOnHitWithAttacksPct));
            Assert.Equal(wide ? 0 : 50, own.GetValueOrDefault(Stat.ActiveSkillBleedingDamagePlusPctFinal));

            uint mana = player.PlayerMana;
            Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (136, 100)));
            Assert.Equal(mana - 10, player.PlayerMana);
            Assert.Equal(2, hits[overlap].Count);
            var swings = hits[overlap];
            Assert.Equal(swings[0].At, Assert.Single(hits[firstOnly]).At);
            Assert.Equal(swings[1].At, Assert.Single(hits[secondOnly]).At);
            Assert.InRange(swings[1].At - swings[0].At, .29, .53);
            if (offHand is not null) Assert.True(swings[0].Damage * 2 < swings[1].Damage);
            Assert.Equal(wide, hits.ContainsKey(sandOnly));
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
