using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

[CollectionDefinition("Final Blow and always hit Gateway", DisableParallelization = true)]
public sealed class FinalBlowAndAlwaysHitGatewayCollection;

// The two Behemoth/Chieftain notables' conditional crit and cannot-be-evaded stats, through the real passive catalog
// and the real pools/panel consumers (PoolsFor, CharacterPlayerStats, OnLowLife, GamblerFor).
[Collection("Final Blow and always hit Gateway")]
public sealed class FinalBlowAndAlwaysHitGatewayTests : IDisposable
{
    readonly string _name = "FinalBlow" + Guid.NewGuid().ToString("N");
    static MethodInfo Method(string name) => typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    public FinalBlowAndAlwaysHitGatewayTests()
    {
        TestSaves.Ensure();
    }

    ClientState Character(bool armed = false, string? suffix = null)
    {
        string name = _name + suffix;
        var conn = new ClientState(new(0, "CritTest", 128, 128, 32, 32, null, []), 0, 1)
        { Character = name, ClassRow = 1 };
        if (armed)
            PlayerInventories.For(name, Program.Items).Add(PlayerInventories.Weapon1, 0, 0,
                ItemCatalog.Create(Program.Items.Row(384)!));
        return conn;
    }

    static void Set(ClientState conn, string name, int value) => conn.PlayerStats[Program.Stats.Key(name)!.Value] = value;
    static CharacterStats.Pools Pools(ClientState conn) =>
        (CharacterStats.Pools)Method("PoolsFor").Invoke(null, [conn, 20])!;
    static Dictionary<uint, int> Panel(ClientState conn) =>
        (Dictionary<uint, int>)Method("CharacterPlayerStats").Invoke(null, [conn])!;
    static GamblerModifiers Gambler(Dictionary<uint, int> panel) =>
        (GamblerModifiers)Method("GamblerFor").Invoke(null, [panel])!;

