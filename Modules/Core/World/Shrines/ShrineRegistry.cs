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

        // Lightning: Shock Novas and shocked ground around the player while its buff is up.
        Register(new ShrineDefinition { Id = "Lightning", AfterActivation = [() => new ShockNovaEffect()] });
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
