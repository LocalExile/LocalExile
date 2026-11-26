using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

public sealed class ChainHookTrarthusTests
{
    [Fact]
    public async Task Base_hook_pulls_and_hits_on_beam_contact_after_the_throw()
    {
        using var run = new Scenario(0, ordinaryHook: true);
        var target = run.Spawn(890, (145, 100));
        run.Step(.7);
        double swing = Game.SupportedAttackSeconds(run.Player, run.Chain);
        Assert.True(await Game.CastGemSkill(run.Player, run.Chain, (100, 100), (145, 100), sequence: 1));

        run.Step(swing + .18);
        Assert.Equal((100, 100), run.Player.Walk.Position(run.Clock.At));
        Assert.Equal(10000u, target.Life);

        run.Step(.10);
        Assert.Equal((137, 100), run.Player.Walk.Position(run.Clock.At));
        Assert.InRange(target.Life, 1u, 9999u);
        uint after = target.Life;
        run.Step(.2);
        Assert.Equal(after, target.Life);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(20, 11)]
    public async Task Attachments_select_nearest_and_replace_the_oldest_at_the_shared_limit(int quality, int perUse)
    {
        using var run = new Scenario(quality);
        var east = Enumerable.Range(0, 12).Select(i => run.Spawn(901u + (uint)i, (126 + i, 100))).ToArray();
        var observer = run.Spawn(930, (120, 111));

        await run.Cast(run.Chain, (160, 100));
        Assert.Equal(perUse, east.Count(run.Attached));
        Assert.All(east.Take(perUse), target =>
        {
            Assert.True(run.Attached(target));
            Assert.Equal(10000u, target.Life);
        });
        Assert.All(east.Skip(perUse), target => Assert.False(run.Attached(target)));
        Assert.False(run.Attached(observer));
        Assert.Equal(10000u, observer.Life);

        var north = Enumerable.Range(0, 12).Select(i => run.Spawn(940u + (uint)i, (100, 65 + i))).ToArray();
        await run.Cast(run.Chain, (100, 55));
        Assert.Equal(16, east.Concat(north).Count(run.Attached));
        Assert.Equal(perUse, north.Count(run.Attached));
        Assert.Equal(quality == 0, run.Attached(east[0]));

        var newest = run.Spawn(960, (100, 130));
        await run.Cast(run.Chain, (100, 145));
        Assert.True(run.Attached(newest));
        Assert.False(run.Attached(east[0]));
        Assert.Equal(16, east.Concat(north).Append(newest).Count(run.Attached));
        Assert.InRange(observer.Life, 1u, 9999u);
    }

    [Fact]
    public async Task Attacks_gain_rage_and_a_chain_snaps_once_on_range_or_death()
    {
        using var run = new Scenario(0);
        var target = run.Spawn(981, (116, 100));
        var observer = run.Spawn(982, (114, 112));
        await run.Cast(run.Chain, (116, 100));
        Assert.True(run.Attached(target));
        Assert.False(run.Attached(observer));
        Assert.Equal(10000u, target.Life);

        await run.Cast(run.Attack, (116, 100), target.ObjectId);
        Assert.Equal(1, run.Player.Rage.Read(run.Clock.At, default).Current);
        Assert.InRange(target.Life, 1u, 9999u);
        Assert.Equal(10000u, observer.Life);

        run.Player.Walk.Teleport(175, 100, run.Clock.At);
        run.Step(.2);
        Assert.True(run.Attached(target));
        run.Player.Walk.Teleport(177, 100, run.Clock.At);
        run.Step(.2);
        run.Step(.2);
        Assert.False(run.Attached(target));
        Assert.InRange(observer.Life, 1u, 9999u);
        uint afterRange = observer.Life;
        run.Step(.3);
        Assert.Equal(afterRange, observer.Life);

        run.Player.Walk.Teleport(100, 100, run.Clock.At);
        await run.Cast(run.Chain, (116, 100));
        Assert.True(run.Attached(target));
        target.Life = 1;
        await run.Cast(run.Attack, (116, 100), target.ObjectId);
        Assert.Equal(0u, target.Life);
        Assert.False(run.Attached(target));
        Assert.InRange(observer.Life, 1u, afterRange - 1);
        uint afterDeath = observer.Life;
        run.Step(.2);
        Assert.Equal(afterDeath, observer.Life);

        var finalAnchor = run.Spawn(983, (123, 100));
        await run.Cast(run.Chain, (123, 100));
        Assert.True(run.Attached(finalAnchor));
        run.Player.PlayerLife = 0;
        run.Step(.2);
        Assert.False(run.Attached(finalAnchor));
        Assert.Equal(10000u, finalAnchor.Life);
    }

