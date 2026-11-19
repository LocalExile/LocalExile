using GameServer.Core.Protocol.Instance;
using GameServer.Core.Accounts;

namespace GameServer.Gateway.Tests;

public sealed class SupportExecutionTests
{
    static Dictionary<uint, int> With(params (string Name, int Value)[] values) =>
        values.ToDictionary(v => Program.Stats.Key(v.Name)!.Value, v => v.Value);

    static ClientState Connection(string name)
    {
        var conn = new ClientState(new(1, name, 1000, 1000, 100, 100, null, []), 1, 1)
        {
            Character = name, PlayerLife = 1000, PlayerMana = 1000, Instance = new(),
            Experience = CharacterStats.ExperienceFor(100),
            Pools = new(100, 1000, 1000, int.MaxValue, 0, Strength: 1000, Dexterity: 1000, Intelligence: 1000),
            LeagueModule = new(new League("test", "Standard", false, false), new HashSet<string>(["Game"])),
        };
        foreach (string attr in new[] { "strength", "dexterity", "intelligence" })
            conn.PlayerStats[Program.Stats.Key($"virtual_{attr}_for_requirements")!.Value] = 1000;
        return conn;
    }

    static Item Gem(string name) => ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/" + name)!);
    static ushort Equip(ClientState conn, string active, string support)
    {
        var items = Game.ItemsOf(conn);
        foreach (var previous in items.EquipmentSnapshot().Where(e => e.Row == PlayerInventories.Weapon1))
            items.Remove(PlayerInventories.Weapon1, previous.Item.Id);
        var sword = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword1")!);
        items.Add(PlayerInventories.Weapon1, 0, 0, sword with
        {
            Sockets = [new(SocketColour.White, Gem(active)), new(SocketColour.White, Gem(support))], Links = [2],
        });
        return Game.GemSkillEffects(conn).Single().Id;
    }

