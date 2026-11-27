using GameServer.Core.Projectiles;

public static partial class Game
{
    // BloodSpears reads the final count, stance and variation on the released action. The network skill
    // snapshot does not run the client's virtual count or its GrantedEffects.SkillID calculator.
    const ushort PerforateAction = 0x69ac;

    static int PerforateSpikeCount(Func<uint, int> get, bool changedStance) =>
        Math.Max(0, ActorScale.Saturate((long)get(Stat.BloodSpearsBaseNumberOfSpears)
            + (changedStance ? get(Stat.BloodSpearsAdditionalNumberOfSpearsIfChangedStanceRecently) : 0)));

    static void PublishPerforateStats(Player player, SkillCatalog skills, int effect, Dictionary<uint, int> stats)
    {
        stats[Stat.SkillBehaviourVariation] = skills.BehaviourVariation(effect);
        stats[Stat.InSandStance] = player.Stance == StanceCatalog.SandSelection ? 1 : 0;
        stats[Stat.VirtualBloodSpearsTotalNumberOfSpears] = PerforateSpikeCount(stats.GetValueOrDefault,
            player.RecentEvents.Happened(StatConditions.ChangedStance, AreaTime(player)));
    }

    static void SchedulePerforateCountExpiryLocked(Player player)
    {
        if (player.RecentEvents.LastWindow(StatConditions.ChangedStance) is not { } changed) return;

        var session = ProjectilesFor(player);
        long end = ProjectileRuntime.Time(changed.Until) + 1;
        session.Runtime.Schedule(Math.Max(end, session.Runtime.Now), () =>
        {
            if (!session.Active || player.RecentEvents.LastWindow(StatConditions.ChangedStance) != changed
                || !GemSkillEffects(player).Any(gem => SkillsFor(player).EffectId(gem.Effect) == "PerforateAltX")) return;
            player.PlayerSkillsCache = null;
            ActiveSkillSets.Remove(player);
            if (!PublishByFrame(player, PlayerStatChange.Panel)) PresentStats(player);
        });
    }

    static bool PerforateSand(Player player, int effect) =>
        player.Stance == StanceCatalog.SandSelection && SkillsFor(player).BehaviourVariation(effect) != 3;

    static int PerforateBaseRadius(bool sand, bool bloodshed) => sand ? 11 : bloodshed ? 16 : 8;

    static double PerforateSandAreaMore(Func<uint, int> get) =>
        Math.Max(0, 1 + get(Stat.SkillAreaOfEffectPlusPctFinalInSandStance) / 100d);

    static double PerforateBloodDamageMore(Func<uint, int> get) =>
        Math.Max(0, 1 + get(Stat.BloodSpearsDamagePlusPctFinalInBloodStance) / 100d);

    static void SchedulePerforateLocked(Player player,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, SupportedGem admitted,
        (int X, int Y) from, (int X, int Y) to, ushort sequence, int repeat, AttackHand hand, SkillCatalog.Unarmed weapon,
        int? offCritical, SkillCriticalRoll critical, double radiusMore, double damageMore, double ruthless,
        bool exerted, bool seismicExerted, double now)
    {
        var session = ProjectilesFor(player);
        var stats = SkillAreaStats(player, admitted.Stats, gem.Effect);
        int StatValue(uint stat) => stats.GetValueOrDefault(stat);
        bool bloodshed = SkillsFor(player).BehaviourVariation(gem.Effect) == 3;
        bool sand = PerforateSand(player, gem.Effect);
        int count = PerforateSpikeCount(StatValue, player.RecentEvents.Happened(StatConditions.ChangedStance, now));
        int reach = Math.Max(0, weapon.Range + StatValue(Stat.AttackMaximumActionDistancePlus));
        int scale = (int)((100 + SkillArea.RadiusPercent(StatValue, spell: false,
            sand ? PerforateSandAreaMore(StatValue) : 1)) * radiusMore);
        uint repeatsLeft = (uint)Math.Max(0, MeleeAttackRepeats(player, gem.Id) - repeat);
        var steps = PerforateSteps(from, to, reach, count, sand, bloodshed, scale,
            StatValue(Stat.BloodSpearsAoeModifiersApplyToBloodSpearPlacementRangeAtPctValue), sequence, player.Id,
            repeatsLeft: repeatsLeft);
        if (steps.Length == 0) return;

        double more = damageMore * (sand ? 1 : PerforateBloodDamageMore(StatValue));
        var source = CaptureAreaAttack(player, gem.Id, admitted, hand, weapon, offCritical, more, more, "spike");
        HashSet<uint>? struck = sand ? [] : null;
        bool Active() => session.Active && player.PlayerLife > 0;
        long start = Math.Max(session.Runtime.Now, ProjectileRuntime.Time(now));
        long interval = ProjectileRuntime.Time(sand ? .02 : bloodshed ? .10 : .05);
        int index = 0;
        void Release()
        {
            if (!Active()) return;
            var step = steps[index];
            double when = ProjectileWorldTime(player);
            int searchRadius = step.LineEnd is { } end
                ? Math.Max(step.Radius, (int)Math.Ceiling(Dist(step.Centre, end)) + step.LineHalfWidth * 2)
                : step.Radius;
            // The spatial query includes body extents. Take the exact circle or rectangle at the spike's own time.
            TriggerEarthshatterSpikesLocked(player, step.Centre, step.Radius, when);
            var candidates = player.Area.MonstersNear(step.Centre, searchRadius)
                .Where(pair => struck?.Contains(pair.Key) != true
                    && pair.Value is { Dead: false, Life: > 0, Hostile: true, OwnerId: null, WorldHidden: false }
                    && PerforateCovers(step, pair.Value.Walk.Position(when), Math.Max(0, LivingCollision.Size(pair.Value))))
                .Select(pair => pair.Key).ToArray();
            if (candidates.Length != 0)
            {
                var hit = AreaSkillHitStrike(player, gem.Id, gem, step.Centre, step.Centre, immediate: true, sequence,
                    actionToken: null, cooldownUse: null, melee: true, damageMore: 1, excludeMonster: 0, critical,
                    channelGeneration: null, targetId: 0, repeat, radiusOverride: step.Radius, once: struck,
                    repeatDelay: 0, radiusMore: 1, sacrifice: null, swingAt: null, triggeredCurrent: Active, discharge: null,
                    strikeIndex: 0, firstContactAt: null, ruthless, priorTargets: null, scaledBlastChaos: 0,
                    singleStrikeTarget: null, onHit: null, scheduledSession: session, cryExerted: exerted,
                    prepaidRawExtra: 0, baseChaos: 0, hammer: null, retainedAttack: source, allowMeleeSplash: false,
                    contactTargets: candidates, seismicExerted: seismicExerted);
                if (!hit.IsCompleted) throw new InvalidOperationException("Scheduled Perforate contact must not yield.");
                hit.GetAwaiter().GetResult();
            }
            index++;
            if (index < steps.Length) session.Runtime.Schedule(start + index * interval, Release);
        }

        session.Runtime.Schedule(start, Release);
    }

