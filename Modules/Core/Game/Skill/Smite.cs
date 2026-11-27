using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    internal static bool IsSmite(int effect) => Skills.EffectId(effect) is "Smite" or "SmiteAltX" or "VaalSmite";

    static Task ScheduleSmite(Player player,
        ushort id, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, uint target, int recovery,
        CooldownUseTicket? ticket, SkillCriticalRoll critical, ushort sequence, double? swingAt, bool cryExerted = false, int ancestralTargets = 0, int? rallyingDamage = null)
    {
        var session = ProjectilesFor(player);
        session.Advance();
        double duration = SupportedAttackSeconds(player, id);
        double contact = GemAttackContactDelay(player, gem.Effect, duration, out _, out _);
        int action = player.ActionCount;
        var source = SupportedSkill(player, id);
        int repeats = SkillsFor(player).IsVaal(gem.Effect) ? 0 : MeleeAttackRepeats(player, id);
        double ruthless = RuthlessBlowMore(player, id, n => GolemStat(source.Stats, n));
        for (int repeat = 0; repeat <= repeats; repeat++)
        {
            int index = repeat;
            session.Runtime.Schedule(ProjectileRuntime.Time((swingAt ?? ProjectileWorldTime(player)) + contact + repeat * duration), () =>
            {
                if (!session.Active || player.ActionInterruptionGeneration != recovery || !PlayerStateAllowsAction(player)
                    || index > 0 && player.ActionCount != action || ticket is not null && !ticket.Current(player)
                    || !SameTriggerSource(source, SupportedSkill(player, id))) return;
                ReleaseSmite(player, session, id, gem, from, aim, target,
                    critical, sequence, index, ruthless, cryExerted, ancestralTargets, rallyingDamage);
            });
        }
        return Task.CompletedTask;
    }

    internal static void ReleaseSmite(Player player,
        ProjectileSession session, ushort id, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, uint target, SkillCriticalRoll critical, ushort sequence,
        int repeat, double ruthless, bool cryExerted = false, int ancestralTargets = 0, int? rallyingDamage = null)
    {
        var skill = SupportedSkill(player, id);
        bool vaal = SkillsFor(player).IsVaal(gem.Effect);
        double now = ProjectileWorldTime(player);
        int range = Math.Max(1, GolemStat(skill.Stats, Stat.SmiteLightningTargetRange));
        int count = Math.Max(1, GolemStat(skill.Stats, Stat.BaseSmiteNumberOfTargets));
        if (!vaal) count += GolemStat(CharacterPlayerStats(player), Stat.BaseSmiteNumberOfTargets);
        var centres = player.Area.Monsters.Where(p => p.Value.Hostile && !p.Value.Dead && p.Value.Life > 0 && p.Value.OwnerId is null)
            .Select(p => (Id: p.Key, At: p.Value.Walk.Position(now)))
            .Where(p => Dist(from, p.At) <= range)
            .OrderBy(p => p.Id == target ? 0 : 1).ThenBy(p => Dist(p.At, aim)).ThenBy(p => p.Id)
            .Take(Math.Clamp(count, 1, 100)).Select(p => p.At).ToList();
        // A ground use still draws the strike; hitting empty ground grants no aura.
        if (centres.Count == 0)
        {
            double distance = Dist(from, aim), factor = distance > range ? range / distance : 1;
            centres.Add(((int)Math.Round(from.X + (aim.X - from.X) * factor),
                (int)Math.Round(from.Y + (aim.Y - from.Y) * factor)));
        }
        bool hit = false;
        var once = vaal ? null : new HashSet<uint>();
        void Hit((int X, int Y) at, uint? single = null)
        {
            var task = AreaSkillHitForUse(ruthless, player, id, gem, from, at,
                immediate: true, sequence: sequence, criticalRoll: critical, targetId: single ?? 0,
                radiusOverride: single is null ? SkillRadius(player, skill.Stats, false, gem.Effect) : WeaponFor(player).Range + 4,
                damageMore: MeleeRepeatDamageMore(player, id, repeat), singleStrikeTarget: single,
                once: once, onHit: victim => { hit = true; once?.Add(victim); }, scheduledSession: session, repeat: repeat, cryExerted: cryExerted,
                ancestralTargets: ancestralTargets, rallyingDamage: rallyingDamage);
            if (!task.IsCompleted) throw new InvalidOperationException("Scheduled Smite contact must not yield.");
            task.GetAwaiter().GetResult();
        }
        uint primary = LightningStrikeTarget(player, from, aim, target, now);
        if (primary != 0 && player.Area.Monsters.TryGetValue(primary, out var enemy)) Hit(enemy.Walk.Position(now), primary);
        if (ancestralTargets > 0)
        {
            var (spirits, _, _) = StrikeExtrasLocked(player, id, gem.Effect, skill.Stats, from, aim, sequence, primary, now,
                exertedTargets: ancestralTargets);
            foreach (uint extra in spirits)
                if (player.Area.Monsters.TryGetValue(extra, out var monster))
                {
                    var at = monster.Walk.Position(now);
                    Hit(at, extra);
                    if (!centres.Contains(at)) centres.Add(at);
                }
        }
        int radius = SkillRadius(player, skill.Stats, false, gem.Effect);
        foreach (var at in centres)
        {
            session.Enqueue(() => PresentMeleeImpact(player,
                "Metadata/Effects/Spells/smite/impact/impact.ao", from, at, Math.Max(.1f, radius / 15f)));
            Hit(at);
        }
        if (!hit) return;
        double seconds = VaalAuraSeconds(GolemStat(skill.Stats, Stat.BaseSkillEffectDuration), CharacterPlayerStats(player), skill.Stats);
        if (seconds <= 0) return;
        // Normal and Vaal Smite grant one family of buff: keep the stronger live version.
        var state = TemporaryAuraStates.GetOrCreateValue(player);
        if (!vaal && state.Active.Values.Any(a => a.Aura.EffectId == "Smite" && a.Until > now
            && SkillsFor(player).IsVaal(a.Aura.Effect))) return;
        StartTemporaryAuraLocked(player, id, "Smite", 1171, seconds, blessing: false);
        session.Enqueue(async () =>
        {
            await SendOwnAuraChanges(player);
            await SendCharacterStats(player, PlayerStatChange.Auras);
        });
    }
}
