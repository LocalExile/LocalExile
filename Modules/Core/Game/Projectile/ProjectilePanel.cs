using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static bool ProxySkillChanged(GrantedSkill sent, GrantedSkill next) =>
        sent.EffectsPerLevelRow != next.EffectsPerLevelRow || sent.StatSetsPerLevelRow != next.StatSetsPerLevelRow
        || !sent.Stats!.SequenceEqual(next.Stats!);

    static void ApplyProxyAttackStats(ClientState conn, SupportedGem gem, Dictionary<uint, int> payload,
        double seconds, bool projectiles)
    {
        var weapon = WeaponFor(conn);
        var player = CharacterPlayerStats(conn);
        foreach (var (key, value) in WeaponStats(conn, weapon, player)) payload[key] = value;
        payload[StatKeys.MainHandAttackSpeedPct] = (int)Math.Round(100 * (weapon.AttackMs / (seconds * 1000) - 1));
        payload[StatKeys.HundredTimesAttacksPerSecond] = (int)Math.Round(100 / seconds);
        if (Stats.Key("main_hand_weapon_type") is uint weaponType)
            payload[weaponType] = WeaponTypeStats.Of(ActiveMainHand(conn)?.Type?.Class);
        if (!projectiles) return;
        var inputs = MergeSkillStats(player, gem.Stats);
        ApplyProjectilePanelStats(gem.Effect, payload, inputs, ArrowsFor(conn, gem.Effect));
        foreach (string name in new[] { "no_barrage_projectile_spread", "barrage_support_projectile_spread_+%" })
            if (Stats.Key(name) is uint key && inputs.TryGetValue(key, out int value)) payload[key] = value;
    }

    // These are native description inputs, projected only onto the outgoing skill record.
    // Never put final counts back into supported/raw stats and resolve quantity modifiers twice.
    public static void ApplyProjectilePanelStats(int effect, Dictionary<uint, int> stats) =>
        ApplyProjectilePanelStats(effect, stats, stats, ArrowsForSkill(effect));

    static ProjectileProfile FiredProjectiles(ProjectileProfile profile, Func<string, int> get) =>
        get("projectiles_rain") != 0 && profile.EndEmission is { } rain ? rain.Child : profile;

    static int BarrageTimePercent(ClientState conn, int effect, Func<string, int> get, int perProjectile)
    {
        if (perProjectile == 0) return 0;
        var groundRain = ArrowRainDefinition(SkillsFor(conn).EffectId(effect));
        if ((groundRain?.Parameters ?? ResolveProjectilePanelParameters(effect)) is not { } parameters) return 0;
        var profile = parameters.Resolve(get, 1, groundRain is not null || ArrowsFor(conn, effect));
        return perProjectile * FiredProjectiles(profile, get).Count;
    }

    static void ApplyProjectilePanelStats(int effect, Dictionary<uint, int> stats, IReadOnlyDictionary<uint, int> inputs, bool bow)
    {
        var groundRain = ArrowRainDefinition(Skills.EffectId(effect));
        if ((groundRain?.Parameters ?? ResolveProjectilePanelParameters(effect)) is not { } parameters) return;
        int Get(string name) => Stats.Key(name) is uint key ? inputs.GetValueOrDefault(key) : 0;
        ProjectileProfile profile;
        bool arrows = groundRain is not null || bow;
        try { profile = parameters.Resolve(Get, 1, arrows); }
        catch (InvalidOperationException ex)
        {
            // An unavailable recipe must not prevent the rest of the skill list from reaching the client.
            Console.WriteLine($"[gateway] {Skills.EffectId(effect)} projectile panel unavailable: {ex.Message}");
            return;
        }
        // Arrow Nova's record describes the volley that leaves the landing cell, not its single payload arrow: the
        // client builds that volley from total_number_of_projectiles_to_fire, and 1 left it with one arrow
        // (live 2026-11-04: one SplitArrowDefault child instead of the nine the stats resolve to).
        profile = FiredProjectiles(profile, Get);
        int total = profile.Count;
        int throwTime = Get("support_barrage_trap_and_mine_throwing_time_+%_final_per_projectile_fired");
        if (throwTime != 0) Put("virtual_trap_and_mine_throwing_time_+%_final", throwTime * total);
        Put("total_number_of_projectiles_to_fire", total);
        Put("virtual_total_number_of_projectiles", total);
        if (arrows)
        {
            Put("total_number_of_arrows_to_fire", total);
            Put("virtual_total_number_of_arrows", total);
        }
        if (groundRain is not null) return; // Rain arrows land at their destination without a flight interaction.
        Put("projectile_number_of_targets_to_pierce", profile.CannotPierce || profile.Pierce < 0 ? 0 : profile.Pierce);
        Put("virtual_always_pierce", !profile.CannotPierce && profile.Pierce < 0 ? 1 : 0);
        Put("virtual_projectiles_cannot_pierce", profile.CannotPierce ? 1 : 0);
        // The native generic pierce rows require both competing arrow aliases to be zero.
        // Their applicable contributions are already included in the profile's combined budget.
        Put("arrow_number_of_targets_to_pierce", 0);
        Put("arrows_always_pierce", 0);
        // Ground bounces are the skill's chains too (Rolling Magma's display_projectiles_chain_when_impacting_ground): the
        // client draws its own orb's bounces from this count, and with 0 it stopped at the first landing (live 2026-11-01).
        Put("virtual_number_of_chains_for_projectiles", profile.CannotChain ? 0 : profile.Chain + profile.GroundChains);
        Put("virtual_number_of_forks_for_projectiles_final", profile.CannotFork ? 0 : profile.Fork);
        Put("virtual_projectiles_cannot_chain", profile.CannotChain ? 1 : 0);
        Put("virtual_projectiles_cannot_fork", profile.CannotFork ? 1 : 0);
        if (Get("kinetic_bolt_forks_apply_to_zig_zags") != 0 || profile.ZigZags > 0)
        {
            Put("kinetic_bolt_number_of_zig_zags", profile.ZigZags);
            if (Get("kinetic_bolt_forks_apply_to_zig_zags") != 0)
                Put("theoretical_number_of_forks", Math.Max(Get("theoretical_number_of_forks"), profile.Fork));
        }
        Put("virtual_projectile_number_to_split", profile.CannotSplit ? 0 : profile.Split);
        Put("projectiles_cannot_split", profile.CannotSplit ? 1 : 0);
        Put("projectiles_return", !profile.CannotReturn && profile.Returns ? 1 : 0);
        Put("projectile_return_%_chance", profile.CannotReturn ? 0 : profile.ReturnChancePercent);
        Put("skill_projectiles_cannot_return", profile.CannotReturn ? 1 : 0);
        Put("returning_projectiles_always_pierce", !profile.CannotReturn && !profile.CannotPierce
            && (profile.Returns || profile.ReturnChancePercent > 0) && profile.PierceAllOnReturn ? 1 : 0);
        if (profile.NativeVolley?.Nova == true) Put("projectiles_nova", 1);
        parameters.PanelStats?.Invoke(profile, stats);

        void Put(string name, int value) { if (Stats.Key(name) is uint key) stats[key] = value; }
    }
}
