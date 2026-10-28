using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

[CollectionDefinition("Actor scale", DisableParallelization = true)]
public class ActorScaleCollection;

[Collection("Actor scale")]
public sealed class ActorScaleGatewayTests : IDisposable
{
    readonly List<string> names = [];
    static MethodInfo Method(string name) => typeof(Program).GetMethod(name,
        BindingFlags.Static | BindingFlags.NonPublic)!;
    static T Call<T>(string name, params object?[] args) => (T)Method(name).Invoke(null, args)!;
    static void Publish(ClientState conn) => Method("PublishPlayerPose").Invoke(null, [conn]);
    static int Scale(ClientState conn) => Program.CharacterPlayerStats(conn)[1973];
    static List<(uint, int)> Refresh(ClientState conn) => Call<List<(uint, int)>>("PlayerStatsWithCleared", conn);

    ClientState Character(InstanceState? instance = null)
    {
        TestSaves.Ensure();
        string name = "Scale" + Guid.NewGuid().ToString("N"); names.Add(name);
        var conn = new ClientState(new(ushort.MaxValue, "ScaleTest", 128, 128, 32, 32, null, []), 0, 1)
        { Character = name, ClassRow = 1, Instance = instance ?? new(), PlayerLife = 100, PlayerSent = true,
            WornCosmetics = [] };
        conn.Pools = Program.PoolsFor(conn, 100);
        return conn;
    }

    static void Wear(ClientState conn, int value, byte slot = 0)
    {
        var record = Program.Mtx.Owned.First(r =>
            Program.MtxEffects.Of(r.MtxTypeHash)?.Stats.Contains((720u, value)) == true);
        conn.WornCosmetics!.Add(new(slot, record.Id, record.MtxTypeHash));
    }

    static Item SizeItem(string type, int value) => ItemCatalog.Create(Program.Items.Find(type)!) with
    { Mods = [Program.Items.Mods.ById("ActorSizeUniqueAmulet2")!.With(ModSection.Explicit, [value], Random.Shared)] };

    [Fact]
    public void Real_passive_item_and_cosmetics_add_then_remove_without_feedback_or_precedence_changes()
    {
        var conn = Character();
        conn.Passives.Add(49937); // Domain fixture only; does not admit a Phrecia class.
        var items = PlayerInventories.For(conn.Character!, Program.Items);
        var amulet = items.Add(5, 0, 0, SizeItem("Amber Amulet", 20));
        Wear(conn, 5); Wear(conn, 3, 1);
        Assert.Equal(38, Scale(conn)); Assert.Equal(38, Scale(conn));
        Assert.False(conn.PlayerStats.ContainsKey(720)); Assert.False(conn.PlayerStats.ContainsKey(1973));
        conn.PlayerStats[720] = 0;
        Assert.Equal(0, Scale(conn));
        conn.PlayerStats.Remove(720);
        conn.PlayerStats[1973] = 777;
        Assert.Equal(777, Scale(conn));
        conn.PlayerStats[1973] = 0;
        Assert.Equal(0, Scale(conn));
        conn.PlayerStats.Remove(1973);
        conn.WornCosmetics!.RemoveAt(1); Assert.Equal(35, Scale(conn));
        conn.WornCosmetics.Clear(); Assert.Equal(30, Scale(conn));
        items.Remove(5, amulet.Id); Assert.Equal(10, Scale(conn));
        conn.Passives.Clear(); Assert.Equal(0, Scale(conn));
        Assert.Equal((1973u, 0), Assert.Single(Refresh(conn), s => s.Item1 == 1973));
        Assert.Equal(0, Scale(Character()));
    }

