using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public partial class ConditionalItemStatsTests
{
    void Blinded_item_conditions_use_the_existing_timed_owner_and_charge_consumer(ClientState conn,
        Func<string, byte, InventoryItem> wear)
    {
        const string input = "gain_frenzy_charge_on_hit_%_while_blinded", output = "add_frenzy_charge_on_skill_hit_%";
        uint Key(string name) => Program.Stats.Key(name)!.Value;
        var inventory = Game.ItemsOf(conn);
        var saved = inventory.Items(5).ToArray();
        var source = wear("Retaliation Charm", 5);
        int chance = ItemModStats.Sum([source.Item], Program.Items.Mods).GetValueOrDefault(input);
        Assert.InRange(chance, 10, 20);
        var owner = Game.ResourceEffectsOf(conn); owner.PlayerBlind.Clear();
        int Chance() => Game.CharacterPlayerStats(conn).GetValueOrDefault(Key(output));
        void Inflict()
        {
            bool running = conn.Clock.IsRunning;
            try
            {
                if (!running) conn.Clock.Start();
                Assert.True(Game.InflictPlayerBlind(conn, 4));
            }
            finally { if (!running) conn.Clock.Stop(); }
        }
        Assert.Equal(0, Chance());
        // Another accepted owner update after capture belongs to the next source snapshot.
        var captured = Game.EquipmentStatSources(conn, inventory.EquipmentSnapshot(), null, null, Inflict);
        Assert.Equal(0, captured.Sources.GetValueOrDefault(Key(output)));
        Assert.False(conn.StatSourcesCache!.Key.Flags!["_while_blinded"]);
        Assert.Equal(chance, Chance());
        foreach (bool spell in new[] { false, true })
        {
            var stats = Game.CharacterPlayerStats(conn);
            int Get(string name) => Program.Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
            var charges = Game.ChargesOf(conn);
            double at = Game.ResourceEffectsTime(conn);
            charges.Admit(at, [new ChargeEvent.Set(null, 0)], Get, () => default, true, new PhasingRoll(0));
            charges.Admit(at, [new ChargeEvent.Hit(false, spell, !spell, MonsterRarity.Normal)], Get,
                () => default, true, new PhasingRoll(chance - 1));
            Assert.Equal(1, charges.Effective(ChargeType.Frenzy));
            Assert.Empty(ChargeGains.Roll(new ChargeEvent.Hit(false, spell, !spell, MonsterRarity.Normal),
                Get, default, new PhasingRoll(chance)));
            Assert.DoesNotContain(ChargeGains.Roll(new ChargeEvent.Kill(MonsterRarity.Normal),
                Get, default, new PhasingRoll(0)), g => g.Source == output);
            charges.Admit(at, [new ChargeEvent.Set(null, 0)], Get, () => default, true, new PhasingRoll(0));
        }
        conn.PlayerStats[Key("cannot_be_blinded")] = 1;
        Assert.Equal(0, Chance());
        conn.PlayerStats.Remove(Key("cannot_be_blinded"));
        Assert.Equal(0, Chance()); // Immunity removed the existing Blind; it does not resurrect after immunity ends.
        Inflict(); Assert.Equal(chance, Chance());
        inventory.Remove(5, source.Id);
        Assert.Equal(0, Chance()); Assert.True(owner.PlayerBlind.Read(Game.ResourceEffectsTime(conn)).Less > 0);
        source = inventory.Add(5, 0, 0, source.Item); Assert.Equal(chance, Chance());
        conn.Experience = CharacterStats.ExperienceFor(1); conn.Pools = Game.PoolsFor(conn, 1);
        Assert.True(Game.IsInactive(conn, source.Id)); Assert.Equal(0, Chance());
        conn.Experience = CharacterStats.ExperienceFor(100); conn.Pools = Game.PoolsFor(conn, 100);
        Assert.Equal(chance, Chance());
        owner.PlayerBlind.Read(Game.ResourceEffectsTime(conn) + 4);
        Assert.Equal(0, Chance());
        Assert.Equal(StatConditions.State.NotModelled, StatConditions.Of(input, StatConditions.Subject.Unknown));
        inventory.Remove(5, source.Id);
        foreach (var item in saved) inventory.Add(5, item.X, item.Y, item.Item);
        conn.Pools = Game.PoolsFor(conn, 100);
    }
}
