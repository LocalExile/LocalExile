using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public sealed class HitDebuffTests
{
    static Func<string, int> Get(params (string Name, int Value)[] stats) =>
        name => stats.FirstOrDefault(s => s.Name == name).Value;

    static Func<string, int> Chances(params string[] stats) => name => stats.Contains(name) ? 100 : 0;

    // Intimidate, Unnerve, Blind and Withered from the player's hits: each rolls its own chances and lasts its own time
    // from the latest hit, and Intimidate, Unnerve and Withered raise the damage the monster takes.
    [Fact]
    public void Hits_intimidate_unnerve_blind_and_wither_by_their_chances()
    {
        // Intimidate and Unnerve: the attack-only and spell-only chances, Grattus Signet's crits, 4 s from the latest hit,
        // and 10% increased damage taken from attacks (Intimidate) or spells (Unnerve).
        {
            var random = new Random(1);
            Assert.Equal((true, false), Game.HitDebuffRolls(Chances("attacks_intimidate_on_hit_%"), spell: false, critical: false, random));
            Assert.Equal((false, false), Game.HitDebuffRolls(Chances("attacks_intimidate_on_hit_%"), spell: true, critical: false, random));
            Assert.Equal((false, true), Game.HitDebuffRolls(Chances("chance_to_unnerve_on_hit_with_spells_%"), true, false, random));
            Assert.Equal((false, false), Game.HitDebuffRolls(Chances("chance_to_unnerve_on_hit_with_spells_%"), false, false, random));
            Assert.Equal((false, true), Game.HitDebuffRolls(Chances("attacks_inflict_unnerve_on_crit"), false, true, random));
            Assert.Equal((false, false), Game.HitDebuffRolls(Chances("attacks_inflict_unnerve_on_crit"), false, false, random));
            Assert.Equal((true, false), Game.HitDebuffRolls(Chances("spells_inflict_intimidate_on_crit"), true, true, random));
            Assert.Equal((true, true), Game.HitDebuffRolls(Chances("chance_to_intimidate_on_hit_%", "chance_to_unnerve_on_hit_%"), true, false, random));

            var m = new SpawnedMonster("intimidated", new(105, 100, 0), 0, 0, 100);
            Game.HitDebuffsOnHitLocked(m, Chances("chance_to_intimidate_on_hit_%"), spell: false, critical: false, now: 10);
            Game.HitDebuffsOnHitLocked(m, Chances("chance_to_intimidate_on_hit_%"), spell: false, critical: false, now: 12);
            Assert.Equal(16, m.IntimidatedUntil);
            Assert.Equal(10, Game.HitDebuffTaken(m, spell: false, 15.9));
            Assert.Equal(0, Game.HitDebuffTaken(m, spell: true, 15.9));
            Assert.Equal(0, Game.HitDebuffTaken(m, spell: false, 16));
        }

        // Blind: the general chance, the attack-only one and the critical one sum into one roll; a Blind lasts 4 s with
        // blind_duration_+%, from the latest hit, and counts as the player's Blind for that long.
        {
            var random = new Random(1);
            Assert.True(Game.BlindRoll(Get(("global_chance_to_blind_on_hit_%", 100)), spell: true, critical: false, random));
            Assert.True(Game.BlindRoll(Get(("attacks_chance_to_blind_on_hit_%", 100)), spell: false, critical: false, random));
            Assert.False(Game.BlindRoll(Get(("attacks_chance_to_blind_on_hit_%", 100)), spell: true, critical: false, random));
            Assert.True(Game.BlindRoll(Get(("%_chance_to_blind_on_critical_strike", 100)), spell: true, critical: true, random));
            Assert.False(Game.BlindRoll(Get(("%_chance_to_blind_on_critical_strike", 100)), spell: true, critical: false, random));

            var blind = Get(("global_chance_to_blind_on_hit_%", 100), ("blind_duration_+%", 50));
            Assert.Equal(6, Game.BlindSeconds(blind), 9);
            var m = new SpawnedMonster("blinded", new(105, 100, 0), 0, 0, 100);
            Game.HitDebuffsOnHitLocked(m, blind, spell: false, critical: false, now: 10);
            Game.HitDebuffsOnHitLocked(m, Get(("global_chance_to_blind_on_hit_%", 100)), spell: false, critical: false, now: 11);
            Assert.Equal(16, m.BlindedUntil, 9);
            Assert.Equal(double.NegativeInfinity, m.IntimidatedUntil);
        }

        // Withered: 2 s or Withering Touch's duration, expiring slower by wither_expire_speed_+%; 6% increased chaos damage
        // taken per stack (with withered_effect_+%), up to 15 stacks with their own ends, on chaos damage only.
        {
            var random = new Random(1);
            var wither = Get(("withered_on_hit_for_2_seconds_%_chance", 100));
            Assert.Equal(0, Game.WitherRoll(Get(), random));
            Assert.Equal(2, Game.WitherRoll(wither, random), 9);
            Assert.Equal(2.5, Game.WitherRoll(Get(("withered_on_hit_chance_%", 100), ("support_withered_base_duration_ms", 2000),
                ("wither_expire_speed_+%", -20)), random), 9);

            var m = new SpawnedMonster("withered", new(105, 100, 0), 0, 0, 1000);
            for (int i = 0; i < 16; i++) Game.WitherOnHitLocked(m, wither, now: 10 + i * 0.1, random);
            Assert.Equal(15, m.Withered.Count);
            Assert.Equal(12.1, m.Withered.Min(s => s.Until), 9);
            Assert.Equal(90, Game.WitheredTaken(m, 11.6), 9);
            Assert.Equal(18, Game.WitheredTaken(m, 13.25), 9);
            Assert.Equal(190, Game.MonsterTakesMitigated(m, DamagePacket.Of(DamageType.Chaos, 100, 100), spell: false, now: 11.6));
            Assert.Equal(100, Game.MonsterTakesMitigated(m, DamagePacket.Of(DamageType.Fire, 100, 100), spell: false, now: 11.6));

            var stronger = new SpawnedMonster("withered more", new(105, 100, 0), 0, 0, 1000);
            Game.WitherOnHitLocked(stronger, Get(("withered_on_hit_for_2_seconds_%_chance", 100), ("withered_effect_+%", 50)), 10, random);
            Assert.Equal(9, Game.WitheredTaken(stronger, 10.5), 9);
        }
    }

    // Exposure on hit: -10% by default, a magnitude stat's own value, the extra and minimum stats, 4 s by element and
    // magnitude, the highest running one lowering the monster's resistance to hits and burns. Elemental Equilibrium: an
    // elemental hit removes Exposure of its own elements and inflicts -25% of the others, scaled by
    // elemental_equilibrium_effect_+%; a hit without elemental damage does neither.
    [Fact]
    public void Hits_expose_by_element_and_elemental_equilibrium_exposes_the_other_elements()
    {
        {
            var random = new Random(1);
            Assert.Equal(-10, Game.ExposureRoll(Get(("base_inflict_fire_exposure_on_hit_%_chance", 100)), DamageType.Fire, false, random));
            Assert.Equal(0, Game.ExposureRoll(Get(("base_inflict_fire_exposure_on_hit_%_chance", 100)), DamageType.Cold, false, random));
            var withCold = Get(("chance_to_inflict_cold_exposure_on_hit_with_cold_damage_%", 100));
            Assert.Equal(0, Game.ExposureRoll(withCold, DamageType.Cold, withType: false, random));
            Assert.Equal(-10, Game.ExposureRoll(withCold, DamageType.Cold, withType: true, random));
            Assert.Equal(-14, Game.ExposureRoll(Get(("lightning_exposure_on_hit_magnitude", -14)), DamageType.Lightning, false, random));
            Assert.Equal(-22, Game.ExposureRoll(Get(("inflict_all_exposure_on_hit", 1),
                ("cold_exposure_you_inflict_applies_extra_cold_resistance_+%", -12)), DamageType.Cold, false, random));
            Assert.Equal(-25, Game.ExposureRoll(Get(("inflict_all_exposure_on_hit", 1),
                ("exposure_you_inflict_has_minimum_resistance_%", -25)), DamageType.Fire, false, random));
            Assert.Equal(0, Game.ExposureRoll(Get(("inflict_all_exposure_on_hit", 1), ("cannot_inflict_exposure", 1)),
                DamageType.Fire, false, random));

            var m = new SpawnedMonster("exposed", new(105, 100, 0), 0, 0, 100);
            var fire = DamagePacket.Of(DamageType.Fire, 10, 10);
            Game.ExposureOnHitLocked(m, Get(("base_inflict_fire_exposure_on_hit_%_chance", 100)), fire, now: 10, random);
            Game.ExposureOnHitLocked(m, Get(("fire_exposure_on_hit_magnitude", -14)), fire, now: 8, random);
            int unexposed = Game.MonsterHitResistance(m, DamageType.Fire, default).Value;
            Assert.Equal(unexposed - 14, Game.MonsterHitResistance(m, DamageType.Fire, default, now: 11).Value);
            Assert.Equal(unexposed - 10, Game.MonsterHitResistance(m, DamageType.Fire, default, now: 12.5).Value);
            Assert.Equal(unexposed, Game.MonsterHitResistance(m, DamageType.Fire, default, now: 14).Value);
            Assert.Equal(unexposed, Game.MonsterHitResistance(m, DamageType.Cold, default, now: 11).Value);
            Assert.Equal(14, m.ExposureUntil[0]);
        }

        {
            var random = new Random(1);
            var m = new SpawnedMonster("balanced", new(105, 100, 0), 0, 0, 100);
            var keystone = Get(("keystone_elemental_equilibrium", 1));
            var physical = DamagePacket.Of(DamageType.Physical, 10, 10);
            Game.ExposureOnHitLocked(m, Get(("base_inflict_fire_exposure_on_hit_%_chance", 100)), physical, now: 10, random);
            Game.ExposureOnHitLocked(m, keystone, physical, now: 10.5, random);
            Assert.Equal(-10, Game.MonsterExposure(m, DamageType.Fire, 11));
            Assert.Equal(0, Game.MonsterExposure(m, DamageType.Cold, 11));

            Game.ExposureOnHitLocked(m, keystone, DamagePacket.Of(DamageType.Cold, 10, 10), now: 11, random);
            Assert.Equal(-25, Game.MonsterExposure(m, DamageType.Fire, 12));
            Assert.Equal(-25, Game.MonsterExposure(m, DamageType.Lightning, 12));
            Assert.Equal(0, Game.MonsterExposure(m, DamageType.Cold, 12));

            Game.ExposureOnHitLocked(m, Get(("keystone_elemental_equilibrium", 1), ("elemental_equilibrium_effect_+%", 20)),
                DamagePacket.Of(DamageType.Fire, 10, 10), now: 12, random);
            Assert.Equal(0, Game.MonsterExposure(m, DamageType.Fire, 12.5));
            Assert.Equal(double.NegativeInfinity, m.ExposureUntil[0]);
            Assert.Equal(-30, Game.MonsterExposure(m, DamageType.Cold, 12.5));
            Assert.Equal(-30, Game.MonsterExposure(m, DamageType.Lightning, 12.5));
        }
    }
}
