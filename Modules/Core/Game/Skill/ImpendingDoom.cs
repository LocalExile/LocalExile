using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const string DoomBlastEffect = "DoomBlast";
    const string RoyaleDoomBlastEffect = "ViciousHexExplosionRoyale";

    static bool IsDoomBlastEffect(string? effect) => effect is DoomBlastEffect or RoyaleDoomBlastEffect;

    static MonsterCurses.EndTrigger? DoomBlastForCastLocked(Player player, SupportedGem hex)
    {
        const uint trigger = Stat.TriggerViciousHexExplosionWhenCurseEnds;
        if (hex.Stats.GetValueOrDefault(trigger) == 0
            || hex.Supports.FirstOrDefault(s => SupportHas(s, trigger)) is not { } support) return null;
        string effect = support.Effect.Id == "SupportImpendingDoomRoyale" ? RoyaleDoomBlastEffect : DoomBlastEffect;
        return SupportCompanionSourceLocked(player, hex, support, effect, "HungryLoopSupportedByImpendingDoom") is { } blast
            ? new(blast.Id, blast.Effect, CaptureGemSourceLifetimeLocked(player, blast)) : null;
    }

    static void ReleaseEndedDoomBlastsLocked(Player viewer, SpawnedMonster monster, double now)
    {
        if (monster.Curses.DrainEndTriggers() is not { } ended || monster.Dead || monster.Life == 0
            || monster.WorldHidden) return;

        var centre = monster.Walk.Position(now);
        foreach (var hex in ended)
        {
            var caster = viewer.Id == hex.Caster ? viewer
                : viewer.Instance.PlayersExcept(viewer.Id).OfType<Player>().FirstOrDefault(p => p.Id == hex.Caster);
            if (caster is { Closed: false, LeavingArea: false, Paused: false, PlayerLife: > 0 }
                && ReferenceEquals(caster.Area, viewer.Area) && hex.TriggerOnEnd is { } blast)
                TriggerDoomBlastLocked(caster, blast, centre, now);
        }
    }

    static void TriggerDoomBlastLocked(Player player, MonsterCurses.EndTrigger blast, (int X, int Y) centre, double now)
    {
        if (!GemSourceLifetimeCurrentLocked(player, blast.Skill, blast.Effect, blast.Source)
            || GemSkillInstanceDisabled(player, blast.Effect, blast.Skill, triggered: true) is not null
            || CooldownForSkill(player, blast.Skill) is not { } cooldown) return;
        var session = ProjectilesFor(player);
        if (!session.Active) return;

        int key = CooldownKey(player, blast.Effect, blast.Skill);
        var state = CooldownStateLocked(player, key, cooldown, now);
        if (!state.Ready || !PayMana(player, blast.Skill, out uint mana, out uint life)) return;
        state.TryUse(now, cooldown);
        player.CooldownSources[blast.Effect] = blast.Skill;
        player.SkillReadyAt[key] = now + state.RemainingSeconds;
        bool lockstep = player.Connection.LockstepClient;
        state.ConfirmNativeUse(state.UseVersion, blast.Skill, synchronizeProgress: !lockstep);

        var spell = SupportedSkill(player, blast.Skill);
        const string label = "Doom Blast";
        var use = (blast.Skill, blast.Effect, Name: label, spell.Level, spell.Quality, spell.SourceGem?.TypeHash ?? 0);
        var critical = CaptureSkillCriticalRoll(player, blast.Skill);
        ushort sequence = NextLockstepSequence(player);
        if (!lockstep) sequence &= 0x7fff;
        bool Current() => session.Active && player.PlayerLife > 0 && !player.Paused
            && GemSourceLifetimeCurrentLocked(player, blast.Skill, blast.Effect, blast.Source);
        session.ScheduleContact(ProjectileRuntime.Time(now), Current, () =>
        {
            session.World(player.Id, InstanceProtocol.InstantAction(player.Id, blast.Skill, centre.X, centre.Y,
                sequence: sequence, flags: lockstep ? (ushort)0x101 : (ushort)0, triggered: true),
                $"0x01ce {label} at {centre}");
            if (mana > 0 || life > 0) session.Enqueue(() => SendSkillCost(player, life, "Doom Blast cost"));
            if (!lockstep) session.Enqueue(() => SendNativeCooldownRefresh(player));
            var hit = AreaSkillHitForUse(null, player, blast.Skill, use, centre, centre, immediate: true,
                sequence: sequence, melee: false, criticalRoll: critical, triggeredCurrent: Current,
                scheduledSession: session, cryExerted: false, nearbyOnly: true);
            if (!hit.IsCompleted) throw new InvalidOperationException("A scheduled Doom Blast must not yield.");
            hit.GetAwaiter().GetResult();
        });
    }
}
