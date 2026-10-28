using System.Reflection;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed partial class AscendancyGatewayIntegrationTests
{
    static uint GamblerKey(string name) => Program.Stats.Key(name)!.Value;
    static int GamblerStat(ClientState state, string name) => Program.CharacterPlayerStats(state).GetValueOrDefault(GamblerKey(name));

    [Fact]
    public void Gambler_checklist_is_the_actual_graph_including_both_choice_options()
    {
        if (!DataAvailable()) return;
        var definition = Program.Ascendancies.Find(4, AscendancyFamily.Phrecia, "Gambler")!;
        Assert.Equal(new ushort[] { 32730, 17298, 56028, 53426, 12789, 63992, 62445, 25530, 15874, 5523,
            51178, 6481, 56067, 20073, 54394, 16638, 11167 }.Order(), definition.Nodes.Keys.Order());
        Assert.DoesNotContain((ushort)48297, definition.Nodes.Keys); // A table row alone does not confer membership.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gambler_legal_eight_point_builds_resolve_every_node_and_survive_reconnection(bool defence)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(choice: "Gambler");
        ushort[] nodes = defence ? [53426, 16638, 54394, 11167, 51178, 56067, 20073, 63992, 15874]
            : [56028, 62445, 17298, 25530, 63992, 15874, 12789, 5523];
        Assert.Null((await game.Change(nodes.Reverse().ToArray(), allocate: true)).Error);
        Assert.Equal(8, Program.AscendancyRules.SpentPoints(4, game.State.MainAscendancy));
        Assert.Equal(nodes.Order(), game.Reload().MainAscendancy.Allocated.Order());
        Assert.Empty(game.State.Passives);
        var mods = Program.GamblerFor(Program.CharacterPlayerStats(game.State));
        Assert.Equal(-30, mods.DamageMorePct);
        Assert.Equal(40, mods.TripleDamageChance);
        Assert.Equal(20, GamblerStat(game.State, "base_item_found_rarity_+%"));
        Assert.Equal(20, ProgramValue<int>("IncreasedItemRarity", game.State));
        Assert.Equal(12, game.State.Pools.BlockChance);
        if (defence)
        {
            Assert.Equal(-1, mods.EnemyDamageExtraRolls);
            Assert.Equal(66, GamblerStat(game.State, "avoid_ignite_%"));
            Assert.Equal(66, GamblerStat(game.State, "avoid_stun_%"));
            Assert.True(game.State.Pools.AttackBlockUnlucky);
            Assert.True(game.State.Pools.SpellBlockLucky);
            Assert.Equal(78, game.State.Pools.SpellBlockChanceMax);
        }
        else
        {
            Assert.Equal(-1, mods.DamageExtraRolls);
            Assert.Equal(1, Program.CriticalExtraRolls(game.State));
            Assert.Equal(50, mods.InvertElementalResistanceChance);
            Assert.Equal(50, mods.EnemyHitsNinetyResistanceChance);
            Assert.Equal(-25, game.State.Pools.FireResistance);
        }
        string before = game.SavedJson;
        Assert.NotNull((await game.Change([defence ? (ushort)56028 : (ushort)56067], allocate: true)).Error);
        Assert.Equal(before, game.SavedJson);
        Assert.NotNull((await game.Change([42293], allocate: true)).Error); // Ordinary Gladiator node is foreign.
        Assert.Equal(before, game.SavedJson);
        if (!defence)
        {
            Assert.Null((await game.Change([5523], allocate: false, refundPoints: true)).Error);
            Assert.Equal(0, Program.CriticalExtraRolls(game.State));
            Assert.Equal(0, Program.GamblerFor(Program.CharacterPlayerStats(game.State)).DamageExtraRolls);
        }
    }

    [Fact]
    public async Task Gambler_choice_switch_and_refund_are_exclusive_paid_atomic_and_remove_live_luck()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(choice: "Gambler", refundPoints: 40);
        Assert.Null((await game.Change([53426, 16638, 54394, 11167, 51178], allocate: true)).Error);
        Assert.Equal(4, Program.AscendancyRules.SpentPoints(4, game.State.MainAscendancy));
        string before = game.SavedJson;
        Assert.NotNull((await game.Change([6481], allocate: true)).Error);
        Assert.Equal(before, game.SavedJson);
        Assert.NotNull((await game.Change([11167], allocate: false, refundPoints: true)).Error);
        Assert.Equal(before, game.SavedJson);
        Saves.FailWrite = name => name == game.Character;
        try { await Assert.ThrowsAsync<IOException>(() => game.Change([51178], allocate: false, refundPoints: true)); }
        finally { Saves.FailWrite = null; }
        Assert.Equal(before, game.SavedJson);
        Assert.True(game.State.Pools.AttackBlockUnlucky);
        Assert.Empty(game.LastBytes);
        Assert.Null((await game.Change([51178], allocate: false, refundPoints: true)).Error);
        Assert.Equal((ushort)35, game.State.RefundPoints);
        Assert.False(game.State.Pools.AttackBlockUnlucky);
        Assert.False(game.State.Pools.SpellBlockLucky);
        Assert.Equal(75, game.State.Pools.SpellBlockChanceMax);
        Assert.Null((await game.Change([6481], allocate: true)).Error);
        Assert.True(game.State.Pools.AttackBlockLucky);
        Assert.True(game.State.Pools.SpellBlockUnlucky);
        Assert.Equal(78, game.State.Pools.BlockChanceMax);
        Assert.True(game.Reload().Pools.SpellBlockUnlucky);
        Assert.Null((await game.Change([6481], allocate: false, refundPoints: true)).Error);
        Assert.Null((await game.Change([11167], allocate: false, refundPoints: true)).Error);
        Assert.Equal((ushort)30, game.State.RefundPoints); // Choice parent refunds for free.
        Assert.False(game.Reload().Pools.AttackBlockLucky);
    }

    [Theory]
    [InlineData(65, true)]
    [InlineData(66, false)]
    public async Task Risk_Aversion_uses_ordinary_66_percent_avoidance_for_actual_monster_ailments_and_refund(int roll, bool avoids)
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(choice: "Gambler", level: 90);
        Assert.Null((await game.Change([56067, 20073], allocate: true)).Error);
        var state = game.State;
        var hit = DamagePacket.Of(DamageType.Fire, 100, 100).Add(DamageType.Cold, new(100, 100))
            .Add(DamageType.Lightning, new(100, 100));
        state.IncomingHitRandom = new GamblerRandom(roll);
        await game.GamblerMonsterHit(hit);
        double now = state.Clock.Elapsed.TotalSeconds;
        var effects = state.ElementalAilments.Snapshot(now);
        Assert.Equal(!avoids, effects.Ignite);
        Assert.Equal(!avoids, effects.Freeze);
        Assert.Equal(!avoids, effects.Chill > 0);
        Assert.Equal(!avoids, effects.Shock > 0);
        Assert.True(state.PlayerLife < state.PlayerMaxLife); // Avoidance is not hit damage immunity.
        Assert.Equal(!avoids, Program.PlayerActionBlocked(state, now, now));
        uint before = state.PlayerLife;
        int dot = Program.AdvancePlayerAilmentsLocked(state, now, now + .25, Program.CharacterPlayerStats(state));
        Assert.Equal(!avoids, dot > 0);
        Assert.Equal(!avoids, state.PlayerLife < before);
        // No avoidance luck is inferred from Spectacular Parlay or Overly Confident.
        Assert.Equal(66, GamblerStat(state, "avoid_stun_%"));
        double stun = PlayerHitRecovery.Chance(400, 1000, n => GamblerStat(state, n));
        Assert.Equal(34, stun, 8);
        Assert.Null((await game.Change([20073], allocate: false, refundPoints: true)).Error);
        Assert.Equal(0, GamblerStat(state, "avoid_ignite_%"));
        Assert.Equal(0, GamblerStat(game.Reload(), "avoid_stun_%"));
        state.ElementalAilments.Clear(); state.PlayerLife = state.PlayerMaxLife;
        state.IncomingHitRandom = new GamblerRandom(0);
        await game.GamblerMonsterHit(hit);
        Assert.True(state.ElementalAilments.Snapshot(state.Clock.Elapsed.TotalSeconds).Ignite);
    }

    [Fact]
    public async Task Risky_Exploit_is_a_per_contact_90_percent_override_and_refunding_restores_normal_resistance()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(choice: "Gambler", level: 90);
        Assert.Null((await game.Change([56028, 62445], allocate: true)).Error);
        game.State.IncomingHitRandom = new GamblerRandom(49);
        uint before = game.State.PlayerLife;
        await game.Command("/hit fire 100");
        Assert.Equal(10u, before - game.State.PlayerLife);
        game.State.IncomingHitRandom = new GamblerRandom(50);
        before = game.State.PlayerLife;
        await game.Command("/hit fire 100");
        Assert.Equal(125u, before - game.State.PlayerLife);
        Assert.Null((await game.Change([62445], allocate: false, refundPoints: true)).Error);
        before = game.State.PlayerLife;
        await game.Command("/hit fire 100");
        Assert.Equal(100u, before - game.State.PlayerLife);
    }

    [Fact]
    public async Task Overly_Confident_rolls_actual_enemy_hit_damage_twice_and_refund_removes_the_second_roll()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(choice: "Gambler", level: 90);
        Assert.Null((await game.Change([53426, 16638], allocate: true)).Error);
        var random = new GamblerDamageRandom(); game.State.IncomingHitRandom = random;
        uint before = game.State.PlayerLife;
        await game.GamblerMonsterHit(DamagePacket.Of(DamageType.Chaos, 1, 100), critical: false);
        Assert.Equal(20u, before - game.State.PlayerLife);
        Assert.Equal(2, random.DamageDraws);
        Assert.Null((await game.Change([16638], allocate: false, refundPoints: true)).Error);
        random = new(); game.State.IncomingHitRandom = random;
        before = game.State.PlayerLife;
        await game.GamblerMonsterHit(DamagePacket.Of(DamageType.Chaos, 1, 100), critical: false);
        Assert.Equal(80u, before - game.State.PlayerLife);
        Assert.Equal(1, random.DamageDraws);
    }

    sealed class GamblerDamageRandom : Random
    {
        public int DamageDraws;
        public override int Next(int minValue, int maxValue) => ++DamageDraws == 1 ? 80 : 20;
        public override double NextDouble() => .999;
    }

    [Fact]
    public async Task Enemy_hit_luck_never_selects_the_separate_monster_ignite_damage_roll()
    {
        if (!DataAvailable()) return;
        using var game = new GatewayCharacter(choice: "Gambler", level: 90);
        Assert.Null((await game.Change([53426, 16638], allocate: true)).Error);
        var state = game.State;
        state.IncomingHitRandom = new IgniteRollRandom(80, 20, 60);
        uint before = state.PlayerLife;
        await game.GamblerMonsterHit(DamagePacket.Of(DamageType.Fire, 1, 100));
        Assert.Equal(26u, before - state.PlayerLife); // Unlucky hit 20, monster critical hit multiplier 130%.
        Assert.Equal(70.2, state.ElementalAilments.Magnitude(PlayerElementalAilments.Kind.Ignite,
            state.Clock.Elapsed.TotalSeconds), 8); // Separate ordinary roll 60, 90%/s and monster +30% ailment crit.
        Assert.Null((await game.Change([16638], allocate: false, refundPoints: true)).Error);
        state.ElementalAilments.Clear(); state.PlayerLife = state.PlayerMaxLife;
        state.IncomingHitRandom = new IgniteRollRandom(80, 60);
        before = state.PlayerLife;
        await game.GamblerMonsterHit(DamagePacket.Of(DamageType.Fire, 1, 100));
        Assert.Equal(104u, before - state.PlayerLife);
        Assert.Equal(70.2, state.ElementalAilments.Magnitude(PlayerElementalAilments.Kind.Ignite,
            state.Clock.Elapsed.TotalSeconds), 8);
    }

    sealed class IgniteRollRandom(params int[] rolls) : Random
    {
        readonly Queue<int> remaining = new(rolls);
        public override int Next(int minValue, int maxValue) => remaining.Dequeue();
        public override int Next(int maxValue) => 0;
        public override double NextDouble() => .999;
    }

    sealed class GamblerRandom(int value) : Random
    {
        public override int Next(int minValue, int maxValue) => Math.Clamp(value, minValue, maxValue - 1);
        public override int Next(int maxValue) => Math.Clamp(value, 0, maxValue - 1);
        public override double NextDouble() => .999;
    }

    sealed partial class GatewayCharacter
    {
        public Task<(string? Error, byte[] Bytes)> GamblerMonsterHit(DamagePacket hit, bool critical = true) => Exchange(async (stream, cipher) =>
        {
            State.EvasionEntropy = 99; State.LastEvasionAt = State.Clock.Elapsed.TotalSeconds;
            var position = State.Walk.Position(State.Clock.Elapsed.TotalSeconds);
            var monster = new SpawnedMonster("Gambler incoming contact", new(position.X + 2, position.Y, 30), 0, 2, 1000)
            {
                Hostile = true,
                Attack = new(SkillCatalog.FirstSkillId, .002, 20, hit),
                Mods = critical ? new([], [], new(new Dictionary<string, int> { ["always_crit"] = 1 })) : MonsterModSet.None,
            };
            State.Monsters[42] = monster;
            await (Task)GatewayMethod("MonsterSwing").Invoke(null, ["gambler-integration", stream, cipher,
                State, 42u, monster, (position.X + 2, position.Y), position, State.IncomingHitEpoch])!;
            return null;
        });
    }
}