    [Fact]
    public void Player_default_limit_differs_from_present_zero_and_flask_inputs_join_before_dev_replacement()
    {
        var conn = Character();
        Wear(conn, 5);
        conn.PlayerStats[720] = 110;
        Assert.Equal(100, Scale(conn));
        conn.PlayerStats[1975] = 0; Assert.Equal(110, Scale(conn));
        conn.PlayerStats.Remove(1975); Assert.Equal(100, Scale(conn));
        conn.PlayerStats.Remove(720);
        var flask = new UtilityFlasks.Utility(0, 0, "size-test", [("base_actor_scale_+%", 7)], 5, 0, 0);
        Assert.Equal(UtilityFlasks.Support.Modelled, UtilityFlasks.SupportOf("base_actor_scale_+%"));
        conn.UtilityFlaskEffects.Start(new(1, 0, "size-test", flask, 0, null,
            [new("size-test", "base_actor_scale_+%", 7)], 0, 5));
        Assert.Equal(12, Scale(conn));
        conn.PlayerStats[720] = 0; Assert.Equal(0, Scale(conn));
        conn.PlayerStats.Remove(720);
        conn.UtilityFlaskEffects.End(6, false, out _); Assert.Equal(5, Scale(conn));
        conn.WornCosmetics!.Clear(); Assert.Equal(0, Scale(conn));
    }

    [Fact]
    public void Requirements_weapon_swap_and_level_changes_use_current_active_global_sources()
    {
        var conn = Character();
        var items = PlayerInventories.For(conn.Character!, Program.Items);
        var weapon = items.Add(PlayerInventories.Weapon1, 0, 0, SizeItem("Driftwood Wand", 20));
        Assert.Equal(20, Scale(conn));
        items.SwapWeaponSets(); Assert.Equal(0, Scale(conn));
        items.SwapWeaponSets(); Assert.Equal(20, Scale(conn));
        items.Remove(PlayerInventories.Weapon1, weapon.Id);
        items.Add(PlayerInventories.BodyArmour, 0, 0, SizeItem("Glorious Plate", 20));
        conn.Pools = Program.PoolsFor(conn, 100);
        Assert.Equal(0, Scale(conn)); // Ordinary class strength fails the actual item requirement.
        conn.God = true; conn.Pools = Program.PoolsFor(conn, 100);
        Assert.Equal(20, Scale(conn));
        conn.Pools = Program.PoolsFor(conn, 1); Assert.Equal(0, Scale(conn));
        conn.Pools = Program.PoolsFor(conn, 100); Assert.Equal(20, Scale(conn));
        conn.God = false; conn.Pools = Program.PoolsFor(conn, 100); Assert.Equal(0, Scale(conn));
    }

    [Theory]
    [InlineData(60, -50, 0, 50, 50)]
    [InlineData(60, -50, 5, 50, 50)]
    [InlineData(110, 0, 5, 100, 100)]
    public void Actual_builders_compose_metadata_ordinary_limits_and_cosmetics_then_replace_with_dev_zero(
        int raw, int ordinaryLimit, int cosmetic, int expectedLimit, int expectedScale)
    {
        var conn = Character();
        PlayerInventories.For(conn.Character!, Program.Items).Add(5, 0, 0, SizeItem("Amber Amulet", raw));
        if (cosmetic != 0) Wear(conn, cosmetic);
        const string limitName = "actor_scale_+%_limit";
        var flask = new UtilityFlasks.Utility(0, 0, "limit-test", [(limitName, ordinaryLimit)], 5, 0, 0);
        conn.UtilityFlaskEffects.Start(new(1, 0, "limit-test", flask, 0, null,
            [new("limit-test", limitName, ordinaryLimit)], 0, 5));
        void AssertBuilders(int limit, int scale)
        {
            var snapshots = new[]
            {
                Program.CharacterPlayerStats(conn),
                Refresh(conn).ToDictionary(s => s.Item1, s => s.Item2),
                Call<PlayerObject>("PlayerForCreation", conn).Stats!
                    .Where(s => s.Key is 1973 or 1975).ToDictionary(s => s.Key, s => s.Value),
                Program.CharacterPlayerStats(conn),
            };
            foreach (var snapshot in snapshots)
            {
                Assert.Equal(limit, snapshot[1975]);
                Assert.Equal(scale, snapshot[1973]);
            }
        }
        AssertBuilders(expectedLimit, expectedScale);
        conn.PlayerStats[1975] = 0;
        AssertBuilders(0, raw + cosmetic);
        conn.PlayerStats.Remove(1975);
        AssertBuilders(expectedLimit, expectedScale);
        conn.UtilityFlaskEffects.End(6, false, out _);
        AssertBuilders(100, Math.Min(raw + cosmetic, 100));
        Assert.False(conn.PlayerStats.ContainsKey(1973));
        Assert.False(conn.PlayerStats.ContainsKey(1975));
    }

