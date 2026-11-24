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
    public static async Task Finish(Player player, uint beforeId, SpawnedMonster before, uint formedId, SpawnedMonster formed)
    {
        var area = player.Area;
        var frames = player.Frames;
        var world = player.Instance?.World;
        double until = AreaTime(player) + TransformSeconds;
        try
        {
            while (true)
            {
                var active = frames.View ?? player;
                bool activeInArea = !active.Closed && !active.LeavingArea && ReferenceEquals(active.Area, area);
                if (!CurrentGameWork || !activeInArea) break;
                if (AreaTime(active) >= until && !active.Paused) break;
                await AreaWait(PlayerUpdateRole.MonsterAction, 50);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            formed.EntranceGrace = false;
            if (area.Monsters.TryGetValue(formedId, out var current) && ReferenceEquals(current, formed))
                current.EntranceGrace = false;
        }
        if (!CurrentGameWork) return;
        if (beforeId == 0 || !area.Monsters.TryGetValue(beforeId, out var body) || !ReferenceEquals(body, before)) return;
        body.CorpseConsumed = true;
        area.Monsters.Remove(beforeId);
        world?.MonsterOrigins.TryRemove(beforeId, out _);
        player.ReleaseCorpseBody(beforeId, body);
        foreach (var peer in frames.Players()) peer.ReleaseCorpseBody(beforeId, body);
        var view = frames.View ?? player;
        if (view.Closed || view.LeavingArea || !ReferenceEquals(view.Area, area)) return;
        view.ReleaseCorpseBody(beforeId, body);
        await SendWorldRemoval(view, beforeId, $"0x02bd {before.Label}'s body leaves: its next form has taken shape");
    }
}
