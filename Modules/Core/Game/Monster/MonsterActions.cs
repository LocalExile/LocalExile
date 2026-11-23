using static Game;

/// <summary>
/// One action of a monster, for a mod's development command: it plays one of the monster's own actor skills by the
/// GrantedEffects Id the client shows, at any time, without its AI choosing it. The clip is the client's and the server
/// owes no hit, so the caller sees exactly one animation.
/// <para>It only queues the action: the instance's frame sends it under its own lock, so it cannot interleave with a
/// cast the monster is already in. A mod reaches this through the module like any other game rule.</para>
/// </summary>
public static class MonsterActions
{
    /// <summary>Plays <paramref name="action"/> (a granted effect's Id, e.g. "EDSSanctumLycia2DelayedBlast", or its
    /// row, 10459) on the monster <paramref name="monsterId"/> of the player's area. False when the monster does not
    /// carry that skill, or it is no longer in the area.</summary>
    public static Task<bool> Use(Player player, uint monsterId, string action) =>
        AtlasBosses.ActorAction(player, monsterId, action);

    /// <summary>The actions the monster's own actor takes, for a command that offers them; null when it carries none.</summary>
    public static IReadOnlyList<string>? Names(Player player, uint monsterId) =>
        AtlasBosses.ActorSkillNames(player, monsterId) is { Count: > 0 } names ? names : null;
}
