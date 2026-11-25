using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class ReaveTests
{
    [Fact]
    public async Task Reave_gains_one_stage_per_sweep_and_loses_stages_after_missing()
    {
        var clock = new ReaveClock();
        using var f = new TriggeredSummonTests.Fixture(clock);
        var player = f.Player;
        var skill = Equip(f, "Reave");
        using var accuracy = AlwaysHit(player);
        var old = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
        try
        {
            var near = Target(player, 901, 108, 100);
            var side = Target(player, 902, 109, 106);
            var nextStage = Target(player, 903, 126, 100);
            Assert.True(await Game.CastGemSkill(player, skill.Id, (100, 100), (140, 100)));
            clock.Step(player);
            Assert.True(near.Life < near.MaxLife);
            Assert.True(side.Life < side.MaxLife);
            Assert.Equal(nextStage.MaxLife, nextStage.Life);
            Assert.Equal(1, Game.ReaveStages(player));

            for (int i = 0; i < 5; ++i)
            {
                clock.Advance(player, .4);
                Assert.True(await Game.CastGemSkill(player, skill.Id, (100, 100), (140, 100)));
                clock.Step(player);
            }
            Assert.True(nextStage.Life < nextStage.MaxLife);
            Assert.Equal(4, Game.ReaveStages(player));
            Assert.True(player.Area.Buffs.HasRow(player.Id, 222));

            clock.Advance(player, .5);
            Assert.True(await Game.CastGemSkill(player, skill.Id, (100, 100), (60, 100)));
            clock.Step(player);
            Assert.Equal(4, Game.ReaveStages(player));
            clock.Advance(player, 3.1);
            Assert.Equal(3, Game.ReaveStages(player));
            for (int i = 0; i < 3; ++i) clock.Advance(player, 1.1);
            Assert.Equal(0, Game.ReaveStages(player));
            Assert.False(player.Area.Buffs.HasRow(player.Id, 222));
        }
        finally { Game.AreaUpdateContext.Value = old; }
    }

    [Fact]
    public async Task Vaal_Reave_spends_once_and_sweeps_behind_the_original_aim_on_a_repeat()
    {
        var clock = new ReaveClock();
        using var f = new TriggeredSummonTests.Fixture(clock);
        var player = f.Player;
        var skill = Equip(f, "Vaal Reave", vaal: true);
        using var accuracy = AlwaysHit(player);
        var old = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(f.Lifetime, clock);
        try
        {
            var ahead = Target(player, 901, 110, 100);
            var behind = Target(player, 902, 90, 100);
            Game.RefreshVaalSouls(player);
            var definition = Assert.NotNull(Program.Skills.VaalBudget(skill.Effect, skill.Level));
            for (int i = 0; i < definition.BaseCost; ++i) Assert.True(player.VaalSouls.Gain());
            Assert.True(Game.TryBeginSkillCooldownUse(player, skill.Id, skill.Effect,
                Game.CooldownForSkill(player, skill.Id), instant: false, () => { }, out var use));
            Assert.True(await Game.CastGemSkill(player, skill.Id, (100, 100), (140, 100), cooldownUse: use));
            clock.Step(player);
            Assert.True(ahead.Life < ahead.MaxLife);
            Assert.True(behind.Life < behind.MaxLife);
            Assert.Equal(0u, player.VaalSouls.Read().Single().Current);
            Assert.InRange(Game.ReaveStages(player), 5, 8);
        }
        finally { Game.AreaUpdateContext.Value = old; }
    }

    static (ushort Id, int Effect, int Level) Equip(TriggeredSummonTests.Fixture f, string name, bool vaal = false)
    {
        var player = f.Player;
        player.Character.ClassRow = Program.Skills.RowOfClass((int)CharacterClass.Marauder);
        player.Walk.Teleport(100, 100, 1);
        player.Area.Entities.Add(new(player.Id), EntityKind.Player);
        var items = Game.ItemsOf(player);
        items.Remove(PlayerInventories.Weapon1, Assert.Single(items.Items(PlayerInventories.Weapon1)).Id);
        var gem = Program.Items.FindGem(name)!.Value;
        items.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find("Rusted Sword")!) with
        {
            Sockets = [new ItemSocket(SocketColour.White, ItemCatalog.Create(gem.Type) with
                { GemVariant = gem.Variant.Index, Corrupted = vaal })],
            Links = [1],
        });
        var skill = Assert.Single(Game.GemSkillEffects(player),
            g => Program.Skills.EffectId(g.Effect) == (vaal ? "VaalReave" : "Reave"));
        player.ProjectileRuntimeState = f.Session;
        return (skill.Id, skill.Effect, skill.Level);
    }

    static IDisposable AlwaysHit(Player player) => GameEvents.PlayerStatSources.Add("Game", (owner, stats) =>
    {
        if (ReferenceEquals(owner, player)) stats[Stat.GlobalAlwaysHit] = 1;
        return stats;
    });

    static SpawnedMonster Target(Player player, uint id, int x, int y)
    {
        var monster = new SpawnedMonster("Reave target", new(x, y, 0), 0, 0, 1_000_000);
        player.Area.Monsters.Add(id, monster);
        player.Area.Grid.Set(new(id), (x, y));
        return monster;
    }

    sealed class ReaveClock : IPlayerUpdateSchedule
    {
        double at = 1;
        public double Now(Player player) => at;
        public Task Wait(int milliseconds, CancellationToken stopping)
        {
            stopping.ThrowIfCancellationRequested();
            at += milliseconds / 1000d;
            return Task.CompletedTask;
        }
        public void Step(Player player) => player.Area.Step((long)(at * 1000 / Program.FrameMs), at);
        public void Advance(Player player, double seconds)
        {
            at += seconds;
            Step(player);
        }
    }
}
