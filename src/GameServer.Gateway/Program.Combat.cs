using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using WalkEstimator = GameServer.Core.World.WalkEstimator;
using Progression = GameServer.Core.World.Progression;
using DamageModel = GameServer.Core.World.DamageModel;
using TargetAccuracy = GameServer.Core.World.TargetAccuracy;
using DamageType = GameServer.Core.World.DamageType;
using DamagePacket = GameServer.Core.World.DamagePacket;
using DamageRange = GameServer.Core.World.DamageRange;
using HitResistances = GameServer.Core.World.HitResistances;

partial class Program
{
    // Sample both moving actors at the hit time, after the approach and contact delay.
    internal static (int Accuracy, bool Unique, bool CloseRange) AttackAccuracyAt(
        ClientState conn, SpawnedMonster target, double at)
    {
        bool unique = target.Rarity == MonsterRarity.Unique;
        bool close = TargetAccuracy.InCloseRange(Dist(conn.Walk.Position(at), target.Walk.Position(at)));
        return (TargetAccuracy.AgainstTarget(conn.Pools.Accuracy, conn.Pools.AccuracyMoreVsUniquePct,
            conn.Pools.AccuracyMoreInCloseRangePct, unique, close), unique, close);
    }

    // A ground bow shot reserves pacing, its action token and stationary walk together with the input-time gate.
    internal static bool TryBeginBowAttack(ClientState conn, double requestedAt, double duration,
        ushort skill, ushort sequence, out (int Action, int X, int Y) started)
    {
        started = default;
        using (ProjectileMutation(conn))
        {
            double now = ProjectileWorldTime(conn);
            if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.Paused || conn.PlayerLife == 0
                || conn.HitRecovery.BlocksInput(requestedAt, now)
                || !AttackTiming.Ready(now, conn.NextAttackAt, duration)) return false;
            requestedAt = Math.Max(requestedAt, conn.ProjectileRuntime?.Runtime.Now / 120000d ?? requestedAt);
            conn.NextAttackAt = AttackTiming.After(now, conn.NextAttackAt, duration);
            int action = Interlocked.Increment(ref conn.ActionCount);
            var (x, y) = conn.Walk.Position(requestedAt);
            conn.Walk.Order(x, y, requestedAt);
            conn.ChannelSkill = skill;
            conn.ChannelSkillSequence = sequence;
            started = (action, x, y);
            return true;
        }
    }

    // Whether an attack may start now: the shared attack timer (the default attack and the attack gem skills) is over, or
    // the use is no more than a quarter of an attack early (the client's jitter). Does not reserve the timer: an attack used
    // on a monster waits for the walk into reach, and CastGemSkill reserves it when the swing starts.
    static double SkillAttackSeconds(ClientState conn, int effect, int level) =>
        SkillAttackTiming.ForSkill(Skills, effect, level, WeaponFor(conn), CharacterPlayerStats(conn));

    static bool AttackReady(ClientState conn, int effect, int level, out double seconds)
    {
        seconds = SkillAttackSeconds(conn, effect, level);
        double now = conn.Clock.Elapsed.TotalSeconds;
        lock (conn.Monsters) return AttackTiming.Ready(now, conn.NextAttackAt, seconds);
    }

    // The 0x1af hit angle (0..255 = a full turn), the direction the hit comes from (attacker → target) in the client's
    // facing convention atan2(dx, −dy), like Positioned's rotation.
    static byte HitAngle(int dx, int dy)
    {
        double a = Math.Atan2(dx, -dy);
        if (a < 0) a += 2 * Math.PI;
        return (byte)((int)Math.Round(a * 256.0 / (2 * Math.PI)) & 0xff);
    }

    // A positive offset on the hit's contact timing (seconds): the client starts its swing a beat after the server's 0x1ab
    // echo (the walk estimate leads the client's own walk, so the contact keyframe alone leaves the blood early). A fixed
    // offset is a compromise over the variable walk-sync; Game_HIT_OFFSET_MS overrides it (150 ms here covers most of the
    // walk-into-range error without overshooting a swing). A value that is not a finite number keeps the default.
    static double HitContactOffsetSeconds() =>
        (double.TryParse(Environment.GetEnvironmentVariable("Game_HIT_OFFSET_MS"), System.Globalization.CultureInfo.InvariantCulture, out var hitOff)
            && double.IsFinite(hitOff) ? hitOff : 150) / 1000.0;

    // The attack's contact time in seconds (AttackTiming.ContactDelay): the attack time scaled to the active weapon's attack1
    // contact keyframe, plus the walk-in offset for an attack that approached its target first, and always before the next
    // attack may start (which would cancel the hit).
    static double AttackContactDelay(ClientState conn, double attackSeconds, bool approach)
    {
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? Skills.ClassFor(conn.ClassRow).AnimatedObject;
        double contact = Animations.AttackContact(ao, WeaponProfileFor(conn, activeSet: true));
        return AttackTiming.ContactDelay(attackSeconds, contact, approach ? HitContactOffsetSeconds() : 0);
    }

    // The player uses a skill on a monster (C→S 0x24, e.g. the default attack on a left click). The client walks into range
    // first, so the server walks its estimate too (WalkEstimator.Approach, reach = the weapon's MaxAttackDistance in cells,
    // centre to centre) and only then starts the swing: it echoes the action aimed at the monster (0x1ab, flags | 0x80 +
    // target id) and, at the contact point (AttackContactDelay: the attack animation's contact keyframe), lowers the monster's life (0x1b1) by the unarmed damage
    // (Characters.dat MinDamage..MaxDamage raised by the increased melee physical damage stats). Any later action (a move, another click) cancels both.
    // One attack per attack time (WeaponSpeed ms scaled by the player's attack speed stats, a quarter early allowed for
    // jitter); earlier repeats of 0x24 are ignored. At 0 the client's Life component runs its death; the corpse stays.
    internal static async Task Attack(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        InstanceTransfer.UseSkillOnObject use, SpawnedMonster m, double requestedAt)
    {
        int projectileEpoch;
        lock (conn.Monsters) projectileEpoch = conn.ProjectileEpoch;
        var weapon = WeaponFor(conn);
        bool bow = WeaponProfileFor(conn, activeSet: true).Bow;
        double now = conn.Clock.Elapsed.TotalSeconds, duration;
        lock (conn.Monsters) duration = weapon.AttackSeconds(CharacterPlayerStats(conn));
        // The client sends one 0x24 per swing while the button is held; jitter makes the next one arrive a little before a
        // full attack time, so accept it up to a quarter of an attack early.
        lock (conn.Monsters)
            if ((bow && conn.Paused) || !AttackTiming.Ready(now, conn.NextAttackAt, duration))
            {
                Console.WriteLine($"[gateway] {remote} <= 0x0024 skill 0x{use.SkillId:x4} seq 0x{use.Sequence:x4} ignored: {conn.NextAttackAt - now:0.000} s before the next attack");
                return;
            }
        int action;
        (int X, int Y) target, here, stop;
        double arrive;
        using (ProjectileMutation(conn))
        {
            now = ProjectileWorldTime(conn);
            if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0
                || conn.HitRecovery.BlocksInput(requestedAt, now)) return;
            action = Interlocked.Increment(ref conn.ActionCount);
            target = m.Walk.Position(now);
            (here, stop, arrive) = conn.Walk.Approach(target.X, target.Y, weapon.Range, now, tolerance: m.StopRange - 2);
        }
        Console.WriteLine($"[gateway] {remote} <= 0x0024 skill 0x{use.SkillId:x4} seq 0x{use.Sequence:x4} on 0x{use.TargetId:x}: player ({here.X}, {here.Y}), " +
            $"monster ({target.X}, {target.Y}), distance {Dist(here, target):0.0}; swing from ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now)
        {
            if (bow) { if (!await WaitProjectileTime(conn, arrive, projectileEpoch)) return; }
            else await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        }
        if (conn.ActionCount != action || (bow && !OrdinaryBowReleaseCurrent(conn, action, projectileEpoch))) { Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} cancelled before the swing"); return; }
        now = conn.Clock.Elapsed.TotalSeconds;
        lock (conn.Monsters)
        {
            target = m.Walk.Position(now);
            conn.NextAttackAt = AttackTiming.After(now, conn.NextAttackAt, duration);
        }
        if (Dist(stop, target) > weapon.Range + 2)
        {
            Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} out of reach: {Dist(stop, target):0.0} > {weapon.Range}");
            return;
        }
        var actionBytes = InstanceProtocol.ActionStarted(PlayerId, stop.X, stop.Y, target.X, target.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId);
        string actionLog = $"0x01ab player skill 0x{use.SkillId:x4} on monster 0x{use.TargetId:x} ({m.Label}, life {m.Life}/{m.MaxLife}), attack time {duration:0.000} s";
        if (bow)
        {
            await LaunchOrdinaryBow(remote, stream, ciphers, conn, stop, target, duration, action, projectileEpoch);
            if (!await SendOrdinaryBowAction(remote, stream, ciphers, conn, actionBytes, actionLog, action, projectileEpoch)) return;
        }
        else await Send(remote, stream, ciphers, actionBytes, actionLog, still: () => PlayerActionCurrent(conn, action));
        if (m.Life == 0) return;
        // A bow's target is an aim, not a reserved hit: its released arrow owns collision and damage.
        if (bow)
        {
            return;
        }
        // The hit lands at the attack animation's contact keyframe (data/animation-contacts.json), not a fixed half: the
        // client stretches the clip to the attack time, so the blood and the weapon impact sound line up with the swing.
        await Task.Delay(TimeSpan.FromSeconds(AttackContactDelay(conn, duration, approach: true)));
        await WhilePaused(conn);
        uint life;
        bool stageStarts = false;
        int accuracy, evasion;
        lock (conn.Monsters)
        {
            if (m.Life == 0) return;
            if (!PlayerActionCurrent(conn, action))
            {
                Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} cancelled by a later action");
                return;
            }
            // The Accuracy Mastery's target conditions at the roll: the target's own rarity and its distance at the hit
            // resolution time (TargetAccuracy). Spells do not roll, so spells never see these.
            var targetAccuracy = AttackAccuracyAt(conn, m, conn.Clock.Elapsed.TotalSeconds);
            bool unique = targetAccuracy.Unique, closeRange = targetAccuracy.CloseRange;
            accuracy = targetAccuracy.Accuracy;
            if (accuracy != conn.Pools.Accuracy)
                Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} ({m.Label}, {m.Rarity}): accuracy {conn.Pools.Accuracy} -> {accuracy} " +
                    $"for the target ({(unique ? "unique target, " : "")}{(closeRange ? "in close range (20 units) " : "")}accuracy mastery more)");
            evasion = m.Mods.Effects.Evasion(Monsters.BaseEvasion(m.Level) ?? 0);
        }
        int hitChance = DamageModel.AccuracyHitChance(conn.FlaskGrounds.Evasion(use.TargetId, evasion), accuracy);   // blinded in smoke
        if (!PlayerAlwaysHits(conn) && !DamageModel.Hits(hitChance, Random.Shared.Next(100)))
        {
            Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} missed: accuracy {accuracy} vs evasion {evasion}, " +
                $"{hitChance}% chance");
            return;
        }
        if (MonsterBlocks(m, Random.Shared, spell: false))   // Stalwart: nothing of the hit happens
        {
            Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} ({m.Label}) blocked");
            return;
        }
        int attackEffect = SkillEffectId(conn, SkillCatalog.FirstSkillId) is { } attackId ? Skills.EffectRow(attackId) : -1;
        var ownPenetration = attackEffect >= 0
            ? Skills.EffectStats(attackEffect, 1).ToDictionary(kv => kv.Item1, kv => kv.Item2) : null;
        // Impale: the stacks already on the target add their stored damage to this hit, this hit uses one of their five
        // hits, and the hit's own physical damage may store a new stack for the hits after it.
        double stored, impaleStored;
        int impaleStacks, damage;
        bool impaled, critical;
        string? resistance;
        using (ProjectileMutation(conn))
        {
            if (m.Life == 0) return;
            if (!PlayerActionCurrent(conn, action) || conn.Paused)
            {
                Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} cancelled by a later action");
                return;
            }
            // Settle Rage at accepted contact before this hit's gain. It is a final multiplier on the attack packet,
            // never on an existing impale bonus or on generic damage/DoT.
            var player = CharacterPlayerStats(conn);
            var pen = Penetration.Of(player, Stats, attack: true, ownPenetration);
            var resistanceMods = HitResistMods(player, ownPenetration);
            var hit = AttackHitConverted(weapon, player, WeaponProfileFor(conn, activeSet: true).Melee,
                ConversionFor(conn, attackEffect, 1, attack: true));
            double physical = hit[DamageType.Physical].Max;
            stored = m.Impale.Bonus;
            m.Impale.AfterHit();
            int impaleChance = Stats.Key("impale_on_hit_%_chance") is uint impaleKey ? player.GetValueOrDefault(impaleKey) : 0;
            impaled = impaleChance > 0 && Random.Shared.Next(100) < impaleChance && m.Impale.Apply(physical);
            // Read before the hit is applied: a killing blow clears the target's impales.
            impaleStacks = m.Impale.Stacks;
            impaleStored = m.Impale.Bonus;
            var ownRolled = hit.Roll(Random.Shared);
            // Impale is stored reflected damage, not part of the direct roll which can generate Fortification.
            var rolled = stored > 0 ? ownRolled.Add(DamageType.Physical,
                new DamageRange(Math.Floor(stored), Math.Floor(stored))) : ownRolled;
            critical = RollCriticalStrike(conn, conn.Pools.AttackCriticalStrikeChance, ownPenetration);
            int criticalMultiplier = CriticalMultiplierOn(m, AttackCriticalMultiplier(conn, player));
            (damage, resistance) = MonsterDamage(m, rolled, critical, attack: true, resistanceMods, pen);
            if (critical) damage = DamageModel.CriticalDamage(damage, criticalMultiplier);
            FortificationOnHitLocked(conn, m, RawFortificationDamage(ownRolled, critical, criticalMultiplier),
                WeaponProfileFor(conn, activeSet: true).Melee, player, ownPenetration);
            (m.Life, stageStarts) = TakeDamageLocked(m, damage);
            if (stageStarts)
            {
                CommitBossStage(remote, stream, ciphers, conn, use.TargetId, m, ProjectileWorldTime(conn));
                stageStarts = false;
            }
            life = m.Life;
            conn.LastEnemyHitAt = conn.Clock.Elapsed.TotalSeconds;
            if (life == 0)
            {
                m.Aggro = false; m.Dead = true; m.Impale.Clear();
                CollectVaalSoulAtDeathLocked(conn, m);
            }
            RageOnHitLocked(conn, attack: true, melee: WeaponProfileFor(conn, activeSet: true).Melee, critical);
        }
        ChargesOnHit(conn, m, critical, spell: false);
        if (critical) _ = GainFlaskChargesOnCrit(remote, stream, ciphers, conn);   // the hit landed
        // At 0 the client's Life tick (0x141e6a190) stops notifying its listeners, so the mini life bar would keep the last
        // value: hide it with the stat its listener watches (hide_mini_life_bar).
        if (life == 0)
            await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(use.TargetId, [(StatKeys.HideMiniLifeBar, 1)]),
                $"0x01b3 monster 0x{use.TargetId:x} hide_mini_life_bar = 1");
        // The hit reaction: one of the rig's TakeHit clips and its SoundEvent (0x1b2).
        await SendFlinch(remote, stream, ciphers, conn, use.TargetId, m, damage, log: true);
        // The hit result for the client's damage/effect pipeline (0x1af): the client applies the hit locally (the target's
        // blood, the weapon impact sound "bonk") and 0x1b1 stays authoritative on life. flags 0x4201 is the real server's
        // player-melee hit (bit 0 hit, 0x200 damage, 0x4000 attack), kind 0x03 = action hit with hit data, the angle drives
        // the directional blood.
        await Send(remote, stream, ciphers,
            InstanceProtocol.ActionHit(PlayerId, use.Sequence, use.TargetId, flags: 0x4201, kind: 0x03,
                angle: HitAngle(target.X - stop.X, target.Y - stop.Y)),
            $"0x01af player hit on monster 0x{use.TargetId:x} (sequence 0x{use.Sequence:x4})");
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(use.TargetId, life),
            $"0x01b1 monster 0x{use.TargetId:x} hit for {damage}{(critical ? " (critical)" : "")}" +
            (resistance is { } note ? $", {note}" : "") +
            (stored > 0 ? $", impale +{stored:0.#} ({impaleStacks} stack(s))" : "") +
            (impaled ? $", impaled ({impaleStacks} stack(s), {impaleStored:0.#} stored)" : "") +
            $", life {life}/{m.MaxLife}{(life == 0 ? " (dies)" : "")}");
        if (life == 0) await MonsterKilled(remote, stream, ciphers, conn, use.TargetId, m);
        if (stageStarts) _ = RunBossStage(remote, stream, ciphers, conn, use.TargetId, m);
    }

    // A gem skill used on a monster (C→S 0x24 with a skill id other than the default attack's). A spell stops and casts from
    // where the player stands: the server echoes the action from there, aimed at the monster, so the client plays the cast.
    // An attack gem (Holy Strike, Ground Slam …) is used like the default attack (see Attack): the client walks into reach
    // first, so the server walks its estimate too and only echoes the swing — and pays the skill's cost — on arrival. Before
    // 2026-10-25 the attack cast at the click from out of reach: the mana was gone at once and the hit landed around the
    // player, hitting nothing. CastGemSkill pays the cost and runs the skill's effect (projectile, area hit, aura, self-cast).
    static async Task CastOn(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        InstanceTransfer.UseSkillOnObject use, SpawnedMonster m, double requestedAt)
    {
        var gem = GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == use.SkillId);
        if (gem.Name is not null && !ProjectilePathAvailable(remote, conn, use.SkillId, gem.Effect)) return;
        // A movement attack (Flicker Strike, Leap Slam) closes the distance itself, and an attack used with "always attack
        // without moving" (Flags bit 0) swings from where the player stands: both keep the immediate cast.
        if (gem.Name is not null && Skills.IsAttack(gem.Effect) && !Skills.IsMovement(gem.Effect) && (use.Flags & 0x1) == 0)
        {
            if (!AttackReady(conn, gem.Effect, gem.Level, out double attackSeconds))
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x}: ignored, the previous attack is still running (attack time {attackSeconds:0.00} s) (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
                return;
            }
            var weapon = WeaponFor(conn);
            double now = conn.Clock.Elapsed.TotalSeconds;
            int action = 0;
            (int X, int Y) target = default, here = default, stop = default;
            double arrive = now;
            if (!TryBeginSkillCooldownUse(conn, use.SkillId, gem.Effect,
                CooldownFor(conn, gem.Effect, gem.Level, gem.Quality), false,
                () =>
                {
                    now = ProjectileWorldTime(conn);
                    action = Interlocked.Increment(ref conn.ActionCount);
                    target = m.Walk.Position(now);
                    (here, stop, arrive) = conn.Walk.Approach(target.X, target.Y, weapon.Range, now, tolerance: m.StopRange - 2);
                }, out var approachTicket, reserve: false, requestedAt: requestedAt)) return;
            Console.WriteLine($"[gateway] {remote} <= 0x0024 skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x}: player ({here.X}, {here.Y}), " +
                $"monster ({target.X}, {target.Y}), distance {Dist(here, target):0.0}; swing from ({stop.X}, {stop.Y}) in {arrive - now:0.00} s (t={now:0.000})");
            if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
            if (conn.ActionCount != action || approachTicket?.Current(conn) == false)
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x} cancelled before the swing");
                return;
            }
            now = conn.Clock.Elapsed.TotalSeconds;
            lock (conn.Monsters) target = m.Walk.Position(now);
            if (Dist(stop, target) > weapon.Range + 2)
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x} out of reach: {Dist(stop, target):0.0} > {weapon.Range}");
                return;
            }
            if (!TryBeginSkillCooldownUse(conn, use.SkillId, gem.Effect,
                CooldownFor(conn, gem.Effect, gem.Level, gem.Quality), false,
                () => { }, out var cooldownUse, actionToken: action)) return;
            bool acknowledged = false;
            bool projectileRuntime = UsesProjectileRuntime(gem.Effect);
            if (projectileRuntime) _ = CastGemSkill(remote, stream, ciphers, conn, use.SkillId, stop, target,
                use.TargetId, sequence: use.Sequence, actionToken: action, cooldownUse: cooldownUse);
            await Send(remote, stream, ciphers,
                InstanceProtocol.ActionStarted(PlayerId, stop.X, stop.Y, target.X, target.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
                $"0x01ab player skill 0x{use.SkillId:x4} {gem.Name} on monster 0x{use.TargetId:x} ({m.Label}) from ({stop.X}, {stop.Y}) to ({target.X}, {target.Y}), flags 0x{use.Flags:x4} (t={conn.Clock.Elapsed.TotalSeconds:0.000})",
                still: () => acknowledged = OwnAuraAcknowledgementAllowed(conn, gem.Effect, use.SkillId,
                    () => cooldownUse?.CommitNativeUse(conn, use.SkillId) ?? PlayerActionCurrent(conn, action)));
            if (!projectileRuntime && !acknowledged) return;
            if (!projectileRuntime) _ = CastGemSkill(remote, stream, ciphers, conn, use.SkillId, stop, target, use.TargetId, sequence: use.Sequence,
                actionToken: action, cooldownUse: cooldownUse);
            return;
        }
        double castAt = conn.Clock.Elapsed.TotalSeconds;
        int castAction = 0;
        (int X, int Y) aim = default, stand = default;
        if (!TryBeginSkillCooldownUse(conn, use.SkillId, gem.Effect,
            gem.Name is null ? null : CooldownFor(conn, gem.Effect, gem.Level, gem.Quality), false,
            () =>
            {
                castAt = ProjectileWorldTime(conn);
                castAction = Interlocked.Increment(ref conn.ActionCount);
                aim = m.Walk.Position(castAt);
                stand = conn.Walk.Position(castAt);
                conn.Walk.Order(stand.X, stand.Y, castAt);
            }, out var spellUse, requestedAt: requestedAt)) return;
        bool spellAcknowledged = false;
        bool spellProjectileRuntime = UsesProjectileRuntime(gem.Effect);
        if (spellProjectileRuntime) _ = CastGemSkill(remote, stream, ciphers, conn, use.SkillId, stand, aim,
            use.TargetId, sequence: use.Sequence, actionToken: spellUse?.ActionToken ?? castAction, cooldownUse: spellUse);
        await Send(remote, stream, ciphers,
            InstanceProtocol.ActionStarted(PlayerId, stand.X, stand.Y, aim.X, aim.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
            $"0x01ab player skill 0x{use.SkillId:x4} cast on monster 0x{use.TargetId:x} ({m.Label}) from ({stand.X}, {stand.Y}) to ({aim.X}, {aim.Y}), flags 0x{use.Flags:x4}",
            still: () => spellAcknowledged = OwnAuraAcknowledgementAllowed(conn, gem.Effect, use.SkillId,
                () => spellUse?.CommitNativeUse(conn, use.SkillId) ?? PlayerActionCurrent(conn, castAction)));
        if (!spellProjectileRuntime && !spellAcknowledged) return;
        if (!spellProjectileRuntime) _ = CastGemSkill(remote, stream, ciphers, conn, use.SkillId, stand, aim, use.TargetId, sequence: use.Sequence,
            actionToken: spellUse?.ActionToken ?? castAction, cooldownUse: spellUse);
    }

    // A gem skill's cast: Righteous Fire puts its buff on, any other skill launches its projectile (if it has one). False when
    // it was refused (cooldown, mana), so its use is not confirmed to the client.
    static async Task<bool> CastGemSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (int X, int Y) from, (int X, int Y) to, uint targetId = 0, ushort sequence = 0,
        int? actionToken = null, CooldownUseTicket? cooldownUse = null, bool locationInstant = false)
    {
        int action = actionToken ?? conn.ActionCount;
        int recoveryGeneration = 0;
        lock (conn.Monsters)
            if (actionToken is not null && (conn.ActionCount != action || conn.HitRecovery.Active(conn.Clock.Elapsed.TotalSeconds))) return false;
        if (InTown(conn) || conn.PlayerLife == 0) return false;
        await EndGracePeriod(remote, stream, ciphers, conn, action: true);
        var gem = GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == skillId);
        bool Current() => cooldownUse?.Current(conn) ?? (actionToken is null || PlayerActionCurrent(conn, action));
        if (!Current()) return false;
        if (gem.Name is not null && !ProjectilePathAvailable(remote, conn, skillId, gem.Effect)) return false;
        if (locationInstant && (gem.Name is null || !InstantLocationSkill.Supported(Skills, gem.Effect))) return false;
        if (gem.Name is not null && Skills.IsSpell(gem.Effect) && !Skills.IsAttack(gem.Effect)
            && CastMs(conn, gem.Effect) is null)
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: invalid cast duration (no effect or mana cost)");
            await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana} (invalid cast duration, cost back)");
            return false;
        }
        // Shared backstop for actor restrictions and gem requirements, before cooldown/payment/effect admission.
        if (gem.Name is not null && GemSkillInstanceDisabled(conn, gem.Effect, skillId) is { } disabled)
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: {disabled} (disabled)");
            await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana} (disabled skill, cost back)");
            return false;
        }
        // Soul collection/display does not enable a Vaal executor or a payment path.
        if (gem.Name is not null && Skills.VaalSouls(gem.Effect, gem.Level) is int souls and > 0)
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: Vaal execution unsupported (base cost {souls} souls)");
            return false;
        }
        // Eligible uses arrive with their one original preflight reservation. Never reserve again after an await.
        if (cooldownUse is not null)
        {
            if (!cooldownUse.TryExecute(conn, gem.Effect)) return false;
            if (!cooldownUse.Instant)
            {
                if (UsesProjectileRuntime(gem.Effect)) ProjectilesFor(remote, stream, ciphers, conn)
                    .Enqueue(() => SendNativeCooldownRefresh(remote, stream, ciphers, conn));
                else await SendNativeCooldownRefresh(remote, stream, ciphers, conn);
            }
            if (!Current()) return false;
        }
        else if (gem.Name is not null && CooldownFor(conn, gem.Effect, gem.Level, gem.Quality) is not null)
            return false;
        // Multi-use and unsupported cooldown mechanics retain their existing raw-deadline behavior.
        else if (gem.Name is not null && Skills.CooldownMs(gem.Effect, gem.Level) is int cooldown and > 0)
        {
            double now = conn.Clock.Elapsed.TotalSeconds, readyAt;
            lock (conn.Monsters)
            {
                readyAt = conn.SkillReadyAt.GetValueOrDefault(gem.Effect);
                if (now >= readyAt)
                {
                    conn.SkillReadyAt[gem.Effect] = now + cooldown / 1000.0;
                    conn.SingleUseCooldowns.Remove(gem.Effect);
                }
            }
            if (now < readyAt)
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: on cooldown for {(readyAt - now) * 1000:0} ms more");
                return false;
            }
        }
        // An attack gem swings with the weapon's attack time, like the default attack (see Attack): while the button is held
        // the client keeps aiming it, and every use used to pay its cost and start a swing (live 2026-10-25: Holy Strike on
        // the right mouse button, twelve swings a second at 6 mana each). The timer is shared with the default attack, so
        // two attacks cannot start at once either.
        if (gem.Name is not null && Skills.IsAttack(gem.Effect))
        {
            double duration = SupportedAttackSeconds(conn, skillId);
            double now = conn.Clock.Elapsed.TotalSeconds, next;
            bool ready;
            lock (conn.Monsters)
            {
                next = conn.NextAttackAt;
                ready = AttackTiming.Ready(now, next, duration);
                if (ready) conn.NextAttackAt = AttackTiming.After(now, next, duration);
            }
            if (!ready)
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: ignored, {next - now:0.000} s before the next attack (t={now:0.000})");
                return false;
            }
        }
        if (gem.Name is not null && Supports.Of(gem.Effect)?.Types.Contains(47) == true)
        {
            conn.NextChannelHitAt = conn.Clock.Elapsed.TotalSeconds + SupportedAttackSeconds(conn, skillId);
            conn.LastChannelUpdateAt = conn.Clock.Elapsed.TotalSeconds;
            _ = RunChannel(remote, stream, ciphers, conn, skillId, gem, action);
        }
        if (gem.Name is not null && SupportedReservation(conn, skillId) is { } reservation)
        {
            if (!Current()) return false;
            return await ToggleAura(remote, stream, ciphers, conn, skillId, gem.Effect, gem.Name, reservation,
                SupportedSkill(conn, skillId).Level, SupportedSkill(conn, skillId).Quality);
        }
        if (gem.Name is not null)
        {
            uint cost;
            bool paid;
            lock (conn.Monsters)
            {
                if (!Current()) return false;
                paid = PayMana(conn, skillId, out cost);
                // Capture at paid admission, before output or release scheduling can catch up other hits.
                recoveryGeneration = conn.HitRecovery.Generation;
            }
            if (!paid)
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: not enough mana ({conn.PlayerMana} < {cost})");
                return false;
            }
            if (cost > 0)
            {
                if (UsesProjectileRuntime(gem.Effect)) ProjectilesFor(remote, stream, ciphers, conn)
                    .Enqueue(() => SendPlayerMana(remote, stream, ciphers, conn, $"projectile mana paid: {gem.Name}"));
                else await SendPlayerMana(remote, stream, ciphers, conn,
                    $"0x01b1 player mana {conn.PlayerMana} (-{cost} for {gem.Name}) (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
            }
        }
        if (!Current()) return false;
        // Location instant acceptance handles only Frostblink's repositioning, without inventing its departure/arrival
        // hits or chilled ground. In particular do not route it through the generic single-area hit approximation.
        if (locationInstant) return true;
        // The Portal skill: a town portal, as the Portal Scroll's, once its cast (2.5 s) is over.
        if (actionToken is not null && !PlayerActionCurrent(conn, action)) return false;
        if (gem.Name is not null && Skills.EffectId(gem.Effect) == "Portal")
        {
            if (!PortalAllowed(conn)) { Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} Portal: not here"); return false; }
            if (CastMs(conn, gem.Effect) is not int castMs) return false;
            _ = Task.Delay(castMs).ContinueWith(async _ =>
            {
                if (!conn.Closed && PlayerActionCurrent(conn, action) && Current())
                    await OpenTownPortal(remote, stream, ciphers, conn, from.X, from.Y, "Portal skill");
            });
            return true;
        }
        if (gem.Name is not null && UseChargeSkillInstance(conn, skillId, gem.Effect, gem.Level, gem.Quality, from)) return true;
        if (gem.Name is not null && IsRighteousFire(gem.Effect))
            await ActivateRighteousFire(remote, stream, ciphers, conn, skillId, gem.Effect, gem.Level, gem.Quality, gem.Gem);
        else
            await LaunchProjectile(remote, stream, ciphers, conn, skillId, from, to, recoveryGeneration, targetId, sequence: sequence,
                actionToken: actionToken, cooldownUse: cooldownUse);
        return true;
    }

    // An instant skill (C→S 0x191): its effect, then S→C 0x1ce, which has the client run the skill's action without animation
    // and consume a use of it: the skill bar's cooldown starts (the tooltip showed the cooldown, the bar did not run it).
    internal static async Task UseInstantSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, uint targetId, (int X, int Y) at)
    {
        var gem = GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is not null && InstantLocationSkill.Supported(Skills, gem.Effect))
        {
            await UseInstantSkillAtLocation(remote, stream, ciphers, conn, skillId, default, targetId);
            return;
        }
        if (gem.Name is null || !InstantLocationSkill.ObjectTargetSupported(Skills, gem.Effect)) return;
        int epoch;
        lock (conn.Monsters)
        {
            epoch = conn.ProjectileEpoch;
            if (!conn.ProjectileActive(epoch) || conn.Paused || !conn.PlayerSent || conn.PendingTransfer is not null) return;
        }
        if (!TryBeginSkillCooldownUse(conn, skillId, gem.Effect,
            CooldownFor(conn, gem.Effect, gem.Level, gem.Quality),
            true, () => { }, out var cooldownUse)) return;
        if (await CastGemSkill(remote, stream, ciphers, conn, skillId, at, at, targetId, cooldownUse: cooldownUse))
        {
            await Send(remote, stream, ciphers, InstanceProtocol.InstantAction(PlayerId, skillId, at.X, at.Y, targetId),
                $"0x01ce player skill 0x{skillId:x4}: instant action (its use and cooldown)",
                still: () =>
                {
                    lock (conn.Monsters)
                        return conn.ProjectileActive(epoch) && conn.PlayerLife > 0 && !conn.Paused
                            && ActorSkillInstanceDisabled(conn, gem.Effect, skillId) is null
                            && (cooldownUse?.CommitNativeUse(conn, skillId) ?? true);
                });
            await SendNativeCooldownRefresh(remote, stream, ciphers, conn);
        }
    }

    static async Task UseInstantSkillAtLocation(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId, (uint X, uint Y) aim, uint? targetObjectId = null)
    {
        var gem = GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is null || !InstantLocationSkill.Supported(Skills, gem.Effect)) return;
        var own = Skills.EffectStats(gem.Effect, gem.Level, gem.Quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        int epoch;
        (int X, int Y) from, to;
        // GemSkills places the skill's own stat over the player's; use the same inputs for native distance resolution.
        Func<string, int> CurrentStats()
        {
            var player = FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2);
            return name => Stats.Key(name) is uint k ? own.TryGetValue(k, out int value) ? value : player.GetValueOrDefault(k) : 0;
        }
        lock (conn.Monsters)
        {
            epoch = conn.ProjectileEpoch;
            if (targetObjectId is uint target)
            {
                if (!InstantLocationSkill.TryResolveObject(conn, epoch, target, InTown(conn), CurrentStats(),
                    out from, out to, out _)) return;
            }
            else if (!InstantLocationSkill.TryResolve(conn, epoch, aim, InTown(conn), CurrentStats(), out from, out to)) return;
        }
        if (!TryBeginSkillCooldownUse(conn, skillId, gem.Effect,
            CooldownFor(conn, gem.Effect, gem.Level, gem.Quality),
            true, () => { }, out var cooldownUse)) return;
        if (!await CastGemSkill(remote, stream, ciphers, conn, skillId, from, to, cooldownUse: cooldownUse, locationInstant: true)) return;
        bool accepted = false;
        // Keep the raw aim: IceDash resolves it from the client's position when 0x1ce runs. Sending an already rounded
        // endpoint can normalize a short diagonal twice and move another cell. Resolve the server copy at this gate too.
        await Send(remote, stream, ciphers, [],
            $"0x01ce player skill 0x{skillId:x4}: instant {(targetObjectId is uint id ? $"on 0x{id:x}" : $"location ({aim.X}, {aim.Y})")}, movement only", atGate: () =>
            {
                using (ProjectileMutation(conn))
                {
                    var current = GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == skillId);
                    if (current != gem || GemSkillInstanceDisabled(conn, gem.Effect, skillId) is not null) return null;
                    (int X, int Y) rawAim;
                    if (targetObjectId is uint target)
                    {
                        if (!InstantLocationSkill.TryCommitObject(conn, epoch, target, InTown(conn), CurrentStats(),
                            out rawAim, out _)) return null;
                    }
                    else
                    {
                        if (!InstantLocationSkill.TryCommit(conn, epoch, aim, InTown(conn), CurrentStats(), out _)) return null;
                        rawAim = ((int)aim.X, (int)aim.Y);
                    }
                    if (cooldownUse?.CommitNativeUse(conn, skillId) == false) return null;
                    accepted = true;
                    return InstanceProtocol.InstantAction(PlayerId, skillId, rawAim.X, rawAim.Y, targetObjectId);
                }
            });
        if (accepted)
        {
            await SendNativeCooldownRefresh(remote, stream, ciphers, conn);
            await FollowRighteousFire(remote, stream, ciphers, conn);
        }
    }

    // Aura skills toggle: the first cast reserves their mana and puts their buff on the player, the next cast turns them
    // off. The reserved mana is sent in the Life pool and in every later 0x1b1 mana update; the client does not derive it.
    static async Task<bool> ToggleAura(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, int effect, string name, (int Flat, int Percent) reservation, int level, int quality)
    {
        string? effectId = Skills.EffectId(effect);
        if (effectId is null || !AuraCatalog.Supported(effectId))
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {name}: reservation is known but this aura's effect is not implemented yet");
            return false;
        }
        AuraCatalog.Aura? ended = null;
        AuraCatalog.Aura? started = null;
        lock (conn.Monsters)
        {
            // Locked commit backstop: a source may have changed after dispatch/cooldown preflight.
            if (conn.Closed || conn.LeavingArea || conn.PlayerLife == 0 || InTown(conn)
                || OwnAuraRestrictionValue(conn) != 0) return false;
            if (conn.Auras.Remove(skillId, out var old))
                ended = old;
            else if (conn.Auras.Values.FirstOrDefault(a => a.EffectId == effectId) is { } same)
            {
                // One aura of a kind at a time: a second gem of the same aura can't start it.
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {name}: refused, {same.Name} already runs (skill 0x{same.SkillId:x4})");
                return false;
            }
            else
            {
                var aura = new AuraCatalog.Aura(skillId, effect, effectId, name, conn.NextBuff++,
                    AuraCatalog.BuffRow(effectId), reservation.Flat, reservation.Percent, level, quality, SourceBuff: conn.NextBuff++);
                conn.Auras[skillId] = aura;
                int reserved = AuraCatalog.RequiredMana(conn.Pools, conn.Auras.Values);
                if (reserved > conn.Pools.Mana)
                {
                    conn.Auras.Remove(skillId);
                    Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {name}: needs {reserved} mana reserved, " +
                        $"maximum is {conn.Pools.Mana}");
                    return false;
                }
                started = aura;
                conn.PlayerMana = (uint)Math.Min(conn.PlayerMana, (uint)(conn.Pools.Mana - reserved));
            }
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
            conn.AuraStatsDirty = true;
            CharacterSaves.SetAuraActive(CharacterName(conn), effectId, started is not null);
        }

        await SendOwnAuraChanges(remote, stream, ciphers, conn);
        await SendCharacterStats(remote, stream, ciphers, conn, started is not null ? $"aura {name} on" : $"aura {ended!.Name} off");
        await SendPlayerMana(remote, stream, ciphers, conn,
            $"0x01b1 player mana {conn.PlayerMana}/{conn.Pools.Mana}, {ReservedMana(conn)} reserved");
        return true;
    }

    static IEnumerable<byte[]> AuraBuffs(AuraCatalog.Aura aura) =>
        AuraCatalog.StartPackets(aura, PlayerId, Skills.ActionHash(aura.Effect));

    // Every projectile uses the same scheduler and simulator. Definitions change rules, never execution paths.
    static Task LaunchProjectile(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (int X, int Y) from, (int X, int Y) to, int recoveryGeneration, uint targetId = 0, ushort sequence = 0,
        int? actionToken = null, CooldownUseTicket? cooldownUse = null)
    {
        var gem = GemSkillEffects(conn.Character).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is null) return Task.CompletedTask;
        if (UsesProjectileRuntime(gem.Effect))
            return ScheduleGemProjectile(remote, stream, ciphers, conn, skillId, gem, to,
                actionToken ?? cooldownUse?.ActionToken ?? conn.ActionCount, recoveryGeneration, cooldownUse);
        // A channelled skill (Cyclone, ActiveSkillType 47) hits as it starts: its spin is already in motion, and the later
        // sweeps follow on the attack timer (RunChannel).
        bool channelled = Supports.Of(gem.Effect)?.Types.Contains(47) == true;
        return AreaSkillHit(remote, stream, ciphers, conn, skillId, gem, from, to, immediate: channelled, sequence: sequence,
            actionToken: actionToken, cooldownUse: cooldownUse);
    }

    // A channelled skill's hits (Cyclone): one sweep around the player every attack time (SupportedAttackSeconds: the
    // skill's own speed, its supports and the player's attack speed, as the tooltip shows) for as long as the channel
    // runs. The hits used to wait for the client's position updates (C→S 0x197), which do not come at that rate, so a
    // short channel landed one late sweep. The channel ends with C→S 0x199, any other action, mana, death or travel, or
    // when the client's 0x197 updates stop for ChannelSilenceSeconds (it sends them all the time while the button is held,
    // so a lost 0x199 cannot keep a channel spinning on its own).
    const double ChannelSilenceSeconds = 1.0;

    static async Task RunChannel(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, int action)
    {
        bool Running() => !conn.Closed && !conn.LeavingArea && conn.PlayerLife > 0 && conn.ChannelSkill == skillId
            && conn.ActionCount == action && conn.Clock.Elapsed.TotalSeconds - conn.LastChannelUpdateAt <= ChannelSilenceSeconds;
        try
        {
            while (true)
            {
                double wait = conn.NextChannelHitAt - conn.Clock.Elapsed.TotalSeconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait));
                await WhilePaused(conn);
                if (!Running()) return;
                double now = conn.Clock.Elapsed.TotalSeconds;
                (int X, int Y) at;
                lock (conn.Monsters)
                {
                    if (!Running() || now < conn.NextChannelHitAt) continue;
                    conn.NextChannelHitAt = Math.Max(conn.NextChannelHitAt, now - 0.05) + SupportedAttackSeconds(conn, skillId);
                    at = conn.Walk.Position(now);
                }
                if (!PayMana(conn, skillId, out uint cost))
                {
                    conn.ChannelSkill = 0;
                    await SendPlayerMana(remote, stream, ciphers, conn, "channel stopped: insufficient mana");
                    return;
                }
                if (cost > 0) await SendPlayerMana(remote, stream, ciphers, conn, $"channel cost {cost}");
                await AreaSkillHit(remote, stream, ciphers, conn, skillId, gem, at, at,
                    immediate: true, sequence: conn.ChannelSkillSequence, actionToken: action);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    // The radius in cells of a gem skill's hit around its aimed point (AreaSkillHit): its own
    // active_skill_base_area_of_effect_radius, else Cyclone's 16 or the generic 12, grown by the player's area stats
    // (SkillArea). The radius stat is in cells (Righteous Fire's 18 is its 1.8 m, verified against the real game 2026-10-23).
    static int SkillRadius(ClientState conn, IReadOnlyDictionary<uint, int> skillStats, bool spell, int effect)
    {
        var areaStats = MergeSkillStats(AreaPlayerStats(conn), skillStats);
        int Get(string name) => Stats.Key(name) is uint k ? areaStats.GetValueOrDefault(k) : 0;
        return SkillArea.Radius(SkillArea.BaseRadius(Skills.EffectId(effect), Get), Get, spell);
    }

    // Whether a living monster stands within a gem attack's hit radius of a ground point. The client sends a point with a
    // monster on it as C→S 0x25 (not C→S 0x24 on the object), so an attack-move at such a point has to become a swing.
    static bool MonsterAtAim(ClientState conn, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        int x, int y, double at)
    {
        var stats = SupportedSkill(conn, gem.Id).Stats;
        int radius = SkillRadius(conn, stats, spell: false, gem.Effect);
        lock (conn.Monsters)
            return conn.Monsters.Any(m => !m.Value.Dead && m.Value.Life > 0
                && Dist(m.Value.Walk.Position(at), (x, y)) <= radius);
    }

    // A gem skill without a projectile (strikes, slams, area spells): a first, simple hit (2026-10-23). It lands once, after
    // half the attack time (attacks) or the cast (spells), on every living monster within the skill's area radius
    // (active_skill_base_area_of_effect_radius, else AreaSkillRadius cells, grown by area of effect: SkillArea) of the aimed point, or of the player for a
    // point too far for a melee skill. Attacks deal the weapon's hit as the default attack does, spells their own damage
    // (SkillCatalog.HitDamage) with the player's stats; both convert, crit and roll per monster. An attack's hit carries
    // the skill's damage effectiveness (SkillCatalog.DamageEffectiveness). Not modelled yet: the skills' own shapes (waves,
    // cones, chains), stun, and the attack's accuracy roll.
    static async Task AreaSkillHit(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) from, (int X, int Y) to,
        bool immediate = false, ushort sequence = 0, int? actionToken = null, CooldownUseTicket? cooldownUse = null)
    {
        bool Current() => cooldownUse?.Current(conn) ?? (actionToken is null || conn.ActionCount == actionToken);
        if (!Current()) return;
        var skillStats = SupportedSkill(conn, skillId).Stats;
        // An attack gem's share of the weapon's damage ("Deals 77.3% of base Attack Damage"): the hit below uses it.
        int effectiveness = Skills.DamageEffectiveness(gem.Effect, gem.Level);
        int Stat(string name) => Stats.Key(name) is uint k ? skillStats.GetValueOrDefault(k) : 0;
        if (Stat("base_deal_no_damage") != 0) return;
        var hitPlan = SkillHitPlan.ForSkill(Skills, gem.Effect, gem.Level, gem.Quality);
        if (!hitPlan.CanHit) return;
        bool spell = hitPlan.Kind == SkillHitKind.Spell;
        var weapon = WeaponFor(conn);
        int? castDuration = spell ? CastMs(conn, gem.Effect, skillId) : 0;
        if (castDuration is null) return;
        double delay;
        // An attack lands at its contact keyframe; the player swings where it stands, so without the walk-in offset.
        lock (conn.Monsters) delay = spell ? castDuration.Value / 1000.0
            : AttackContactDelay(conn, SupportedAttackSeconds(conn, skillId), approach: false);
        int radius = SkillRadius(conn, skillStats, spell, gem.Effect);
        // A melee skill aimed far away still lands in front of the player.
        var centre = !spell && Dist(from, to) > radius + weapon.Range ? from : to;
        int action = actionToken ?? cooldownUse?.ActionToken ?? conn.ActionCount;
        if (cooldownUse?.Instant != true && conn.ActionCount != action) return;
        // A channel's hits (Cyclone, C→S 0x197) land at once, where the player is.
        if (!immediate)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.05, delay)));
            await WhilePaused(conn);
            if ((cooldownUse?.Instant != true && conn.ActionCount != action) || conn.Closed || !Current()) return;
        }
        List<(uint Id, SpawnedMonster M)> targets;
        using (ProjectileMutation(conn))
        {
            double now = ProjectileWorldTime(conn);
            targets = [.. conn.Monsters.Where(e => !e.Value.Dead && e.Value.Life > 0 && Dist(e.Value.Walk.Position(now), centre) <= radius)
                .Select(e => (e.Key, e.Value))];
        }
        bool fortificationGranted = false; // One simultaneous sweep, independent of notification latency.
        bool rageGranted = false; // Only a positive gain takes this sweep's latch.
        // One sweep, one hit: its offensive inputs are read once, at the first contact (inside the projectile mutation, after
        // earlier contacts caught up), and rolled per monster. They were computed again for every monster (a whole stat
        // calculation each), which spread one sweep's hits over a second in a crowd.
        Dictionary<uint, int>? player = null;
        Penetration.PerType pen = default;
        HitResistances.Mods mods = default;
        DamagePacket sweepHit = default!;
        int criticalChance = 0, sweepMultiplier = 0;
        // Every monster gets its hit before the sweep's deaths run their loot, experience and quest work.
        var deaths = new List<(uint Id, SpawnedMonster M)>();
        foreach (var (id, m) in targets)
        {
            MonsterHit? applied;
            int damage;
            bool critical;
            string? resistance;
            using (ProjectileMutation(conn))
            {
                // Catch up earlier projectile contacts before reading Rage or any other live offensive input.
                if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0 || conn.Paused
                    || !Current() || cooldownUse is null && !PlayerActionCurrent(conn, action)) return;
                if (m.Dead || m.Life == 0) continue;
                if (MonsterBlocks(m, Random.Shared, spell))
                {
                    Console.WriteLine($"[gateway] {remote} {gem.Name} on 0x{id:x} ({m.Label}) blocked");
                    continue;
                }
                if (player is null)
                {
                    player = CharacterPlayerStats(conn);
                    var stats = FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2);
                    pen = Penetration.Of(stats, Stats, attack: !spell, skillStats);
                    mods = HitResistMods(stats, skillStats);
                    sweepHit = SupportedHit(conn, skillId);
                    criticalChance = SupportedCriticalChance(conn, skillId);
                    sweepMultiplier = SupportedCriticalMultiplier(conn, skillId);
                }
                var rolled = sweepHit.Roll(Random.Shared);
                critical = RollCriticalStrike(conn, criticalChance, skillStats, attack: !spell);
                int multiplier = CriticalMultiplierOn(m, sweepMultiplier);
                (damage, resistance) = MonsterDamage(m, rolled, critical, attack: !spell, mods, pen);
                if (critical) damage = DamageModel.CriticalDamage(damage, multiplier);
                applied = ApplyMonsterHitLocked(conn, m, damage);
                if (applied is { StageStarts: true } staged)
                {
                    CommitBossStage(remote, stream, ciphers, conn, id, m, ProjectileWorldTime(conn));
                    applied = staged with { StageStarts = false };
                }
                if (applied is not null)
                {
                    if (!fortificationGranted)
                        fortificationGranted = FortificationOnHitLocked(conn, m,
                            RawFortificationDamage(rolled, critical, multiplier),
                            !spell && Skills.IsMelee(gem.Effect), player, skillStats);
                    if (!rageGranted) rageGranted = RageOnHitLocked(conn, attack: !spell,
                        melee: !spell && Skills.IsMelee(gem.Effect), critical, skillStats);
                }
            }
            if (applied is not { } hit) continue;
            if (critical) _ = GainFlaskChargesOnCrit(remote, stream, ciphers, conn);
            await NotifyMonsterHit(remote, stream, ciphers, conn, id, m, hit, damage,
                $"{gem.Name}{(critical ? " (critical)" : "")}{(resistance is null ? "" : $", {resistance}")}",
                melee: !spell, sequence: sequence, deaths: deaths);
            ChargesOnHit(conn, m, critical, spell, gem.Effect, gem.Level, gem.Quality, primary: id == targets[0].Id);
        }
        foreach (var (deadId, dead) in deaths) await MonsterKilled(remote, stream, ciphers, conn, deadId, dead);
        Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: {(spell ? "spell" : "attack")} hit {targets.Count} monster(s) within {radius} cells of ({centre.X}, {centre.Y}) (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
    }

    static async Task<bool> WaitProjectileTime(ClientState conn, double until, int epoch)
    {
        while (true)
        {
            lock (conn.Monsters)
            {
                if (!conn.ProjectileActive(epoch)) return false;
                if (!conn.Paused && conn.Clock.Elapsed.TotalSeconds >= until) return true;
            }
            await Task.Delay(16);
        }
    }

    // A hit on a living monster: life is mutated once under Monsters, then notifications await outside the lock. False when
    // the monster was dead already or the action was replaced (the hit did not land).
    static async Task<bool> HitMonster(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m,
        int damage, string by, bool log = true, bool melee = false, ushort sequence = 0, int? actionToken = null,
        CooldownUseTicket? cooldownUse = null, Action? accepted = null, List<(uint Id, SpawnedMonster M)>? deaths = null)
    {
        MonsterHit? hit;
        using (ProjectileMutation(conn))
        {
            if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0 || conn.Paused) return false;
            if (cooldownUse is not null ? !cooldownUse.Current(conn) : actionToken is int token && !PlayerActionCurrent(conn, token)) return false;
            hit = ApplyMonsterHitLocked(conn, m, damage);
            if (hit is { StageStarts: true } staged)
            {
                CommitBossStage(remote, stream, ciphers, conn, id, m, ProjectileWorldTime(conn));
                hit = staged with { StageStarts = false };
            }
            if (hit is not null) accepted?.Invoke();
        }
        if (hit is { } applied) await NotifyMonsterHit(remote, stream, ciphers, conn, id, m, applied, damage, by, log, melee, sequence, deaths);
        return hit is not null;
    }

    // The damage a player's outgoing hit deals to its monster: one roll per type, then each type's own base resistance
    // with the hit's penetration subtracted from the capped value (HitMitigation), summed to the total the critical
    // multiplier later applies to. Damage over time has neither here and keeps its own path. Internal for the tests.
    internal static int MonsterDamage(SpawnedMonster m, DamagePacket hit, Random random, Penetration.PerType pen) =>
        MonsterTakes(m, hit, random, spell: false, pen);

    // Resolve an already-rolled packet using its single critical draw; target damage taken follows mitigation.
    internal static (int Damage, string? Resistance) MonsterDamage(SpawnedMonster m, DamagePacket rolled,
        bool critical, bool attack, HitResistances.Mods mods, Penetration.PerType pen = default)
    {
        var described = new List<string>();
        var strike = new HitResistances.Strike(Critical: critical, Attack: attack);
        var mitigated = DamageModel.Mitigate(rolled, type =>
        {
            var value = MonsterHitResistance(m, type, pen);
            if (rolled[type].Max > 0 && HitResistances.Describe(type, value, strike, mods) is { } note) described.Add(note);
            return HitResistances.Effective(type, value, strike, mods);
        });
        return (MonsterTakesMitigated(m, mitigated, spell: !attack),
            described.Count > 0 ? string.Join(", ", described) : null);
    }

    internal static HitResistances.Mods HitResistMods(IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int>? skill = null)
    {
        var a = HitResistances.Mods.From(player, Stats.Key);
        var b = skill is null ? default : HitResistances.Mods.From(skill, Stats.Key);
        return new(a.CriticalIgnoreElemental | b.CriticalIgnoreElemental,
            a.AttackCriticalIgnoreElemental | b.AttackCriticalIgnoreElemental,
            a.NonCriticalPenetration + b.NonCriticalPenetration, a.CannotPenetrateOrIgnore | b.CannotPenetrateOrIgnore);
    }

    // The short state mutation is shared with projectile impact; no awaits under Monsters.
    static MonsterHit? ApplyMonsterHitLocked(ClientState conn, SpawnedMonster m, int damage)
    {
        if (m.Dead || m.Life == 0) return null;
        (m.Life, bool starts) = TakeDamageLocked(m, damage);
        conn.LastEnemyHitAt = conn.Clock.Elapsed.TotalSeconds;
        if (m.Life == 0)
        {
            m.Aggro = false; m.Dead = true;
            CollectVaalSoulAtDeathLocked(conn, m);
        }
        return new(m.Life, starts);
    }

    static async Task NotifyMonsterHit(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m,
        MonsterHit hit, int damage, string by, bool log = true, bool melee = false, ushort sequence = 0,
        List<(uint Id, SpawnedMonster M)>? deaths = null)
    {
        var (life, stageStarts) = hit;
        if (life == 0)
            await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]),
                !log ? null : $"0x01b3 monster 0x{id:x} hide_mini_life_bar = 1");
        // The hit result for the client's damage/effect pipeline (0x1af): a melee hit plays the target's blood and the weapon
        // impact sound "bonk" locally, 0x1b1 stays authoritative on life. Spells and projectiles have their own impact and skip it.
        if (melee)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            var (px, py) = conn.Walk.Position(now);
            var (mx, my) = m.Walk.Position(now);
            await Send(remote, stream, ciphers,
                InstanceProtocol.ActionHit(PlayerId, sequence, id, flags: 0x4201, kind: 0x03, angle: HitAngle(mx - px, my - py)),
                !log ? null : $"0x01af player hit on monster 0x{id:x} (skill, sequence 0x{sequence:x4})");
        }
        // The hit reaction: one of the rig's TakeHit clips and its SoundEvent (0x1b2), as in Attack.
        await SendFlinch(remote, stream, ciphers, conn, id, m, damage, log);
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(id, life),
            !log && life > 0 ? null : $"0x01b1 monster 0x{id:x} hit by {by} for {damage}, life {life}/{m.MaxLife}{(life == 0 ? " (dies)" : "")}");
        // One sweep's deaths (loot, experience, quests) wait until every monster of the sweep has its hit (AreaSkillHit).
        if (life == 0) { if (deaths is not null) deaths.Add((id, m)); else await MonsterKilled(remote, stream, ciphers, conn, id, m); }
        if (stageStarts) _ = RunBossStage(remote, stream, ciphers, conn, id, m);
    }

    // The hit reaction: one of the rig's TakeHit clips and its SoundEvent, S→C 0x1b2 kind 0 (see Attack and HitMonster).
    // A killing blow gets none: the client's Life component runs the death instead, and the flinch would only be cut off.
    // The gates (HitReaction) keep it a stun-like reaction rather than a loop; see that class. The monster is checked again
    // inside the send gate: a hit that kills it meanwhile marks it Dead before it sends its own 0x1b1, so the flinch either
    // goes out before the death or not at all.
    static async Task SendFlinch(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        SpawnedMonster m, int damage, bool log)
    {
        HitAnimation? clip;
        lock (conn.Monsters) clip = HitReaction.TryFlinch(m, damage, conn.Clock.Elapsed.TotalSeconds, Random.Shared);
        if (clip is not { } flinch) return;
        await Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(id, 0, (uint)flinch.Index, 1f),
            !log ? null : $"0x01b2 monster 0x{id:x} flinch (animation {flinch.Index}, {flinch.Seconds:0.00} s)",
            still: () => { lock (conn.Monsters) return m.Life > 0 && !m.Dead; });
    }

    internal sealed record PlayerRecoveryIntent(int Epoch, int Action, double Until, float Seconds);

    // Cancellation belongs to the hit boundary; the native animation is a projection of that committed interval.
    internal static PlayerRecoveryIntent? CommitPlayerHitRecoveryLocked(ClientState conn, int damage, double now)
    {
        if (damage <= 0) return null;
        var stats = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        void Trace(string reason)
        {
            if (Get(AntiquarianEffects.StunRecoverySource) > 0)
                Console.WriteLine($"[gateway] Turtle Shell stun: {reason}");
        }
        if (stats.FirstOrDefault(s => s.Value != 0 && PlayerHitRecovery.UnsupportedStat(Stats.Name(s.Key))) is { Key: > 0 } unsupported)
        { Trace("unsupported stat " + Stats.Name(unsupported.Key)); return null; }
        // Skill immunity can depend on a native action's contact phase, which this server does not track yet.
        // Conservatively skip builds with any equipped skill carrying it, rather than stun an uninterruptible phase.
        if (GemSkillEffects(conn.Character).Any(g => Skills.EffectStats(g.Effect, g.Level, g.Quality)
            .Any(s => s.Item2 != 0 && Stats.Name(s.Item1).StartsWith("skill_cannot_be_stunned", StringComparison.Ordinal))))
        { Trace("unsupported skill immunity"); return null; }
        if (PlayerHasEnergyShieldForRecovery(conn, Get)) { Trace("energy shield present"); return null; }
        double chance = PlayerHitRecovery.Chance(damage, conn.PlayerMaxLife, Get);
        float seconds = PlayerHitRecovery.Duration(Get);
        if (seconds <= 0 || Random.Shared.NextDouble() * 100 >= chance
            || conn.Closed || conn.PlayerLife == 0 || !conn.HitRecovery.Begin(now, seconds))
        { Trace($"not accepted (chance {chance:0.###}%, duration {seconds:0.###} s)"); return null; }
        Trace($"accepted ({seconds:0.###} s, {Get(AntiquarianEffects.StunRecoverySource)}% Life over one second)");
        conn.Antiquarian.Stunned(now, conn.PlayerMaxLife, Get(AntiquarianEffects.StunRecoverySource));
        int action = Interlocked.Increment(ref conn.ActionCount);
        conn.ChannelSkill = 0; conn.NextAttackAt = now;
        var at = conn.Walk.Position(now); conn.Walk.Teleport(at.X, at.Y, now);
        return new(conn.ProjectileEpoch, action, conn.HitRecovery.Until, seconds);
    }

    static Task SendPlayerHitRecovery(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        PlayerRecoveryIntent recovery) => Send(remote, stream, ciphers, [], "0x01d1 player TakeHit recovery", atGate: () =>
        {
            lock (conn.Monsters)
            {
                double remaining = recovery.Until - conn.Clock.Elapsed.TotalSeconds;
                if (!conn.ProjectileActive(recovery.Epoch) || conn.PlayerLife == 0 || conn.Paused
                    || conn.ActionCount != recovery.Action || remaining <= 0) return null;
                return InstanceProtocol.ActorAnimation(PlayerId, (float)Math.Min(remaining, recovery.Seconds), PlayerHitRecovery.TakeHitAnimation);
            }
        });

    static bool PlayerActionCurrent(ClientState conn, int action)
    {
        lock (conn.Monsters)
            return !conn.Closed && !conn.LeavingArea && !conn.RageRetired && conn.PlayerLife > 0
                && conn.ActionCount == action && !conn.HitRecovery.Active(conn.Clock.Elapsed.TotalSeconds);
    }

    // A hit's damage off a monster's life (under the Monsters lock): the new life, and whether the hit starts its boss stage
    // (BossFights; the stage is then taken, so it runs once).
    static (uint Life, bool StageStarts) TakeDamageLocked(SpawnedMonster m, int damage)
    {
        // While the stage runs the boss cannot die: its life fills up at the end anyway.
        if (m.StageRunning) return ((uint)Math.Max(1, (long)m.Life - damage), false);
        var (life, starts) = BossFights.Damage(m.Life, m.MaxLife, damage, m.StageTaken ? null : m.Stage);
        if (starts) m.StageTaken = m.StageRunning = true;
        return (life, starts);
    }

    // A boss's stage (BossFights): it stops and uses its stage skill in place (0x1ab, the skill's animation: Hillock pulls
    // out his sword), then its Transitionable steps to the stage's state (0x2ba: the sword in his hand) and its life fills
    // up (0x1b1).
    static async Task RunBossStage(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m)
    {
        var stage = m.Stage!;
        (int X, int Y) at;
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            at = m.Walk.Position(now);
            m.Walk.Order(at.X, at.Y, now);
            m.LastTarget = null;
            m.BusyUntil = now + stage.CastSeconds;
        }
        await Send(remote, stream, ciphers, InstanceProtocol.ActionStarted(id, at.X, at.Y, at.X, at.Y, StageSkillId, 0xffff, 0x0408),
            $"0x01ab monster 0x{id:x} ({m.Label}) starts its stage at {m.Life}/{m.MaxLife} life: skill 0x{StageSkillId:x4} (GrantedEffects {stage.SkillEffect}), {stage.CastSeconds:0.#} s");
        await Task.Delay(TimeSpan.FromSeconds(stage.CastSeconds));
        await WhilePaused(conn);
        uint life;
        lock (conn.Monsters)
        {
            m.StageRunning = false;
            if (m.Dead || conn.Closed) return;
            m.Life = life = m.MaxLife;
        }
        if (m.Variety?.ComponentsOrBase.ToList().IndexOf("Transitionable") is int t and >= 0)
            await Send(remote, stream, ciphers, InstanceEntities.TransitionableUpdate(id, (byte)t, stage.TransitionTo),
                $"0x02ba monster 0x{id:x} ({m.Label}) Transitionable → {stage.TransitionTo}");
        await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(id, life), $"0x01b1 monster 0x{id:x} ({m.Label}) life refilled to {life}");
        // The stage's action is over: 0x1ac ends it, so the client never keeps the boss in it.
        await Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(id), $"0x01ac monster 0x{id:x} ({m.Label}) stage action ended");
    }

    // A boss whose variety has the big life bar (MonsterVarieties BossHealthBar) shows it at the top of the screen once it
    // aggroes: its StateMachine's boss_life_bar (Metadata/Monsters/BossLifeBar.ot) and aggro (Hillock's aggro effect) go to 1.
    static async Task ShowBossLifeBar(string remote, NetworkStream stream, ConnectionCiphers ciphers, uint id, SpawnedMonster m, ClientState conn)
    {
        if (m.Variety is not { } v || m.States is not { } states) return;
        int index = v.ComponentsOrBase.ToList().IndexOf("StateMachine");
        if (index < 0) return;
        states = states.Set("boss_life_bar", 1).Set("aggro", 1);
        lock (conn.Monsters) m.States = states;
        await Send(remote, stream, ciphers, InstanceEntities.StateMachineUpdate(id, (byte)index, states),
            $"0x02ba monster 0x{id:x} ({m.Label}) StateMachine: boss_life_bar 1, aggro 1");
    }

    // A monster died (its life reached 0 from the player's attack or skill): the player gains its experience when near
    // enough and it drops its items around where it fell (ItemDrops). Its corpse stays where it fell for as long as the
    // player is in the area (it stays in Monsters as Dead: no AI, no body collision); leaving does not save it.
    static async Task MonsterKilled(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        (int X, int Y) at, player;
        bool alive;
        lock (conn.Monsters) { at = m.Walk.Position(now); player = conn.Walk.Position(now); alive = conn.PlayerLife > 0; }
        await SendVaalSouls(remote, stream, ciphers, conn);
        if (m.Variety is not { } variety || !m.Hostile) return;
        ChargesOnKill(conn, m);
        if (conn.Layout.Id == TutorialObjects.Strand && variety.Id == TutorialObjects.Zombie
            && CharacterSaves.Load(CharacterName(conn))?.Tutorial is { ZombieKilled: false })
        {
            CharacterSaves.ChangeTutorial(CharacterName(conn), t => t with { ZombieKilled = true });
            await DropTutorialGem(remote, stream, ciphers, conn, false, at.X, at.Y);
        }
        await SetQuestFlags(remote, stream, ciphers, conn, Quests.OnMonsterKilled(conn.Layout.Id, variety.Id));
        await DropQuestItems(remote, stream, ciphers, conn, variety.Id, at);   // a1q5: Hailrake's Medicine Chest
        if (alive && Dist(at, player) <= ExperienceRange && Monsters.BaseExperience(variety, m.Level) is int baseXp)
        {
            uint xp = Progression.MonsterExperience(baseXp, variety.ExperienceMultiplier, m.Rarity, conn.Pools.Level, m.Level);
            // The gems gain their share of the kill only (not a quest's level or /add_xp), at the character's cap too.
            await GainGemExperience(remote, stream, ciphers, conn, xp);
            await GainExperience(remote, stream, ciphers, conn, xp, $"{m.Rarity} {m.Label} 0x{id:x}");
        }
        // A quest kill that levels the character up (a1q1: Hillock → at least level 2), wherever the player stands.
        if (Quests.LevelAfterKill(conn.Layout.Id, variety.Id) is int level and > 1 && level <= CharacterStats.LevelStarts.Count)
        {
            uint needed = CharacterStats.LevelStarts[level - 1], have;
            lock (conn.Monsters) have = conn.Experience;
            if (have < needed) await GainExperience(remote, stream, ciphers, conn, needed - have, $"quest: {m.Label} killed, level {level}");
        }
        await GainKillCharges(remote, stream, ciphers, conn, m.Rarity, $"{m.Rarity} {m.Label} 0x{id:x} killed");
        await DropLoot(remote, stream, ciphers, conn, m, at);
    }

    // The monster's items (ItemDrops.Roll) as WorldItem objects scattered around where it died, on walkable cells when the
    // area's walk map is known. Like dropped items they lie in the instance (InstanceStore).
    static async Task DropLoot(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, SpawnedMonster m, (int X, int Y) at)
    {
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0") return;
        List<ItemDrops.Drop> drops = [.. Drops.Roll(m.Level, m.Rarity, Random.Shared, IncreasedItemRarity(conn))];
        // And a pile of gold (ItemDrops.Gold), picked up into the stash's gold.
        if (Drops.Gold(m.Level, m.Rarity, Random.Shared) is { } gold) drops.Add(gold);
        foreach (var drop in drops)
        {
            // A ring around the corpse, 3 to 6 cells out.
            double angle = Random.Shared.NextDouble() * Math.Tau, r = 3 + Random.Shared.NextDouble() * 3;
            int x = at.X + (int)Math.Round(Math.Cos(angle) * r), y = at.Y + (int)Math.Round(Math.Sin(angle) * r);
            if (conn.Layout.Walkable is { } walks && !walks(x, y)) (x, y) = at;
            var ground = new WorldItemObject(x, y, drop.Item);
            uint id = conn.Instance.Ground.Add(ground);
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob()),
                $"0x02b9 create world item 0x{id:x} {drop.Type.Name} (item level {drop.Item.ItemLevel}) dropped by {m.Label} at ({x}, {y})");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
        }
    }

    // /god: what it adds on top of the character's stats (GodStats) and pools (PoolsFor): 1000% increased attack and cast
    // speed, 100% of maximum life and 10 000 mana regenerated per second, 200% increased area of effect, +10 000 maximum
    // life and +1000 to each attribute.
}
