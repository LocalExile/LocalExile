using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    const string AmbushEffect = "Ambush";
    const ushort AmbushBuffRow = 1869;
    // 3.29.3c VanishingAmbush: range virtual 0x141243a90; cursor/fallback searches in 0x141cc5610.
    const int AmbushReach = 60, AmbushCursorSearch = 15, AmbushNearbySearch = 40;

    readonly record struct AmbushTarget(uint Id, SpawnedMonster Monster);
    sealed record AmbushBuff(GameArea Area, double Until, GemSourceLifetimes.SourceLifetime Source,
        int CriticalChance, int CriticalMultiplier);

    static readonly RuleTable<Player, AmbushBuff> AmbushBuffs = new();

    static AmbushTarget? FindAmbushTarget(Player player, int effect, uint clicked, (int X, int Y) aim, double now)
    {
        if (InTown(player)) return null;
        var from = player.Walk.Position(now);
        if (clicked != 0 && clicked != player.Id)
        {
            if (!player.Area.Monsters.TryGetValue(clicked, out var monster)) return null;
            var selected = new AmbushTarget(clicked, monster);
            return AmbushLanding(player, effect, selected, from, now) is not null ? selected : null;
        }
        return Nearest(aim, AmbushCursorSearch) ?? Nearest(from, AmbushNearbySearch);

        AmbushTarget? Nearest((int X, int Y) center, int radius)
        {
            AmbushTarget? nearest = null;
            double distance = radius;
            foreach (var (id, monster) in player.Area.MonstersNear(center, radius))
            {
                double candidateDistance = Dist(center, monster.Walk.Position(now));
                if (candidateDistance > distance || candidateDistance == distance && nearest is { } best && id > best.Id) continue;
                var candidate = new AmbushTarget(id, monster);
                if (AmbushLanding(player, effect, candidate, from, now) is null) continue;
                nearest = candidate;
                distance = candidateDistance;
            }
            return nearest;
        }
    }

    static (int X, int Y)? AmbushLanding(Player player, int effect, AmbushTarget target, (int X, int Y) from, double now)
    {
        var monster = target.Monster;
        if (!ReferenceEquals(player.Area.Monsters.GetValueOrDefault(target.Id), monster)
            || monster is not { Hostile: true, Life: > 0, Dead: false, OwnerId: null, WorldHidden: false, Invulnerable: false }
            || MonsterAsleep(player, target.Id)) return null;
        var to = monster.Walk.Position(now);
        return Dist(from, to) <= AmbushReach && player.Layout.WalkableFor(to.X, to.Y, 0)
            && !player.Barrier.Contains(to) && MovementLanding(player, effect, from, to) == to ? to : null;
    }

    static bool ScheduleAmbush(Player player, ushort skill, int effect, AmbushTarget target, int castMs,
        int action, double? began, Func<bool> current)
    {
        if (CaptureGemSourceLifetimeLocked(player, skill, effect) is not { } source) return false;
        var session = ProjectilesFor(player);
        int recoveryGeneration = player.ActionInterruptionGeneration;
        double contact = (began ?? AreaTime(player)) + castMs / 1000d
            * Animations.SpellAreaRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject);
        session.ScheduleContact(ProjectileRuntime.Time(contact),
            () => recoveryGeneration == player.ActionInterruptionGeneration && current()
                && GemSourceLifetimeCurrentLocked(player, skill, effect, source), () =>
            {
                double now = ProjectileWorldTime(player);
                if (AmbushLanding(player, effect, target, player.Walk.Position(now), now) is not { } to
                    || !TryBeginSkillCooldownUse(player, skill, effect, CooldownForSkill(player, skill), instant: true,
                        () => { }, out var use, actionToken: action)
                    || use?.CommitNativeUse(player, skill) != true)
                {
                    session.Enqueue(() => SendPlayerMana(player, "Ambush: target or cost unavailable at release"));
                    return;
                }
                RecordAcceptedGemUse(player, effect, to);
                RecordNonInstantSpellUseLocked(player, effect, now);
                PhaseRunOnSkillUse(player, skill);
                ChargesOnSkillUse(player, effect, vaal: false);
                QueueFlaskTrigger(player, FlaskEnchantments.Trigger.Travel);
                LoyaltyTravelSkillTriggered(player);
                ReleaseAmbush(player, session, skill, source, target, to, now);
                session.Enqueue(async () =>
                {
                    await SendSkillCost(player, use.Paid?.Life ?? 0, "Ambush: skill cost");
                    await SendNativeCooldownRefresh(player);
                });
            });
        return true;
    }

    static void ReleaseAmbush(Player player, ProjectileSession session, ushort skill, GemSourceLifetimes.SourceLifetime source,
        AmbushTarget target, (int X, int Y) to, double now)
    {
        var own = SupportedSkill(player, skill).Stats;
        var stats = CharacterPlayerStats(player);
        int Get(uint key) => ActorScale.Saturate((long)stats.GetValueOrDefault(key) + own.GetValueOrDefault(key));
        double duration = SkillDurationModifiers.Seconds(own.GetValueOrDefault(Stat.BaseSkillEffectDuration),
            stats.GetValueOrDefault, own.GetValueOrDefault, SkillDurationModifiers.Kinds.Buff);
        double blind = SkillDurationModifiers.Seconds(own.GetValueOrDefault(Stat.BaseSecondarySkillEffectDuration),
            stats.GetValueOrDefault, own.GetValueOrDefault, SkillDurationModifiers.Kinds.Secondary)
            * Math.Max(0, 1 + Get(Stat.BlindDurationPlusPct) / 100d);
        player.Walk.Teleport(to.X, to.Y, now);
        session.World(player.Id, InstanceProtocol.Teleport(player.Id, to.X, to.Y), "Ambush landing");
        InflictMonsterBlind(player, target.Id, target.Monster, now, (100, Get(Stat.BlindEffectPlusPct), blind));
        if (duration <= 0) return;
        int multiplier = ActorScale.Saturate((long)own.GetValueOrDefault(Stat.VanishingAmbushCriticalStrikeMultiplierPlus)
            + Get(Stat.AmbushBuffCriticalStrikeMultiplierPlus));
        var buff = new AmbushBuff(player.Area, now + duration, source,
            own.GetValueOrDefault(Stat.AmbushAdditionalCriticalStrikeChancePermyriad), multiplier);
        AmbushBuffs.AddOrUpdate(player, buff);
        player.Area.Buffs.Set(player.Id, new(AmbushBuffRow, player.Id, skill, U68: Buff.Seconds(duration)));
        EveryFrameAsPlayer(player, () => AmbushBuffs.TryGetValue(player, out var active) && ReferenceEquals(active, buff), _ =>
        {
            if (!AmbushBuffCurrent(player, buff)) ClearAmbush(player);
            return Task.CompletedTask;
        });
    }

    static Action? PrepareTriggeredAmbush(Player player, ProjectileSession session, SupportedGem gem, ushort skill,
        uint targetId, (int X, int Y) aim, Func<bool> current)
    {
        double now = ProjectileWorldTime(player);
        if (CaptureGemSourceLifetimeLocked(player, skill, gem.Effect) is not { } source
            || FindAmbushTarget(player, gem.Effect, targetId, aim, now) is not { } target) return null;
        return () => session.ScheduleContact(session.Runtime.Now,
            () => current() && GemSourceLifetimeCurrentLocked(player, skill, gem.Effect, source), () =>
            {
                double at = ProjectileWorldTime(player);
                if (AmbushLanding(player, gem.Effect, target, player.Walk.Position(at), at) is { } to)
                    ReleaseAmbush(player, session, skill, source, target, to, at);
            });
    }

    static bool AmbushBuffCurrent(Player player, AmbushBuff buff) => ReferenceEquals(player.Area, buff.Area)
        && !player.Closed && !player.LeavingArea && player.PlayerLife > 0 && ProjectileWorldTime(player) < buff.Until
        && player.GemSourceLifetimes.IsCurrent(buff.Source);

    static void ClearAmbush(Player player)
    {
        if (!AmbushBuffs.TryGetValue(player, out var buff)) return;
        AmbushBuffs.Remove(player);
        buff.Area.Buffs.Clear(player.Id, AmbushBuffRow, player.Id);
    }

    static bool ConsumeAmbush(Player player, SkillCatalog.Unarmed weapon, SkillCriticalRoll attack)
    {
        if (attack.AmbushExerted) return true;
        if (!AmbushBuffs.TryGetValue(player, out var buff) || !AmbushBuffCurrent(player, buff)
            || weapon.ItemClass is not ("One Hand Sword" or "Thrusting One Hand Sword" or "One Hand Axe"
                or "One Hand Mace" or "Sceptre" or "Claw" or "Dagger" or "Rune Dagger")) return false;
        attack.SetAmbush(buff.CriticalChance, buff.CriticalMultiplier);
        ClearAmbush(player);
        return true;
    }
}
