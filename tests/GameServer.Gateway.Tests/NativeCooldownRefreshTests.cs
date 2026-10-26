using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class NativeCooldownRefreshTests
{
    static readonly (ushort Id, int Effect)[] Eligible = [(0x103, 10619)];
    static (ClientState Conn, SingleUseCooldownState State, CooldownUseTicket Ticket) Spent()
    {
        var conn = new ClientState(new AreaLayout(1, "native-cooldown", 200, 200, 10, 10, null, []), 1, 1)
            { PlayerLife = 100, PlayerMana = 100, ClientSkillIds = [0x103] };
        var state = new SingleUseCooldownState(10, 1);
        Assert.True(state.TryUse(10, new(2500, 0)));
        conn.SingleUseCooldowns[10619] = state;
        return (conn, state, new(10619, conn.ActionCount, conn.ProjectileEpoch, false, State: state, UseVersion: state.UseVersion));
    }
    static byte[]? Snapshot(ClientState conn) => Program.NativeCooldownRefresh(conn, conn.ProjectileEpoch, () => Eligible);
    static float Float(byte[] bytes, int offset) => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset));

    [Fact]
    public void An_acknowledged_active_rate_change_sends_progress_once_without_paying_or_consuming_again()
    {
        var (conn, state, ticket) = Spent();
        Assert.True(ticket.CommitNativeUse(conn, 0x103));
        state.Refresh(10.5, 2);
        byte[] bytes = Assert.IsType<byte[]>(Snapshot(conn));
        Assert.Equal(2, Float(bytes, 29));
        Assert.Equal(2.5f, Float(bytes, 33));
        Assert.Equal(2, Float(bytes, 37));
        Assert.Equal(100u, conn.PlayerMana);
        Assert.Equal(0, conn.ActionCount);
        Assert.True(ticket.TryExecute(conn, 10619));
        Assert.False(ticket.TryExecute(conn, 10619));
        Assert.Null(Snapshot(conn));
    }

    [Fact]
    public void Unacknowledged_unchanged_and_ineligible_states_cannot_fabricate_native_entries()
    {
        var (conn, state, ticket) = Spent();
        Assert.Null(Snapshot(conn));
        state.Refresh(10.5, 2);
        Assert.Null(Snapshot(conn)); // Reserved while its original echo is still queued/cancelled.
        Assert.True(ticket.CommitNativeUse(conn, 0x103));
        Assert.Null(Program.NativeCooldownRefresh(conn, conn.ProjectileEpoch, () => []));
        conn.ClientSkillIds.Clear();
        Assert.Null(Snapshot(conn)); // A skill removed while queued cannot have an old entry restored.
        conn.ClientSkillIds.Add(0x103);
        Assert.NotNull(Snapshot(conn)); // Excluding the skill did not falsely acknowledge a packet.
        Assert.Null(Snapshot(conn));
    }

    [Fact]
    public void An_unchanged_active_rate_and_a_use_that_finished_before_the_rate_change_send_nothing()
    {
        var (conn, state, ticket) = Spent();
        ticket.CommitNativeUse(conn, 0x103);
        state.Refresh(10.5, 1);
        Assert.Null(Snapshot(conn));
        state.Refresh(13, 2);
        Assert.True(state.Ready);
        Assert.Null(Snapshot(conn));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Expiry_while_a_rate_change_is_queued_clears_the_native_entry_instead_of_restarting_it(bool roundtrip)
    {
        var (conn, state, ticket) = Spent();
        ticket.CommitNativeUse(conn, 0x103);
        state.Refresh(10.5, 2);
        if (roundtrip) state.Refresh(11, 1);
        state.Refresh(13, state.Rate);
        var bytes = Assert.IsType<byte[]>(Snapshot(conn));
        Assert.Equal(29, bytes.Length);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(25)));
        Assert.Null(Snapshot(conn));
    }

    [Fact]
    public void Paused_rate_changes_and_zero_to_positive_refresh_replace_one_entry_without_resetting_base_progress()
    {
        var (conn, state, ticket) = Spent();
        ticket.CommitNativeUse(conn, 0x103);
        conn.Clock.Stop();
        state.Refresh(10.5, 0);
        var stalled = Assert.IsType<byte[]>(Snapshot(conn));
        Assert.Equal(2, Float(stalled, 29));
        Assert.Equal(0, Float(stalled, 37));
        state.Refresh(100, 2); // Old zero rate made no progress, including across the pause.
        var resumed = Assert.IsType<byte[]>(Snapshot(conn));
        Assert.Equal(2, Float(resumed, 29));
        Assert.Equal(2.5f, Float(resumed, 33));
        Assert.Equal(2, Float(resumed, 37));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(resumed.AsSpan(25)));
        Assert.Null(Snapshot(conn));
    }

    [Fact]
    public void Stale_action_area_or_use_generations_cannot_acknowledge_or_refresh_a_new_use()
    {
        var (conn, state, ticket) = Spent();
        conn.ActionCount++;
        Assert.False(ticket.CommitNativeUse(conn, 0x103));
        state.Refresh(10.5, 2);
        Assert.Null(Snapshot(conn));
        state.Refresh(12, 2);
        Assert.True(state.TryUse(12, new(2500, 100)));
        Assert.False(ticket.Current(conn));
        Assert.False(ticket.CommitNativeUse(conn, 0x103));
        Assert.Null(Snapshot(conn));
        int epoch = conn.ProjectileEpoch;
        Assert.True(conn.TryBeginAreaChange(new(2, 0, false), out _));
        Assert.Null(Program.NativeCooldownRefresh(conn, epoch, () => Eligible));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queued_send_builds_and_encrypts_the_cooldown_snapshot_at_its_actual_gate(bool roundtrip)
    {
        var (conn, state, ticket) = Spent();
        ticket.CommitNativeUse(conn, 0x103);
        state.Refresh(10.5, 2);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var writer = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        await writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var reader = await accepting;
        var ciphers = new ConnectionCiphers(new byte[64], isClient: false);
        var gate = Program.SendGates.GetValue(ciphers, _ => new(1, 1));
        await gate.WaitAsync();
        bool built = false;
        var sending = Program.Send("cooldown-test", writer.GetStream(), ciphers, [], null, atGate: () =>
        {
            built = true;
            return Snapshot(conn);
        });
        Assert.False(built);
        if (roundtrip) state.Refresh(11, 1); // Equal rates do not imply aligned progress after 1 -> 2 -> 1.
        else state.Refresh(11.25, 2); // Advance while waiting: staged 2.0 s would rewind progress.
        gate.Release();
        await sending.WaitAsync(TimeSpan.FromSeconds(5));
        byte[] bytes = new byte[45];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await reader.GetStream().ReadExactlyAsync(bytes, timeout.Token);
        new ConnectionCiphers(new byte[64], isClient: true).Receive.Process(bytes);
        Assert.True(built);
        Assert.Equal(roundtrip ? 1 : .5f, Float(bytes, 29));
        Assert.Equal(2.5f, Float(bytes, 33));
        Assert.Equal(roundtrip ? 1 : 2, Float(bytes, 37));
        Assert.Null(Snapshot(conn));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rate_roundtrips_before_or_after_acknowledgement_retain_unsynchronized_progress(bool beforeAck)
    {
        var (conn, state, ticket) = Spent();
        if (!beforeAck) ticket.CommitNativeUse(conn, 0x103);
        state.Refresh(10.5, 2);
        state.Refresh(11, 1);
        if (beforeAck)
        {
            Assert.Null(Snapshot(conn));
            ticket.CommitNativeUse(conn, 0x103);
        }
        var bytes = Assert.IsType<byte[]>(Snapshot(conn));
        Assert.Equal(1, Float(bytes, 29));
        Assert.Equal(1, Float(bytes, 37));
        Assert.Null(Snapshot(conn));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Instant_acknowledgement_aligns_original_reservation_progress_even_without_a_rate_change(bool expired)
    {
        var (conn, state, ordinary) = Spent();
        var ticket = ordinary with { Instant = true };
        state.Refresh(expired ? 13 : 11, 1);
        Assert.Null(Snapshot(conn));
        Assert.True(ticket.CommitNativeUse(conn, 0x103));
        var bytes = Assert.IsType<byte[]>(Snapshot(conn));
        if (expired) Assert.Equal(29, bytes.Length);
        else
        {
            Assert.Equal(1.5f, Float(bytes, 29));
            Assert.Equal(2.5f, Float(bytes, 33));
            Assert.Equal(1, Float(bytes, 37));
        }
        Assert.Null(Snapshot(conn));
    }

    [Fact]
    public void Actual_gate_map_identity_and_death_prevent_refreshing_a_reused_skill_id_or_dead_player()
    {
        var (conn, state, ticket) = Spent();
        ticket.CommitNativeUse(conn, 0x103);
        state.Refresh(10.5, 2);
        (ushort Id, int Effect)[] currentMap = [(0x103, 10656)]; // The same id now belongs to Frostblink, not Dash.
        Assert.Null(Program.NativeCooldownRefresh(conn, conn.ProjectileEpoch, () =>
        {
            Assert.True(Monitor.IsEntered(conn.Monsters));
            return currentMap;
        }));
        Assert.True(state.NeedsNativeRefresh);
        currentMap = Eligible;
        conn.PlayerLife = 0;
        Assert.Null(Program.NativeCooldownRefresh(conn, conn.ProjectileEpoch,
            () => throw new Exception("A dead state may not resolve or synchronize skills")));
        conn.PlayerLife = 100;
        Assert.NotNull(Program.NativeCooldownRefresh(conn, conn.ProjectileEpoch, () => currentMap));
    }
}
