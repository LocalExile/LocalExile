public static partial class Game
{
    public static class GolemPools
    {
        public readonly record struct Result(
            double RawBaseLife,
            double LifeIncreasedPercent,
            double LifeMore,
            double EnergyShieldIncreasedPercent,
            double DefencesIncreasedPercent,
            double EnergyShieldMore,
            double ConversionPercent,
            double MaximumEnergyShield,
            uint MaximumLife,
            bool Fatal,
            int ChaosResistance);

    public static Result Calculate(MonsterCatalog monsters, MonsterCatalog.Variety variety, byte level,
        GolemState.Golem golem, IReadOnlyDictionary<uint, int> player, MonsterModEffects effects)
    {
        int Own(string name) => GolemStat(golem.Own, name);
        int Player(string name) => GolemStat(player, name);

        double rawBaseLife = (double)(monsters.BaseLife(variety, level) ?? 100)
            + golem.Gear.GetValueOrDefault("base_maximum_life");
        double lifeIncreased = (long)Player("minion_maximum_life_+%") + Own("minion_maximum_life_+%")
            + (golem.Definition.IsGolem ? Player("golem_maximum_life_+%") : 0)
            + golem.Gear.GetValueOrDefault("maximum_life_+%");
        double lifeMore = Math.Max(0, 1 + Own("active_skill_minion_life_+%_final") / 100.0)
            * Math.Max(0, 1 + Own("support_minion_maximum_life_+%_final") / 100.0)
            * Math.Max(0, 1 + Own("support_minion_damage_minion_life_+%_final") / 100.0)
            * (golem.Definition.IsGolem ? Math.Max(0, 1 + GolemTether(player, "life") / 100.0) : 1);
        if (golem.Definition.Kind is "Flame" or "Ice" or "Lightning"
            && Player("elemental_golems_maximum_life_is_doubled") > 0)
            lifeMore *= 2;
        if (golem.Definition.Kind != "Reaper")
            lifeMore *= Math.Max(0, 1 + Player("active_skill_non_reaper_minion_maximum_life_+%_final") / 100d);

        int chaosMaximum = (int)Math.Clamp((long)MonsterModEffects.MaxResistance
            + effects["base_maximum_chaos_damage_resistance_%"], 0L, GameServer.Core.World.DamageModel.MaxResistCap);
        int uncappedChaos = (int)Math.Clamp(
            (long)HitMitigation.Resistance(monsters, variety, level, GameServer.Core.World.DamageType.Chaos).Value
            + effects.Resistance(GameServer.Core.World.DamageType.Chaos), int.MinValue, int.MaxValue);
        int chaosResistance = GameServer.Core.World.DamageModel.Capped(uncappedChaos, chaosMaximum);
        long conversionBasis = (long)effects[
            "maximum_life_%_to_convert_to_maximum_energy_shield_per_1%_chaos_resistance"]
            * Math.Max(0, chaosResistance);
        double conversion = Math.Clamp(conversionBasis, 0, 100);
        bool fatal = conversion >= 100;
        double lifeMultiplier = Math.Max(.01, 1 + lifeIncreased / 100);
        double remainingLife = rawBaseLife * lifeMultiplier * lifeMore * Math.Max(0, 1 - conversion / 100);
        uint maximumLife = fatal ? 0
            : (uint)Math.Clamp(Math.Floor(remainingLife), 1, int.MaxValue);

        double energyShieldIncreased = effects["maximum_energy_shield_+%"];
        double defencesIncreased = effects["global_defences_+%"];
        double joint = Math.Max(0, 1 + ((double)lifeIncreased + energyShieldIncreased + defencesIncreased) / 100);
        double energyShieldMore = Math.Max(0, 1 + Own("active_skill_minion_energy_shield_+%_final") / 100.0)
            * Math.Max(0, 1 + effects["energy_shield_+%_final_for_minion"] / 100.0);
        double gained = rawBaseLife * Math.Max(0, effects["maximum_life_%_to_add_as_maximum_energy_shield"]
            + conversion) / 100d * joint * lifeMore * energyShieldMore;
        double flat = ((long)golem.Auras.GetValueOrDefault("base_maximum_energy_shield")
            + golem.Gear.GetValueOrDefault("base_maximum_energy_shield"))
            * Math.Max(0, 1 + (energyShieldIncreased + defencesIncreased) / 100d)
            * energyShieldMore;
        bool noEnergyShield = effects["minions_have_no_armour_or_energy_shield"] != 0;
        double maximumEnergyShield = fatal || noEnergyShield ? 0 : Math.Max(0, gained + flat);

        return new(rawBaseLife, lifeIncreased, lifeMore, energyShieldIncreased, defencesIncreased,
            energyShieldMore, conversion, maximumEnergyShield, maximumLife, fatal, chaosResistance);
    }
    }
}
