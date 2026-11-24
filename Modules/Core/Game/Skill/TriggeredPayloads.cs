using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // These spells have a single radial hit executor. Do not silently turn ground damage, chains, corpses or
    // persistent entities into one area hit merely because their extracted stat set contains damage.
    static bool TriggerAreaSpell(Player player, SupportedGem gem) =>
        SkillsFor(player).EffectId(gem.Effect) == "IceNova" || IsAbsolutionHitSpell(player, gem.Effect);

    static bool TriggerBrandSpell(Player player, int effect) => IsBrandSkill(player, effect)
        && BrandFamilyOf(SkillsFor(player).EffectId(effect)) is not BrandFamily.Trigger;

    public static bool TriggerPayloadAvailable(Player player, SupportedGem gem)
    {
        if (TriggerGuardSpell(player, gem) || TriggerSummonSpell(player, gem)
            || TriggerBrandSpell(player, gem.Effect) || MinionUtility(SkillsFor(player).EffectId(gem.Effect))) return true;
        if (CurseCatalog.All.ContainsKey(SkillsFor(player).EffectId(gem.Effect) ?? "")) return true;
        return (UsesProjectileRuntime(gem.Effect) && ResolveProjectileDefinition(gem.Effect) is not null
                || TriggerAreaSpell(player, gem) || IsFirestorm(player, gem.Effect)
                || IsStormCall(SkillsFor(player).EffectId(gem.Effect)))
            && SkillHitPlan.ForSkill(SkillsFor(player), gem.Effect, gem.Level, gem.Quality).CanHit;
    }

    // Prepare without payment or a player action. The caller commits exactly one cost before invoking this release.
    static Action? PrepareTriggeredPayload(Player player, ProjectileSession session, ushort id,
        (int X, int Y) aim, Func<bool> current, (int X, int Y)? brandOrigin = null, SpawnedMonster? brandedTarget = null)
    {
        var gem = SupportedSkill(player, id);
        var skill = GemSkillEffects(player).FirstOrDefault(g => g.Id == id);
        if (skill.Name is null || !TriggerPayloadAvailable(player, gem)) return null;
        if (TriggerGuardSpell(player, gem))
            return TriggerGuardReady(player, AreaTime(player))
                ? () => StartTriggeredGuard(player, session, id, AreaTime(player)) : null;
        if (TriggerSummonSpell(player, gem))
            return () => session.Enqueue(() => ReleaseTriggeredSummon(player, session, id, aim, current));
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
            return () => session.ScheduleFirestorm(skill, aim, 0, current, use, critical, brandedTarget: brandedTarget);
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
        if (brandOrigin is { } origin && TriggerAreaSpell(player, gem))
            return () => session.Enqueue(() => AreaSkillHitForUse(null, player,
                id, skill, origin, aim, immediate: true, triggeredCurrent: current, brandedTarget: brandedTarget));
        return () => session.Enqueue(() => ReleaseTriggeredNonProjectile(player, session, id, aim, current));
    }

    static bool PlacedUtilityPayload(string? effect) =>
        effect is "FleshOffering" or "BoneOffering" or "SpiritOffering" || CurseCatalog.All.ContainsKey(effect ?? "");

    public static Task ReleaseTriggeredNonProjectile(Player player, ProjectileSession session, ushort id,
        (int X, int Y) aim, Func<bool> current) => ReleaseTriggeredNonProjectile(player, session, id, aim, current, fromPlacement: false);

    static async Task ReleaseTriggeredNonProjectile(Player player, ProjectileSession session, ushort id,
        (int X, int Y) aim, Func<bool> current, bool fromPlacement)
    {
        var gem = SupportedSkill(player, id);
        var skill = GemSkillEffects(player).FirstOrDefault(g => g.Id == id);
        if (skill.Name is null || !current()) return;
        if (MinionUtility(SkillsFor(player).EffectId(gem.Effect)))
        {
            using (ProjectileMutation(player, advance: false))
            {
                if (!current() || !CurrentGameWork || player.Closed || player.LeavingArea || player.Paused || player.PlayerLife == 0) return;
                double now = AreaTime(player);
                string effect = SkillsFor(player).EffectId(gem.Effect)!;
                if (effect is "ConvocationOld" or "Convocation") RecallMinionsLocked(player, gem, now);
                else StartOfferingLocked(player, id, gem, effect, aim, 0, now);
            }
            await PublishMinionOutcomes(player);
        }
        else if (CurseCatalog.All.TryGetValue(SkillsFor(player).EffectId(gem.Effect) ?? "", out var curse))
        {
            using (ProjectileMutation(player, advance: false))
            {
                if (!current() || !CurrentGameWork || player.Closed || player.LeavingArea || player.Paused || player.PlayerLife == 0) return;
                double now = AreaTime(player);
                var playerStats = CharacterPlayerStats(player);
                if (curse.Mark)
                {
                    var target = player.Area.Monsters.Values.Where(m => m is { Hostile: true, Dead: false, Life: > 0, OwnerId: null }
                            && (!fromPlacement || Dist(m.Walk.Position(now), aim) <= 60 && !m.Asleep && !m.Invulnerable))
                        .OrderBy(m => Dist(m.Walk.Position(now), aim)).ThenBy(m => m.ObjectId).FirstOrDefault();
                    if (target is not null)
                        ApplyCurseLocked(player, target, id, now, false, gem, playerStats, 0, 1, fromPlacement);
                }
                else
                {
                    int radius = CurseRadius(player, id, false, gem, playerStats);
                    foreach (var monster in player.Area.Monsters.Values)
                        if (Dist(monster.Walk.Position(now), aim) <= radius
                            && (!fromPlacement || monster is { OwnerId: null, Asleep: false, Invulnerable: false }))
                            ApplyCurseLocked(player, monster, id, now, false, gem, playerStats, 0, 1, fromPlacement);
                }
            }
            await EnemyDebuffsTick(player, AreaTime(player));
        }
        else if (TriggerBrandSpell(player, gem.Effect))
            await ResolveCastBrand(player, id, skill, aim, recoveryGeneration: null, current);
        else if (TriggerAreaSpell(player, gem))
            await AreaSkillHit(player, id, skill,
                player.Walk.Position(AreaTime(player)), aim, immediate: true, triggeredCurrent: current);
    }
}
