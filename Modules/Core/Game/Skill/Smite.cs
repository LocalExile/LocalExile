using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Login;

public static partial class Game
{
    internal static bool IsSmite(int effect) => Skills.EffectId(effect) is "Smite" or "SmiteAltX" or "VaalSmite";

    static Task ScheduleSmite(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort id, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, uint target, int recovery,
        CooldownUseTicket? ticket, SkillCriticalRoll critical, ushort sequence, double? swingAt, bool cryExerted = false)
    {
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        session.Advance();
        double duration = SupportedAttackSeconds(conn, id);
        double contact = GemAttackContactDelay(conn, gem.Effect, duration, out _, out _);
        int action = conn.ActionCount;
        var source = SupportedSkill(conn, id);
        int repeats = SkillsFor(conn).IsVaal(gem.Effect) ? 0 : MeleeAttackRepeats(conn, id);
        double ruthless = RuthlessBlowMore(conn, id, n => GolemStat(source.Stats, n));
        for (int repeat = 0; repeat <= repeats; repeat++)
        {
            int index = repeat;
            session.Runtime.Schedule(ProjectileRuntime.Time((swingAt ?? ProjectileWorldTime(conn)) + contact + repeat * duration), () =>
            {
                if (!session.Active || conn.ActionInterruptionGeneration != recovery || !PlayerStateAllowsAction(conn)
                    || index > 0 && conn.ActionCount != action || ticket is not null && !ticket.Current(conn)
                    || !SameTriggerSource(source, SupportedSkill(conn, id))) return;
                ReleaseSmite(remote, stream, ciphers, conn, session, id, gem, from, aim, target,
                    critical, sequence, index, ruthless, cryExerted);
            });
        }
        return Task.CompletedTask;
    }

    internal static void ReleaseSmite(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ProjectileSession session, ushort id, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, uint target, SkillCriticalRoll critical, ushort sequence,
        int repeat, double ruthless, bool cryExerted = false)
    {
        var skill = SupportedSkill(conn, id);
        bool vaal = SkillsFor(conn).IsVaal(gem.Effect);
        double now = ProjectileWorldTime(conn);
        int range = Math.Max(1, GolemStat(skill.Stats, "smite_lightning_target_range"));
        int count = Math.Max(1, GolemStat(skill.Stats, "base_smite_number_of_targets"));
        if (!vaal) count += GolemStat(CharacterPlayerStats(conn), "base_smite_number_of_targets");
        var centres = conn.Monsters.Where(p => p.Value.Hostile && !p.Value.Dead && p.Value.Life > 0 && p.Value.OwnerId is null)
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
            var task = AreaSkillHitForUse(ruthless, remote, stream, ciphers, conn, id, gem, from, at,
                immediate: true, sequence: sequence, criticalRoll: critical, targetId: single ?? 0,
                radiusOverride: single is null ? SkillRadius(conn, skill.Stats, false, gem.Effect) : WeaponFor(conn).Range + 4,
                damageMore: MeleeRepeatDamageMore(conn, id, repeat), singleStrikeTarget: single,
                once: once, onHit: victim => { hit = true; once?.Add(victim); }, scheduledSession: session, repeat: repeat, cryExerted: cryExerted);
            if (!task.IsCompleted) throw new InvalidOperationException("Scheduled Smite contact must not yield.");
            task.GetAwaiter().GetResult();
        }
        uint primary = LightningStrikeTarget(conn, from, aim, target, now);
        if (primary != 0 && conn.Monsters.TryGetValue(primary, out var enemy)) Hit(enemy.Walk.Position(now), primary);
        int radius = SkillRadius(conn, skill.Stats, false, gem.Effect);
        foreach (var at in centres)
        {
            session.Enqueue(() => PresentMeleeImpact(remote, stream, ciphers, conn,
                "Metadata/Effects/Spells/smite/impact/impact.ao", from, at, Math.Max(.1f, radius / 15f)));
            Hit(at);
        }
        if (!hit) return;
        double seconds = VaalAuraSeconds(GolemStat(skill.Stats, "base_skill_effect_duration"), CharacterPlayerStats(conn), skill.Stats);
        if (seconds <= 0) return;
        // Normal and Vaal Smite grant one family of buff: keep the stronger live version.
        var state = TemporaryAuraStates.GetOrCreateValue(conn);
        if (!vaal && state.Active.Values.Any(a => a.Aura.EffectId == "Smite" && a.Until > now
            && SkillsFor(conn).IsVaal(a.Aura.Effect))) return;
        StartTemporaryAuraLocked(conn, id, "Smite", 1171, seconds, blessing: false);
        session.Enqueue(async () =>
        {
            await SendOwnAuraChanges(remote, stream, ciphers, conn);
            await SendCharacterStats(remote, stream, ciphers, conn, "Smite hit aura");
        });
    }
}