    sealed class Scenario : IDisposable
    {
        readonly TriggeredSummonTests.Fixture fixture;
        readonly IDisposable stats;
        readonly PlayerUpdatesState? previous;
        readonly ushort buff = BuffRows.Find("chain_strike_attachment_buff");
        ushort sequence;
        internal readonly ViperStrikeMambaTests.AttackClock Clock = new();
        internal Player Player => fixture.Player;
        internal ushort Chain { get; }
        internal ushort Attack { get; }

        internal Scenario(int quality, bool ordinaryHook = false)
        {
            fixture = new(Clock);
            Player.Use(new(1, "chain-hook-test", 300, 300, 100, 100, (_, _) => true, []) { Sight = (_, _) => 2 });
            Player.Walk.Teleport(100, 100, Clock.At);
            Player.Connection.LockstepClient = false;
            Player.Connection.PlayerObjectCreated = true;
            Player.ProjectileRuntimeState = fixture.Session;
            var inventory = Game.ItemsOf(Player);
            var weapon = Assert.Single(inventory.Items(PlayerInventories.Weapon1));
            inventory.Change(PlayerInventories.Weapon1, weapon.Id, _ => ItemCatalog.Create(Program.Items.Find("Rusted Sword")!));
            stats = GameEvents.PlayerStatSources.Add("Game", (owner, map) =>
            {
                if (!ReferenceEquals(owner, Player)) return map;
                map[Stat.GlobalAlwaysHit] = 1;
                map[Stat.GlobalCannotCrit] = 1;
                map[Stat.Strength] = 200;
                map[Stat.Dexterity] = 200;
                return map;
            });

            var (chainType, variant) = Program.Items.FindGem(ordinaryHook ? "Chain Hook" : "Chain Hook of Trarthus")!.Value;
            var chain = ItemCatalog.Create(chainType) with { GemVariant = variant.Index, Quality = (byte)quality };
            var (attackType, attackVariant) = Program.Items.FindGem("Heavy Strike")!.Value;
            var attack = ItemCatalog.Create(attackType) with { GemVariant = attackVariant.Index };
            inventory.Change(PlayerInventories.Weapon1, weapon.Id, item => item with
            {
                Sockets = [new(SocketColour.White, chain), new(SocketColour.White, attack)],
                Links = [1, 1],
            });
            string effect = ordinaryHook ? "ChainHook" : "ChainHookAltY";
            Chain = Game.GemSkillEffects(Player).Single(g => Program.Skills.EffectId(g.Effect) == effect).Id;
            Attack = Game.GemSkillEffects(Player).Single(g => Program.Skills.EffectId(g.Effect) == "HeavyStrike").Id;
            previous = Game.AreaUpdateContext.Value;
            Game.AreaUpdateContext.Value = new(fixture.Lifetime, Clock);
            Game.RecomputePoolsAndSpeed(Player);
        }

        internal SpawnedMonster Spawn(uint id, (int X, int Y) at)
        {
            var target = new SpawnedMonster("chain target", new(at.X, at.Y, 0), 0, 0, 10000)
                { ObjectId = id, Hostile = true, NoRewards = true };
            Player.Area.Monsters[id] = target;
            Player.Area.Grid.Set(new(id), at);
            return target;
        }

        internal bool Attached(SpawnedMonster target) => Player.Area.Buffs.Has(target.ObjectId, buff, Player.Id);

        internal async Task Cast(ushort skill, (int X, int Y) aim, uint target = 0)
        {
            Step(.7);
            Assert.True(await Game.CastGemSkill(Player, skill, Player.Walk.Position(Clock.At), aim,
                targetId: target, sequence: ++sequence));
            Step(1);
            Step(.11);
        }

        internal void Step(double seconds)
        {
            Clock.At += seconds;
            fixture.Session.AdvanceTo(ProjectileRuntime.Time(Clock.At));
        }

        public void Dispose()
        {
            fixture.Session.Invalidate();
            Game.AreaUpdateContext.Value = previous;
            stats.Dispose();
            fixture.Dispose();
        }
    }
}
