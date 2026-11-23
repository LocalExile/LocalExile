using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

/// <summary>
/// Molten Strike's projectile distribution, ballistic sector sampling and melee-strike target selection
/// (3.29.3c RE: 0x141a6a4b0 melee hit gate, 0x141ba8c50 target split, 0x141b6ade0 270° sector and 2–25 cell scaling,
/// 0x141b6b39a 633 ms flight clip synchronization, 0x141b6baa0 base radius 9).
/// </summary>
public static class MoltenStrikeGeometry
{
    public const int BaseExplosionRadius = 9;
    public const double BaseMinDistance = 2.0;
    public const double BaseMaxDistance = 25.0;
    public const double SectorHalfAngleRadians = 2.356194490192345; // 135 degrees (270° sector; 90° blank behind target)
    public const double DefaultClipSeconds = 0.633;

    public static int[] DistributeCount(int totalProjectiles, int hitTargetCount)
    {
        if (totalProjectiles <= 0 || hitTargetCount <= 0) return [];
        int perTarget = totalProjectiles / hitTargetCount;
        int remainder = totalProjectiles % hitTargetCount;
        var counts = new int[hitTargetCount];
        for (int i = 0; i < hitTargetCount; i++) counts[i] = perTarget + (i < remainder ? 1 : 0);
        return counts;
    }

    public static (double MinDistance, double MaxDistance) DistanceBounds(int radiusPercent, int projectileSpeedPct,
        int chainingRangePct)
    {
        int aoePct = Math.Max(0, 100 + radiusPercent);
        double speedFactor = Math.Max(0, 100 + projectileSpeedPct) / 100.0;
        double chainFactor = Math.Max(0, 100 + chainingRangePct) / 100.0;
        int minScaled = (int)((int)(BaseMinDistance * aoePct / 100.0) * speedFactor * chainFactor);
        int maxScaled = (int)((int)(BaseMaxDistance * aoePct / 100.0) * speedFactor * chainFactor);
        double min = Math.Max(BaseMinDistance, minScaled);
        double max = Math.Max(min, maxScaled);
        return (min, max);
    }

    public static int EffectiveAnimationSpeedPct(int statAnimSpeedPct, int projectileCount) =>
        statAnimSpeedPct + 10 * Math.Max(0, projectileCount);

    public static ProjectileVector SwingOrigin((int X, int Y) playerAt, (int X, int Y) aim, double weaponRange)
    {
        double dx = aim.X - playerAt.X, dy = aim.Y - playerAt.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len <= 1e-6) return new(playerAt.X, playerAt.Y);
        double reach = Math.Min(len, Math.Max(1.0, weaponRange * 0.5));
        return new(playerAt.X + dx / len * reach, playerAt.Y + dy / len * reach);
    }

    public static (ProjectileVector Target, double Speed) SampleBall(ProjectileVector origin,
        ProjectileVector aimDirection, double minDist, double maxDist, double clipSeconds, int animSpeedPct,
        Random random)
    {
        var forward = aimDirection.Length > 1e-6 ? aimDirection.Unit : new ProjectileVector(1, 0);
        double angleOffset = (random.NextDouble() * 2 - 1) * SectorHalfAngleRadians;
        var dir = forward.Rotate(angleOffset);
        double dist = Math.Max(BaseMinDistance, minDist + (maxDist - minDist) * random.NextDouble());
        var target = origin + dir * dist;
        double animScale = Math.Max(0.01, 1 + animSpeedPct / 100.0);
        double flightSeconds = Math.Max(0.001, clipSeconds / animScale * (0.9 + random.NextDouble() * 0.2));
        return (target, dist / flightSeconds);
    }

    public static uint? SelectGroundStrikeTarget(IEnumerable<(uint Id, (int X, int Y) Pos, int Size)> candidates,
        (int X, int Y) playerAt, (int X, int Y) aim, double weaponRange)
    {
        double dx = aim.X - playerAt.X, dy = aim.Y - playerAt.Y;
        double aimLen = Math.Sqrt(dx * dx + dy * dy);
        (double X, double Y) tip = aimLen <= weaponRange || aimLen <= 0
            ? (aim.X, aim.Y)
            : (playerAt.X + dx / aimLen * weaponRange, playerAt.Y + dy / aimLen * weaponRange);
        uint? bestId = null;
        double bestTip = double.PositiveInfinity, bestPlayer = double.PositiveInfinity;
        foreach (var (id, pos, size) in candidates)
        {
            double distPlayer = Math.Sqrt((pos.X - playerAt.X) * (double)(pos.X - playerAt.X)
                + (pos.Y - playerAt.Y) * (double)(pos.Y - playerAt.Y));
            if (distPlayer > weaponRange + Math.Max(0, size) + 2) continue;
            double distTip = Math.Sqrt((pos.X - tip.X) * (pos.X - tip.X) + (pos.Y - tip.Y) * (pos.Y - tip.Y));
            if (distTip < bestTip || (Math.Abs(distTip - bestTip) < 1e-9
                && (distPlayer < bestPlayer || (Math.Abs(distPlayer - bestPlayer) < 1e-9 && id < (bestId ?? uint.MaxValue)))))
            {
                bestId = id;
                bestTip = distTip;
                bestPlayer = distPlayer;
            }
        }
        return bestId;
    }
}

