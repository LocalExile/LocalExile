using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    const string WildStrikeEffect = "WildStrike";
    const string WildStrikeExtremesEffect = "WildStrikeAltX";
    const string WildStrikeColdWave = "Metadata/Projectiles/ElementalStrikeColdProjectile";
    static readonly ProjectileSkillDefinition WildStrikeProjectile = new(WildStrikeColdWave,
        new((get, speed, _) => GatewayProjectileDefaults.Resolve(WildStrikeColdWave, get, speed, attack: true)));
    static readonly RuleTable<Player, Dictionary<int, DamageType>> WildStrikeElements = new();

    static bool IsWildStrike(string? effect) => effect is WildStrikeEffect or WildStrikeExtremesEffect;
    static bool IsWildStrike(int effect) => IsWildStrike(Skills.EffectId(effect));

    static ProjectileUse? ReleaseWildStrike(Player player, ProjectileSession session, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) aim, uint targetId, SkillCriticalRoll critical, ushort sequence,
        int repeat, bool cryExerted, double ruthless)
    {
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        uint primary = LightningStrikeTarget(player, from, aim, targetId, now);
        if (primary == 0 || !player.Area.Monsters.TryGetValue(primary, out var monster)) return null;
        var source = SupportedSkill(player, skillId);
        var playerStats = CharacterPlayerStats(player);
        var element = SelectWildStrikeElement(player, gem.Effect, playerStats);
        if (element is null) return null;

        var at = monster.Walk.Position(now);
        var excluded = session.ActorKey(primary, monster);
        bool hit = false;
        double more = MeleeRepeatDamageMore(player, skillId, repeat);
        var contact = AreaSkillHitForUse(ruthless, player, skillId, gem, from, at,
            immediate: true, sequence: sequence, criticalRoll: critical, targetId: primary,
            radiusOverride: WeaponFor(player).Range + monster.StopRange, damageMore: more,
            singleStrikeTarget: primary, onHit: _ => hit = true, scheduledSession: session,
            cryExerted: cryExerted, repeat: repeat);
        if (!contact.IsCompleted) throw new InvalidOperationException("Scheduled Wild Strike contact must not yield.");
        contact.GetAwaiter().GetResult();
        if (!hit) return null;

        bool cold = element == DamageType.Cold;
        var origin = ProjectileSession.Vector(at);
        var direction = (origin - ProjectileSession.Vector(from)).Unit;
        source = WildStrikeDamageSource(source, element.Value, followup: true);
        var use = CaptureGemProjectile(player, session, skillId, gem, aim, critical, sequence, cryExerted: cryExerted,
            from: at, capturedSkill: source);
        var shot = ((ProjectileOffense)use.Payload!).ScaleDamage(more) with
        {
            ExcludedTarget = excluded, ClientPredicted = true, AreaVisual = null
        };
        use = use with { Origin = origin, Aim = origin + direction * use.Profile.Range, Payload = shot };
        if (cold) return use;

        if (element == DamageType.Fire)
        {
            var stats = source.Stats;
            // The fire radius uses the common strike area until the client explosion extent is measured.
            int radius = SkillRadius(player, 24, stats, spell: false, gem.Effect, playerStats);
            radius = (int)Math.Round(radius * SkillArea.RadiusMultiplier(stats.GetValueOrDefault(Stat.WildStrikeRadiusPlusPct)));
            session.Runtime.Impact(use, origin, new("WildStrikeFire", radius));
        }
        else ReleaseWildStrikeArc(session, use, excluded);
        return null;
    }

    static SupportedGem WildStrikeDamageSource(SupportedGem source, DamageType element, bool followup)
    {
        uint conversion = element switch
        {
            DamageType.Fire => Stat.SkillPhysicalDamagePctToConvertToFire,
            DamageType.Cold => Stat.SkillPhysicalDamagePctToConvertToCold,
            _ => Stat.SkillPhysicalDamagePctToConvertToLightning
        };
        var stats = new Dictionary<uint, int>(source.Stats);
        stats[conversion] = ActorScale.Saturate((long)stats.GetValueOrDefault(conversion)
            + stats.GetValueOrDefault(Stat.ElementalStrikePhysicalDamagePctToConvert));
        if (followup) stats[Stat.BaseIsProjectile] = element == DamageType.Cold ? 1 : 0;
        return source with { Stats = stats, WildStrikeFollowupElement = followup ? element : null };
    }

    static DamageType? SelectWildStrikeElement(Player player, int effect, IReadOnlyDictionary<uint, int> playerStats)
    {
        var available = new List<DamageType>(3);
        if (playerStats.GetValueOrDefault(Stat.ElementalHitCannotRollFireDamage) == 0) available.Add(DamageType.Fire);
        if (playerStats.GetValueOrDefault(Stat.ElementalHitCannotRollColdDamage) == 0) available.Add(DamageType.Cold);
        if (playerStats.GetValueOrDefault(Stat.ElementalHitCannotRollLightningDamage) == 0) available.Add(DamageType.Lightning);
        if (available.Count == 0) return null;
        var previous = WildStrikeElements.GetOrCreateValue(player);
        if (available.Count > 1 && previous.TryGetValue(effect, out var last)) available.Remove(last);
        var element = available[AreaRng(player).Next(available.Count)];
        previous[effect] = element;
        return element;
    }

    static void ReleaseWildStrikeArc(ProjectileSession session, ProjectileUse use, ProjectileActorKey primary)
    {
        const double chainRange = 45;
        var seen = new HashSet<ProjectileActorKey> { primary };
        var centre = use.Origin;
        long now = session.Runtime.Now;
        int chains = Math.Clamp(use.Profile.Chain, 0, 256);
        for (int i = 0; i <= chains; i++)
        {
            var target = session.ActorsNear(centre, chainRange, now)
                .Where(actor => actor.Team != use.Team && actor.Key != use.Owner && !seen.Contains(actor.Key))
                .Select(actor => (Actor: actor, At: actor.Position(now)))
                .Where(candidate => (candidate.At - centre).Length <= chainRange
                    && session.TraceTerrain(centre, candidate.At) is null)
                .OrderBy(candidate => (candidate.At - centre).Length).ThenBy(candidate => candidate.Actor.Key.Id)
                .FirstOrDefault();
            if (target.Actor is null) break;
            session.Runtime.ImpactTarget(use with { Origin = centre }, target.Actor, "WildStrikeLightning");
            seen.Add(target.Actor.Key);
            centre = target.At;
        }
    }
}
