using GameServer.Core.Protocol.Instance;

/// <summary>
/// Owner-only Enduring Cry values, resolved from current skill rows and caller-supplied completion-time targets.
/// Data: GE268/StatSet268, GESSPL2836/2855, quality21. Pinned PoB16de4b82 act_str3986-4069 supplies radius60,
/// ConfigOptions743 rarity power, and CalcPerform2379-2398 whole power groups and the regeneration cap25.
/// This does not execute a cast, taunt, party sharing, Deathrattle or charge admission/cooldown budgeting.
/// </summary>
static class EnduringCry
{
    public const string Effect = "EnduringCry";
    public const ushort BuffDefinition = 1541;
    public const int BaseRadius = 60;
    public const int RegenerationPowerCap = 25;
    public const string ChargePowerStat = "endurance_charge_granted_per_X_monster_power_during_endurance_warcry";
    public const string RegenStat = "enduring_cry_life_regeneration_rate_per_minute_%_per_5_power_up_to_cap";

    public sealed record Profile(int EffectRow, int Level, int Quality, WarcryTiming Timing,
        int BaseCooldownMilliseconds, int StoredUses, int RecoveryPercent, int BaseManaCost, int BaseLifeCost,
        int Radius, int DurationMilliseconds, int PowerPerCharge, int AdditionalCharges,
        int IncreasedPowerPercent, bool MinimumTenPower, bool InfinitePower, int RegenPerMinutePerFivePower,
        decimal BuffEffect, string? DisabledReason)
    {
        // Restriction/timing scope only. The caller still needs canonical cooldown eligibility and paid admission.
        public bool CanResolveOwnerEffect => DisabledReason is null && Timing.DurationMilliseconds is not null;
        public double RecoveryRate => Math.Max(0, 1 + RecoveryPercent / 100.0);
        public double CooldownSeconds => RecoveryRate > 0 ? BaseCooldownMilliseconds / 1000.0 / RecoveryRate
            : double.PositiveInfinity;
    }

    /// <summary>
    /// Raw character inputs (not an already overlaid skill vector). A published generic cooldown total includes its
    /// base input; skill quality and warcry/specific recovery add separately. The canonical SingleUseCooldown adapter
    /// must still enforce its unsupported overrides/use-count gates before admitting any returned plan.
    /// </summary>
    public static Profile? For(SkillCatalog skills, StatCatalog stats, int effect, int level, int quality,
        IReadOnlyDictionary<uint, int> player, IReadOnlyDictionary<uint, int>? own = null)
    {
        if (level < 1 || skills.EffectId(effect) != Effect || !skills.IsWarcry(effect)) return null;
        own ??= skills.EffectStats(effect, level, quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        int Read(IReadOnlyDictionary<uint, int> source, string name) =>
            stats.Key(name) is uint key ? source.GetValueOrDefault(key) : 0;
        int Own(string name) => Read(own, name);
        int Player(string name) => Read(player, name);
        long Sum(string name) => (long)Player(name) + Own(name);
        static int Int(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
        int Combined(string name) => Int(Sum(name));
        int Projected(string total, string raw) => stats.Key(total) is uint key && player.TryGetValue(key, out int value)
            ? value : Player(raw);
        int per = Own(ChargePowerStat);
        if (per <= 0 || WarcryTiming.ForSkill(skills, stats, effect, level, quality, player, own) is not { } timing) return null;

        long recovery = Projected("virtual_cooldown_speed_+%", "base_cooldown_speed_+%")
            + (long)Own("base_cooldown_speed_+%") + Own("virtual_cooldown_speed_+%")
            + Sum("warcry_cooldown_speed_+%") + Sum("enduring_cry_cooldown_speed_+%");
        // Native one-use recovery construction adds100 in signed i32. This is the existing adapter's numeric bound.
        int recoveryPercent = (int)Math.Clamp(recovery, -100, int.MaxValue - 100);
        long increasedDuration = Sum("skill_effect_duration_+%") + Sum("warcry_duration_+%");
        decimal duration = Math.Max(0, Own("base_skill_effect_duration"))
            * Math.Max(0m, 100m + increasedDuration) / 100m
            * Math.Max(0m, 100m + Sum("skill_effect_duration_+%_final")) / 100m;
        int durationMs = (int)Math.Clamp(decimal.Truncate(duration), 0m, int.MaxValue);
        decimal buffEffect = Math.Max(0m, 100m + Sum("skill_buff_effect_+%")
            + Sum("warcry_buff_effect_+%") + Sum("enduring_cry_buff_effect_+%")
            + Sum("buff_effect_on_self_+%")) / 100m;

        // The generic area total is already combined by CharacterPlayerStats. Add only applicable warcry area once.
        int Area(string name) => name switch
        {
            "skill_area_of_effect_+%" => Int((long)Projected("skill_area_of_effect_+%", "base_skill_area_of_effect_+%")
                + Own("skill_area_of_effect_+%") + Own("base_skill_area_of_effect_+%")
                + Sum("warcry_skill_area_of_effect_+%")),
            _ => Combined(name),
        };
        int radius = SkillArea.Radius(BaseRadius, Area, spell: false, combined: true);
        string? disabled = SkillRestrictions.From(Combined).CannotUse(spell: false, aura: false, warcry: true);
        return new(effect, level, quality, timing, skills.CooldownMs(effect, level), skills.CooldownUses(effect, level),
            recoveryPercent, Math.Max(0, Own("base_mana_cost")), Math.Max(0, Own("base_life_cost")), radius, durationMs,
            per, Combined("enduring_cry_grants_x_additional_endurance_charges"), Combined("warcry_monster_power_+%"),
            Combined("warcries_have_minimum_10_power") != 0, Combined("warcries_have_infinite_power") != 0,
            Math.Max(0, Own(RegenStat)), buffEffect, disabled);
    }

    /// <summary>A caller snapshots these at accepted effect completion, with distance from its current centre.</summary>
    public readonly record struct Target(MonsterRarity Rarity, double Distance, bool Alive = true,
        bool Hostile = true, bool Owned = false);
    public readonly record struct Outcome(decimal Power, int Charges, int RegenPerMinute, int DurationMilliseconds);

    public static int RarityPower(MonsterRarity rarity) => rarity switch
    {
        MonsterRarity.Normal => 1,
        MonsterRarity.Magic => 2,
        MonsterRarity.Rare => 10,
        MonsterRarity.Unique => 20,
        _ => 0,
    };

    public static Outcome Resolve(Profile profile, IEnumerable<Target> targets)
    {
        if (!profile.CanResolveOwnerEffect) return default;
        decimal raw = 0;
        foreach (var target in targets)
            if (target.Alive && target.Hostile && !target.Owned && double.IsFinite(target.Distance)
                && target.Distance >= 0 && target.Distance <= profile.Radius)
                raw += RarityPower(target.Rarity);
        decimal power = profile.InfinitePower ? decimal.MaxValue
            : Math.Max(profile.MinimumTenPower ? 10m : 0m, raw * Math.Max(0m, 100m + profile.IncreasedPowerPercent) / 100m);
        decimal charges = profile.PowerPerCharge > 0 ? decimal.Floor(power / profile.PowerPerCharge) : 0;
        charges += profile.AdditionalCharges;
        decimal regen = profile.RegenPerMinutePerFivePower
            * decimal.Floor(Math.Min(power, RegenerationPowerCap) / 5) * profile.BuffEffect;
        return new(power, (int)Math.Clamp(charges, 0m, int.MaxValue),
            (int)Math.Clamp(decimal.Truncate(regen), 0m, int.MaxValue), profile.DurationMilliseconds);
    }
}
