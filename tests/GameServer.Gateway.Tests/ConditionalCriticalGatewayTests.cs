using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ConditionalCriticalGatewayTests
{
    sealed class Draws(params int[] values) : Random
    {
        public int Calls;
        public override int Next(int maxValue)
        {
            Assert.Equal(10000, maxValue);
            int index = Interlocked.Increment(ref Calls) - 1;
            Assert.True(index < values.Length, "Unexpected critical draw");
            return values[index];
        }
    }

    static MethodInfo Method(string name) => typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    sealed class Contact : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(), reader;
        readonly ConnectionCiphers ciphers = new(new byte[64], isClient: false);
        readonly ConnectionCiphers received = new(new byte[64], isClient: true);
        readonly string character = "Critical" + Guid.NewGuid().ToString("N");
        public ClientState Conn { get; }
        public SpawnedMonster Target { get; }
        public ushort Skill = SkillCatalog.FirstSkillId;
        public SemaphoreSlim SendGate => Program.SendGates.GetValue(ciphers, _ => new(1, 1));

        public Contact(Draws random, string? gemName = null, string? weaponName = null, int distance = 2,
            uint targetLife = 100000)
        {
            TestSaves.Ensure();
            Conn = new(new(ushort.MaxValue, "CriticalContact", 200, 200, 32, 32, (_, _) => true, []), 1, 1)
            { Character = character, ClassRow = 1, PlayerSent = true, PlayerLife = 50, PlayerMana = 1000,
                CriticalRandom = random, Instance = new InstanceState() };
            var inventory = PlayerInventories.For(character, Program.Items);
            if (weaponName is not null)
                inventory.Add(PlayerInventories.Weapon1, 0, 0, ItemCatalog.Create(Program.Items.Find(weaponName)!));
            if (gemName is not null)
            {
                var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGem" + gemName)!);
                var support = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemIncreasedCriticalDamage")!);
                var host = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!) with
                { Sockets = [new(SocketColour.White, gem), new(SocketColour.White, support)], Links = [2] };
                inventory.Add(PlayerInventories.BodyArmour, 0, 0, host);
                Skill = Program.GemSkillIdPool.Value[0];
            }
            Set(AttackBaseCriticalStrike.UnarmedBaseStat, 1000);
            Set(AttackBaseCriticalStrike.WeaponOverrideStat, 1000);
            Set("extra_critical_rolls_while_on_low_life", 1);
            Set("physical_damage_+%", -100);
            Set("attack_minimum_added_chaos_damage", 1000);
            Set("attack_maximum_added_chaos_damage", 1000);
            Set("spell_minimum_added_chaos_damage", 1000);
            Set("spell_maximum_added_chaos_damage", 1000);
            Set("attack_speed_+%", 200);
            Refresh();
            Target = new("critical target", new(32 + distance, 32, 30), 0, 2, targetLife);
            Conn.Monsters.Add(42, Target);
            Conn.Clock.Start();
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }

        public void Set(string name, int value) => Conn.PlayerStats[Program.Stats.Key(name)!.Value] = value;
        public void Refresh() => Conn.Pools = Program.PoolsFor(Conn, 40) with
        { Life = 100, Mana = 1000, Accuracy = int.MaxValue, Strength = 1000, Dexterity = 1000, Intelligence = 1000 };
        public int Multiplier => Skill == SkillCatalog.FirstSkillId
            ? Program.AttackCriticalMultiplier(Conn, Program.CharacterPlayerStats(Conn))
            : (int)Method("SupportedCriticalMultiplier").Invoke(null, [Conn, Skill])!;
        public DamagePacket Hit => (DamagePacket)Method("SupportedHit").Invoke(null, [Conn, Skill])!;

        public Task Use()
        {
            var bytes = new LoginWriter().U16(0x24).U32(42).U16(Skill).U16(0).U16(0x409).ToArray();
            var use = InstanceTransfer.ReadUseSkillOnObject(bytes);
            return ((Task)Method("OnUseObject").Invoke(null,
                ["critical-contact-test", writer.GetStream(), ciphers, Conn, use, Conn.Clock.Elapsed.TotalSeconds])!)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async Task HitLands()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Target.Life == Target.MaxLife) await Task.Delay(10, timeout.Token);
        }

        public async Task Refund(ushort node)
        {
            var bytes = new LoginWriter().U16(0x36).U32(node).U8(0).U8(0).ToArray();
            await Program.OnClientMessages("critical-refund-test", writer.GetStream(), ciphers,
                [(InstanceEntities.RefundPassiveId, bytes)], [], Conn);
        }

        public Task RunPanelTick() => (Task)Method("RunPlayerTick").Invoke(null,
            ["critical-panel-test", writer.GetStream(), ciphers, Conn])!;

        public IReadOnlyList<GrantedSkill> CurrentSkills() =>
            ((IEnumerable<GrantedSkill>)Method("PlayerSkills").Invoke(null, [Conn])!).ToList();

        public Task RefreshStats(bool charges) => (Task)Method(charges ? "SendChargeStats" : "SendCharacterStats")
            .Invoke(null, ["critical-queued-stats-test", writer.GetStream(), ciphers, Conn, "critical context"])!;

        public async Task AssertNextPlayerStats()
        {
            var stats = ((IEnumerable<(uint, int)>)Method("FinalPlayerStats").Invoke(null, [Conn])!).ToList();
            await AssertNextPacket(InstanceEntities.StatsUpdate(Program.PlayerId, stats));
        }

        public Task AssertNextActorStats() => AssertNextPacket(
            InstanceEntities.PlayerSkillsChange(Program.PlayerId, CurrentSkills(), []));

        async Task AssertNextPacket(byte[] expected)
        {
            byte[] actual = new byte[expected.Length];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await reader.GetStream().ReadExactlyAsync(actual, timeout.Token);
            received.Receive.Process(actual);
            Assert.Equal(expected, actual);
        }

        public void Dispose()
        {
            Conn.Closed = true;
            writer.Dispose(); reader.Dispose(); listener.Stop();
            PlayerInventories.Forget(character);
            Program.GemSkillIdsOf.TryRemove(character, out _);
        }
    }

    [Theory]
    [InlineData(50u, 0, 2, true)]
    [InlineData(51u, 0, 1, false)]
    [InlineData(50u, -1, 1, false)]
    [InlineData(50u, 1, 2, true)]
    [InlineData(50u, 100, 2, true)]
    [InlineData(50u, -100, 2, false)]
    public async Task Parsed_default_contact_uses_live_life_and_signed_luck(uint life, int ordinary, int calls, bool crit)
    {
        var random = new Draws(9000, 0);
        using var f = new Contact(random);
        f.Set("extra_critical_rolls", ordinary);
        f.Set(CriticalStrikePolicy.UnarmedMeleeMultiplierStat, 50);
        var use = f.Use();
        f.Conn.PlayerLife = life; // Action already accepted; contact is still waiting.
        await use;
        Assert.Equal(calls, random.Calls);
        Assert.Equal(crit ? 2000u : 1000u, f.Target.MaxLife - f.Target.Life);
    }

    [Theory]
    [InlineData(50, false, 200, 14)]
    [InlineData(0, false, 150, 12)]
    [InlineData(50, true, 100, 10)]
    public async Task Parsed_default_critical_multiplier_also_scales_the_direct_Fortification_source(
        int unarmedMultiplier, bool cannotCrit, int damage, int stacks)
    {
        var random = new Draws(0);
        using var f = new Contact(random, targetLife: 400);
        f.Set("attack_minimum_added_chaos_damage", 100);
        f.Set("attack_maximum_added_chaos_damage", 100);
        f.Set(CriticalStrikePolicy.UnarmedMeleeMultiplierStat, unarmedMultiplier);
        f.Set("extra_critical_rolls_while_on_low_life", 0);
        f.Set("global_cannot_crit", cannotCrit ? 1 : 0);
        f.Set(FortificationSession.OnMeleeHit, 1);
        f.Refresh();
        Assert.Empty(f.Conn.Passives); // Ordinary raw-source control; no Phrecia class or node allocation.
        await f.Use();
        Assert.Equal(400u - (uint)damage, f.Target.Life);
        Assert.Equal(stacks, Program.FortificationOf(f.Conn).Read(f.Conn.Clock.Elapsed.TotalSeconds).Current);
        Assert.Equal(cannotCrit ? 0 : 1, random.Calls);
    }

    [Theory]
    [InlineData("Cyclone", null)]
    [InlineData("IceNova", null)]
    [InlineData("Fireball", null)]
    [InlineData("BurningArrow", "Crude Bow")]
    public async Task Parsed_supported_area_and_projectile_hits_share_luck_and_context_multiplier(string gem, string? weapon)
    {
        var random = new Draws(9000, 0);
        using var f = new Contact(random, gem, weapon);
        int baseline = f.Multiplier;
        Assert.True(baseline > 150); // The real linked Increased Critical Damage Support is applied.
        f.Set(CriticalStrikePolicy.UnarmedMeleeMultiplierStat, 50);
        var hit = f.Hit;
        int multiplier = f.Multiplier;
        Assert.Equal(baseline + (gem == "Cyclone" ? 50 : 0), multiplier);
        var panel = ((IEnumerable<GrantedSkill>)Method("PlayerSkills").Invoke(null, [f.Conn])!).Single(s => s.Id == f.Skill);
        uint multiplierKey = Program.Stats.Key(gem is "Cyclone" or "BurningArrow"
            ? "main_hand_critical_strike_multiplier" : "spell_critical_strike_multiplier")!.Value;
        Assert.Contains((multiplierKey, multiplier), panel.Stats!);
        Assert.Contains((Program.Stats.Key("virtual_extra_critical_rolls")!.Value, 1), panel.Stats!);
        // The low-life condition is read when the hit resolves: at the contact for a swing or a cast, at once for a
        // channelled skill (Cyclone's first sweep lands as it starts, RunChannel).
        f.Conn.PlayerLife = gem == "Cyclone" ? 50u : 51u;
        await f.Use();
        f.Conn.PlayerLife = 50;
        await f.HitLands();
        Assert.Equal(2, random.Calls);
        Assert.InRange((int)(f.Target.MaxLife - f.Target.Life),
            DamageModel.CriticalDamage((int)Math.Round(hit.MinTotal), multiplier),
            DamageModel.CriticalDamage((int)Math.Round(hit.MaxTotal), multiplier));
    }

    [Theory]
    [InlineData("global_cannot_crit", null)]
    [InlineData("virtual_global_cannot_crit", "Fireball")]
    [InlineData("cannot_critical_strike_with_attacks", "Cyclone")]
    public async Task Parsed_cannot_crit_takes_precedence_and_consumes_no_critical_draw(string stat, string? gem)
    {
        var random = new Draws();
        using var f = new Contact(random, gem);
        f.Set(stat, 1);
        f.Refresh();
        await f.Use();
        await f.HitLands();
        Assert.Equal(0, random.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Released_ordinary_bow_reads_life_luck_and_prohibition_at_impact(bool cannotCrit)
    {
        var random = new Draws(9000, 0);
        using var f = new Contact(random, weaponName: "Crude Bow", distance: 60);
        f.Set(CriticalStrikePolicy.UnarmedMeleeMultiplierStat, 50);
        f.Conn.PlayerLife = 51;
        Task use;
        using (Program.ProjectileMutation(f.Conn, advance: false))
        {
            // Accept the real bow action while unpaused, then freeze the pump before it can release or hit.
            use = f.Use();
            f.Conn.Clock.Stop();
            try
            {
                var session = Assert.IsType<Program.ProjectileSession>(f.Conn.ProjectileRuntime);
                long deadline = session.Runtime.Now + ProjectileRuntime.Time(2);
                while (session.Runtime.Projectiles.Count == 0 && session.Runtime.Now < deadline)
                    session.AdvanceTo(session.Runtime.Now + ProjectileRuntime.TickQuanta / 10);
                Assert.Single(session.Runtime.Projectiles);
                Assert.Equal(0, random.Calls);
                Assert.Equal(f.Target.MaxLife, f.Target.Life);
                f.Conn.PlayerLife = 50;
                f.Set("global_cannot_crit", cannotCrit ? 1 : 0);
                session.AdvanceTo(session.Runtime.Now + ProjectileRuntime.Time(1));
            }
            finally { f.Conn.Clock.Start(); }
        }
        await use;
        Assert.Equal(cannotCrit ? 0 : 2, random.Calls);
        Assert.Equal(cannotCrit ? 1000u : 1500u, f.Target.MaxLife - f.Target.Life);
    }

    [Fact]
    public async Task Raw_node_stat_removal_and_unselected_ascendancy_refund_keep_lifecycle_separate()
    {
        using var f = new Contact(new Draws());
        f.Conn.PlayerStats.Clear();
        f.Conn.RefundPoints = 5;
        var raw = Program.Passives.Stats([10135]);
        Assert.Equal(1000, raw[Program.Stats.Key(AttackBaseCriticalStrike.UnarmedBaseStat)!.Value]);
        Assert.Equal(1, raw[Program.Stats.Key("extra_critical_rolls_while_on_low_life")!.Value]);
        Assert.Equal(50, raw[Program.Stats.Key(CriticalStrikePolicy.UnarmedMeleeMultiplierStat)!.Value]);
        foreach (var (key, value) in raw) f.Conn.PlayerStats[key] = value;
        f.Refresh();
        Assert.Equal(200, f.Multiplier);
        Assert.Equal(1, Program.CriticalExtraRolls(f.Conn));
        // Raw-source controls allocate no node or Phrecia choice; upstream selection and play gates remain in force.
        await f.Refund(10135);
        Assert.Empty(f.Conn.Passives);
        Assert.Null(f.Conn.MainAscendancy.Selection);
        Assert.Empty(f.Conn.MainAscendancy.Allocated);
        Assert.Equal(5, f.Conn.RefundPoints);
        Assert.Equal(200, f.Multiplier);
        foreach (uint key in raw.Keys) f.Conn.PlayerStats.Remove(key);
        f.Refresh();
        Assert.Equal(150, f.Multiplier);
        Assert.Equal(0, Program.CriticalExtraRolls(f.Conn));
        var panel = Program.CharacterPlayerStats(f.Conn);
        Assert.Equal(150, panel[Program.Stats.Key("main_hand_critical_strike_multiplier")!.Value]);
        Assert.Equal(0, panel[Program.Stats.Key("virtual_extra_critical_rolls")!.Value]);
    }

    [Fact]
    public void Active_weapon_sets_and_selected_spell_or_ranged_attack_exclude_unarmed_multiplier()
    {
        using var f = new Contact(new Draws(), "Fireball");
        f.Set(CriticalStrikePolicy.UnarmedMeleeMultiplierStat, 50);
        var inventory = PlayerInventories.For(f.Conn.Character!, Program.Items);
        inventory.Add(PlayerInventories.Weapon2, 0, 0, ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!));
        Assert.Equal(200, Program.AttackCriticalMultiplier(f.Conn, Program.CharacterPlayerStats(f.Conn)));
        foreach (string effect in new[] { "Fireball", "BurningArrow" })
            Assert.Equal(150, Program.AttackCriticalMultiplier(f.Conn, Program.CharacterPlayerStats(f.Conn), Program.Skills.EffectRow(effect)));
        inventory.SwapWeaponSets();
        f.Refresh();
        Assert.Equal(150, Program.AttackCriticalMultiplier(f.Conn, Program.CharacterPlayerStats(f.Conn)));
        inventory.SwapWeaponSets();
        f.Refresh();
        Assert.Equal(200, Program.AttackCriticalMultiplier(f.Conn, Program.CharacterPlayerStats(f.Conn)));
    }

    [Fact]
    public void Live_owner_conditions_sum_before_clamp_and_panel_virtuals_follow_them()
    {
        using var f = new Contact(new Draws());
        f.Set("extra_critical_rolls", -2);
        f.Set("extra_critical_rolls_during_focus", 1);
        f.Set("keystone_sharp_and_brittle", 1);
        Assert.Equal(0, Program.CriticalExtraRolls(f.Conn)); // -2 + low life + Sharp and Brittle.
        f.Set("has_focus", 1);
        Assert.Equal(1, Program.CriticalExtraRolls(f.Conn));
        f.Conn.PlayerLife = 51;
        Assert.Equal(0, Program.CriticalExtraRolls(f.Conn));
        f.Set("low_life_threshold_%_override", 55);
        Assert.Equal(1, Program.CriticalExtraRolls(f.Conn));
        f.Conn.PlayerLife = 56;
        f.Set("you_count_as_low_life_while_not_on_full_life", 1);
        Assert.Equal(1, Program.CriticalExtraRolls(f.Conn));
        var panel = Program.CharacterPlayerStats(f.Conn);
        Assert.Equal(1, panel[Program.Stats.Key("virtual_on_low_life")!.Value]);
        Assert.Equal(1, panel[Program.Stats.Key("virtual_extra_critical_rolls")!.Value]);
        f.Conn.PlayerLife = 100;
        Assert.Equal(0, Program.CriticalExtraRolls(f.Conn));
        f.Set("you_count_as_low_life_while_affected_by_vulnerability", 1);
        f.Set("cursed_by_vulnerability", 1);
        Assert.Equal(1, Program.CriticalExtraRolls(f.Conn));
        f.Conn.PlayerLife = 0;
        Assert.Equal(0, Program.CriticalExtraRolls(f.Conn));
    }

    [Fact]
    public async Task Attack_only_prohibition_does_not_disable_a_low_life_spell()
    {
        var random = new Draws(9000, 0);
        using var f = new Contact(random, "Fireball");
        f.Set("cannot_critical_strike_with_attacks", 1);
        f.Refresh();
        await f.Use();
        await f.HitLands();
        Assert.Equal(2, random.Calls);
    }

    [Fact]
    public void Unarmed_ranged_gem_does_not_inherit_the_melee_base_or_multiplier()
    {
        using var f = new Contact(new Draws(), "BurningArrow");
        f.Set(CriticalStrikePolicy.UnarmedMeleeMultiplierStat, 50);
        Assert.Equal(0, (int)Method("SupportedCriticalChance").Invoke(null, [f.Conn, f.Skill])!);
        Assert.Equal(1, Program.CriticalExtraRolls(f.Conn)); // Luck itself has no melee gate.
        Assert.Equal(250, f.Multiplier); // Only the linked support's level-one +100 multiplier.
    }

    [Fact]
    public async Task Live_life_boundary_refreshes_native_player_stats_without_a_new_action()
    {
        using var f = new Contact(new Draws());
        var ticking = f.RunPanelTick();
        try
        {
            f.Conn.PlayerLife = 51;
            await f.AssertNextPlayerStats();
        }
        finally
        {
            f.Conn.Closed = true;
            await ticking.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task Queued_full_and_Actor_refreshes_sample_current_life_and_active_weapon(
        bool charges, bool becomesLowLife, bool becomesArmed)
    {
        using var f = new Contact(new Draws(), "Cyclone");
        f.Set(CriticalStrikePolicy.UnarmedMeleeMultiplierStat, 50);
        var inventory = PlayerInventories.For(f.Conn.Character!, Program.Items);
        inventory.Add(PlayerInventories.Weapon2, 0, 0, ItemCatalog.Create(Program.Items.Find("Driftwood Wand")!));
        if (!becomesArmed) inventory.SwapWeaponSets();
        f.Refresh();
        f.Conn.PlayerLife = becomesLowLife ? 100u : 50u;
        f.Conn.ClientSkillIds = [.. f.CurrentSkills().Select(s => s.Id)]; // Keep the charge-only fast path eligible.
        await f.SendGate.WaitAsync();
        Task refresh;
        try
        {
            refresh = f.RefreshStats(charges);
            Assert.False(refresh.IsCompleted);
            lock (f.Conn.Monsters)
            {
                f.Conn.PlayerLife = becomesLowLife ? 50u : 100u;
                inventory.SwapWeaponSets();
                f.Refresh();
            }
        }
        finally { f.SendGate.Release(); }
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        int lowLife = becomesLowLife ? 1 : 0;
        var player = Program.CharacterPlayerStats(f.Conn);
        uint lifeKey = Program.Stats.Key("virtual_on_low_life")!.Value;
        uint rollsKey = Program.Stats.Key("virtual_extra_critical_rolls")!.Value;
        uint multiplierKey = Program.Stats.Key("main_hand_critical_strike_multiplier")!.Value;
        Assert.Equal(lowLife, player[lifeKey]);
        Assert.Equal(lowLife, player[rollsKey]);
        Assert.Equal(becomesArmed ? 150 : 200, player[multiplierKey]);
        var selected = f.CurrentSkills().Single(s => s.Id == f.Skill);
        Assert.Contains((lifeKey, lowLife), selected.Stats!);
        Assert.Contains((rollsKey, lowLife), selected.Stats!);
        Assert.Contains((multiplierKey, becomesArmed ? 250 : 300), selected.Stats!);
        await f.AssertNextPlayerStats();
        await f.AssertNextActorStats();
    }
}
