using GameServer.Core.Protocol.Instance;

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

        // The speed scales with the aura's own effect and per-aura effect, rounded down per aura.
        var strong = haste with { ExternalEffectPct = 50 };
        var plainStrong = plain with { ExternalEffectPct = 50 };
        var strongBonus = AuraCatalog.Bonus([strong], 0, skills, Program.Stats);
        var plainStrongBonus = AuraCatalog.Bonus([plainStrong], 0, skills, Program.Stats);
        Assert.Equal(4, strongBonus.AttackSpeedPct - plainStrongBonus.AttackSpeedPct);

        var perAura = haste with { PerAuraEffectPct = 100 };
        var plainPerAura = plain with { PerAuraEffectPct = 100 };
        Assert.Equal(6, AuraCatalog.Bonus([perAura], 0, skills, Program.Stats).AttackSpeedPct
            - AuraCatalog.Bonus([plainPerAura], 0, skills, Program.Stats).AttackSpeedPct);

        // Two copies of the same aura family do not double-count Commander of Darkness speed.
        var dupBonus = AuraCatalog.Bonus([haste, strong], 0, skills, Program.Stats);
        Assert.Equal(4, dupBonus.AttackSpeedPct - plainStrongBonus.AttackSpeedPct);

        // Enemy-affecting auras (AuraAffectsEnemies) and hexes grant nothing: Pride, Flesh and Stone, Skitterbots, hex.
        foreach (var (id, row) in new (string, ushort)[]
        {
            ("Pride", AuraCatalog.PrideBuffRow),
            ("FleshAndStone", AuraCatalog.FleshAndStoneBloodRow),
            ("SummonSkitterbots", 1390),
            ("TemporalChains", 69),
        })
        {
            var enemyAura = new AuraCatalog.Aura(3, skills.EffectRow(id), id, id, 3, row, 0, 5000, Level: 20,
                CommanderAttackCastSpeedPct: 3, CommanderElementalResistancePct: 30, CommanderDamagePct: 30);
            var b = AuraCatalog.Bonus([enemyAura], 0, skills, Program.Stats);
            Assert.Equal(0, b.AttackSpeedPct);
            Assert.Equal(0, b.FireResistance);
            var r = AuraCatalog.RawStats([enemyAura], 0, skills, Program.Stats).ToDictionary(p => p.Key, p => p.Value);
            Assert.Equal(0, r.GetValueOrDefault(cast));
            Assert.Equal(0, r.GetValueOrDefault(damage));
        }
    }

    [Fact]
    public void Quiver_modifier_effect_scales_quiver_mods_caches_reference_and_stacks_with_Widowhail()
    {
        var items = Program.Items;
        var quiverType = items.Find("Broadhead Arrow Quiver")!;
        var bowType = items.Find("Short Bow")!;
        var widowhailMod = items.Mods.ById("QuiverModifierEffectUnique__1")!;
        var dexMod = items.Mods.ById("DexterityUniqueQuiver7")!;
        var projSpeedMod = items.Mods.ById("ProjectileSpeedUniqueQuiver8")!;
        var quiver = ItemCatalog.Create(quiverType) with
        {
            Mods = [new(ModSection.Explicit, dexMod.Hash, [30]), new(ModSection.Explicit, projSpeedMod.Hash, [-25])],
        };
        var bow = ItemCatalog.Create(bowType) with
        {
            Mods = [new(ModSection.Explicit, widowhailMod.Hash, [200])],
        };
        var passives = new Dictionary<uint, int> { [Stat.QuiverModEffectPlusPct] = 20 };

        // Bow Mastery (+20%) + Widowhail (+200%) = +220% (3.2x), truncated toward zero for positive and negative values.
        var scaled1 = Game.WithQuiverModifierEffect([bow, quiver], items, passives);
        var scaled2 = Game.WithQuiverModifierEffect([bow, quiver], items, passives);
        var scaledQuiver = scaled1[1];
        Assert.Same(scaledQuiver, scaled2[1]);
        Assert.Equal([96], scaledQuiver.Mods[0].Values);
        Assert.Equal([-80], scaledQuiver.Mods[1].Values);
        // Original inventory item is never mutated.
        Assert.Equal([30], quiver.Mods[0].Values);
        // Zero effect returns the original item reference; < -100% clamps multiplier to 0.
        Assert.Same(quiver, Game.WithQuiverModifierEffect([quiver], items, new Dictionary<uint, int>())[0]);
        Assert.Equal([0], Game.ScaledModifiers(quiver, -150).Mods[0].Values);
    }
}
