using GameServer.Core.Protocol.Instance;

/// <summary>
/// Monsters with life regeneration (of the Hydra) regain life: every 15 frames (about 0.5 s) the area adds it and every
/// player is sent the new life (S→C 0x1b1) of each one that changed, as for hits. The area's system, once for the area:
/// each connection's loop used to step it, so in an instance of two players the monsters regenerated twice as fast.
/// </summary>
public static class MonsterRegeneration
{
    const int EveryFrames = 15;

    public static void Start(GameArea area, Player player, object source)
    {
        // Capture area-scoped handles (never the joining Player, who may leave while others stay in the instance).
        var frames = player.Frames;
        var instance = player.Instance;
        double last = double.NaN;
        area.Systems.Add("monster regeneration", AreaPhase.Recovery, a =>
        {
            double now = a.Time, seconds = double.IsNaN(last) ? 0 : now - last;
            last = now;
            if (seconds <= 0) return;
            var living = a.SyncLivingMonsters();
            if (living.Count > 0)
            {
                Game.RegenerateMonstersLocked(living, seconds, now, OnHitPct(instance, frames, now, seconds));
                for (int i = 0; i < living.Count; i++)
                {
                    var (id, m) = living[i];
                    Game.StepMonsterEnergyShieldLocked(a, id, m, seconds, now);
                }
                Game.StepMonsterChargesLocked(a, living, now, frames.Rng);
            }
            Game.StepMonsterAurasLocked(a, frames, living, seconds, now);
        }, every: EveryFrames, source: source);
    }

    // Devout of Ryslatha's enemy regeneration on hit (Pools.EnemyRegenerationOnHitPct) of a player who hit an enemy in the
    // last 4 s; with several, the strongest.
    static int OnHitPct(InstanceState? instance, AreaFrames frames, double now, double seconds)
    {
        int pct = 0;
        var players = instance is null
            ? (IEnumerable<Player>)frames.Players()
            : Program.InGame.Keys.Where(c => ReferenceEquals(c.Instance, instance));
        foreach (var player in players)
            if (now - seconds - player.LastEnemyHitAt <= Game.RecentlySeconds && player.Pools.EnemyRegenerationOnHitPct is var p
                && Math.Abs(p) > Math.Abs(pct))
                pct = p;
        return pct;
    }
}
