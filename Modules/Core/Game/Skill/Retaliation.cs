using GameServer.Core.Projectiles;
using GameServer.Core.World;

public static partial class Game
{
    const string DivineRetributionEffect = "DivineRetribution";
    const string DivineRetributionOpportunity = "divine_retribution_opportunity";
    const string VengefulCryEffect = "VengefulCry";
    const string VengefulCryOpportunity = "vengeful_cry_opportunity";
    const ushort CrushingFistAction = 0x11f1;
    readonly record struct RetaliationGate(string Event, ushort Buff, bool OnBlock = true);

    static RetaliationGate? RetaliationOpportunity(string? effect) => effect switch
    {
        DivineRetributionEffect => new(DivineRetributionOpportunity, 2470),
        VengefulCryEffect => new(VengefulCryOpportunity, 2471, OnBlock: false),
        "CrushingFist" => new("crushing_fist_opportunity", 2469),
        "GlacialShieldSwipe" => new("glacial_shield_swipe_opportunity", 2468),
        "Swordstorm" => new("swordstorm_opportunity", 2472),
        "Eviscerate" => new("eviscerate_opportunity", 2473),
        "ShieldOfLight" => new("shield_of_light_opportunity", 2474),
        _ => null,
    };

    // The invisible BuffDefinitions rows unlock the native skill buttons for the same window the server validates.
    static void OpenRetaliationWindows(Player player, bool blocked, double now)
    {
        if (player.PlayerLife == 0) return;
        string originalGate = blocked ? DivineRetributionOpportunity : VengefulCryOpportunity;
        player.RecentEvents.Record(originalGate, now);
        var catalog = SkillsFor(player);
        foreach (var gem in GemSkillEffects(player))
        {
            if (RetaliationOpportunity(catalog.EffectId(gem.Effect)) is not { } gate
                || gate.OnBlock != blocked || catalog.ActionHash(gem.Effect) is not ushort action) continue;
            double duration = RetaliationWindowSeconds(player, SupportedSkill(player, gem.Id).Stats);
            if (duration <= 0) continue;
            if (gate.Event != originalGate) player.RecentEvents.Record(gate.Event, now);
            player.Area.Buffs.Set(player.Id, BuffRows.Spec(gate.Buff, player.Id, null, duration, tag: gem.Id) with
            {
                SourceSkill = gem.Id,
                ActiveSkill = (gem.Id, action),
            });
        }
    }

    static void SpendRetaliationWindow(Player player, RetaliationGate gate)
    {
        player.RecentEvents.Forget(gate.Event);
        player.Area.Buffs.ClearRow(player.Id, gate.Buff);
    }

    static bool RetaliationReady(Player player, ushort skillId, string key, double now)
    {
        if (player.RecentEvents.LastWindow(key) is not { } opportunity || now < opportunity.From) return false;
        var skill = SupportedSkill(player, skillId);
        if (key == DivineRetributionOpportunity
            && (PlacedKind(skill) != default || IsTotemSkill(player, skillId))) return false;
        return now < opportunity.From + RetaliationWindowSeconds(player, skill.Stats);
    }

    static double RetaliationWindowSeconds(Player player, IReadOnlyDictionary<uint, int> own)
    {
        long milliseconds = own.GetValueOrDefault(Stat.RetaliationBaseUseWindowDurationMs);
        if (milliseconds <= 0) return 0;
        var character = PlayerStatValues(player);
        milliseconds += (long)own.GetValueOrDefault(Stat.RetaliationSkillAdditionalUseWindowDurationMs)
            + character.GetValueOrDefault(Stat.RetaliationSkillAdditionalUseWindowDurationMs);
        long increased = (long)own.GetValueOrDefault(Stat.RetaliationUseWindowDurationPlusPct)
            + character.GetValueOrDefault(Stat.RetaliationUseWindowDurationPlusPct);
        return Math.Max(0, milliseconds) * Math.Max(0, 100d + increased) / 100000d;
    }

    static bool IsSwordstorm(string? effect) => effect == "Swordstorm";

    static double CrushingFistContactDelay(Player player, AttackHand hand, double attackSeconds,
        out double placementDelay, out string? clip, out double contactFraction)
    {
        // RetaliationHandOfJustice fits SpellAreaOfEffect and GroundSlam (or GroundSlamOffhand) into
        // successive halves of the attack time. The first contact fixes the centre, the second deals damage.
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO")
            ?? SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject;
        var profile = AnimationWeaponProfileFor(player);
        string stance = Stance(ActiveMainHand(player)?.Type?.Class) + Stance(ActiveOffHand(player)?.Type?.Class);
        if (stance.Length == 0) stance = "Unarmed";
        double placement = Contact("SpellAreaOfEffect")?.Contact ?? Animations.SpellAreaRelease(ao);
        var slam = (hand == AttackHand.Off ? Contact("GroundSlamOffhand") : null)
            ?? Contact("GroundSlam")
            ?? Animations.SkillAttackContact(ao, profile, "groundslam");
        placementDelay = attackSeconds * placement / 2;
        clip = slam?.Name ?? "GroundSlam";
        contactFraction = (1 + (slam?.Contact ?? .65)) / 2;
        return attackSeconds * contactFraction;

        (string Name, double Contact)? Contact(string action) =>
            Animations.ActAnimationEvents(ao, stance, action, "contact") is { Times.Count: > 0 } animation
                ? (animation.Name, (double)animation.Times[0] / animation.LengthMs) : null;

        static string Stance(string? itemClass) => itemClass switch
        {
            null => "",
            "Claw" => "Claw",
            "Dagger" or "Rune Dagger" => "Dagger",
            "Thrusting One Hand Sword" => "OneHandSwordThrusting",
            "Staff" or "Warstaff" => "Staff",
            "Two Hand Sword" or "Two Hand Axe" => "TwoHandSword",
            "Two Hand Mace" => "TwoHandMace",
            "Shield" => "Shield",
            _ => "OneHandSword",
        };
    }

