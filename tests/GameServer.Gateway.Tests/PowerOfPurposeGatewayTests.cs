using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class PowerOfPurposeGatewayTests
{
    static uint Key(string name) => Program.Stats.Key(name)!.Value;
    static MethodInfo Method(string name) =>
        typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    sealed class Character : IDisposable
    {
        public ClientState State { get; }
        public Character()
        {
            TestSaves.Ensure();
            State = new(new(ushort.MaxValue, "PurposeTest", 128, 128, 32, 32, null, []), 0, 1)
            {
                Character = "Purpose" + Guid.NewGuid().ToString("N"), ClassRow = SkillCatalog.WitchRow,
                PlayerLife = 25, PlayerSent = true,
            };
            State.PlayerStats[Key("mana_regeneration_rate_+%")] = -100;
            State.Pools = Program.PoolsFor(State, 1);
        }
        public void Dispose() => PlayerInventories.Forget(State.Character!);
    }

    [Fact]
    public void Real_Mental_Conditioning_raw_flag_enables_conversion_and_display_alone_does_not()
    {
        Assert.Equal(9548u, Key(PowerOfPurpose.RawFlag));
        Assert.Equal("display_mode_is_behemoth_ascendancy", Program.Stats.Name(21159));
        var node = new PassiveTree().Find(38080)!;
        Assert.Equal("Mental Conditioning", node.Name);
        Assert.Equal("AscendancyCustomBerserker11", node.Id);
        Assert.Contains((9548u, 1), node.Stats);
        using var character = new Character();
        var conn = character.State;
        var ordinary = conn.Pools;
        conn.PlayerStats[21159] = 1;
        Assert.Equal(ordinary, Program.PoolsFor(conn, 1));
        conn.Passives.Add(38080);
        var active = Program.PoolsFor(conn, 1);
        Assert.Equal(16u, active.Mana);
        Assert.Equal(128, active.Armour); // B = 34 + 6 + 32/2 + floor(14/4)*8 = 80.
        Assert.Equal(ordinary.Strength, active.Strength);
        Assert.Equal(ordinary.Intelligence, active.Intelligence);
        // Explicit conversion zero retains the node's independent mana-per-Strength source.
        conn.PlayerStats[9548] = 0;
        Assert.Equal(ordinary with { Mana = 80 }, Program.PoolsFor(conn, 1));
        Assert.Equal(0, Program.PoolsFor(conn, 1).Armour);
        conn.PlayerStats.Remove(9548);
        Assert.Equal(active, Program.PoolsFor(conn, 1));
        using var rebuilt = new Character();
        rebuilt.State.Passives.AddRange(conn.Passives);
        Assert.Equal(active, Program.PoolsFor(rebuilt.State, 1)); // Fresh connection derives the same pools.
        conn.Passives.Remove(38080);
        Assert.Equal(ordinary, Program.PoolsFor(conn, 1));
    }

    [Theory]
    [InlineData(3u, 3u, false)]
    [InlineData(50u, 9u, false)]
    [InlineData(0u, 0u, true)]
    public async Task Actual_commands_publish_idle_reservation_changes_and_clamp_without_refill_or_resurrection(
        uint current, uint shrunk, bool dead)
    {
        using var character = new Character();
        var conn = character.State;
        conn.PlayerMana = current;
        if (dead) conn.PlayerLife = 0;
        conn.Auras[0x700] = new(0x700, -1, "FixtureReservation", "Reservation", 1, 1, 0, 1000);
        Assert.Equal(6, AuraCatalog.ReservedMana(conn.Pools, conn.Auras.Values));
        Assert.Equal(0d, (double)Method("ManaRegenPerSecond").Invoke(null, [conn])!);

        var on = await Command(conn, "/set_stat 9548 1");
        Assert.Equal(11u, conn.Pools.Mana);
        Assert.Equal(90, conn.Pools.Armour);
        Assert.Equal(shrunk, conn.PlayerMana);
        Assert.Equal(dead ? 0u : 25u, conn.PlayerLife);
        AssertWire(on, 11, 90, shrunk, 2);

        var off = await Command(conn, $"/set_stat {PowerOfPurpose.RawFlag} 0");
        Assert.Equal(56u, conn.Pools.Mana);
        Assert.Equal(0, conn.Pools.Armour);
        Assert.Equal(shrunk, conn.PlayerMana);
        Assert.Equal(dead ? 0u : 25u, conn.PlayerLife);
        AssertWire(off, 56, 0, shrunk, 6);
        var onAgain = await Command(conn, "/set_stat 9548 -1"); // Any nonzero raw bool enables it.
        AssertWire(onAgain, 11, 90, shrunk, 2);
    }

    [Fact]
    public async Task Increased_attributes_and_conversion_publish_complete_snapshots_and_restore_independent_sources()
    {
        using var character = new Character();
        var conn = character.State;
        // Real raw sources: the small node adds 3% Strength / 10% Armour, Mental Conditioning adds both mana inputs.
        conn.MainAscendancy = new(Nodes: [4561, 38080]);
        PlayerInventories.For(conn.Character!, Program.Items).Add(6, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
            {
                Mods = [Mod("additional_strength", 1), Mod("additional_intelligence", 1),
                    Mod("strength_+%", 4), Mod("intelligence_+%", 10),
                    Mod("maximum_mana_+%", 25), Mod("physical_damage_reduction_rating_+%", 50)]
            });
        conn.Pools = Program.PoolsFor(conn, 100); // The percentage modifiers have real item-level requirements.
        Assert.Equal((16, 36, 171u, 2025),
            (conn.Pools.Strength, conn.Pools.Intelligence, conn.Pools.Mana, conn.Pools.Armour));
        Assert.Equal(7, Program.CharacterPlayerStats(conn)[Key("strength_+%")]);
        conn.PlayerMana = 85;
        conn.Auras[0x700] = new(0x700, -1, "FixtureReservation", "Reservation", 1, 1, 0, 5000);

        async Task Check(string command, int strength, int dexterity, int intelligence,
            int mana, int armour, uint current, int? shownStrength = null, bool publishesMana = true)
        {
            byte[] bytes = await Command(conn, command);
            Assert.Equal((strength, dexterity, intelligence),
                (conn.Pools.Strength, conn.Pools.Dexterity, conn.Pools.Intelligence));
            Assert.Equal((uint)(1238 + strength / 2), conn.Pools.Life);
            Assert.Equal(25u, conn.PlayerLife);
            Assert.Equal(current, conn.PlayerMana);
            Assert.Equal((uint)mana, conn.Pools.Mana);
            Assert.Equal(armour, conn.Pools.Armour);
            if (publishesMana) AssertWire(bytes, mana, armour, current, (uint)(mana + 1) / 2);
            else Assert.Empty(ManaUpdates(bytes)); // Final display overrides do not rebuild or publish pool resources.
            var published = StatUpdates(bytes);
            foreach (var (name, value) in new[] { ("strength", shownStrength ?? strength), ("dexterity", dexterity),
                ("intelligence", intelligence), ("virtual_strength_for_requirements", shownStrength ?? strength),
                ("virtual_dexterity_for_requirements", dexterity), ("virtual_intelligence_for_requirements", intelligence),
                ("base_maximum_mana", mana), ("maximum_mana", mana), ("physical_damage_reduction_rating", armour),
                ("maximum_life", 1238 + strength / 2), ("maximum_energy_shield_+%", intelligence / 5),
                ("melee_physical_damage_+%", strength / 5) })
                Assert.Equal(value, published[Key(name)]);
            // Compare every final stat from the actual full 0x1b3, allowing only zeroes for previously sent keys.
            var expected = ((IEnumerable<(uint Key, int Value)>)Method("FinalPlayerStats").Invoke(null, [conn])!)
                .ToDictionary(s => s.Key, s => s.Value);
            Assert.All(expected, pair => Assert.Equal(pair.Value, published[pair.Key]));
            Assert.All(published.Where(pair => !expected.ContainsKey(pair.Key)), pair => Assert.Equal(0, pair.Value));
            Assert.Equal(conn.Pools, Program.PoolsFor(conn, 100)); // No feedback from final stats on another rebuild.
        }

        await Check("/set_stat all_attributes_+% 10", 18, 15, 40, 172, 2031, 85);
        await Check("/set_stat all_attributes_+% off", 16, 14, 36, 171, 2025, 85);
        await Check("/set_stat intelligence_+% 0", 16, 14, 33, 171, 2020, 85); // B=682.5 keeps the half point.
        await Check("/set_stat strength_+% 40", 21, 14, 33, 173, 2044, 85); // Replaces the passive + item total.
        await Check("/set_stat strength_+% 0", 15, 14, 33, 169, 1997, 84);
        await Check("/set_stat strength_+% off", 16, 14, 33, 171, 2020, 84);
        await Check("/set_stat X_mana_per_4_strength 0", 16, 14, 33, 163, 1925, 81);
        await Check("/set_stat X_mana_per_4_strength off", 16, 14, 33, 171, 2020, 81);
        await Check("/set_stat 9548 0", 16, 14, 33, 852, 0, 81); // Ordinary mana truncates Intelligence / 2.
        await Check("/set_stat 9548 off", 16, 14, 33, 171, 2020, 81);
        await Check("/set_stat strength 999", 16, 14, 33, 171, 2020, 81, shownStrength: 999, publishesMana: false);
        await Check("/set_stat strength off", 16, 14, 33, 171, 2020, 81, publishesMana: false);
        await Check("/set_stat intelligence_+% off", 16, 14, 36, 171, 2025, 81);
        Assert.DoesNotContain(Key("strength_+%"), conn.PlayerStats.Keys);
        Assert.DoesNotContain(Key("intelligence_+%"), conn.PlayerStats.Keys);
        Assert.DoesNotContain(4993u, conn.PlayerStats.Keys);
        Assert.DoesNotContain(9548u, conn.PlayerStats.Keys);
    }

    [Fact]
    public void Active_equipment_feeds_both_mana_sources_and_increases_but_inactive_weapons_do_not()
    {
        using var character = new Character();
        var conn = character.State;
        var items = PlayerInventories.For(conn.Character!, Program.Items);
        conn.MainAscendancy = new(Nodes: [38080]);
        var ring = ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
        {
            Mods = [Mod("additional_strength", 200)]
        };
        var wornRing = items.Add(6, 0, 0, ring);
        var weapon = items.Add(2, 0, 0, ItemCatalog.Create(Program.Items.Find("Imperial Maul")!) with
        {
            Mods = [Mod("base_maximum_mana", 44), Mod("maximum_mana_+%", 25),
                Mod("physical_damage_reduction_rating_+%", 50)]
        });
        var active = Program.PoolsFor(conn, 100);
        Assert.Equal(214, active.Strength);
        // B = 34 + 600 + 16 + 44 + floor(214/4)*8 = 1118.
        Assert.Equal(280u, active.Mana);
        Assert.Equal(3130, active.Armour);
        Assert.DoesNotContain(weapon.Id, Program.InactiveItems[conn.Character!]);
        Assert.Equal(active, Program.PoolsFor(conn, 100));

        items.PickUp(6, wornRing.Id);
        var inactive = Program.PoolsFor(conn, 100);
        Assert.Contains(weapon.Id, Program.InactiveItems[conn.Character!]);
        Assert.Equal(14, inactive.Strength);
        Assert.Equal(135u, inactive.Mana); // (650 + 24)*.2.
        Assert.Equal(1078, inactive.Armour);
        items.Add(6, 0, 0, ring);
        Assert.Equal(active, Program.PoolsFor(conn, 100));
    }

    [Fact]
    public async Task Both_raw_sources_support_zero_off_and_removal_without_refilling_reserved_mana()
    {
        using var character = new Character();
        var conn = character.State;
        conn.MainAscendancy = new(Nodes: [38080]);
        conn.Pools = Program.PoolsFor(conn, 1);
        conn.PlayerMana = 8;
        conn.Auras[0x700] = new(0x700, -1, "FixtureReservation", "Reservation", 1, 1, 0, 5000);
        AssertWire(await Command(conn, "/set_stat 4993 0"), 11, 90, 5, 6);
        AssertWire(await Command(conn, "/set_stat 4993 off"), 16, 128, 5, 8);
        AssertWire(await Command(conn, "/set_stat 9548 0"), 80, 0, 5, 40);
        AssertWire(await Command(conn, "/set_stat 9548 off"), 16, 128, 5, 8);
        Assert.DoesNotContain(4993u, conn.PlayerStats.Keys);
        Assert.DoesNotContain(9548u, conn.PlayerStats.Keys);
        conn.MainAscendancy = new();
        conn.Pools = Program.PoolsFor(conn, 1);
        AssertWire(await Command(conn, "/set_stat 9548 off"), 56, 0, 5, 28);
    }

    [Fact]
    public void Converted_armour_preserves_chaos_hit_mitigation_and_Chaos_Inoculation()
    {
        using var character = new Character();
        var conn = character.State;
        conn.Passives.AddRange([38080, 29437]);
        var pools = Program.PoolsFor(conn, 1);
        Assert.True(pools.ArmourAppliesToChaosDamage);
        Assert.Equal(128, pools.Armour);
        Assert.Equal(80, PlayerHitMitigation.AfterResistance(pools, DamageType.Chaos, 100, 90, 90));
        conn.PlayerStats[Key(PlayerHitMitigation.ArmourAppliesToChaos)] = 0;
        var off = Program.PoolsFor(conn, 1);
        Assert.Equal(128, off.Armour);
        Assert.Equal(100, PlayerHitMitigation.AfterResistance(off, DamageType.Chaos, 100, 90, 90));
        conn.Passives.Add(11455); // Chaos Inoculation.
        var ci = Program.PoolsFor(conn, 1);
        Assert.Equal(1u, ci.Life);
        Assert.Equal(pools.Mana, ci.Mana);
        Assert.Equal(pools.Armour, ci.Armour);
    }

    [Fact]
    public void Mageblood_and_a_running_Granite_apply_ordinary_armour_once_beside_the_converted_branch()
    {
        using var character = new Character();
        var conn = character.State;
        conn.MainAscendancy = new(Nodes: [38080]);
        var items = PlayerInventories.For(conn.Character!, Program.Items);
        items.Add(6, 0, 0, ItemCatalog.Create(Program.Items.Find("Iron Ring")!) with
        {
            Mods = [Mod("physical_damage_reduction_rating_+%", 50)]
        });
        var belt = items.Add(PlayerInventories.Belt, 0, 0, Program.Uniques.Create("Mageblood", new Random(1))!);
        var baseline = Program.PoolsFor(conn, 100);
        var type = Program.Items.Find("Granite Flask")!;
        var flask = items.Add(PlayerInventories.Flask, 0, 0,
            ItemCatalog.Create(type) with { Rarity = ItemRarity.Magic });
        var permanent = Program.PoolsFor(conn, 100);
        Assert.Equal(baseline.Mana, permanent.Mana);
        Assert.Equal(baseline.Armour + 2250, permanent.Armour); // 1500 ordinary flat Armour * 1.50.
        var utility = Program.Items.UtilityFlaskOf(type)!;
        var (sources, _) = UtilityFlasks.For(utility, flask.Item,
            ItemModStats.Local(flask.Item, Program.Items.Mods), _ => 0);
        conn.UtilityFlaskEffects.Start(new(flask.Id, flask.X, type.Name, utility, 1, null, sources, 0, 1000));
        Assert.Equal(permanent, Program.PoolsFor(conn, 100));
        items.PickUp(PlayerInventories.Belt, belt.Id);
        var running = Program.PoolsFor(conn, 100);
        items.PickUp(PlayerInventories.Flask, flask.Id);
        Assert.Equal(running, Program.PoolsFor(conn, 100)); // Running effect survives removing its item and belt.
    }

    [Fact]
    public async Task Queued_mana_reconciles_own_aura_restriction_with_both_current_raw_mana_sources()
    {
        using var character = new Character();
        var conn = character.State;
        conn.PlayerStats[4993] = 8;
        conn.Pools = Program.PoolsFor(conn, 1);
        conn.PlayerMana = 7;
        conn.Auras[0x700] = new(0x700, Program.Skills.EffectRow("Haste"), "Haste", "Haste",
            1, AuraCatalog.BuffRow("Haste"), 0, 5000);
        var bytes = await Exchange(async (stream, ciphers) =>
        {
            var gate = Program.SendGates.GetValue(ciphers, _ => new(1, 1));
            await gate.WaitAsync();
            var pending = (Task)Method("SendPlayerMana").Invoke(null, ["purpose-test", stream, ciphers, conn, null])!;
            try
            {
                Assert.False(pending.IsCompleted);
                lock (conn.Monsters)
                {
                    conn.PlayerStats[4993] = 16;
                    conn.PlayerStats[9548] = 1;
                    conn.PlayerStats[21148] = 1;
                    conn.Pools = Program.PoolsFor(conn, 1);
                }
            }
            finally { gate.Release(); }
            await pending;
        });
        Assert.Empty(conn.Auras);
        Assert.Equal(21u, conn.Pools.Mana); // (56 + three groups * 16)*.2 = 20.8.
        Assert.Equal(166, conn.Pools.Armour);
        Assert.All(ManaUpdates(bytes), update => Assert.Equal((7u, 0u), update));
        Assert.NotEmpty(ManaUpdates(bytes));
    }

    [Fact]
    public async Task Queued_mana_samples_current_and_reservation_together_at_the_send_gate()
    {
        using var character = new Character();
        var conn = character.State;
        conn.PlayerMana = 30;
        conn.Auras[0x700] = new(0x700, -1, "FixtureReservation", "Reservation", 1, 1, 0, 1000);
        var bytes = await Exchange(async (stream, ciphers) =>
        {
            var gate = Program.SendGates.GetValue(ciphers, _ => new(1, 1));
            await gate.WaitAsync();
            var sending = (Task)Method("SendPlayerMana").Invoke(null, ["purpose-test", stream, ciphers, conn, null])!;
            try
            {
                Assert.False(sending.IsCompleted);
                lock (conn.Monsters)
                {
                    conn.PlayerStats[9548] = 1;
                    conn.Pools = Program.PoolsFor(conn, 1);
                    conn.PlayerMana = 7;
                }
            }
            finally { gate.Release(); }
            await sending;
        });
        Assert.Equal((7u, 2u), Assert.Single(ManaUpdates(bytes)));
    }

    [Fact]
    public async Task Queued_command_snapshot_uses_the_latest_flag_removal_and_pool()
    {
        using var character = new Character();
        var conn = character.State;
        conn.PlayerMana = 3;
        conn.Auras[0x700] = new(0x700, -1, "FixtureReservation", "Reservation", 1, 1, 0, 1000);
        var bytes = await Exchange(async (stream, ciphers) =>
        {
            conn.Commands = (ChatCommands)Method("Commands").Invoke(null, ["purpose-test", stream, ciphers, conn])!;
            var gate = Program.SendGates.GetValue(ciphers, _ => new(1, 1));
            await gate.WaitAsync();
            var command = conn.Commands.RunAsync("/set_stat 9548 1");
            try
            {
                Assert.False(command.IsCompleted);
                Assert.Equal(11u, conn.Pools.Mana);
                lock (conn.Monsters)
                {
                    conn.PlayerStats.Remove(9548);
                    conn.Pools = Program.PoolsFor(conn, 1);
                }
            }
            finally { gate.Release(); }
            Assert.True(await command);
        });
        AssertWire(bytes, 56, 0, 3, 6);
    }

    [Fact]
    public async Task Native_signed_limits_and_final_mana_lightning_and_Steel_Willed_consumers_share_the_pool()
    {
        using var character = new Character();
        var conn = character.State;
        conn.Passives.AddRange([38080, 2103, 22720]);
        conn.Pools = Program.PoolsFor(conn, 100);
        var stats = Program.CharacterPlayerStats(conn);
        Assert.Equal((int)conn.Pools.Mana, stats[Key("maximum_mana")]);
        Assert.Equal(conn.Pools.Armour, stats[Key("physical_damage_reduction_rating")]);
        int attackIncrease = conn.Pools.Armour / 450 * 2;
        Assert.True(attackIncrease > 0);
        Assert.Equal(attackIncrease, stats[Key("attack_damage_+%")]);
        var hit = Program.AttackHit(new(1000, 100, 100, 20), stats, melee: false);
        Assert.Equal(conn.Pools.Mana * .06 * (1 + attackIncrease / 100.0), hit[DamageType.Lightning].Min, 6);
        Assert.Equal(hit[DamageType.Lightning].Min, hit[DamageType.Lightning].Max);
        conn.PlayerMana = 0;
        Assert.Equal(stats[Key("maximum_mana")], Program.CharacterPlayerStats(conn)[Key("maximum_mana")]);

        conn.Passives.Clear();
        conn.Pools = CharacterStats.For(SkillCatalog.ClassInfo.Witch, 1,
            new(Mana: int.MaxValue, ManaPct: int.MaxValue, ArmourPct: int.MaxValue, MentalConditioning: true));
        conn.PlayerMana = (uint)int.MaxValue;
        var bytes = await Exchange(async (stream, ciphers) =>
        {
            await (Task)Method("SendCharacterStats").Invoke(null,
                ["purpose-test", stream, ciphers, conn, "native bounds"])!;
            await (Task)Method("SendPlayerMana").Invoke(null, ["purpose-test", stream, ciphers, conn, null])!;
        });
        AssertWire(bytes, int.MaxValue, int.MaxValue, (uint)int.MaxValue, 0);
    }

    static Task<byte[]> Command(ClientState conn, string command) => Exchange(async (stream, ciphers) =>
    {
        conn.Commands = (ChatCommands)Method("Commands").Invoke(null, ["purpose-test", stream, ciphers, conn])!;
        Assert.True(await conn.Commands.RunAsync(command));
    });

    static ItemMod Mod(string name, int value)
    {
        var mod = Program.Items.Mods.All.Where(m => m.Domain == 1 && m.Stats.Count == 1
            && m.Stats[0].Id == name && Program.Items.Mods.ByHash(m.Hash)?.Id == m.Id)
            .OrderBy(m => m.Level).First();
        return new(ModSection.Explicit, mod.Hash, [value]);
    }

    static async Task<byte[]> Exchange(Func<NetworkStream, ConnectionCiphers, Task> work)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var accepting = listener.AcceptTcpClientAsync();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var server = await accepting;
        using var capture = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reading = client.GetStream().CopyToAsync(capture, timeout.Token);
        byte[] key = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        try { await work(server.GetStream(), new(key, isClient: false)).WaitAsync(timeout.Token); }
        finally
        {
            server.Client.Shutdown(SocketShutdown.Send);
            await reading;
        }
        var bytes = capture.ToArray();
        new ConnectionCiphers(key, isClient: true).Receive.Process(bytes);
        return bytes;
    }

    static void AssertWire(byte[] bytes, int maximum, int armour, uint current, uint reserved)
    {
        var stats = StatUpdates(bytes);
        Assert.Equal(maximum, stats[Key("maximum_mana")]);
        Assert.Equal(maximum, stats[Key("base_maximum_mana")]);
        Assert.Equal(armour, stats[Key("physical_damage_reduction_rating")]);
        Assert.All(ManaUpdates(bytes), pair => Assert.Equal((current, reserved), pair));
        Assert.NotEmpty(ManaUpdates(bytes));
    }

    // Decode captured server output after transport decryption, rather than matching the production writer's bytes.
    static Dictionary<uint, int> StatUpdates(byte[] bytes)
    {
        var result = new Dictionary<uint, int>();
        foreach (int start in Headers(bytes, 0x1b3))
        {
            int pos = start + 12;
            int count = Compact(bytes, ref pos, signed: false);
            Assert.InRange(count, 1, 2000);
            for (int i = 0; i < count; i++)
            {
                uint key = (uint)Compact(bytes, ref pos, signed: false);
                result[key] = Compact(bytes, ref pos, signed: true);
            }
        }
        return result;
    }

    static List<(uint Current, uint Reserved)> ManaUpdates(byte[] bytes) =>
        Headers(bytes, 0x1b1).Where(start => bytes[start + 20] == 1)
            .Select(start => (BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(start + 12)),
                BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(start + 16)))).ToList();

    static List<int> Headers(byte[] bytes, ushort id)
    {
        var result = new List<int>();
        for (int pos = 0; pos <= bytes.Length - 12; pos++)
            if (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(pos)) == id
                && BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos + 2)) == Program.PlayerId
                && bytes.AsSpan(pos + 6, 6).IndexOfAnyExcept((byte)0) < 0)
                result.Add(pos);
        return result;
    }

    static int Compact(byte[] bytes, ref int pos, bool signed)
    {
        byte first = bytes[pos++];
        int extra = first < 0x80 ? 0 : first < 0xc0 ? 1 : first < 0xe0 ? 2 : first < 0xf0 ? 3 : 4;
        int bits = extra == 4 ? 32 : 7 * (extra + 1);
        uint value = extra == 4 ? 0 : (uint)(first & (0x7f >> extra));
        for (int i = 0; i < extra; i++) value = value << 8 | bytes[pos++];
        if (signed && bits < 32 && (value & (1u << (bits - 1))) != 0) value |= uint.MaxValue << bits;
        return unchecked((int)value);
    }
}
