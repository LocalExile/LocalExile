using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class SpellNoDamageTests
{
    const string Flag = "spell_skills_deal_no_damage";
    static MethodInfo Method(string name) =>
        typeof(Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;

    sealed class Fixture : IDisposable
    {
        readonly string name = "SpellNoDamage" + Guid.NewGuid().ToString("N");
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly TcpClient writer = new();
        public readonly TcpClient Reader;
        public readonly ConnectionCiphers Ciphers = new(new byte[64], isClient: false);
        public readonly ClientState Conn;
        public readonly SpawnedMonster Target;
        public readonly int Effect;
        public ushort Skill => Program.GemSkillIdPool.Value[0];
        public NetworkStream Stream => writer.GetStream();
        public PlayerInventories Items => PlayerInventories.For(name, Program.Items);

        public Fixture(string? effect = null, string? weapon = null, string? support = null, int targetX = 38)
        {
            TestSaves.Ensure();
            Conn = new(new(ushort.MaxValue, "SpellNoDamage", 300, 300, 32, 32, null, []), 0, 1)
            { Character = name, ClassRow = 1, PlayerSent = true };
            Set("global_cannot_crit", 1);
            Set("base_cast_speed_+%", 1000);
            Set("attack_speed_+%", 1000);
            // Use an ordinary equipment stat source for requirements; dev attributes are not pool inputs on main.
            var attributes = Program.Items.Mods.All.First(m => m.GenerationType is 1 or 2
                && m.Stats.Count == 1 && m.Stats[0].Id == "additional_all_attributes");
            var amulet = ItemCatalog.Create(Program.Items.Find("Paua Amulet")!) with
                { Rarity = ItemRarity.Rare, Mods = [new(ModSection.Explicit, attributes.Hash, [1000])] };
            PlayerInventories.For(name, Program.Items).Add(5, 0, 0, amulet);
            if (weapon is not null)
                PlayerInventories.For(name, Program.Items).Add(PlayerInventories.Weapon1, 0, 0,
                    ItemCatalog.Create(Program.Items.Find(weapon)!));
            Effect = effect is null ? -1 : Program.Skills.EffectRow(effect);
            if (effect is not null)
            {
                string gemId = effect switch
                {
                    "CausticArrow" => "PoisonArrow",
                    "ScorchingRay" => "FireBeam",
                    _ => effect,
                };
                var gem = ItemCatalog.Create(Program.Items.Find($"Metadata/Items/Gems/SkillGem{gemId}")!);
                var body = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Armours/BodyArmours/BodyStr1")!) with
                    { Sockets = [new(SocketColour.White, gem)] };
                if (support is not null)
                    body = body with { Links = [2], Sockets = [new(SocketColour.White, gem),
                        new(SocketColour.White, ItemCatalog.Create(Program.Items.Find(support)!))] };
                PlayerInventories.For(name, Program.Items).Add(PlayerInventories.BodyArmour, 0, 0, body);
            }
            Conn.Pools = Program.PoolsFor(Conn, 100) with { Accuracy = int.MaxValue };
            Conn.PlayerLife = Conn.PlayerMaxLife;
            Conn.PlayerMana = (uint)Conn.Pools.Mana;
            Target = new("Spell suppression target", new(targetX, 32, 30), 0, 2, 100000)
                { Hostile = true, Level = 1 };
            Conn.Monsters.Add(42, Target);
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            writer.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            Reader = accepting.GetAwaiter().GetResult();
        }

        public void Set(string stat, int value) => Conn.PlayerStats[Program.Stats.Key(stat)!.Value] = value;
        public DamagePacket Hit() => (DamagePacket)Method("SupportedHit").Invoke(null, [Conn, Skill])!;
        public Dictionary<uint, int> Panel() => ((IReadOnlyList<GrantedSkill>)Method("PlayerSkills")
            .Invoke(null, [Conn])!).Single(s => s.Id == Skill).Stats!.ToDictionary(s => s.Item1, s => s.Item2);

        public Task Invoke(string name, params object?[] args) => (Task)Method(name).Invoke(null,
            new object?[] { "spell-no-damage-test", Stream, Ciphers, Conn }.Concat(args).ToArray())!;

        public async Task Command(string line)
        {
            Conn.Commands ??= (ChatCommands)Method("Commands").Invoke(null,
                ["spell-no-damage-test", Stream, Ciphers, Conn])!;
            var input = new LoginWriter().U16(InstanceChat.ChatId).WStr(line).U8(0).ToArray();
            var messages = InstanceProtocol.SplitClientMessages(input, out var partial, out var unknown);
            Assert.Empty(partial); Assert.Empty(unknown); Assert.Single(messages);
            await Program.OnClientMessages("spell-no-damage-test", Stream, Ciphers, messages, [], Conn)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }

        public Task Dispatch(bool attack = false, bool instant = false)
        {
            ushort id = instant ? (ushort)0x191 : (ushort)0x24;
            var w = new LoginWriter().U16(id).U32(instant ? 0x1000u : 42u)
                .U16(attack ? SkillCatalog.FirstSkillId : Skill);
            if (!instant) w.U16(7).U16(1);
            return Program.OnClientMessages("spell-no-damage-test", Stream, Ciphers,
                [(id, w.ToArray())], [], Conn);
        }

        public void Dispose()
        {
            Conn.Closed = true; Conn.ProjectileRuntime?.Invalidate();
            writer.Dispose(); Reader.Dispose(); listener.Stop(); PlayerInventories.Forget(name);
        }
    }

    static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    static int Value(IReadOnlyDictionary<uint, int> stats, string name) =>
        stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);

    [Fact]
    public void Rampager_supplies_the_raw_key_without_admitting_the_class_or_allocating_its_node()
    {
        var node = new PassiveTree().Find(49937);
        Assert.NotNull(node);
        Assert.Equal("Rampager", node.Name);
        Assert.Equal(5341u, Program.Stats.Key(Flag));
        Assert.Equal(5341u, StatKeys.SpellSkillsDealNoDamage);
        Assert.Contains((5341u, 1), node.Stats);
    }

    [Fact]
    public void Context_uses_the_actual_caster_and_preserves_identity_for_attacks_or_absent_zero_sources()
    {
        var hit = DamagePacket.Of(DamageType.Physical, 10, 20).Add(DamageType.Chaos, new(5, 10));
        var owner = new Dictionary<uint, int> { [5341] = 1 };
        var caster = new Dictionary<uint, int>();
        var independent = new SkillDamageContext(SkillHitKind.Spell, caster);
        Assert.Same(hit, independent.Hit(hit));
        Assert.Same(DamagePacket.Empty, new SkillDamageContext(SkillHitKind.Spell, owner).Hit(hit));
        caster[5341] = 0;
        Assert.Same(hit, independent.Hit(hit));
        caster[5341] = -1;
        Assert.Same(DamagePacket.Empty, independent.Hit(hit));
        var attack = SkillHitPlan.Create(attack: true, spell: true, hit);
        Assert.Same(hit, new SkillDamageContext(attack.Kind, owner).Hit(hit));
        var dot = new DamageOverTime(DamageType.Fire, 100, 2);
        Assert.Equal(dot, new SkillDamageContext(SkillHitKind.Attack, owner).OverTime(dot));
        Assert.Equal(0, independent.OverTime(dot).DamagePerSecond);
        owner.Clear(); // Changing the owner never supplies or removes this caster's own source.
        Assert.Same(DamagePacket.Empty, independent.Hit(hit));
    }

    [Theory]
    [InlineData("Fireball", DamageType.Fire)]
    [InlineData("EtherealKnives", DamageType.Physical)]
    [InlineData("IceNova", DamageType.Cold)]
    [InlineData("Arc", DamageType.Lightning)]
    [InlineData("DarkPact", DamageType.Chaos)]
    public void Supported_spell_constructors_and_final_native_panel_share_the_suppressed_packet(
        string effect, DamageType type)
    {
        using var f = new Fixture(effect, support: "Added Chaos Damage Support");
        var plan = SkillHitPlan.ForSkill(Program.Skills, f.Effect, 1);
        Assert.True(plan.CanHit);
        Assert.Equal(SkillHitKind.Spell, plan.Kind);
        var baseline = f.Hit();
        Assert.True(baseline[type].Max > 0);
        Assert.True(baseline[DamageType.Chaos].Max > 0);
        var normal = f.Panel();
        Assert.Equal((int)Math.Floor(baseline.MinTotal), Value(normal, "spell_minimum_total_damage"));
        Assert.Equal((int)Math.Floor(baseline.MaxTotal), Value(normal, "spell_maximum_total_damage"));
        Assert.Equal(SpellDamage.Display((baseline.MinTotal, baseline.MaxTotal), 1).PerUse,
            Value(normal, "display_hundred_times_damage_per_skill_use"));
        f.Set(Flag, 1);
        Assert.Same(DamagePacket.Empty, f.Hit());
        var suppressed = f.Panel();
        foreach (string hand in new[] { "spell", "main_hand" })
            foreach (string slug in new[] { "total", "physical", "fire", "cold", "lightning", "chaos" })
                foreach (string bound in new[] { "minimum", "maximum" })
                    Assert.Equal(0, Value(suppressed, $"{hand}_{bound}_{slug}_damage"));
        Assert.Equal(0, Value(suppressed, "display_hundred_times_damage_per_skill_use"));
        Assert.Equal(0, Value(suppressed, "hundred_times_damage_per_second"));
        foreach (string stat in new[] { "mana_cost", "virtual_base_spell_cast_time_ms",
            "hundred_times_casts_per_second", "spell_critical_strike_chance", "spell_critical_strike_multiplier" })
            Assert.Equal(Value(normal, stat), Value(suppressed, stat));
        f.Set(Flag, 0);
        Assert.Equal(baseline.Components, f.Hit().Components);
        Assert.Equal(Value(normal, "hundred_times_damage_per_second"),
            Value(f.Panel(), "hundred_times_damage_per_second"));
        f.Conn.PlayerStats.Remove(5341);
        Assert.Equal(baseline.Components, f.Hit().Components);
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(0, 1, false)]
    public void Spell_hit_seam_reads_the_caster_flag_before_any_skill_dictionary_overlay(
        int casterFlag, int skillFlag, bool suppressed)
    {
        var plan = SkillHitPlan.ForSkill(Program.Skills, Program.Skills.EffectRow("Fireball"), 1);
        var caster = new Dictionary<uint, int> { [5341] = casterFlag };
        var own = new Dictionary<uint, int> { [5341] = skillFlag };
        var hit = SpellHit.Resolve(plan, Program.Stats, caster, own, new DamageConversion());
        Assert.Equal(suppressed, hit.IsZero);
    }

    [Theory]
    [InlineData("Fireball", 0)]
    [InlineData("Fireball", 1)]
    [InlineData("Fireball", -1)]
    [InlineData("IceNova", 0)]
    [InlineData("IceNova", 1)]
    [InlineData("IceNova", -1)]
    [InlineData("Arc", 1)]
    [InlineData("DarkPact", 0)]
    [InlineData("DarkPact", 1)]
    public async Task Dispatched_spells_still_cast_and_pay_but_suppressed_hits_do_not_reduce_target_Life(
        string effect, int flag)
    {
        using var f = new Fixture(effect);
        f.Set(Flag, flag);
        f.Set("gain_power_charge_on_non_critical_strike_%", 100);
        f.Set("gain_x_rage_on_hit", 2);
        f.Set("gain_x_rage_on_attack_hit", 3);
        f.Set("gain_x_rage_on_melee_hit", 5);
        f.Set("no_inherent_rage_loss", 1);
        f.Set(FortificationSession.OnMeleeHit, 1);
        uint mana = f.Conn.PlayerMana;
        await f.Dispatch();
        await Until(() => double.IsFinite(f.Conn.LastEnemyHitAt));
        Assert.Equal(flag != 0, f.Target.Life == f.Target.MaxLife);
        Assert.True(f.Conn.PlayerMana < mana);
        Assert.Equal(1, f.Conn.ActionCount);
        Assert.True(f.Reader.Available > 0); // Action/cost/effect packets still traverse the encrypted send path.
        // Existing zero-damage hits retain accepted-hit callbacks, including noncritical charge gains.
        var charges = (ChargeSession)Method("ChargesOf").Invoke(null, [f.Conn])!;
        await Until(() =>
        {
            if (charges.Pending)
            {
                var current = Program.CharacterPlayerStats(f.Conn);
                charges.Step(f.Conn.Clock.Elapsed.TotalSeconds, name => Value(current, name),
                    () => Program.WeaponProfileFor(f.Conn), true, false, false, new Random(1));
            }
            return charges.Effective(ChargeType.Power) > 0;
        });
        var stats = Program.CharacterPlayerStats(f.Conn);
        charges.Step(f.Conn.Clock.Elapsed.TotalSeconds, name => Value(stats, name),
            () => Program.WeaponProfileFor(f.Conn), true, false, false, new Random(1));
        Assert.Equal(1, charges.Effective(ChargeType.Power));
        await Until(() => f.Conn.ProjectileObjects.Count == 0);
        // Main's accepted-contact policy grants generic Rage even when spell damage is zero.
        // Attack/melee Rage and Fortification remain ineligible for these actual spell skills.
        Assert.Equal(2, Value(Program.CharacterPlayerStats(f.Conn), "current_rage"));
        Assert.Equal(0, Program.FortificationOf(f.Conn).Value.Current);
    }

    [Theory]
    [InlineData("Cyclone", "Driftwood Club", true)]
    [InlineData("BurningArrow", "Crude Bow", true)]
    [InlineData("Fireball", "Driftwood Wand", false)]
    [InlineData("IceNova", "Driftwood Wand", false)]
    public void Final_supported_packets_compose_local_weapon_quality_mana_conversion_charges_and_Rage(
        string effect, string weaponName, bool attack)
    {
        using var f = new Fixture(effect, weaponName, "Added Chaos Damage Support");
        var held = Assert.Single(f.Items.Items(PlayerInventories.Weapon1));
        var local = Program.Items.Mods.All.First(m => m.Domain == 1 && m.Stats.Count == 1
            && m.Stats[0].Id == "local_physical_damage_+%" && m.Stats[0].Local);
        f.Items.Change(PlayerInventories.Weapon1, held.Id, item => item with
            { Quality = 20, Mods = [new(ModSection.Explicit, local.Hash, [100])] });
        var basis = Program.Skills.WeaponBase(Program.Items.Find(weaponName)!.Row)!.Value;
        var weapon = (SkillCatalog.Unarmed)Method("WeaponFor").Invoke(null, [f.Conn])!;
        Assert.Equal((int)Math.Floor(basis.Min * 2 * 1.2), weapon.MinDamage);
        Assert.Equal((int)Math.Floor(basis.Max * 2 * 1.2), weapon.MaxDamage);
        f.Set("no_inherent_rage_loss", 1);
        f.Set("attack_minimum_added_chaos_damage", 100);
        f.Set("attack_maximum_added_chaos_damage", 100);
        f.Set("spell_minimum_added_lightning_damage", 7);
        f.Set("spell_maximum_added_lightning_damage", 9);
        f.Set(DamageStats.MaxManaAddedLightning, 5);
        f.Set("physical_damage_%_to_convert_to_fire", 25);
        f.Set("non_chaos_damage_to_add_as_chaos_damage_%", 10);
        f.Set("current_frenzy_charges", 3);
        f.Set("object_inherent_damage_+%_final_per_frenzy_charge", 4);
        var baseline = f.Hit();
        Assert.True(baseline[DamageType.Chaos].Min > 0);
        Assert.True(baseline[DamageType.Lightning].Min > 0);
        var inputs = RageStats.Inputs(name => Value(Program.CharacterPlayerStats(f.Conn), name));
        Assert.True(f.Conn.Rage.Gain(29, f.Conn.Clock.Elapsed.TotalSeconds, inputs));
        var withRage = f.Hit();
        foreach (var component in baseline.Components)
        {
            Assert.Equal(attack ? component.Range.Min * 129 / 100 : component.Range.Min,
                withRage[component.Type].Min, 10);
            Assert.Equal(attack ? component.Range.Max * 129 / 100 : component.Range.Max,
                withRage[component.Type].Max, 10);
        }
        foreach (int flag in new[] { 1, -1, 0 })
        {
            f.Set(Flag, flag);
            var expected = !attack && flag != 0 ? DamagePacket.Empty : withRage;
            Assert.Equal(expected.Components, f.Hit().Components);
            var panel = f.Panel();
            Assert.Equal((int)Math.Floor(expected.MaxTotal), Value(panel, "main_hand_maximum_total_damage"));
            Assert.Equal(SpellDamage.Display((expected.MinTotal, expected.MaxTotal), 1).PerUse,
                Value(panel, "display_hundred_times_damage_per_skill_use"));
        }
        f.Conn.PlayerStats.Remove(StatKeys.SpellSkillsDealNoDamage);
        Assert.Equal(withRage.Components, f.Hit().Components);
    }

    [Fact]
    public async Task Parsed_source_overrides_preserve_Strength_mana_chaos_armour_and_Mageblood_deduplication()
    {
        using var f = new Fixture("Fireball");
        var type = Program.Items.Find("Granite Flask")!;
        var flask = f.Items.Add(PlayerInventories.Flask, 0, 0,
            ItemCatalog.Create(type) with { Rarity = ItemRarity.Magic });
        f.Items.Add(PlayerInventories.Belt, 0, 0, Program.Uniques.Create("Mageblood", new Random(1))!);
        var utility = Program.Items.UtilityFlaskOf(type)!;
        var (sources, _) = UtilityFlasks.For(utility, flask.Item,
            ItemModStats.Local(flask.Item, Program.Items.Mods), _ => 0);
        f.Conn.UtilityFlaskEffects.Start(new(flask.Id, flask.X, type.Name, utility, 1, null, sources, 0, 1000));
        var ordinary = Program.PoolsFor(f.Conn, 100);
        // Raw passive fixtures exercise consumers without admitting or allocating a Phrecia class.
        f.Conn.MainAscendancy = new(Nodes: [49937, 38080, 29437]);
        f.Set(PowerOfPurpose.RawFlag, 0); // Keep this existing control on ordinary mana and Armour.
        f.Conn.Pools = Program.PoolsFor(f.Conn, 100);
        uint combinedMana = ordinary.Mana + (uint)(ordinary.Strength / 4 * 8);
        Assert.Equal(combinedMana, f.Conn.Pools.Mana);
        Assert.True(f.Conn.Pools.ArmourAppliesToChaosDamage);
        Assert.Equal(1500, Program.UtilityStats(f.Conn, _ => 0)
            .Where(s => s.Stat == "base_physical_damage_reduction_rating").Sum(s => s.Value));
        Assert.Equal(ordinary.Armour, f.Conn.Pools.Armour);
        f.Conn.PlayerMana = combinedMana;
        f.Set(DamageStats.MaxManaAddedLightning, 5);
        Assert.Same(DamagePacket.Empty, f.Hit());

        await f.Command($"/set_stat {Flag} 0");
        var restored = f.Hit();
        Assert.True(restored.MaxTotal > 0);
        double AttackLightning() => Program.AttackHit(new(1000, 0, 0, 8),
            Program.CharacterPlayerStats(f.Conn), melee: false)[DamageType.Lightning].Min;
        double lightning = AttackLightning();
        Assert.Equal(combinedMana * .05, lightning, 10);
        Assert.Equal(0, restored[DamageType.Lightning].Min); // The mana grant is attack-only.
        await f.Command($"/set_stat {StrengthMana.PerFourStrength} 0");
        Assert.Equal(ordinary.Mana, f.Conn.Pools.Mana);
        Assert.Equal(ordinary.Mana, f.Conn.PlayerMana);
        Assert.Equal(ordinary.Mana * .05, AttackLightning(), 10);
        Assert.True(AttackLightning() < lightning);
        Assert.Equal(restored.Components, f.Hit().Components);
        await f.Command($"/set_stat {StrengthMana.PerFourStrength} off");
        Assert.Equal(combinedMana, f.Conn.Pools.Mana);
        Assert.Equal(ordinary.Mana, f.Conn.PlayerMana); // Restoring sources does not refill current Mana.
        Assert.Equal(restored.Components, f.Hit().Components);
        await f.Command($"/set_stat {Flag} -1");
        Assert.Same(DamagePacket.Empty, f.Hit());
        await f.Command("/set_stat base_armour_applies_to_chaos_damage 0");
        Assert.False(f.Conn.Pools.ArmourAppliesToChaosDamage);
        await f.Command("/set_stat base_armour_applies_to_chaos_damage off");
        Assert.True(f.Conn.Pools.ArmourAppliesToChaosDamage);
        await f.Command($"/set_stat {Flag} off");
        Assert.Same(DamagePacket.Empty, f.Hit()); // The Rampager raw passive source is visible again.
        f.Conn.MainAscendancy = new(Nodes: [38080, 29437]);
        Assert.Equal(restored.Components, f.Hit().Components);
        Assert.Equal(combinedMana, f.Conn.Pools.Mana);
        Assert.Equal(ordinary.Armour, f.Conn.Pools.Armour);
        Assert.Null(f.Conn.MainAscendancy.Selection);
    }

    [Theory]
    [InlineData("Fireball", 0)]
    [InlineData("Fireball", 1)]
    [InlineData("Fireball", -1)]
    [InlineData("IceNova", 0)]
    [InlineData("IceNova", 1)]
    [InlineData("IceNova", -1)]
    public async Task Unpaid_spells_keep_existing_action_intent_but_never_release_or_proc(string effect, int flag)
    {
        using var f = new Fixture(effect);
        f.Set(Flag, flag);
        f.Set("gain_x_rage_on_hit", 2);
        f.Conn.PlayerMana = 0;
        await f.Dispatch();
        Assert.Equal(0u, f.Conn.PlayerMana);
        Assert.Equal(1, f.Conn.ActionCount); // Main records intent before CastGemSkill rejects payment.
        Assert.Equal(f.Target.MaxLife, f.Target.Life);
        Assert.False(double.IsFinite(f.Conn.LastEnemyHitAt));
        Assert.Empty(f.Conn.ProjectileObjects);
        Assert.Empty(f.Conn.SingleUseCooldowns);
        Assert.False(((ChargeSession)Method("ChargesOf").Invoke(null, [f.Conn])!).Pending);
        Assert.Equal(0, Value(Program.CharacterPlayerStats(f.Conn), "current_rage"));
    }

    [Fact]
    public async Task A_suppressed_utility_spell_keeps_its_reservation_and_single_use_cooldown()
    {
        using var f = new Fixture("Haste");
        f.Set(Flag, -1);
        await f.Dispatch(instant: true);
        await Until(() => f.Conn.Auras.Count == 1);
        uint mana = f.Conn.PlayerMana;
        var state = Assert.Single(f.Conn.SingleUseCooldowns).Value;
        int uses = state.UseVersion;
        Assert.False(state.Ready);
        await f.Dispatch(instant: true);
        Assert.Single(f.Conn.Auras);
        Assert.Equal(mana, f.Conn.PlayerMana);
        Assert.Equal(uses, state.UseVersion);
        Assert.True(AuraCatalog.ReservedMana(f.Conn.Pools, f.Conn.Auras.Values) > 0);
    }

    [Fact]
    public async Task Zeroed_spell_panels_preserve_cosmetic_identity_and_reject_a_retired_gem_target()
    {
        using var f = new Fixture("Fireball");
        var type = Program.Items.Find("Metadata/Items/Gems/SkillGemFireball")!;
        List<InstanceMicrotransactions.SkillSlot> Slots() =>
            (List<InstanceMicrotransactions.SkillSlot>)Method("SkillSlots").Invoke(null, [f.Conn])!;
        var original = Assert.Single(Slots());
        var record = Program.Mtx.Owned.First(r => Program.MtxEffects.FitsSkill(r.MtxTypeHash, type, 0));
        await f.Invoke("PickSkillCosmetic", (original.Key, record.Id));
        int variation = Value(f.Panel(), "skill_art_variation");
        Assert.NotEqual(0, variation);
        f.Set(Flag, -1);
        var suppressed = f.Panel();
        Assert.Equal(0, Value(suppressed, "spell_maximum_total_damage"));
        Assert.Equal(variation, Value(suppressed, "skill_art_variation"));
        Assert.Equal(original.Key, Assert.Single(Slots()).Key);
        var host = Assert.Single(f.Items.Items(PlayerInventories.BodyArmour));
        f.Items.Change(PlayerInventories.BodyArmour, host.Id, item => item with
        {
            Sockets = [new(SocketColour.White,
                ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemIceNova")!))]
        });
        var replacement = Assert.Single(Slots());
        Assert.NotEqual(original.Key, replacement.Key);
        await f.Invoke("PickSkillCosmetic", (original.Key, record.Id));
        Assert.Equal(0u, Assert.Single(Slots()).RecordId);
        var skills = (IReadOnlyList<GrantedSkill>)Method("PlayerSkills").Invoke(null, [f.Conn])!;
        var nova = skills.Single(s => s.EffectsPerLevelRow ==
            Program.Skills.FindByRow(0, Program.Skills.EffectRow("IceNova"), 1)!.EffectsPerLevelRow);
        var stats = nova.Stats!.ToDictionary(s => s.Item1, s => s.Item2);
        Assert.Equal(0, Value(stats, "skill_art_variation"));
        Assert.Equal(0, Value(stats, "spell_maximum_total_damage"));
        f.Set(Flag, 0);
        Assert.Contains(((IReadOnlyList<GrantedSkill>)Method("PlayerSkills").Invoke(null, [f.Conn])!)
            .Single(s => s.Id == nova.Id).Stats!, s =>
                s.Item1 == Program.Stats.Key("spell_maximum_total_damage") && s.Item2 > 0);
    }

    [Fact]
    public void Converted_and_gained_damage_are_suppressed_after_the_complete_supported_spell_packet()
    {
        using var f = new Fixture("EtherealKnives", support: "Added Chaos Damage Support");
        var original = f.Hit();
        f.Set("physical_damage_%_to_convert_to_fire", 50);
        f.Set("physical_damage_%_to_add_as_chaos", 25);
        var converted = f.Hit();
        Assert.Equal(original[DamageType.Physical].Scale(.5), converted[DamageType.Physical]);
        Assert.Equal(original[DamageType.Physical].Scale(.5), converted[DamageType.Fire]);
        Assert.Equal(original[DamageType.Chaos].Add(original[DamageType.Physical].Scale(.25)),
            converted[DamageType.Chaos]);
        f.Set(Flag, 1);
        Assert.Same(DamagePacket.Empty, f.Hit());
        Assert.Equal(0, Value(f.Panel(), "spell_maximum_total_damage"));
        Assert.Equal(0, Value(f.Panel(), "hundred_times_damage_per_second"));
        f.Set(Flag, 0);
        Assert.Equal(converted.Components, f.Hit().Components);
    }

    [Theory]
    [InlineData("RighteousFire")]
    [InlineData("ScorchingRay")]
    [InlineData("Haste")]
    public void Final_hitless_spell_records_clear_inherited_damage_with_or_without_the_restriction(string effect)
    {
        using var f = new Fixture(effect);
        f.Set("attack_minimum_added_chaos_damage", 100);
        f.Set("attack_maximum_added_chaos_damage", 100);
        f.Set("spell_minimum_added_chaos_damage", 100);
        f.Set("spell_maximum_added_chaos_damage", 100);
        Assert.False(SkillHitPlan.ForSkill(Program.Skills, f.Effect, 1).CanHit);
        Assert.Equal(100, Program.CharacterPlayerStats(f.Conn)
            .GetValueOrDefault(Program.Stats.Key("main_hand_minimum_chaos_damage")!.Value));
        foreach (int flag in new[] { 0, 1, 0 })
        {
            f.Set(Flag, flag);
            var panel = f.Panel();
            foreach (string hand in new[] { "spell", "main_hand" })
                foreach (string type in new[] { "total", "physical", "fire", "cold", "lightning", "chaos" })
                    foreach (string bound in new[] { "minimum", "maximum" })
                        Assert.Equal(0, Value(panel, $"{hand}_{bound}_{type}_damage"));
            Assert.Equal(0, Value(panel, "display_hundred_times_damage_per_skill_use"));
            Assert.Equal(0, Value(panel, "hundred_times_damage_per_second"));
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task Released_spell_projectiles_resolve_live_source_changes_at_impact(int releaseFlag, int impactFlag)
    {
        using var f = new Fixture("Fireball", targetX: 100);
        f.Set(Flag, releaseFlag);
        var launch = f.Invoke("LaunchProjectile", f.Skill, (32, 32), (100, 32), f.Conn.HitRecovery.Generation,
            42u, (ushort)7, null, null);
        await Until(() => f.Conn.ProjectileRuntime?.Runtime.Projectiles.Count > 0);
        using (Program.ProjectileMutation(f.Conn, advance: false))
        {
            f.Conn.Clock.Stop();
            Assert.Equal(f.Target.MaxLife, f.Target.Life);
            if (impactFlag == 0) f.Conn.PlayerStats.Remove(5341);
            else f.Set(Flag, impactFlag);
            f.Conn.ProjectileRuntime!.AdvanceTo(GameServer.Core.Projectiles.ProjectileRuntime.Time(5));
        }
        await launch.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(impactFlag == 1, f.Target.Life == f.Target.MaxLife);
        Assert.True(double.IsFinite(f.Conn.LastEnemyHitAt));
        f.Conn.Clock.Start();
        await Until(() => f.Conn.ProjectileObjects.Count == 0);
        f.Conn.Closed = true; f.Conn.ProjectileRuntime!.Invalidate();
        await f.Conn.ProjectileRuntime.OutputCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(f.Conn.ProjectileObjects);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "Crude Bow")]
    [InlineData("Cyclone", null)]
    [InlineData("BurningArrow", "Crude Bow")]
    [InlineData("CausticArrow", "Crude Bow")]
    public async Task Ordinary_melee_bow_and_attack_gems_keep_damage_with_the_spell_flag(string? effect, string? weapon)
    {
        using var f = new Fixture(effect, weapon);
        f.Set("attack_minimum_added_chaos_damage", 100);
        f.Set("attack_maximum_added_chaos_damage", 100);
        if (effect is not null)
        {
            var baseline = f.Hit();
            f.Set(Flag, 1);
            Assert.Equal(baseline.Components, f.Hit().Components);
        }
        else f.Set(Flag, 1);
        await f.Dispatch(attack: effect is null);
        await Until(() => f.Target.Life < f.Target.MaxLife);
        Assert.True(double.IsFinite(f.Conn.LastEnemyHitAt));
        await Until(() => f.Conn.ProjectileObjects.Count == 0);
    }

    [Theory]
    [InlineData("Anger", "Fire")]
    [InlineData("Wrath", "Lightning")]
    [InlineData("Hatred", "Cold")]
    public async Task Spell_tagged_auras_keep_their_buffs_and_damage_granted_to_attacks(string effect, string type)
    {
        using var f = new Fixture(effect);
        Assert.True(Program.Skills.IsSpell(f.Effect));
        f.Set(Flag, 1);
        await f.Dispatch(instant: true);
        await Until(() => f.Conn.Auras.Count == 1);
        var stats = Program.CharacterPlayerStats(f.Conn);
        var weapon = new SkillCatalog.Unarmed(1000, 100, 100, 20);
        var hit = Program.AttackHit(weapon, stats, melee: true)
            .Convert(DamageStats.Conversion(stats, Program.Stats, attack: true));
        Assert.True(hit[Enum.Parse<DamageType>(type)].Max > 0);
        f.Set(Flag, 0);
        stats = Program.CharacterPlayerStats(f.Conn);
        var withoutFlag = Program.AttackHit(weapon, stats, melee: true)
            .Convert(DamageStats.Conversion(stats, Program.Stats, attack: true));
        Assert.Equal(hit.Components, withoutFlag.Components);
        Assert.True(f.Conn.Auras.Count == 1);
        f.Set(Flag, 1);
        await f.Dispatch(attack: true);
        await Until(() => f.Target.Life < f.Target.MaxLife);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    public async Task Righteous_Fire_enemy_ticks_use_live_sources_while_self_burn_keeps_running(int initialFlag)
    {
        using var f = new Fixture("RighteousFire");
        f.Set(Flag, initialFlag);
        f.Set("gain_x_rage_on_hit", 2);
        await Program.UseInstantSkill("spell-no-damage-test", f.Stream, f.Ciphers, f.Conn,
            f.Skill, 0x1000, (32, 32));
        var rf = Assert.IsType<RighteousFireState>(f.Conn.RighteousFire);
        Assert.NotNull(rf.Ring);
        uint initialLife = f.Target.Life;
        await f.Invoke("BurnAround", rf, .25);
        Assert.Equal(initialFlag != 0, initialLife == f.Target.Life);
        Assert.Single(rf.Auras); // Non-damage enemy aura membership remains active.
        f.Set(Flag, 1);
        f.Target.Burn = .375;
        uint suppressedLife = f.Target.Life;
        await f.Invoke("BurnAround", rf, .25);
        Assert.Equal(suppressedLife, f.Target.Life);
        Assert.Equal(.375, f.Target.Burn);
        Assert.False(double.IsFinite(f.Conn.LastEnemyHitAt));
        Assert.Equal(0, Value(Program.CharacterPlayerStats(f.Conn), "current_rage"));
        uint selfLife = f.Conn.PlayerLife;
        var recovery = f.Invoke("RunResourceRecovery");
        try
        {
            await Until(() => f.Conn.PlayerLife < selfLife);
            Assert.Same(rf, f.Conn.RighteousFire);
            Assert.True(f.Conn.PlayerLife > 0);
            Assert.Equal(suppressedLife, f.Target.Life);
            f.Conn.PlayerStats.Remove(5341);
            await f.Invoke("BurnAround", rf, .25);
            Assert.True(f.Target.Life < suppressedLife);
        }
        finally
        {
            f.Conn.Closed = true;
            await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
