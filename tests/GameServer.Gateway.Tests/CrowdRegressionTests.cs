using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class CrowdRegressionTests
{
    static T Call<T>(string name, params object?[] args) => (T)typeof(Program)
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;

    static async Task Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "The expected simulation transition did not occur.");
            await Task.Delay(5);
        }
    }

    sealed class Connection : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(), reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public NetworkStream ReadStream => reader.GetStream();
        public readonly ClientState State;
        public readonly PlayerInventories Items;
        public readonly SpawnedMonster Target;
        public readonly (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Gem;
        public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));
        public Connection(bool cyclone = false)
        {
            TestSaves.Ensure();
            State = new(new(ushort.MaxValue, "CrowdRegression", 200, 200, 100, 100, (_, _) => true, []), 1, 1)
            {
                Character = "CrowdRegression" + Guid.NewGuid().ToString("N"), ClassRow = 1,
                PlayerSent = true, PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            };
            Program.ConnectionMetrics.Add(Ciphers, State.Performance);
            Items = PlayerInventories.For(State.Character, Program.Items);
            if (cyclone)
            {
                // Item activation reads the requirement attributes, not the pools set below (socketed gems count).
                foreach (string attribute in new[] { "strength", "dexterity", "intelligence" })
                    Set($"virtual_{attribute}_for_requirements", 1000);
                var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemCyclone")!);
                var host = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!) with
                    { Sockets = [new(SocketColour.White, gem)], Links = [1] };
                Items.Add(PlayerInventories.BodyArmour, 0, 0, host);
                Gem = Call<IEnumerable<(ushort, int, string, int, int, uint)>>("GemSkillEffects", State).Single();
            }
            Set("global_cannot_crit", 1); Set("physical_damage_+%", -100);
            Set("attack_minimum_added_chaos_damage", 100); Set("attack_maximum_added_chaos_damage", 100);
            State.Pools = Program.PoolsFor(State, 40) with
                { Life = 1000, Mana = 1000, Accuracy = int.MaxValue, Strength = 1000, Dexterity = 1000, Intelligence = 1000 };
            Target = new("old position target", new(102, 100, 30), 0, 2, 1000000);
            State.Monsters[42] = Target;
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }
        public void Set(string name, int value) => State.PlayerStats[Program.Stats.Key(name)!.Value] = value;
        public Task Message(ushort id, byte[] bytes) => Program.OnClientMessages("crowd-regression", Stream, Ciphers,
            [(id, bytes)], [], State);
        public void Dispose()
        {
            State.Closed = true;
            writer.Dispose(); reader.Dispose(); listener.Stop();
            PlayerInventories.Forget(State.Character!);
            Program.GemSkillIdsOf.TryRemove(State.Character!, out _);
        }
    }

    // A freeze (a critical cold hit of the player's maximum life) or a stun's hit recovery, both longer than the
    // channel's silence timeout.
    static void Interrupt(ClientState state, string how)
    {
        double now = state.Clock.Elapsed.TotalSeconds;
        var cold = DamagePacket.Of(DamageType.Cold, state.PlayerMaxLife, state.PlayerMaxLife);
        lock (state.Monsters)
            if (how == "stun") Assert.True(state.HitRecovery.Begin(now, 10));
            else state.ElementalAilments.Apply(cold, cold, true, state.PlayerMaxLife, now, _ => 0, new Random(1));
        Assert.True(Program.PlayerActionBlocked(state, now, now));
    }

    [Fact]
    public async Task A_failed_initial_channel_payment_never_starts_a_timer()
    {
        using var f = new Connection(cyclone: true);
        f.State.PlayerMana = 0; f.State.ActionCount = 7;
        Assert.False(await Call<Task<bool>>("CastGemSkill", "crowd-regression", f.Stream, f.Ciphers, f.State,
            f.Gem.Id, (100, 100), (100, 100), 0u, (ushort)0, (int?)7, null, false));
        Assert.Equal(0, f.State.NextChannelHitAt);
        Assert.Equal((ushort)0, f.State.ChannelSkill);
    }

}