    // The granted gem records through the real skill catalog: the default attack's stats and the copied player
    // stats, exactly as SendPlayer's Skills list composes them.
    static IReadOnlyList<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)> GemPlacements(ClientState conn) =>
        [.. (IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)Method("GemSkillEffects").Invoke(null, [conn])!];
    static IReadOnlyList<GrantedSkill> GrantedGems(ClientState conn)
    {
        var stats = Panel(conn);
        var player = stats.Select(kv => (kv.Key, kv.Value)).ToList();
        var weapon = (SkillCatalog.Unarmed)Method("WeaponFor").Invoke(null, [conn])!;
        var weaponStats = (List<(uint, int)>)Method("WeaponStats").Invoke(null, [conn, weapon, stats])!;
        return [.. (IEnumerable<GrantedSkill>)Method("GemSkills").Invoke(null, [conn, weaponStats.Concat(player)])!];
    }

    int RecordMultiplier(GrantedSkill record, string name) =>
        record.Stats!.First(kv => kv.Key == (uint)Program.Stats.Key(name)!.Value).Value;

    // Invoke the real command handler (the actual ChatCommands registry), not a dev Set + PoolsFor, and return the
    // decrypted capture of what the handler sent (the chat replies are plain Wireshark-ascii inside 0x17 lines).
    static async Task<byte[]> RunCommand(ClientState conn, string line)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var server = await accept;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var capture = new MemoryStream();
        var reading = client.GetStream().CopyToAsync(capture, timeout.Token);
        byte[] key = [.. Enumerable.Range(0, 64).Select(i => (byte)i)];
        var serverCipher = new ConnectionCiphers(key, isClient: false);
        var clientCipher = new ConnectionCiphers(key, isClient: true);
        conn.Commands = (ChatCommands)typeof(Program)
            .GetMethod("Commands", BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(string), typeof(NetworkStream), typeof(ConnectionCiphers), typeof(ClientState)])!
            .Invoke(null, ["set-stat-integration", server.GetStream(), serverCipher, conn])!;
        try
        {
            await conn.Commands.RunAsync(line).WaitAsync(timeout.Token);
        }
        finally
        {
            server.Client.Shutdown(SocketShutdown.Send);
            await reading;
        }
        byte[] sent = capture.ToArray();
        clientCipher.Receive.Process(sent);
        return sent;
    }

    int PanelValue(Dictionary<uint, int> panel, string name) => panel[Program.Stats.Key(name)!.Value];

    [Fact]
    public void Final_Blow_multiplies_the_unarmed_melee_critical_only_with_an_empty_active_main_hand()
    {
        var conn = Character();
        conn.Passives.Add(10135); // Final Blow: base melee crit while unarmed 10%, low-life extra roll, +50% multiplier.
        conn.Pools = Pools(conn);
        var pools = Pools(conn);
        Assert.Equal(1000, pools.AttackCriticalStrikeChance); // 10.00% unarmed base, no increased.
        // The bonus is stored separately (the melee context decides at the roll); the panel row shows the sum.
        Assert.Equal(150, pools.CriticalStrikeMultiplier);
        Assert.Equal(50, conn.Pools.UnarmedCriticalStrikeMultiplierBonus);
        var panel = Panel(conn);
        Assert.Equal(1000, PanelValue(panel, "main_hand_critical_strike_chance"));
        Assert.Equal(200, PanelValue(panel, "main_hand_critical_strike_multiplier"));
        // An unarmed non-melee attack gem keeps the plain attack multiplier (its own catalog's classification).
        int concoction = Program.Skills.EffectRow("ExplosiveConcoction");
        Assert.True(Program.Skills.IsAttack(concoction));
        Assert.False(Program.Skills.IsMelee(concoction));
        Assert.Equal(150, Program.CriticalMultiplierFor(conn, spell: false, melee: Program.MeleeAttack(conn, concoction)));
        // The unarmed melee attack's context and the panel keep the +50.
        Assert.Equal(200, Program.CriticalMultiplierFor(conn, spell: false, melee: Program.MeleeAttack(conn)));
        // The spell route reads neither the unarmed base nor the unarmed multiplier.
        Assert.Equal(150, pools.SpellCriticalStrikeMultiplier);
        Assert.Equal(500, pools.SpellCriticalStrikeChance);
        // A weapon in the active main hand suppresses the unarmed pair (its base moves to the weapon's 8.30%).
        var armed = Character(armed: true, "armed");
        armed.Passives.Add(10135);
        var armedPools = Pools(armed);
        Assert.Equal(830, armedPools.AttackCriticalStrikeChance);
        Assert.Equal(150, armedPools.CriticalStrikeMultiplier);
        Assert.Equal(0, armedPools.UnarmedCriticalStrikeMultiplierBonus);
        // A weapon in the inactive set does not make the empty active hand armed, and swapping into it stops the grant.
        var swapped = Character(suffix: "swapped");
        PlayerInventories.For(_name + "swapped", Program.Items).Add(PlayerInventories.Weapon2, 0, 0,
            ItemCatalog.Create(Program.Items.Row(384)!));
        swapped.Passives.Add(10135);
        Assert.Equal(50, Pools(swapped).UnarmedCriticalStrikeMultiplierBonus);
        PlayerInventories.For(_name + "swapped", Program.Items).SwapWeaponSets();
        var swappedPools = Pools(swapped);
        Assert.Equal(0, swappedPools.UnarmedCriticalStrikeMultiplierBonus);
        Assert.Equal(150, swappedPools.CriticalStrikeMultiplier);
        // The refund restores the base multiplier and drops the experimental path too.
        conn.Passives.Remove(10135);
        var refunded = Pools(conn);
        Assert.Equal(150, refunded.CriticalStrikeMultiplier);
        Assert.Equal(0, refunded.UnarmedCriticalStrikeMultiplierBonus);
        Assert.Equal(0, refunded.AttackCriticalStrikeChance);
    }

    [Fact]
    public void The_final_blow_extra_critical_roll_follows_the_live_low_life_boundary()
    {
        var conn = Character();
        // The production Gambler carries the low-life extra roll only from the allocated node.
        Assert.Equal(0, Gambler(Panel(conn)).LowLifeCriticalExtraRolls);
        conn.Passives.Add(10135);
        Assert.Equal(1, Gambler(Panel(conn)).LowLifeCriticalExtraRolls);
        // The boundary reads the live life values, inclusive at exactly half of the pool's maximum.
        conn.Pools = Pools(conn);
        conn.PlayerLife = conn.PlayerMaxLife / 2;
        Assert.True(Program.OnLowLife(conn));
        conn.PlayerLife = conn.PlayerMaxLife / 2 + 1;
        Assert.False(Program.OnLowLife(conn));
        // The Life Mastery-style raised threshold (the override percentage) moves the boundary, and boundaries stay
        // inclusive there too; the ordinary conditional percentage applies when no override is set.
        Set(conn, "low_life_threshold_%_override", 55);
        conn.Pools = Pools(conn);
        conn.PlayerLife = (uint)Math.Floor(conn.PlayerMaxLife * 0.55);
        Assert.True(Program.OnLowLife(conn));
        conn.PlayerLife = (uint)Math.Floor(conn.PlayerMaxLife * 0.55) + 1;
        Assert.False(Program.OnLowLife(conn));
        Set(conn, "low_life_threshold_%_override", 0);
        Set(conn, "low_life_threshold_%", 60);
        conn.Pools = Pools(conn);
        conn.PlayerLife = (uint)Math.Floor(conn.PlayerMaxLife * 0.59);
        Assert.True(Program.OnLowLife(conn));
        // Zero life is the boundary itself (a dead player never rolls, but the answer keeps its meaning).
        conn.PlayerLife = 0;
        Assert.True(Program.OnLowLife(conn));
    }

    [Fact]
    public void Exhibit_122_makes_the_reference_hit_chance_100_without_touching_rating_or_crit()
    {
        var baseline = Character(armed: true, "base");
        var baselinePools = Pools(baseline);
        var withNode = Character(armed: true, "node");
        withNode.Passives.Add(36978); // Exhibit 122: global_always_hit, dexterity_+% -50.
        var poolsNode = Pools(withNode);
        withNode.Pools = poolsNode;
        Assert.True(poolsNode.AlwaysHit);
        // The cannot-be-evaded flag is not an accuracy or crit value: the critical chance and multiplier match the
        // baseline (the node's separate -50% dexterity penalty moves only the attributes it names, at PassiveBonus).
        Assert.Equal(baselinePools.AttackCriticalStrikeChance, poolsNode.AttackCriticalStrikeChance);
        Assert.Equal(baselinePools.CriticalStrikeMultiplier, poolsNode.CriticalStrikeMultiplier);
        var panelNode = Panel(withNode);
        // Both reference rows (the level's normal and high-dexterity monster) read 100; the flagged character never
        // rolls a miss, including against that high-evasion reference.
        Assert.Equal(100, PanelValue(panelNode, "main_hand_chance_to_hit_%"));
        Assert.Equal(100, PanelValue(panelNode, "main_hand_chance_to_hit_evasive_monsters_%"));
        Assert.InRange(PanelValue(Panel(baseline), "main_hand_chance_to_hit_evasive_monsters_%"), 5, 99);
        // A dev /set_stat input reaches the same flag, /accuracy's rating stays, and both removals drop it again.
        var dev = Character(armed: true, "dev");
        Set(dev, "global_always_hit", 1);
        var poolsDev = Pools(dev);
        dev.Pools = poolsDev;
        Assert.True(poolsDev.AlwaysHit);
        Assert.Equal(baselinePools.Accuracy, poolsDev.Accuracy);
        Assert.Equal(100, PanelValue(Panel(dev), "main_hand_chance_to_hit_%"));
        Set(dev, "global_always_hit", 0);
        Assert.False(Pools(dev).AlwaysHit);
        withNode.Passives.Remove(36978);
        withNode.Pools = Pools(withNode);
        Assert.False(withNode.Pools.AlwaysHit);
        Assert.InRange(PanelValue(Panel(withNode), "main_hand_chance_to_hit_evasive_monsters_%"), 5, 99);
    }

    [Fact]
    public void Granted_gem_records_carry_the_multiplier_their_own_context_swallows()
    {
        var conn = Character();
        conn.Passives.Add(10135); // Final Blow: the unarmed melee attack's +50 and the copied panel row.
        conn.Pools = Pools(conn);
        PlayerInventories.For(_name + "gems", Program.Items);
        // Real equipment with the two real gems: the Melee-classified gem and Explosive Concoction.
        string[] gemNames = { "Cleave", "Explosive Concoction" };
        PlayerInventories.For(conn.Character!, Program.Items).Add(PlayerInventories.Helmet, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Iron Hat")!) with
            {
                Sockets = gemNames.Select(n =>
                {
                    var gem = Program.Items.FindGem(n)!.Value;
                    return new ItemSocket(SocketColour.Blue,
                        ItemCatalog.Create(gem.Type) with { GemVariant = gem.Variant.Index });
                }).ToArray(),
                Links = new byte[] { 0, 1 },
            });
        var placements = GemPlacements(conn);
        var records = GrantedGems(conn);
        Assert.Equal(placements.Count, records.Count);
        // The copied panel row itself is the unarmed melee attack's total (+50 with Final Blow).
        Assert.Equal(200, PanelValue(Panel(conn), "main_hand_critical_strike_multiplier"));
        var byEffect = placements.Zip(records, (p, r) => (p, r)).Where(x => Program.Skills.IsAttack(x.p.Effect)).ToList();
        // The attack gems' records serialize their own contexts: the Melee-classified one the unarmed +50,
        // the non-Melee classified one the plain value.
        var melee = byEffect.Single(x => Program.Skills.IsMelee(x.p.Effect));
        var nonMelee = byEffect.Single(x => !Program.Skills.IsMelee(x.p.Effect));
        Assert.Equal(200, RecordMultiplier(melee.r, "main_hand_critical_strike_multiplier"));
        // The unarmed non-melee attack gem keeps the plain attack multiplier; the leak does not reach its record.
        int concoction = nonMelee.p.Effect;
        Assert.True(Program.Skills.IsAttack(concoction));
        Assert.False(Program.Skills.IsMelee(concoction));
        Assert.Equal(150, RecordMultiplier(nonMelee.r, "main_hand_critical_strike_multiplier"));
        // The default attack's own multiplier stays the panel's unarmed melee total (+50); it travels in the
        // connection's stats, not the granted record (its Stats list carries damage, speed and reach rows only).
        Assert.Equal(200, PanelValue(Panel(conn), "main_hand_critical_strike_multiplier"));
        Assert.Equal(200, Program.CriticalMultiplierFor(conn, spell: false, melee: Program.MeleeAttack(conn)));
    }

    [Fact]
    public async Task The_set_stat_command_rebuilds_the_conditional_pool_inputs()
    {
        var conn = Character();
        conn.Pools = Pools(conn);
        var baseline = conn.Pools;
        Assert.False(baseline.AlwaysHit);
        Assert.Equal(0, baseline.LowLifeThresholdOverride);
        Assert.Equal(0, baseline.LowLifeThresholdPct);
        // The flag: the handler rebuilds the pools, so the panel and the roll state change with the command alone.
        await RunCommand(conn, "/set_stat global_always_hit 1");
        Assert.True(conn.Pools.AlwaysHit);
        Assert.Equal(100, PanelValue(Panel(conn), "main_hand_chance_to_hit_%"));
        // The flag is not a rating or crit value: both stay at the baseline.
        Assert.Equal(baseline.Accuracy, conn.Pools.Accuracy);
        Assert.Equal(baseline.AttackCriticalStrikeChance, conn.Pools.AttackCriticalStrikeChance);
        Assert.Equal(baseline.CriticalStrikeMultiplier, conn.Pools.CriticalStrikeMultiplier);
        await RunCommand(conn, "/set_stat global_always_hit 0");
        Assert.False(conn.Pools.AlwaysHit);
        // The Life Mastery-style threshold: the boundary moves with the command and stays inclusive there.
        conn.Pools = Pools(conn);
        await RunCommand(conn, "/set_stat low_life_threshold_%_override 55");
        Assert.Equal(55, conn.Pools.LowLifeThresholdOverride);
        conn.PlayerLife = (uint)Math.Floor(conn.PlayerMaxLife * 0.55);
        Assert.True(Program.OnLowLife(conn));
        conn.PlayerLife = (uint)Math.Floor(conn.PlayerMaxLife * 0.55) + 1;
        Assert.False(Program.OnLowLife(conn));
        // The override wins over the ordinary threshold stat.
        await RunCommand(conn, "/set_stat low_life_threshold_% 60");
        Assert.Equal(60, conn.Pools.LowLifeThresholdPct);
        conn.PlayerLife = (uint)Math.Floor(conn.PlayerMaxLife * 0.55);
        Assert.True(Program.OnLowLife(conn));
        conn.PlayerLife = (uint)Math.Floor(conn.PlayerMaxLife * 0.60) + 1;
        Assert.False(Program.OnLowLife(conn));
        // The dev replace: an explicit 0 override clears itself, and the ordinary threshold takes over.
        await RunCommand(conn, "/set_stat low_life_threshold_%_override 0");
        Assert.Equal(0, conn.Pools.LowLifeThresholdOverride);
        conn.PlayerLife = (uint)Math.Floor(conn.PlayerMaxLife * 0.60);
        Assert.True(Program.OnLowLife(conn));
        conn.PlayerLife = (uint)Math.Floor(conn.PlayerMaxLife * 0.60) + 1;
        Assert.False(Program.OnLowLife(conn));
    }

    [Fact]
    public async Task The_accuracy_diagnostic_reports_cannot_be_evaded_reference_chances()
    {
        var conn = Character();
        conn.Pools = Pools(conn);
        // The real handler's own reply: the rating and its increased form stay unconditional (no flag, no mastery).
        string before = Encoding.ASCII.GetString(await RunCommand(conn, "/accuracy 88"));
        int rating = conn.Pools.Accuracy;
        Assert.Contains($"Accuracy {rating} (", before);
        Assert.Contains("% chance to hit a level 88 monster", before);
        Assert.DoesNotContain("100% chance to hit", before);
        Assert.DoesNotContain("100% against evasive monsters", before);
        Assert.DoesNotContain("Targets gain", before);
        // The can't-be-evaded flag: both reference chances read 100, whatever the monster level's evasion.
        await RunCommand(conn, "/set_stat global_always_hit 1");
        Assert.True(conn.Pools.AlwaysHit);
        string flagged = Encoding.ASCII.GetString(await RunCommand(conn, "/accuracy 88"));
        Assert.Contains("100% chance to hit a level 88 monster", flagged);
        Assert.Contains("100% against evasive monsters", flagged);
        // The same exact rating form stays (the flag is not an accuracy value).
        Assert.Contains($"Accuracy {rating} (", flagged);
        // The removal restores the ordinary values again.
        await RunCommand(conn, "/set_stat global_always_hit 0");
        string after = Encoding.ASCII.GetString(await RunCommand(conn, "/accuracy 88"));
        Assert.Contains($"Accuracy {rating} (", after);
        Assert.DoesNotContain("100% chance to hit", after);
        Assert.DoesNotContain("100% against evasive monsters", after);
    }

    public void Dispose()
    {
        PlayerInventories.Forget(_name);
        PlayerInventories.Forget(_name + "gems");
        PlayerInventories.Forget(_name + "base");
        PlayerInventories.Forget(_name + "node");
        PlayerInventories.Forget(_name + "dev");
        PlayerInventories.Forget(_name + "armed");
        PlayerInventories.Forget(_name + "swapped");
    }
}
