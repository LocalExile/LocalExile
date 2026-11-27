using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class RetaliationTests
{
    sealed class LowRolls : Random
    {
        public override double NextDouble() => 0;
    }

    sealed class Clock : IPlayerUpdateSchedule
    {
        public double Time = 1;
        public double Now(Player player) => Time;
        public Task Wait(int milliseconds, CancellationToken stopping) =>
            throw new InvalidOperationException("Skill admission must run in the current area frame.");
    }

    [Fact]
    public void Divine_Retribution_spends_one_unexpired_block_opportunity_after_cost_admission()
    {
        using var fixture = new TriggeredSummonTests.Fixture();
        var player = fixture.Player;
        ushort id = Assert.Single(fixture.Equip(("Divine Retribution", 1)));
        var skill = Game.SupportedSkill(player, id);
        player.IncomingHitRandom = new LowRolls();
        var clock = new Clock();
        var previous = Game.AreaUpdateContext.Value;
        Game.AreaUpdateContext.Value = new(fixture.Lifetime, clock);
        try
        {
            Game.CharacterPlayerStats(player);
            bool Admit(bool reserve = true, bool triggered = false) => Game.TryBeginCooldownUseCore(player, skill.Effect,
                reserve ? Game.CooldownForSkill(player, id) : null, false, () => { }, out _, actionToken: null,
                reserve: reserve, requestedAt: null, skillId: id, triggered: triggered);

            void Block()
            {
                player.Pools = player.Pools with { SpellBlockChance = 100 };
                Assert.True(Game.TrySyntheticEnemyHit(player, DamageType.Physical, 1,
                    block: true, spell: true, suppress: false, out var hit));
                Assert.True(hit.Blocked);
            }

            uint mana = player.PlayerMana;
            Assert.False(Admit());
            Assert.Equal(mana, player.PlayerMana);
            Assert.Null(Game.SingleUseState(player, skill.Effect, id));

            Block();
            Assert.False(Admit(triggered: true));
            Assert.False(Game.TriggerPayloadAvailable(player, skill));
            var readyBuff = Assert.IsType<Buff>(player.Area.Buffs.Get(player.Id, 2470, player.Id, tag: id));
            Assert.Equal(id, readyBuff.SourceSkill);
            Assert.Equal((ushort)0, readyBuff.U82);
            clock.Time += 2;
            Assert.True(player.RecentEvents.Happened(StatConditions.Blocked, clock.Time));
            Assert.False(Admit());
            Assert.Equal(mana, player.PlayerMana);

            Block();
            player.PlayerMana = 0;
            Assert.False(Admit());
            Assert.True(player.Area.Buffs.Has(player.Id, 2470, player.Id, tag: id));
            player.PlayerMana = mana;
            Assert.True(Admit());
            Assert.True(player.PlayerMana < mana);
            Assert.False(player.Area.Buffs.Has(player.Id, 2470, player.Id, tag: id));
            Assert.Equal(1, Game.SingleUseState(player, skill.Effect, id)!.SpentUses);
            // The usual four-second block condition survives the one-shot retaliation window.
            Assert.True(player.RecentEvents.Happened(StatConditions.Blocked, clock.Time));
            Assert.False(Admit(reserve: false));
        }
        finally { Game.AreaUpdateContext.Value = previous; }
    }
}
