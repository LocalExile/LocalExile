using GameServer.Modding;
using static GameServer.Modules.Survivors.SurvivorsSkills;

namespace GameServer.Modules.Survivors;

public sealed class SurvivorsRunModule : IModule
{
    public void Load(IModuleContext module)
    {
        SurvivorsRunRules.Lifetime = module.Lifetime;
        GameEvents.Command(module.Manifest.Id, "survivors", "/survivors [start | stop | pick <1-6> | status] (Survivors league)", SurvivorsRunRules.SurvivorsCommand);
        GameEvents.AreaGenerated.Add(module.Manifest.Id, conn => SurvivorsRunRules.EnterArea(conn));
        GameEvents.PlayerLeaving.Add(module.Manifest.Id, (conn, reason) =>
        {
            if (conn.Closed) SurvivorsRunRules.PendingRuns.TryRemove(conn, out _);
            SurvivorsRunRules.Release(conn);
            return Task.CompletedTask;
        });
        GameEvents.Kill.Add(module.Manifest.Id, kill =>
        {
            if (!kill.Monster.Hostile || StateOf(kill.Conn).Survivors is not { } run) return;
            run.RecordKill(kill.Monster.Rarity);
            // Experience waits on the ground as an orb; the run tick collects it when the player passes near.
            int experience = SurvivorsRules.ExperienceForKill(kill.Monster.Rarity);
            var (remote, stream, ciphers) = kill.Conn.Wire;
            kill.Later(() => SurvivorsDropOrb(remote, stream, ciphers, kill.Conn, kill.At, experience));
            // A share of kills leave a death ground zone: a DOT that lands under the monster or under the player.
            if (Random.Shared.NextDouble() < SurvivorsRules.DeathZoneChance)
                kill.Later(() => SurvivorsDropDeathZone(remote, stream, ciphers, kill.Conn, kill.At));
        });
        GameEvents.LootAllowed.Add(module.Manifest.Id, conn => StateOf(conn).Survivors is null);
        GameEvents.IncomingDamage.Add(module.Manifest.Id, (conn, damage) =>
            StateOf(conn).Survivors is { } run ? SurvivorsRules.IncomingDamage(damage, run.IncomingDamagePercent) : damage);
        // A run's leech must not make the player immortal: the base 20%-a-second cap out-heals the horde and lets a
        // leech build stand still, so the run lowers the cap to a trickle and cancels instant leech outright.
        GameEvents.PlayerStatSources.Add(module.Manifest.Id, (conn, sources) =>
        {
            if (StateOf(conn).Survivors is null) return sources;
            Nerf(sources, "maximum_life_leech_rate_%_per_minute", -SurvivorsRules.LifeLeechRateNerfPermyriadPerMinute);
            Nerf(sources, "leech_%_is_instant", -SurvivorsRules.InstantLeechNerf);
            Nerf(sources, "life_leech_%_is_instant", -SurvivorsRules.InstantLeechNerf);
            return sources;

            static void Nerf(Dictionary<uint, int> sources, string name, int value)
            {
                if (Stats.Key(name) is uint key) sources[key] = sources.GetValueOrDefault(key) + value;
            }
        });
        GameEvents.TravelRefusal.Add(module.Manifest.Id, SurvivorsRunRules.TravelRefusal);
        GameEvents.TransferResolved.Add(module.Manifest.Id, SurvivorsRunRules.TransferResolved);
        GameEvents.Resurrected.Add(module.Manifest.Id, SurvivorsRunRules.Resurrected);
        module.Lifetime.Own(new OnUnload(SurvivorsRunRules.ReleaseAll));
    }

    sealed class OnUnload(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
