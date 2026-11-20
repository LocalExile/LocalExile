using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
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
        var tinctureItem = baseTincture with
        {
            Rarity = ItemRarity.Magic,
            Quality = 20,
            Mods =
            [
                .. baseTincture.Mods,
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
            Assert.True(manaDrain > 0);
            Assert.Equal(0, lifeDrain);

            // On-kill Mana Burn removal mastery removes 1 stack.
            var killStats = new Dictionary<uint, int>(stats2)
            {
                [Program.Stats.Key("chance_to_remove_1_tincture_toxicity_on_kill_%")!.Value] = 100,
            };
            Game.TinctureOnKillLocked(conn2, killStats);
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

            var stats3 = Game.CharacterPlayerStats(conn3);
            List<uint>? deadIds = null;
            Game.AdvanceTincturesLocked(conn3, now: t3 + 1.42, seconds: 0.71, stats3, out manaDrain, out lifeDrain, ref deadIds);
            Assert.Equal(2, conn3.TinctureEffects.BurnStacks);
            Assert.Equal(0, manaDrain);
            Assert.True(lifeDrain > 0);

            // Manual toggle off starts lingering for 1.0 s (2 stacks * 500 ms) while clearing active & burn state.
            await Game.UseFlask("tincture-test", stream, ciphers, conn3, 0);
            Assert.False(conn3.TinctureEffects.AnyActive);
            Assert.True(conn3.TinctureEffects.HasLingering);
            Assert.Equal(1, Stat(Game.CharacterStatSources(conn3).Stats, "has_lingering_tincture"));
            Assert.Equal(1, Stat(Game.CharacterStatSources(conn3).Stats, "tincture_applied"));

            // Death clears lingering effects too, and a fresh ClientState starts with no active tinctures.
            Game.DeactivateAllTincturesOnDeathLocked(conn3, now: t3 + 1.5, stats3, ref deadIds);
            Assert.False(conn3.TinctureEffects.AnyEffective);
            Assert.Equal(0, Stat(Game.CharacterStatSources(conn3).Stats, "tincture_applied"));
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
