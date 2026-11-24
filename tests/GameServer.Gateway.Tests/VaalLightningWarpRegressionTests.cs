using GameServer.Core.Projectiles;

namespace GameServer.Gateway.Tests;

public sealed class VaalLightningWarpRegressionTests
{
    [Fact]
    public async Task Paid_warp_commits_both_enemy_hits_and_teleport_with_output_held()
    {
        var clock = new Clock();
        using var fixture = new TriggeredSummonTests.Fixture(clock);
        var player = fixture.Player;
        player.Use(new(1, "vaal-warp-regression", 350, 350, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
        player.Walk.Teleport(100, 100, clock.At);
        player.Connection.PlayerObjectCreated = true;
        player.ProjectileRuntimeState = fixture.Session;
        var items = Game.ItemsOf(player);
        items.Change(PlayerInventories.Weapon1, Assert.Single(items.Items(PlayerInventories.Weapon1)).Id,
            _ => ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!, random: new(21)));
        fixture.Equip(("Vaal Lightning Warp", 1), ("Vaal Temptation Support", 1),
            ("Increased Area of Effect Support", 1), ("Less Duration Support", 1));
        var skill = Assert.Single(Game.GemSkillEffects(player), row => row.Name == "Vaal Lightning Warp"
            && Program.Skills.VaalBudget(row.Effect, row.Level) is not null);
        var supported = Game.SupportedSkill(player, skill.Id);
        Assert.True(supported.RequirementsMet);
        Assert.Equal(3, supported.Supports.Count);
        Assert.Equal("VaalLightningWarpInstant", Game.SkillsFor(player).EffectId(skill.Effect));

        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        using var stat = GameEvents.PlayerStatSources.Add("Game", (owner, values) =>
        {
            if (!ReferenceEquals(owner, player)) return values;
            values[Stat.GlobalCannotCrit] = 1;
            values[Stat.SpellMinimumAddedLightningDamage] = values[Stat.SpellMaximumAddedLightningDamage] = 400;
            return values;
        });
        try
        {
            player.Pools = Game.BuildPoolsFor(player, 100);
            player.PlayerLife = player.PlayerMaxLife;
            player.ApplyMovementSpeed();
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
            var enemy = At(711, 106, 100);
            var distant = At(712, 200, 200);
            var contacts = new List<(int X, int Y)>();
            using var hits = GameEvents.OutgoingDamage.Add("Game", (p, _, monster, packet) =>
            {
                if (ReferenceEquals(p, player) && ReferenceEquals(monster, enemy))
                    contacts.Add(enemy.Walk.Position(Game.ProjectileWorldTime(player)));
                return packet;
            });
            Game.RefreshVaalSouls(player);
            var budget = Assert.NotNull(Program.Skills.VaalBudget(skill.Effect, supported.Level));
            Assert.Equal(20, (int)budget.BaseCost);
            for (int i = 0; i < budget.BaseCost; i++) Assert.True(player.VaalSouls.Gain());
            Assert.True(Game.TryBeginSkillCooldownUse(player, skill.Id, skill.Effect,
                Game.CooldownForSkill(player, skill.Id), false, () => { }, out var ticket));
            Assert.False(Game.LandsAtAim(player, skill.Effect));
            Assert.True(await Game.CastGemSkill(player, skill.Id, (100, 100), (142, 100), cooldownUse: ticket));
            Assert.Equal(0u, player.VaalSouls.Read().Single(s => s.ActiveSkillsRow == budget.ActiveSkillsRow).Current);
            Assert.Equal((106, 100), enemy.Walk.Position(clock.At));
            Assert.Equal(enemy.MaxLife, enemy.Life);

            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Session.Enqueue(() => hold.Task);
            try
            {
                clock.At += 6;
                fixture.Session.AdvanceTo(ProjectileRuntime.Time(clock.At));
                Assert.Equal(2, contacts.Count);
                Assert.True(enemy.Life < enemy.MaxLife);
            }
            finally { hold.TrySetResult(); }
            var at = enemy.Walk.Position(clock.At);
            Assert.InRange(at.X, 142, 158);
            Assert.Equal((100, 100), player.Walk.Position(clock.At));
            Assert.Equal((200, 200), distant.Walk.Position(clock.At));
            Assert.Equal(distant.MaxLife, distant.Life);
            Assert.Equal(new[] { (106, 100), at }, contacts);
            Assert.True(enemy.Life < enemy.MaxLife);

            SpawnedMonster At(uint id, int x, int y)
            {
                var target = new SpawnedMonster("warp witness", new(x, y, 0), 0, 0, 100_000)
                    { ObjectId = id, Hostile = true, NoRewards = true };
                player.Area.Monsters.Add(id, target);
                player.Area.Grid.Set(new(id), (x, y));
                return target;
            }
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }

    sealed class Clock : IPlayerUpdateSchedule
    {
        public double At = 1;
        public double Now(Player player) => At;
        public async Task Wait(int milliseconds, CancellationToken stopping)
        {
            await Task.Yield();
            stopping.ThrowIfCancellationRequested();
            At += Math.Max(milliseconds, 1) / 1000d;
        }
    }
}
