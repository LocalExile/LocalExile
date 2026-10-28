using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

namespace GameServer.Gateway.Tests;

public class WarcryAdmissionTests
{
    const string Flag = "your_warcry_skills_are_disabled";
    static uint Key => Program.Stats.Key(Flag)!.Value;
    static MethodInfo Method(string name) => typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    sealed class Fixture : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        readonly TcpClient reader;
        readonly ConnectionCiphers receive = new(new byte[64], isClient: true);
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        public readonly ClientState Conn;
        public readonly int Effect;
        public ushort Skill => Program.GemSkillIdPool.Value[0];
        public uint Cost => (uint)Program.Skills.EffectStats(Effect, 1)
            .Single(s => s.Item1 == Program.Stats.Key("base_mana_cost")).Item2;
        public ChargeSession Charges => (ChargeSession)Method("ChargesOf").Invoke(null, [Conn])!;
        public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));
        public bool HasOutput => reader.Available > 0;

        public Fixture(string gemId = "EnduringCry")
        {
            TestSaves.Ensure();
            Conn = new(new(ushort.MaxValue, "WarcryAdmission", 128, 128, 32, 32, null, []), 0, 1)
            { Character = "Warcry" + Guid.NewGuid().ToString("N"), ClassRow = 1, PlayerSent = true,
                PlayerLife = 500, PlayerMana = 1000, ActionCount = 17, ChannelSkill = 0x104,
                Pools = new(100, 500, 1000, 100, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
                SentEnergyShield = 0, SentRechargeActive = false, SentEsRecoveryRate = 0 };
            var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGem" + gemId)!);
            var host = ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!) with
                { Sockets = [new(SocketColour.White, gem)] };
            PlayerInventories.For(Conn.Character, Program.Items).Add(PlayerInventories.Weapon1, 0, 0, host);
            Effect = Program.Items.GemEffects(gem).First();
            Assert.Null(Method("GemDisabled").Invoke(null, [Conn, Effect])); // Requirements must really pass.
            Conn.Monsters.Add(42, new("WarcryTarget", new(34, 32, 30), 0, 2, 1000)
                { Hostile = true, Rarity = MonsterRarity.Rare });
            Conn.Clock.Start();
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }

        public Task Use(string route, ushort? skill = null)
        {
            ushort id = skill ?? Skill;
            if (route is "self" or "monster")
            {
                byte[] bytes = new LoginWriter().U16(0x24).U32(route == "self" ? 0x1000u : 42u)
                    .U16(id).U16(3).U16(0x408).ToArray();
                var use = InstanceTransfer.ReadUseSkillOnObject(bytes);
                return ((Task)Method("OnUseObject").Invoke(null,
                    ["warcry-test", writer.GetStream(), Ciphers, Conn, use, Conn.Clock.Elapsed.TotalSeconds])!)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
            ushort message = route switch { "instant-object" => 0x191, "instant-ground" => 0x190, _ => 0x25 };
            var w = new LoginWriter().U16(message);
            if (message == 0x191) w.U32(0x1000).U16(id);
            else
            {
                w.U32(38).U32(32).U16(id);
                if (message == 0x25) w.U16(3).U16(0x408);
            }
            return Program.OnClientMessages("warcry-test", writer.GetStream(), Ciphers,
                [(message, w.ToArray())], [], Conn).WaitAsync(TimeSpan.FromSeconds(5));
        }

        public Task<bool> Cast() => (Task<bool>)Method("CastGemSkill").Invoke(null,
            ["warcry-test", writer.GetStream(), Ciphers, Conn, Skill, (32, 32), (38, 32), 0u, (ushort)0,
                null, null, false])!;

        public async Task<byte[]> Drain()
        {
            byte[] bytes = new byte[reader.Available];
            if (bytes.Length > 0) await reader.GetStream().ReadExactlyAsync(bytes);
            receive.Receive.Process(bytes);
            return bytes;
        }

        public async Task ExpectPackets(params byte[][] packets)
        {
            byte[] expected = [.. packets.SelectMany(p => p)];
            byte[] received = new byte[expected.Length];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await reader.GetStream().ReadExactlyAsync(received, timeout.Token);
            receive.Receive.Process(received);
            Assert.Equal(expected, received);
        }

        public void AssertUnspent()
        {
            Assert.Equal(17, Conn.ActionCount);
            Assert.Equal((ushort)0x104, Conn.ChannelSkill);
            Assert.Equal((32, 32), Conn.Walk.Destination);
            Assert.Equal(1000u, Conn.PlayerMana);
            Assert.Equal(500u, Conn.PlayerLife);
            Assert.Empty(Conn.SkillReadyAt);
            Assert.Empty(Conn.SingleUseCooldowns);
            Assert.False(Charges.Pending);
            Assert.Equal(0, Charges.Effective(ChargeType.Endurance));
            Assert.Equal(1000u, Conn.Monsters[42].Life);
        }

        public void AssertChargeRelease()
        {
            Charges.Step(Conn.Clock.Elapsed.TotalSeconds, _ => 0, () => default, true, false, false,
                new ScriptedRandom());
            Assert.Equal(2, Charges.Effective(ChargeType.Endurance));
        }

        public void Dispose()
        {
            Conn.Closed = true;
            writer.Dispose(); reader.Dispose(); listener.Stop();
            PlayerInventories.Forget(Conn.Character!);
        }
    }

    static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    [Theory]
    [InlineData("ground", 1, true)]
    [InlineData("self", -1, false)]
    [InlineData("monster", 1, false)]
    [InlineData("ground", -1, false)]
    [InlineData("self", 1, true)]
    public async Task Parsed_requests_refuse_before_action_cost_cooldown_or_effect_and_clear_restores_use(
        string route, int value, bool explicitZero)
    {
        using var f = new Fixture();
        var pools = f.Conn.Pools;
        f.Conn.PlayerStats[Key] = value;
        await f.Use(route);
        await Task.Delay(850); // Beyond the ordinary cry's release: no hidden charge continuation was admitted.
        f.AssertUnspent();
        Assert.Equal(route == "ground" ? InstanceProtocol.LifeUpdate(0x1000, 1000, pool: 1) : [], await f.Drain());
        if (explicitZero) f.Conn.PlayerStats[Key] = 0;
        else f.Conn.PlayerStats.Remove(Key);
        Assert.Same(pools, f.Conn.Pools); // Source changes need no pool rebuild.
        await f.Use(route);
        await Until(() => f.Charges.Pending);
        Assert.Equal(18, f.Conn.ActionCount);
        Assert.True(f.Cost > 0);
        Assert.Equal(1000u - f.Cost, f.Conn.PlayerMana); // Actual paid positive, with the real gem cost.
        Assert.True(f.Conn.SkillReadyAt[f.Effect] > f.Conn.Clock.Elapsed.TotalSeconds);
        f.AssertChargeRelease();
        byte[] packets = await f.Drain();
        Assert.True(packets.AsSpan().IndexOf(new byte[] { 0x01, 0xab }) >= 0);
    }

    [Fact]
    public async Task Shared_cast_entry_refuses_before_legacy_cooldown_or_payment()
    {
        using var f = new Fixture();
        f.Conn.PlayerStats[Key] = -7;
        Assert.False(await f.Cast());
        f.AssertUnspent();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Central_preflight_refuses_before_creating_single_use_state_for_normal_and_instant(bool instant)
    {
        using var f = new Fixture();
        f.Conn.PlayerStats[Key] = 1;
        Assert.False(Program.TryBeginCooldownUse(f.Conn, f.Effect, new(8000, 0), instant,
            () => f.Conn.ActionCount++, out var ticket));
        Assert.Null(ticket);
        f.AssertUnspent();
        f.Conn.PlayerStats[Key] = 0;
        Assert.True(Program.TryBeginCooldownUse(f.Conn, f.Effect, new(8000, 0), instant,
            () => f.Conn.ActionCount++, out ticket));
        Assert.NotNull(ticket);
        Assert.Single(f.Conn.SingleUseCooldowns);
        Assert.Equal(18, f.Conn.ActionCount);
    }

    [Theory]
    [InlineData("instant-ground", 0)]
    [InlineData("instant-ground", 1)]
    [InlineData("instant-object", 0)]
    [InlineData("instant-object", -1)]
    public async Task Unsupported_Enduring_Cry_instant_aliases_stay_unsupported(string route, int value)
    {
        using var f = new Fixture();
        Assert.False(Program.Skills.IsInstant(f.Effect));
        f.Conn.PlayerStats[Key] = value;
        await f.Use(route);
        f.AssertUnspent();
        Assert.Empty(await f.Drain());
    }

    [Theory]
    [InlineData("Fireball", "ground")]
    [InlineData("Haste", "instant-object")]
    [InlineData("RighteousFire", "instant-object")]
    public async Task Ordinary_spell_aura_and_admitted_instant_RF_remain_available(string gem, string route)
    {
        using var f = new Fixture(gem);
        f.Conn.PlayerStats[Key] = -1;
        await f.Use(route);
        await Until(() => gem == "Fireball" ? f.Conn.PlayerMana < 1000
            : gem == "Haste" ? f.Conn.Auras.Count > 0 : f.Conn.RighteousFire is not null);
        if (gem == "Fireball") Assert.Equal(18, f.Conn.ActionCount);
        else
        {
            Assert.Equal(17, f.Conn.ActionCount);
            await Until(() => f.Conn.SkillReadyAt.Count > 0);
        }
        await Until(() => f.HasOutput);
        Assert.NotEmpty(await f.Drain());
    }

    [Fact]
    public async Task Ordinary_attack_still_starts_with_the_flag()
    {
        using var f = new Fixture();
        f.Conn.PlayerStats[Key] = 1;
        await f.Use("ground", SkillCatalog.FirstSkillId);
        Assert.Equal(18, f.Conn.ActionCount);
        Assert.NotEmpty(await f.Drain());
    }

    [Fact]
    public async Task A_paid_accepted_cry_still_releases_after_the_source_and_action_change()
    {
        using var f = new Fixture();
        Assert.True(await f.Cast()); // Returns only after UseChargeSkill has scheduled the accepted release.
        Assert.True(f.Cost > 0);
        Assert.Equal(1000u - f.Cost, f.Conn.PlayerMana);
        f.Conn.PlayerStats[Key] = 1;
        f.Conn.ActionCount++; // Existing non-cancellable release survives another action too.
        await Until(() => f.Charges.Pending);
        f.AssertChargeRelease();
    }

    [Fact]
    public async Task Queued_action_echo_reaches_the_existing_shared_cast_admission_check_before_payment()
    {
        using var f = new Fixture();
        await f.Gate.WaitAsync();
        Task use = f.Use("ground");
        try
        {
            Assert.Equal(18, f.Conn.ActionCount); // Action admitted, but CastGemSkill has not run yet.
            f.Conn.PlayerStats[Key] = 1;
        }
        finally { f.Gate.Release(); }
        await use;
        await f.ExpectPackets(InstanceProtocol.ActionStarted(0x1000, 32, 32, 38, 32, f.Skill, 3, 0x408),
            InstanceProtocol.LifeUpdate(0x1000, 1000, pool: 1));
        Assert.Equal(1000u, f.Conn.PlayerMana);
        Assert.Empty(f.Conn.SkillReadyAt);
        Assert.False(f.Charges.Pending);
    }

    [Fact]
    public async Task A_source_change_behind_the_paid_mana_packet_does_not_redesign_the_existing_continuation()
    {
        using var f = new Fixture();
        await f.Gate.WaitAsync();
        Task<bool> cast = f.Cast();
        try
        {
            Assert.True(f.Conn.PlayerMana < 1000); // Shared cast admission passed and paid before the await.
            f.Conn.PlayerStats[Key] = -1;
        }
        finally { f.Gate.Release(); }
        Assert.True(await cast.WaitAsync(TimeSpan.FromSeconds(5)));
        await Until(() => f.Charges.Pending);
        f.AssertChargeRelease();
    }

    [Fact]
    public void A_skill_overlay_cannot_clear_the_actor_restriction_or_remove_the_granted_skill()
    {
        using var f = new Fixture();
        f.Conn.PlayerStats[Key] = 1;
        var actor = Program.CharacterPlayerStats(f.Conn);
        var overlay = (Dictionary<uint, int>)Method("MergeSkillStats").Invoke(null,
            [actor, new Dictionary<uint, int> { [Key] = -1 }])!;
        Assert.Equal(0, overlay[Key]); // A skill's merged input can cancel the value; admission reads the caster.
        Assert.NotNull(Method("GemDisabled").Invoke(null, [f.Conn, f.Effect]));
        var granted = (IReadOnlyList<GrantedSkill>)Method("PlayerSkills").Invoke(null, [f.Conn])!;
        Assert.Contains(granted, skill => skill.Id == f.Skill);
        Assert.Equal(1, actor[Key]);
    }

    [Fact]
    public void Passive_equipment_and_raw_dev_sources_share_caster_precedence_without_a_class_bypass()
    {
        Assert.Equal(5409u, Key);
        using var f = new Fixture();
        var node = Program.Passives.Find(49937)!;
        Assert.Equal("Rampager", node.Name);
        Assert.Contains((Key, 1), node.Stats);
        Assert.Null(f.Conn.MainAscendancy.Selection); // A raw-source test does not enable Phrecia play.
        Assert.Throws<AscendancyRuleException>(() => AscendancyClientCompatibility.RequirePlayable(
            new(AscendancyFamily.Phrecia, "Berserker", AscendancyCatalog.Revision)));
        f.Conn.Passives.Add(49937);
        Assert.NotNull(Method("GemDisabled").Invoke(null, [f.Conn, f.Effect]));
        f.Conn.PlayerStats[Key] = 0;
        Assert.Null(Method("GemDisabled").Invoke(null, [f.Conn, f.Effect]));
        f.Conn.PlayerStats.Remove(Key);
        f.Conn.Passives.Clear();
        var mod = Program.Items.Mods.All.Single(m => m.Id == "MutatedUniqueBodyStrDex7WarcriesAreDisabled");
        var body = ItemCatalog.Create(Program.Items.Find("Simple Robe")!) with
            { Mods = [new(ModSection.Explicit, mod.Hash, [1])] };
        var inv = PlayerInventories.For(f.Conn.Character!, Program.Items);
        var worn = inv.Add(PlayerInventories.BodyArmour, 0, 0, body);
        Assert.NotNull(Method("GemDisabled").Invoke(null, [f.Conn, f.Effect]));
        f.Conn.PlayerStats[Key] = 0;
        Assert.Null(Method("GemDisabled").Invoke(null, [f.Conn, f.Effect]));
        f.Conn.PlayerStats.Remove(Key);
        inv.Remove(PlayerInventories.BodyArmour, worn.Id);
        Assert.Null(Method("GemDisabled").Invoke(null, [f.Conn, f.Effect]));
    }
}
