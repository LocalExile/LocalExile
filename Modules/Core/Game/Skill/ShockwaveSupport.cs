using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string ShockwaveEffect = "SupportBluntWeaponShockwave";
    const string VoidShockwaveEffect = "TriggeredSupportVoidShockwave";
    const string VoidShockwaveArt = "Metadata/Effects/Spells/supports/void_shockwave/void_shockwave.ao";

    static void TriggerShockwaveLocked(Player player, ushort drivingSkill, SupportedGem parent,
        AttackHand hand, SkillCatalog.Unarmed weapon, int? offCritical, (int X, int Y) at, double now)
    {
        const uint chance = Stat.SupportBluntChanceToTriggerShockwaveOnHitPct;
        if (parent.Supports.FirstOrDefault(s => SupportHas(s, chance)) is not { } support
            || !SameTriggerSource(parent, SupportedSkill(player, drivingSkill))) return;
        bool isVoid = support.Effect.Id == "SupportVoidShockwave";
        string effect = isVoid ? VoidShockwaveEffect : ShockwaveEffect;
        var source = SupportCompanionSourceLocked(player, parent, support, effect,
            isVoid ? "HungryLoopSupportedByVoidShockwave" : "HungryLoopSupportedByShockwave_");
        if (source is null && !isVoid)
            source = PlayerSkillSourcesLocked(player).FirstOrDefault(row => row.Active && row.Level == support.Gem.Level
                && row.WornGrant is { } grant && grant.Host == (parent.WornGrant?.Host ?? parent.Host)
                && grant.EffectId == ShockwaveEffect && grant.ModId.StartsWith("WeaponTreeSupportShockwave", StringComparison.Ordinal));
        if (source is null || GemSkillInstanceDisabled(player, source.Effect, source.Id, triggered: true) is not null
            || CooldownForSkill(player, source.Id) is not { } cooldown) return;

        var session = ProjectilesFor(player);
        if (!session.Active || player.PlayerLife == 0 || player.Paused || player.Closed || player.LeavingArea) return;
        int key = CooldownKey(player, source.Effect, source.Id);
        var state = CooldownStateLocked(player, key, cooldown, now);
        if (!state.Ready || !RandomRolls.Chance(parent.Stats.GetValueOrDefault(chance), 100, AreaRng(player))
            || !state.TryUse(now, cooldown)) return;

        var gem = SupportedSkill(player, source.Id);
        string label = isVoid ? "Void Shockwave" : "Shockwave";
        var attack = CaptureAreaAttack(player, source.Id, gem, hand, weapon, offCritical, 1, 1, label);
        var lifetime = CaptureGemSourceLifetimeLocked(player, source);
        var critical = CaptureSkillCriticalRoll(player, source.Id);
        int epoch = player.ProjectileEpoch;
        bool lockstep = player.Connection.LockstepClient;
        ushort sequence = NextLockstepSequence(player);
        if (!lockstep) sequence &= 0x7fff;
        player.CooldownSources[source.Effect] = source.Id;
        player.SkillReadyAt[key] = now + state.RemainingSeconds;
        state.ConfirmNativeUse(state.UseVersion, source.Id, synchronizeProgress: !lockstep);
        bool Current() => CurrentGameWork && player.ProjectileActive(epoch) && player.PlayerLife > 0 && !player.Paused
            && GemSourceLifetimeCurrentLocked(player, source.Id, source.Effect, lifetime);
        session.ScheduleContact(ProjectileRuntime.Time(now), Current, () =>
        {
            session.World(player.Id, InstanceProtocol.InstantAction(player.Id, source.Id, at.X, at.Y,
                sequence: sequence, flags: lockstep ? (ushort)0x101 : (ushort)0, triggered: true),
                $"0x01ce {label} at {at}");
            if (!lockstep) session.Enqueue(() => SendNativeCooldownRefresh(player));
            var use = (source.Id, source.Effect, Name: label, gem.Level, gem.Quality, source.Gem);
            int radius = SkillRadius(player, gem.Stats, spell: false, source.Effect);
            int count = Math.Clamp(gem.Stats.GetValueOrDefault(Stat.VoidShockwaveChainsXTimes), 0, 10);
            List<(int X, int Y)>? repeats = count > 0 ? new(count) : null;

            void Collect(uint id)
            {
                if (repeats is { } points && points.Count < count && player.Area.Monsters.TryGetValue(id, out var enemy))
                    points.Add(enemy.Walk.Position(ProjectileWorldTime(player)));
            }

            void Hit((int X, int Y) centre, RetainedAreaAttack damage, Action<uint>? onHit)
            {
                var hit = AreaSkillHitForUse(null, player, source.Id, use, centre, centre, immediate: true,
                    sequence: sequence, melee: true, criticalRoll: critical, radiusOverride: radius, onHit: onHit,
                    triggeredCurrent: Current, scheduledSession: session, cryExerted: false, allowMeleeSplash: false,
                    nearbyOnly: true, retainedAttack: damage);
                if (!hit.IsCompleted) throw new InvalidOperationException("A scheduled Shockwave must not yield.");
                hit.GetAwaiter().GetResult();
            }

            Hit(at, attack, repeats is null ? null : Collect);
            if (repeats is null) return;
            double more = Math.Max(0, 1 + gem.Stats.GetValueOrDefault(Stat.ShockwaveChainedDamagePlusPctFinal) / 100d);
            var repeated = attack with { HitMore = more, AilmentMore = more, Phase = "repeat" };
            foreach (var origin in repeats)
            {
                session.ShowVoidShockwaveRepeat(origin, radius);
                Hit(origin, repeated, null);
            }
        });
    }

    public sealed partial class ProjectileSession
    {
        internal void ShowVoidShockwaveRepeat((int X, int Y) at, int radius)
        {
            if (Player.Connection.Connected)
                Effect(at.X, at.Y, VoidShockwaveArt, ProjectileRuntime.Time(1), radius);
        }
    }
}