public static partial class Game
{
    const ushort MoltenStrikeFifthAttackBuffRow = 2353;

    /// <summary>Resolves Molten Strike's melee contact delay against the skill clip or the class ACT's defaultAttack pool
    /// (never clamped by EarlyAllowance or shifted earlier than the client's late pair view).</summary>
    public static double MoltenStrikeContactDelay(Player player, int effect, double attackSeconds, ushort sequence,
        out string? clip, out double fraction, out double earliestSeconds)
    {
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject;
        var weapon = AnimationWeaponProfileFor(player);
        double latestLimit = Math.Max(0, attackSeconds - AttackTiming.LandMargin);
        if (SkillsFor(player).EffectId(effect) is { } effectId
            && Animations.SkillAttackContact(ao, weapon, SkillClipName(effectId)) is { } found)
        {
            double delay = AttackTiming.ContactDelay(attackSeconds, found.Contact, 0, early: false);
            (clip, fraction, earliestSeconds) = (found.Name, found.Contact, delay);
            return delay;
        }
        if (Animations.DefaultAttackContactBounds(ao, weapon, sequence, attackSeconds) is { } bounds)
        {
            double delay = Math.Clamp(bounds.LatestSeconds, 0, latestLimit);
            earliestSeconds = Math.Clamp(bounds.EarliestSeconds, 0, delay);
            clip = bounds.Animation;
            fraction = attackSeconds > 0 ? delay / attackSeconds : 0;
            return delay;
        }
        clip = null;
        fraction = Animations.AttackContact(ao, weapon);
        double fallback = AttackTiming.ContactDelay(attackSeconds, fraction, 0, early: false);
        earliestSeconds = fallback;
        return fallback;
    }

