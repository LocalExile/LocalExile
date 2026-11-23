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
            var cullingMod = items.Mods.All.First(m => m.Stats.Any(s => s.Id == Stat.CullingStrikeWithTincturedWeapons));
            var withCulling = qualityUp with
            {
                Mods = [.. qualityUp.Mods, cullingMod.With(ModSection.Explicit, cullingMod.MaxValues(), new Random(5))],
            };
            double mult = Tinctures.EffectMultiplier(withCulling, new Dictionary<uint, int>
            {
                [Stat.LocalTinctureModEffectPlusPct] = 30,
            }, key => key switch
            {
                Stat.TinctureModEffectPlusPct => 50,
                Stat.TinctureModEffectPlusPctPerEmptyFlaskSlot => 10,
                _ => 0,
            }, emptyFlaskSlots: 2);
            Assert.Equal(2.2, mult, 6);
            var scaled = Tinctures.ScaledMods(withCulling, items.Mods, mult);
            int rawCrit = ItemModStats.Values(withCulling, items.Mods, local: false)
                .Where(s => s.Id == Stat.CriticalStrikeChancePlusPctWithTincturedWeapons).Sum(s => s.Value);
            Assert.Equal(UtilityFlasks.Scale(rawCrit, mult),
                scaled.Single(s => s.Stat == Stat.CriticalStrikeChancePlusPctWithTincturedWeapons).Value);
            Assert.Equal(1, scaled.Single(s => s.Stat == Stat.CullingStrikeWithTincturedWeapons).Value);

            Assert.Equal(0.35, Tinctures.DebuffInterval(def, new Dictionary<uint, int>
            {
                [Stat.LocalTinctureToxicityRatePlusPct] = 100,
            }, _ => 0), 6);
            Assert.True(double.IsPositiveInfinity(Tinctures.DebuffInterval(def, new Dictionary<uint, int>
            {
                [Stat.LocalCannotGenerateToxicityStacksOverTime] = 1,
            }, _ => 0)));
            Assert.Equal(4.0, Tinctures.CooldownSeconds(def, new Dictionary<uint, int>(),
                key => key == Stat.TinctureCooldownRecoveryPlusPct ? 100 : 0), 6);
            Assert.Equal(0.0, Tinctures.BurnPerSecond(2, 200, ignoredStacks: 2), 6);
            Assert.Equal(6.0, Tinctures.BurnPerSecond(5, 200, ignoredStacks: 2), 6);

            // Mightblood Ire Melee Splash activates only when tincture_applied != 0 and applies no less-damage penalty.
            Assert.Null(MeleeSplash.Settings(key => key == Stat.MeleeSplashWithTincturedWeapons ? 1 : 0));
            var splash = MeleeSplash.Settings(key => key switch
            {
                Stat.MeleeSplashWithTincturedWeapons => 1,
                Stat.TinctureApplied => 1,
                Stat.MeleeSplashAreaOfEffectPlusPct => 50,
                _ => 0,
            });
            Assert.NotNull(splash);
            Assert.Equal(1.0, splash.Value.More, 6);
            Assert.True(splash.Value.Radius > MeleeSplash.BaseRadius);

            // Weapon-only ignite chance/duration apply on main_hand and never leak to spell.
            int IgniteStat(uint key) => key switch
            {
                Stat.TinctureApplied => 1,
                Stat.ChanceToIgnitePctWithTincturedWeapons => 40,
                Stat.ElementalAilmentDurationPlusPctWithTincturedWeapons => 50,
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
        var critMultMod = items.Mods.All.First(m => m.Stats.Any(s => s.Id == Stat.CriticalStrikeMultiplierPlusWithTincturedWeapons));
        var cullMod = items.Mods.All.First(m => m.Stats.Any(s => s.Id == Stat.CullingStrikeWithTincturedWeapons));
        var baseTincture = ItemCatalog.Create(rosethorn, random: new Random(3));
        uint[] killStats = [Stat.LifeGainedOnKillingEnemiesWithTincturedWeapons, Stat.BaseManaGainedOnTincturedWeaponKill,
            Stat.FlaskChargesGrantedFromTincturedWeaponKillsPlusPct];
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

        var extraStats = new Dictionary<Player, Dictionary<uint, int>>();
        using var statsHook = GameEvents.Stats.Add("Game", sheet =>
        {
            if (extraStats.TryGetValue(sheet.Player, out var map))
                foreach (var (k, v) in map) sheet.Add(k, v);
        });
        void SetPlayerStat(Player p, uint key, int value)
        {
            if (!extraStats.TryGetValue(p, out var map)) extraStats[p] = map = [];
            map[key] = value;
        }

        Player MakeConn(string name, int level, out InventoryItem placedTincture, out InventoryItem placedSword)
        {
            names.Add(name);
            var conn = new Player(new(1, name, 200, 200, 100, 100, null, []), 1, 1)
            {
                Character = { Name = name },
                Connection = { Session = new("tincture-test", stream, ciphers) },
                LeagueModule = new(new GameServer.Core.Accounts.League("test", "Standard", false, false), new HashSet<string>(["Game"])),
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

        int StatVal(IReadOnlyDictionary<uint, int> stats, string name) =>
            Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;

        CharacterStats.Bonus BonusOf(Player conn)
        {
            var (stats, attributes) = Game.CharacterStatSources(conn);
            return Game.PassiveBonus(stats, Game.WeaponProfileFor(conn, activeSet: true), attributes);
        }

        try
        {
            // 1. Level gate, activation via UseFlask, weapon-only stat isolation, and unequip cleanup.
            var conn1 = MakeConn("TinctureStats_" + Guid.NewGuid().ToString("N"), level: 1, out var slot0, out var eqSword);
            await Game.UseFlask(conn1, 0);
            Assert.False(conn1.TinctureEffects.AnyActive);

            conn1.Pools = Game.PoolsFor(conn1, level: 60);
            var baselineStats = Game.CharacterStatSources(conn1).Stats;
            int baseAttackCrit = StatVal(baselineStats, "attack_critical_strike_chance_+%");
            var baseBonus = BonusOf(conn1);
            Assert.Equal(0, Game.Cull.Of(baselineStats, skill: null, critical: false, spell: false).Threshold);

            await Game.UseFlask(conn1, 0);
            Assert.True(conn1.TinctureEffects.IsActive(slot0.Id));
            Assert.False(Game.ItemsOf(conn1).Items(PlayerInventories.Flask).Single().Item.TinctureActive);

            var activeStats = Game.CharacterStatSources(conn1).Stats;
            var activeBonus = BonusOf(conn1);
            Assert.Equal(1, StatVal(activeStats, "using_tincture"));
            Assert.Equal(1, StatVal(activeStats, "tincture_applied"));
            Assert.Equal(1, StatVal(activeStats, "tincture_applied_to_main_hand"));
            Assert.True(StatVal(activeStats, "attack_critical_strike_chance_+%") > baseAttackCrit);
            Assert.Equal(baseBonus.CriticalStrikeMultiplier + 36, activeBonus.CriticalStrikeMultiplier); // 30 * 1.2 quality
            Assert.Equal(baseBonus.SpellCriticalStrikeMultiplier, activeBonus.SpellCriticalStrikeMultiplier);
            Assert.Equal(10, Game.Cull.Of(activeStats, skill: null, critical: false, spell: false).Threshold);
            Assert.Equal(0, Game.Cull.Of(activeStats, skill: null, critical: false, spell: true).Threshold);

            // A spell kill uses these unconditional stats even while a tinctured sword is equipped.
            foreach (string stat in new[] { "base_life_gained_on_enemy_death", "base_mana_gained_on_enemy_death",
                "charges_gained_+%", "flask_charges_gained_+%" })
                Assert.Equal(StatVal(baselineStats, stat), StatVal(activeStats, stat));
            var kill = Game.TinctureKillBonusFor(conn1, activeStats, AttackHand.Main);
            Assert.NotNull(kill);
            Assert.All(killStats, stat => Assert.True(activeStats.GetValueOrDefault(stat) > 0));
            Assert.Equal((activeStats.GetValueOrDefault(killStats[0]), activeStats.GetValueOrDefault(killStats[1]), activeStats.GetValueOrDefault(killStats[2])),
                (kill.Life, kill.Mana, kill.FlaskChargesPct));
            Assert.Null(Game.TinctureKillBonusFor(conn1, activeStats, AttackHand.Off));
            Assert.Equal(1 + kill.FlaskChargesPct / 100.0, FlaskCharges.KillCharges(MonsterRarity.Normal, items.Mods, kill.FlaskChargesPct));

            // Ranged weapon gets no weapon stats without Seasoned Hunter, and gets them with it; unarmed gets none.
            var inv1 = Game.ItemsOf(conn1);
            inv1.Remove(PlayerInventories.Weapon1, eqSword.Id);
            var eqBow = inv1.Add(PlayerInventories.Weapon1, 0, 0, bow);
            var bowStats = Game.CharacterStatSources(conn1).Stats;
            Assert.Equal(0, StatVal(bowStats, "tincture_applied"));
            Assert.Equal(baseAttackCrit, StatVal(bowStats, "attack_critical_strike_chance_+%"));
            conn1.Character.Passives = [Game.PassivesFor(conn1).FindById("AscendancyRaider19")!.GraphId];
            var bowWithMastery = Game.CharacterStatSources(conn1).Stats;
            Assert.Equal(1, StatVal(bowWithMastery, "tincture_applied"));
            Assert.True(StatVal(bowWithMastery, "attack_critical_strike_chance_+%") > baseAttackCrit);
            inv1.Remove(PlayerInventories.Weapon1, eqBow.Id);
            var unarmedStats = Game.CharacterStatSources(conn1).Stats;
            Assert.Equal(0, StatVal(unarmedStats, "tincture_applied"));
            Assert.Equal(baseAttackCrit, StatVal(unarmedStats, "attack_critical_strike_chance_+%"));

            // Unequipping the tincture immediately deactivates it and removes its effects.
            inv1.Remove(PlayerInventories.Flask, slot0.Id);
            await Game.ReconcileTincturesAfterFlaskBarChange(conn1);
            Assert.False(conn1.TinctureEffects.AnyEffective);
            Assert.Equal(0, StatVal(Game.CharacterStatSources(conn1).Stats, "using_tincture"));

            // 2. Mana Burn accumulation, on-kill removal, 0-mana auto-deactivation, and cooldown enforcement.
            var conn2 = MakeConn("TinctureBurn_" + Guid.NewGuid().ToString("N"), level: 60, out var slot2, out _);
            await Game.UseFlask(conn2, 0);
            Assert.True(conn2.TinctureEffects.IsActive(slot2.Id));
            double t2 = conn2.TinctureEffects.Of(slot2.Id)!.ActivatedAt;
            List<uint>? deactivated = null;
            var stats2 = Game.CharacterStatSources(conn2).Stats;
            Game.AdvanceTincturesLocked(conn2, now: t2 + 0.71, seconds: 0.71, stats2, out double manaDrain, out double lifeDrain, ref deactivated);
            Assert.Equal(1, conn2.TinctureEffects.BurnStacks);
            Assert.Equal(60, StatVal(Game.CharacterStatSources(conn2).Stats, "mana_degeneration_per_minute_%_per_tincture_toxicity"));
            Assert.True(manaDrain > 0);
            Assert.Equal(0, lifeDrain);

            // On-kill Mana Burn removal mastery removes 1 stack.
            SetPlayerStat(conn2, Stat.ChanceToRemove1TinctureToxicityOnKillPct, 100);
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
            await Game.UseFlask(conn2, 0);
            Assert.False(conn2.TinctureEffects.AnyActive);

            // 3. Manual toggle, Bloodsoaked Blade (life burn), Enduring Suffusion linger, and death cleanup.
            var conn3 = MakeConn("TinctureLife_" + Guid.NewGuid().ToString("N"), level: 60, out var slot3, out _);
            SetPlayerStat(conn3, Stat.KeystoneTincturesDrainLife, 1);
            SetPlayerStat(conn3, Stat.TinctureEffectsLingerForMsPerToxicityUpTo6Seconds, 500);
            await Game.UseFlask(conn3, 0);
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
            Assert.Equal(-2, PlayerDamageTaken.Flat(DamageType.Fire, attack: false, key => burning.GetValueOrDefault(key)));
            Assert.Equal(0, StatVal(burning, "fire_damage_taken_+%"));
            Assert.Equal(60, StatVal(Game.CharacterStatSources(conn3).Stats, "non_lethal_life_loss_%_per_minute_per_tincture_toxicity"));
            Assert.Equal(0, manaDrain);
            Assert.True(lifeDrain > 0);

            // Manual toggle off starts lingering for 1.0 s (2 stacks * 500 ms) while clearing active & burn state.
            await Game.UseFlask(conn3, 0);
            Assert.False(conn3.TinctureEffects.AnyActive);
            Assert.Empty(conn3.FlaskAutomation.Packets);
            Assert.True(conn3.TinctureEffects.HasLingering);
            Assert.Equal(1, StatVal(Game.CharacterStatSources(conn3).Stats, "has_lingering_tincture"));
            Assert.Equal(1, StatVal(Game.CharacterStatSources(conn3).Stats, "tincture_applied"));

            // Death clears lingering effects too, and a fresh Player starts with no active tinctures.
            Game.DeactivateAllTincturesOnDeathLocked(conn3, now: t3 + 1.5, stats3, ref deadIds);
            Assert.False(conn3.TinctureEffects.AnyEffective);
            Assert.Equal(0, StatVal(Game.CharacterStatSources(conn3).Stats, "tincture_applied"));

            // 4. Weapon-only DoT/PDR-ignore/low-life suffixes, unique on-hit hooks, Nature's Concoction, and live re-scaling.
            var conn4 = MakeConn("TinctureAdv_" + Guid.NewGuid().ToString("N"), level: 60, out var slot4, out _);
            var lifeFlask = ItemCatalog.Create(items.Find("Small Life Flask")!, random: new Random(9)) with { Charges = 0 };
            Game.ItemsOf(conn4).Add(PlayerInventories.Flask, 1, 0, lifeFlask);
            SetPlayerStat(conn4, Stat.TinctureEffectPlusPctIfUsedLifeFlaskRecently, 50);
            SetPlayerStat(conn4, Stat.FlasksAdjacentToActiveTincturesHaveEffectPlusPctIfHitWithWeaponRecently, 25);
            SetPlayerStat(conn4, Stat.FlasksAdjacentToActiveTincturesGainXChargesOnWeaponHit, 3);
            SetPlayerStat(conn4, Stat.HitsIgnoreEnemyMonsterPhysicalDamageReductionPctChanceWithTincturedWeapons, 100);
            SetPlayerStat(conn4, Stat.DamageOverTimeMultiplierPlusWithTincturedWeapons, 40);
            SetPlayerStat(conn4, Stat.ApplyXWitherStacksFor2SecondsWithTincturedWeapons, 2);
            SetPlayerStat(conn4, Stat.RefreshIgniteDurationOnTincturedWeaponHitPctChance, 100);
            SetPlayerStat(conn4, Stat.TincturedWeaponCoverInAshOnHitForXSecondsVsFullLifeEnemies, 4);

            await Game.UseFlask(conn4, 0);
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
            int Stats4(uint key) => stats4.GetValueOrDefault(key);
            var fireBase = DamagePacket.Of(DamageType.Fire, 100, 100);
            var attackAilments = AilmentPanelStats.Compute(new("main_hand", Spell: false, Melee: true), fireBase, new DamageConversion(), Stats4);
            var spellAilments = AilmentPanelStats.Compute(new("spell", Spell: true, Melee: false), fireBase, new DamageConversion(), Stats4);
            int atkIgnite = attackAilments.Single(r => r.Stat == Stat.MinimumIgniteDamageToDealPerMinuteFromNonCriticalMainHandDamage).Value;
            int splIgnite = spellAilments.Single(r => r.Stat == Stat.MinimumIgniteDamageToDealPerMinuteFromNonCriticalSpellDamage).Value;
            Assert.True(atkIgnite > splIgnite);

            // Ignore enemy physical damage reduction applies on attacks and not spells.
            var armored = new SpawnedMonster("armored", new(10, 10, 0), 0, 0, 1000)
            {
                EncounterStats = new Dictionary<uint, int> { [Stat.BaseAdditionalPhysicalDamageReductionPct] = 50 },
            };
            var physHit = DamagePacket.Of(DamageType.Physical, 100, 100);
            var resMods = Game.HitResistMods(stats4);
            Assert.Equal(100, Game.MonsterHitDamage(armored, physHit, critical: false, attack: true, now: t4 + 0.2, resMods).Damage);
            Assert.Equal(50, Game.MonsterHitDamage(armored, physHit, critical: false, attack: false, now: t4 + 0.2, resMods).Damage);

            // Low-life enemy damage suffix applies to weapon attacks against low-life targets.
            SetPlayerStat(conn4, Stat.TincturedWeaponDamagePlusPctVsEnemiesThatAreOnLowLife, 75);
            var lowLifeStats = Game.CharacterPlayerStats(conn4);
            var weapon = Game.SkillsFor(conn4).UnarmedFor(conn4.Character.ClassRow);
            conn4.Area.Monsters[99] = armored;
            var fullLifePrehit = Game.DefaultMeleePrehitProfileLocked(conn4, 99, armored, lowLifeStats, weapon, new DamageConversion(), null, t4 + 0.2, 100);
            armored.Life = 400; // <= 50% -> low life
            var lowLifePrehit = Game.DefaultMeleePrehitProfileLocked(conn4, 99, armored, lowLifeStats, weapon, new DamageConversion(), null, t4 + 0.2, 100);
            Assert.NotNull(fullLifePrehit);
            Assert.NotNull(lowLifePrehit);
            Assert.True(lowLifePrehit.Hit.MaxTotal > fullLifePrehit.Hit.MaxTotal);
            armored.Life = 1000;

            // On-weapon-hit hooks: Wither stacks, Ignite refresh, Covered in Ash (+20% fire taken), and Nature's Concoction.
            armored.Ignites.Add(t4, perSecond: 100, seconds: 4);
            double hitAt = t4 + 1.0;
            double fireDotBefore = Game.MonsterDotTaken(armored, DamageType.Fire, hitAt);
            Game.TinctureOnWeaponHitLocked(conn4, stats4, armored, targetLifeBefore: armored.MaxLife, now: hitAt, new Random(1));
            Assert.Equal(2, armored.Withered.Count);
            Assert.Equal(hitAt + 4.0, armored.Ignites.Until, 6);
            Assert.True(armored.CoveredInAshUntil >= hitAt + 4.0);
            Assert.True(Game.MonsterDotTaken(armored, DamageType.Fire, hitAt) > fireDotBefore);

            // Nature's Concoction queued adjacent charges for slot 1 (adjacent to slot 0) and adjacent flask effect applies.
            var queuedCharges = conn4.TinctureEffects.DrainPendingAdjacentCharges();
            Assert.NotNull(queuedCharges);
            Assert.Contains((Slot: (byte)1, Charges: 3), queuedCharges);
            conn4.RecentEvents.RecordHit(t4 + 0.9, critical: false);
            Assert.Equal(0, Game.AdjacentTinctureFlaskEffect(conn4, slot: 1, Stats4, t4 + 0.9));
            conn4.RecentEvents.Record(StatConditions.HitEnemyWithWeapon, t4 + 1.0);
            Assert.Equal(25, Game.AdjacentTinctureFlaskEffect(conn4, slot: 1, Stats4, t4 + 1.0));
            Assert.Equal(0, Game.AdjacentTinctureFlaskEffect(conn4, slot: 3, Stats4, t4 + 1.0));

            conn4.RecentEvents.RecordHit(t4 + 4.5, critical: false);
            Assert.Equal(0, Game.AdjacentTinctureFlaskEffect(conn4, slot: 1, Stats4, t4 + 5.1));

            SetPlayerStat(conn4, Stat.TinctureEffectsLingerForMsPerToxicityUpTo6Seconds, 500);
            conn4.TinctureEffects.AddBurnStacks(2);
            await Game.UseFlask(conn4, 0);
            Assert.True(conn4.TinctureEffects.HasLingering);
            Game.ItemsOf(conn4).Remove(PlayerInventories.Flask, slot4.Id);
            await Game.ReconcileTincturesAfterFlaskBarChange(conn4);
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
