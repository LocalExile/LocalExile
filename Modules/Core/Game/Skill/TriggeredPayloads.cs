using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // These spells can release through the area hit executor, including Hexblast's primary and conditional splash.
    static bool TriggerAreaSpell(Player player, SupportedGem gem)
    {
        string? effect = SkillsFor(player).EffectId(gem.Effect);
        return effect is "IceNova" or "IceNovaAltY" or "BladeBlastAltY"
            || IsHexblastEffect(effect) || IsAbsolutionHitSpell(player, gem.Effect);
    }

    static bool TriggerChaosDebuff(string? effect) => IsBaneEffect(effect) || IsContagionEffect(effect);

    static bool TriggerBrandSpell(Player player, int effect) => IsBrandSkill(player, effect)
        && BrandFamilyOf(SkillsFor(player).EffectId(effect)) is not BrandFamily.Trigger;

    public static bool TriggerPayloadAvailable(Player player, SupportedGem gem)
    {
        string? payload = SkillsFor(player).EffectId(gem.Effect);
        if (TriggerChaosDebuff(payload)) return true;
        if (SpatialSpellEffect(payload)) return SkillHitPlan.ForSkill(SkillsFor(player), gem.Effect, gem.Level, gem.Quality).CanHit;
        if (TriggerGuardSpell(player, gem) || TriggerSummonSpell(player, gem)
            || TriggerBrandSpell(player, gem.Effect) || MinionUtility(SkillsFor(player).EffectId(gem.Effect))) return true;
        string? effect = SkillsFor(player).EffectId(gem.Effect);
        if (TimedAreaSpellEffect(effect) && effect != "VaalDetonateDead" || ArcProxyPayload(effect)) return true;
        if (CurseCatalog.All.ContainsKey(effect ?? "")) return true;
        return (UsesProjectileRuntime(gem.Effect) && ResolveProjectileDefinition(gem.Effect) is not null
                || TriggerAreaSpell(player, gem) || IsFirestorm(player, gem.Effect)
                || IsStormCall(SkillsFor(player).EffectId(gem.Effect)))
            && SkillHitPlan.ForSkill(SkillsFor(player), gem.Effect, gem.Level, gem.Quality).CanHit;
    }

    // Prepare without payment or a player action. The caller commits exactly one cost before invoking this release.
    static Action? PrepareTriggeredPayload(Player player, ProjectileSession session, ushort id,
        (int X, int Y) aim, Func<bool> current, (int X, int Y)? brandOrigin = null, SpawnedMonster? brandedTarget = null,
        uint triggerTarget = 0)
    {
        var gem = SupportedSkill(player, id);
        var skill = GemSkillEffects(player).FirstOrDefault(g => g.Id == id);
        if (skill.Name is null || !TriggerPayloadAvailable(player, gem)) return null;
        if (TriggerGuardSpell(player, gem))
            return GuardAvailableForTrigger(player, AreaTime(player))
                ? () => StartTriggeredGuard(player, session, id, AreaTime(player)) : null;
        if (TriggerSummonSpell(player, gem))
            return () => session.Enqueue(() => ReleaseTriggeredSummon(player, session, id, aim, current));
        if (TimedAreaSpellEffect(SkillsFor(player).EffectId(gem.Effect)))
        {
            var sacrifice = new SacrificeUse(player, id, skill.Effect, skill.Gem, triggered: true);
            var critical = CaptureSkillCriticalRoll(player, id);
            return () =>
            {
                var origin = brandOrigin ?? player.Walk.Position(AreaTime(player));
                session.ScheduleTimedAreaSpell(skill, aim, 0, 0, 0xffff, current, critical, sacrifice, 1, 0);
                foreach (var area in SpellCascadeAreas(player, skill.Effect, gem.Stats, origin, aim))
                    session.ScheduleTimedAreaSpell(skill, area, 0, 0, 0xffff, current, critical, sacrifice, 1, 0);
            };
        }
        if (ArcProxyPayload(SkillsFor(player).EffectId(gem.Effect)))
            return () => ScheduleTriggeredArc(player, session, skill, aim, brandOrigin, current, triggerTarget);
        if (IsStormCall(SkillsFor(player).EffectId(gem.Effect)))
        {
            var sacrifice = new SacrificeUse(player, id, skill.Effect, skill.Gem, triggered: true);
            var critical = CaptureSkillCriticalRoll(player, id);
            return () => session.Enqueue(() => TriggerStormCall(player, id, skill, aim, brandOrigin, critical, sacrifice, current));
        }
        if (IsFirestorm(player, gem.Effect))
        {
            var use = new SacrificeUse(player, id, skill.Effect, skill.Gem, triggered: true);
            var critical = CaptureSkillCriticalRoll(player, id);
            return () => session.ScheduleFirestorm(skill, aim, 0, current, use, critical, brandedTarget: brandedTarget, triggered: true);
        }
        if (UsesProjectileRuntime(gem.Effect))
        {
            var sacrifice = new SacrificeUse(player, id, skill.Effect, skill.Gem, triggered: true);
            var critical = CaptureSkillCriticalRoll(player, id);
            return () => session.Schedule(session.Runtime.Now, () => current() && sacrifice.Current, () =>
            {
                var use = CaptureGemProjectile(player, session, id, skill, aim, critical, from: brandOrigin, sacrifice: sacrifice);
                use = ClientDrawnVolley(player, session, id, skill, use, 0, 0);
                return use with { Payload = ((ProjectileOffense)use.Payload!) with { ClientPredicted = false, BrandedTarget = brandedTarget } };
            });
        }
        if (SpatialSpellEffect(SkillsFor(player).EffectId(gem.Effect)))
        {
            var sacrifice = new SacrificeUse(player, id, skill.Effect, skill.Gem, triggered: true);
            var critical = CaptureSkillCriticalRoll(player, id);
            return () => session.ScheduleSpatialSpell(skill, brandOrigin ?? player.Walk.Position(AreaTime(player)), aim, 0,
                current, sacrifice, critical, brandedTarget: brandedTarget);
        }
        if (brandOrigin is { } origin && TriggerAreaSpell(player, gem))
            return () => session.Enqueue(() => AreaSkillHitForUse(null, player,
                id, skill, origin, aim, immediate: true, triggeredCurrent: current, brandedTarget: brandedTarget));
        return () => session.Enqueue(() => ReleaseTriggeredNonProjectile(player, session, id, aim, current));
    }

    static bool PlacedUtilityPayload(string? effect) =>
        effect is "FleshOffering" or "BoneOffering" or "SpiritOffering"
        || IsContagionEffect(effect) || CurseCatalog.All.ContainsKey(effect ?? "");

    public static async Task ReleaseTriggeredNonProjectile(Player player, ProjectileSession session, ushort id,
        (int X, int Y) aim, Func<bool> current)
    {
        var gem = SupportedSkill(player, id);
        var skill = GemSkillEffects(player).FirstOrDefault(g => g.Id == id);
        if (skill.Name is null || !current()) return;
        string effect = SkillsFor(player).EffectId(gem.Effect) ?? "";
        if (MinionUtility(effect) || CurseCatalog.All.ContainsKey(effect) || TriggerChaosDebuff(effect))
        {
            using (ProjectileMutation(player, advance: false))
            {
                if (!current() || !CurrentGameWork || player.Closed || player.LeavingArea || player.Paused || player.PlayerLife == 0) return;
                CommitTriggeredUtilityLocked(player, id, gem, skill.Gem, effect, aim, AreaTime(player), fromPlacement: false);
            }
            if (MinionUtility(effect)) await PublishMinionOutcomes(player);
            else await EnemyDebuffsTick(player, AreaTime(player));
        }
        else if (TriggerBrandSpell(player, gem.Effect))
            await ResolveCastBrand(player, id, skill, aim, recoveryGeneration: null, current);
        else if (TriggerAreaSpell(player, gem))
            await AreaSkillHit(player, id, skill,
                player.Walk.Position(AreaTime(player)), aim, immediate: true, triggeredCurrent: current);
    }

    static void CommitTriggeredUtilityLocked(Player player, ushort id, SupportedGem gem, uint gemObject,
        string effect, (int X, int Y) aim, double now, bool fromPlacement)
    {
        if (MinionUtility(effect))
        {
            if (effect is "ConvocationOld" or "Convocation") RecallMinionsLocked(player, gem, now);
            else StartOfferingLocked(player, id, gem, effect, aim, 0, now);
        }
        else if (CurseCatalog.All.TryGetValue(effect, out var curse))
        {
            var playerStats = CharacterPlayerStats(player);
            if (curse.Mark)
            {
                var nearby = fromPlacement ? player.Area.MonstersNear(aim, 60).Select(p => p.Value) : player.Area.Monsters.Values;
                var target = nearby.Where(m => m is { Hostile: true, Dead: false, Life: > 0, OwnerId: null }
                        && (!fromPlacement || Dist(m.Walk.Position(now), aim) <= 60 && !m.Asleep && !m.Invulnerable))
                    .OrderBy(m => Dist(m.Walk.Position(now), aim)).ThenBy(m => m.ObjectId).FirstOrDefault();
                if (target is not null)
                    ApplyCurseLocked(player, target, id, now, false, gem, playerStats, 0, 1, fromPlacement);
            }
            else
            {
                int radius = CurseRadius(player, id, false, gem, playerStats);
                var nearby = fromPlacement ? player.Area.MonstersNear(aim, radius).Select(p => p.Value) : player.Area.Monsters.Values;
                foreach (var monster in nearby)
                    if (Dist(monster.Walk.Position(now), aim) <= radius
                        && (!fromPlacement || monster is { OwnerId: null, Asleep: false, Invulnerable: false }))
                        ApplyCurseLocked(player, monster, id, now, false, gem, playerStats, 0, 1, fromPlacement);
            }
        }
        else if (!fromPlacement && IsBaneEffect(effect))
            BaneContactLocked(player, id, gem, aim, 1, ProjectileWorldTime(player));
        else if (ContagionCast(player, id, gem.Effect) is { } contagion)
            ApplyContagionLocked(player, aim, contagion with { SpreadAo = ContagionSkinAo(player, gemObject) });
    }
}
