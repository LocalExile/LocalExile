using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    const string GreaterKineticFluxEffect = "TriggeredSupportGreaterKineticInstability";
    const string KineticFluxEffect = "TriggeredSupportKineticInstability";
    const string KineticFluxStage = "KineticFlux";

    static void TriggerKineticFluxLocked(Player player, ProjectileSession session, ushort skillId, SupportedGem parent,
        bool critical, bool killed, (int X, int Y) at, double now)
    {
        const uint critStat = Stat.SupportKineticInstabilityChanceToCreateInstabilityOnCriticalStrikePct;
        const uint killStat = Stat.SupportKineticInstabilityChanceToCreateInstabilityOnKillPct;
        if (!(critical && parent.Stats.GetValueOrDefault(critStat) > 0 || killed && parent.Stats.GetValueOrDefault(killStat) > 0)
            || !session.Active || player.PlayerLife == 0 || player.Paused || player.Closed || player.LeavingArea
            || !SameTriggerSource(parent, SupportedSkill(player, skillId))) return;

        if (critical) Use(critStat, GreaterKineticFluxEffect, "HungryLoopSupportedByGreaterKineticInstability", "crit");
        if (killed) Use(killStat, KineticFluxEffect, "HungryLoopSupportedByKineticInstability", "kill");

        void Use(uint stat, string effect, string wornSupport, string reason)
        {
            int chance = parent.Stats.GetValueOrDefault(stat);
            if (chance <= 0 || parent.Supports.FirstOrDefault(s => SupportHas(s, stat)) is not { } support
                || SupportCompanionSourceLocked(player, parent, support, effect, wornSupport) is not { } source
                || GemSkillInstanceDisabled(player, source.Effect, source.Id, triggered: true) is not null) return;

            var cooldown = CooldownForSkill(player, source.Id);
            int key = CooldownKey(player, source.Effect, source.Id);
            var state = cooldown is { } plan ? CooldownStateLocked(player, key, plan, now) : null;
            if (state is { Ready: false } || !RandomRolls.Chance(chance, 100, AreaRng(player))) return;
            var gem = SupportedSkill(player, source.Id);
            int Own(uint k) => gem.Stats.GetValueOrDefault(k);
            int limit = Own(Stat.KineticInstabilityMaximumNumberOfInstabilityOrbsAllowed);
            int radius = SkillRadius(player, gem.Stats, spell: false, source.Effect);
            double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), CharacterPlayerStats(player).GetValueOrDefault,
                Own, DurationKinds(player, source.Effect));
            if (limit <= 0 || radius <= 0 || duration <= 0) return;
            if (state is not null && cooldown is { } recovery)
            {
                if (!state.TryUse(now, recovery)) return;
                player.CooldownSources[source.Effect] = source.Id;
                player.SkillReadyAt[key] = now + state.RemainingSeconds;
                state.ConfirmNativeUse(state.UseVersion, source.Id, synchronizeProgress: true);
                session.Enqueue(() => SendNativeCooldownRefresh(player));
            }

            var use = (source.Id, source.Effect, Name: "Kinetic Flux", gem.Level, gem.Quality, source.Gem);
            ushort sequence = NextLockstepSequence(player);
            var captured = CaptureGemProjectile(player, session, source.Id, use, at,
                CaptureSkillCriticalRoll(player, source.Id), sequence, from: at, capturedSkill: gem);
            var offense = (ProjectileOffense)captured.Payload!;
            captured = captured with { Payload = offense with { MirageSource = null, CriticalTrigger = null, KineticFluxDriver = null } };
            session.PlantKineticAnomaly(captured, ProjectileSession.Vector(at), radius, duration, limit, KineticFluxStage);
            if (ServerTrace.Skills)
                Console.WriteLine($"[gateway] {player.Connection.Remote} kinetic flux trigger reason={reason} at={session.Runtime.Now} cell={at} uses={(state is null ? -1 : state.Uses - state.SpentUses)}");
        }
    }
}
