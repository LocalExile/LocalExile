using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Modding;

namespace GameServer.Gateway.Tests;

public class TinctureTests
{
    [Fact]
    public void Tinctures_equip_in_flask_belt_serialize_wire_state_and_scale_with_quality_and_currencies()
    {
        var items = Program.Items;
        var rosethorn = items.Find("Rosethorn Tincture");
        Assert.NotNull(rosethorn);
        Assert.Equal("Tincture", rosethorn.Class);
        Assert.Equal("Metadata/Items/Tinctures/Tincture2", rosethorn.Id);
        Assert.True(rosethorn.Supported);

        var def = items.TinctureOf(rosethorn);
        Assert.NotNull(def);
        Assert.Equal(0.7, def.DebuffIntervalSeconds, 6);
        Assert.Equal(8.0, def.CooldownSeconds, 6);
        Assert.True(def.BuffRow > 0);
        Assert.Equal("tincture_rosethorn", def.BuffId);

        Assert.True(items.TinctureBuffs.ManaBurn > 0);
        Assert.True(items.TinctureBuffs.LifeBurn > 0);
        Assert.True(items.TinctureBuffs.Parent > 0);
        Assert.True(items.TinctureBuffs.Uniques.TryGetValue("tincture_unique4", out ushort unique4Buff) && unique4Buff > 0);

        var battleWithin = new UniqueCatalog(items).Create("The Battle Within", new Random(7));
        Assert.NotNull(battleWithin);
        Assert.Equal(unique4Buff, Tinctures.ResolveBuffRow(battleWithin, items.Mods, def, items.TinctureBuffs));

        // Belt slot acceptance: tinctures fit flask belt slots 0..4 via cursor placement and not other equipment slots.
        Assert.True(PlayerInventories.Accepts(PlayerInventories.Flask, "Tincture"));
        Assert.False(PlayerInventories.Accepts(PlayerInventories.Belt, "Tincture"));
        var path = Path.Combine(Path.GetTempPath(), $"tincture-belt-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        const string character = "TinctureBeltTest";
        try
        {
            var inv = PlayerInventories.For(character, items);
            foreach (var starter in inv.Items(PlayerInventories.Flask).ToArray())
                inv.Remove(PlayerInventories.Flask, starter.Id);
            var created = ItemCatalog.Create(rosethorn, random: new Random(1));
            inv.Add(PlayerInventories.Cursor, 0, 0, created);
            Assert.Null(inv.Place(PlayerInventories.Belt, 0, 0, Game.ShapeOf, out _));
            Assert.NotNull(inv.Place(PlayerInventories.Flask, 0, 0, Game.ShapeOf, out _));
            inv.Add(PlayerInventories.Cursor, 0, 0, battleWithin);
            Assert.NotNull(inv.Place(PlayerInventories.Flask, 4, 0, Game.ShapeOf, out _));
            Assert.Equal(2, inv.Items(PlayerInventories.Flask).Count);
            Assert.All(inv.Items(PlayerInventories.Flask), slot => Assert.False(slot.Item.TinctureActive));

            // Wire serialization: Tincture component emits 0 when stored/inactive and 1 when wire-active; JSON ignores it.
            byte[] inactivePayload = created.Payload();
            byte[] activePayload = (created with { TinctureActive = true }).Payload();
            Assert.Equal(inactivePayload.Length, activePayload.Length);
            int diffs = 0;
            for (int i = 0; i < inactivePayload.Length; i++)
                if (inactivePayload[i] != activePayload[i])
                {
                    diffs++;
                    Assert.Equal(0, inactivePayload[i]);
                    Assert.Equal(1, activePayload[i]);
                }
            Assert.Equal(1, diffs);
            var roundTripped = JsonSerializer.Deserialize<Item>(JsonSerializer.Serialize(created with { TinctureActive = true }))!;
            Assert.False(roundTripped.TinctureActive);

            // Quality and magic-only currency rules.
            var qualityUp = CurrencyEffects.Glassblower(created);
            Assert.NotNull(qualityUp);
            Assert.Equal(20, qualityUp.Quality);
            var rolls = new ItemRolls(items);
            var transmuted = rolls.Transmute(created, new Random(2));
            Assert.NotNull(transmuted);
            Assert.Equal(ItemRarity.Magic, transmuted.Rarity);
            Assert.Null(rolls.Regal(transmuted, new Random(3)));
            Assert.Null(rolls.Alchemy(created, new Random(4)));

            // Effect multiplier, scaled mods (flag stats unscaled), burn interval, cooldown, and burn rate.
            var cullingMod = items.Mods.All.First(m => m.Stats.Any(s => s.Id == "culling_strike_with_tinctured_weapons"));
            var withCulling = qualityUp with
            {
                Mods = [.. qualityUp.Mods, cullingMod.With(ModSection.Explicit, cullingMod.MaxValues(), new Random(5))],
            };
            double mult = Tinctures.EffectMultiplier(withCulling, new Dictionary<string, int>
            {
                ["local_tincture_mod_effect_+%"] = 30,
            }, name => name switch
            {
                "tincture_mod_effect_+%" => 50,
                "tincture_mod_effect_+%_per_empty_flask_slot" => 10,
                _ => 0,
            }, emptyFlaskSlots: 2);
            Assert.Equal(2.2, mult, 6);
            var scaled = Tinctures.ScaledMods(withCulling, items.Mods, mult);
            int rawCrit = ItemModStats.Values(withCulling, items.Mods, local: false)
                .Where(s => s.Id == "critical_strike_chance_+%_with_tinctured_weapons").Sum(s => s.Value);
            Assert.Equal(UtilityFlasks.Scale(rawCrit, mult),
                scaled.Single(s => s.Stat == "critical_strike_chance_+%_with_tinctured_weapons").Value);
            Assert.Equal(1, scaled.Single(s => s.Stat == "culling_strike_with_tinctured_weapons").Value);

            Assert.Equal(0.35, Tinctures.DebuffInterval(def, new Dictionary<string, int>
            {
                ["local_tincture_toxicity_rate_+%"] = 100,
            }, _ => 0), 6);
            Assert.True(double.IsPositiveInfinity(Tinctures.DebuffInterval(def, new Dictionary<string, int>
            {
                ["local_cannot_generate_toxicity_stacks_over_time"] = 1,
            }, _ => 0)));
            Assert.Equal(4.0, Tinctures.CooldownSeconds(def, new Dictionary<string, int>(),
                name => name == "tincture_cooldown_recovery_+%" ? 100 : 0), 6);
            Assert.Equal(0.0, Tinctures.BurnPerSecond(2, 200, ignoredStacks: 2), 6);
            Assert.Equal(6.0, Tinctures.BurnPerSecond(5, 200, ignoredStacks: 2), 6);

            // Mightblood Ire Melee Splash activates only when tincture_applied != 0 and applies no less-damage penalty.
            Assert.Null(MeleeSplash.Settings(name => name == "melee_splash_with_tinctured_weapons" ? 1 : 0));
            var splash = MeleeSplash.Settings(name => name switch
            {
                "melee_splash_with_tinctured_weapons" => 1,
                "tincture_applied" => 1,
                "melee_splash_area_of_effect_+%" => 50,
                _ => 0,
            });
            Assert.NotNull(splash);
            Assert.Equal(1.0, splash.Value.More, 6);
            Assert.True(splash.Value.Radius > MeleeSplash.BaseRadius);

            // Weapon-only ignite chance/duration apply on main_hand and never leak to spell.
            int IgniteStat(string name) => name switch
            {
                "tincture_applied" => 1,
                "chance_to_ignite_%_with_tinctured_weapons" => 40,
                "elemental_ailment_duration_+%_with_tinctured_weapons" => 50,
                _ => 0,
            };
            var weaponIgnite = MonsterIgnites.Settings.From([], "main_hand", IgniteStat);
            var spellIgnite = MonsterIgnites.Settings.From([], "spell", IgniteStat);
            Assert.Equal(40, weaponIgnite.Chance);
            Assert.Equal(6.0, weaponIgnite.Seconds, 6);
            Assert.Equal(0, spellIgnite.Chance);
            Assert.Equal(4.0, spellIgnite.Seconds, 6);

            // Organic drops: native 3.25+ tinctures can drop from monsters, never legacy rows and never rare.
            var drops = new ItemDrops(items, rolls: rolls, uniques: new UniqueCatalog(items));
            var dropRng = new Random(12345);
            var droppedTinctures = new List<ItemDrops.Drop>();
            for (int i = 0; i < 600 && droppedTinctures.Count < 10; i++)
                foreach (var d in drops.Roll(68, MonsterRarity.Rare, dropRng))
                    if (d.Type.Class == "Tincture") droppedTinctures.Add(d);
            Assert.NotEmpty(droppedTinctures);
            Assert.All(droppedTinctures, d =>
            {
                Assert.True(items.IsNativeTincture(d.Type));
                Assert.Contains(d.Item.Rarity, new[] { ItemRarity.Magic, ItemRarity.Unique });
            });
        }
        finally
        {
            PlayerInventories.Forget(character);
            TestEnvironment.ReleaseSaves(path);
        }
    }

    [Fact]
    public async Task Tincture_activation_stats_burn_cooldown_and_cleanup_lifecycle()
    {
        var items = Program.Items;
        var rosethorn = items.Find("Rosethorn Tincture")!;
        var sword = ItemCatalog.Create(items.Find("Rusted Sword")!, random: new Random(1));
        var bow = ItemCatalog.Create(items.Find("Crude Bow")!, random: new Random(2));
        var critMultMod = items.Mods.All.First(m => m.Stats.Any(s => s.Id == "critical_strike_multiplier_+_with_tinctured_weapons"));
        var cullMod = items.Mods.All.First(m => m.Stats.Any(s => s.Id == "culling_strike_with_tinctured_weapons"));
        var baseTincture = ItemCatalog.Create(rosethorn, random: new Random(3));
        string[] killStats = ["life_gained_on_killing_enemies_with_tinctured_weapons", "base_mana_gained_on_tinctured_weapon_kill",
            "flask_charges_granted_from_tinctured_weapon_kills_+%"];
        var killMods = killStats.Select(stat => items.Mods.All.First(m => m.Stats.Any(s => s.Id == stat)))
            .Select(mod => mod.With(ModSection.Explicit, mod.MaxValues(), new Random(6)));
        var tinctureItem = baseTincture with
        {
            Rarity = ItemRarity.Magic,
            Quality = 20,
            Mods =
            [
                .. baseTincture.Mods,
                .. killMods,
                critMultMod.With(ModSection.Explicit, [30], new Random(4)),
                cullMod.With(ModSection.Explicit, [1], new Random(5)),
            ],
        };

        var path = Path.Combine(Path.GetTempPath(), $"tincture-life-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic());
        var names = new List<string>();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var writer = new TcpClient();
        writer.Connect((IPEndPoint)listener.LocalEndpoint);
        using var reader = listener.AcceptTcpClient();
        var stream = writer.GetStream();
        var ciphers = new ConnectionCiphers(new byte[64], false);

        ClientState MakeConn(string name, int level, out InventoryItem placedTincture, out InventoryItem placedSword)
        {
            names.Add(name);
            var conn = new ClientState(new(1, name, 200, 200, 100, 100, null, []), 1, 1)
            {
                Character = name,
                PlayerLife = 200,
                PlayerMana = 100,
            };
            conn.Pools = Game.PoolsFor(conn, level);
            conn.PlayerLife = conn.PlayerMaxLife;
            conn.PlayerMana = (uint)conn.Pools.Mana;
            var inv = Game.ItemsOf(conn);
            foreach (var starter in inv.Items(PlayerInventories.Weapon1).ToArray())
                inv.Remove(PlayerInventories.Weapon1, starter.Id);
            foreach (var starter in inv.Items(PlayerInventories.Flask).ToArray())
                inv.Remove(PlayerInventories.Flask, starter.Id);
            placedSword = inv.Add(PlayerInventories.Weapon1, 0, 0, sword);
            placedTincture = inv.Add(PlayerInventories.Flask, 0, 0, tinctureItem);
            return conn;
        }

        int Stat(IReadOnlyDictionary<uint, int> stats, string name) =>
            Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

        CharacterStats.Bonus BonusOf(ClientState conn)
        {
            var (stats, attributes) = Game.CharacterStatSources(conn);
            return Game.PassiveBonus(stats, Game.WeaponProfileFor(conn, activeSet: true), attributes);
        }

        try
        {
            // 1. Level gate, activation via UseFlask, weapon-only stat isolation, and unequip cleanup.
            var conn1 = MakeConn("TinctureStats_" + Guid.NewGuid().ToString("N"), level: 1, out var slot0, out var eqSword);
            await Game.UseFlask("tincture-test", stream, ciphers, conn1, 0);
            Assert.False(conn1.TinctureEffects.AnyActive);

            conn1.Pools = Game.PoolsFor(conn1, level: 60);
            var baselineStats = Game.CharacterStatSources(conn1).Stats;
            int baseAttackCrit = Stat(baselineStats, "attack_critical_strike_chance_+%");
            var baseBonus = BonusOf(conn1);
            Assert.Equal(0, Game.Cull.Of(baselineStats, skill: null, critical: false, spell: false).Threshold);

            await Game.UseFlask("tincture-test", stream, ciphers, conn1, 0);
            Assert.True(conn1.TinctureEffects.IsActive(slot0.Id));
            Assert.False(Game.ItemsOf(conn1).Items(PlayerInventories.Flask).Single().Item.TinctureActive);

            var activeStats = Game.CharacterStatSources(conn1).Stats;
            var activeBonus = BonusOf(conn1);
            Assert.Equal(1, Stat(activeStats, "using_tincture"));
            Assert.Equal(1, Stat(activeStats, "tincture_applied"));
            Assert.Equal(1, Stat(activeStats, "tincture_applied_to_main_hand"));
            Assert.True(Stat(activeStats, "attack_critical_strike_chance_+%") > baseAttackCrit);
            Assert.Equal(baseBonus.CriticalStrikeMultiplier + 36, activeBonus.CriticalStrikeMultiplier); // 30 * 1.2 quality
            Assert.Equal(baseBonus.SpellCriticalStrikeMultiplier, activeBonus.SpellCriticalStrikeMultiplier);
            Assert.Equal(10, Game.Cull.Of(activeStats, skill: null, critical: false, spell: false).Threshold);
            Assert.Equal(0, Game.Cull.Of(activeStats, skill: null, critical: false, spell: true).Threshold);

            // A spell kill uses these unconditional stats even while a tinctured sword is equipped.
            foreach (string stat in new[] { "base_life_gained_on_enemy_death", "base_mana_gained_on_enemy_death",
                "charges_gained_+%", "flask_charges_gained_+%" })
                Assert.Equal(Stat(baselineStats, stat), Stat(activeStats, stat));
            var kill = Game.TinctureKillBonusFor(conn1, activeStats, AttackHand.Main);
            Assert.NotNull(kill);
            Assert.All(killStats, stat => Assert.True(Stat(activeStats, stat) > 0));
            Assert.Equal((Stat(activeStats, killStats[0]), Stat(activeStats, killStats[1]), Stat(activeStats, killStats[2])),
                (kill.Life, kill.Mana, kill.FlaskChargesPct));
            Assert.Null(Game.TinctureKillBonusFor(conn1, activeStats, AttackHand.Off));
            Assert.Equal(1 + kill.FlaskChargesPct / 100.0, FlaskCharges.KillCharges(MonsterRarity.Normal, items.Mods, kill.FlaskChargesPct));

            // Ranged weapon gets no weapon stats without Seasoned Hunter, and gets them with it; unarmed gets none.
            var inv1 = Game.ItemsOf(conn1);
            inv1.Remove(PlayerInventories.Weapon1, eqSword.Id);
            var eqBow = inv1.Add(PlayerInventories.Weapon1, 0, 0, bow);
            var bowStats = Game.CharacterStatSources(conn1).Stats;
            Assert.Equal(0, Stat(bowStats, "tincture_applied"));
            Assert.Equal(baseAttackCrit, Stat(bowStats, "attack_critical_strike_chance_+%"));
            conn1.PlayerStats[Program.Stats.Key("tinctures_can_apply_to_ranged_weapons")!.Value] = 1;
            var bowWithMastery = Game.CharacterStatSources(conn1).Stats;
            Assert.Equal(1, Stat(bowWithMastery, "tincture_applied"));
            Assert.True(Stat(bowWithMastery, "attack_critical_strike_chance_+%") > baseAttackCrit);
            inv1.Remove(PlayerInventories.Weapon1, eqBow.Id);
            var unarmedStats = Game.CharacterStatSources(conn1).Stats;
            Assert.Equal(0, Stat(unarmedStats, "tincture_applied"));
            Assert.Equal(baseAttackCrit, Stat(unarmedStats, "attack_critical_strike_chance_+%"));

            // Unequipping the tincture immediately deactivates it and removes its effects.
            inv1.Remove(PlayerInventories.Flask, slot0.Id);
            await Game.ReconcileTincturesAfterFlaskBarChange("tincture-test", stream, ciphers, conn1);
            Assert.False(conn1.TinctureEffects.AnyEffective);
            Assert.Equal(0, Stat(Game.CharacterStatSources(conn1).Stats, "using_tincture"));

            // 2. Mana Burn accumulation, on-kill removal, 0-mana auto-deactivation, and cooldown enforcement.
            var conn2 = MakeConn("TinctureBurn_" + Guid.NewGuid().ToString("N"), level: 60, out var slot2, out _);
            await Game.UseFlask("tincture-test", stream, ciphers, conn2, 0);
            Assert.True(conn2.TinctureEffects.IsActive(slot2.Id));
            double t2 = conn2.TinctureEffects.Of(slot2.Id)!.ActivatedAt;
            List<uint>? deactivated = null;
            var stats2 = Game.CharacterStatSources(conn2).Stats;
            Game.AdvanceTincturesLocked(conn2, now: t2 + 0.71, seconds: 0.71, stats2, out double manaDrain, out double lifeDrain, ref deactivated);
            Assert.Equal(1, conn2.TinctureEffects.BurnStacks);
            Assert.Equal(60, Stat(Game.CharacterStatSources(conn2).Stats, "mana_degeneration_per_minute_%_per_tincture_toxicity"));
            Assert.True(manaDrain > 0);
            Assert.Equal(0, lifeDrain);

            // On-kill Mana Burn removal mastery removes 1 stack.
            conn2.PlayerStats[Program.Stats.Key("chance_to_remove_1_tincture_toxicity_on_kill_%")!.Value] = 100;
            Game.ChargesOnKill(conn2, new SpawnedMonster("target", new(10, 10, 0), 0, 0, 100));
            Assert.Equal(0, conn2.TinctureEffects.BurnStacks);

            // Hitting 0 Mana auto-deactivates, clears stacks, starts cooldown, and blocks reactivation during cooldown.
            Game.AdvanceTincturesLocked(conn2, now: t2 + 1.42, seconds: 0.71, stats2, out _, out _, ref deactivated);
            Assert.Equal(1, conn2.TinctureEffects.BurnStacks);
            conn2.PlayerMana = 0;
            Game.CheckTinctureResourceDeactivationLocked(conn2, now: t2 + 1.42, stats2, ref deactivated);
            Assert.False(conn2.TinctureEffects.AnyActive);
            Assert.Equal(0, conn2.TinctureEffects.BurnStacks);
            Assert.NotNull(deactivated);
            Assert.Contains(slot2.Id, deactivated);
            Assert.True(conn2.TinctureEffects.OnCooldown(t2 + 1.42));
            Assert.False(conn2.TinctureEffects.OnCooldown(t2 + 10.0));
            conn2.PlayerMana = 100;
            await Game.UseFlask("tincture-test", stream, ciphers, conn2, 0);
            Assert.False(conn2.TinctureEffects.AnyActive);

            // 3. Manual toggle, Bloodsoaked Blade (life burn), Enduring Suffusion linger, and death cleanup.
            var conn3 = MakeConn("TinctureLife_" + Guid.NewGuid().ToString("N"), level: 60, out var slot3, out _);
            conn3.PlayerStats[Program.Stats.Key("keystone_tinctures_drain_life")!.Value] = 1;
            conn3.PlayerStats[Program.Stats.Key("tincture_effects_linger_for_ms_per_toxicity_up_to_6_seconds")!.Value] = 500;
            await Game.UseFlask("tincture-test", stream, ciphers, conn3, 0);
            Assert.True(conn3.TinctureEffects.IsActive(slot3.Id));
            Assert.True(conn3.TinctureEffects.LifeBurn);
            double t3 = conn3.TinctureEffects.Of(slot3.Id)!.ActivatedAt;

            var wildfire = items.Mods.All.Single(m => m.Id == "TinctureFireDamageTakenPerToxicityUnique__1");
            var wildStats = Tinctures.ScaledMods(baseTincture with { Mods = [wildfire.With(ModSection.Explicit, [-1], new Random(7))] }, items.Mods, 1);
            conn3.TinctureEffects.UpdateScaling(slot3.Id, conn3.TinctureEffects.Of(slot3.Id)!.EffectMultiplier, wildStats);
            var stats3 = Game.CharacterPlayerStats(conn3);
            List<uint>? deadIds = null;
            Game.AdvanceTincturesLocked(conn3, now: t3 + 1.42, seconds: 0.71, stats3, out manaDrain, out lifeDrain, ref deadIds);
            Assert.Equal(2, conn3.TinctureEffects.BurnStacks);
            var burning = Game.CharacterStatSources(conn3).Stats;
            Assert.Equal(-2, PlayerDamageTaken.Flat(DamageType.Fire, attack: false, name => Stat(burning, name)));
            Assert.Equal(0, Stat(burning, "fire_damage_taken_+%"));
            Assert.Equal(60, Stat(Game.CharacterStatSources(conn3).Stats, "non_lethal_life_loss_%_per_minute_per_tincture_toxicity"));
            Assert.Equal(0, manaDrain);
            Assert.True(lifeDrain > 0);

            // Manual toggle off starts lingering for 1.0 s (2 stacks * 500 ms) while clearing active & burn state.
            await Game.UseFlask("tincture-test", stream, ciphers, conn3, 0);
            Assert.False(conn3.TinctureEffects.AnyActive);
            Assert.Empty(conn3.FlaskAutomation.Packets);
            Assert.True(conn3.TinctureEffects.HasLingering);
            Assert.Equal(1, Stat(Game.CharacterStatSources(conn3).Stats, "has_lingering_tincture"));
            Assert.Equal(1, Stat(Game.CharacterStatSources(conn3).Stats, "tincture_applied"));

            // Death clears lingering effects too, and a fresh ClientState starts with no active tinctures.
            Game.DeactivateAllTincturesOnDeathLocked(conn3, now: t3 + 1.5, stats3, ref deadIds);
            Assert.False(conn3.TinctureEffects.AnyEffective);
            Assert.Equal(0, Stat(Game.CharacterStatSources(conn3).Stats, "tincture_applied"));

            // 4. Weapon-only DoT/PDR-ignore/low-life suffixes, unique on-hit hooks, Nature's Concoction, and live re-scaling.
            var conn4 = MakeConn("TinctureAdv_" + Guid.NewGuid().ToString("N"), level: 60, out var slot4, out _);
            var lifeFlask = ItemCatalog.Create(items.Find("Small Life Flask")!, random: new Random(9)) with { Charges = 0 };
            Game.ItemsOf(conn4).Add(PlayerInventories.Flask, 1, 0, lifeFlask);
            conn4.PlayerStats[Program.Stats.Key("tincture_effect_+%_if_used_life_flask_recently")!.Value] = 50;
            conn4.PlayerStats[Program.Stats.Key("flasks_adjacent_to_active_tinctures_have_effect_+%_if_hit_with_weapon_recently")!.Value] = 25;
            conn4.PlayerStats[Program.Stats.Key("flasks_adjacent_to_active_tinctures_gain_X_charges_on_weapon_hit")!.Value] = 3;
            conn4.PlayerStats[Program.Stats.Key("hits_ignore_enemy_monster_physical_damage_reduction_%_chance_with_tinctured_weapons")!.Value] = 100;
            conn4.PlayerStats[Program.Stats.Key("damage_over_time_multiplier_+_with_tinctured_weapons")!.Value] = 40;
            conn4.PlayerStats[Program.Stats.Key("apply_X_wither_stacks_for_2_seconds_with_tinctured_weapons")!.Value] = 2;
            conn4.PlayerStats[Program.Stats.Key("refresh_ignite_duration_on_tinctured_weapon_hit_%_chance")!.Value] = 100;
            conn4.PlayerStats[Program.Stats.Key("tinctured_weapon_cover_in_ash_on_hit_for_X_seconds_vs_full_life_enemies")!.Value] = 4;

            await Game.UseFlask("tincture-test", stream, ciphers, conn4, 0);
            Assert.True(conn4.TinctureEffects.IsActive(slot4.Id));
            double t4 = conn4.TinctureEffects.Of(slot4.Id)!.ActivatedAt;
            double multBefore = conn4.TinctureEffects.Of(slot4.Id)!.EffectMultiplier;

            // Live effect re-scaling: using a life flask recently boosts Colloidal Mixture mid-activation.
            conn4.RecentEvents.Record(StatConditions.UsedLifeFlask, t4 + 0.1);
            var stats4 = Game.CharacterPlayerStats(conn4);
            List<uint>? advDeactivated = null;
            Game.AdvanceTincturesLocked(conn4, now: t4 + 0.2, seconds: 0.2, stats4, out _, out _, ref advDeactivated);
            Assert.Equal(multBefore + 0.5, conn4.TinctureEffects.Of(slot4.Id)!.EffectMultiplier, 6);

            // Spell isolation on DoT multiplier: main_hand ignite scales, spell ignite does not.
            int Stats4(string name) => Stat(stats4, name);
            var fireBase = DamagePacket.Of(DamageType.Fire, 100, 100);
            var attackAilments = AilmentPanelStats.Compute(new("main_hand", Spell: false, Melee: true), fireBase, new DamageConversion(), Stats4);
            var spellAilments = AilmentPanelStats.Compute(new("spell", Spell: true, Melee: false), fireBase, new DamageConversion(), Stats4);
            int atkIgnite = attackAilments.Single(r => r.Stat == "minimum_ignite_damage_to_deal_per_minute_from_non_critical_main_hand_damage").Value;
            int splIgnite = spellAilments.Single(r => r.Stat == "minimum_ignite_damage_to_deal_per_minute_from_non_critical_spell_damage").Value;
            Assert.True(atkIgnite > splIgnite);

            // Ignore enemy physical damage reduction applies on attacks and not spells.
            var armored = new SpawnedMonster("armored", new(10, 10, 0), 0, 0, 1000)
            {
                EncounterStats = new Dictionary<string, int> { ["base_additional_physical_damage_reduction_%"] = 50 },
            };
            var physHit = DamagePacket.Of(DamageType.Physical, 100, 100);
            var resMods = Game.HitResistMods(stats4);
            Assert.Equal(100, Game.MonsterHitDamage(armored, physHit, critical: false, attack: true, now: t4 + 0.2, resMods).Damage);
            Assert.Equal(50, Game.MonsterHitDamage(armored, physHit, critical: false, attack: false, now: t4 + 0.2, resMods).Damage);

            // Low-life enemy damage suffix applies to weapon attacks against low-life targets.
            conn4.PlayerStats[Program.Stats.Key("tinctured_weapon_damage_+%_vs_enemies_that_are_on_low_life")!.Value] = 75;
            var lowLifeStats = Game.CharacterPlayerStats(conn4);
            var weapon = Game.SkillsFor(conn4).UnarmedFor(conn4.ClassRow);
            conn4.Monsters[99] = armored;
            var fullLifePrehit = Game.DefaultMeleePrehitProfileLocked(conn4, 99, armored, lowLifeStats, weapon, new DamageConversion(), null, t4 + 0.2, 100);
            armored.Life = 400; // <= 50% -> low life
            var lowLifePrehit = Game.DefaultMeleePrehitProfileLocked(conn4, 99, armored, lowLifeStats, weapon, new DamageConversion(), null, t4 + 0.2, 100);
            Assert.NotNull(fullLifePrehit);
            Assert.NotNull(lowLifePrehit);
            Assert.True(lowLifePrehit.Hit.MaxTotal > fullLifePrehit.Hit.MaxTotal);
            armored.Life = 1000;

            // On-weapon-hit hooks: Wither stacks, Ignite refresh, Covered in Ash (+20% fire taken), and Nature's Concoction.
            armored.Ignites.Add(t4, perSecond: 100, seconds: 4);
            double fireDotBefore = Game.MonsterDotTaken(armored, DamageType.Fire, t4 + 1.0);
            Game.TinctureOnWeaponHitLocked(conn4, stats4, armored, targetLifeBefore: armored.MaxLife, now: t4 + 1.0, new Random(1));
            Assert.Equal(2, armored.Withered.Count);
            Assert.Equal(t4 + 5.0, armored.Ignites.Until, 6);
            Assert.True(armored.CoveredInAshUntil >= t4 + 5.0);
            Assert.True(Game.MonsterDotTaken(armored, DamageType.Fire, t4 + 1.0) > fireDotBefore);

            // Nature's Concoction queued adjacent charges for slot 1 (adjacent to slot 0) and adjacent flask effect applies.
            var queuedCharges = conn4.TinctureEffects.DrainPendingAdjacentCharges();
            Assert.NotNull(queuedCharges);
            Assert.Contains((Slot: (byte)1, Charges: 3), queuedCharges);
            conn4.RecentEvents.RecordHit(t4 + 1.0, critical: false);
            Assert.Equal(25, Game.AdjacentTinctureFlaskEffect(conn4, slot: 1, Stats4, t4 + 1.0));
            Assert.Equal(0, Game.AdjacentTinctureFlaskEffect(conn4, slot: 3, Stats4, t4 + 1.0));

            conn4.PlayerStats[Program.Stats.Key("tincture_effects_linger_for_ms_per_toxicity_up_to_6_seconds")!.Value] = 500;
            conn4.TinctureEffects.AddBurnStacks(2);
            await Game.UseFlask("tincture-test", stream, ciphers, conn4, 0);
            Assert.True(conn4.TinctureEffects.HasLingering);
            Game.ItemsOf(conn4).Remove(PlayerInventories.Flask, slot4.Id);
            await Game.ReconcileTincturesAfterFlaskBarChange("tincture-test", stream, ciphers, conn4);
            Assert.False(conn4.TinctureEffects.AnyEffective);
        }
        finally
        {
            lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = previousLifetime;
            if (previousLogic is not null) Program.SetGameLogic(previousLogic);
            foreach (string n in names) PlayerInventories.Forget(n);
            TestEnvironment.ReleaseSaves(path);
        }
    }
}
