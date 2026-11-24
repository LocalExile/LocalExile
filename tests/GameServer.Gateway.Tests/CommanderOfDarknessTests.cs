namespace GameServer.Gateway.Tests;

/// <summary>Commander of Darkness (Necromancer): each of the caster's auras grants its speed, and any aura
/// affecting the recipient grants its damage and elemental resistance once.</summary>
public class CommanderOfDarknessTests
{
    [Fact]
    public void Commander_of_Darkness_grants_speed_per_aura_and_damage_and_resistance_once()
    {
        var skills = Program.Skills;
        // The node's values (PassiveSkills 1514, Stats.dat rows 3773/4817/4482): 3% speed, +30% resistances, 30% damage.
        var haste = new AuraCatalog.Aura(1, skills.EffectRow("Haste"), "Haste", "Haste", 1, 152, 0, 5000, Level: 20,
            CommanderAttackCastSpeedPct: 3, CommanderElementalResistancePct: 30, CommanderDamagePct: 30);
        var plain = haste with { CommanderAttackCastSpeedPct = 0, CommanderElementalResistancePct = 0, CommanderDamagePct = 0 };
        var hatred = new AuraCatalog.Aura(2, skills.EffectRow("Hatred"), "Hatred", "Hatred", 2, 150, 0, 5000, Level: 20,
            CommanderAttackCastSpeedPct: 3, CommanderElementalResistancePct: 30, CommanderDamagePct: 30);
        var plainHatred = hatred with { CommanderAttackCastSpeedPct = 0, CommanderElementalResistancePct = 0, CommanderDamagePct = 0 };

        var bonus = AuraCatalog.Bonus([haste], 0, skills, Program.Stats);
        var plainBonus = AuraCatalog.Bonus([plain], 0, skills, Program.Stats);
        Assert.Equal(3, bonus.AttackSpeedPct - plainBonus.AttackSpeedPct);
        Assert.Equal(30, bonus.FireResistance - plainBonus.FireResistance);
        Assert.Equal(30, bonus.ColdResistance - plainBonus.ColdResistance);
        Assert.Equal(30, bonus.LightningResistance - plainBonus.LightningResistance);
        Assert.Equal(0, bonus.ChaosResistance - plainBonus.ChaosResistance);

        uint cast = Program.Stats.Key("cast_speed_+%")!.Value;
        uint damage = Program.Stats.Key("damage_+%")!.Value;
        var raw = AuraCatalog.RawStats([haste], 0, skills, Program.Stats).ToDictionary(p => p.Key, p => p.Value);
        var plainRaw = AuraCatalog.RawStats([plain], 0, skills, Program.Stats).ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(3, raw.GetValueOrDefault(cast) - plainRaw.GetValueOrDefault(cast));
        Assert.Equal(30, raw.GetValueOrDefault(damage) - plainRaw.GetValueOrDefault(damage));

        // A second aura grants its own speed, but the damage and resistance still apply only once.
        var twoBonus = AuraCatalog.Bonus([haste, hatred], 0, skills, Program.Stats);
        var twoPlainBonus = AuraCatalog.Bonus([plain, plainHatred], 0, skills, Program.Stats);
        Assert.Equal(6, twoBonus.AttackSpeedPct - twoPlainBonus.AttackSpeedPct);
        Assert.Equal(30, twoBonus.FireResistance - twoPlainBonus.FireResistance);
        var twoRaw = AuraCatalog.RawStats([haste, hatred], 0, skills, Program.Stats).ToDictionary(p => p.Key, p => p.Value);
        var twoPlainRaw = AuraCatalog.RawStats([plain, plainHatred], 0, skills, Program.Stats).ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(6, twoRaw.GetValueOrDefault(cast) - twoPlainRaw.GetValueOrDefault(cast));
        Assert.Equal(30, twoRaw.GetValueOrDefault(damage) - twoPlainRaw.GetValueOrDefault(damage));

        // The speed scales with the aura's own effect and rounds down per aura: 3% at 50% effect is 4%.
        var strong = haste with { ExternalEffectPct = 50 };
        var plainStrong = plain with { ExternalEffectPct = 50 };
        var strongBonus = AuraCatalog.Bonus([strong], 0, skills, Program.Stats);
        var plainStrongBonus = AuraCatalog.Bonus([plainStrong], 0, skills, Program.Stats);
        Assert.Equal(4, strongBonus.AttackSpeedPct - plainStrongBonus.AttackSpeedPct);

        // Enemy auras grant nothing: Pride and a hex carry no Commander of Darkness bonus.
        var pride = new AuraCatalog.Aura(3, skills.EffectRow("Pride"), "Pride", "Pride", 3, AuraCatalog.PrideBuffRow, 0, 5000, Level: 20,
            CommanderAttackCastSpeedPct: 3, CommanderElementalResistancePct: 30, CommanderDamagePct: 30);
        var prideBonus = AuraCatalog.Bonus([pride], 0, skills, Program.Stats);
        Assert.Equal(0, prideBonus.AttackSpeedPct);
        Assert.Equal(0, prideBonus.FireResistance);
        var prideRaw = AuraCatalog.RawStats([pride], 0, skills, Program.Stats).ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(0, prideRaw.GetValueOrDefault(cast));
        Assert.Equal(0, prideRaw.GetValueOrDefault(damage));
        var hex = new AuraCatalog.Aura(4, skills.EffectRow("TemporalChains"), "TemporalChains", "Temporal Chains", 4, 69, 0, 5000, Level: 20,
            CommanderAttackCastSpeedPct: 3, CommanderElementalResistancePct: 30, CommanderDamagePct: 30);
        var hexBonus = AuraCatalog.Bonus([hex], 0, skills, Program.Stats);
        Assert.Equal(0, hexBonus.AttackSpeedPct);
        Assert.Equal(0, hexBonus.FireResistance);
        var hexRaw = AuraCatalog.RawStats([hex], 0, skills, Program.Stats).ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(0, hexRaw.GetValueOrDefault(cast));
        Assert.Equal(0, hexRaw.GetValueOrDefault(damage));
    }
}
