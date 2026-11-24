using GameServer.Core.Protocol.Instance;

namespace GameServer.Gateway.Tests;

/// <summary>
/// Eldritch "while a Pinnacle Boss is in your Presence" implicits: a mod carrying
/// <c>local_influence_mod_requires_celestial_boss_presence</c> (Stats 15802, IsLocal) only reaches the character
/// while a Pinnacle Boss is in presence; a mod without the condition stat is unchanged. The presence fact arrives
/// on the subject's flags (<see cref="StatConditions.CelestialBossInPresence"/>), published by
/// <c>Game.ItemConditionFlags</c> from <c>Game.CelestialBossInPresenceLocked</c> (PresenceRadius 80; the predicate
/// is the client's <c>PinnacleAtlasBoss</c> marker mod on the monster's variety).
/// </summary>
public class CelestialPresenceTests
{
    const string ConditionStat = "local_influence_mod_requires_celestial_boss_presence";

    [Fact]
    public void Gated_mod_counts_only_while_a_pinnacle_boss_is_in_presence()
    {
        var items = Program.Items;
        // This row's StatsKey* were verified in client.db (3.29.3c): StatsKey1 = 70 (attack_speed_+%),
        // StatsKey2 = 15802 (the celestial condition stat).
        var mod = items.Mods.All.Single(m => m.Id == "IncreasedAttackSpeedEldritchImplicitPinnaclePresence1");
        Assert.Contains(mod.Stats, s => s.Name == ConditionStat);
        var item = ItemCatalog.Create(items.Find("Iron Hat")!) with
        {
            // The row's own values: attack_speed_+% 16 (Stat1Min = Stat1Max = 16) and the condition stat's 1.
            Mods = [new(ModSection.Implicit, mod.Hash, [16, 1])],
        };
        uint key = Program.Stats.Key("attack_speed_+%")!.Value;
        var absent = new StatConditions.Subject(Flags: new Dictionary<string, bool> { [StatConditions.CelestialBossInPresence] = false });
        var present = new StatConditions.Subject(Flags: new Dictionary<string, bool> { [StatConditions.CelestialBossInPresence] = true });
        Assert.Equal(0, ItemModStats.Global([item], items.Mods, Program.Stats, absent).GetValueOrDefault(key));
        Assert.Equal(16, ItemModStats.Global([item], items.Mods, Program.Stats, present)[key]);
        // A subject that does not answer presence leaves the mod as before.
        Assert.Equal(16, ItemModStats.Global([item], items.Mods, Program.Stats)[key]);
    }

    [Fact]
    public void No_scan_when_no_worn_item_carries_the_pinnacle_presence_mod()
    {
        var player = new Player(new(1, "no-pinnacle-mod", 100, 100, 100, 100, null, []), 1, 1)
            { Character = { Name = "NoPinnacleMod" + Guid.NewGuid().ToString("N") } };
        double now = player.Clock.Elapsed.TotalSeconds;
        // No worn item at all: the flag is absent, not false - the monster scan never ran.
        Assert.DoesNotContain(StatConditions.CelestialBossInPresence, Game.ItemConditionFlags(player, now).Keys);
        // A worn item with an ordinary mod, and no pinnacle-presence mod, leaves the flag absent too.
        var items = Program.Items;
        var plain = items.Mods.All.First(m => m.Stats.Count == 1 && m.Stats[0].Id == Stat.BaseMaximumLife);
        var item = ItemCatalog.Create(items.Find("Iron Hat")!) with
        {
            Mods = [new(ModSection.Explicit, plain.Hash, [42])],
        };
        Assert.DoesNotContain(StatConditions.CelestialBossInPresence,
            Game.ItemConditionFlags(player, now, [item]).Keys);
    }
}
