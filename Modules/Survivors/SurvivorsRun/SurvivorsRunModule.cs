using GameServer.Modding;
using static GameServer.Modules.Survivors.SurvivorsSkills;

namespace GameServer.Modules.Survivors;

public sealed class SurvivorsRunModule : IModule
{
    public void Load(IModuleContext module)
    {
        SurvivorsRunRules.Lifetime = module.Lifetime;
        module.OnChatCommand("survivors", "/survivors [start | stop | pick <1-6> | status] (Survivors league)",
            SurvivorsRunRules.SurvivorsCommand);
        module.OnAreaGenerated(player => SurvivorsRunRules.EnterArea(ConnOf(player)));
        module.OnPlayerLeaving((player, reason) =>
        {
            var conn = ConnOf(player);
            if (conn.Closed) SurvivorsRunRules.PendingRuns.TryRemove(conn, out _);
            SurvivorsRunRules.Release(conn);
            return Task.CompletedTask;
        });
        module.Lifetime.Own(GameEvents.Kill.Add("SurvivorsRun", kill =>
        {
            if (kill.Monster.Hostile) StateOf(kill.Conn).Survivors?.RecordKill(kill.Monster.Rarity);
        }));
        module.Lifetime.Own(GameEvents.LootAllowed.Add("SurvivorsRun", conn => StateOf(conn).Survivors is null));
        module.Lifetime.Own(GameEvents.IncomingDamage.Add("SurvivorsRun", (conn, damage) =>
            StateOf(conn).Survivors is { } run ? SurvivorsRules.IncomingDamage(damage, run.IncomingDamagePercent) : damage));
        module.Lifetime.Own(GameEvents.TravelRefusal.Add("SurvivorsRun", SurvivorsRunRules.TravelRefusal));
        module.Lifetime.Own(GameEvents.TransferResolved.Add("SurvivorsRun", SurvivorsRunRules.TransferResolved));
        module.Lifetime.Own(GameEvents.Resurrected.Add("SurvivorsRun", SurvivorsRunRules.Resurrected));
        module.Lifetime.Own(new OnUnload(SurvivorsRunRules.ReleaseAll));
    }

    sealed class OnUnload(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
