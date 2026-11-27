public static partial class Game
{
    static readonly IReadOnlyDictionary<string, string> ReservationNames = new Dictionary<string, string>
    {
        ["PurityOfElements"] = "purity_of_elements", ["PurityOfFire"] = "purity_of_fire",
        ["PurityOfIce"] = "purity_of_ice", ["PurityOfLightning"] = "purity_of_lightning",
        ["BloodAndSand"] = "blood_sand", ["FleshAndStone"] = "flesh_stone",
        ["ArcticArmour"] = "arctic_armour", ["TempestShield"] = "tempest_shield",
        ["HeraldOfAsh"] = "herald_of_ash", ["HeraldOfIce"] = "herald_of_ice",
        ["HeraldOfThunder"] = "herald_of_thunder", ["HeraldOfPurity"] = "herald_of_purity",
        ["HeraldOfAgony"] = "herald_of_agony", ["SummonSkitterbots"] = "skitterbots",
        ["PetrifiedBlood"] = "petrified_blood", ["TemporalChains"] = "temporal_chains",
        ["ElementalWeakness"] = "elemental_weakness", ["Enfeeble"] = "enfeeble",
        ["Flammability"] = "flammability", ["Frostbite"] = "frostbite", ["Conductivity"] = "conductivity",
        ["Vulnerability"] = "vulnerability", ["Despair"] = "despair", ["Punishment"] = "punishment",
    };

    static string ReservationStatName(string family) => ReservationNames.GetValueOrDefault(family, family.ToLowerInvariant());

    public static bool IsBlasphemySupport(string effect) => effect is "SupportBlasphemy"
        or "SupportAwakenedBlasphemy" or "SupportEldritchBlasphemy" or "SupportBlasphemyPlus";

    // Whether the skill's reservation is taken from Life: its own gem, a support or player stat, or Blood Magic.
    static bool ReservesLifeInstead(Player player, SupportedGem gem, bool curse, Func<uint, int> get) =>
        SkillsFor(player).LifeReservation(gem.Effect, gem.Level)
        || get(Stat.KeystoneBloodMagic) != 0
        || get(Stat.BaseSkillReserveLifeInsteadOfMana) != 0 || get(Stat.SkillReserveLifeInsteadOfMana) != 0
        || curse && get(Stat.CurseAuraSkillsReserveLife) != 0
        || Supports.Of(gem.Effect)?.Types.Contains(35) == true && get(Stat.SkillsThatThrowMinesReserveLife) != 0;

    static bool HasFixedManaReservation(string effect, Func<uint, int> get) =>
        IsHeraldAura(effect) && get(Stat.HeraldManaReservationOverride45Pct) != 0;

    public static (bool Life, int Efficiency) ReservationContext(Player player, ushort skill) =>
        ReservationContext(player, skill, null);

    public static (bool Life, int Efficiency) ReservationContext(Player player, ushort skill,
        IReadOnlyDictionary<uint, int>? playerStats)
    {
        var gem = SupportedSkill(player, skill);
        playerStats ??= StatSourceValues(player);
        int Own(uint n) => GolemStat(gem.Stats, n);
        int Get(uint n) => Own(n) + (n is var key
            ? playerStats.GetValueOrDefault(key) : 0);
        bool curse = gem.Supports.Any(s => IsBlasphemySupport(s.Effect.Id));
        bool life = ReservesLifeInstead(player, gem, curse, Get);
        string resource = life ? "life" : "mana";
        // Global efficiency is already in Pools. Add skill-local/support and family modifiers only.
        int efficiency = Own(Stat.BaseReservationEfficiencyPlusPct) + Own(Stat.ReservationEfficiencyPlusPct)
            + Own(Stat.Of($"base_{resource}_reservation_efficiency_+%")) + Own(Stat.Of($"{resource}_reservation_efficiency_+%"));
        string effect = AuraCatalog.Family(SkillsFor(player).EffectId(gem.Effect) ?? "");
        string name = ReservationStatName(effect);
        if (!life)
        {
            efficiency += Get(Stat.Of(name + "_mana_reservation_efficiency_+%"))
                - 2 * Get(Stat.Of(name + "_mana_reservation_efficiency_-2%_per_1"));
            if (effect.StartsWith("HeraldOf", StringComparison.Ordinal))
                efficiency += Get(Stat.HeraldSkillsManaReservationEfficiencyPlusPct)
                    - 2 * Get(Stat.HeraldSkillsManaReservationEfficiencyMinus2PctPer1);
            if (curse) efficiency += Get(Stat.CurseAuraSkillsManaReservationEfficiencyPlusPct)
                - 2 * Get(Stat.CurseAuraSkillsManaReservationEfficiencyMinus2PctPer1);
            int mineEfficiency = Get(Stat.ManaReservationEfficiencyPlusPctForSkillsThatThrowMines);
            if (mineEfficiency != 0 && PlacedKind(gem).Mine)
                efficiency = ActorScale.Saturate((long)efficiency + mineEfficiency);
        }
        if (!life && AuraCatalog.IsStance(effect)) efficiency += Get(Stat.StanceSkillsManaReservationEfficiencyPlusPct);
        if (curse) efficiency += Get(Stat.CurseAuraSkillsReservationEfficiencyPlusPct);
        return (life, efficiency);
    }

    public static bool ReservationBudgetFits(Player player) => ReservationBudgetFits(player,
        player.PetrifiedBlood.Active ? player.PetrifiedBlood.ManaPercent : 0,
        player.PetrifiedBlood.ReservesLife, player.PetrifiedBlood.ReservationEfficiencyPct);

    public static bool ReservationBudgetFits(Player player, int percent, bool reservesLife, int efficiency)
    {
        int mana = reservesLife ? 0 : PetrifiedBlood.Required(player.Pools.Mana, player.Pools.ManaReservationEfficiencyPct, percent, efficiency);
        int proposedLife = reservesLife
            ? PetrifiedBlood.Required(player.Pools.Life, player.Pools.LifeReservationEfficiencyPct, percent, efficiency)
            : 0;
        var lifeReservation = LifeReservation.Of(player.Pools, player.QualifiedItemLifeReservationPercent,
            player.Auras.Values, proposedLife);
        return (long)AuraCatalog.RequiredMana(player.Pools, player.Auras.Values) + mana
                + PlacedSkills.Reserved(player.Pools, player.Placed.All.Values, false) + CooldownTriggerReservation(player, life: false) <= player.Pools.Mana
            && (long)lifeReservation.Required + PlacedSkills.Reserved(player.Pools, player.Placed.All.Values, true)
                + CooldownTriggerReservation(player, life: true) < player.Pools.Life;
    }

    public static int AdditionalLifeReservation(Player player) => (int)Math.Clamp(
        (long)PetrifiedBlood.RequiredLife(player.Pools, player.PetrifiedBlood)
        + PlacedSkills.Reserved(player.Pools, player.Placed.All.Values, true)
        + CooldownTriggerReservation(player, life: true),
        0, int.MaxValue);

    static int ReservedManaArmourIncrease(int reserved, int per50) =>
        ActorScale.Saturate((long)(reserved / 50) * per50);

    public static void ClampReservedResources(Player player)
    {
        int reserved = ReservedMana(player);
        if (player.Pools.ArmourPctPer50ReservedMana != 0
            && ReservedManaArmourIncrease(reserved, player.Pools.ArmourPctPer50ReservedMana) != player.Pools.ArmourPctFromReservedMana)
        {
            player.Pools = PoolsFor(player, player.Pools.Level);
            player.AuraStatsDirty = true;
        }
        player.PlayerMana = Math.Min(player.PlayerMana, (uint)Math.Max(0, (long)player.Pools.Mana - reserved));
        player.PlayerLife = LifeReservation.Clamp(player.PlayerLife, LifeReservationOf(player));
        TrimLeechLocked(player);
    }
}
