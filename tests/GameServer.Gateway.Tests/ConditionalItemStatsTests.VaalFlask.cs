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
    }
}
