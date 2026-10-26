using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public class InstantObjectDispatchTests
{
    sealed class Fixture : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        readonly string character = $"Blink{Guid.NewGuid():N}";
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        public readonly ClientState Conn = InstantObjectSkillTests.Connection();
        public readonly SpawnedMonster Target;
        public ushort Skill => Program.GemSkillIdPool.Value[0];
        public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));

        public Fixture()
        {
            TestSaves.Ensure();
            Conn = new(Conn.Layout, 1, 1) { Character = character, PlayerSent = true, PlayerLife = 285,
                PlayerMana = 170, ActionCount = 17, ChannelSkill = 0x104, ChannelSkillSequence = 3,
                Pools = new(20, 285, 170, 100, 0, Strength: 100, Dexterity: 100, Intelligence: 100),
                SentEnergyShield = 0, SentRechargeActive = false, SentEsRecoveryRate = 0 };
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemFrostblink")!);
            var host = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
                { Sockets = [new(SocketColour.Blue, gem)] };
            PlayerInventories.For(character, Program.Items).Add(PlayerInventories.Weapon1, 0, 0, host);
            Target = InstantObjectSkillTests.Target(Conn);
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }

        public Task Use() => Program.UseInstantSkill("object-blink-test", writer.GetStream(), Ciphers, Conn,
            Skill, 42, (280, 220));

        public void Unequip() => PlayerInventories.For(character, Program.Items).Remove(PlayerInventories.Weapon1,
            PlayerInventories.For(character, Program.Items).Items(PlayerInventories.Weapon1).Single().Id);

        public async Task AssertPackets(params byte[][] packets)
        {
            byte[] expected = [.. packets.SelectMany(p => p)];
            if (expected.Length > 0)
            {
                byte[] received = new byte[expected.Length];
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await reader.GetStream().ReadExactlyAsync(received, timeout.Token);
                new ConnectionCiphers(new byte[64], isClient: true).Receive.Process(received);
                Assert.Equal(expected, received);
            }
            Assert.Equal(0, reader.Available);
        }

        public void Dispose()
        {
            Conn.Closed = true;
            writer.Dispose(); reader.Dispose(); listener.Stop(); PlayerInventories.Forget(character);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("moving")]
    [InlineData("dead")]
    [InlineData("partial-trace")]
    public async Task Invalid_object_preflight_never_pays_or_reserves_or_confirms(string state)
    {
        using var f = new Fixture();
        if (state == "missing") f.Conn.Monsters.Clear();
        if (state == "moving") f.Target.Walk.Order(380, 220, f.Conn.Clock.Elapsed.TotalSeconds);
        if (state == "dead") f.Target.Dead = true;
        if (state == "partial-trace") f.Conn.Barrier = new HashSet<(int, int)> { (315, 220) };
        await f.Use().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(170u, f.Conn.PlayerMana); Assert.Empty(f.Conn.SkillReadyAt);
        Assert.Equal((280, 220), f.Conn.Walk.Destination); Assert.Equal(17, f.Conn.ActionCount);
        await f.AssertPackets();
    }

    [Fact]
    public async Task Actual_object_route_sends_bound_raw_aim_and_moves_without_generic_damage()
    {
        using var f = new Fixture();
        await f.Use().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((317, 220), f.Conn.Walk.Destination);
        Assert.Equal(158u, f.Conn.PlayerMana); Assert.Single(f.Conn.SkillReadyAt);
        Assert.Equal(100u, f.Target.Life); Assert.Equal(double.NegativeInfinity, f.Conn.LastEnemyHitAt);
        Assert.Equal(17, f.Conn.ActionCount); Assert.Equal((ushort)0x104, f.Conn.ChannelSkill);
        Assert.Equal((ushort)3, f.Conn.ChannelSkillSequence);
        await f.AssertPackets(InstanceProtocol.LifeUpdate(0x1000, 158, pool: 1),
            InstanceProtocol.InstantAction(0x1000, f.Skill, 300, 220, targetObjectId: 42));
    }

    [Theory]
    [InlineData("target-walk")]
    [InlineData("target-death")]
    [InlineData("transfer")]
    [InlineData("pause")]
    [InlineData("barrier")]
    [InlineData("unequip")]
    [InlineData("ticket-version")]
    public async Task Paid_start_cannot_send_a_late_confirmation_after_queued_invalidation(string change)
    {
        using var f = new Fixture();
        await f.Gate.WaitAsync();
        var use = f.Use(); // Preflight/payment completes before its queued mana packet can leave.
        Assert.Equal(158u, f.Conn.PlayerMana);
        if (change == "target-walk") f.Target.Walk.Order(380, 220, f.Conn.Clock.Elapsed.TotalSeconds);
        if (change == "target-death") f.Target.Life = 0;
        if (change == "transfer") f.Conn.TryBeginAreaChange(new(2, 0, false), out _);
        if (change == "pause") f.Conn.Clock.Stop();
        if (change == "barrier") f.Conn.Barrier = new HashSet<(int, int)> { (315, 220) };
        if (change == "unequip") f.Unequip();
        if (change == "ticket-version")
        {
            var cooldown = Assert.Single(f.Conn.SingleUseCooldowns).Value;
            Assert.True(cooldown.TryUse(f.Conn.Clock.Elapsed.TotalSeconds + 10,
                new SingleUseCooldown(cooldown.TotalBaseMilliseconds, 0)));
        }
        f.Gate.Release();
        await use.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((280, 220), f.Conn.Walk.Destination); Assert.Equal(17, f.Conn.ActionCount);
        Assert.Single(f.Conn.SkillReadyAt); // Existing accepted-start cost/cooldown policy has no cancellation refund.
        await f.AssertPackets(InstanceProtocol.LifeUpdate(0x1000, 158, pool: 1));
    }

    [Fact]
    public async Task Confirmation_resamples_current_stopped_target_and_player_after_the_send_queue()
    {
        using var f = new Fixture();
        await f.Gate.WaitAsync();
        var use = f.Use();
        f.Conn.Walk.Teleport(285, 220, f.Conn.Clock.Elapsed.TotalSeconds);
        f.Target.Walk.Teleport(315, 220, f.Conn.Clock.Elapsed.TotalSeconds);
        f.Gate.Release();
        await use.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((322, 220), f.Conn.Walk.Destination);
        Assert.Equal(17, f.Conn.ActionCount);
        await f.AssertPackets(InstanceProtocol.LifeUpdate(0x1000, 158, pool: 1),
            InstanceProtocol.InstantAction(0x1000, f.Skill, 315, 220, targetObjectId: 42));
    }
}