    static bool PerforateCovers(PerforateStep step, (int X, int Y) target, int body)
    {
        if (step.LineStart is not { } start || step.LineEnd is not { } end)
            return Dist(step.Centre, target) <= step.Radius + body;
        if (start == end) return Dist(start, target) <= step.LineHalfWidth + body;
        var direction = GroundSlamWave.Direction(start, end);
        double x = target.X - start.X, y = target.Y - start.Y;
        double along = x * direction.X + y * direction.Y;
        double across = x * direction.Y - y * direction.X;
        int width = step.LineHalfWidth;
        double outsideAlong = Math.Max(0, Math.Max(-width - along, along - Dist(start, end) - width));
        double outsideAcross = Math.Max(0, Math.Abs(across) - width);
        return outsideAlong * outsideAlong + outsideAcross * outsideAcross <= (double)body * body;
    }

    internal readonly record struct PerforateStep((int X, int Y) Centre, int Radius,
        (int X, int Y)? LineStart, (int X, int Y)? LineEnd, int LineHalfWidth);

    internal static PerforateStep[] PerforateSteps((int X, int Y) from, (int X, int Y) aim, int reach,
        int count, bool sand, bool bloodshed, int areaScale, int placementAreaShare, ushort sequence,
        uint owner, byte detail = 2, uint repeatsLeft = 0, uint repeatVariant = 0)
    {
        sand &= !bloodshed;
        var random = new AnimationVariantSelector.NativeRandom([sequence, detail, owner, repeatsLeft, repeatVariant, 0, 0, 0]);
        var direction = GroundSlamWave.Direction(from, aim);
        var centre = aim == from
            ? PerforateOffset(from, reach, random.Range(0, MathF.Tau))
            : (from.X + PerforateRound((float)direction.X * reach), from.Y + PerforateRound((float)direction.Y * reach));
        int radius = areaScale * PerforateBaseRadius(sand, bloodshed) / 100;
        var steps = new PerforateStep[count + (sand ? 1 : 0)];
        if (steps.Length == 0) return steps;
        steps[0] = new(centre, radius, null, null, 0);

        if (sand)
        {
            int reachOut = areaScale * 20 / 100;
            int halfWidth = areaScale * 9 / 100;
            random = new AnimationVariantSelector.NativeRandom([sequence, detail, owner, repeatsLeft, repeatVariant, 0, 0, 0]);
            float facing = random.Range(0, MathF.Tau);
            var angles = new float[count];
            for (int i = 0; i < angles.Length; i++)
                angles[i] = facing + MathF.Tau / count * i;
            for (int i = 0; i < angles.Length; i++)
            {
                int selected = random.Uniform(i, angles.Length - 1);
                (angles[i], angles[selected]) = (angles[selected], angles[i]);
                var start = PerforateOffset(centre, 8, angles[i]);
                steps[i + 1] = new(centre, radius, start, PerforateOffset(start, reachOut, angles[i]), halfWidth);
            }
        }
        else
        {
            int placementScale = 100 + (areaScale - 100) * placementAreaShare / 100;
            float maximumOffset = (bloodshed ? 30 : 15) * placementScale / 100f;
            for (int i = 1; i < count; i++)
            {
                random = new AnimationVariantSelector.NativeRandom([sequence, detail, owner, repeatsLeft, repeatVariant, (uint)i, 0, 0]);
                float offset = i * maximumOffset / count;
                steps[i] = new(PerforateOffset(centre, offset, random.Range(0, MathF.Tau)), radius, null, null, 0);
            }
        }
        return steps;
    }

    static (int X, int Y) PerforateOffset((int X, int Y) centre, float distance, float angle) =>
        (centre.X + PerforateRound(MathF.Sin(angle) * distance),
         centre.Y + PerforateRound(-MathF.Cos(angle) * distance));

    static int PerforateRound(float n) => (int)MathF.Floor(n + 0.5f);
}
