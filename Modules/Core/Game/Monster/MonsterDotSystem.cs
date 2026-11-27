/// <summary>
/// The monsters' damage over time (ignites, bleeds, poisons, contagions, essence drains) ticks once for the area, every
/// frame, instead of on the tick of the area's first player (which stopped while that player paused or left).
/// The tick still sends and credits as that player (the area's view), as a task of theirs; the next waits for it.
/// </summary>
public static partial class Game
{
    public static bool AreaRunsGame(Player player) => AreaSetup.Runs(player);

    public static void StartMonsterDamageOverTime(GameArea area, Player player, object source)
    {
        var frames = player.Frames;
        Task pass = Task.CompletedTask;
        area.Systems.Add("monster damage over time", AreaPhase.Ailments, a =>
        {
            if (!(a.HasActiveDots || HasFrigidBondDamage(a) || HasSearingBondDamage(a) || HasPestilentDamage(a)
                    || HasSoulrendDamage(a) || HasFleshrendDamage(a) || HasTrarthusBurn(a))
                || !pass.IsCompleted || !CurrentGameWork || frames.View is not { } view || view.Closed || view.LeavingArea
                || view.Paused || !view.Connection.PlayerSent || view.PlayerUpdatesState is not PlayerUpdatesState updates
                || !PlayerUpdatesCurrent(view, updates)) return;
            var player = view;
            pass = AsPlayer(view, updates, async () =>
            {
                await BurnIgnited(view);
                await BleedMonsters(view);
                await TickPoisons(view);
                await TickMinionDecays(view);
                await TickFrigidBondDamage(view);
                await TickSearingBondDamage(view);
                await TickTrarthusBurn(view);
                await TickContagions(view);
                await TickEssenceDrains(view);
            });
        }, source: source);
    }
}
