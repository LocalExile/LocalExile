using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Explicit_curse_cast_histories_reach_actual_item_consumers(ClientState conn, Func<string, byte, InventoryItem> wear)
    {
        var uniques = new UniqueCatalog(Program.Items);
        var definition = uniques.Find("The Balance of Terror")!;
        var group = Assert.Single(definition.RollGroups!); Assert.Equal(2, group.Count);
        var rows = Program.Items.Mods.All.Where(m => m.Stats.Any(s => StatConditions.TryRecentCast(s.Id, out _, out _))).ToArray();
        Assert.Equal(18, rows.Length);
        Item? immuneSource = null;
        void Record(string effect, double at)
        {
            bool running = conn.Clock.IsRunning;
            try { if (!running) conn.Clock.Start(); Game.RecordCompletedCurseCast(conn, effect, at); }
            finally { if (!running) conn.Clock.Stop(); }
        }
        foreach (var row in rows)
        {
            var stat = Assert.Single(row.Stats);
            Assert.True(StatConditions.TryRecentCast(stat.Id, out string token, out int duration)); Assert.Equal(10, duration);
            Assert.Contains(row.Id, group.Options);
            // Both chosen options are in the actual catalogue's two-option roll group.
            var source = uniques.Create(definition with { RollGroups = [group with
                { Options = [row.Id, group.Options.First(id => id != row.Id)] }] }, new Random(504))!;
            int value = ItemModStats.Sum([source], Program.Items.Mods).GetValueOrDefault(stat.Id); Assert.True(value > 0);
            string effect = token == "dispair" ? "Despair" : string.Concat(token.Split('_').Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
            conn.RecentEvents.Clear();
            Record(effect, 100);
            string eventKey = StatConditions.CastEventKey(token, 10);
            Assert.Contains(eventKey, Game.TrackedItemRecentEvents);
            Assert.True(conn.RecentEvents.Happened(eventKey, 104.001), $"{effect}: current={Game.CurrentGameWork}, closed={conn.Closed}, leaving={conn.LeavingArea}, pending={conn.PendingTransfer is not null}, life={conn.PlayerLife}, paused={conn.Paused}");
            Assert.True(conn.RecentEvents.Happened(eventKey, 109.999));
            Assert.False(conn.RecentEvents.Happened(eventKey, 110));
            Assert.Equal(110, conn.RecentEvents.NextExpiry(109.999));
            Dictionary<string, int> At(double now) => ItemModStats.Sum([source], Program.Items.Mods,
                subject: new(Recent: conn.RecentEvents.Snapshot(now), KnownRecent: Game.TrackedItemRecentEvents), statExists: Program.Stats.Has);
            Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of(stat.Id, StatConditions.Subject.Unknown));
            var active = At(109.999); var off = At(110);
            int Get(string name) => active.GetValueOrDefault(name);
            foreach (string target in StatConditions.FoldAll(stat.Id, Program.Stats.Has))
            {
                Assert.Equal(StatConditions.ValueForFold(stat.Id, value), active.GetValueOrDefault(target));
                Assert.Equal(0, off.GetValueOrDefault(target));
            }
            switch (StatConditions.PlainOf(stat.Id))
            {
                case "chance_to_deal_double_damage_+%": Assert.Equal(value, GamblerModifiers.From(Get).DoubleDamageChance); break;
                case "critical_strike_multiplier_+%":
                    Assert.Equal(value, Game.PassiveBonus(active.Where(p => Program.Stats.Key(p.Key) is not null)
                        .ToDictionary(p => Program.Stats.Key(p.Key)!.Value, p => p.Value), default, default).CriticalStrikeMultiplier);
                    break;
                case "cooldown_recovery_+%":
                    var player = PanelStats.Compute(Get).Where(p => Program.Stats.Key(p.Stat) is not null)
                        .ToDictionary(p => Program.Stats.Key(p.Stat)!.Value, p => p.Value);
                    int frostblink = Program.Skills.EffectRow("Frostblink");
                    var plan = SingleUseCooldown.ForSkill(Program.Skills, Program.Stats, frostblink, 1, player,
                        Program.Skills.EffectStats(frostblink, 1).ToDictionary(p => p.Item1, p => p.Item2));
                    Assert.NotNull(plan); Assert.Equal(value, plan.Value.RecoveryPercent);
                    break;
                case "action_speed_cannot_be_slowed_below_base":
                case "unaffected_by_freeze":
                    var frozen = new PlayerElementalAilments(); frozen.Inflict(PlayerElementalAilments.Kind.Freeze, 100, 2, 100, Get, new Random(1));
                    frozen.Reconcile(Get); Assert.True(frozen.Snapshot(100).Freeze); Assert.Equal(1, frozen.ActionFactor(100));
                    Assert.Equal(102, frozen.FreezeStatusUntil(100));
                    if (Get("unaffected_by_freeze") != 0) Assert.Equal(100, frozen.FreezeUntil(100));
                    frozen.Reconcile(_ => 0); Assert.True(frozen.Snapshot(100).Freeze); Assert.Equal(0, frozen.ActionFactor(100));
                    Assert.Equal(102, frozen.FreezeUntil(100));
                    break;
                case "unaffected_by_shock":
                    var shocked = new PlayerElementalAilments(); shocked.Inflict(PlayerElementalAilments.Kind.Shock, 10, 2, 100, Get, new Random(1));
                    shocked.Reconcile(Get); Assert.Equal(10, shocked.Snapshot(100).Shock); Assert.Equal(1, shocked.DamageTakenFactor(100));
                    shocked.Reconcile(_ => 0); Assert.Equal(1.1, shocked.DamageTakenFactor(100), 8);
                    break;
                case "unaffected_by_ignite":
                    var ignited = new PlayerElementalAilments(); ignited.Inflict(PlayerElementalAilments.Kind.Ignite, 100, 2, 100, Get, new Random(1));
                    ignited.Reconcile(Get); Assert.True(ignited.Snapshot(100).Ignite);
                    Assert.Equal(0, ignited.Advance(100, 101, Get, 0, 75, false));
                    ignited.Reconcile(_ => 0); Assert.Equal(50, ignited.Advance(101, 101.5, _ => 0, 0, 75, false));
                    break;
                case "unaffected_by_bleed":
                    var bleed = new IncomingDamagingAilments(); bleed.ApplyBleed(100, 100, 2);
                    double nonChaos = 0, chaos = 0;
                    Assert.Equal(0, bleed.Advance(100, 101, new(100, 1000, 1000, 0, 0), new(), Get,
                        0, 75, 0, 75, false, false, false, (_, _) => 0, ref nonChaos, ref chaos));
                    Assert.True(bleed.Bleeding(101));
                    Assert.Equal(50, bleed.Advance(101, 101.5, new(100, 1000, 1000, 0, 0), new(), _ => 0,
                        0, 75, 0, 75, false, false, false, (_, _) => 0, ref nonChaos, ref chaos));
                    break;
                case "take_no_extra_damage_from_critical_strikes": Assert.Equal(MonsterModEffects.CriticalMultiplier, Game.SelfCriticalReduction(Get)); break;
                case "immune_to_curses":
                    immuneSource = source;
                    var raw = active.Where(p => Program.Stats.Key(p.Key) is not null).ToDictionary(p => Program.Stats.Key(p.Key)!.Value, p => p.Value);
                    raw[Program.Stats.Key("self_cursed_with_level_x_vulnerability")!.Value] = 20;
                    Assert.Empty(Game.SelfVulnerabilityStats(conn, raw)); raw.Remove(Program.Stats.Key("immune_to_curses")!.Value);
                    Assert.NotEmpty(Game.SelfVulnerabilityStats(conn, raw)); break;
                case "intimidate_enemies_on_hit":
                    foreach (bool spell in new[] { false, true }) Assert.True(Game.HitDebuffRolls(Get, spell, false, new PhasingRoll(99)).Intimidate);
                    break;
                case "inflict_withered_for_2_seconds_on_hit":
                    Assert.Equal(2, Game.WitherRoll(Get, new PhasingRoll(99)));
                    var target = new SpawnedMonster("cast-wither", new(1, 1, 1), 1, 1, 1000);
                    Game.WitherOnHitLocked(target, Get, 100, new PhasingRoll(99));
                    Assert.Equal(6, Game.WitheredTaken(target, 101.999)); Assert.Equal(0, Game.WitheredTaken(target, 102)); break;
                case "inflict_fire_exposure": Assert.Equal(-10, Game.ExposureRoll(Get, DamageType.Fire, false, new PhasingRoll(99))); break;
                case "inflict_cold_exposure": Assert.Equal(-10, Game.ExposureRoll(Get, DamageType.Cold, false, new PhasingRoll(99))); break;
                case "inflict_lightning_exposure": Assert.Equal(-10, Game.ExposureRoll(Get, DamageType.Lightning, false, new PhasingRoll(99))); break;
                default:
                    Assert.Contains(StatConditions.PlainOf(stat.Id), new[]
                    {
                        "gain_+%_physical_damage_as_random_element", "immune_to_exposure", "immune_to_reflect_damage",
                    });
                    break;
            }
        }
        conn.RecentEvents.Clear();
        Assert.NotNull(immuneSource);
        var inventory = Game.ItemsOf(conn);
        var saved = inventory.EquipmentSnapshot().Where(e => e.Row == PlayerInventories.Belt).ToArray();
        var belt = wear("Coward's Legacy", PlayerInventories.Belt);
        ushort node = Program.Passives.JewelNodeForSlot(0)!.Value; bool allocated = conn.Passives.Contains(node); conn.Passives.Add(node);
        var jewel = inventory.Add(PlayerInventories.PassiveJewels, 0, 0, immuneSource!);
        conn.PlayerLife = conn.PlayerMaxLife;
        int Cursed() => Game.CharacterPlayerStats(conn).GetValueOrDefault(Program.Stats.Key("cursed_by_vulnerability")!.Value);
        Assert.Equal(1, Cursed());
        var captured = Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot(), null, null,
            () => Record("Despair", conn.Clock.Elapsed.TotalSeconds));
        Assert.Equal(0, captured.Sources.GetValueOrDefault(Program.Stats.Key("immune_to_curses")!.Value));
        Assert.True(conn.StatSourcesCache!.Key.Flags!["cursed_by_vulnerability"]);
        Assert.DoesNotContain(StatConditions.CastEventKey("dispair", 10), conn.StatSourcesCache!.Key.Recent!);
        Assert.Equal(0, Cursed()); // The next snapshot observes the completed cast, never a mixed prior state.
        Record("Despair", conn.Clock.Elapsed.TotalSeconds);
        Assert.Equal(0, Cursed()); Assert.False(conn.StatSourcesCache!.Key.Flags!["_while_on_low_life"]);
        conn.Passives.Remove(node); Assert.Equal(1, Cursed());
        conn.Passives.Add(node); Assert.Equal(0, Cursed());
        inventory.Remove(PlayerInventories.PassiveJewels, jewel.Id); Assert.Equal(1, Cursed());
        Assert.True(conn.RecentEvents.Happened(StatConditions.CastEventKey("dispair", 10), conn.Clock.Elapsed.TotalSeconds));
        inventory.Remove(PlayerInventories.Belt, belt.Id);
        foreach (var (slot, item) in saved) inventory.Add(slot, item.X, item.Y, item.Item);
        if (!allocated) conn.Passives.Remove(node);
        conn.RecentEvents.Clear(); conn.Pools = Game.PoolsFor(conn, 100);
        Record("Vulnerability", 100); Record("Vulnerability", 105);
        Assert.True(conn.RecentEvents.Happened(StatConditions.CastEventKey("vulnerability", 10), 114.999));
        Assert.False(conn.RecentEvents.Happened(StatConditions.CastEventKey("vulnerability", 10), 115));
        conn.RecentEvents.Clear();
        if (conn.Paused)
        {
            Game.RecordCompletedCurseCast(conn, "Vulnerability", 100);
            Assert.Empty(conn.RecentEvents.Snapshot(100));
        }
        Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of("damage_+%_if_cast_unknown_in_past_10_seconds",
            new(Recent: new HashSet<string>(), KnownRecent: Game.TrackedItemRecentEvents)));
    }
}
