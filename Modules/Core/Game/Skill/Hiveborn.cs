using GameServer.Core.Diagnostics;
using GameServer.Core.World;

static partial class Game
{
    const string HivebornEffect = "TriggeredSupportHiveborn";
    const uint HivebornTriggerChance = Stat.SupportTriggerGreatAvalancheOnOfferingChancePct;

    static void TriggerHivebornLocked(Player player, SupportedGem offering, (int X, int Y) centre, double now)
    {
        if (offering.Supports.FirstOrDefault(s => SupportHas(s, HivebornTriggerChance)) is not { } support
            || SupportCompanionSourceLocked(player, offering, support, HivebornEffect, "HungryLoopSupportedByHiveborn") is not { } source
            || GemSkillInstanceDisabled(player, source.Effect, source.Id, triggered: true) is not null
            || CooldownForSkill(player, source.Id) is not { } cooldown) return;

        var session = ProjectilesFor(player);
        if (!session.Active) return;
        int key = CooldownKey(player, source.Effect, source.Id);
        var state = CooldownStateLocked(player, key, cooldown, now);
        if (!state.Ready || !RandomRolls.Chance(GolemStat(offering.Stats, HivebornTriggerChance), 100, AreaRng(player))
            || !state.TryUse(now, cooldown)) return;

        var lifetime = CaptureGemSourceLifetimeLocked(player, source);
        player.CooldownSources[source.Effect] = source.Id;
        player.SkillReadyAt[key] = now + state.RemainingSeconds;
        state.ConfirmNativeUse(state.UseVersion, source.Id, synchronizeProgress: true);
        session.Enqueue(() => SendNativeCooldownRefresh(player));
        session.Enqueue(() => ReleaseTriggeredSummon(player, session, source.Id, centre,
            () => GemSourceLifetimeCurrentLocked(player, source.Id, source.Effect, lifetime)));
        if (ServerTrace.Skills)
            Console.WriteLine($"[hiveborn] offering triggered at={now:F4} centre={centre}");
    }
}
