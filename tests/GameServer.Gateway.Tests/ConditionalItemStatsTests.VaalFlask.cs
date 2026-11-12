using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Qualified_vaal_use_and_during_flask_sources_have_final_consumers(ClientState conn,
        Func<string, byte, InventoryItem> wear)
    {
        conn.PlayerLife = conn.PlayerMaxLife;
        var inventory = Game.ItemsOf(conn); var charges = Game.ChargesOf(conn);
        var amulet = wear("Sacrificial Heart", 5); var stats = Game.CharacterPlayerStats(conn);
        int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        Assert.Equal(1, Get(Game.MaximumPowerOnVaalUse));
        charges.Die(); conn.Clock.Start();
        double at = Game.ResourceEffectsTime(conn);
        Game.GrantVaalUseChargesLocked(conn, at); conn.Clock.Stop();
        Assert.Equal(charges.Limits(ChargeType.Power).Maximum, charges.Effective(ChargeType.Power));
        Assert.True(conn.RecentEvents.Happened(StatConditions.GainedPowerCharge, at));
        var conversion = wear("Graven's Secret", PlayerInventories.Belt);
        stats = Game.CharacterPlayerStats(conn); charges.Die(); conn.RecentEvents.Clear();
        conn.Clock.Start(); at = Game.ResourceEffectsTime(conn); Game.GrantVaalUseChargesLocked(conn, at); conn.Clock.Stop();
        stats = Game.CharacterPlayerStats(conn);
        Assert.Equal(0, Get("current_power_charges")); Assert.True(Get("current_absorption_charges") > 0);
        Assert.False(conn.RecentEvents.Happened(StatConditions.GainedPowerCharge, at));
        inventory.Remove(PlayerInventories.Belt, conversion.Id); inventory.Remove(5, amulet.Id);
        stats = Game.CharacterPlayerStats(conn); charges.Die();
        conn.Clock.Start(); Game.GrantVaalUseChargesLocked(conn, Game.ResourceEffectsTime(conn)); conn.Clock.Stop();
        Assert.Equal(0, charges.Effective(ChargeType.Power));
        Projectile_kills_publish_current_vaal_budgets_at_the_output_gate(conn).GetAwaiter().GetResult();

        var boots = wear("Dusktoe", PlayerInventories.Boots);
        var raw = ItemModStats.Sum([boots.Item], Program.Items.Mods);
        const string minimum = "spell_and_attack_minimum_added_chaos_damage_during_flask_effect";
        const string maximum = "spell_and_attack_maximum_added_chaos_damage_during_flask_effect";
        int min = raw.GetValueOrDefault(minimum), max = raw.GetValueOrDefault(maximum);
        Assert.InRange(min, 30, 40); Assert.InRange(max, 50, 60);
        foreach (bool active in new[] { false, true, false })
        {
            var conditional = ItemModStats.Sum([boots.Item], Program.Items.Mods,
                subject: new(Flags: new Dictionary<string, bool> { ["_during_flask_effect"] = active,
                    ["_while_using_flask"] = active }), statExists: Program.Stats.Has);
            Assert.Equal(active ? min : 0, conditional.GetValueOrDefault("global_minimum_added_chaos_damage"));
            foreach (bool spell in new[] { false, true })
            {
                var added = DamageStats.RawAdded(DamagePacket.Empty, conditional.GetValueOrDefault, spell, .5);
                Assert.Equal(active ? min * .5 : 0, added[DamageType.Chaos].Min, 6);
                Assert.Equal(active ? max * .5 : 0, added[DamageType.Chaos].Max, 6);
            }
            Assert.False(conditional.ContainsKey(minimum)); Assert.False(conditional.ContainsKey(maximum));
        }
        Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of(minimum, StatConditions.Subject.Unknown));
        inventory.Remove(PlayerInventories.Boots, boots.Id); Game.CharacterPlayerStats(conn);
        Actual_atziri_flask_coefficients_reach_chaos_damage_and_leech();
        Actual_rumi_and_taste_effects_reach_block_conversion_and_avoidance();
    }

    void Actual_rumi_and_taste_effects_reach_block_conversion_and_avoidance()
    {
        foreach (string name in new[] { "Rumi's Concoction", "Replica Rumi's Concoction", "Taste of Hate" })
        {
            var flask = new UniqueCatalog(Program.Items).Create(name, new Random(504))!;
            var local = ItemModStats.Local(flask, Program.Items.Mods);
            var utility = Program.Items.UtilityFlaskOf(Program.Items.ByHash(flask.TypeHash)!)!;
            foreach (int increased in new[] { 0, 50 })
            {
                int Character(string stat) => stat == "flask_effect_+%" ? increased
                    : stat == "magic_utility_flask_effect_+%" ? 100 : 0;
                var effect = Game.ItemUtilityFlaskFor(utility, flask, local, Character, 100);
                var running = new UtilityFlasks.Effects();
                var active = new UtilityFlasks.Active(504, 2, name, utility, 1, null, effect.Stats, 10, 10 + effect.Seconds);
                running.Start(active);
                var combined = UtilityFlasks.Strongest(running.Sources().Concat(running.Sources()));
                var raw = new Dictionary<uint, int>(); Game.AddItemUtilityFlaskRawStats(raw, combined, Program.Stats);
                int Get(string stat) => Program.Stats.Key(stat) is uint key ? raw.GetValueOrDefault(key) : 0;
                var bonus = Game.ApplyItemUtilityFlaskBonuses(new(), combined);
                int Expected(string input)
                {
                    Assert.True(local.GetValueOrDefault(input) > 0, name + ": " + input);
                    return UtilityFlasks.Scale(local[input], (100 + increased) / 100d);
                }
                if (name.Contains("Rumi"))
                {
                    Assert.Equal(Expected("local_unique_flask_block_%_while_healing"), bonus.BlockChance);
                    Assert.Equal(Expected("local_unique_flask_spell_block_%_while_healing"), bonus.SpellBlockChance);
                    Assert.Equal(0, Get("additional_block_%")); Assert.Equal(0, Get("base_spell_block_%"));
                }
                else
                {
                    int cold = Expected("local_unique_flask_physical_damage_%_to_add_as_cold_while_healing");
                    Assert.Equal(cold, Get("physical_damage_%_to_add_as_cold"));
                    foreach (bool attack in new[] { false, true })
                    {
                        var converted = DamagePacket.Of(DamageType.Physical, 100, 100)
                            .Convert(DamageStats.Conversion(raw, Program.Stats, attack));
                        Assert.Equal(cold, converted[DamageType.Cold].Min, 8);
                        Assert.Equal(100, converted[DamageType.Physical].Min, 8);
                    }
                    foreach (var (kind, input, target) in new[]
                    {
                        (PlayerElementalAilments.Kind.Chill, "local_unique_flask_avoid_chill_%_while_healing", "base_avoid_chill_%"),
                        (PlayerElementalAilments.Kind.Freeze, "local_unique_flask_avoid_freeze_%_while_healing", "base_avoid_freeze_%"),
                    })
                    {
                        int chance = Expected(input); Assert.Equal(chance, Get(target));
                        var panel = PanelStats.Compute(Get).ToDictionary(p => p.Stat, p => p.Value);
                        var ailments = new PlayerElementalAilments();
                        ailments.Inflict(kind, 10, 2, 10, panel.GetValueOrDefault, new PhasingRoll(chance - 1));
                        Assert.Equal(0, ailments.Magnitude(kind, 10));
                        ailments.Inflict(kind, 10, 2, 10, panel.GetValueOrDefault, new PhasingRoll(chance));
                        Assert.Equal(10, ailments.Magnitude(kind, 10));
                    }
                }
                Assert.Single(running.End(10 + effect.Seconds, false, out _));
                Assert.Equal(0, Game.ApplyItemUtilityFlaskBonuses(new(), running.Stats()).BlockChance);
                raw.Clear(); Game.AddItemUtilityFlaskRawStats(raw, running.Stats(), Program.Stats); Assert.Empty(raw);
                running.Start(active);
                Assert.True(UtilityFlasks.LeftItsCell(active, []));
                Assert.Single(running.End(10, true, out _));
            }
        }
    }

    void Actual_atziri_flask_coefficients_reach_chaos_damage_and_leech()
    {
        var flask = new UniqueCatalog(Program.Items).Create("Atziri's Promise", new Random(504))!;
        var local = ItemModStats.Local(flask, Program.Items.Mods);
        var utility = Program.Items.UtilityFlaskOf(Program.Items.ByHash(flask.TypeHash)!)!;
        var rows = new[]
        {
            ("local_unique_flask_physical_damage_%_to_add_as_chaos_while_healing", "physical_damage_%_to_add_as_chaos"),
            ("local_unique_flask_elemental_damage_%_to_add_as_chaos_while_healing", "elemental_damage_%_to_add_as_chaos"),
            ("local_unique_flask_life_leech_from_chaos_damage_permyriad_while_healing", "base_life_leech_from_chaos_damage_permyriad"),
        };
        Assert.InRange(local[rows[0].Item1], 5, 8); Assert.InRange(local[rows[1].Item1], 5, 8);
        Assert.Equal(200, local[rows[2].Item1]);
        foreach (int increased in new[] { 0, 50 })
        {
            int Character(string name) => name == "flask_effect_+%" ? increased
                : name == "magic_utility_flask_effect_+%" ? 100 : 0;
            var effect = Game.ItemUtilityFlaskFor(utility, flask, local, Character, 100);
            var running = new UtilityFlasks.Effects();
            running.Start(new(504, 2, "Atziri's Promise", utility, 1, null, effect.Stats, 10, 10 + effect.Seconds));
            var raw = new Dictionary<uint, int>();
            // Repeated snapshots of one effect cannot stack it twice.
            Game.AddItemUtilityFlaskRawStats(raw,
                UtilityFlasks.Strongest(running.Sources().Concat(running.Sources())), Program.Stats);
            int Get(string name) => Program.Stats.Key(name) is uint key ? raw.GetValueOrDefault(key) : 0;
            foreach (var (source, target) in rows)
                Assert.Equal((int)(local[source] * (100 + increased) / 100d), Get(target));
            foreach (bool attack in new[] { false, true })
            {
                var packet = DamagePacket.Of(DamageType.Physical, 100, 100).Add(DamageType.Fire, new(100, 100));
                var converted = packet.Convert(DamageStats.Conversion(raw, Program.Stats, attack));
                Assert.Equal(Get(rows[0].Item2) + Get(rows[1].Item2), converted[DamageType.Chaos].Min, 6);
                Assert.Equal(Get(rows[2].Item2), LeechSources.From(Get, attack).Life[DamageType.Chaos].Min);
            }
            Assert.Single(running.End(10 + effect.Seconds, false, out _));
            raw.Clear(); Game.AddItemUtilityFlaskRawStats(raw, running.Stats(), Program.Stats);
            Assert.All(rows, row => Assert.Equal(0, Get(row.Item2)));
        }
    }

    async Task Projectile_kills_publish_current_vaal_budgets_at_the_output_gate(ClientState conn)
    {
        var inventory = Game.ItemsOf(conn);
        foreach (byte slot in new[] { PlayerInventories.Weapon1, PlayerInventories.Offhand1 })
            foreach (var item in inventory.Items(slot)) inventory.Remove(slot, item.Id);
        var gem = ItemCatalog.Create(Program.Items.Find("Metadata/Items/Gems/SkillGemVaalGroundSlam")!);
        var host = inventory.Add(PlayerInventories.Weapon1, 0, 0,
            ItemCatalog.Create(Program.Items.Find("Driftwood Club")!) with
                { Sockets = [new ItemSocket(SocketColour.White, gem)], Links = [1] });
        Game.RefreshVaalSouls(conn); Game.VaalSoulChanges(conn);
        var initial = Assert.Single(conn.VaalSouls.Read());
        Assert.Equal(0u, initial.Current); Assert.True(initial.Capacity > 0);
        var at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        var variety = Game.Monsters.Find("Cannibal")!;
        using var wire = new Wire();
        var session = new Game.ProjectileSession("vaal-kill-publication", wire.Stream, wire.Ciphers, conn);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.Clock.Start();
        async Task Drain()
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Enqueue(() => { done.SetResult(true); return Task.CompletedTask; });
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        void Kill(uint id)
        {
            var target = new SpawnedMonster("vaal publication", new(at.X, at.Y, 8), 60, 2, 1)
                { Life = 1, Hostile = true, Variety = variety, Level = 1, NoRewards = true };
            lock (conn.Monsters)
            {
                conn.Monsters[id] = target;
                Assert.NotNull(Game.ApplyMonsterHitLocked(conn, target, 1));
                Assert.True(target.SoulKillProcessed);
                Game.CommitProjectileKill(session, id, target, 0, new Random(504));
            }
        }
        try
        {
            Kill(0x50a0); await Drain();
            Assert.Equal(1u, Assert.Single(conn.VaalSouls.Read()).Current);
            Assert.Equal(1u, conn.SentVaalSouls[initial.ActiveSkillsRow].Current);
            session.Enqueue(async () => await release.Task);
            Kill(0x50a1);
            Assert.Equal(2u, Assert.Single(conn.VaalSouls.Read()).Current);
            inventory.Remove(PlayerInventories.Weapon1, host.Id);
            release.SetResult(true); await Drain();
            Assert.Empty(conn.VaalSouls.Read()); Assert.Empty(conn.SentVaalSouls);
        }
        finally
        {
            release.TrySetResult(true);
            session.Invalidate(); await session.OutputCompleted;
            conn.Clock.Stop();
            conn.Monsters.Remove(0x50a0); conn.Monsters.Remove(0x50a1);
            inventory.Remove(PlayerInventories.Weapon1, host.Id);
        }
    }
}
