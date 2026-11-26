using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class CleaveTests
{
    [Fact]
    public async Task Cleave_hits_the_front_and_caps_quality_radius_at_ten_cells()
    {
        await CheckQuality(0);
        await CheckQuality(20);

        static async Task CheckQuality(byte quality)
        {
            var clock = new CleaveClock();
            using var f = new TriggeredSummonTests.Fixture(clock);
            var player = f.Player;
            player.ProjectileRuntimeState = f.Session;
            ushort skill = SetUp(player, "Cleave", quality, null);
            using var accuracy = AlwaysHit(player);
            var previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
            try
            {
                var front = Target(player, 901, 110, 100);
                var side = Target(player, 902, 100, 115);
                var back = Target(player, 903, 91, 100);
                var wider = Target(player, 904, 130, 100);
                var outsideCap = Target(player, 905, 133, 100);
                for (int i = 0; i < 12; i++) Target(player, (uint)(920 + i), 94, 94 + i);
                Assert.True(await Game.CastGemSkill(player, skill, (100, 100), (139, 100)));
                Assert.True(front.Life < front.MaxLife);
                Assert.True(side.Life < side.MaxLife);
                Assert.Equal(back.MaxLife, back.Life);
                Assert.Equal(quality != 0, wider.Life < wider.MaxLife);
                Assert.Equal(outsideCap.MaxLife, outsideCap.Life);
            }
            finally { Game.AreaUpdateContext.Value = previous; }
        }
    }

    [Fact]
    public async Task Cleave_of_rage_uses_the_paid_swing_radius_and_requires_an_axe_and_sword()
    {
        await CheckRage(4, "Rusted Hatchet", true);
        await CheckRage(5, "Rusted Hatchet", true);
        await CheckRage(5, "Copper Sword", false);
        await CheckRage(5, null, false);

        static async Task CheckRage(int rage, string? offhand, bool allowed)
        {
            var clock = new CleaveClock();
            using var f = new TriggeredSummonTests.Fixture(clock);
            var player = f.Player;
            player.ProjectileRuntimeState = f.Session;
            ushort skill = SetUp(player, "Cleave of Rage", 20, offhand);
            using var accuracy = AlwaysHit(player);
            var previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
            try
            {
                var rageInputs = new RageState.Inputs(NoInherentLoss: true);
                player.Rage.Gain(rage, clock.At, rageInputs);
                var front = Target(player, 901, 110, 100);
                var boundary = Target(player, 902, 123, 100);
                uint mana = player.PlayerMana;
                Assert.Equal(allowed, await Game.CastGemSkill(player, skill, (100, 100), (140, 100)));
                Assert.Equal(allowed ? mana - 7 : mana, player.PlayerMana);
                Assert.Equal(allowed, front.Life < front.MaxLife);
                Assert.Equal(allowed && rage >= 5, boundary.Life < boundary.MaxLife);
                Assert.Equal(allowed ? rage + 3 : rage, player.Rage.Read(clock.At, rageInputs).Current);
            }
            finally { Game.AreaUpdateContext.Value = previous; }
        }
    }

    static IDisposable AlwaysHit(Player player) => GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
    {
        if (ReferenceEquals(owner, player))
        {
            stats[Stat.GlobalAlwaysHit] = 1;
            stats[Stat.GlobalCannotCrit] = 1;
            stats[Stat.NoInherentRageLoss] = 1;
        }
        return stats;
    });

    static ushort SetUp(Player player, string name, byte quality, string? offhand)
    {
        player.Character.ClassRow = Program.Skills.RowOfClass((int)CharacterClass.Marauder);
        player.Walk.Teleport(100, 100, 1);
        var items = Game.ItemsOf(player);
        var gear = Assert.Single(items.Items(PlayerInventories.Weapon1));
        var gem = Program.Items.FindGem(name)!.Value;
        items.Remove(PlayerInventories.Weapon1, gear.Id);
        items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!) with
        {
            Sockets = [new ItemSocket(SocketColour.White, ItemCatalog.Create(gem.Type) with
                { GemVariant = gem.Variant.Index, Quality = quality })],
            Links = [1],
        });
        if (offhand is not null)
            items.Add(PlayerInventories.Offhand1, 0, 0, ItemCatalog.Create(Program.Items.Find(offhand)!));
        return Assert.Single(Game.GemSkillEffects(player)).Id;
    }

    static SpawnedMonster Target(Player player, uint id, int x, int y)
    {
        var monster = new SpawnedMonster("Cleave target", new(x, y, 0), 0, 0, 1_000_000);
        player.Area.Monsters.Add(id, monster);
        player.Area.Grid.Set(new(id), (x, y));
        return monster;
    }

    sealed class CleaveClock : IPlayerUpdateSchedule
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
