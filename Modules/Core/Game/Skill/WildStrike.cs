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
        int repeat, bool cryExerted, double ruthless, int ancestralTargets, int? rallyingDamage)
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
        var contacts = new List<(ProjectileActorKey Actor, (int X, int Y) At)>();
        double more = MeleeRepeatDamageMore(player, skillId, repeat);
        var contact = AreaSkillHitForUse(ruthless, player, skillId, gem, from, at,
            immediate: true, sequence: sequence, criticalRoll: critical, targetId: primary,
            radiusOverride: WeaponFor(player).Range + monster.StopRange, damageMore: more,
            onHit: NoteContact, scheduledSession: session, cryExerted: cryExerted, repeat: repeat,
            targetFilter: (id, _) => id == primary, ancestralTargets: ancestralTargets, rallyingDamage: rallyingDamage);
        if (!contact.IsCompleted) throw new InvalidOperationException("Scheduled Wild Strike contact must not yield.");
        contact.GetAwaiter().GetResult();
        if (contacts.Count == 0) return null;

        source = WildStrikeDamageSource(source, element.Value, followup: true);
        var use = CaptureGemProjectile(player, session, skillId, gem, aim, critical, weaponHand: null, sequence: sequence,
            cryExerted: cryExerted, from: at, capturedSkill: source, otherWarcryExertions: ancestralTargets > 0 ? 1 : 0,
            rallyingDamage: rallyingDamage, ancestralExerted: ancestralTargets > 0);
        var shot = ((ProjectileOffense)use.Payload!).ScaleDamage(more) with { ClientPredicted = true, AreaVisual = null };
        foreach (var strike in contacts)
        {
            var origin = ProjectileSession.Vector(strike.At);
            var direction = (origin - ProjectileSession.Vector(from)).Unit;
            var followup = use with { Origin = origin, Aim = origin + direction * use.Profile.Range,
                Payload = shot with { ExcludedTarget = strike.Actor } };
            if (element == DamageType.Cold) session.ReleaseNow(followup);
            else if (element == DamageType.Fire)
            {
                // The fire radius uses the common strike area until the client explosion extent is measured.
                int extraArea = source.Stats.GetValueOrDefault(Stat.WildStrikeRadiusPlusPct)
                    + playerStats.GetValueOrDefault(Stat.WildStrikeRadiusPlusPct);
                var fireStats = extraArea == 0 ? source.Stats : new Dictionary<uint, int>(source.Stats)
                {
                    [Stat.BaseSkillAreaOfEffectPlusPct] = ActorScale.Saturate(
                        (long)source.Stats.GetValueOrDefault(Stat.BaseSkillAreaOfEffectPlusPct) + extraArea)
                };
                int radius = SkillRadius(player, 24, fireStats, spell: false, gem.Effect, playerStats);
                session.Runtime.Impact(followup, origin, new("WildStrikeFire", radius));
            }
            else ReleaseWildStrikeArc(session, followup, strike.Actor, source.Stats.GetValueOrDefault(Stat.AdditionalBeamOnlyChains));
        }
        return null;

        void NoteContact(uint id)
        {
            if (id == primary) contacts.Add((excluded, at));
            else if (player.Area.Monsters.TryGetValue(id, out var target))
                contacts.Add((session.ActorKey(id, target), target.Walk.Position(now)));
        }
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

    static void ReleaseWildStrikeArc(ProjectileSession session, ProjectileUse use, ProjectileActorKey primary, int beamChains)
    {
        const double chainRange = 45;
        var seen = new HashSet<ProjectileActorKey> { primary };
        var centre = use.Origin;
        long now = session.Runtime.Now;
        int chains = (int)Math.Clamp((long)use.Profile.Chain + beamChains, 0, 256);
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
