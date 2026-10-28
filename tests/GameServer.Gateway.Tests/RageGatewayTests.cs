using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class RageGatewayTests
{
    static object Call(string name, params object[] args) => typeof(Program)
        .GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args)!;
    static void Set(ClientState conn, string stat, int value) => conn.PlayerStats[Program.Stats.Key(stat)!.Value] = value;
    static int Current(ClientState conn) => Program.CharacterPlayerStats(conn)[Program.Stats.Key("current_rage")!.Value];
    static RageState.Inputs Input(ClientState conn) => RageStats.Inputs(name =>
        Program.Stats.Key(name) is uint key ? Program.CharacterPlayerStats(conn).GetValueOrDefault(key) : 0);
    static DamagePacket Hit(ClientState conn, ushort skill) => (DamagePacket)Call("SupportedHit", conn, skill);

    [Theory]
    [InlineData(15)]
    [InlineData(29)]
    public void Actual_default_attack_panel_keeps_fixed_hundred_chaos_at_integer_Rage_boundaries(int held)
    {
        using var f = new Fixture(); f.Conn.Clock.Stop();
        Set(f.Conn, "attack_minimum_added_chaos_damage", 100);
        Set(f.Conn, "attack_maximum_added_chaos_damage", 100);
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100);
        var inputs = Input(f.Conn);
        Assert.True(f.Conn.Rage.Gain(held, f.Conn.Clock.Elapsed.TotalSeconds, inputs));
        var actual = Program.CharacterPlayerStats(f.Conn);
        Assert.Equal(held, actual[Program.Stats.Key("effective_rage_for_per_rage_effects")!.Value]);
        Assert.Equal(100 + held, actual[Program.Stats.Key("main_hand_minimum_chaos_damage")!.Value]);
        Assert.Equal(100 + held, actual[Program.Stats.Key("main_hand_maximum_chaos_damage")!.Value]);
    }

    public static IEnumerable<object[]> DamageCases()
    {
        foreach (int held in Enumerable.Range(0, 46)) yield return [held, 0];
        foreach (var (held, effect) in new[] { (10, 50), (30, 50), (45, 50), (29, 100), (45, -50), (45, -100) })
            yield return [held, effect];
    }

    [Theory]
    [MemberData(nameof(DamageCases))]
    public void Actual_pools_default_supported_and_bow_panels_preserve_fixed_hundred_chaos_and_mana_lightning(int held, int effect)
    {
        using var f = new Fixture("Metadata/Items/Gems/SkillGemBurningArrow", "Crude Bow");
        f.Conn.Clock.Stop();
        Set(f.Conn, "maximum_rage", 15); Set(f.Conn, "rage_effect_+%", effect);
        Set(f.Conn, "attack_minimum_added_chaos_damage", 100); Set(f.Conn, "attack_maximum_added_chaos_damage", 100);
        Set(f.Conn, "base_maximum_mana", 1000); Set(f.Conn, DamageStats.MaxManaAddedLightning, 5);
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100);
        var inputs = Input(f.Conn);
        f.Conn.Rage.Gain(held, f.Conn.Clock.Elapsed.TotalSeconds, inputs);
        int effective = (int)decimal.Truncate(held * Math.Max(0m, 100m + effect) / 100);
        int expected = 100 + effective;
        var character = Program.CharacterPlayerStats(f.Conn);
        Assert.Equal(held, character[Program.Stats.Key("current_rage")!.Value]);
        Assert.Equal(effective, character[Program.Stats.Key("effective_rage_for_per_rage_effects")!.Value]);
        Assert.Equal(effective, character[Program.Stats.Key("attack_damage_+%_final_from_rage")!.Value]);
        Assert.Equal((int)f.Conn.Pools.Mana, character[Program.Stats.Key("maximum_mana")!.Value]);
        Assert.Equal(expected, character[Program.Stats.Key("main_hand_minimum_chaos_damage")!.Value]);
        Assert.Equal(expected, character[Program.Stats.Key("main_hand_maximum_chaos_damage")!.Value]);
        var weapon = (SkillCatalog.Unarmed)Call("WeaponFor", f.Conn);
        var ordinary = Program.AttackHit(weapon, character, melee: false);
        var bow = OrdinaryBowShot.Capture(weapon, character, [], Program.Stats, f.Conn.Pools.Accuracy, 0, 150).Hit;
        foreach (var hit in new[] { ordinary, bow })
        {
            Assert.Equal(new DamageRange(expected, expected), hit[DamageType.Chaos]);
            Assert.Equal(expected, hit[DamageType.Chaos].Roll(new Random(1)));
            double lightning = (double)(f.Conn.Pools.Mana * .05m * (100 + effective) / 100);
            Assert.Equal(lightning, hit[DamageType.Lightning].Min, 10);
        }
        int skillEffectiveness = Program.Skills.DamageEffectiveness(Program.Skills.EffectRow("BurningArrow"), 1);
        decimal skillChaos = 100m * skillEffectiveness / 10000 * (100 + effective) / 100;
        var supported = Hit(f.Conn, f.Skill);
        Assert.Equal((double)skillChaos, supported[DamageType.Chaos].Min, 10);
        Assert.Equal((double)skillChaos, supported[DamageType.Chaos].Max, 10);
        var skills = (IReadOnlyList<GrantedSkill>)Call("PlayerSkills", f.Conn);
        foreach (var (skill, chaos) in new[] { (SkillCatalog.FirstSkillId, expected), (f.Skill, (int)decimal.Floor(skillChaos)) })
        {
            var stats = skills.Single(s => s.Id == skill).Stats!.ToDictionary(s => s.Item1, s => s.Item2);
            Assert.Equal(chaos, stats[Program.Stats.Key("main_hand_minimum_chaos_damage")!.Value]);
            Assert.Equal(chaos, stats[Program.Stats.Key("main_hand_maximum_chaos_damage")!.Value]);
        }
    }

    [Theory]
    [MemberData(nameof(RageAttackScalingTests.Percentages), MemberType = typeof(RageAttackScalingTests))]
    public void Actual_dual_wield_main_and_offhand_native_physical_bounds_keep_fixed_hundred_exact(int held)
    {
        using var f = new Fixture(); f.Conn.Clock.Stop();
        var type = Program.Items.Find("Driftwood Club")!;
        var basis = Program.Skills.WeaponBase(type.Row)!.Value;
        var local = Program.Items.Mods.All.First(m => m.Domain == 1 && m.Stats.Count == 2
            && m.Stats[0].Id == "local_minimum_added_physical_damage" && m.Stats[1].Id == "local_maximum_added_physical_damage"
            && m.Stats.All(s => s.Local));
        var weapon = ItemCatalog.Create(type) with
            { Mods = [new(ModSection.Explicit, local.Hash, [100 - basis.Min, 100 - basis.Max])] };
        foreach (var item in f.Items.Items(PlayerInventories.Weapon1).ToList())
            f.Items.Remove(PlayerInventories.Weapon1, item.Id);
        f.Items.Add(PlayerInventories.Weapon1, 0, 0, weapon); f.Items.Add(PlayerInventories.Offhand1, 0, 0, weapon);
        foreach (string stat in new[] { "damage_+%", "physical_damage_+%", "melee_damage_+%", "melee_physical_damage_+%" })
            Set(f.Conn, stat, 0);
        Set(f.Conn, "maximum_rage", 15);
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100);
        f.Conn.Rage.Gain(held, f.Conn.Clock.Elapsed.TotalSeconds, Input(f.Conn));
        Assert.True(Program.WeaponProfileFor(f.Conn, activeSet: true).DualWield);
        var main = (SkillCatalog.Unarmed)Call("WeaponFor", f.Conn);
        Assert.Equal(100, main.MinDamage); Assert.Equal(100, main.MaxDamage);
        var stats = ((IEnumerable<(uint, int)>)Call("FinalPlayerStats", f.Conn)).ToDictionary(s => s.Item1, s => s.Item2);
        foreach (uint key in new[] { StatKeys.MainHandMinPhysical, StatKeys.MainHandMaxPhysical,
            StatKeys.OffHandMinPhysical, StatKeys.OffHandMaxPhysical, StatKeys.MainHandMinTotal,
            StatKeys.MainHandMaxTotal, StatKeys.OffHandMinTotal, StatKeys.OffHandMaxTotal })
            Assert.Equal(100 + held, stats[key]);
    }

    [Fact]
    public void Production_loss_rate_and_delay_recovery_read_distinct_native_stat_keys()
    {
        using var f = new Fixture();
        Set(f.Conn, "rage_decay_speed_+%", -20);
        Set(f.Conn, "rage_loss_delay_recovery_rate_+%", 100);
        var inputs = Input(f.Conn);
        Assert.Equal(8, inputs.LossPerSecond); Assert.Equal(1, inputs.LossDelaySeconds);
        Assert.Equal(12216u, Program.Stats.Key("rage_decay_speed_+%"));
        Assert.Equal(20820u, Program.Stats.Key("rage_loss_delay_recovery_rate_+%"));
    }

    sealed class Fixture : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new(), reader;
        readonly ConnectionCiphers receive = new(new byte[64], isClient: true);
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        public readonly ClientState Conn;
        public readonly SpawnedMonster Target;
        public readonly PlayerInventories Items;
        public NetworkStream Stream => writer.GetStream();
        public SemaphoreSlim Gate => Program.SendGates.GetValue(Ciphers, _ => new(1, 1));
        public ushort Skill => Program.GemSkillIdPool.Value[0];
        public Fixture(string? gem = null, string weapon = "Driftwood Club", bool rageSupport = false,
            uint targetLife = 100000)
        {
            TestSaves.Ensure();
            Conn = new(new AreaLayout(1, "test", 300, 300, 100, 100, null, []),
                (uint)Random.Shared.Next(1, int.MaxValue), 1)
            {
                Character = $"Rage{Guid.NewGuid():N}", PlayerLife = 1000, PlayerMana = 1000, PlayerSent = true,
                Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 500, Dexterity: 500, Intelligence: 500),
                SentEnergyShield = 0, SentRechargeActive = false, SentEsRecoveryRate = 0
            };
            Items = PlayerInventories.For(Conn.Character, Program.Items);
            var host = ItemCatalog.Create(Program.Items.Find(weapon)!);
            if (gem is not null)
            {
                var active = ItemCatalog.Create(Program.Items.Find(gem)!);
                host = host with { Sockets = rageSupport
                    ? [new(SocketColour.White, active), new(SocketColour.White,
                        ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SupportGemRage")!))]
                    : [new(SocketColour.White, active)], Links = rageSupport ? [2] : [1] };
            }
            Items.Add(PlayerInventories.Weapon1, 0, 0, host);
            Set(Conn, "no_inherent_rage_loss", 1);
            Set(Conn, "global_cannot_crit", 1);
            Target = new("rage target", new(105, 100, 10, width: 300, height: 300), 0, 2, targetLife);
            Conn.Monsters.Add(42, Target);
            listener.Start(); var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            reader = accepting.GetAwaiter().GetResult();
        }

        public Task Dispatch(bool ground = false, ushort? skill = null)
        {
            ushort id = ground ? InstanceProtocol.UseSkillAtLocationId : InstanceTransfer.UseSkillOnObjectId;
            var packet = ground
                ? new LoginWriter().U16(id).U32(105).U32(100).U16(skill ?? Skill).U16(1).U16(1).ToArray()
                : new LoginWriter().U16(id).U32(42).U16(skill ?? SkillCatalog.FirstSkillId).U16(1).U16(1).ToArray();
            return Program.OnClientMessages("rage-dispatch-test", Stream, Ciphers, [(id, packet)], [], Conn);
        }

        public Task Sweep()
        {
            var gem = ((IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)
                Call("GemSkillEffects", Conn.Character!)).Single();
            return (Task)typeof(Program).GetMethod("AreaSkillHit", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, ["rage-sweep-test", Stream, Ciphers, Conn, Skill, gem,
                    (100, 100), (105, 100), true, (ushort)1, Conn.ActionCount, null])!;
        }

        public async Task ObserveLife()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var bytes = new List<byte>(); byte[] chunk = new byte[65536];
            while (true)
            {
                int count = await reader.GetStream().ReadAsync(chunk, timeout.Token);
                Assert.True(count > 0);
                receive.Receive.Process(chunk.AsSpan(0, count));
                bytes.AddRange(chunk.Take(count));
                if (bytes.ToArray().AsSpan().IndexOf(InstanceProtocol.LifeUpdate(42, Target.Life)) >= 0) return;
            }
        }

        public int Available => reader.Available;

        public async Task<byte[]> Receive(int count)
        {
            byte[] bytes = new byte[count];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await reader.GetStream().ReadExactlyAsync(bytes, timeout.Token);
            receive.Receive.Process(bytes);
            return bytes;
        }

        public void Dispose()
        {
            Conn.Closed = true; Conn.Clock.Start(); writer.Dispose(); reader.Dispose(); listener.Stop();
            PlayerInventories.Forget(Conn.Character!);
        }
    }

    [Theory]
    [InlineData("Driftwood Club", 15)]
    [InlineData("Crude Bow", 7)]
    public async Task Parsed_ordinary_attacks_gain_only_their_attack_melee_and_critical_sources(string weapon, int expected)
    {
        using var f = new Fixture(weapon: weapon);
        Set(f.Conn, "global_cannot_crit", 0);
        f.Conn.Pools = f.Conn.Pools with { AttackCriticalStrikeChance = 10000 };
        foreach (var (name, value) in new[] { ("gain_x_rage_on_hit", 1), ("gain_x_rage_on_attack_hit", 2),
            ("gain_x_rage_on_melee_hit", 3), ("gain_x_rage_on_attack_crit", 4), ("gain_x_rage_on_melee_crit", 5) })
            Set(f.Conn, name, value);
        await f.Dispatch(); await f.ObserveLife();
        Assert.True(f.Target.Life < f.Target.MaxLife);
        Assert.Equal(expected, Current(f.Conn));
    }

    [Theory]
    [InlineData(0, 0, 100, 10)]
    [InlineData(0, 5, 100, 10)]
    [InlineData(40, 5, 140, 11)]
    public async Task Parsed_melee_contact_uses_existing_Rage_for_actual_damage_and_Fortification_before_gaining_more(
        int held, int gain, int damage, int fortification)
    {
        using var f = new Fixture(targetLife: 400);
        Set(f.Conn, "maximum_rage", 15); Set(f.Conn, "gain_x_rage_on_attack_hit", gain);
        Set(f.Conn, FortificationSession.OnMeleeHit, 1);
        Set(f.Conn, "attack_minimum_added_chaos_damage", 100); Set(f.Conn, "attack_maximum_added_chaos_damage", 100);
        Set(f.Conn, "physical_damage_+%", -100); Set(f.Conn, "melee_physical_damage_+%", 0);
        Set(f.Conn, "damage_+%", 0); Set(f.Conn, "attack_damage_+%", 0); Set(f.Conn, "melee_damage_+%", 0);
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100) with { Accuracy = int.MaxValue };
        var inputs = Input(f.Conn);
        f.Conn.Rage.Gain(held, f.Conn.Clock.Elapsed.TotalSeconds, inputs);
        var before = Program.CharacterPlayerStats(f.Conn);
        var weapon = (SkillCatalog.Unarmed)Call("WeaponFor", f.Conn);
        var packet = Program.AttackHitConverted(weapon, before, melee: true, new DamageConversion());
        Assert.Equal(new DamageRange(damage, damage), packet[DamageType.Chaos]);
        Assert.Equal(0, packet[DamageType.Physical].Max);
        Assert.Equal(0, Program.FortificationOf(f.Conn).Value.Current);
        await f.Dispatch(); await f.ObserveLife();
        Assert.Equal((uint)(400 - damage), f.Target.Life);
        Assert.Equal(fortification, Program.FortificationOf(f.Conn).Value.Current);
        Assert.Equal(held + gain, Current(f.Conn));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    public void Real_Whisperer_and_Steel_Willed_sources_stay_additive_before_Rage_and_after_refund(int held)
    {
        using var f = new Fixture("Metadata/Items/Gems/SkillGemBurningArrow", "Crude Bow");
        f.Conn.Clock.Stop(); f.Conn.Passives.AddRange([50932, 2103]);
        Set(f.Conn, "maximum_rage", 15); Set(f.Conn, "spell_damage_+%", 1);
        Set(f.Conn, "physical_damage_reduction_rating", 900);
        Set(f.Conn, "attack_minimum_added_chaos_damage", 100); Set(f.Conn, "attack_maximum_added_chaos_damage", 100);
        Set(f.Conn, "base_maximum_mana", 1000); Set(f.Conn, DamageStats.MaxManaAddedLightning, 6);
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100);
        f.Conn.Rage.Gain(held, f.Conn.Clock.Elapsed.TotalSeconds, Input(f.Conn));
        var stats = Program.CharacterPlayerStats(f.Conn);
        Assert.Equal(900, stats[Program.Stats.Key("physical_damage_reduction_rating")!.Value]);
        Assert.Equal(4, stats[Program.Stats.Key("attack_damage_+%")!.Value]);
        Assert.Equal(1, stats[Program.Stats.Key(DamageStats.SpellDamageIncreasesApplyToAttacks150)!.Value]);
        var weapon = (SkillCatalog.Unarmed)Call("WeaponFor", f.Conn);
        var hit = Program.AttackHitConverted(weapon, stats, false, new DamageConversion());
        decimal expected = 105.5m * (100 + held) / 100;
        Assert.Equal((double)expected, hit[DamageType.Chaos].Min, 10);
        Assert.Equal((double)(f.Conn.Pools.Mana * .06m * 1.055m * (100 + held) / 100),
            hit[DamageType.Lightning].Min, 10);
        int effectiveness = Program.Skills.DamageEffectiveness(Program.Skills.EffectRow("BurningArrow"), 1);
        Assert.Equal((double)(105.5m * effectiveness / 10000 * (100 + held) / 100),
            Hit(f.Conn, f.Skill)[DamageType.Chaos].Min, 10);
        f.Conn.Passives.Clear(); f.Conn.Pools = Program.PoolsFor(f.Conn, 100);
        Assert.Equal(100 + held, Hit(f.Conn, f.Skill)[DamageType.Chaos].Min / (effectiveness / 10000.0), 10);
    }

    [Fact]
    public async Task Parsed_supported_melee_uses_real_Rage_support_and_one_shared_interval_across_targets()
    {
        using var f = new Fixture("Metadata/Items/Gems/SkillGemGroundSlam", rageSupport: true);
        var second = new SpawnedMonster("second", new(106, 100, 10, width: 300, height: 300), 0, 2, 100000);
        f.Conn.Monsters.Add(43, second);
        int effect = Program.Skills.EffectRow("SupportRage");
        int amount = Program.Skills.EffectStats(effect, 1).Single(s =>
            s.Item1 == Program.Stats.Key("gain_x_rage_on_attack_hit")).Item2;
        Assert.True(amount > 0);
        await f.Dispatch(ground: true); await f.ObserveLife();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (second.Life == second.MaxLife) await Task.Delay(10, timeout.Token);
        Assert.Equal(amount, Current(f.Conn));
    }

    [Theory]
    [InlineData("gain_x_rage_on_attack_hit", 0)]
    [InlineData("gain_x_rage_on_melee_hit", 0)]
    [InlineData("gain_x_rage_on_attack_crit", 0)]
    [InlineData("gain_x_rage_on_melee_crit", 0)]
    [InlineData("gain_x_rage_on_hit", 3)]
    public async Task Parsed_spell_projectile_never_borrows_attack_sources(string source, int expected)
    {
        using var f = new Fixture("Metadata/Items/Gems/SkillGemFireball", "Driftwood Wand");
        Set(f.Conn, source, 3);
        await f.Dispatch(ground: true); await f.ObserveLife();
        Assert.Equal(expected, Current(f.Conn));
    }

    [Theory]
    [InlineData("cannot_gain_rage", 1)]
    [InlineData("cannot_gain_rage_during_soul_gain_prevention", 1)]
    public async Task Parsed_hits_respect_native_gain_prohibitions(string prohibition, int value)
    {
        using var f = new Fixture(); Set(f.Conn, "gain_x_rage_on_attack_hit", 5);
        Set(f.Conn, prohibition, value); Set(f.Conn, "has_soul_gain_prevention", 1);
        await f.Dispatch(); await f.ObserveLife(); Assert.Equal(0, Current(f.Conn));
        Assert.Equal(8855u, Program.Stats.Key("has_soul_gain_prevention"));
        Assert.Equal(14363u, Program.Stats.Key("cannot_gain_rage_during_soul_gain_prevention"));
    }

    [Fact]
    public async Task Sheer_Fury_alone_does_not_grant_Rage_and_true_generation_can_reach_its_maximum()
    {
        using var f = new Fixture();
        Set(f.Conn, "maximum_rage", 15); Set(f.Conn, "gain_max_rage_on_rage_gain_from_hit_%_chance", 100);
        await f.Dispatch(); await f.ObserveLife(); Assert.Equal(0, Current(f.Conn));
        Set(f.Conn, "gain_x_rage_on_attack_hit", 1); f.Conn.NextAttackAt = 0;
        await f.Dispatch(); await f.ObserveLife(); Assert.Equal(45, Current(f.Conn));
    }

    [Theory]
    [InlineData("transfer")]
    [InlineData("death")]
    [InlineData("action")]
    [InlineData("disconnect")]
    public async Task Parsed_queued_attack_cannot_grant_after_invalidation(string invalidation)
    {
        using var f = new Fixture(); Set(f.Conn, "gain_x_rage_on_attack_hit", 5);
        await f.Gate.WaitAsync(); await f.Dispatch();
        if (invalidation == "transfer") f.Conn.TryBeginAreaChange(new(2, 0, false), out _);
        if (invalidation == "death") { f.Conn.PlayerLife = 0; Program.ClearRageOnDeath(f.Conn); }
        if (invalidation == "action") f.Conn.ActionCount++;
        if (invalidation == "disconnect") f.Conn.Closed = true;
        f.Gate.Release(); await Task.Delay(1200);
        Assert.Equal(f.Target.MaxLife, f.Target.Life); Assert.Equal(0, Current(f.Conn));
    }

    [Fact]
    public void Final_attack_packet_and_supported_conversion_scale_once_while_spells_and_speed_stay_independent()
    {
        using var f = new Fixture("Metadata/Items/Gems/SkillGemBurningArrow", "Crude Bow");
        var before = Hit(f.Conn, f.Skill); var stats = Program.CharacterPlayerStats(f.Conn);
        var nativeBefore = ((IEnumerable<(uint, int)>)Call("FinalPlayerStats", f.Conn)).ToDictionary(v => v.Item1, v => v.Item2);
        var weapon = new SkillCatalog.Unarmed(1000, 10, 10, 8);
        var ordinary = Program.AttackHit(weapon, stats, false);
        Assert.True(f.Conn.Rage.Gain(20, f.Conn.Clock.Elapsed.TotalSeconds, Input(f.Conn)));
        Set(f.Conn, "rage_effect_+%", 50); stats = Program.CharacterPlayerStats(f.Conn);
        Assert.Equal(ordinary.MaxTotal * 1.3, Program.AttackHit(weapon, stats, false).MaxTotal, 8);
        Assert.Equal(before.MaxTotal * 1.3, Hit(f.Conn, f.Skill).MaxTotal, 8);
        Assert.Equal(before[DamageType.Fire].Max * 1.3, Hit(f.Conn, f.Skill)[DamageType.Fire].Max, 8);
        Assert.Equal(0, Hit(f.Conn, f.Skill)[DamageType.Physical].Max);
        Assert.Equal(1000, weapon.AttackSeconds(stats) * 1000);
        Assert.Equal(30, stats[Program.Stats.Key("attack_damage_+%_final_from_rage")!.Value]);
        var nativeAfter = ((IEnumerable<(uint, int)>)Call("FinalPlayerStats", f.Conn)).ToDictionary(v => v.Item1, v => v.Item2);
        Assert.Equal((int)Math.Floor(nativeBefore[StatKeys.MainHandMaxPhysical] * 1.3),
            nativeAfter[StatKeys.MainHandMaxPhysical]);
        var arrow = OrdinaryBowShot.Capture(weapon, stats, [], Program.Stats, int.MaxValue, 0, 150);
        Assert.Equal(ordinary.MaxTotal * 1.3, arrow.Hit.MaxTotal, 8);
        var spellPlan = SkillHitPlan.ForSkill(Program.Skills, Program.Skills.EffectRow("Fireball"), 1);
        var spell = SpellDamage.Packet(spellPlan.SpellBase, name => stats.GetValueOrDefault(Program.Stats.Key(name) ?? 0), 1);
        Assert.Equal(spellPlan.SpellBase.MaxTotal, spell.MaxTotal);
    }

    [Fact]
    public void Ticket_carry_is_one_shot_character_bound_and_retires_the_old_owner_even_with_no_Rage()
    {
        using var f = new Fixture();
        f.Conn.Rage.Gain(20, f.Conn.Clock.Elapsed.TotalSeconds, Input(f.Conn));
        uint ticket = (uint)Random.Shared.Next(1, int.MaxValue);
        Program.CarryRage(f.Conn, ticket);
        Assert.True(f.Conn.RageRetired); Assert.Equal(0, Current(f.Conn));
        Assert.Null(Program.RagePresentation(f.Conn));
        var wrong = new ClientState(f.Conn.Layout, ticket, 1) { Character = "another", PlayerLife = 100 };
        Program.RestoreRage(wrong); Assert.Equal(0, Current(wrong));
        var next = new ClientState(f.Conn.Layout, ticket, 1) { Character = f.Conn.Character, PlayerLife = 100 };
        Program.RestoreRage(next); Assert.Equal(20, Current(next));
        var duplicate = new ClientState(f.Conn.Layout, ticket, 1) { Character = f.Conn.Character, PlayerLife = 100 };
        Program.RestoreRage(duplicate); Assert.Equal(0, Current(duplicate));
        Program.CarryRage(f.Conn, ticket + 1); // A retired owner cannot duplicate its resource.
        var another = new ClientState(f.Conn.Layout, ticket + 1, 1) { Character = f.Conn.Character, PlayerLife = 100 };
        Program.RestoreRage(another); Assert.Equal(0, Current(another));
    }

    [Fact]
    public async Task Pause_freezes_decay_and_native_refresh_clears_death_without_a_stale_buff()
    {
        using var f = new Fixture(); Set(f.Conn, "no_inherent_rage_loss", 0);
        f.Conn.Rage.Gain(20, f.Conn.Clock.Elapsed.TotalSeconds, Input(f.Conn));
        f.Conn.Clock.Stop();
        byte[] shown = Program.RagePresentation(f.Conn)!;
        Assert.True(shown.AsSpan().StartsWith(InstanceEntities.AddBuff(Program.PlayerId, 1, 1060,
            Program.PlayerId, stacks: 20)));
        Assert.Null(Program.RagePresentation(f.Conn));
        await Task.Delay(2200); Assert.Equal(20, Current(f.Conn));
        f.Conn.PlayerLife = 0; Program.ClearRageOnDeath(f.Conn);
        byte[] cleared = Program.RagePresentation(f.Conn)!;
        Assert.True(cleared.AsSpan().StartsWith(InstanceEntities.RemoveBuff(Program.PlayerId, 1)));
        Assert.Equal(0, Current(f.Conn)); Assert.Equal(0, f.Conn.RageBuff);
    }

    [Theory]
    [InlineData("transfer")]
    [InlineData("disconnect")]
    public async Task Actual_native_send_gate_rejects_a_queued_refresh_from_the_old_owner(string change)
    {
        using var f = new Fixture();
        f.Conn.Rage.Gain(20, f.Conn.Clock.Elapsed.TotalSeconds, Input(f.Conn));
        await f.Gate.WaitAsync();
        var sending = Program.SendRage("rage-presentation-test", f.Stream, f.Ciphers, f.Conn);
        if (change == "transfer")
        {
            f.Conn.TryBeginAreaChange(new(2, 0, false), out _);
            Program.CarryRage(f.Conn, (uint)Random.Shared.Next(1, int.MaxValue));
        }
        else f.Conn.Closed = true;
        f.Gate.Release(); await sending;
        Assert.Equal(0, f.Available); Assert.Equal(0, f.Conn.RageBuff);
    }

    [Fact]
    public async Task Count_changes_replace_the_native_buff_and_emit_the_same_derived_attack_stats()
    {
        using var f = new Fixture();
        f.Conn.Rage.Gain(5, f.Conn.Clock.Elapsed.TotalSeconds, Input(f.Conn));
        await Program.SendRage("rage-presentation-test", f.Stream, f.Ciphers, f.Conn);
        byte[] add = InstanceEntities.AddBuff(Program.PlayerId, 1, 1060, Program.PlayerId, stacks: 5);
        Assert.Equal(add, await f.Receive(add.Length));
        Assert.Equal(new RageState.Value(5, 30, 5), f.Conn.SentRage);
        Assert.Equal(5, Program.CharacterPlayerStats(f.Conn)[Program.Stats.Key("attack_damage_+%_final_from_rage")!.Value]);
        f.Conn.Rage.Gain(5, f.Conn.Clock.Elapsed.TotalSeconds, Input(f.Conn));
        byte[] next = Program.RagePresentation(f.Conn)!;
        byte[] replacement = [.. InstanceEntities.RemoveBuff(Program.PlayerId, 1),
            .. InstanceEntities.AddBuff(Program.PlayerId, 2, 1060, Program.PlayerId, stacks: 10)];
        Assert.True(next.AsSpan().StartsWith(replacement));
        foreach (var (stat, value) in new[] { ("current_rage", 10), ("virtual_maximum_rage", 30),
            ("effective_rage_for_per_rage_effects", 10), ("attack_damage_+%_final_from_rage", 10) })
        {
            var encoded = new LoginWriter();
            InstanceProtocol.CompactUInt(encoded, Program.Stats.Key(stat)!.Value);
            InstanceProtocol.CompactInt(encoded, value);
            Assert.True(next.AsSpan(replacement.Length + 12).IndexOf(encoded.ToArray()) >= 0, stat);
        }
    }

    [Fact]
    public async Task Real_item_generation_comes_from_the_active_set_and_cannot_leak_from_Weapon2()
    {
        using var f = new Fixture();
        var mod = Program.Items.Mods.All.Single(m => m.Id == "RageOnAttackHitEldritchImplicit1");
        var weapon = ItemCatalog.Create(Program.Items.Find("Driftwood Club")!) with
            { Mods = [mod.Roll(ModSection.Implicit, Random.Shared)] };
        f.Items.Add(PlayerInventories.Weapon2, 0, 0, weapon);
        await f.Dispatch(); await f.ObserveLife(); Assert.Equal(0, Current(f.Conn));
        f.Items.SwapWeaponSets(); f.Conn.NextAttackAt = 0;
        await f.Dispatch(); await f.ObserveLife(); Assert.Equal(1, Current(f.Conn));
    }

    [Theory]
    [InlineData(false, 6)]
    [InlineData(true, 0)]
    public async Task Actual_incoming_contact_grants_when_hit_but_an_evaded_attack_does_not(bool evaded, int expected)
    {
        using var f = new Fixture(); Set(f.Conn, "gain_x_rage_when_hit", 6);
        var attacker = new SpawnedMonster("attacker", new(105, 100, 10, width: 300, height: 300), 0, 2, 100)
            { Attack = new(SkillCatalog.FirstSkillId, .05, 10, DamagePacket.Of(DamageType.Physical, 1, 1)) };
        f.Conn.Pools = f.Conn.Pools with { Evasion = evaded ? int.MaxValue : 0 };
        f.Conn.LastEvasionAt = f.Conn.Clock.Elapsed.TotalSeconds;
        f.Conn.EvasionEntropy = evaded ? 0 : 99;
        f.Conn.Monsters[43] = attacker;
        await (Task)Call("MonsterSwing", "rage-incoming-test", f.Stream, f.Ciphers, f.Conn,
            43u, attacker, (105, 100), (100, 100), f.Conn.IncomingHitEpoch);
        Assert.Equal(expected, Current(f.Conn));
        Assert.Equal(evaded ? 1000u : 999u, f.Conn.PlayerLife);
    }

    [Fact]
    public async Task A_hit_after_successful_carry_cannot_regrant_to_the_retired_connection()
    {
        using var f = new Fixture(); Set(f.Conn, "gain_x_rage_on_attack_hit", 5);
        await f.Dispatch(); await f.ObserveLife(); Assert.Equal(5, Current(f.Conn));
        uint ticket = (uint)Random.Shared.Next(1, int.MaxValue);
        Program.CarryRage(f.Conn, ticket);
        f.Conn.NextAttackAt = 0; uint before = f.Target.Life;
        await f.Dispatch(); await Task.Delay(1200);
        Assert.Equal(before, f.Target.Life); Assert.Equal(0, Current(f.Conn));
        var destination = new ClientState(f.Conn.Layout, ticket, 1) { Character = f.Conn.Character, PlayerLife = 100 };
        Program.RestoreRage(destination); Assert.Equal(5, Current(destination));
    }

    [Fact]
    public async Task Accepted_area_contact_commits_Rage_before_notifications_await_and_never_regrants_after_carry()
    {
        using var f = new Fixture("Metadata/Items/Gems/SkillGemGroundSlam");
        Set(f.Conn, "gain_x_rage_on_attack_hit", 5);
        var gem = ((IEnumerable<(ushort Id, int Effect, string Name, int Level, int Quality, uint Gem)>)
            Call("GemSkillEffects", f.Conn.Character!)).Single();
        await f.Gate.WaitAsync();
        var hit = (Task)typeof(Program).GetMethod("AreaSkillHit", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, ["rage-commit-test", f.Stream, f.Ciphers, f.Conn, f.Skill, gem,
                (100, 100), (105, 100), true, (ushort)1, null, null])!;
        Assert.False(hit.IsCompleted);
        Assert.True(f.Target.Life < f.Target.MaxLife);
        Assert.Equal(5, Current(f.Conn)); // No notification has passed the real send gate yet.
        uint ticket = (uint)Random.Shared.Next(1, int.MaxValue);
        Program.CarryRage(f.Conn, ticket);
        f.Gate.Release(); await hit.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, Current(f.Conn));
        var destination = new ClientState(f.Conn.Layout, ticket, 1) { Character = f.Conn.Character, PlayerLife = 100 };
        Program.RestoreRage(destination); Assert.Equal(5, Current(destination));
    }

    [Fact]
    public async Task Carried_ticket_keeps_its_balance_and_remaining_delay_through_a_running_destination_load()
    {
        using var f = new Fixture(); Set(f.Conn, "no_inherent_rage_loss", 0);
        f.Conn.Clock.Stop(); // Establish an exact remaining grace period, unaffected by source-side test scheduling.
        var inputs = Input(f.Conn);
        f.Conn.Rage.Gain(30, f.Conn.Clock.Elapsed.TotalSeconds, inputs);
        uint ticket = (uint)Random.Shared.Next(1, int.MaxValue);
        Program.CarryRage(f.Conn, ticket);
        var destination = new ClientState(f.Conn.Layout, ticket, 1)
        {
            Character = f.Conn.Character, PlayerLife = 1000, PlayerMana = 1000, Pools = f.Conn.Pools
        };
        Assert.True(destination.Clock.IsRunning);
        await Task.Delay(4100); // The destination has a clock but has not received 0x7d/constructed its player yet.
        Assert.True(destination.Clock.Elapsed.TotalSeconds >= 4);
        Assert.Equal(0, Current(destination)); // The ticket is still staged, not decaying on this running clock.
        destination.Clock.Stop(); // Freeze observations after loading, independent of initial-packet build duration.
        await ((Task)Call("SendPlayer", "rage-load-test", f.Stream, f.Ciphers, destination))
            .WaitAsync(TimeSpan.FromSeconds(10));
        var initial = destination.PeerTemplate!.Stats!.GroupBy(s => s.Item1)
            .ToDictionary(s => s.Key, s => s.Last().Item2);
        Assert.Equal(30, initial[Program.Stats.Key("current_rage")!.Value]);
        Assert.Equal(30, initial[Program.Stats.Key("attack_damage_+%_final_from_rage")!.Value]);
        double started = destination.Clock.Elapsed.TotalSeconds;
        Assert.Equal(30, destination.Rage.Read(started + 1.9, inputs).Current);
        Assert.Equal(29, destination.Rage.Read(started + 2.15, inputs).Current);
        var duplicate = new ClientState(f.Conn.Layout, ticket, 1) { Character = f.Conn.Character, PlayerLife = 100 };
        Program.RestoreRage(duplicate); Assert.Equal(0, Current(duplicate));
    }

    [Fact]
    public async Task One_area_sweep_grants_once_even_when_its_first_notification_waits_over_the_shared_interval()
    {
        using var f = new Fixture("Metadata/Items/Gems/SkillGemGroundSlam");
        Set(f.Conn, "gain_x_rage_on_attack_hit", 5);
        var second = new SpawnedMonster("second", new(106, 100, 10, width: 300, height: 300), 0, 2, 100000);
        f.Conn.Monsters.Add(43, second);
        await f.Gate.WaitAsync();
        var sweep = f.Sweep();
        Assert.False(sweep.IsCompleted);
        Assert.True(f.Target.Life < f.Target.MaxLife); Assert.Equal(second.MaxLife, second.Life);
        Assert.Equal(5, Current(f.Conn));
        double blockedAt = f.Conn.Clock.Elapsed.TotalSeconds;
        await Task.Delay(650);
        Assert.True(f.Conn.Clock.Elapsed.TotalSeconds - blockedAt > .5);
        f.Gate.Release(); await sweep.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(second.Life < second.MaxLife);
        Assert.Equal(5, Current(f.Conn));
    }

    [Theory]
    [InlineData("action")]
    [InlineData("transfer")]
    [InlineData("death")]
    [InlineData("pause")]
    [InlineData("disconnect")]
    public async Task Delayed_area_notifications_keep_the_remaining_contacts_guarded(string invalidation)
    {
        using var f = new Fixture("Metadata/Items/Gems/SkillGemGroundSlam");
        Set(f.Conn, "gain_x_rage_on_attack_hit", 5);
        var second = new SpawnedMonster("second", new(106, 100, 10, width: 300, height: 300), 0, 2, 100000);
        f.Conn.Monsters.Add(43, second);
        await f.Gate.WaitAsync();
        var sweep = f.Sweep();
        Assert.False(sweep.IsCompleted); Assert.Equal(5, Current(f.Conn));
        await Task.Delay(650);
        lock (f.Conn.Monsters)
        {
            if (invalidation == "action") f.Conn.ActionCount++;
            if (invalidation == "transfer") f.Conn.TryBeginAreaChange(new(2, 0, false), out _);
            if (invalidation == "death") { f.Conn.PlayerLife = 0; Program.ClearRageOnDeath(f.Conn); }
            if (invalidation == "pause") f.Conn.Clock.Stop();
            if (invalidation == "disconnect") f.Conn.Closed = true;
        }
        f.Gate.Release(); await sweep.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(second.MaxLife, second.Life);
        Assert.Equal(invalidation == "death" ? 0 : 5, Current(f.Conn));
    }

    [Theory]
    [InlineData("no-source")]
    [InlineData("prevented")]
    public async Task A_nonpositive_first_area_contact_does_not_consume_the_sweeps_positive_grant(string refusal)
    {
        using var f = new Fixture("Metadata/Items/Gems/SkillGemGroundSlam");
        Set(f.Conn, "gain_x_rage_on_attack_hit", refusal == "no-source" ? 0 : 5);
        Set(f.Conn, "cannot_gain_rage", refusal == "prevented" ? 1 : 0);
        var second = new SpawnedMonster("second", new(106, 100, 10, width: 300, height: 300), 0, 2, 100000);
        f.Conn.Monsters.Add(43, second);
        await f.Gate.WaitAsync();
        var sweep = f.Sweep();
        Assert.False(sweep.IsCompleted); Assert.Equal(0, Current(f.Conn));
        await Task.Delay(650);
        Set(f.Conn, "gain_x_rage_on_attack_hit", 5); Set(f.Conn, "cannot_gain_rage", 0);
        f.Gate.Release(); await sweep.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(second.Life < second.MaxLife); Assert.Equal(5, Current(f.Conn));
    }

    [Fact]
    public async Task A_queued_Rage_snapshot_rechecks_expiry_and_other_stat_inputs_inside_the_actual_send_gate()
    {
        using var f = new Fixture();
        Set(f.Conn, "no_inherent_rage_loss", 0); Set(f.Conn, "rage_loss_delay_ms", -2000);
        f.Conn.Clock.Stop();
        var inputs = Input(f.Conn);
        f.Conn.Rage.Gain(1, f.Conn.Clock.Elapsed.TotalSeconds, inputs);
        Assert.Equal(1, Current(f.Conn));
        await f.Gate.WaitAsync();
        var sending = Program.SendRage("rage-expiry-test", f.Stream, f.Ciphers, f.Conn);
        Assert.False(sending.IsCompleted);
        f.Conn.Clock.Start();
        await Task.Delay(650); Assert.Equal(0, Current(f.Conn));
        Set(f.Conn, "base_fire_damage_resistance_%", 37);
        f.Gate.Release(); await sending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new RageState.Value(0, 30, 0), f.Conn.SentRage); Assert.Equal(0, f.Conn.RageBuff);
        var final = (List<(uint, int)>)Call("PlayerStatsWithCleared", f.Conn);
        Assert.Contains((Program.Stats.Key("base_fire_damage_resistance_%")!.Value, 37), final);
        byte[] expected = InstanceEntities.StatsUpdate(Program.PlayerId, final);
        Assert.Equal(expected, await f.Receive(expected.Length));
    }

    [Fact]
    public async Task Queued_charge_refresh_cannot_replay_expired_Rage_in_full_stats_or_Actor_damage()
    {
        using var f = new Fixture("Metadata/Items/Gems/SkillGemBurningArrow", "Crude Bow");
        Set(f.Conn, "no_inherent_rage_loss", 0); Set(f.Conn, "rage_loss_delay_ms", -2000);
        f.Conn.Clock.Stop();
        var inputs = Input(f.Conn);
        f.Conn.Rage.Gain(1, f.Conn.Clock.Elapsed.TotalSeconds, inputs);
        var before = (IReadOnlyList<GrantedSkill>)Call("PlayerSkills", f.Conn);
        f.Conn.ClientSkillIds = [.. before.Select(skill => skill.Id)]; // Exercise SendChargeStats' unchanged-skill path.
        Assert.Equal(1, Current(f.Conn));
        await f.Gate.WaitAsync();
        var sending = (Task)Call("SendChargeStats", "rage-charge-snapshot-test", f.Stream, f.Ciphers, f.Conn,
            "queued before Rage expiry");
        Assert.False(sending.IsCompleted);
        f.Conn.Clock.Start();
        await Task.Delay(650); Assert.Equal(0, Current(f.Conn));
        f.Conn.Clock.Stop();
        Set(f.Conn, "base_fire_damage_resistance_%", 37);
        f.Gate.Release(); await sending.WaitAsync(TimeSpan.FromSeconds(5));
        var finalStats = (List<(uint, int)>)Call("PlayerStatsWithCleared", f.Conn);
        var finalSkills = (IReadOnlyList<GrantedSkill>)Call("PlayerSkills", f.Conn);
        Assert.Contains((Program.Stats.Key("current_rage")!.Value, 0), finalStats);
        Assert.Contains((Program.Stats.Key("attack_damage_+%_final_from_rage")!.Value, 0), finalStats);
        Assert.Contains((Program.Stats.Key("base_fire_damage_resistance_%")!.Value, 37), finalStats);
        byte[] expected = [.. InstanceEntities.StatsUpdate(Program.PlayerId, finalStats),
            .. InstanceEntities.PlayerSkillsChange(Program.PlayerId, finalSkills, [])];
        Assert.Equal(expected, await f.Receive(expected.Length));
        Assert.Equal(0, f.Available);
    }
}