    static (int X, int Y) PlaceCrushingFist(Player player, int effect, SkillCatalog.Unarmed weapon,
        IReadOnlyDictionary<uint, int> skillStats, (int X, int Y) aim, uint targetId)
    {
        double now = ProjectileWorldTime(player);
        var origin = player.Walk.Position(now);
        if (targetId != 0 && player.Area.Monsters.TryGetValue(targetId, out var target) && !target.Dead)
            aim = target.Walk.Position(now);

        double distance = Dist(origin, aim);
        double reach = RetaliationActionReach(player, weapon, effect, skillStats);
        if (distance > reach)
        {
            double scale = reach / distance;
            aim = (origin.X + (int)Math.Floor((aim.X - origin.X) * scale + .5),
                origin.Y + (int)Math.Floor((aim.Y - origin.Y) * scale + .5));
        }
        var centre = origin;
        var sight = player.Layout.Sight;
        foreach (var cell in ClientPathfinder.Line(origin, aim))
        {
            // The projectile layer stops the projection. The walking layer only decides which cells can hold the fist.
            if (sight?.Invoke(cell.X, cell.Y) == 0 || player.Barrier.Contains(cell)) break;
            if (player.Layout.WalkableFor(cell.X, cell.Y, 0)) centre = cell;
        }
        return centre;
    }

    static long RetaliationActionReach(Player player, SkillCatalog.Unarmed weapon, int effect,
        IReadOnlyDictionary<uint, int> skillStats) =>
        Math.Max(0, (long)MeleeSkillReach(player, weapon, effect, skillStats)
            + CharacterPlayerStats(player).GetValueOrDefault(Stat.AttackMaximumActionDistancePlus)
            + skillStats.GetValueOrDefault(Stat.AttackMaximumActionDistancePlus));

    // The ice and lightning retaliation cones advance the same 10-cell band, at different speeds and angles.
    static List<uint>? RetaliationConeTargetsLocked(Player player, (int X, int Y) origin, (double X, double Y) facing,
        double sine, double cosine, int radius, int step, int steps, HashSet<uint> seen)
    {
        const int band = 10;
        int inner = (int)((long)Math.Max(0, radius - band) * step / steps);
        int outer = Math.Min(radius, inner + band);
        double time = ProjectileWorldTime(player);
        List<uint>? targets = null;
        foreach (var (id, monster) in player.Area.MonstersNear(origin, outer))
        {
            if (!monster.Hostile || monster.Dead || monster.Life == 0 || monster.OwnerId is not null
                || monster.WorldHidden || seen.Contains(id)) continue;
            var at = monster.Walk.Position(time);
            double dx = at.X - origin.X, dy = at.Y - origin.Y;
            double distanceSq = dx * dx + dy * dy;
            int body = LivingCollision.Size(monster);
            double near = Math.Max(0, inner - body), far = (double)outer + body;
            if (distanceSq < near * near || far < 0 || distanceSq > far * far) continue;
            double forward = dx * facing.X + dy * facing.Y;
            double across = dy * facing.X - dx * facing.Y;
            if (forward * sine < Math.Abs(across) * cosine || !MeleeArcSight(player, origin, at)) continue;
            (targets ??= []).Add(id);
        }
        return targets;
    }

