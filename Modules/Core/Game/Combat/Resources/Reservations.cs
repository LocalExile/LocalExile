static partial class Game
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

    internal static bool IsBlasphemySupport(string effect) => effect is "SupportBlasphemy"
        or "SupportAwakenedBlasphemy" or "SupportEldritchBlasphemy" or "SupportBlasphemyPlus";

    internal static (bool Life, int Efficiency) ReservationContext(ClientState conn, ushort skill)
    {
        var gem = SupportedSkill(conn, skill);
        var player = CharacterStatSources(conn).Stats;
        int Own(string n) => GolemStat(gem.Stats, n);
        int Get(string n) => Own(n) + (Stats.Key(n) is uint key
            ? conn.PlayerStats.GetValueOrDefault(key, player.GetValueOrDefault(key)) : 0);
        bool curse = gem.Supports.Any(s => IsBlasphemySupport(s.Effect.Id));
        bool life = SkillsFor(conn).LifeReservation(gem.Effect, gem.Level)
            || Get("base_skill_reserve_life_instead_of_mana") != 0 || Get("skill_reserve_life_instead_of_mana") != 0
            || curse && Get("curse_aura_skills_reserve_life") != 0
            || Supports.Of(gem.Effect)?.Types.Contains(35) == true && Get("skills_that_throw_mines_reserve_life") != 0;
        string resource = life ? "life" : "mana";
        // Global efficiency is already in Pools. Add skill-local/support and family modifiers only.
        int efficiency = Own("base_reservation_efficiency_+%") + Own("reservation_efficiency_+%")
            + Own($"base_{resource}_reservation_efficiency_+%") + Own($"{resource}_reservation_efficiency_+%");
        string effect = AuraCatalog.Family(SkillsFor(conn).EffectId(gem.Effect) ?? "");
        string name = ReservationNames.GetValueOrDefault(effect, effect.ToLowerInvariant());
        if (!life)
        {
            efficiency += Get(name + "_mana_reservation_efficiency_+%")
                - 2 * Get(name + "_mana_reservation_efficiency_-2%_per_1");
            if (effect.StartsWith("HeraldOf", StringComparison.Ordinal))
                efficiency += Get("herald_skills_mana_reservation_efficiency_+%")
                    - 2 * Get("herald_skills_mana_reservation_efficiency_-2%_per_1");
            if (curse) efficiency += Get("curse_aura_skills_mana_reservation_efficiency_+%")
                - 2 * Get("curse_aura_skills_mana_reservation_efficiency_-2%_per_1");
        }
        if (!life && AuraCatalog.IsStance(effect)) efficiency += Get("stance_skills_mana_reservation_efficiency_+%");
        if (curse) efficiency += Get("curse_aura_skills_reservation_efficiency_+%");
        return (life, efficiency);
    }

    internal static bool ReservationBudgetFits(ClientState conn) => ReservationBudgetFits(conn,
        conn.PetrifiedBlood.Active ? conn.PetrifiedBlood.ManaPercent : 0,
        conn.PetrifiedBlood.ReservesLife, conn.PetrifiedBlood.ReservationEfficiencyPct);

    internal static bool ReservationBudgetFits(ClientState conn, int percent, bool reservesLife, int efficiency)
    {
        int mana = reservesLife ? 0 : PetrifiedBlood.Required(conn.Pools.Mana, conn.Pools.ManaReservationEfficiencyPct, percent, efficiency);
        int proposedLife = reservesLife
            ? PetrifiedBlood.Required(conn.Pools.Life, conn.Pools.LifeReservationEfficiencyPct, percent, efficiency)
            : 0;
        var lifeReservation = LifeReservation.Of(conn.Pools, conn.QualifiedItemLifeReservationPercent,
            conn.Auras.Values, proposedLife);
        return (long)AuraCatalog.RequiredMana(conn.Pools, conn.Auras.Values) + mana + PlacedSkills.Reserved(conn.Pools, conn.Placed.All.Values, false) <= conn.Pools.Mana
            && (long)lifeReservation.Required + PlacedSkills.Reserved(conn.Pools, conn.Placed.All.Values, true) < conn.Pools.Life;
    }

    internal static int AdditionalLifeReservation(ClientState conn) => (int)Math.Clamp(
        (long)PetrifiedBlood.RequiredLife(conn.Pools, conn.PetrifiedBlood)
        + PlacedSkills.Reserved(conn.Pools, conn.Placed.All.Values, true),
        0, int.MaxValue);

    internal static void ClampReservedResources(ClientState conn)
    {
        conn.PlayerMana = Math.Min(conn.PlayerMana, (uint)UnreservedMana(conn));
        conn.PlayerLife = LifeReservation.Clamp(conn.PlayerLife, LifeReservationOf(conn));
        TrimLeechLocked(conn);
    }
}
