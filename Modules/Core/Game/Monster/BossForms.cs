using static Game;

/// <summary>
/// A boss that dies into its next form (Quests' SecondForm: Merveil, Dominus, Avarius into Innocence). The new form
/// stands where the old one fell: for the change's length it cannot be hurt, and when the change is done the old form's
/// body leaves instead of lying under the new one.
/// </summary>
public static class BossForms
{
    /// <summary>How long the change takes (seconds, ours: the forms' own clips are not timed here).</summary>
    public static double TransformSeconds = 2;

    /// <summary>Ends the change: the new form can be hurt, the old form's body leaves.</summary>
    public static async Task Finish(Player player, uint beforeId, SpawnedMonster before, uint formedId)
    {
        double until = AreaTime(player) + TransformSeconds;
        while (AreaTime(player) < until || player.Paused)
        {
            if (!CurrentGameWork || player.Closed || player.LeavingArea) break;
            await AreaWait(PlayerUpdateRole.MonsterAction, 50);
        }
        if (player.Area.Monsters.TryGetValue(formedId, out var formed)) formed.EntranceGrace = false;
        if (!CurrentGameWork || player.Closed || player.LeavingArea) return;
        if (beforeId == 0 || !player.Area.Monsters.TryGetValue(beforeId, out var body) || !ReferenceEquals(body, before)) return;
        body.CorpseConsumed = true;
        player.Area.Monsters.Remove(beforeId);
        await SendWorldRemoval(player, beforeId, $"0x02bd {before.Label}'s body leaves: its next form has taken shape");
    }
}