    [Fact]
    public void Second_wind_reads_native_supports_and_recovers_stored_uses_in_order()
    {
        string path = Path.Combine(Path.GetTempPath(), $"supports-cooldown-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        Saves.Open(path); Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = Connection("SecondWindAcceptance");
        try
        {
            ushort id = Equip(conn, "SkillGemDash", "SupportGemSecondWind");
            var gem = Game.SupportedSkill(conn, id);
            var resolved = Game.CooldownFor(conn, gem.Effect, gem.Level, gem.Quality);
            Assert.True(resolved is not null, string.Join(", ", gem.Stats.Select(s => $"{Program.Stats.Name(s.Key)}={s.Value}")));
            var plan = resolved!.Value;
            // PoEDB: one additional NON-INSTANT use, level-one 24% reduced recovery, 200% cost multiplier.
            Assert.Equal(2, plan.Uses); Assert.Equal(-24, plan.RecoveryPercent);
            Assert.Equal(200, gem.Supports.Single().CostMultiplier);
            var state = new SingleUseCooldownState(0, plan.RecoveryRate);
            Assert.True(state.TryUse(0, plan));
            state.Refresh(.25, plan.RecoveryRate);
            double progress = state.RemainingBaseMilliseconds;
            Assert.True(state.TryUse(.25, plan)); Assert.Equal(progress, state.RemainingBaseMilliseconds, 6);
            Assert.False(state.TryUse(.25, plan)); Assert.Equal(2, state.SpentUses);
            // Native replacement contains two entries, and the second has a full base-time budget.
            var bytes = InstanceEntities.PlayerSingleUseCooldowns(1,
                [new(id, (float)(progress / 1000), plan.BaseMilliseconds / 1000f, (float)plan.RecoveryRate, state.SpentUses)]);
            var single = InstanceEntities.PlayerSingleUseCooldowns(1,
                [new(id, (float)(progress / 1000), plan.BaseMilliseconds / 1000f, (float)plan.RecoveryRate)]);
            Assert.Equal(single.Length + 16, bytes.Length);
            Assert.Equal((float)(progress / 1000), BitConverter.ToSingle(bytes, bytes.Length - 32));
            Assert.Equal(plan.BaseMilliseconds / 1000f, BitConverter.ToSingle(bytes, bytes.Length - 16));
            state.Refresh(plan.BaseMilliseconds / (1000 * plan.RecoveryRate), plan.RecoveryRate);
            Assert.True(state.Ready); Assert.Equal(1, state.SpentUses);
            Assert.True(state.TryUse(plan.BaseMilliseconds / (1000 * plan.RecoveryRate), plan));
            // Rate changes settle progress first, and reducing capacity never refunds a spent use.
            state.Refresh(plan.BaseMilliseconds / (1000 * plan.RecoveryRate) + .5, 2);
            state.Capacity(1); Assert.False(state.Ready);
            state.Capacity(2); Assert.False(state.Ready); // Re-linking cannot refund either spent use.
            state.Refresh(100, 2); Assert.True(state.FullyRecovered);
            var zero = new SingleUseCooldown(1000, -100, 2);
            Assert.True(state.TryUse(100, zero)); Assert.True(state.TryUse(100, zero));
            state.Refresh(1000, 0); Assert.False(state.Ready);
            // Instant skills receive recovery and cost modifiers, but never the extra use.
            id = Equip(conn, "SkillGemFrostblink", "SupportGemSecondWind");
            gem = Game.SupportedSkill(conn, id);
            plan = Game.CooldownFor(conn, gem.Effect, gem.Level, gem.Quality)!.Value;
            Assert.Equal(1, plan.Uses); Assert.Equal(-24, plan.RecoveryPercent);
            // Native Flame Dash already has three charges; Second Wind grants the fourth.
            id = Equip(conn, "SkillGemFlameDash", "SupportGemSecondWind");
            gem = Game.SupportedSkill(conn, id);
            Assert.Equal(4, Game.CooldownFor(conn, gem.Effect, gem.Level, gem.Quality)!.Value.Uses);
            // PoEDB's level-20 penalty (-5) and 20% quality recovery (+5) cancel in the installed native data.
            var qualityStats = Game.SkillsFor(conn).EffectStats(Game.SkillsFor(conn).EffectRow("SupportSecondWind"), 20, 20)
                .ToDictionary(s => s.Item1, s => s.Item2);
            Assert.Equal(0, qualityStats[Program.Stats.Key("base_cooldown_speed_+%")!.Value]);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!); Program.SetGameLogic(previous!); TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public void Machinations_costs_are_skill_local_and_combat_rush_is_consumed_only_by_paid_travel()
    {
        string path = Path.Combine(Path.GetTempPath(), $"supports-cost-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        Saves.Open(path); Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = Connection("SupportCostsAcceptance");
        try
        {
            ushort id = Equip(conn, "SkillGemFireball", "SupportGemMachinations");
            var gem = Game.SupportedSkill(conn, id);
            int Value(Game.SupportedGem skill, string name) => skill.Stats.GetValueOrDefault(Program.Stats.Key(name)!.Value);
            Assert.Equal(120, gem.Supports.Single().CostMultiplier);
            Assert.Equal(0, Value(gem, "base_cost_+%"));
            Assert.Equal(6, Value(gem, "mana_cost")); // Fireball 5 Mana * 120%.
            foreach (ushort node in new ushort[] { 10661, 12926 })
            { Assert.True(Program.Passives.Find(node)!.IsKeystone); conn.Passives.Add(node); }
            gem = Game.SupportedSkill(conn, id);
            Assert.Equal(20, Value(gem, "base_cost_+%"));
            Assert.Equal(0, Game.CharacterPlayerStats(conn).GetValueOrDefault(Program.Stats.Key("base_cost_+%")!.Value));
            Assert.Equal(12, Value(gem, "physical_damage_%_to_add_as_chaos"));
            conn.PlayerStats[Program.Stats.Key("mana_cost_+%")!.Value] = 30;
            // 6 * (100 + 20 + 30)% = 9: the two increases add, rather than multiply.
            Assert.Equal(9, Game.SupportedStats(conn, id)[Program.Stats.Key("mana_cost")!.Value]);
            Assert.True(Game.PayMana(conn, id, out uint mana, out uint life));
            Assert.Equal((9u, 0u, 991u), (mana, life, conn.PlayerMana));
            conn.PlayerStats[Program.Stats.Key("keystone_blood_magic")!.Value] = 1;
            Assert.True(Game.PayMana(conn, id, out mana, out life));
            Assert.Equal((0u, 9u, 991u), (mana, life, conn.PlayerLife));
            conn.PlayerStats.Remove(Program.Stats.Key("keystone_blood_magic")!.Value);
            conn.Passives.Clear();
            conn.PlayerStats.Remove(Program.Stats.Key("mana_cost_+%")!.Value);
            // Another link group has no Machinations increase, and refunding the keystones removes it.
            Assert.Equal(0, Value(Game.SupportedSkill(conn, id), "base_cost_+%"));
            id = Equip(conn, "SkillGemLeapSlam", "SupportGemMachinations");
            gem = Game.SupportedSkill(conn, id);
            Assert.True(Game.SkillsFor(conn).IsTravel(gem.Effect));
            double now = conn.Clock.Elapsed.TotalSeconds;
            Game.CombatRushOnHit(conn, With(("supported_by_slashing", 1),
                ("support_slashing_buff_base_duration_ms", 2000),
                ("support_slashing_buff_attack_speed_+%_final_to_grant", 20)), now);
            Assert.Equal(1.2, Game.CombatRushMore(conn, id, gem, now), 9);
            conn.PlayerMana = 0;
            Assert.False(Game.PayMana(conn, id, out _, out _));
            Assert.Equal(now + 2, conn.CombatRushUntil);
            conn.PlayerMana = 1000;
            double faster = Game.SupportedAttackSeconds(conn, id);
            Assert.True(Game.PayMana(conn, id, out _, out _));
            Assert.Equal(0, conn.CombatRushUntil);
            // Consumption must not slow the already accepted action when its release timing is read again.
            Assert.Equal(faster, Game.SupportedAttackSeconds(conn, id), 9);
            conn.ActionCount++;
            Assert.Equal(1, Game.CombatRushMore(conn, id, gem, conn.Clock.Elapsed.TotalSeconds));
            Assert.True(Game.SupportedAttackSeconds(conn, id) > faster);
            id = Equip(conn, "SkillGemLeapSlam", "SupportGemCloseCombat");
            gem = Game.SupportedSkill(conn, id);
            Assert.Single(gem.Supports);
            conn.CombatRushUntil = conn.Clock.Elapsed.TotalSeconds + 2; conn.CombatRushAttackSpeed = 20;
            Assert.Equal(1, Game.CombatRushMore(conn, id, gem, conn.Clock.Elapsed.TotalSeconds));
            Assert.True(Game.PayMana(conn, id, out _, out _));
            Assert.Equal(0, conn.CombatRushUntil); // supported travel consumes it too, without the speed bonus.
            id = Equip(conn, "SkillGemFireball", "SupportGemMachinations");
            conn.CombatRushUntil = conn.Clock.Elapsed.TotalSeconds + 2; conn.CombatRushAttackSpeed = 20;
            Assert.True(Game.PayMana(conn, id, out _, out _)); Assert.True(conn.CombatRushUntil > 0);
            conn.RetireOwnedCombat(); Assert.Equal(0, conn.CombatRushUntil); Assert.Null(conn.CombatRushUse);
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!); Program.SetGameLogic(previous!); TestEnvironment.ReleaseSaves(path);
        }
    }
    [Fact]
    public void Multistrike_preserves_native_panel_damage_and_paces_one_paid_cancellable_repeat_series()
    {
        string path = Path.Combine(Path.GetTempPath(), $"multistrike-panel-{Guid.NewGuid():N}.db");
        var previous = Program.GameLogicOrNull;
        Saves.Open(path); Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var conn = Connection("MultistrikePanelAcceptance");
        try
        {
            foreach (var (support, repeats) in new[]
            {
                ("SupportGemMultistrike", 2),
                ("SupportGemGreaterMultistrike", 3),
                ("SupportGemAddedFireDamage", 0),
            })
            {
                ushort id = Equip(conn, "SkillGemCleave", support);
                Assert.Equal(repeats, Game.MeleeAttackRepeats(conn, id));
                var items = Game.ItemsOf(conn);
                for (int hands = 1; hands <= 2; hands++)
                {
                    if (hands == 2)
                    {
                        var off = ItemCatalog.Create(Program.Items.Find(
                            "Metadata/Items/Weapons/OneHandWeapons/OneHandSwords/OneHandSword5")!);
                        items.Add(PlayerInventories.Offhand1, 0, 0, off);
                    }
                    conn.PlayerSkillsCache = null;
                    var rows = Game.PlayerSkills(conn).Single(s => s.Id == id).Stats!.ToDictionary(s => s.Key, s => s.Value);
                    int Row(string name) => rows.GetValueOrDefault(Program.Stats.Key(name)!.Value);
                    Assert.Equal(repeats, Row("attack_repeat_count"));
                    double Factor(string hand) => CriticalStrikePolicy.DamageFactor(Row(hand + "_critical_strike_chance"),
                        Row(hand + "_critical_strike_multiplier"), 0, Row(hand + "_chance_to_hit_%"));
                    // Dual-wielded Cleave hits with both weapons at once, at its data's merged 40% less damage
                    // (skill_double_hits_when_dual_wielding, active_skill_merged_damage_+%_final_while_dual_wielding).
                    double dualMore = Game.HandDamageMore(conn, Game.SupportedSkill(conn, id).Stats).DualWielding;
                    Assert.Equal(hands == 2 ? .6 : 1, dualMore, 9);
                    var mainHit = Game.SupportedHit(conn, id).Scale(dualMore);
                    foreach (var (stat, value) in PanelStats.HandDamage(mainHit, "main_hand"))
                        Assert.Equal(value, Row(stat));
                    double mainSeconds = Game.SupportedAttackSeconds(conn, id);
                    var main = SpellDamage.Display((mainHit.MinTotal, mainHit.MaxTotal), 1 / mainSeconds, Factor("main_hand"));
                    int perUse = main.PerUse, perSecond = main.PerSecond;
                    if (hands == 2)
                    {
                        var offWeapon = Game.OffhandWeaponFor(conn)!;
                        var offInputs = Game.SupportedHitInputsFor(conn, id, weapon: offWeapon)!;
                        var offHit = Game.SupportedHitFrom(offInputs, suppress: true).Scale(dualMore);
                        foreach (var (stat, value) in PanelStats.HandDamage(offHit, "off_hand"))
                            Assert.Equal(value, Row(stat));
                        double offSeconds = Game.SupportedOffHandAttackSeconds(conn, id, offWeapon);
                        var off = SpellDamage.Display((offHit.MinTotal, offHit.MaxTotal), 1 / offSeconds, Factor("off_hand"));
                        // Both hits per use, at the average of the two weapons' attack times.
                        Assert.Equal(AttackHand.Both, Game.PanelHands(conn, id));
                        perUse = main.PerUse + off.PerUse;
                        perSecond = (int)Math.Round(perUse / ((mainSeconds + offSeconds) / 2));
                    }
                    Assert.Equal(perUse, Row("display_hundred_times_damage_per_skill_use"));
                    Assert.Equal(perSecond, Row("hundred_times_damage_per_second"));
                    // Native calculators keep these rows per hit; repeat damage belongs to execution only.
                    for (int repeat = 1; repeat <= repeats; repeat++)
                        Assert.Equal(1 + .22 * repeat, Game.MeleeRepeatDamageMore(conn, id, repeat), 8);
                    conn.NextAttackAt = 10 + mainSeconds;
                    conn.LockstepBusyUntil = 10 + mainSeconds * .6;
                    uint mana = conn.PlayerMana;
                    Game.ReserveMeleeRepeats(conn, id, mainSeconds);
                    Assert.Equal(10 + mainSeconds * (repeats + 1), conn.NextAttackAt, 8);
                    Assert.Equal(mana, conn.PlayerMana);
                    if (repeats > 0)
                    {
                        Assert.False(Game.MeleeRepeatAllowsMove(conn, 10 + mainSeconds * 1.5));
                        Assert.True(Game.MeleeRepeatAllowsMove(conn, 10 + mainSeconds * 1.7));
                        Game.CancelMeleeRepeats(conn, 10 + mainSeconds * 1.7);
                        Assert.Equal(10 + mainSeconds * 2, conn.NextAttackAt, 8);
                        Assert.True(Game.MeleeRepeatAllowsMove(conn, 10 + mainSeconds * 1.7));
                        // An object interaction replaces the action too (loot, portals, NPCs). It must release the
                        // unstarted swings' reservation and movement gates, while preserving the current swing.
                        conn.Clock.Stop();
                        double now = Game.ProjectileWorldTime(conn), start = now - mainSeconds * .7;
                        conn.NextAttackAt = start + mainSeconds;
                        conn.LockstepBusyUntil = start + mainSeconds * .6;
                        Game.ReserveMeleeRepeats(conn, id, mainSeconds);
                        double reserved = conn.NextAttackAt;
                        conn.PlayerLife = 0;
                        Assert.False(Game.TryBeginObjectWalk(conn, now, 110, 100, 3, out _));
                        Assert.Equal(reserved, conn.NextAttackAt);
                        conn.PlayerLife = 1000;
                        Assert.True(Game.TryBeginObjectWalk(conn, now, 110, 100, 3, out _));
                        Assert.Equal(start + mainSeconds, conn.NextAttackAt, 8);
                        Assert.True(Game.MeleeRepeatAllowsMove(conn, start + mainSeconds * 1.5));
                    }
                }
                foreach (string disabled in new[] { "disable_attack_repeats", "disable_skill_repeats" })
                {
                    uint key = Program.Stats.Key(disabled)!.Value;
                    conn.PlayerStats[key] = 1;
                    Assert.Equal(0, Game.MeleeAttackRepeats(conn, id));
                    conn.PlayerSkillsCache = null;
                    Assert.Equal(0, Game.PlayerSkills(conn).Single(s => s.Id == id).Stats!.Single(s => s.Key == Program.Stats.Key("attack_repeat_count")).Value);
                    conn.PlayerStats.Remove(key);
                }
                foreach (var off in items.EquipmentSnapshot().Where(e => e.Row == PlayerInventories.Offhand1))
                    items.Remove(PlayerInventories.Offhand1, off.Item.Id);
            }
        }
        finally
        {
            PlayerInventories.Forget(conn.Character!); Program.SetGameLogic(previous!); TestEnvironment.ReleaseSaves(path);
        }
    }

}