    [Theory]
    [InlineData(int.MaxValue, int.MaxValue)]
    [InlineData(int.MinValue, -99)]
    public void Extreme_item_sources_saturate_when_combined_with_real_passive_and_cosmetic(int raw, int expected)
    {
        var conn = Character(); conn.Passives.Add(49937); Wear(conn, 5);
        var item = SizeItem("Amber Amulet", raw);
        PlayerInventories.For(conn.Character!, Program.Items).Add(5, 0, 0, item);
        conn.PlayerStats[1975] = 0;
        Assert.Equal(expected, Scale(conn)); Assert.Equal(expected, Scale(conn));
        Assert.Equal(raw, item.Mods.Single().Values.Single());
        Assert.False(conn.PlayerStats.ContainsKey(720));
    }

    // One loopback transport with persistent cipher state; no server/client executable or native session.
    sealed class Wire : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        TcpClient reader = null!;
        readonly ConnectionCiphers decrypt = new(new byte[64], true);
        public readonly ConnectionCiphers Ciphers = new(new byte[64], false);
        public NetworkStream Stream => writer.GetStream();
        public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));
        public static async Task<Wire> Open()
        {
            var wire = new Wire(); wire.listener.Start();
            var accept = wire.listener.AcceptTcpClientAsync();
            await wire.writer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)wire.listener.LocalEndpoint).Port);
            wire.reader = await accept;
            return wire;
        }
        public Task Invoke(string method, ClientState conn, params object?[] tail) =>
            Call<Task>(method, ["actor-scale-test", Stream, Ciphers, conn, .. tail]);
        public async Task<byte[]> Read()
        {
            // A sentinel sent through the same gate makes collection independent of TCP packet boundaries/timing.
            byte[] sentinel = new LoginWriter().U16(0x7ffe).U32(0x12345678).ToArray();
            await Program.Send("actor-scale-test", Stream, Ciphers, sentinel.ToArray(), null);
            var result = new List<byte>(); var buffer = new byte[4096];
            while (true)
            {
                int n = await reader.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(n > 0);
                byte[] chunk = buffer[..n]; decrypt.Receive.Process(chunk); result.AddRange(chunk);
                if (result.Count >= sentinel.Length && result.TakeLast(sentinel.Length).SequenceEqual(sentinel))
                    return result.Take(result.Count - sentinel.Length).ToArray();
            }
        }
        public void Dispose() { reader.Dispose(); writer.Dispose(); listener.Dispose(); }
    }

    static uint Compact(byte[] data, ref int at, bool signed = false)
    {
        byte first = data[at++];
        int extra = first < 0x80 ? 0 : first < 0xc0 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
        int bits = extra == 4 ? 32 : 7 * (extra + 1);
        uint value = extra == 4 ? 0u : (uint)(first & (0x7f >> extra));
        for (int i = 0; i < extra; i++) value = (value << 8) | data[at++];
        if (signed && bits < 32 && (value & (1u << (bits - 1))) != 0) value |= uint.MaxValue << bits;
        return value;
    }
    static List<(uint, int)> StatsAt(byte[] bytes, int at)
    {
        int count = (int)Compact(bytes, ref at); var stats = new List<(uint, int)>();
        for (int i = 0; i < count; i++) stats.Add((Compact(bytes, ref at), unchecked((int)Compact(bytes, ref at, true))));
        return stats;
    }
    static int Index(byte[] bytes, byte[] prefix)
    {
        int at = bytes.AsSpan().IndexOf(prefix);
        Assert.True(at >= 0, $"Missing packet prefix {Convert.ToHexString(prefix)}");
        return at;
    }
    static void AssertUpdate(byte[] bytes, uint id, int scale)
    {
        var prefix = new LoginWriter().U16(0x1b3).U32(id).U32(0).U16(0).ToArray();
        var stats = StatsAt(bytes, Index(bytes, prefix) + prefix.Length);
        Assert.Equal((1973u, scale), Assert.Single(stats, s => s.Item1 == 1973));
    }
    static void AssertCreate(byte[] bytes, uint id, int scale)
    {
        int start = Index(bytes, new LoginWriter().U16(0x2b9).U32(id).ToArray());
        int at = start + 18; // Object header, type and blob16 length, then the little-endian component blob.
        Assert.Equal(0, bytes[at + 16]); // Player Positioned flags0 has no custom scale extension.
        Assert.Equal((1973u, scale), Assert.Single(StatsAt(bytes, at + 20), s => s.Item1 == 1973));
    }

    [Fact]
    public async Task Real_creation_postactivation_refresh_and_reset_agree_with_one_final_key()
    {
        var conn = Character(); Wear(conn, 5);
        using var wire = await Wire.Open();
        string? old = Environment.GetEnvironmentVariable("Game_GATEWAY_ENTER");
        try
        {
            Environment.SetEnvironmentVariable("Game_GATEWAY_ENTER", "1");
            await wire.Invoke("SendPlayer", conn).WaitAsync(TimeSpan.FromSeconds(10));
            byte[] bytes = await wire.Read();
            AssertCreate(bytes, Program.PlayerId, 5); AssertUpdate(bytes, Program.PlayerId, 5);
            conn.WornCosmetics!.Clear();
            await wire.Invoke("SendCharacterStats", conn, "remove size");
            AssertUpdate(await wire.Read(), Program.PlayerId, 0);
            await wire.Invoke("SendPlayer", conn);
            bytes = await wire.Read();
            AssertCreate(bytes, Program.PlayerId, 0); AssertUpdate(bytes, Program.PlayerId, 0);
        }
        finally { Environment.SetEnvironmentVariable("Game_GATEWAY_ENTER", old); }
    }

    [Fact]
    public async Task Queued_creation_and_a_later_full_refresh_cannot_restore_a_removed_cosmetic()
    {
        var conn = Character(); Wear(conn, 5);
        using var wire = await Wire.Open();
        await wire.Gate.WaitAsync();
        var creation = wire.Invoke("SendPlayer", conn);
        Assert.False(creation.IsCompleted);
        conn.WornCosmetics!.Clear();
        wire.Gate.Release(); await creation;
        AssertCreate(await wire.Read(), Program.PlayerId, 0);
        Assert.Equal(0, conn.PeerTemplate!.Stats!.Single(s => s.Key == 1973).Value);
        await wire.Invoke("SendCharacterStats", conn, "queued source removed");
        AssertUpdate(await wire.Read(), Program.PlayerId, 0);
    }

    [Fact]
    public void Unrelated_cosmetic_stats_keep_the_existing_dev_precedence_across_creation_and_updates()
    {
        var conn = Character();
        var record = Program.Mtx.Owned.First(r => Program.MtxEffects.Of(r.MtxTypeHash)?.Stats
            .Any(s => !ActorScale.IsInput(s.Stat) && s.Stat != 1973) == true);
        var stat = Program.MtxEffects.Of(record.MtxTypeHash)!.Stats
            .First(s => !ActorScale.IsInput(s.Stat) && s.Stat != 1973);
        conn.WornCosmetics!.Add(new(0, record.Id, record.MtxTypeHash));
        conn.PlayerStats[stat.Stat] = 123;
        Assert.Equal(123, Refresh(conn).Single(s => s.Item1 == stat.Stat).Item2);
        Assert.Equal(123, Call<PlayerObject>("PlayerForCreation", conn).Stats!
            .Single(s => s.Key == stat.Stat).Value);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Queued_full_snapshot_rebuilds_current_sources_and_rejects_closed_or_leaving(bool closed, bool leaving)
    {
        var conn = Character(); Wear(conn, 5);
        using var wire = await Wire.Open();
        await wire.Gate.WaitAsync();
        var pending = wire.Invoke("SendPlayerStatSnapshot", conn,
            (Func<byte[]>)(() => InstanceEntities.StatsUpdate(Program.PlayerId, Refresh(conn))), "scale gate");
        Assert.False(pending.IsCompleted);
        conn.WornCosmetics!.Clear(); conn.Closed = closed; conn.LeavingArea = leaving;
        wire.Gate.Release(); await pending;
        byte[] bytes = await wire.Read();
        if (closed || leaving) Assert.Empty(bytes);
        else AssertUpdate(bytes, Program.PlayerId, 0);
    }

    [Fact]
    public async Task Peer_first_change_reset_late_join_and_revive_keep_other_creation_fields()
    {
        var instance = new InstanceState(); var owner = Character(instance); Wear(owner, 5);
        owner.PeerType = 123;
        owner.PeerTemplate = Call<PlayerObject>("PlayerForCreation", owner);
        var original = owner.PeerTemplate;
        var observer = Character(instance);
        using var wire = await Wire.Open();
        Publish(owner); await wire.Invoke("SyncInstancePlayers", observer);
        AssertCreate(await wire.Read(), owner.PeerId, 5);
        await wire.Gate.WaitAsync();
        var queued = wire.Invoke("SyncInstancePlayers", observer);
        owner.PlayerStats[720] = 25; Publish(owner);
        wire.Gate.Release(); await queued;
        byte[] delta = await wire.Read(); AssertUpdate(delta, owner.PeerId, 25);
        Assert.Equal(InstanceEntities.StatsUpdate(owner.PeerId, [(1973, 25)]), delta);
        Assert.NotSame(original, owner.PeerTemplate);
        Assert.Same(original.Equipped, owner.PeerTemplate!.Equipped);
        Assert.Same(original.Skills, owner.PeerTemplate.Skills);
        Assert.Equal(original.Stats!.Where(s => s.Key != 1973), owner.PeerTemplate.Stats!.Where(s => s.Key != 1973));
        owner.PlayerStats.Remove(720); owner.WornCosmetics!.Clear(); Publish(owner);
        await wire.Invoke("SyncInstancePlayers", observer);
        Assert.Equal(InstanceEntities.StatsUpdate(owner.PeerId, [(1973, 0)]), await wire.Read());
        Wear(owner, 10); Publish(owner);
        var late = Character(instance); await wire.Invoke("SyncInstancePlayers", late);
        AssertCreate(await wire.Read(), owner.PeerId, 10);
        owner.PlayerLife = 0; Publish(owner); await wire.Invoke("SyncInstancePlayers", observer); await wire.Read();
        owner.PlayerLife = 77; owner.Walk.Teleport(40, 41, owner.Clock.Elapsed.TotalSeconds); Publish(owner);
        await wire.Invoke("SyncInstancePlayers", observer);
        AssertCreate(await wire.Read(), owner.PeerId, 10);
        Assert.Equal((40, 41, 77u), (observer.SeenPlayers[owner.PeerId].X,
            observer.SeenPlayers[owner.PeerId].Y, observer.SeenPlayers[owner.PeerId].Life));
        await wire.Gate.WaitAsync(); var leaving = wire.Invoke("SyncInstancePlayers", observer);
        owner.LeavingArea = true; wire.Gate.Release(); await leaving; // No pose tick needed to reject a departed owner.
        Assert.Equal(InstanceProtocol.RemoveObject(owner.PeerId), await wire.Read());
        Assert.False(observer.SeenPlayers.ContainsKey(owner.PeerId));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Queued_peer_creation_uses_current_publication_and_checks_observer_owner(bool closed, bool leaving)
    {
        var instance = new InstanceState(); var owner = Character(instance); Wear(owner, 5);
        owner.PeerTemplate = Call<PlayerObject>("PlayerForCreation", owner); Publish(owner);
        var observer = Character(instance);
        using var wire = await Wire.Open(); await wire.Gate.WaitAsync();
        var queued = wire.Invoke("SyncInstancePlayers", observer);
        owner.PlayerStats[720] = 30; Publish(owner);
        observer.Closed = closed; observer.LeavingArea = leaving;
        wire.Gate.Release(); await queued;
        var bytes = await wire.Read();
        if (closed || leaving) { Assert.Empty(bytes); Assert.Empty(observer.SeenPlayers); }
        else AssertCreate(bytes, owner.PeerId, 30);
    }

    [Fact]
    public void Actor_scale_preserves_existing_skill_weapon_collision_navigation_projectile_and_RF_geometry()
    {
        var conn = Character();
        var before = Call<IEnumerable<(uint, int)>>("FinalPlayerStats", conn).ToDictionary(s => s.Item1, s => s.Item2);
        var blockers = conn.Blockers.ToArray();
        int Radius(string effect) => Call<int>("SkillRadius", conn, new Dictionary<uint, int>(),
            effect != "Cyclone", Program.Skills.EffectRow(effect));
        int cyclone = Radius("Cyclone"), fireball = Radius("Fireball"), rf = Radius("RighteousFire");
        conn.PlayerStats[720] = 100;
        var after = Call<IEnumerable<(uint, int)>>("FinalPlayerStats", conn).ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(before.Where(s => !ActorScale.IsInput(s.Key) && s.Key != 1973),
            after.Where(s => !ActorScale.IsInput(s.Key) && s.Key != 1973));
        Assert.Equal(cyclone, Radius("Cyclone")); Assert.Equal(fireball, Radius("Fireball"));
        Assert.Equal(rf, Radius("RighteousFire")); Assert.Equal(1, LivingCollision.PlayerSize);
        Assert.Equal(blockers, conn.Blockers); Assert.Equal(0, after.GetValueOrDefault(9807u));
        byte[] art = new EffectObject(32, 32, "RF") { Scale = 2.7f }.Blob();
        Assert.Equal(2.7f, BitConverter.ToSingle(art, 18));
        Assert.Equal(1f, BitConverter.ToSingle(art, 22)); Assert.Equal(1f, BitConverter.ToSingle(art, 26));
    }

    [Fact]
    public async Task Actual_projectile_route_contact_radius_and_all_height_packets_ignore_actor_scale()
    {
        var conn = Character();
        var target = new SpawnedMonster("scale geometry", new(60, 35, 0), 0, 0, 100);
        conn.Monsters.Add(42, target);
        await using var f = new ProjectileRuntimeFixture(conn);
        async Task<byte[]> Flight()
        {
            conn.NextObjectId = 0x3000; target.Life = 100;
            double at = f.Session.Runtime.Now / (double)GameServer.Core.Projectiles.ProjectileRuntime.QuantaPerSecond;
            f.Release(f.Shot(new("Metadata/Projectiles/DefaultArrow") { Speed = 10000, Range = 68, Radius = 4 },
                origin: new(32, 32), aim: new(100, 32)));
            byte[] creation = await f.Read(); // Publish the real object before its terminal contact.
            f.Advance(at + .02);
            byte[] terminal = await f.Read();
            // Three cells from the route is inside radius four, independently of actor scale.
            Index(terminal, new LoginWriter().U16(0x1b7).U32(0x3000).ToArray());
            Assert.Equal(90u, target.Life);
            Assert.Empty(f.Session.Runtime.Projectiles);
            return [.. creation, .. terminal];
        }
        var baseline = await Flight();
        conn.PlayerStats[720] = 100; Assert.Equal(100, Scale(conn));
        Assert.Equal(baseline, await Flight());
    }
    public void Dispose() { foreach (string name in names) PlayerInventories.Forget(name); }
}
