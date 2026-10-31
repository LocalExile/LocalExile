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

    sealed class GamblerDamageRandom : Random
    {
        public int DamageDraws;
        public override int Next(int minValue, int maxValue) => ++DamageDraws == 1 ? 80 : 20;
        public override double NextDouble() => .999;
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
                State, 42u, monster, monster.ActionGeneration])!;
            return null;
        });
    }
}