    static void HitRetaliationWaveLocked(Player player,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) origin,
        ushort sequence, int radius, SkillCriticalRoll critical, double ruthless, ProjectileSession session, Func<bool> current,
        RetainedAreaAttack attack, HashSet<uint> seen, List<uint>? targets)
    {
        if (targets is null) return;
        var hit = AreaSkillHitStrike(player, gem.Id, gem, origin, origin, immediate: true, sequence,
            actionToken: null, cooldownUse: null, melee: true, damageMore: 1, excludeMonster: 0,
            criticalRoll: critical, channelGeneration: null, targetId: 0, repeat: 0, radiusOverride: radius,
            once: seen, repeatDelay: 0, radiusMore: 1, sacrifice: null, swingAt: null, triggeredCurrent: current,
            discharge: null, strikeIndex: 0, firstContactAt: null, ruthlessMore: ruthless, priorTargets: null,
            scaledBlastChaos: 0, singleStrikeTarget: null, onHit: null, scheduledSession: session, cryExerted: false,
            prepaidRawExtra: 0, baseChaos: 0, hammer: null, retainedAttack: attack, allowMeleeSplash: false,
            contactTargets: targets);
        if (!hit.IsCompleted) throw new InvalidOperationException("Scheduled retaliation contact must not yield.");
        hit.GetAwaiter().GetResult();
    }

    static void ScheduleSwordstormLocked(Player player,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, SupportedGem source,
        SkillCatalog.Unarmed weapon, SkillCriticalRoll critical, (int X, int Y) aim, ushort sequence,
        double now, int radius, double damageMore, double ruthless)
    {
        int hits = Math.Clamp(source.Stats.GetValueOrDefault(Stat.SwordstormNumHits), 0, byte.MaxValue);
        if (hits == 0 || radius <= 0) return;
        var origin = player.Walk.Position(now);
        var direction = aim == origin
            ? (Math.Sin(player.Layout.SpawnRotation), -Math.Cos(player.Layout.SpawnRotation))
            : GroundSlamWave.Direction(origin, aim);
        // RetaliationWeaponStorm (0x141c13e50) fixes the centre one attack reach forward.
        long offset = RetaliationActionReach(player, weapon, gem.Effect, source.Stats);
        var centre = (origin.X + (int)Math.Floor(direction.Item1 * offset + .5),
            origin.Y + (int)Math.Floor(direction.Item2 * offset + .5));
        var attack = CaptureAreaAttack(player, gem.Id, source, AttackHand.Both, weapon, null,
            damageMore, damageMore, "swordstorm");
        var session = ProjectilesFor(player);
        int action = player.ActionCount, interruption = player.ActionInterruptionGeneration;
        bool Current() => PlayerStateAllowsAction(player) && player.ActionCount == action
            && player.ActionInterruptionGeneration == interruption
            && SameTriggerSource(source, SupportedSkill(player, gem.Id)) && WeaponRestricted(player, gem.Effect) is null;
        long start = session.Runtime.Now;
        // The primary release starts a .2-second countdown, then the native tick consumes one of the hits each .15 s.
        Schedule(0);

        void Schedule(int index) => session.ScheduleContact(start + ProjectileRuntime.Time(.2 + .15 * index), Current, () =>
        {
            var task = AreaSkillHitStrike(player, gem.Id, gem, origin, centre, immediate: true, sequence,
                actionToken: null, cooldownUse: null, melee: true, damageMore: 1, excludeMonster: 0,
                criticalRoll: critical, channelGeneration: null, targetId: 0, repeat: 0, radiusOverride: radius,
                once: null, repeatDelay: 0, radiusMore: 1, sacrifice: null, swingAt: null, triggeredCurrent: Current,
                discharge: null, strikeIndex: 0, firstContactAt: null, ruthlessMore: ruthless, priorTargets: null,
                scaledBlastChaos: 0, singleStrikeTarget: null, onHit: null, scheduledSession: session, cryExerted: false,
                prepaidRawExtra: 0, baseChaos: 0, hammer: null, retainedAttack: attack, allowMeleeSplash: false);
            if (!task.IsCompleted) throw new InvalidOperationException("Scheduled Swordstorm contact must not yield.");
            task.GetAwaiter().GetResult();
            if (index + 1 < hits) Schedule(index + 1);
        });
    }

    public sealed partial class ProjectileSession
    {
        // RetaliationHolySpell (3.29.3c) starts the waves eight cells from the aim, then spreads each over
        // a scaled 70-cell reach in one second. Each marker detonates after 800 ms.
        void ScheduleDivineRetribution((int X, int Y) from, (int X, int Y) aim, int waveCount, int burstCount,
            Func<int, int> scale, int baseRadius, Func<bool> current, Action<(int X, int Y), int> contact)
        {
            int waves = Math.Clamp(waveCount, 0, 32), bursts = Math.Clamp(burstCount, 0, 32);
            if (waves == 0 || bursts == 0) return;
            int radius = scale(baseRadius);
            int offset = scale(8), reach = scale(70);
            long first = Runtime.Now + ProjectileRuntime.Time(.8);
            long gap = ProjectileRuntime.Time((1000 / bursts) / 1000d);
            double angle = Math.Atan2(aim.Y - from.Y, aim.X - from.X);
            for (int wave = 0; wave < waves; wave++)
            {
                double bearing = angle + Math.Tau * wave / waves;
                var direction = new ProjectileVector(Math.Cos(bearing), Math.Sin(bearing));
                var start = Cell(new ProjectileVector(aim.X, aim.Y), direction * offset);
                for (int burst = 0; burst < bursts; burst++)
                {
                    var point = Cell(start, direction * (reach * (burst / (double)bursts)));
                    if (TraceTerrain(start, point) is not null) break;
                    var centre = ((int)point.X, (int)point.Y);
                    ScheduleContact(first + gap * burst, current, () => contact(centre, radius));
                }
            }

            static ProjectileVector Cell(ProjectileVector start, ProjectileVector offset) =>
                new(start.X + Math.Round(offset.X), start.Y + Math.Round(offset.Y));
        }
    }
}
