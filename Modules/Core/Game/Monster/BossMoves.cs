using System.Collections.Concurrent;
using System.Text.RegularExpressions;

/// <summary>
/// The moves of a unique monster's kit the skill types do not mark as a travel: a boss's own teleport (Merveil's, a
/// "…Teleport" or "…Blink" skill) and a monster-only movement skill (ActiveSkillType Movement without Travel: a warp,
/// a flicker, a monster leap). MonsterSkills.ShapeOf plays them as a travel: the action toward the player, the monster
/// where it lands, its hit around that if it deals damage. Without them a boss whose kit is one ranged spell stood at
/// its spawn point for the whole fight (Merveil, the Siren).
/// </summary>
public static partial class BossMoves
{
    // Steps of a scripted encounter, not something to do in a fight (a teleport of the player, a trip to the arena's
    // centre, a start or portal-in, a hide), and the ids BossKits.Effects leaves out for what they drive.
    static readonly string[] Scripted =
    [
        "TeleportPlayer", "ToCentre", "NoTeleport", "TeleportTower", "StartFight", "PortalIn", "Hide", "Detonation",
        "MirrorArrow", "Burrow", "TeleportMarker", "DeathTeleport",
        "Daemon", "MemoryGame", "Bond", "Mark", "Souls", "EmptyAction", "Emerge", "Retreat",
    ];

    static readonly ConcurrentDictionary<int, (bool Move, bool Teleport)> Kinds = new();

    static (bool Move, bool Teleport) KindOf(SkillCatalog catalog, int effect) => Kinds.GetOrAdd(effect, e =>
    {
        string id = catalog.EffectId(e) ?? "";
        if (id.Length == 0 || Scripted.Any(w => id.Contains(w, StringComparison.Ordinal))) return (false, false);
        // A move the monster makes itself: not a hold (Cyclone), a clone (Blink Arrow) or a skill set off for it.
        if (catalog.IsChannelled(e) || catalog.HasType(e, "Minion") || catalog.CastsOnDeath(e) || catalog.IsAura(e)) return (false, false);
        bool projectile = catalog.IsProjectile(e) || id.Contains("Projectile", StringComparison.Ordinal) || id.Contains("Shot", StringComparison.Ordinal);
        if ((id.Contains("Teleport", StringComparison.Ordinal) || id.EndsWith("Blink", StringComparison.Ordinal)) && !projectile) return (true, true);
        return (catalog.IsMovement(e), false);
    });

    /// <summary>Skills that fire several projectiles one after another from one cast, and how far apart (seconds). The
    /// tables give one projectile a cast; the barrage is the official server's script (wiki: Merveil's "barrage of ice
    /// bolts"), so the numbers are ours.</summary>
    public static readonly Dictionary<string, (int Count, double Seconds)> Barrages = new(StringComparer.Ordinal)
    {
        ["MerveilIceSpear"] = (4, .18),
    };

    /// <summary>The barrage a skill fires, or null for a single shot.</summary>
    public static (int Count, double Seconds)? BarrageOf(SkillCatalog catalog, int effect) =>
        catalog.EffectId(effect) is { } id && Barrages.TryGetValue(id, out var barrage) && barrage.Count > 1 ? barrage : null;

    /// <summary>A boss skill that raises a hazard under the player which strikes a moment later: the art, the wait, the
    /// radius it hits within, the stat that says how many come up (0: one) and how far around the first the others
    /// scatter (cells). The object and its timing are the official server's; the client has only the art
    /// (MiscObjects MerveilGeyser), so every number is ours.</summary>
    /// <param name="Count">How many come up when the skill's CountStat is 0 or absent.</param>
    /// <param name="Pulse">While it stays (the skill's base_skill_effect_duration, else StaySeconds) it strikes again
    /// this often (seconds).</param>
    /// <param name="Cooldown">The least cooldown the kit gives it: a field of them is not raised every turn.</param>
    public sealed record StrikeRow(string Art, double Seconds, double Radius, uint CountStat, int Count, double Spread,
        double StaySeconds, double Pulse, double Cooldown);

    public static readonly Dictionary<string, StrikeRow> Strikes = new(StringComparer.Ordinal)
    {
        // One under the player and a few around; ten across the arena.
        ["MerveilGeyser"] = new(GeyserArt, 1.0, 6, 0, 4, 22, 10, 1, 4),
        ["MerveilMultiGeyser"] = new(GeyserArt, 1.2, 6, Stat.MerveilNumberOfGeysers, 10, 55, 6, 1, 9),
    };

    const string GeyserArt = "Metadata/Effects/Spells/monsters_effects/act1/merveil/water_geyser/watertest.ao";

    /// <summary>The delayed strike a kit skill places instead of a plain hit, or null.</summary>
    public static (MonsterDelayedStrike Strike, double Radius, int Count, double Spread, int Pulses, double Pulse, double Cooldown)? StrikeOf(
        SkillCatalog catalog, int effect, byte level)
    {
        if (catalog.EffectId(effect) is not { } id || !Strikes.TryGetValue(id, out var row)) return null;
        var stats = Game.GolemData.Skill(catalog, effect, level).Stats;
        int count = row.CountStat != 0 && stats.GetValueOrDefault(row.CountStat) is > 0 and var named ? Math.Min(named, 30) : Math.Max(1, row.Count);
        double stay = stats.GetValueOrDefault(Stat.BaseSkillEffectDuration) is > 0 and var ms ? ms / 1000d : row.StaySeconds;
        int pulses = row.Pulse > 0 ? (int)Math.Floor(Math.Max(0, stay - row.Seconds) / row.Pulse) : 0;
        return (new(row.Seconds, row.Art, row.Art, stay, false), row.Radius, count, row.Spread, pulses, row.Pulse, row.Cooldown);
    }

    /// <summary>Where the other hazards of one use come up: scattered within <paramref name="spread"/> cells of the
    /// first, on walkable ground.</summary>
    public static List<(int X, int Y)> StrikeSpots((int X, int Y) at, int count, double spread, Random random, AreaLayout layout)
    {
        List<(int X, int Y)> spots = [];
        for (int tries = 0; spots.Count < count && tries < count * 6; tries++)
        {
            // Evenly over the disc, not bunched at its centre.
            double turn = random.NextDouble() * Math.Tau, far = Math.Max(6, spread * Math.Sqrt(random.NextDouble()));
            var spot = (at.X + (int)Math.Round(Math.Cos(turn) * far), at.Y + (int)Math.Round(Math.Sin(turn) * far));
            if (layout.WalkableFor(spot.Item1, spot.Item2, 0)) spots.Add(spot);
        }
        return spots;
    }

    /// <summary>A teleport or a monster-only movement skill a kit plays as a travel.</summary>
    public static bool IsMove(SkillCatalog catalog, int effect) => KindOf(catalog, effect).Move;

    /// <summary>A teleport: the kit gives it a longer cooldown when the tables give none.</summary>
    public static bool IsTeleport(SkillCatalog catalog, int effect) => KindOf(catalog, effect).Teleport;

    /// <summary>The cooldown a kit gives a move the tables give none (seconds, ours): a boss that teleports every free
    /// turn only blinks around the player and never casts.</summary>
    public const double TeleportCooldown = 8, MoveCooldown = 6;
}
