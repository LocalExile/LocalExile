// Kills by what the player owns. A kill by the player's own totem, trap or mine, or by their minion, reaches
// GameEvents.Kill with PlayerKill false, and the effects that read PlayerKill are not changed here. Those kill sites
// mark the monster before its kill is committed, so an on-kill rule whose own reference counts such kills as its
// owner's can ask KilledByPlayerOrOwned. kill.Conn is the owner.
public static partial class Game
{
    static readonly RuleTable<SpawnedMonster, object> OwnedKills = new();
    static readonly object Owned = new();

    /// <summary>Marks that the player's own totem, trap, mine or minion dealt the killing hit (<paramref name="life"/>
    /// 0). Under the Monsters lock, before the kill is committed.</summary>
    public static void OwnedKillLocked(SpawnedMonster m, uint life)
    {
        if (life == 0) OwnedKills.AddOrUpdate(m, Owned);
    }

    /// <summary>The player's own kill, or their totem's, trap's, mine's or minion's.</summary>
    public static bool KilledByPlayerOrOwned(KillContext kill) =>
        kill.PlayerKill || OwnedKills.TryGetValue(kill.Monster, out _);
}
