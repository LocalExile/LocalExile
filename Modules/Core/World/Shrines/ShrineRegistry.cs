namespace GameServer.Modules.Shrines;

/// <summary>
/// The registration point of the shrine system: every type's <see cref="ShrineDefinition"/>, by Shrines row id. A type
/// with no registration runs on its data row alone (its buff's stats, its duration and its guard packs), which is all
/// most of the fifteen basic shrines need.
/// </summary>
public static class ShrineRegistry
{
    static readonly Dictionary<string, ShrineDefinition> Registered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers (or replaces) one shrine type's gameplay. Idempotent, so a module reload re-registers the
    /// same entries.</summary>
    public static void Register(ShrineDefinition definition) => Registered[definition.Id] = definition;

    /// <summary>The registered gameplay of a Shrines row id, or null when the type runs on its data alone.</summary>
    public static ShrineDefinition? Find(string id) => Registered.GetValueOrDefault(id);

    /// <summary>Every registered type, for the log and the dev command.</summary>
    public static IReadOnlyCollection<ShrineDefinition> All => Registered.Values;

    /// <summary>
    /// The types that come with gameplay beyond their data. The values here are the client's own, checked against
    /// <c>client.db</c>: BuffDefinitions row 241 (<c>shrine_acceleration</c>) grants Stats rows 347 (<c>action_speed_-%</c>),
    /// 177 (<c>base_projectile_speed_+%</c>) and 5818 (<c>has_shrine_effect</c>); the player's BuffTemplates row 383
    /// (<c>acceleration_player</c>) holds -50, 80, 1 and its monster row 384 (<c>acceleration_monster</c>) holds -60, 100,
    /// 1 — the monsters are made faster than the player, so the two sides are separate values. The Shrines row's
    /// Duration is 45 and it names no guard packs.
    /// </summary>
    public static void RegisterDefaults()
    {
        Register(new ShrineDefinition
        {
            Id = "Acceleration",
            Name = "Acceleration Shrine",
            Duration = 45,
            PlayerStats =
            [
                ("action_speed_-%", -50),        // 50% increased action speed
                ("base_projectile_speed_+%", 80), // 80% increased projectile speed
                ("has_shrine_effect", 1),
            ],
            // Before activation: the shrine accelerates the monsters around it with its own monster buff, and takes that
            // buff off them when the player claims it (data: -60 action speed, 100 projectile speed).
            BeforeActivation = [() => new MonsterAuraEffect()],
        });

        // Massive: the client's own row (Shrines row 258, BuffDefinitions shrine_massive) grants the player increased
        // life, area of effect and size — BuffTemplates 407 holds 30, 40, 40 and the has_shrine_effect flag — for 45 s;
        // its monster row 408 is stronger (20, 155, 60, 1) and belongs on the monsters around the unclaimed shrine, so
        // the definition only adds that aura. The stats and the duration come from the row, not from this definition.
        Register(new ShrineDefinition
        {
            Id = "Massive",
            Name = "Massive Shrine",
            BeforeActivation = [() => new MonsterAuraEffect()],
        });

        // Resistance: +50% to all elemental resistances and +10% to all maximum resistances for 45 s — BuffDefinitions
        // 262 (shrine_resistance), BuffTemplates 411 (player) and 412 (monster), both holding 50, 10, 1. The monsters
        // around the unclaimed shrine get the same row, so the type adds only the shared aura; stats and duration are
        // the Shrines row's.
        Register(new ShrineDefinition
        {
            Id = "Resistance",
            Name = "Resistance Shrine",
            BeforeActivation = [() => new MonsterAuraEffect()],
        });

        // Impenetrable (the client's row id is Defence): BuffDefinitions 260 (shrine_defence) for 45 s. The player's
        // BuffTemplates row 409 is +100% armour, +100% evasion and +100% maximum energy shield; the monster row 410 is
        // the same stats at 0 plus 20% less damage taken (-20 base_damage_taken_+%), 20% of maximum life as extra
        // maximum energy shield and +20% spell suppression, so the monsters around the unclaimed shrine are the ones
        // that get tougher. Stats and duration come from the Shrines row.
        Register(new ShrineDefinition
        {
            Id = "Defence",
            Name = "Impenetrable Shrine",
            BeforeActivation = [() => new MonsterAuraEffect()],
        });

        // Diamond (the client's row id is Criticals): BuffDefinitions 246 (shrine_critical) for 45 s. The player's
        // BuffTemplates row 391 is always_crit 1, has_shrine_effect 1
        // the monster row 392 is always_crit 1, has_shrine_effect 1
        // so the monsters around the unclaimed shrine get their own values, not the player's. Stats and duration come
        // from the Shrines row; the definition adds the shared aura.
        Register(new ShrineDefinition
        {
            Id = "Criticals",
            Name = "Diamond Shrine",
            BeforeActivation = [() => new MonsterAuraEffect()],
        });

        // Replenishing (the client's row id is Regen): BuffDefinitions 245 (shrine_regen) for 45 s. The player's
        // BuffTemplates row 385 is mana_regeneration_rate_+% 200, life_regeneration_rate_per_minute_% 400, has_shrine_effect 1
        // the monster row 386 is mana_regeneration_rate_+% 200, life_regeneration_rate_per_minute_% 400, has_shrine_effect 1
        // so the monsters around the unclaimed shrine get their own values. Stats and duration come from the Shrines
        // row; the definition adds the shared aura.
        Register(new ShrineDefinition
        {
            Id = "Regen",
            Name = "Replenishing Shrine",
            BeforeActivation = [() => new MonsterAuraEffect()],
        });

        // Brutal: BuffDefinitions 242 (shrine_brutal) for 45 s. The player's BuffTemplates row
        // 387 is damage_+% 50, global_knockback 1, base_stun_duration_+% 30, extra_gore 1, has_shrine_effect 1
        // the monster row 388 is damage_+% 50, global_knockback 1, base_stun_duration_+% 30, extra_gore 1, has_shrine_effect 1
        // so the monsters around the unclaimed shrine get their own values. Stats and duration come from the Shrines
        // row; the definition adds the shared aura.
        Register(new ShrineDefinition
        {
            Id = "Brutal",
            Name = "Brutal Shrine",
            BeforeActivation = [() => new MonsterAuraEffect()],
        });

        // Echoing (the client's row id is Multi): BuffDefinitions 256 (shrine_echoing) for 45 s. The player's
        // BuffTemplates row 404 is echoing_shrine_cast_speed_+%_final 100, echoing_shrine_attack_speed_+%_final 100, skill_repeat_count 1, has_shrine_effect 1
        // the monster row 403 is echoing_shrine_cast_speed_+%_final 100, echoing_shrine_attack_speed_+%_final 100, skill_repeat_count 1, has_shrine_effect 1
        // Stats and duration come from the Shrines row; the definition adds the shared aura.

        Register(new ShrineDefinition
        {
            Id = "Multi",
            Name = "Echoing Shrine",
            BeforeActivation = [() => new MonsterAuraEffect()],
        });

        // Resonating: BuffDefinitions 1312 (shrine_resonating) for 45 s. The player's BuffTemplates row
        // 422 is chance_to_gain_random_standard_charge_on_hit_% 20, power_frenzy_or_endurance_charge_on_kill_% 60, has_shrine_effect 1, max_power_charges 1, max_endurance_charges 1, max_frenzy_charges 1
        // the monster row 423 is chance_to_gain_random_standard_charge_on_hit_% 100, power_frenzy_or_endurance_charge_on_kill_% 0, has_shrine_effect 1, max_power_charges 0, max_endurance_charges 0, max_frenzy_charges 0
        // Stats and duration come from the Shrines row; the definition adds the shared aura.
        Register(new ShrineDefinition
        {
            Id = "Resonating",
            Name = "Resonating Shrine",
            BeforeActivation = [() => new MonsterAuraEffect()],
        });

        // Gloom: BuffDefinitions 1313 (shrine_gloom) for 45 s. The player's BuffTemplates row
        // 424 is non_chaos_damage_to_add_as_chaos_damage_% 10, explode_enemies_for_25%_life_as_chaos_on_kill_chance_% 40, has_shrine_effect 1
        // the monster row 425 is non_chaos_damage_to_add_as_chaos_damage_% 10, explode_enemies_for_25%_life_as_chaos_on_kill_chance_% 0, has_shrine_effect 1
        // Stats and duration come from the Shrines row; the definition adds the shared aura.
        Register(new ShrineDefinition
        {
            Id = "Gloom",
            Name = "Gloom Shrine",
            BeforeActivation = [() => new MonsterAuraEffect()],
        });

        // Divine (the Shrines row GodMode): BuffDefinitions 261 (shrine_godmode) for 20 s. The player's
        // BuffTemplates row 413 is base_cannot_be_damaged 1, has_shrine_effect 1
        // the monster row 414 is base_cannot_be_damaged 1, has_shrine_effect 1
        // Stats and duration come from the Shrines row; the definition adds the shared aura.
        Register(new ShrineDefinition
        {
            Id = "GodMode",
            Name = "Divine Shrine",
            BeforeActivation = [() => new MonsterAuraEffect()],
        });

        // Cold (the Freezing shrine): Ice Nova on the statue while it stands unclaimed, then following the player for its
        // 45 s buff. Shrines row Cold, BuffTemplates 394, GrantedEffects 101 ShrineIceNova and 102 ShrineIceNovaPlayer,
        // daemon MonsterVarieties 2920.
        Register(new ShrineDefinition
        {
            Id = "Cold",
            Name = "Freezing Shrine",
            BeforeActivation = [() => new IceNovaEffect()],
            AfterActivation = [() => new IceNovaEffect()],
        });

        // Lightning: Shock Novas and shocked ground around the shrine while it stands unclaimed, then around the player
        // while its buff is up.
        Register(new ShrineDefinition
        {
            Id = "Lightning",
            BeforeActivation = [() => new ShockNovaEffect()],
            AfterActivation = [() => new ShockNovaEffect()],
        });
    }

    /// <summary>The effects a shrine of this row runs while it is unclaimed: the registered ones, then the data row's
    /// own guard packs when it names any (the standard map shrines name none).</summary>
    public static IReadOnlyList<IShrineEffect> Before(ShrineCatalog.ShrineDefinition row)
    {
        var effects = new List<IShrineEffect>();
        if (Find(row.Id) is { } registered)
            foreach (var make in registered.BeforeActivation) effects.Add(make());
        if (row.GuardPacks.Count > 0) effects.Add(new GuardedPacksEffect());
        return effects;
    }

    /// <summary>The effects a shrine of this row runs while the player's buff is active.</summary>
    public static IReadOnlyList<IShrineEffect> After(ShrineCatalog.ShrineDefinition row) =>
        Find(row.Id) is { } registered ? [.. registered.AfterActivation.Select(make => make())] : [];
}
