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

    /// <summary>A teleport or a monster-only movement skill a kit plays as a travel.</summary>
    public static bool IsMove(SkillCatalog catalog, int effect) => KindOf(catalog, effect).Move;

    /// <summary>A teleport: the kit gives it a longer cooldown when the tables give none.</summary>
    public static bool IsTeleport(SkillCatalog catalog, int effect) => KindOf(catalog, effect).Teleport;

    /// <summary>The cooldown a kit gives a move the tables give none (seconds, ours): a boss that teleports every free
    /// turn only blinks around the player and never casts.</summary>
    public const double TeleportCooldown = 8, MoveCooldown = 6;
}
