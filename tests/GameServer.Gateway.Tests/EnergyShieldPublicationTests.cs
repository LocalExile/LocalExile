using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public sealed class EnergyShieldPublicationTests
{
    static uint Key(string name) => Program.Stats.Key(name)!.Value;
    static byte[] Resource(uint current) => InstanceProtocol.LifeUpdate(Program.PlayerId, current, pool: 2);
    static byte[] Prediction(int rate) => InstanceEntities.StatsUpdate(Program.PlayerId,
        [(Key("energy_shield_recovery_per_minute"), rate)]);

    sealed class EsContact : IDisposable
    {
        static readonly MethodInfo Sender = typeof(Program).GetMethod("SendPlayerEnergyShield",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(), reader;
        readonly ConnectionCiphers ciphers = new(new byte[64], isClient: false);
        readonly ConnectionCiphers received = new(new byte[64], isClient: true);
        readonly string name = "EsPublication" + Guid.NewGuid().ToString("N");
        public ClientState Conn { get; }
        public SemaphoreSlim Gate => Program.SendGates.GetValue(ciphers, _ => new(1, 1));

        public EsContact()
        {
            TestSaves.Ensure();
            CharacterSaves.SetExperience(name, 0); // No starter equipment or unrelated recovery modifiers.
            Conn = new(new(ushort.MaxValue, "EsPublication", 128, 128, 32, 32, null, []), 0, 1)
            { Character = name, ClassRow = SkillCatalog.WitchRow, PlayerLife = 50, PlayerSent = true };
            Conn.Pools = Program.PoolsFor(Conn, 1);
            SetMaximum(137, refill: true);
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }

        public void SetMaximum(uint maximum, bool refill = false)
        {
            Conn.Pools = Conn.Pools with { EnergyShield = maximum };
            if (refill) Conn.EnergyShield.SetMaximum(maximum, refill: true);
        }

        public void StartRecharge()
        {
            Assert.Equal(0, Conn.EnergyShield.Damage(1, bypass: false, now: 0));
            Conn.EnergyShield.Advance(1.99, 2.01, _ => 0, alive: true);
            Assert.Equal(136u, Conn.EnergyShield.Current);
            Assert.True(Conn.EnergyShield.Recharging);
        }

        public Task SendAsync() => ((Task)Sender.Invoke(null,
            ["es-publication-test", writer.GetStream(), ciphers, Conn])!).WaitAsync(TimeSpan.FromSeconds(5));

        public async Task ReadPacketsAsync(params byte[][] packets)
        {
            byte[] expected = packets.SelectMany(p => p).ToArray();
            byte[] actual = new byte[expected.Length];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await reader.GetStream().ReadExactlyAsync(actual, timeout.Token);
            received.Receive.Process(actual);
            Assert.Equal(expected, actual);
        }

        public async Task BarrierAsync(params byte[][] packetsBeforeBarrier)
        {
            // A following valid packet proves suppression without a timeout or a network-availability race.
            byte[] barrier = InstanceProtocol.LifeUpdate(Program.PlayerId, 17, pool: Program.ManaPool);
            await Program.Send("es-publication-barrier", writer.GetStream(), ciphers, barrier.ToArray(), null);
            await ReadPacketsAsync([.. packetsBeforeBarrier, barrier]);
        }

        public void Dispose()
        {
            Conn.Closed = true;
            writer.Dispose(); reader.Dispose(); listener.Stop();
            PlayerInventories.Forget(name);
            Program.InactiveItems.TryRemove(name, out _);
            Program.GemSkillIdsOf.TryRemove(name, out _);
        }
    }

    static void AssertSent(ClientState conn, uint? current, bool? active, int? rate)
    {
        Assert.Equal(current, conn.SentEnergyShield);
        Assert.Equal(active, conn.SentRechargeActive);
        Assert.Equal(rate, conn.SentEsRecoveryRate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_ES_samples_clamped_current_and_cleared_prediction_even_if_old_state_was_already_sent(
        bool oldStateAlreadySent)
    {
        using var contact = new EsContact();
        var conn = contact.Conn;
        contact.StartRecharge();
        conn.SentEnergyShield = oldStateAlreadySent ? 136u : 120u;
        conn.SentRechargeActive = oldStateAlreadySent;
        conn.SentEsRecoveryRate = oldStateAlreadySent ? 2740 : 111;
        await contact.Gate.WaitAsync();
        Task pending;
        try
        {
            pending = contact.SendAsync();
            Assert.False(pending.IsCompleted);
            AssertSent(conn, oldStateAlreadySent ? 136u : 120u, oldStateAlreadySent,
                oldStateAlreadySent ? 2740 : 111);
            lock (conn.Monsters) contact.SetMaximum(126);
            Assert.Equal(126u, conn.EnergyShield.Current);
            Assert.False(conn.EnergyShield.Recharging);
        }
        finally { contact.Gate.Release(); }
        await pending;
        await contact.ReadPacketsAsync(Resource(126), Prediction(0));
        AssertSent(conn, 126, false, 0);
    }

    [Fact]
    public async Task Queued_ES_reads_recovery_sources_after_acquiring_the_send_gate()
    {
        using var contact = new EsContact();
        var conn = contact.Conn;
        await contact.Gate.WaitAsync();
        Task pending;
        try
        {
            pending = contact.SendAsync();
            Assert.False(pending.IsCompleted);
            AssertSent(conn, null, null, null);
            lock (conn.Monsters) conn.PlayerStats[Key("base_energy_shield_regeneration_rate_per_minute")] = 60;
        }
        finally { contact.Gate.Release(); }
        await pending;
        await contact.ReadPacketsAsync(Resource(137), Prediction(60));
        AssertSent(conn, 137, false, 60);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_ES_applies_the_current_alive_or_dead_state_at_the_send_gate(bool deadAtSend)
    {
        using var contact = new EsContact();
        var conn = contact.Conn;
        conn.PlayerStats[Key("base_energy_shield_regeneration_rate_per_minute")] = 60;
        conn.PlayerLife = deadAtSend ? 50u : 0u;
        await contact.Gate.WaitAsync();
        Task pending;
        try
        {
            pending = contact.SendAsync();
            Assert.False(pending.IsCompleted);
            Assert.Equal(137u, conn.EnergyShield.Current); // A queued dead snapshot must not empty a later live one.
            AssertSent(conn, null, null, null);
            lock (conn.Monsters) conn.PlayerLife = deadAtSend ? 0u : 50u;
        }
        finally { contact.Gate.Release(); }
        await pending;
        uint current = deadAtSend ? 0u : 137u;
        int rate = deadAtSend ? 0 : 60;
        await contact.ReadPacketsAsync(Resource(current), Prediction(rate));
        Assert.Equal(current, conn.EnergyShield.Current);
        AssertSent(conn, current, false, rate);
    }

    [Fact]
    public async Task Two_queued_ES_publications_deduplicate_at_the_gate_and_unchanged_repeats_emit_nothing()
    {
        using var contact = new EsContact();
        await contact.Gate.WaitAsync();
        Task first, second;
        try
        {
            first = contact.SendAsync();
            second = contact.SendAsync();
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            AssertSent(contact.Conn, null, null, null);
        }
        finally { contact.Gate.Release(); }
        await Task.WhenAll(first, second);
        await contact.BarrierAsync(Resource(137), Prediction(0));
        AssertSent(contact.Conn, 137, false, 0);
        await contact.SendAsync();
        await contact.BarrierAsync();
        AssertSent(contact.Conn, 137, false, 0);
    }

    [Fact]
    public async Task ES_current_changes_publish_without_redundant_prediction_and_a_larger_maximum_does_not_refill()
    {
        using var contact = new EsContact();
        var conn = contact.Conn;
        await contact.SendAsync();
        await contact.ReadPacketsAsync(Resource(137), Prediction(0));
        Assert.Equal(0, conn.EnergyShield.Damage(1, bypass: false, now: 0));
        contact.SetMaximum(148);
        await contact.SendAsync();
        await contact.BarrierAsync(Resource(136));
        Assert.Equal(148u, conn.EnergyShield.Maximum);
        Assert.Equal(136u, conn.EnergyShield.Current);
        AssertSent(conn, 136, false, 0);
    }
}
