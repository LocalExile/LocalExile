using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;
using MonsterRarity = GameServer.Core.Protocol.Instance.MonsterRarity;

namespace GameServer.Gateway.Tests;

public sealed class KitavaActFiveTests
{
    [Fact]
    public void Each_earned_Kitava_penalty_rebuilds_immediately_and_never_stacks_with_its_alias_or_repeated_flag()
    {
        using var runtime = new Runtime();
        var c = new Player(new(1, "penalty", 100, 100, 20, 20, null, []), 1, 1)
        { Character = { Name = "KitavaPenaltyRegression" }, Pools = new(45, 1000, 100, 1, 0) };
        runtime.Player = c;
        c.Character.QuestFlags.Add(Quests.EndGameAvailable); c.Character.UnearnedQuestFlags.Add(Quests.EndGameAvailable);
        c.Pools = Game.PoolsFor(c, 45);
        DamageType[] types = [DamageType.Fire, DamageType.Cold, DamageType.Lightning, DamageType.Chaos];
        string[] names = ["fire", "cold", "lightning", "chaos"];
        int[] Panel() => names.Select(n => Game.CharacterPlayerStats(c).GetValueOrDefault(Program.Stats.Key($"uncapped_{n}_damage_resistance_%")!.Value)).ToArray();
        var before = types.Select(t => Game.Resistance(c, t)).ToArray();
        Assert.Equal(before, Panel());
        Assert.Equal(0, Game.ResistancePenalty(c));
        Assert.NotNull(Game.CommitQuestFlags(c, [KitavaActFive.Complete]));
        Assert.Equal(before.Select(r => r - 30), types.Select(t => Game.Resistance(c, t)));
        Assert.Equal(before.Select(r => r - 30), Panel());
        Assert.Null(Game.CommitQuestFlags(c, [KitavaActFive.Complete]));
        Game.CommitQuestFlags(c, ["SinOnKitavaDefeat"]); Assert.Equal(-30, Game.ResistancePenalty(c));
        Assert.NotNull(Game.CommitQuestFlags(c, [Quests.EndGameAvailable]));
        Assert.Equal(before.Select(r => r - 60), types.Select(t => Game.Resistance(c, t)));
        Assert.Equal(before.Select(r => r - 60), Panel());
        Assert.Null(Game.CommitQuestFlags(c, [Quests.EndGameAvailable]));
        Game.CommitQuestFlags(c, ["a10q3-UsedRewardBook"]); Assert.Equal(-60, Game.ResistancePenalty(c));
    }
    sealed class Runtime : IDisposable
    {
        readonly string saves = Path.Combine(Path.GetTempPath(), $"localexile-kitava-{Guid.NewGuid():N}.db");
        readonly IGameLogic? previousLogic = Program.GameLogicOrNull;
        readonly IModuleLifetime? previousLifetime = Game.CombatLifetime;
        readonly ModuleLifetime lifetime = new("Game");
        public Player? Player;
        public Runtime() { Saves.Open(saves); Game.CombatLifetime = lifetime; Program.SetGameLogic(new GameServer.Modules.Game.GameLogic()); }
        public void Dispose() { if (Player is { } c) c.Closed = true; lifetime.Stop(ex => throw ex); Game.CombatLifetime = previousLifetime; Program.SetGameLogic(previousLogic!); TestEnvironment.ReleaseSaves(saves); }
    }
    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(); readonly TcpClient reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public Wire() { listener.Start(); var accept = listener.AcceptTcpClientAsync(); writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port); reader = accept.GetAwaiter().GetResult(); }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Stop(); }
    }
}
