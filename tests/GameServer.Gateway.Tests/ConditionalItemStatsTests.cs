using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;


/// <summary>
/// Conditional item mods, issue #504, against the real 3.29.3b tables. <see cref="StatConditions"/> decides whether a
/// stat id's condition holds; a condition the server cannot answer stays unmodelled and keeps its previous behaviour.
/// A condition that does hold moves the value onto the stat's plain name, which is what the pools and panels already
/// read — without that the condition would be decided correctly and the mod would still do nothing.
/// </summary>
public class ConditionalItemStatsTests
{
    static ModCatalog.Mod Mod(string id) => Program.Items.Mods.All.Single(m => m.Id == id);

    /// <summary>An item carrying one explicit roll of the named mod, at the given value.</summary>
    static Item WithMod(string id, int value)
    {
        var mod = Mod(id).Roll(ModSection.Explicit, new Random(1));
        return new Item(1, ["Mods"], 0)
        {
            Rarity = ItemRarity.Unique,
            Mods = [mod with { Values = [.. mod.Values.Select(_ => value)] }],
        };
    }

    [Fact]
    public void Recent_events_expire_at_four_seconds_and_cache_inputs_track_condition_changes()
    {
        var events = new RecentEvents();
        events.Record(StatConditions.Crit, 10);
        Assert.Contains(StatConditions.Crit, events.Snapshot(13.999));
        Assert.Equal(14, events.NextExpiry(10));
        Assert.DoesNotContain(StatConditions.Crit, events.Snapshot(14));
        Assert.Null(events.NextExpiry(14));
        events.Record(StatConditions.BeenHit, 10);
        Assert.Contains(StatConditions.BeenHit, events.Snapshot(13.999));
        Assert.DoesNotContain(StatConditions.BeenHit, events.Snapshot(14));

        var baseline = CacheKey(new HashSet<string> { "grace" }, new HashSet<string> { StatConditions.Crit }, 14, 600);
        Assert.False(baseline.Matches(CacheKey(new HashSet<string>(), new HashSet<string> { StatConditions.Crit }, 14, 600)));
        Assert.False(baseline.Matches(CacheKey(new HashSet<string> { "grace" }, new HashSet<string>(), null, 600)));
        Assert.False(baseline.Matches(CacheKey(new HashSet<string> { "grace" }, new HashSet<string> { StatConditions.Crit }, 14, 599)));

        static global::Game.StatSourcesKey CacheKey(IReadOnlySet<string> auras, IReadOnlySet<string> recent, double? expiry, int strength) =>
            new([], new Dictionary<ushort, ushort>(), [], new Dictionary<uint, int>(), 1, false, 1, null, [], 0, [],
                auras, recent, expiry, strength, 0, 0, 1, 1);
    }

