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
        int Own(uint name) => GolemStat(golem.Own, name);
        int Player(uint name) => GolemStat(player, name);

        var inherited = golem.InheritedMods.Effects;
        double rawBaseLife = (double)(monsters.BaseLife(variety, level) ?? 100)
            + golem.Gear.GetValueOrDefault(Stat.BaseMaximumLife)
            + (golem.Definition.Kind == "Zombie" ? Player(Stat.ZombieMaximumLifePlus) : 0);
        int familyLife = golem.Definition.IsGolem ? Player(Stat.GolemMaximumLifePlusPct) : golem.Definition.Kind switch
        {
            "Zombie" => Player(Stat.BaseZombieMaximumLifePlusPct),
            "Spectre" => Player(Stat.BaseSpectreMaximumLifePlusPct),
            "RagingSpirit" => Player(Stat.MinionRagingSpiritMaximumLifePlusPct),
            _ => 0,
        };
        double lifeIncreased = (long)Player(Stat.MinionMaximumLifePlusPct) + Own(Stat.MinionMaximumLifePlusPct)
            + familyLife + golem.Gear.GetValueOrDefault(Stat.MaximumLifePlusPct) + inherited[Stat.MaximumLifePlusPct];
        double lifeMore = Math.Max(0, 1 + Own(Stat.ActiveSkillMinionLifePlusPctFinal) / 100.0)
            * Math.Max(0, 1 + Own(Stat.SupportMinionMaximumLifePlusPctFinal) / 100.0)
            * Math.Max(0, 1 + Own(Stat.SupportMinionDamageMinionLifePlusPctFinal) / 100.0)
            * Math.Max(0, 1 + Player(Stat.NecromancerDefensiveNotableMinionMaximumLifePlusPctFinal) / 100.0)
            * (golem.Definition.IsGolem ? Math.Max(0, 1 + GolemTether(player, "life") / 100.0) : 1);
        if (golem.Definition.Kind is "Flame" or "Ice" or "Lightning"
            && Player(Stat.ElementalGolemsMaximumLifeIsDoubled) > 0)
            lifeMore *= 2;
        if (golem.Definition.Kind != "Reaper")
            lifeMore *= Math.Max(0, 1 + Player(Stat.ActiveSkillNonReaperMinionMaximumLifePlusPctFinal) / 100d);

        lifeMore *= Math.Max(0, 1 + inherited[Stat.MonsterLifePlusPctFinalFromRarity] / 100d)
            * Math.Max(0, 1 + inherited[MonsterModCatalog.RarityTableLifeStat] / 100d)
            * Math.Max(0, 1 + inherited[MonsterModCatalog.MapHiddenLifeStat] / 100d);

        int chaosMaximum = (int)Math.Clamp((long)MonsterModEffects.MaxResistance
            + effects[Stat.BaseMaximumChaosDamageResistancePct], 0L, GameServer.Core.World.DamageModel.MaxResistCap);
        int uncappedChaos = (int)Math.Clamp(
            (long)HitMitigation.Resistance(monsters, variety, level, GameServer.Core.World.DamageType.Chaos).Value
            + effects.Resistance(GameServer.Core.World.DamageType.Chaos), int.MinValue, int.MaxValue);
        int chaosResistance = GameServer.Core.World.DamageModel.Capped(uncappedChaos, chaosMaximum);
        long conversionBasis = (long)effects[
            Stat.MaximumLifePctToConvertToMaximumEnergyShieldPer1PctChaosResistance]
            * Math.Max(0, chaosResistance);
        double conversion = Math.Clamp(conversionBasis, 0, 100);
        bool fatal = conversion >= 100;
        double lifeMultiplier = Math.Max(.01, 1 + lifeIncreased / 100);
        double remainingLife = rawBaseLife * lifeMultiplier * lifeMore * Math.Max(0, 1 - conversion / 100);
        uint maximumLife = fatal ? 0
            : (uint)Math.Clamp(Math.Floor(remainingLife), 1, int.MaxValue);

        double energyShieldIncreased = effects[Stat.MaximumEnergyShieldPlusPct];
        double defencesIncreased = effects[Stat.GlobalDefencesPlusPct];
        double joint = Math.Max(0, 1 + ((double)lifeIncreased + energyShieldIncreased + defencesIncreased) / 100);
        double energyShieldMore = Math.Max(0, 1 + Own(Stat.ActiveSkillMinionEnergyShieldPlusPctFinal) / 100.0)
            * Math.Max(0, 1 + effects[Stat.EnergyShieldPlusPctFinalForMinion] / 100.0);
        double gained = rawBaseLife * Math.Max(0, effects[Stat.MaximumLifePctToAddAsMaximumEnergyShield]
            + conversion) / 100d * joint * lifeMore * energyShieldMore;
        double flat = ((long)golem.Auras.GetValueOrDefault(Stat.BaseMaximumEnergyShield)
            + golem.Gear.GetValueOrDefault(Stat.BaseMaximumEnergyShield))
            * Math.Max(0, 1 + (energyShieldIncreased + defencesIncreased) / 100d)
            * energyShieldMore;
        bool noEnergyShield = effects[Stat.MinionsHaveNoArmourOrEnergyShield] != 0;
        double maximumEnergyShield = fatal || noEnergyShield ? 0 : Math.Max(0, gained + flat);

        return new(rawBaseLife, lifeIncreased, lifeMore, energyShieldIncreased, defencesIncreased,
            energyShieldMore, conversion, maximumEnergyShield, maximumLife, fatal, chaosResistance);
    }
    }
}
