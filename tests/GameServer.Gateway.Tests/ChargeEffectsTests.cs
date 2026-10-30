using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class ChargeEffectsTests
{
    static readonly Lazy<StatCatalog> Catalog = new(() => new StatCatalog(ChargePanelStatsTestsData.Folder));

    // The character's stats: Character.ot's per-charge stats plus the given ones.
    static Func<string, int> Character(params (string Name, int Value)[] stats)
    {
        var all = ChargeEffects.Inherent.Select(i => (Name: i.Stat, i.Value)).Concat(stats).ToList();
        return name => all.Where(s => s.Name == name).Sum(s => s.Value);
    }

    static Func<ChargeType, int> Held(int endurance = 0, int frenzy = 0, int power = 0) =>
        t => t switch { ChargeType.Endurance => endurance, ChargeType.Frenzy => frenzy, _ => power };

    // The expanded stats added onto the character's, as WithCharges does.
    static Func<string, int> With(Func<string, int> character, Func<ChargeType, int> held)
    {
        var added = ChargeEffects.Expand(character, held);
        var display = ChargeEffects.Display(held, t => new Charges.Limits(3, 0, 10_000)).ToDictionary(d => d.Stat, d => d.Value);
        return name => character(name) + added.GetValueOrDefault(name) + display.GetValueOrDefault(name);
    }

    [Fact]
    public void The_inherent_bonuses_are_Character_ot_s()
    {
        // Metadata/Characters/Character.ot (3.29.3b).
        Assert.Equal(
        [
            ("base_attack_speed_+%_per_frenzy_charge", 4), ("base_cast_speed_+%_per_frenzy_charge", 4),
            ("object_inherent_damage_+%_final_per_frenzy_charge", 4), ("physical_damage_reduction_%_per_endurance_charge", 4),
            ("elemental_damage_reduction_%_per_endurance_charge", 4), ("critical_strike_chance_+%_per_power_charge", 50),
        ], ChargeEffects.Inherent);
    }

}