    [Fact]
    public void A_stats_condition_is_decided_from_the_state_the_server_keeps()
    {
        // A stat without a condition always counts, even with everything known.
        Assert.Equal(StatConditions.State.None, StatConditions.Of("maximum_life", StatConditions.Subject.Unknown));
        Assert.True(StatConditions.Counts(StatConditions.State.None));

        // The weapon and shield conditions follow the current weapons.
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("attack_speed_while_dual_wielding_+%", new StatConditions.Subject(DualWield: true)));
        Assert.Equal(StatConditions.State.Fails,
            StatConditions.Of("attack_speed_while_dual_wielding_+%", new StatConditions.Subject(DualWield: false)));
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("critical_strike_chance_while_wielding_shield_+%", new StatConditions.Subject(Shield: true)));

        // An aura condition follows the auras running now; "X_and_Y" needs both.
        var grace = new HashSet<string> { "grace" };
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("chance_to_evade_attacks_%_while_affected_by_grace", new StatConditions.Subject(Auras: grace)));
        Assert.Equal(StatConditions.State.Fails,
            StatConditions.Of("x_while_affected_by_grace_and_determination", new StatConditions.Subject(Auras: grace)));

        // A "recently" condition follows the events the subject reports for the last four seconds, and reads a "not X"
        // id as the absence of X. Its key is the event, not the verb the id happens to spell it with.
        Assert.True(StatConditions.TryRecentEvent("damage_+%_if_you_have_crit_recently", out string crit, out bool negated));
        Assert.Equal(StatConditions.Crit, crit);
        Assert.False(negated);
        var hitAndCrit = new HashSet<string> { StatConditions.HitEnemy, StatConditions.Crit };
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("damage_+%_if_you_have_crit_recently", new StatConditions.Subject(Recent: hitAndCrit)));
        Assert.Equal(StatConditions.State.Fails,
            StatConditions.Of("spell_damage_+%_if_you_have_crit_recently",
                new StatConditions.Subject(Recent: new HashSet<string> { StatConditions.HitEnemy })));
        // A bare "hit" is a hit received. The explicit enemy-hit spellings still mean a hit dealt, so the two directions
        // cannot satisfy each other's recently conditions.
        Assert.True(StatConditions.TryRecentEvent("evasion_+%_if_hit_recently", out string receivedHit, out bool receivedNegated));
        Assert.Equal(StatConditions.BeenHit, receivedHit);
        Assert.False(receivedNegated);
        Assert.True(StatConditions.TryRecentEvent("damage_+%_if_hit_an_enemy_recently", out string dealtHit, out _));
        Assert.Equal(StatConditions.HitEnemy, dealtHit);
        Assert.True(StatConditions.TryRecentEvent("damage_+%_if_you_have_not_hit_an_enemy_recently",
            out string notDealtHit, out bool notDealtHitNegated));
        Assert.Equal(StatConditions.HitEnemy, notDealtHit);
        Assert.True(notDealtHitNegated);
        Assert.True(StatConditions.TryRecentEvent("damage_+%_if_enemy_hit_recently", out string otherDealtHit, out _));
        Assert.Equal(StatConditions.HitEnemy, otherDealtHit);
        Assert.Equal(StatConditions.State.Fails,
            StatConditions.Of("evasion_+%_if_hit_recently",
                new StatConditions.Subject(Recent: new HashSet<string> { StatConditions.HitEnemy })));
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("evasion_+%_if_hit_recently",
                new StatConditions.Subject(Recent: new HashSet<string> { StatConditions.BeenHit })));
        Assert.True(StatConditions.TryRecentEvent("damage_taken_+%_if_not_hit_recently_final",
            out string notReceivedHit, out bool notReceivedNegated));
        Assert.Equal(StatConditions.BeenHit, notReceivedHit);
        Assert.True(notReceivedNegated);
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("damage_taken_+%_if_not_hit_recently_final",
                new StatConditions.Subject(Recent: new HashSet<string>())));
        Assert.Equal(StatConditions.State.Fails,
            StatConditions.Of("damage_taken_+%_if_not_hit_recently_final",
                new StatConditions.Subject(Recent: new HashSet<string> { StatConditions.BeenHit })));
        // "if enemy not killed recently" holds exactly when the kill event is absent.
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("attack_speed_+%_if_enemy_not_killed_recently",
                new StatConditions.Subject(Recent: new HashSet<string> { StatConditions.HitEnemy })));
        Assert.Equal(StatConditions.State.Fails,
            StatConditions.Of("attack_speed_+%_if_enemy_not_killed_recently",
                new StatConditions.Subject(Recent: new HashSet<string> { StatConditions.KilledEnemy })));

        // An "if you have at least N X" condition compares the attribute the subject reads.
        Assert.True(StatConditions.TryThreshold("attack_speed_+%_if_you_have_at_least_600_strength",
            out string attribute, out int needed));
        Assert.Equal("strength", attribute);
        Assert.Equal(600, needed);
        Assert.Equal(StatConditions.State.Holds,
            StatConditions.Of("attack_speed_+%_if_you_have_at_least_600_strength",
                new StatConditions.Subject(Value: n => n == "strength" ? 650 : 0)));
        Assert.Equal(StatConditions.State.Fails,
            StatConditions.Of("attack_speed_+%_if_you_have_at_least_600_strength",
                new StatConditions.Subject(Value: n => n == "strength" ? 599 : 0)));

        // Per-charge ids belong to ChargeEffects, which expands them against the counts already: this layer leaves them
        // alone so the same value is never counted twice, whatever the subject knows.
        Assert.True(StatConditions.NamesACharge("attack_speed_+%_per_frenzy_charge"));
        Assert.True(StatConditions.NamesACharge("add_power_charge_on_kill"));
        Assert.Equal(StatConditions.State.NotModelled,
            StatConditions.Of("attack_speed_+%_per_frenzy_charge", StatConditions.Subject.Unknown));
        Assert.Null(StatConditions.Fold("attack_speed_+%_per_frenzy_charge", _ => true));

        // A condition whose subject the server does not model (a shrine, elusive, a league buff) stays unmodelled, so
        // its stat keeps counting rather than being silently dropped; the same holds with no subject at all.
        foreach (var id in new[]
                 {
                     "movement_speed_+%_while_affected_by_elusive",
                     "x_while_you_have_a_shrine_buff",
                     "attack_speed_while_dual_wielding_+%",
                     "damage_+%_if_you_have_crit_recently",
                 })
        {
            Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of(id, StatConditions.Subject.Unknown));
            Assert.True(StatConditions.Counts(StatConditions.State.NotModelled));
        }
    }

    [Fact]
    public void The_shield_condition_is_answered_in_every_spelling_the_tables_use()
    {
        // The 3.29.3b tables spell the shield condition two ways: "_while_holding_a_shield" (18 ids) and
        // "_while_holding_shield" (29 ids, the jewel/shield mods such as Battlemage's). Both name the same subject, so
        // both must be read from Subject.Shield — a spelling that is missed falls through to NotModelled and the mod is
        // counted unconditionally, i.e. a shield-conditional jewel would grant its value with no shield equipped.
        foreach (string id in new[]
                 {
                     "spell_damage_+%_while_holding_shield",
                     "attack_speed_+%_while_holding_shield",
                     "maximum_life_+%_while_holding_shield",
                     "spell_critical_strike_chance_+%_while_holding_shield",
                     "attack_damage_+%_while_holding_a_shield",
                     "critical_strike_chance_while_wielding_shield_+%",
                 })
        {
            Assert.Equal(StatConditions.State.Holds, StatConditions.Of(id, new StatConditions.Subject(Shield: true)));
            Assert.Equal(StatConditions.State.Fails, StatConditions.Of(id, new StatConditions.Subject(Shield: false)));
            // No subject at all still keeps the old behaviour: unmodelled, so the stat is left as it was.
            Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of(id, StatConditions.Subject.Unknown));
        }

        // The plain name the two spellings both fold onto is the stat the pools read.
        Assert.Equal("spell_damage_+%", StatConditions.PlainOf("spell_damage_+%_while_holding_shield"));
        Assert.Equal("attack_damage_+%", StatConditions.PlainOf("attack_damage_+%_while_holding_a_shield"));
    }

    [Fact]
    public void A_shield_condition_that_holds_counts_on_its_plain_stat_and_not_without_a_shield()
    {
        var exists = (string n) => Program.Stats.Has(n);

        // Battlemage's (ShieldSpellDamageJewel): spell_damage_+%_while_holding_shield. The plain name is a real stat the
        // damage pipeline reads, so a shield folds it there; with no shield nothing counts.
        const string jewel = "ShieldSpellDamageJewel";
        Assert.Contains(Mod(jewel).Stats, s => s.Id == "spell_damage_+%_while_holding_shield");
        var item = WithMod(jewel, 20);
        var on = ItemModStats.Sum([item], Program.Items.Mods,
            subject: new StatConditions.Subject(Shield: true), statExists: exists);
        Assert.Equal(20, on.GetValueOrDefault("spell_damage_+%"));
        Assert.False(on.ContainsKey("spell_damage_+%_while_holding_shield"));
        var off = ItemModStats.Sum([item], Program.Items.Mods,
            subject: new StatConditions.Subject(Shield: false), statExists: exists);
        Assert.Equal(0, off.GetValueOrDefault("spell_damage_+%"));
        Assert.False(off.ContainsKey("spell_damage_+%_while_holding_shield"));
        // Without a subject (every pre-existing caller) the mod keeps its old behaviour: counted on its own id.
        Assert.Equal(20, ItemModStats.Sum([item], Program.Items.Mods)
            .GetValueOrDefault("spell_damage_+%_while_holding_shield"));

        // A shield id that already has a consumer of its own is gated but never folded, or the consumer would double
        // count it. Charging (ShieldAttackSpeedJewel): attack_speed_+%_while_holding_shield, which Progression's
        // attack-speed block already reads under a shield. With a shield it counts only on its own id.
        var dedicated = WithMod("ShieldAttackSpeedJewel", 5);
        var dedicatedOn = ItemModStats.Sum([dedicated], Program.Items.Mods,
            subject: new StatConditions.Subject(Shield: true), statExists: exists);
        Assert.Equal(5, dedicatedOn.GetValueOrDefault("attack_speed_+%_while_holding_shield"));
        Assert.Equal(0, dedicatedOn.GetValueOrDefault("attack_speed_+%"));
        Assert.Equal(0, ItemModStats.Sum([dedicated], Program.Items.Mods,
            subject: new StatConditions.Subject(Shield: false), statExists: exists)
            .GetValueOrDefault("attack_speed_+%_while_holding_shield"));
    }

    [Fact]
    public void A_held_condition_counts_on_its_plain_stat_and_nothing_changes_without_a_subject()
    {
        // A real jewel roll whose plain name the damage pipeline reads.
        const string id = "SynthesisImplicitSpellDamageWithDualWieldJewel1";
        Assert.Contains(Mod(id).Stats, s => s.Id == "spell_damage_+%_while_dual_wielding");
        var item = WithMod(id, 30);
        var exists = (string n) => Program.Stats.Has(n);

        // Dual wielding: the value lands on the plain stat; not dual wielding: nothing at all.
        var on = ItemModStats.Sum([item], Program.Items.Mods,
            subject: new StatConditions.Subject(DualWield: true), statExists: exists);
        Assert.Equal(30, on.GetValueOrDefault("spell_damage_+%"));
        Assert.False(on.ContainsKey("spell_damage_+%_while_dual_wielding"));
        var off = ItemModStats.Sum([item], Program.Items.Mods,
            subject: new StatConditions.Subject(DualWield: false), statExists: exists);
        Assert.Equal(0, off.GetValueOrDefault("spell_damage_+%"));

        // A "recently" roll does the same: the plain name while the event is inside the window, nothing outside it.
        const string recentId = "IncreasedDamageIfFrozenRecentlyUnique__1";
        Assert.Equal("damage_+%", StatConditions.PlainOf("damage_+%_if_you_have_frozen_enemy_recently"));
        Assert.Contains(Mod(recentId).Stats, s => s.Id == "damage_+%_if_you_have_frozen_enemy_recently");
        var recentItem = WithMod(recentId, 20);
        var froze = StatConditions.Of("damage_+%_if_you_have_frozen_enemy_recently",
            new StatConditions.Subject(Recent: new HashSet<string> { StatConditions.FrozeEnemy }));
        Assert.Equal(StatConditions.State.Holds, froze);

        // Kintsugi uses "evasion" in its stat id, but Progression's real evasion consumer reads "evasion_rating".
        const string kintsugi = "IncreasedEvasionIfHitRecentlyUnique___1";
        Assert.Contains(Mod(kintsugi).Stats, s => s.Id == "evasion_+%_if_hit_recently");
        Assert.Equal("evasion_rating_+%", StatConditions.Fold("evasion_+%_if_hit_recently", exists));
        var kintsugiItem = WithMod(kintsugi, 100);
        var kintsugiHit = ItemModStats.Sum([kintsugiItem], Program.Items.Mods,
            subject: new StatConditions.Subject(Recent: new HashSet<string> { StatConditions.BeenHit }),
            statExists: exists);
        Assert.Equal(100, kintsugiHit.GetValueOrDefault("evasion_rating_+%"));
        Assert.False(kintsugiHit.ContainsKey("evasion_+%_if_hit_recently"));
        var enemyHitOnly = ItemModStats.Sum([kintsugiItem], Program.Items.Mods,
            subject: new StatConditions.Subject(Recent: new HashSet<string> { StatConditions.HitEnemy }),
            statExists: exists);
        Assert.Equal(0, enemyHitOnly.GetValueOrDefault("evasion_rating_+%"));

        // The synthetic command uses the same accepted-incoming-hit commit as a monster contact. A failed transfer
        // invalidates retired work without forgetting history; an actual lifecycle clear removes it.
        string path = Path.Combine(Path.GetTempPath(), $"localexile-conditional-item-stats-{Guid.NewGuid():N}.db");
        Saves.Open(path);
        try
        {
            var conn = new ClientState(new(1, "received-hit", 300, 300, 100, 100, null, []), 1, 1)
            {
                Pools = new(1, 1000, 100, 2, 0), PlayerLife = 900,
            };
            Assert.True(CommandsRules.TryDevEnemyHit(conn, GameServer.Core.World.DamageType.Physical, 1,
                block: false, spell: false, suppress: false, out _));
            double now = conn.Clock.Elapsed.TotalSeconds;
            Assert.Equal(1, conn.RecentEnemyHits.Count(now));
            Assert.True(conn.RecentEvents.Happened(StatConditions.BeenHit, now));
            conn.InvalidateIncomingHits(clear: false);
            Assert.Equal(1, conn.RecentEnemyHits.Count(now));
            Assert.True(conn.RecentEvents.Happened(StatConditions.BeenHit, now));
            conn.InvalidateIncomingHits();
            Assert.Equal(0, conn.RecentEnemyHits.Count(now));
            Assert.False(conn.RecentEvents.Happened(StatConditions.BeenHit, now));
        }
        finally
        {
            TestEnvironment.ReleaseSaves(path);
        }

        // A stat with a consumer of its own is never folded, or its mod would count twice.
        var dedicated = WithMod("BlockWhileDualWieldingUniqueDagger3", 10);
        Assert.Equal(10, ItemModStats.Sum([dedicated], Program.Items.Mods,
            subject: new StatConditions.Subject(DualWield: true), statExists: exists)
            .GetValueOrDefault("block_while_dual_wielding_%"));

        // No subject and no catalog, as every pre-existing caller passes: nothing changes at all.
        Assert.Equal(30, ItemModStats.Sum([item], Program.Items.Mods)
            .GetValueOrDefault("spell_damage_+%_while_dual_wielding"));
        Assert.Equal(10, ItemModStats.Sum([dedicated], Program.Items.Mods)
            .GetValueOrDefault("block_while_dual_wielding_%"));

        // An unconditional mod is untouched by any subject.
        var plain = WithMod(Program.Items.Mods.All.First(m => m.Stats.Count == 1 && m.Stats[0].Id == "base_maximum_life").Id, 42);
        foreach (var subject in new[]
                 {
                     StatConditions.Subject.Unknown,
                     new StatConditions.Subject(DualWield: true),
                     new StatConditions.Subject(DualWield: false, Shield: true),
                 })
            Assert.Equal(42, ItemModStats.Sum([plain], Program.Items.Mods, subject: subject)
                .GetValueOrDefault("base_maximum_life"));
    }
}
