using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

static class ConditionalOperatorCoreCases
{
    public static void Compound_operators_fold_only_supplied_live_subjects_and_keep_source_local_values()
    {
        var values = new Dictionary<string, int> { ["strength"] = 100, ["dexterity"] = 99,
            ["life"] = 1000, ["mana"] = 950, ["equipped_corrupted"] = 2, ["equipped_total"] = 2 };
        var flags = new Dictionary<string, bool> { ["_while_on_full_life"] = true };
        var subject = new StatConditions.Subject(Value: key => values.TryGetValue(key, out int n) ? n : null, Flags: flags);
        const string compound = "damage_+%_while_on_full_life_if_you_have_at_least_100_strength";
        Assert.Equal(StatConditions.State.Holds, StatConditions.Of(compound, subject));
        Assert.Equal("damage_+%", Assert.Single(StatConditions.FoldAll(compound, n => n == "damage_+%", subject)));
        values["strength"] = 99;
        Assert.Equal(StatConditions.State.Fails, StatConditions.Of(compound, subject));
        values["strength"] = 100;
        flags["_while_on_full_life"] = false;
        Assert.Equal(StatConditions.State.Fails, StatConditions.Of(compound, subject));
        Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of(compound, default));
        Assert.True(StatConditions.Counts(StatConditions.State.NotModelled));
        Assert.Equal(StatConditions.State.Holds, StatConditions.Of("damage_+%_while_max_life_mana_within_50", subject));
        values["mana"] = 949;
        Assert.Equal(StatConditions.State.Fails, StatConditions.Of("damage_+%_while_max_life_mana_within_50", subject));
        Assert.Equal(StatConditions.State.Holds, StatConditions.Of("damage_+%_if_strength_higher_than_dexterity", subject));
        Assert.Equal(StatConditions.State.Holds, StatConditions.Of("damage_+%_with_all_corrupted_equipped_items", subject));
        values["equipped_corrupted"] = 1;
        Assert.Equal(StatConditions.State.Fails, StatConditions.Of("damage_+%_with_all_corrupted_equipped_items", subject));

        var catalog = Program.Items;
        var ring = new UniqueCatalog(catalog).Create("Mark of the Shaper", new Random(1))!;
        var sourceSubject = new StatConditions.Subject(ItemValue: (_, key) => key == "other_ring_is_elder_item" ? 1 : null);
        var active = ItemModStats.Sum([ring], catalog.Mods, subject: sourceSubject, statExists: n => Program.Stats.Key(n) is not null);
        Assert.True(active.GetValueOrDefault(Stat.SpellDamagePlusPct) > 0);
        var absent = ItemModStats.Sum([ring], catalog.Mods,
            subject: sourceSubject with { ItemValue = (_, key) => key == "other_ring_is_elder_item" ? 0 : null },
            statExists: n => Program.Stats.Key(n) is not null);
        Assert.Equal(0, absent.GetValueOrDefault(Stat.SpellDamagePlusPct));

        foreach (string overleech in new[] { "energy_shield_leech_does_not_stop_on_full_energy_shield",
            "energy_shield_leech_from_attacks_does_not_stop_on_full_energy_shield" })
        {
            var esFlags = new Dictionary<string, bool> { ["_on_full_energy_shield"] = false };
            var belowFull = new StatConditions.Subject(Flags: esFlags);
            Assert.Null(StatConditions.PlainOf(overleech));
            Assert.Equal(StatConditions.State.None, StatConditions.Of(overleech, belowFull));
            Assert.Equal(StatConditions.State.Fails,
                StatConditions.Of("damage_+%_on_full_energy_shield", belowFull));
            int Get(uint id) => Program.Stats.Name(id) is var name && name == overleech && StatConditions.Counts(StatConditions.Of(name, belowFull)) ? 1 : 0;
            var leech = new LeechSession();
            leech.Configure(0, LeechLimits.Resolve(Get, 1000, 1000, 1000), Get);
            leech.Hit(0, (0, 0, 100), false, false, false, attack: true);
            leech.ClearFull(0, false, false, true);
            Assert.True(leech.Active(0).EnergyShield);
            esFlags["_on_full_energy_shield"] = true;
            Assert.Equal(StatConditions.State.None, StatConditions.Of(overleech, belowFull));
            Assert.Equal(StatConditions.State.Holds,
                StatConditions.Of("damage_+%_on_full_energy_shield", belowFull));
        }
    }

    public static void Event_amounts_use_exact_independent_windows_and_saturating_coefficients()
    {
        var events = new RecentEvents();
        events.RecordAmount(StatConditions.KilledEnemy, 10, 2);
        events.RecordAmount(StatConditions.KilledEnemy, 11, 3);
        events.RecordCriticalOutcome(10, true);
        Assert.Equal(5, events.Amounts(13)[StatConditions.KilledEnemy]);
        Assert.Equal(3, events.Amounts(14)[StatConditions.KilledEnemy]);
        Assert.True(events.Happened(StatConditions.Crit8Seconds, 17.999));
        Assert.False(events.Happened(StatConditions.Crit8Seconds, 18));
        Assert.False(events.Happened(StatConditions.Crit, 14));
        Assert.Equal(15, events.NextExpiry(14));
        var subject = new StatConditions.Subject(Amounts: events.Amounts(14));
        Assert.Equal(9, StatConditions.ApplyRecentAmount("damage_+%_per_enemy_killed_recently", 3, subject));
        Assert.Equal(7, StatConditions.ApplyRecentAmount("damage_+%_per_enemy_killed_recently_up_to_7%", 3, subject));
        subject = subject with { Amounts = new Dictionary<string, long> { [StatConditions.SpentMana] = long.MaxValue } };
        Assert.Equal(int.MaxValue, StatConditions.ApplyRecentAmount("damage_+%_per_10_mana_spent_recently", int.MaxValue, subject));
        events.Clear();
        Assert.Empty(events.Amounts(14));
        Assert.Empty(events.Snapshot(14));
        Assert.Null(events.NextExpiry(14));
    }
}