    public static Task ScheduleMoltenStrike(Player player,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) to, int recoveryGeneration, uint targetId = 0, ushort sequence = 0,
        int? actionToken = null, CooldownUseTicket? cooldownUse = null, SkillCriticalRoll? criticalRoll = null,
        double? swingAt = null)
    {
        var hitPlan = SkillHitPlan.ForSkill(SkillsFor(player), gem.Effect, gem.Level, gem.Quality);
        if (!hitPlan.CanHit) return Task.CompletedTask;
        var initialStats = SupportedSkill(player, skillId).Stats;
        int InitialStat(uint k) => initialStats.GetValueOrDefault(k);
        if (InitialStat(Stat.BaseDealNoDamage) != 0) return Task.CompletedTask;

        double ruthless = RuthlessBlowMore(player, skillId, InitialStat);
        int meleeRepeats = MeleeAttackRepeats(player, skillId);
        double attackSeconds = SupportedAttackSeconds(player, skillId, sequence);
        double repeatInterval = meleeRepeats > 0 ? attackSeconds : 0;
        double acceptedAt = swingAt ?? AreaTime(player);
        var roll = criticalRoll ?? CaptureSkillCriticalRoll(player, skillId);
        bool hasFifthMechanic = InitialStat(Stat.MoltenStrikeEvery5thAttackFireXAdditionalProjectiles) > 0
            || InitialStat(Stat.MoltenStrikeEvery5thAttackProjectilesDamagePlusPctFinal) != 0;
        bool fifthAttack = false;
        ushort armFifthBuff = 0;
        if (hasFifthMechanic)
        {
            if (!player.MoltenStrikeFifthBuffs.TryGetValue(skillId, out ushort buffId) || buffId == 0)
            {
                player.MoltenStrikeFifthBuffs[skillId] = buffId = 1;
                armFifthBuff = buffId;
            }
            int next = player.MoltenStrikeAttackCounts.GetValueOrDefault(skillId) + 1;
            if (next >= 5)
            {
                fifthAttack = true;
                player.MoltenStrikeAttackCounts[skillId] = 0;
            }
            else
            {
                player.MoltenStrikeAttackCounts[skillId] = next;
            }
        }
        var jobs = new List<Task>();
        for (int repeat = 0; repeat <= meleeRepeats; repeat++)
            jobs.Add(ExecuteMoltenStrikeSwing(player, skillId, gem, from, to,
                recoveryGeneration, targetId, sequence, actionToken, cooldownUse, roll, acceptedAt,
                repeat, meleeRepeats, attackSeconds, repeat * repeatInterval, ruthless,
                fifthAttack, repeat == 0 ? armFifthBuff : (ushort)0));
        return Task.WhenAll(jobs);
    }

    public static async Task ExecuteMoltenStrikeSwing(Player player, ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) to, int recoveryGeneration, uint targetId, ushort sequence,
        int? actionToken, CooldownUseTicket? cooldownUse, SkillCriticalRoll criticalRoll, double? swingAt,
        int repeat, int totalRepeats, double attackSeconds, double repeatDelay, double ruthless,
        bool fifthAttack = false, ushort armFifthBuff = 0)
    {
        var paidVaal = SkillsFor(player).VaalBudget(gem.Effect, gem.Level) is null ? null : player.VaalUse;
        bool Current() => PlayerStateAllowsAction(player)
            && player.ActionInterruptionGeneration == recoveryGeneration
            && (SkillsFor(player).VaalBudget(gem.Effect, gem.Level) is null
                || paidVaal is not null && VaalAdmissionCurrent(player, paidVaal));
        if (!Current()) return;

        var startStats = SupportedSkill(player, skillId).Stats;
        bool cannotCancel = Stat.CannotCancelSkillBeforeContactPoint is var cancelKey
            && startStats.GetValueOrDefault(cancelKey) != 0;
        bool vaalBehaviour = SkillsFor(player).BehaviourVariation(gem.Effect) == 2;

        double delay;
        double earliestContact;
        string? contactClip;
        double contactFraction;
        delay = MoltenStrikeContactDelay(player, gem.Effect, attackSeconds, sequence,
            out contactClip, out contactFraction, out earliestContact);
        double swung = swingAt ?? AreaTime(player);
        // From here on the swing has connected for the client: a later use (accepted from 1 - EarlyAllowance of the
        // attack time, AttackReady) or a queued Move no longer takes it back, as AreaSkillHit's holdsItsSwing and Lightning
        // Strike keep theirs. A later Multistrike swing still belongs to the original action and is cancelled with it.
        double keptFrom = Math.Min(earliestContact,
            Math.Max(0, attackSeconds * (1 - AttackTiming.EarlyAllowance) - AttackTiming.LandMargin));
        double earliestAt = swung + keptFrom;

        int action = actionToken ?? cooldownUse?.ActionToken ?? player.ActionCount;
        bool Replaced()
        {
            if (cooldownUse?.Instant == true || player.ActionCount == action) return false;
            if (repeat > 0) return true;
            if (cannotCancel) return false;
            if (AreaTime(player) >= earliestAt && PlayerStateAllowsAction(player)) return false;
            Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {gem.Name}: the swing was replaced by action "
                + $"{player.ActionCount} before its hit (action {action}), no hit lands");
            return true;
        }
        if (Replaced()) return;

        List<uint> spirit = [];
        (int X, int Y)? echo = null;
        byte[]? memoryBuff = null;
        using (ProjectileMutation(player))
            (spirit, echo, memoryBuff) = StrikeExtrasLocked(player, skillId, gem.Effect, startStats, from, to,
                sequence, targetId, ProjectileWorldTime(player));
        if (memoryBuff is not null && repeat == 0)
            await SendWorldRecord(player, player.Id, memoryBuff,
                $"0x01c1 player buff skill_location_memory for skill 0x{skillId:x4}");
        if (armFifthBuff != 0)
            player.Area.Buffs.Set(player.Id, new(MoltenStrikeFifthAttackBuffRow, player.Id, skillId,
                ActiveSkill: SkillsFor(player).ActionHash(gem.Effect) is ushort hash ? (skillId, hash) : null, Stacks: 1, Tag: skillId));

        double remaining = swung + delay + repeatDelay - AreaTime(player);
        if (remaining > 0)
            await AreaDelay(player, remaining);
        await WhilePaused(player);
        if (Replaced() || player.Closed || !Current()) return;

        double repeatMore = repeat > 0 ? MeleeRepeatDamageMore(player, skillId, repeat) : 1;
        var results = new List<(uint Id, SpawnedMonster Monster, MonsterHit Hit, int Damage, bool Critical, string? Resistance,
            List<byte[]>? Ignited, string IgniteLog, List<byte[]>? Bled, string BleedLog, StunHit Stun)>();
        var deaths = new List<(uint Id, SpawnedMonster M)>();
        var reflected = new List<MonsterElementalAilments.Effect>();
        Dictionary<uint, int>? playerStats = null;
        int emitted = 0;
        int hitTargets = 0;
        int totalTargets = 0;

        using (ProjectileMutation(player))
        {
            if (!Current() || Replaced() || player.Closed || player.LeavingArea || player.RageRetired || player.PlayerLife == 0 || player.Paused)
                return;
            double now = ProjectileWorldTime(player);
            var playerAt = player.Walk.Position(now);
            // A dual-wielded use's hand (AttackHands): its reach, hit, chance and local modifiers.
            var hand = AttackHandFor(player, skillId, sequence);
            var weapon = HandWeapon(player, hand);
            var skillStats = SupportedSkill(player, skillId).Stats;
            int Stat(uint k) => skillStats.GetValueOrDefault(k);
            if (Stat(global::Stat.BaseDealNoDamage) != 0) return;

            var targets = new List<(uint Id, SpawnedMonster Monster, bool IsStrikeTarget)>();
            var struck = new HashSet<uint>();
            uint? primaryId = targetId != 0
                ? targetId
                : MoltenStrikeGeometry.SelectGroundStrikeTarget(
                    player.Area.Monsters.Where(e => e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0 && e.Value.OwnerId is null)
                        .Select(e => (e.Key, e.Value.Walk.Position(now), LivingCollision.Size(e.Value))),
                    playerAt, to, weapon.Range);
            if (primaryId is uint pid && player.Area.Monsters.TryGetValue(pid, out var pm)
                && pm.Hostile && !pm.Dead && pm.Life > 0 && pm.OwnerId is null
                && Dist(playerAt, pm.Walk.Position(now)) <= weapon.Range + Math.Max(0, LivingCollision.Size(pm)) + 2
                && struck.Add(pid))
                targets.Add((pid, pm, true));
            foreach (uint sid in spirit)
                if (struck.Add(sid) && player.Area.Monsters.TryGetValue(sid, out var sm) && sm.Hostile && !sm.Dead && sm.Life > 0 && sm.OwnerId is null)
                    targets.Add((sid, sm, true));
            if (echo is { } echoAt
                && MoltenStrikeGeometry.SelectGroundStrikeTarget(
                    player.Area.Monsters.Where(e => e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0 && e.Value.OwnerId is null && !struck.Contains(e.Key))
                        .Select(e => (e.Key, e.Value.Walk.Position(now), LivingCollision.Size(e.Value))),
                    echoAt, echoAt, weapon.Range) is uint eid
                && player.Area.Monsters.TryGetValue(eid, out var em) && struck.Add(eid))
                targets.Add((eid, em, true));

            var splashMore = new Dictionary<uint, double>();
            var merged = SkillAreaStats(player, SupportedSkill(player, skillId).Stats, gem.Effect);
            int SplashStat(uint k) => merged.GetValueOrDefault(k);
            if (targets.Count > 0 && MeleeSplash.Settings(SplashStat) is { } splash)
                foreach (var (_, strikeMonster, _) in targets.ToArray())
                {
                    var splashAt = strikeMonster.Walk.Position(now);
                    foreach (var (sid, sm) in player.Area.Monsters)
                        if (sm.Hostile && !sm.Dead && sm.Life > 0 && sm.OwnerId is null && struck.Add(sid)
                            && Dist(sm.Walk.Position(now), splashAt) <= splash.Radius)
                        {
                            targets.Add((sid, sm, false));
                            splashMore[sid] = splash.More;
                        }
                }

            totalTargets = targets.Count;
            if (targets.Count == 0 && !vaalBehaviour)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {gem.Name}: melee swing whiffed at ({to.X}, {to.Y}), no projectiles emitted (t={AreaTime(player):0.000})");
                return;
            }

            playerStats = new Dictionary<uint, int>(CharacterPlayerStats(player));
            var local = HandLocalSources(player, hand);
            var swingWeapon = OffHandSwing(player, hand);
            var pen = Penetration.Of(playerStats, Stats, attack: true, skillStats, local);
            var mods = HitResistMods(playerStats, skillStats);
            var meleeHit = SupportedSwingHit(player, skillId, hand, w => SupportedHitFor(player, skillId, suppress: true, weapon: w));
            if (ruthless != 1) meleeHit = meleeHit.Scale(ruthless);
            int criticalMultiplierBase = SupportedCriticalMultiplier(player, skillId);
            var accuracy = SupportedAccuracy(player, skillId, hand);
            var leech = HitLeechSources(playerStats, true, skillStats, local, melee: true);
            var impale = ImpaleStats.From(playerStats, Stats, true, skillStats, local);
            var criticalImpale = ImpaleStats.From(playerStats, Stats, true, skillStats, local, critical: true);
            var ignite = SupportedIgnite(player, skillId, spell: false, playerStats, swingWeapon);
            var bleed = SupportedBleed(player, skillId, spell: false, playerStats, swingWeapon, melee: true);
            var poison = SupportedPoison(player, skillId, spell: false, playerStats, swingWeapon);
            ignite = RepeatAilment(ignite, repeatMore);
            bleed = RepeatAilment(bleed, repeatMore);
            poison = RepeatAilment(poison, repeatMore);
            var criticalTrigger = CaptureCriticalTriggerSource(player, skillId);
            var meleeKillTrigger = CaptureMeleeKillTriggerSource(player, skillId);
            bool fortificationGranted = false;
            bool rageGranted = false;
            var hitOrigins = new List<ProjectileVector>();

            foreach (var (id, m, isStrikeTarget) in targets)
            {
                if (m.Dead || m.Life == 0 || !player.Area.Monsters.TryGetValue(id, out var current) || !ReferenceEquals(current, m))
                    continue;
                var contactHit = WithMeleeProximityLocked(player, playerStats, skillStats, m, meleeHit, now);
                int hitChance = AttackHitChanceAt(player, id, m, now, accuracy, playerStats);
                if (!accuracy.CannotBeEvaded && !m.AttackEvasion.Hits(hitChance, now, player.AttackAccuracyRandom))
                {
                    Console.WriteLine($"[gateway] {player.Connection.Remote} {gem.Name} on 0x{id:x} ({m.Label}) evaded ({hitChance}% chance to hit)");
                    continue;
                }
                if (MonsterBlocks(m, AreaRng(player), spell: false, now, skillStats))
                {
                    Console.WriteLine($"[gateway] {player.Connection.Remote} {gem.Name} on 0x{id:x} ({m.Label}) blocked");
                    continue;
                }
                var gambler = GamblerFor(playerStats);
                var rolled = WithConditionalSupportDamageLocked(player, skillStats, attack: true, m,
                    GameEvents.WithOutgoingDamage(player, playerStats, m, (gambler with { DamageMorePct = 0 })
                        .ForSkill(Stat, spell: false, finalRepeat: repeat == totalRepeats)
                        .ForWeapon(local).RollOutgoingHit(contactHit, AreaRng(player))), now);
                if (m.HallowingFlameUntil > now && rolled[DamageType.Physical].Min is > 0 and var flamePhys)
                {
                    double flameFire = flamePhys * m.HallowingFlamePct / 100.0;
                    rolled = rolled.Add(DamageType.Fire, new(flameFire, flameFire));
                    m.HallowingFlameUntil = 0;
                }
                int criticalChance = CriticalChanceOn(player, id, SupportedCriticalChance(player, skillId, hand),
                    SupportedCriticalChanceVsBlinded(player, skillId, release: false, SwingWeaponCritical(player, hand)),
                    SupportedCriticalChanceVsGround(player, skillId, release: false, SwingWeaponCritical(player, hand)));
                bool critical = RollCriticalStrike(player, criticalChance, skillStats, attack: true, skillRoll: criticalRoll, playerStats: playerStats)
                    && EvasionEntropy.ConfirmsCritical(hitChance, player.AttackAccuracyRandom);
                int multiplier = CriticalMultiplierOn(m, criticalMultiplierBase);
                var resonance = WithResonanceHitLocked(player, skillStats, rolled, mods, pen, now);
                rolled = resonance.Packet;
                var (damage, resistance, mitigated) = MonsterHitDamage(m, rolled, critical, attack: true, now,
                    resonance.Mods, resonance.Penetration,
                    gambler.InvertsResistance(AreaRng(player)));
                var taken = MonsterTakenComponents(m, mitigated, spell: false, now);
                if (critical) damage = DamageModel.CriticalDamage(damage, multiplier);
                double more = repeatMore * splashMore.GetValueOrDefault(id, 1);
                if (more != 1) damage = (int)Math.Floor(damage * Math.Max(0, more));
                LeechOnHitLocked(player, m, taken, damage, leech);
                damage = WithImpaleHitLocked(player, m, damage, rolled, critical, criticalMultiplierBase,
                    critical ? criticalImpale : impale, more).Damage;
                var stun = OwnerStun(player, m, damage, mitigated, melee: true, spell: false, critical, playerStats, skillStats);
                var mAt = m.Walk.Position(now);
                var rampageSource = RampageOnMeleeHitLocked(player, m, melee: true, playerStats);
                uint lifeBefore = m.Life;
                var applied = ApplyMonsterHitLocked(player, m, damage, rampageSource, playerStats,
                    cull: Cull.Of(playerStats, skillStats, critical, spell: false));
                if (applied is not { } hit) continue;
                if (hit.Life < lifeBefore) RecordResonanceHitLocked(player, m, skillStats, rolled, now);
                if (weapon.ItemClass is not null) player.RecentEvents.Record(StatConditions.HitEnemyWithWeapon, now);
                if (hit.Life == 0) RecordTinctureKill(m, TinctureKillBonusFor(player, playerStats, hand));
                // Cruelty, as AreaSkillHit's hit grants it: this pipeline is a copy of that one.
                CrueltyOnHitLocked(player, skillId, damage, lifeBefore, m.MaxLife, skillStats, now);

                if (isStrikeTarget) hitOrigins.Add(ProjectileSession.Vector(mAt));
                ReleaseCriticalTrigger(player, ProjectilesFor(player), criticalTrigger, critical, m.Hostile, mAt, now);
                if (hit.Life == 0) ReleaseMeleeKillTrigger(player, ProjectilesFor(player),
                    meleeKillTrigger, true, m.Hostile, mAt, now);
                player.RecentEvents.RecordCriticalOutcome(now, critical);
                if (ElementalOverloadCritLocked(player, skillId, null, playerStats, critical) is { } eoBuff)
                    ProjectilesFor(player).Packet(eoBuff, $"elemental overload on skill 0x{skillId:x4}");
                if (global::Stat.BaseKilledMonsterDroppedItemQuantityPlusPct is var quantityKey)
                    m.KilledWithItemQuantityPct = skillStats.GetValueOrDefault(quantityKey);
                if (global::Stat.BaseKilledMonsterDroppedItemRarityPlusPct is var rarityKey)
                    m.KilledWithItemRarityPct = skillStats.GetValueOrDefault(rarityKey);
                SoulEaterHit(player, m);
                if (hit.Life == 0) DevourOnKill(player, m, skillStats);
                AilmentsOnHitLocked(player, id, m, mitigated, spell: false, critical, multiplier, playerStats, skillStats, now,
                    AreaRng(player), reflected: reflected, poison: poison, sourceSkill: skillId,
                    blind: BlindHitInput.Capture(playerStats, skillStats),
                    attackGains: AttackAilmentGains.Capture(playerStats, attack: true));
                List<byte[]>? ignited = null, bled = null;
                string igniteLog = "", bleedLog = "";
                if (m.Life > 0 && ignite.CanIgnite(rolled) && IgniteOnHitLocked(player, id, m, ignite, critical, now) is { } ignitePackets)
                    (ignited, igniteLog) = (ignitePackets, IgnitedLog(id, m, critical, now));
                if (m.Life > 0 && rolled[DamageType.Physical].Max > 0 && BleedOnHitLocked(player, id, m, bleed, critical, now) is { } cut)
                    (bled, bleedLog) = (cut, BleedingLog(id, m, critical, now));
                if (hit.StageStarts)
                {
                    CommitBossStage(player, id, m, now);
                    hit = hit with { StageStarts = false };
                }
                // As AreaSkillHit's: the stun carries the hit's Fortification damage, so "Melee Hits which Stun have X%
                // chance to Fortify" rolls for this skill too.
                var fortificationDamage = RawFortificationDamage(rolled, critical, multiplier);
                bool fortifiedThisHit = !fortificationGranted && (fortificationGranted = FortificationOnHitLocked(player, m,
                    fortificationDamage, melee: true, playerStats, skillStats));
                stun = stun with { FortificationDamage = fortificationDamage, Fortified = fortifiedThisHit };
                if (!rageGranted)
                    rageGranted = RageOnHitLocked(player, attack: true, melee: true, critical, skillStats, playerStats: playerStats);
                CombatRushOnHit(player, skillStats, now);
                HallowOnMeleeHit(player, m, skillStats, now);
                ExcommunicateOnMeleeHit(player, m, skillStats, now);
                results.Add((id, m, hit, damage, critical, resistance, ignited, igniteLog, bled, bleedLog, stun));
            }

            hitTargets = hitOrigins.Count;
            if (hitOrigins.Count == 0 && vaalBehaviour)
                hitOrigins.Add(MoltenStrikeGeometry.SwingOrigin(playerAt, to, weapon.Range));
            if (hitOrigins.Count > 0)
            {
                var session = ProjectilesFor(player);
                emitted = EmitMoltenStrikeBallsLocked(player, session, skillId, gem, playerAt, to, criticalRoll,
                    sequence, (uint)(totalRepeats - repeat), delay, repeatMore, hitOrigins, AreaRng(player), fifthAttack);
            }
        }

        var supportedStats = SupportedSkill(player, skillId).Stats;
        foreach (var r in results)
        {
            if (r.Critical) _ = GainFlaskChargesOnCrit(player);
            if (r.Critical) await MaybeCreateProfaneGround(player, r.Monster.Walk.Position(ProjectileWorldTime(player)));
            await NotifyMonsterHit(player, r.Id, r.Monster, r.Hit, r.Damage,
                $"{gem.Name}{(r.Critical ? " (critical)" : "")}{(r.Resistance is null ? "" : $", {r.Resistance}")}",
                melee: true, sequence: sequence, deaths: deaths, stun: r.Stun);
            await SendDotBuff(player, r.Ignited, r.IgniteLog);
            await SendDotBuff(player, r.Bled, r.BleedLog);
            ChargesOnHit(player, r.Monster, r.Critical, spell: false, gem.Effect, gem.Level, gem.Quality, primary: r.Id == results[0].Id,
                skillStats: supportedStats);
        }
        if (reflected.Count > 0 && playerStats is not null)
            using (ProjectileMutation(player)) ReflectAilmentsLocked(player, reflected, ProjectileWorldTime(player), playerStats, AreaRng(player));
        foreach (var (deadId, dead) in deaths) await MonsterKilled(player, deadId, dead);
        string contactNote = contactClip is null ? "the attack1 fallback" : contactClip;
        Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {gem.Name}: melee hit {hitTargets}/{totalTargets} monster(s), "
            + $"launched {emitted} molten projectile(s), contact {contactFraction:0.###} of the attack ({contactNote}) (t={AreaTime(player):0.000})");
    }

    /// <summary>Distributes and releases Molten Strike's balls across struck targets. Call in the area's frame.</summary>
    public static int EmitMoltenStrikeBallsLocked(Player player, ProjectileSession session, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) playerAt, (int X, int Y) aim, SkillCriticalRoll criticalRoll, ushort sequence,
        uint repeatsLeft, double releaseDelay, double repeatMore, IReadOnlyList<ProjectileVector> hitOrigins,
        Random random, bool fifthAttack = false)
    {
        if (hitOrigins.Count == 0) return 0;
        ProjectileUse baseUse;
        try
        {
            baseUse = CaptureGemProjectile(player, session, skillId, gem, aim, criticalRoll, sequence, repeatsLeft, releaseDelay);
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"[gateway] {session.Player.Connection.Remote} {gem.Name} projectile release skipped: {ex.Message}");
            return 0;
        }
        var own = SupportedSkill(player, skillId).Stats;
        var merged = MergeSkillStats(CharacterPlayerStats(player), own);
        int Merged(uint k) => merged.GetValueOrDefault(k);

        int totalCount = baseUse.Profile.Count;
        var shot = (ProjectileOffense)baseUse.Payload!;
        double damageScale = repeatMore;
        if (fifthAttack)
        {
            int fifthExtra = Math.Max(0, Merged(Stat.MoltenStrikeEvery5thAttackFireXAdditionalProjectiles));
            int fifthMorePct = Merged(Stat.MoltenStrikeEvery5thAttackProjectilesDamagePlusPctFinal);
            totalCount += fifthExtra;
            if (fifthMorePct != 0) damageScale *= Math.Max(0, 1 + fifthMorePct / 100.0);
        }
        if (damageScale != 1)
            shot = shot with { Hit = shot.Hit.Scale(damageScale), AreaHit = shot.AreaHit?.Scale(damageScale),
                Ignite = RepeatAilment(shot.Ignite, damageScale), Bleed = RepeatAilment(shot.Bleed, damageScale),
                Poison = RepeatAilment(shot.Poison, damageScale) };

        var areaStats = SkillAreaStats(player, own, gem.Effect);
        int AreaGet(uint name) => name == SkillArea.BaseIncreased
            ? own.GetValueOrDefault(name) + areaStats.GetValueOrDefault(Stat.MoltenStrikeRadiusPlusPct)
            : areaStats.GetValueOrDefault(name);
        int radiusPercent = SkillArea.RadiusPercent(AreaGet, spell: false);
        var (minDist, maxDist) = MoltenStrikeGeometry.DistanceBounds(radiusPercent, Merged(Stat.ProjectileSpeedPlusPct), Merged(Stat.ChainingRangePlusPct));
        double clipSeconds = (SkillsFor(player).Clips(baseUse.Profile.Id)?.Flight?.Ms ?? 633) / 1000.0;
        // Client 0x141b6b3cf..0x141b6b411 adds 10 * base projectile count (before Zenith's 5th-attack +5) to molten_strike_projectile_animation_speed_+%.
        int animSpeedPct = MoltenStrikeGeometry.EffectiveAnimationSpeedPct(
            Merged(Stat.MoltenStrikeProjectileAnimationSpeedPlusPct), baseUse.Profile.Count);
        int[] counts = MoltenStrikeGeometry.DistributeCount(totalCount, hitOrigins.Count);
        var playerVec = ProjectileSession.Vector(playerAt);
        var aimVec = ProjectileSession.Vector(aim);
        int launched = 0;
        for (int t = 0; t < hitOrigins.Count; t++)
        {
            int count = counts[t];
            if (count <= 0) continue;
            var origin = hitOrigins[t];
            var forward = origin - playerVec;
            if (forward.Length <= 1e-6) forward = aimVec - playerVec;
            var targets = new ProjectileVector[count];
            var speeds = new double[count];
            for (int k = 0; k < count; k++)
                (targets[k], speeds[k]) = MoltenStrikeGeometry.SampleBall(origin, forward, minDist, maxDist,
                    clipSeconds, animSpeedPct, random);
            var groupUse = baseUse with
            {
                Origin = origin,
                Aim = targets[0],
                Payload = shot,
                Profile = baseUse.Profile with { Count = count },
                ProjectileTargets = targets,
                ProjectileSpeeds = speeds
            };
            session.ReleaseNow(groupUse);
            launched += count;
        }
        return launched;
    }
}
