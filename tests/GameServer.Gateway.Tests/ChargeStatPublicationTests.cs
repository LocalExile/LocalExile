using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

[Collection("Ascendancy Gateway integration")]
public sealed class ChargeStatPublicationTests : IDisposable
{
    readonly List<string> characters = [];
    static readonly uint RetiredStat = Program.Stats.Key("global_cannot_crit")!.Value;
    const ushort RetiredSkill = 65_000;

    public ChargeStatPublicationTests() => TestSaves.Ensure();

    ClientState Character()
    {
        string name = "ChargePublish" + Guid.NewGuid().ToString("N");
        characters.Add(name);
        var conn = new ClientState(new(0, "charge publication", 128, 128, 32, 32, null, []), 0, 1)
        { Character = name, ClassRow = 1, PlayerLife = 50 };
        Program.ChargeStats(conn, []);
        Assert.True(Program.GrantCharges(conn, ChargeType.Power, 2, "publication control", new Dictionary<uint, int>()));
        Program.ChargeStats(conn, []);
        conn.SentStatKeys = [RetiredStat];
        conn.ClientSkillIds = [RetiredSkill];
        return conn;
    }

    static T Invoke<T>(string name, params object?[] args) => (T)typeof(Program)
        .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;

    static (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) Equip(ClientState conn, string name)
    {
        conn.God = true;
        conn.Pools = Invoke<CharacterStats.Pools>("PoolsFor", conn, 100);
        var gem = Program.Items.FindGem(name)!.Value;
        PlayerInventories.For(conn.Character!, Program.Items).Add(PlayerInventories.Helmet, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
            {
                Sockets = [new ItemSocket(SocketColour.Blue, ItemCatalog.Create(gem.Type) with { GemVariant = gem.Variant.Index })],
                Links = [0],
            });
        return Invoke<IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>>(
            "GemSkillEffects", conn).Single(g => g.Name == name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expiry_at_the_actual_cipher_gate_preserves_clears_and_removals_until_a_fresh_retry(bool actor)
    {
        var conn = Character();
        var publication = Assert.IsType<Program.ChargePublication>(Program.PrepareChargePublication(conn));
        double at = conn.Clock.Elapsed.TotalSeconds;
        bool Current() => Program.ChargePublicationCurrent(conn, publication, at);
        Assert.True(Current());
        await using var wire = await Wire.Create();
        var gate = Program.SendGates.GetValue(wire.Sending, _ => new(1, 1));
        await gate.WaitAsync(wire.Timeout.Token);
        Task pending = actor
            ? Program.SendSkillList("charge-publication", wire.Stream, wire.Sending, conn, Current)
            : Program.SendPlayerStats("charge-publication", wire.Stream, wire.Sending, conn, null, Current);
        Assert.False(pending.IsCompleted);
        Assert.Equal(new uint[] { RetiredStat }, conn.SentStatKeys);
        Assert.Equal(new ushort[] { RetiredSkill }, conn.ClientSkillIds);

        // Advance only the charge publication's injected game boundary; no sleeps or process clocks are changed.
        at += Charges.BaseDurationMs / 1000.0 + 1;
        gate.Release();
        await pending.WaitAsync(wire.Timeout.Token);
        if (actor) Assert.Null(await (Task<Program.SkillListPublication?>)pending);
        else Assert.False(await (Task<bool>)pending);
        Assert.Equal(new uint[] { RetiredStat }, conn.SentStatKeys);
        Assert.Equal(new ushort[] { RetiredSkill }, conn.ClientSkillIds);

        // This control packet also proves the skipped snapshot consumed no cipher bytes.
        byte[] control = InstanceProtocol.LifeUpdate(0x1000, 50);
        await Program.Send("charge-publication", wire.Stream, wire.Sending, [.. control], null);
        publication = Assert.IsType<Program.ChargePublication>(Program.PrepareChargePublication(conn));
        byte[] expected;
        if (actor)
        {
            var skills = Invoke<IReadOnlyList<GrantedSkill>>("PlayerSkills", conn);
            expected = InstanceEntities.PlayerSkillsChange(0x1000, skills, [RetiredSkill]);
            var accepted = Assert.IsType<Program.SkillListPublication>(
                await Program.SendSkillList("charge-publication", wire.Stream, wire.Sending, conn, Current));
            Assert.Equal(new ushort[] { RetiredSkill }, accepted.Gone);
            Assert.DoesNotContain(RetiredSkill, conn.ClientSkillIds);
            Assert.Equal(skills.Select(s => s.Id).Order(), conn.ClientSkillIds.Order());
            Assert.Equal(new uint[] { RetiredStat }, conn.SentStatKeys);
        }
        else
        {
            var stats = Invoke<IEnumerable<(uint, int)>>("FinalPlayerStats", conn).ToList();
            expected = InstanceEntities.StatsUpdate(0x1000, [.. stats, (RetiredStat, 0)]);
            Assert.True(await Program.SendPlayerStats("charge-publication", wire.Stream, wire.Sending, conn, null, Current));
            Assert.DoesNotContain(RetiredStat, conn.SentStatKeys);
            Assert.Equal(stats.Select(s => s.Item1).Order(), conn.SentStatKeys.Order());
            Assert.Equal(new ushort[] { RetiredSkill }, conn.ClientSkillIds);
        }
        Assert.True(Program.CompleteChargePublication(conn, publication, accepted: true));
        byte[] actual = await wire.Finish();
        Assert.Equal((byte[])[.. control, .. expected], actual);
    }

    [Fact]
    public async Task Character_refresh_stops_after_a_rejected_stat_snapshot_without_publishing_an_Actor_record()
    {
        var conn = Character();
        var publication = Assert.IsType<Program.ChargePublication>(Program.PrepareChargePublication(conn));
        double at = conn.Clock.Elapsed.TotalSeconds;
        await using var wire = await Wire.Create();
        var gate = Program.SendGates.GetValue(wire.Sending, _ => new(1, 1));
        await gate.WaitAsync(wire.Timeout.Token);
        var pending = Program.SendCharacterStats("charge-publication", wire.Stream, wire.Sending, conn,
            "charge boundary", () => Program.ChargePublicationCurrent(conn, publication, at));
        Assert.False(pending.IsCompleted);
        at += Charges.BaseDurationMs / 1000.0 + 1;
        gate.Release();
        Assert.False(await pending.WaitAsync(wire.Timeout.Token));
        Assert.False(Program.CompleteChargePublication(conn, publication, accepted: false));
        Assert.Equal(new uint[] { RetiredStat }, conn.SentStatKeys);
        Assert.Equal(new ushort[] { RetiredSkill }, conn.ClientSkillIds);
        Assert.Empty(await wire.Finish());
    }

    [Fact]
    public async Task Energy_shield_expiry_rejection_preserves_the_sent_ledger_and_fresh_retry_publishes_current_recovery()
    {
        var conn = Character();
        Program.ChargePublication Prepare() => Assert.IsType<Program.ChargePublication>(Program.PrepareChargePublication(conn,
            pools: (c, _) => c.Pools with { EnergyShield = 100 }));
        var publication = Prepare();
        conn.EnergyShield.SetMaximum(100, refill: true);
        conn.EnergyShield.Damage(40, bypass: false, conn.Clock.Elapsed.TotalSeconds);
        uint regenKey = Program.Stats.Key("base_energy_shield_regeneration_rate_per_minute")!.Value;
        conn.PlayerStats[regenKey] = 60;
        conn.SentEnergyShield = 17;
        conn.SentRechargeActive = true;
        conn.SentEsRecoveryRate = 7;
        double at = conn.Clock.Elapsed.TotalSeconds;
        bool Current() => Program.ChargePublicationCurrent(conn, publication, at);
        await using var wire = await Wire.Create();
        var gate = Program.SendGates.GetValue(wire.Sending, _ => new(1, 1));
        await gate.WaitAsync(wire.Timeout.Token);
        var pending = Invoke<Task<bool>>("SendPlayerEnergyShield", "charge-publication", wire.Stream,
            wire.Sending, conn, (Func<bool>)Current);
        Assert.False(pending.IsCompleted);
        at += Charges.BaseDurationMs / 1000.0 + 1;
        gate.Release();
        Assert.False(await pending.WaitAsync(wire.Timeout.Token));
        Assert.Equal(17u, conn.SentEnergyShield);
        Assert.True(conn.SentRechargeActive);
        Assert.Equal(7, conn.SentEsRecoveryRate);
        Assert.Equal(60u, conn.EnergyShield.Current);

        byte[] control = InstanceProtocol.LifeUpdate(0x1000, 50);
        await Program.Send("charge-publication", wire.Stream, wire.Sending, [.. control], null);
        publication = Prepare();
        conn.PlayerStats[regenKey] = 120;
        Assert.True(await Invoke<Task<bool>>("SendPlayerEnergyShield", "charge-publication", wire.Stream,
            wire.Sending, conn, (Func<bool>)Current));
        Assert.Equal(60u, conn.SentEnergyShield);
        Assert.False(conn.SentRechargeActive);
        Assert.Equal(120, conn.SentEsRecoveryRate);
        // A valid unchanged refresh succeeds without sending or consuming a duplicate packet.
        Assert.True(await Invoke<Task<bool>>("SendPlayerEnergyShield", "charge-publication", wire.Stream,
            wire.Sending, conn, (Func<bool>)Current));
        byte[] expected = [.. control, .. InstanceProtocol.LifeUpdate(0x1000, 60, pool: 2),
            .. InstanceEntities.StatsUpdate(0x1000, [(Program.Stats.Key("energy_shield_recovery_per_minute")!.Value, 120)])];
        Assert.Equal(expected, await wire.Finish());
    }

    [Fact]
    public async Task Valid_energy_shield_publication_samples_the_recharge_phase_at_the_gate()
    {
        var conn = Character();
        var publication = Assert.IsType<Program.ChargePublication>(Program.PrepareChargePublication(conn,
            pools: (c, _) => c.Pools with { EnergyShield = 100 }));
        conn.EnergyShield.SetMaximum(100, refill: true);
        double hitAt = conn.Clock.Elapsed.TotalSeconds;
        conn.EnergyShield.Damage(40, bypass: false, hitAt);
        conn.PlayerStats[Program.Stats.Key("base_energy_shield_regeneration_rate_per_minute")!.Value] = 60;
        conn.SentEnergyShield = 60;
        conn.SentRechargeActive = false;
        conn.SentEsRecoveryRate = 60;
        bool Current() => Program.ChargePublicationCurrent(conn, publication, hitAt);
        await using var wire = await Wire.Create();
        var gate = Program.SendGates.GetValue(wire.Sending, _ => new(1, 1));
        await gate.WaitAsync(wire.Timeout.Token);
        var pending = Invoke<Task<bool>>("SendPlayerEnergyShield", "charge-publication", wire.Stream,
            wire.Sending, conn, (Func<bool>)Current);
        Assert.False(pending.IsCompleted);
        Assert.False(conn.EnergyShield.Recharging);

        // Recharge starts without replacing Pools or the charge revision. The admitted packet must choose its
        // recovery rate from this new phase, not the regeneration-only phase captured before waiting.
        int Get(string name) => name == "base_energy_shield_regeneration_rate_per_minute" ? 60 : 0;
        conn.EnergyShield.Advance(hitAt + 2, hitAt + 2.1, Get, alive: true);
        Assert.True(conn.EnergyShield.Recharging);
        Assert.Equal(63u, conn.EnergyShield.Current);
        Assert.True(Current());
        gate.Release();
        Assert.True(await pending.WaitAsync(wire.Timeout.Token));
        Assert.True(conn.SentRechargeActive);
        Assert.Equal(2060, conn.SentEsRecoveryRate); // 60 regen +100 maximum *20 recharge per minute.
        byte[] expected = [.. InstanceProtocol.LifeUpdate(0x1000, 63, pool: 2),
            .. InstanceEntities.StatsUpdate(0x1000, [(Program.Stats.Key("energy_shield_recovery_per_minute")!.Value, 2060)])];
        Assert.Equal(expected, await wire.Finish());
    }

    [Fact]
    public async Task Rejected_Righteous_Fire_resize_keeps_radius_ring_and_object_id_then_fresh_retry_replaces_once()
    {
        var conn = Character();
        var gem = Equip(conn, "Righteous Fire");
        conn.PlayerStats[Program.Stats.Key("skill_area_of_effect_+%_per_power_charge")!.Value] = 50;
        var publication = Assert.IsType<Program.ChargePublication>(Program.PrepareChargePublication(conn));
        var rf = new RighteousFireState(7, 8, 0x7000, gem.Id, 4200, 5400, 1)
        {
            RingArt = Program.RighteousFireRingAo, RingCreated = true, RingAt = (5, 6),
            EnergyShieldBurnPctPerMinute = 123, EnergyShieldDamagePctPerMinute = 456,
        };
        conn.RighteousFire = rf;
        conn.NextObjectId = 0x8000;
        double at = conn.Clock.Elapsed.TotalSeconds;
        bool Current() => Program.ChargePublicationCurrent(conn, publication, at);
        await using var wire = await Wire.Create();
        var gate = Program.SendGates.GetValue(wire.Sending, _ => new(1, 1));
        await gate.WaitAsync(wire.Timeout.Token);
        var pending = Invoke<Task>("RefreshRighteousFireStats", "charge-publication", wire.Stream,
            wire.Sending, conn, (Func<bool>)Current);
        Assert.False(pending.IsCompleted);
        at += Charges.BaseDurationMs / 1000.0 + 1;
        gate.Release();
        await pending.WaitAsync(wire.Timeout.Token);
        Assert.Same(rf, conn.RighteousFire);
        Assert.Equal(1, rf.Radius);
        Assert.Equal(0x7000u, rf.Ring);
        Assert.Equal((5, 6), rf.RingAt);
        Assert.Equal(0x8000u, conn.NextObjectId);
        Assert.Equal(123, rf.EnergyShieldBurnPctPerMinute);
        Assert.Equal(456, rf.EnergyShieldDamagePctPerMinute);

        byte[] control = InstanceProtocol.LifeUpdate(0x1000, 50);
        await Program.Send("charge-publication", wire.Stream, wire.Sending, [.. control], null);
        publication = Assert.IsType<Program.ChargePublication>(Program.PrepareChargePublication(conn));
        await Invoke<Task>("RefreshRighteousFireStats", "charge-publication", wire.Stream,
            wire.Sending, conn, (Func<bool>)Current);
        Assert.Equal(31, rf.Radius); // level-one base18, /god's200% area; expired Power adds no remaining area.
        Assert.Equal(0x8000u, rf.Ring);
        Assert.Equal(0x8001u, conn.NextObjectId);
        Assert.Equal(4200, rf.EnergyShieldBurnPctPerMinute);
        Assert.Equal(4200, rf.EnergyShieldDamagePctPerMinute);
        var here = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        Assert.Equal(here, rf.RingAt);
        float scale = (float)(31 * (250.0 / 23) / (Program.Art.ArtRadius(Program.RighteousFireRingAo) ?? 400));
        var effect = new EffectObject(here.X, here.Y, Program.RighteousFireRingAo)
            { Flags0 = EffectObject.FixedRotation, Scale = scale };
        byte[] expected = [.. control, .. InstanceProtocol.RemoveObject(0x7000),
            .. InstanceEntities.CreateObject(0x8000, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), effect.Blob()),
            .. InstanceProtocol.EnterWakeZone(0x8000)];
        await Invoke<Task>("RefreshRighteousFireStats", "charge-publication", wire.Stream,
            wire.Sending, conn, (Func<bool>)Current);
        Assert.Equal(0x8001u, conn.NextObjectId); // unchanged radius cannot allocate or publish another ring.
        Assert.Equal(expected, await wire.Finish());
    }

    [Fact]
    public async Task Rejected_cooldown_refresh_keeps_its_dirty_native_progress_for_a_valid_retry()
    {
        var conn = Character();
        var gem = Equip(conn, "Dash");
        var publication = Assert.IsType<Program.ChargePublication>(Program.PrepareChargePublication(conn));
        double usedAt = conn.Clock.Elapsed.TotalSeconds;
        var state = new SingleUseCooldownState(usedAt, 1);
        Assert.True(state.TryUse(usedAt, new(2500, 0)));
        conn.SingleUseCooldowns[gem.Effect] = state;
        conn.ClientSkillIds.Add(gem.Id);
        var ticket = new CooldownUseTicket(gem.Effect, conn.ActionCount, conn.ProjectileEpoch, false,
            State: state, UseVersion: state.UseVersion);
        Assert.True(ticket.CommitNativeUse(conn, gem.Id));
        state.Refresh(usedAt + .25, 2);
        Assert.True(state.NeedsNativeRefresh);
        double at = conn.Clock.Elapsed.TotalSeconds;
        bool Current() => Program.ChargePublicationCurrent(conn, publication, at);
        await using var wire = await Wire.Create();
        var gate = Program.SendGates.GetValue(wire.Sending, _ => new(1, 1));
        await gate.WaitAsync(wire.Timeout.Token);
        var pending = Invoke<Task>("SendNativeCooldownRefresh", "charge-publication", wire.Stream,
            wire.Sending, conn, (Func<bool>)Current);
        Assert.False(pending.IsCompleted);
        at += Charges.BaseDurationMs / 1000.0 + 1;
        gate.Release();
        await pending.WaitAsync(wire.Timeout.Token);
        Assert.True(state.NeedsNativeRefresh);
        Assert.Equal(2250, state.RemainingBaseMilliseconds);

        byte[] control = InstanceProtocol.LifeUpdate(0x1000, 50);
        await Program.Send("charge-publication", wire.Stream, wire.Sending, [.. control], null);
        publication = Assert.IsType<Program.ChargePublication>(Program.PrepareChargePublication(conn));
        await Invoke<Task>("SendNativeCooldownRefresh", "charge-publication", wire.Stream,
            wire.Sending, conn, (Func<bool>)Current);
        Assert.False(state.NeedsNativeRefresh);
        byte[] expected = [.. control,
            .. InstanceEntities.PlayerSingleUseCooldowns(0x1000, [new(gem.Id, 2.25f, 2.5f, 2)])];
        Assert.Equal(expected, await wire.Finish());
    }

    sealed class Wire : IAsyncDisposable
    {
        readonly TcpClient writer, reader;
        readonly MemoryStream capture = new();
        readonly Task reading;
        readonly ConnectionCiphers receiving;
        bool finished;
        public CancellationTokenSource Timeout { get; } = new(TimeSpan.FromSeconds(10));
        public ConnectionCiphers Sending { get; }
        public NetworkStream Stream => writer.GetStream();

        Wire(TcpClient writer, TcpClient reader)
        {
            this.writer = writer;
            this.reader = reader;
            byte[] key = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
            Sending = new(key, isClient: false);
            receiving = new(key, isClient: true);
            reading = reader.GetStream().CopyToAsync(capture, Timeout.Token);
        }

        public static async Task<Wire> Create()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var reader = new TcpClient();
            var accept = listener.AcceptTcpClientAsync();
            await reader.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
            return new(await accept, reader);
        }

        public async Task<byte[]> Finish()
        {
            writer.Client.Shutdown(SocketShutdown.Send);
            await reading.WaitAsync(Timeout.Token);
            finished = true;
            byte[] bytes = capture.ToArray();
            receiving.Receive.Process(bytes);
            return bytes;
        }

        public async ValueTask DisposeAsync()
        {
            if (!finished)
            {
                Timeout.Cancel();
                try { await reading; }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
            }
            writer.Dispose(); reader.Dispose(); capture.Dispose(); Timeout.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (string character in characters) PlayerInventories.Forget(character);
    }
}
