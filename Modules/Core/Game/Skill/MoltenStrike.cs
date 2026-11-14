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
    public static double MoltenStrikeContactDelay(ClientState conn, int effect, double attackSeconds, ushort sequence,
        out string? clip, out double fraction, out double earliestSeconds)
    {
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject;
        var weapon = WeaponProfileFor(conn, activeSet: true);
        double latestLimit = Math.Max(0, attackSeconds - AttackTiming.LandMargin);
        if (SkillsFor(conn).EffectId(effect) is { } effectId
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

    public static Task ScheduleMoltenStrike(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) to, int recoveryGeneration, uint targetId = 0, ushort sequence = 0,
        int? actionToken = null, CooldownUseTicket? cooldownUse = null, SkillCriticalRoll? criticalRoll = null,
        double? swingAt = null)
    {
        var hitPlan = SkillHitPlan.ForSkill(SkillsFor(conn), gem.Effect, gem.Level, gem.Quality);
        if (!hitPlan.CanHit) return Task.CompletedTask;
        var initialStats = SupportedSkill(conn, skillId).Stats;
        int InitialStat(string name) => Stats.Key(name) is uint k ? initialStats.GetValueOrDefault(k) : 0;
        if (InitialStat("base_deal_no_damage") != 0) return Task.CompletedTask;

        double ruthless = RuthlessBlowMore(conn, skillId, InitialStat);
        int meleeRepeats = MeleeAttackRepeats(conn, skillId);
        double attackSeconds = SupportedAttackSeconds(conn, skillId);
        double repeatInterval = meleeRepeats > 0 ? attackSeconds / (meleeRepeats + 1) : 0;
        double acceptedAt = swingAt ?? conn.Clock.Elapsed.TotalSeconds;
        var roll = criticalRoll ?? CaptureSkillCriticalRoll(conn, skillId);
        bool hasFifthMechanic = InitialStat("molten_strike_every_5th_attack_fire_X_additional_projectiles") > 0
            || InitialStat("molten_strike_every_5th_attack_projectiles_damage_+%_final") != 0;
        bool fifthAttack = false;
        ushort armFifthBuff = 0;
        if (hasFifthMechanic)
        {
            lock (conn.Monsters)
            {
                if (!conn.MoltenStrikeFifthBuffs.TryGetValue(skillId, out ushort buffId) || buffId == 0)
                {
                    conn.MoltenStrikeFifthBuffs[skillId] = buffId = conn.NextBuff++;
                    armFifthBuff = buffId;
                }
                int next = conn.MoltenStrikeAttackCounts.GetValueOrDefault(skillId) + 1;
                if (next >= 5)
                {
                    fifthAttack = true;
                    conn.MoltenStrikeAttackCounts[skillId] = 0;
                }
                else
                {
                    conn.MoltenStrikeAttackCounts[skillId] = next;
                }
            }
        }
        var jobs = new List<Task>();
        for (int repeat = 0; repeat <= meleeRepeats; repeat++)
            jobs.Add(ExecuteMoltenStrikeSwing(remote, stream, ciphers, conn, skillId, gem, from, to,
                recoveryGeneration, targetId, sequence, actionToken, cooldownUse, roll, acceptedAt,
                repeat, meleeRepeats, attackSeconds, repeat * repeatInterval, ruthless,
                fifthAttack, repeat == 0 ? armFifthBuff : (ushort)0));
        return Task.WhenAll(jobs);
    }

    public static async Task ExecuteMoltenStrikeSwing(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) from, (int X, int Y) to, int recoveryGeneration, uint targetId, ushort sequence,
        int? actionToken, CooldownUseTicket? cooldownUse, SkillCriticalRoll criticalRoll, double? swingAt,
        int repeat, int totalRepeats, double attackSeconds, double repeatDelay, double ruthless,
        bool fifthAttack = false, ushort armFifthBuff = 0)
    {
        var paidVaal = SkillsFor(conn).VaalBudget(gem.Effect, gem.Level) is null ? null : conn.VaalUse;
        bool Current() => PlayerStateAllowsAction(conn)
            && conn.ActionInterruptionGeneration == recoveryGeneration
            && (SkillsFor(conn).VaalBudget(gem.Effect, gem.Level) is null
                || paidVaal is not null && VaalAdmissionCurrent(conn, paidVaal));
        if (!Current()) return;

        var startStats = SupportedSkill(conn, skillId).Stats;
        bool cannotCancel = Stats.Key("cannot_cancel_skill_before_contact_point") is uint cancelKey
            && startStats.GetValueOrDefault(cancelKey) != 0;
        bool vaalBehaviour = SkillsFor(conn).BehaviourVariation(gem.Effect) == 2;

        double delay;
        double earliestContact;
        string? contactClip;
        double contactFraction;
        lock (conn.Monsters)
            delay = MoltenStrikeContactDelay(conn, gem.Effect, attackSeconds, sequence,
                out contactClip, out contactFraction, out earliestContact);
        double swung = swingAt ?? conn.Clock.Elapsed.TotalSeconds;
        double earliestAt = swung + earliestContact + repeatDelay;

        int action = actionToken ?? cooldownUse?.ActionToken ?? conn.ActionCount;
        bool Replaced()
        {
            if (cannotCancel || cooldownUse?.Instant == true || conn.ActionCount == action) return false;
            double nowAt = conn.Clock.Elapsed.TotalSeconds;
            bool moving;
            lock (conn.Monsters) moving = conn.Walk.Moving(nowAt);
            // After the stance pool's earliest contact frame (e.g. 01b), a Move order cancels backswing rather than windup.
            if (nowAt >= earliestAt && PlayerStateAllowsAction(conn) && moving)
                return false;
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: the swing was replaced by action "
                + $"{conn.ActionCount} before its hit (action {action}), no hit lands");
            return true;
        }
        if (Replaced()) return;

        List<uint> spirit = [];
        (int X, int Y)? echo = null;
        byte[]? memoryBuff = null;
        using (ProjectileMutation(conn))
            (spirit, echo, memoryBuff) = StrikeExtrasLocked(conn, skillId, gem.Effect, startStats, from, to,
                sequence, targetId, ProjectileWorldTime(conn));
        if (memoryBuff is not null && repeat == 0)
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, memoryBuff,
                $"0x01c1 player buff skill_location_memory for skill 0x{skillId:x4}");
        if (armFifthBuff != 0)
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId,
                InstanceEntities.AddBuff(PlayerId, armFifthBuff, MoltenStrikeFifthAttackBuffRow, PlayerId, skillId,
                    activeSkill: SkillsFor(conn).ActionHash(gem.Effect) is ushort hash ? (skillId, hash) : null,
                    stacks: 1),
                $"0x01c1 player buff molten_strike_fifth_attack_counter armed for skill 0x{skillId:x4}");

        double remaining = swung + delay + repeatDelay - conn.Clock.Elapsed.TotalSeconds;
        if (remaining > 0)
            await Task.Delay(TimeSpan.FromSeconds(remaining));
        await WhilePaused(conn);
        if (Replaced() || conn.Closed || !Current()) return;

        double repeatMore = repeat > 0 ? MeleeRepeatDamageMore(conn, skillId, repeat) : 1;
        var results = new List<(uint Id, SpawnedMonster Monster, MonsterHit Hit, int Damage, bool Critical, string? Resistance,
            List<byte[]>? Ignited, string IgniteLog, List<byte[]>? Bled, string BleedLog, StunHit Stun)>();
        var deaths = new List<(uint Id, SpawnedMonster M)>();
        var reflected = new List<MonsterElementalAilments.Effect>();
        Dictionary<uint, int>? player = null;
        int emitted = 0;
        int hitTargets = 0;
        int totalTargets = 0;

        using (ProjectileMutation(conn))
        {
            if (!Current() || Replaced() || conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0 || conn.Paused)
                return;
            double now = ProjectileWorldTime(conn);
            var playerAt = conn.Walk.Position(now);
            var weapon = WeaponFor(conn);
            var skillStats = SupportedSkill(conn, skillId).Stats;
            int Stat(string name) => Stats.Key(name) is uint k ? skillStats.GetValueOrDefault(k) : 0;
            if (Stat("base_deal_no_damage") != 0) return;

            var targets = new List<(uint Id, SpawnedMonster Monster, bool IsStrikeTarget)>();
            var struck = new HashSet<uint>();
            uint? primaryId = targetId != 0
                ? targetId
                : MoltenStrikeGeometry.SelectGroundStrikeTarget(
                    conn.Monsters.Where(e => e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0 && e.Value.OwnerId is null)
                        .Select(e => (e.Key, e.Value.Walk.Position(now), LivingCollision.Size(e.Value))),
                    playerAt, to, weapon.Range);
            if (primaryId is uint pid && conn.Monsters.TryGetValue(pid, out var pm)
                && pm.Hostile && !pm.Dead && pm.Life > 0 && pm.OwnerId is null
                && Dist(playerAt, pm.Walk.Position(now)) <= weapon.Range + Math.Max(0, LivingCollision.Size(pm)) + 2
                && struck.Add(pid))
                targets.Add((pid, pm, true));
            foreach (uint sid in spirit)
                if (struck.Add(sid) && conn.Monsters.TryGetValue(sid, out var sm) && sm.Hostile && !sm.Dead && sm.Life > 0 && sm.OwnerId is null)
                    targets.Add((sid, sm, true));
            if (echo is { } echoAt
                && MoltenStrikeGeometry.SelectGroundStrikeTarget(
                    conn.Monsters.Where(e => e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0 && e.Value.OwnerId is null && !struck.Contains(e.Key))
                        .Select(e => (e.Key, e.Value.Walk.Position(now), LivingCollision.Size(e.Value))),
                    echoAt, echoAt, weapon.Range) is uint eid
                && conn.Monsters.TryGetValue(eid, out var em) && struck.Add(eid))
                targets.Add((eid, em, true));

            var splashMore = new Dictionary<uint, double>();
            var merged = SupportedStats(conn, skillId);
            int SplashStat(string name) => Stats.Key(name) is uint k ? merged.GetValueOrDefault(k) : 0;
            if (targets.Count > 0 && MeleeSplash.Settings(SplashStat) is { } splash)
                foreach (var (_, strikeMonster, _) in targets.ToArray())
                {
                    var splashAt = strikeMonster.Walk.Position(now);
                    foreach (var (sid, sm) in conn.Monsters)
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
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: melee swing whiffed at ({to.X}, {to.Y}), no projectiles emitted (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
                return;
            }

            player = CharacterPlayerStats(conn);
            var local = MainHandLocalSources(conn);
            var pen = Penetration.Of(player, Stats, attack: true, skillStats, local);
            var mods = HitResistMods(player, skillStats);
            var meleeHit = SupportedHit(conn, skillId);
            if (ruthless != 1) meleeHit = meleeHit.Scale(ruthless);
            int criticalMultiplierBase = SupportedCriticalMultiplier(conn, skillId);
            var accuracy = SupportedAccuracy(conn, skillId);
            var leech = HitLeechSources(player, true, skillStats, local);
            var impale = ImpaleStats.From(player, Stats, true, skillStats, local);
            var criticalImpale = ImpaleStats.From(player, Stats, true, skillStats, local, critical: true);
            var ignite = SupportedIgnite(conn, skillId, spell: false, player);
            var bleed = SupportedBleed(conn, skillId, spell: false, player, melee: true);
            var poison = SupportedPoison(conn, skillId, spell: false, player);
            var criticalTrigger = CaptureCriticalTriggerSource(conn, skillId);
            bool fortificationGranted = false;
            bool rageGranted = false;
            var hitOrigins = new List<ProjectileVector>();

            foreach (var (id, m, isStrikeTarget) in targets)
            {
                if (m.Dead || m.Life == 0 || !conn.Monsters.TryGetValue(id, out var current) || !ReferenceEquals(current, m))
                    continue;
                var contactHit = WithMeleeProximityLocked(conn, player, skillStats, m, meleeHit, now);
                int hitChance = AttackHitChanceAt(conn, id, m, now, accuracy, player);
                if (!accuracy.CannotBeEvaded && !m.AttackEvasion.Hits(hitChance, now, conn.AttackAccuracyRandom))
                {
                    Console.WriteLine($"[gateway] {remote} {gem.Name} on 0x{id:x} ({m.Label}) evaded ({hitChance}% chance to hit)");
                    continue;
                }
                if (MonsterBlocks(m, Random.Shared, spell: false, now, skillStats))
                {
                    Console.WriteLine($"[gateway] {remote} {gem.Name} on 0x{id:x} ({m.Label}) blocked");
                    continue;
                }
                var gambler = GamblerFor(player);
                var rolled = WithConditionalSupportDamageLocked(conn, skillStats, attack: true, m,
                    GameEvents.WithOutgoingDamage(conn, player, m, (gambler with { DamageMorePct = 0 })
                        .ForSkill(Stat, spell: false, finalRepeat: repeat == totalRepeats)
                        .RollOutgoingHit(contactHit, Random.Shared)), now);
                if (m.HallowingFlameUntil > now && rolled[DamageType.Physical].Min is > 0 and var flamePhys)
                {
                    double flameFire = flamePhys * m.HallowingFlamePct / 100.0;
                    rolled = rolled.Add(DamageType.Fire, new(flameFire, flameFire));
                    m.HallowingFlameUntil = 0;
                }
                int criticalChance = CriticalChanceOn(conn, id, SupportedCriticalChance(conn, skillId),
                    SupportedCriticalChanceVsBlinded(conn, skillId, release: false),
                    SupportedCriticalChanceVsProfane(conn, skillId, release: false));
                bool critical = RollCriticalStrike(conn, criticalChance, skillStats, attack: true, skillRoll: criticalRoll, player: player)
                    && EvasionEntropy.ConfirmsCritical(hitChance, conn.AttackAccuracyRandom);
                int multiplier = CriticalMultiplierOn(m, criticalMultiplierBase);
                var (damage, resistance, mitigated) = MonsterHitDamage(m, rolled, critical, attack: true, now, mods, pen,
                    gambler.InvertsResistance(Random.Shared));
                var taken = MonsterTakenComponents(m, mitigated, spell: false, now);
                if (critical) damage = DamageModel.CriticalDamage(damage, multiplier);
                double more = repeatMore * splashMore.GetValueOrDefault(id, 1);
                if (more != 1) damage = (int)Math.Floor(damage * Math.Max(0, more));
                LeechOnHitLocked(conn, m, taken, damage, leech);
                damage = WithImpaleHitLocked(conn, m, damage, rolled, critical, criticalMultiplierBase,
                    critical ? criticalImpale : impale, more).Damage;
                var stun = OwnerStun(conn, m, damage, mitigated, melee: true, spell: false, critical, player, skillStats);
                var mAt = m.Walk.Position(now);
                var rampageSource = RampageOnMeleeHitLocked(conn, m, melee: true, player);
                var applied = ApplyMonsterHitLocked(conn, m, damage, rampageSource, player,
                    cull: Cull.Of(player, skillStats, critical, spell: false));
                if (applied is not { } hit) continue;

                if (isStrikeTarget) hitOrigins.Add(ProjectileSession.Vector(mAt));
                ReleaseCriticalTrigger(conn, ProjectilesFor(remote, stream, ciphers, conn), criticalTrigger, critical, m.Hostile, mAt, now);
                if (critical) conn.RecentEvents.Record(StatConditions.Crit, now);
                if (Stats.Key("base_killed_monster_dropped_item_quantity_+%") is uint quantityKey)
                    m.KilledWithItemQuantityPct = skillStats.GetValueOrDefault(quantityKey);
                if (Stats.Key("base_killed_monster_dropped_item_rarity_+%") is uint rarityKey)
                    m.KilledWithItemRarityPct = skillStats.GetValueOrDefault(rarityKey);
                SoulEaterHit(conn, m);
                if (hit.Life == 0) DevourOnKill(conn, m, skillStats);
                AilmentsOnHitLocked(conn, m, mitigated, spell: false, critical, multiplier, player, skillStats, now,
                    Random.Shared, reflected, poison, sourceSkill: skillId);
                List<byte[]>? ignited = null, bled = null;
                string igniteLog = "", bleedLog = "";
                if (m.Life > 0 && ignite.CanIgnite(rolled) && IgniteOnHitLocked(conn, id, m, ignite, critical, now) is { } ignitePackets)
                    (ignited, igniteLog) = (ignitePackets, IgnitedLog(id, m, critical, now));
                if (m.Life > 0 && rolled[DamageType.Physical].Max > 0 && BleedOnHitLocked(conn, id, m, bleed, critical, now) is { } cut)
                    (bled, bleedLog) = (cut, BleedingLog(id, m, critical, now));
                if (hit.StageStarts)
                {
                    CommitBossStage(remote, stream, ciphers, conn, id, m, now);
                    hit = hit with { StageStarts = false };
                }
                if (!fortificationGranted)
                    fortificationGranted = FortificationOnHitLocked(conn, m,
                        RawFortificationDamage(rolled, critical, multiplier), melee: true, player, skillStats);
                if (!rageGranted)
                    rageGranted = RageOnHitLocked(conn, attack: true, melee: true, critical, skillStats, player: player);
                InflictMonsterBlind(conn, id, m, now, BlindInputs(player, skillStats));
                CombatRushOnHit(conn, skillStats, now);
                HallowOnMeleeHit(conn, m, skillStats, now);
                ExcommunicateOnMeleeHit(conn, m, skillStats, now);
                results.Add((id, m, hit, damage, critical, resistance, ignited, igniteLog, bled, bleedLog, stun));
            }

            hitTargets = hitOrigins.Count;
            if (hitOrigins.Count == 0 && vaalBehaviour)
                hitOrigins.Add(MoltenStrikeGeometry.SwingOrigin(playerAt, to, weapon.Range));
            if (hitOrigins.Count > 0)
            {
                var session = ProjectilesFor(remote, stream, ciphers, conn);
                emitted = EmitMoltenStrikeBallsLocked(conn, session, skillId, gem, playerAt, to, criticalRoll,
                    sequence, (uint)(totalRepeats - repeat), delay, repeatMore, hitOrigins, Random.Shared, fifthAttack);
            }
        }

        foreach (var r in results)
        {
            if (r.Critical) _ = GainFlaskChargesOnCrit(remote, stream, ciphers, conn);
            if (r.Critical) await MaybeCreateProfaneGround(remote, stream, ciphers, conn, r.Monster.Walk.Position(ProjectileWorldTime(conn)));
            await NotifyMonsterHit(remote, stream, ciphers, conn, r.Id, r.Monster, r.Hit, r.Damage,
                $"{gem.Name}{(r.Critical ? " (critical)" : "")}{(r.Resistance is null ? "" : $", {r.Resistance}")}",
                melee: true, sequence: sequence, deaths: deaths, stun: r.Stun);
            await SendDotBuff(remote, stream, ciphers, r.Ignited, r.IgniteLog);
            await SendDotBuff(remote, stream, ciphers, r.Bled, r.BleedLog);
            ChargesOnHit(conn, r.Monster, r.Critical, spell: false, gem.Effect, gem.Level, gem.Quality, primary: r.Id == results[0].Id);
        }
        if (reflected.Count > 0 && player is not null)
            using (ProjectileMutation(conn)) ReflectAilmentsLocked(conn, reflected, ProjectileWorldTime(conn), player, Random.Shared);
        foreach (var (deadId, dead) in deaths) await MonsterKilled(remote, stream, ciphers, conn, deadId, dead);
        string contactNote = contactClip is null ? "the attack1 fallback" : contactClip;
        Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: melee hit {hitTargets}/{totalTargets} monster(s), "
            + $"launched {emitted} molten projectile(s), contact {contactFraction:0.###} of the attack ({contactNote}) (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
    }

    /// <summary>Distributes and releases Molten Strike's balls across struck targets. Call under the Monsters lock.</summary>
    public static int EmitMoltenStrikeBallsLocked(ClientState conn, ProjectileSession session, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        (int X, int Y) playerAt, (int X, int Y) aim, SkillCriticalRoll criticalRoll, ushort sequence,
        uint repeatsLeft, double releaseDelay, double repeatMore, IReadOnlyList<ProjectileVector> hitOrigins,
        Random random, bool fifthAttack = false)
    {
        if (hitOrigins.Count == 0) return 0;
        ProjectileUse baseUse;
        try
        {
            baseUse = CaptureGemProjectile(conn, session, skillId, gem, aim, criticalRoll, sequence, repeatsLeft, releaseDelay);
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"[gateway] {session.remote} {gem.Name} projectile release skipped: {ex.Message}");
            return 0;
        }
        var own = SupportedSkill(conn, skillId).Stats;
        var merged = MergeSkillStats(CharacterPlayerStats(conn), own);
        int Merged(string name) => Stats.Key(name) is uint k ? merged.GetValueOrDefault(k) : 0;

        int totalCount = baseUse.Profile.Count;
        var shot = (ProjectileOffense)baseUse.Payload!;
        double damageScale = repeatMore;
        if (fifthAttack)
        {
            int fifthExtra = Math.Max(0, Merged("molten_strike_every_5th_attack_fire_X_additional_projectiles"));
            int fifthMorePct = Merged("molten_strike_every_5th_attack_projectiles_damage_+%_final");
            totalCount += fifthExtra;
            if (fifthMorePct != 0) damageScale *= Math.Max(0, 1 + fifthMorePct / 100.0);
        }
        if (damageScale != 1)
            shot = shot with { Hit = shot.Hit.Scale(damageScale), AreaHit = shot.AreaHit?.Scale(damageScale) };

        var areaStats = MergeSkillStats(AreaPlayerStats(conn), own);
        int AreaGet(string name) => name == SkillArea.BaseIncreased
            ? (Stats.Key(name) is uint k ? own.GetValueOrDefault(k) : 0) + (Stats.Key("molten_strike_radius_+%") is uint mk ? areaStats.GetValueOrDefault(mk) : 0)
            : Stats.Key(name) is uint ak ? areaStats.GetValueOrDefault(ak) : 0;
        int radiusPercent = SkillArea.RadiusPercent(AreaGet, spell: false);
        var (minDist, maxDist) = MoltenStrikeGeometry.DistanceBounds(radiusPercent, Merged("projectile_speed_+%"), Merged("chaining_range_+%"));
        double clipSeconds = (SkillsFor(conn).Clips(baseUse.Profile.Id)?.Flight?.Ms ?? 633) / 1000.0;
        // Client 0x141b6b3cf..0x141b6b411 adds 10 * base projectile count (before Zenith's 5th-attack +5) to molten_strike_projectile_animation_speed_+%.
        int animSpeedPct = MoltenStrikeGeometry.EffectiveAnimationSpeedPct(
            Merged("molten_strike_projectile_animation_speed_+%"), baseUse.Profile.Count);
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
