using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // These spells have a single radial hit executor. Do not silently turn ground damage, chains, corpses or
    // persistent entities into one area hit merely because their extracted stat set contains damage.
    static bool TriggerAreaSpell(ClientState conn, SupportedGem gem) => SkillsFor(conn).EffectId(gem.Effect)
        is "IceNova";

    public static bool TriggerPayloadAvailable(ClientState conn, SupportedGem gem)
    {
        if (TriggerGuardSpell(conn, gem)) return true;
        if (CurseCatalog.All.ContainsKey(SkillsFor(conn).EffectId(gem.Effect) ?? "")) return true;
        return (UsesProjectileRuntime(gem.Effect) && ResolveProjectileDefinition(gem.Effect) is not null
                || TriggerAreaSpell(conn, gem))
            && SkillHitPlan.ForSkill(SkillsFor(conn), gem.Effect, gem.Level, gem.Quality).CanHit;
    }

    // Prepare without payment or a player action. The caller commits exactly one cost before invoking this release.
    static Action? PrepareTriggeredPayload(ClientState conn, ProjectileSession session, ushort id,
        (int X, int Y) aim, Func<bool> current)
    {
        var gem = SupportedSkill(conn, id);
        var skill = GemSkillEffects(conn).FirstOrDefault(g => g.Id == id);
        if (skill.Name is null || !TriggerPayloadAvailable(conn, gem)) return null;
        if (TriggerGuardSpell(conn, gem))
            return TriggerGuardReady(conn, conn.Clock.Elapsed.TotalSeconds)
                ? () => StartTriggeredGuard(conn, session, id, conn.Clock.Elapsed.TotalSeconds) : null;
        if (UsesProjectileRuntime(gem.Effect))
        {
            var sacrifice = new SacrificeUse(conn, id, skill.Effect, skill.Gem, triggered: true);
            var critical = CaptureSkillCriticalRoll(conn, id);
            return () => session.Schedule(session.Runtime.Now, () => current() && sacrifice.Current, () =>
            {
                var use = CaptureGemProjectile(conn, session, id, skill, aim, critical, sacrifice: sacrifice);
                use = ClientDrawnVolley(conn, session, id, skill, use, 0, 0);
                return use with { Payload = ((ProjectileOffense)use.Payload!) with { ClientPredicted = false } };
            });
        }
        return () => session.Enqueue(() => ReleaseTriggeredNonProjectile(conn, session, id, aim, current));
    }

    public static async Task ReleaseTriggeredNonProjectile(ClientState conn, ProjectileSession session, ushort id,
        (int X, int Y) aim, Func<bool> current)
    {
        var gem = SupportedSkill(conn, id);
        var skill = GemSkillEffects(conn).FirstOrDefault(g => g.Id == id);
        if (skill.Name is null || !current()) return;
        if (CurseCatalog.All.TryGetValue(SkillsFor(conn).EffectId(gem.Effect) ?? "", out var curse))
        {
            lock (conn.Monsters)
            {
                if (!current()) return;
                double now = AreaTime(conn);
                var player = CharacterPlayerStats(conn);
                int radius = CurseRadius(conn, id, false, gem, player);
                var target = conn.Monsters.Where(m => m.Value.Hostile && !m.Value.Dead && m.Value.Life > 0)
                    .OrderBy(m => Dist(m.Value.Walk.Position(now), aim)).ThenBy(m => m.Key).FirstOrDefault();
                foreach (var (objectId, monster) in conn.Monsters)
                    if (curse.Mark ? objectId == target.Key : Dist(monster.Walk.Position(now), aim) <= radius)
                        ApplyCurseLocked(conn, monster, id, now, false, gem, player);
            }
            await EnemyDebuffsTick(session.remote, session.stream, session.ciphers, conn, AreaTime(conn));
        }
        else if (TriggerAreaSpell(conn, gem))
            await AreaSkillHit(session.remote, session.stream, session.ciphers, conn, id, skill,
                conn.Walk.Position(AreaTime(conn)), aim, immediate: true, triggeredCurrent: current);
    }
}
