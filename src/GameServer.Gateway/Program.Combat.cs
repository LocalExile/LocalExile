using System.Diagnostics;
using System.Net;
using System.Text.Json;
using PlayerWalk = GameServer.Core.World.PlayerWalk;
using ClientPathfinder = GameServer.Core.World.ClientPathfinder;
using SaveFiles = GameServer.Core.Saves.SaveFiles;
using DamageOverTime = GameServer.Core.World.DamageOverTime;
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

    // Low Life checks the live pools' current life against their own maximum with the pools' threshold (the override
    // percentage when set, else the ordinary percentage, else the default 50, inclusive; DamageModel.LowLife). Dead
    // (current 0 is the boundary, but dead players do not roll) and a zero maximum (no pool) are not low life.
    internal static bool OnLowLife(ClientState conn) => DamageModel.LowLife(conn.PlayerLife, conn.PlayerMaxLife,
        conn.Pools.LowLifeThresholdOverride, conn.Pools.LowLifeThresholdPct);

    // The input gate reserves pacing and movement together, before an ordinary bow echo is queued.
    internal static bool TryBeginBowAttack(ClientState conn, double requestedAt, double duration,
        ushort skill, ushort sequence, out (int Action, int X, int Y) started)
    {
        started = default;
        lock (conn.Monsters)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            if (conn.Closed || conn.Paused || conn.PlayerLife == 0 || conn.HitRecovery.BlocksInput(requestedAt, now)
                || !double.IsFinite(duration) || !AttackTiming.Ready(now, conn.NextAttackAt, duration)) return false;
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
    internal static double SkillAttackSeconds(ClientState conn, int effect, int level) =>
        SkillAttackTiming.ForSkill(Skills, effect, level, WeaponFor(conn), CharacterPlayerStats(conn));

    static bool AttackReady(ClientState conn, ushort skillId, out double seconds)
    {
        // Admission and execution must use the same linked support and action-speed inputs. A premature request
        // cannot replace the current action before CastGemSkill rejects its shorter supported attack interval.
        seconds = SupportedAttackSeconds(conn, skillId);
        double now = conn.Clock.Elapsed.TotalSeconds;
        lock (conn.Monsters) return double.IsFinite(seconds) && AttackTiming.Ready(now, conn.NextAttackAt, seconds);
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
    // jitter); earlier repeats of 0x24 are ignored. At 0 the client's Life component runs its death; the corpse is
    // removed 10 s later.
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
            if ((bow && conn.Paused) || !double.IsFinite(duration) || !AttackTiming.Ready(now, conn.NextAttackAt, duration))
            {
                Console.WriteLine($"[gateway] {remote} <= 0x0024 skill 0x{use.SkillId:x4} seq 0x{use.Sequence:x4} ignored: {conn.NextAttackAt - now:0.000} s before the next attack");
                return;
            }
        int action;
        (int X, int Y) target, here, stop;
        double arrive;
        lock (conn.Monsters)
        {
            if (conn.HitRecovery.BlocksInput(requestedAt, conn.Clock.Elapsed.TotalSeconds)) return;
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
            if (!await SendOrdinaryBowAction(remote, stream, ciphers, conn, actionBytes, actionLog, action, projectileEpoch)) return;
        }
        else await Send(remote, stream, ciphers, actionBytes, actionLog, still: () => PlayerActionCurrent(conn, action));
        if (m.Life == 0) return;
        // A bow's target is an aim, not a reserved hit: its released arrow owns collision and damage.
        if (bow)
        {
            await LaunchOrdinaryBow(remote, stream, ciphers, conn, stop, target, duration, action, projectileEpoch);
            return;
        }
        // The hit lands at the attack animation's contact keyframe (data/animation-contacts.json), not a fixed half: the
        // client stretches the clip to the attack time, so the blood and the weapon impact sound line up with the swing.
        await Task.Delay(TimeSpan.FromSeconds(AttackContactDelay(conn, duration, approach: true)));
        uint life;
        bool stageStarts = false;
        int accuracy, evasion;
        lock (conn.Monsters)
        {
            if (m.Life == 0) return;
            if (conn.ActionCount != action)
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
            evasion = conn.FlaskGrounds.Evasion(use.TargetId,
                m.Mods.Effects.Evasion(Monsters.BaseEvasion(m.Level) ?? 0));
        }
        // Exhibit 122's global_always_hit (Stats key 687): the attack cannot be evaded, so there is no accuracy roll
        // at all. The accuracy rating itself, the panel's rating rows and the critical chance stay untouched.
        int hitChance = conn.Pools.AlwaysHit ? 100 : DamageModel.AccuracyHitChance(evasion, accuracy);
        if (!conn.Pools.AlwaysHit && !DamageModel.Hits(hitChance, Random.Shared.Next(100)))
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
        var player = CharacterPlayerStats(conn);
        var attackOwn = attackEffect >= 0 ? Skills.EffectStats(attackEffect, 1).ToDictionary(kv => kv.Item1, kv => kv.Item2) : null;
        var hit = AttackHit(weapon, player, WeaponProfileFor(conn, activeSet: true).Melee)
            .Convert(SkillConversion(player, attackEffect, 1, attack: true));
        var rolls = GamblerFor(player);
        hit = rolls.RollOutgoingHit(hit, Random.Shared);
        bool critical = rolls.IsCritical(conn.Pools.AttackCriticalStrikeChance, Random.Shared, OnLowLife(conn));
        if (critical) hit = hit.Critical(CriticalMultiplierOn(m,
            CriticalMultiplierFor(conn, spell: false, melee: MeleeAttack(conn))));
        // Impale: the stacks already on the target add their stored damage to this hit, this hit uses one of their five
        // hits, and the hit's own physical damage may store a new stack for the hits after it.
        double physical = hit[DamageType.Physical].Max;
        double stored, impaleStored;
        int impaleStacks, damage;
        bool impaled;
        lock (conn.Monsters)
        {
            if (m.Life == 0) return;
            if (conn.ActionCount != action)
            {
                Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} cancelled by a later action");
                return;
            }
            stored = m.Impale.Bonus;
            if (stored > 0) hit = hit.Add(DamageType.Physical, new DamageRange(stored, stored));
            m.Impale.AfterHit();
            int impaleChance = Stats.Key("impale_on_hit_%_chance") is uint impaleKey ? player.GetValueOrDefault(impaleKey) : 0;
            impaled = impaleChance > 0 && Random.Shared.Next(100) < impaleChance && m.Impale.Apply(physical);
            // Read before the hit is applied: a killing blow clears the target's impales.
            impaleStacks = m.Impale.Stacks;
            impaleStored = m.Impale.Bonus;
            // Impale reflection joins after outgoing multipliers, before the target's own mitigation.
            hit = ApplyPassiveDamageTaken(conn, m, ApplyMonsterModDamageTaken(m,
                rolls.MitigateOutgoingHit(hit,
                    HitResistancesFor(conn, m, player, attackOwn, critical, attack: true), Random.Shared), spell: false));
            damage = (int)Math.Round(hit.MinTotal);
            (m.Life, stageStarts) = TakeDamageLocked(m, damage);
            GainRageOnHit(conn, player, attack: true, WeaponProfileFor(conn, activeSet: true).Melee, critical);
            if (critical) ReserveWolfLocked(conn, DateTimeOffset.UtcNow);
            life = m.Life;
            conn.LastEnemyHitAt = conn.Clock.Elapsed.TotalSeconds;
            if (life == 0) { m.Aggro = false; m.Dead = true; m.Impale.Clear(); }
            ChargeHitCommitted(conn, m, new(life, stageStarts), critical, spell: false,
                WeaponProfileFor(conn, activeSet: true).Melee, new(), player,
                attackEffect >= 0 ? Skills.EffectId(attackEffect) : null, attackOwn);
        }
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
        await SendMonsterLife(remote, stream, ciphers, conn, use.TargetId, m,
            $"0x01b1 monster 0x{use.TargetId:x} hit for {damage}{(critical ? " (critical)" : "")}" +
            (stored > 0 ? $", impale +{stored:0.#} ({impaleStacks} stack(s))" : "") +
            (impaled ? $", impaled ({impaleStacks} stack(s), {impaleStored:0.#} stored)" : "") +
            $", life {life}/{m.MaxLife}{(life == 0 ? " (dies)" : "")}");
        if (life == 0) await MonsterKilled(remote, stream, ciphers, conn, use.TargetId, m);
        if (critical) await RefreshSummons(remote, stream, ciphers, conn);
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
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == use.SkillId);
        if (gem.Name is not null && GemDisabled(conn, gem.Effect, use.SkillId) is { } disabled)
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{use.SkillId:x4} {gem.Name}: {disabled} (action not started)");
            return;
        }
        if (gem.Name is not null && Skills.IsAttack(gem.Effect)
            && !AttackReady(conn, use.SkillId, out double attackSeconds))
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x}: ignored, the previous attack is still running (attack time {attackSeconds:0.00} s) (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
            return;
        }
        // A movement attack (Flicker Strike, Leap Slam) closes the distance itself, and an attack used with "always attack
        // without moving" (Flags bit 0) swings from where the player stands: both keep the immediate cast.
        if (gem.Name is not null && Skills.IsAttack(gem.Effect) && !Skills.IsMovement(gem.Effect) && (use.Flags & 0x1) == 0)
        {
            var weapon = WeaponFor(conn);
            double now = conn.Clock.Elapsed.TotalSeconds;
            int action = 0;
            (int X, int Y) target = default, here = default, stop = default;
            double arrive = now;
            if (!TryBeginCooldownUse(conn, gem.Effect, CooldownFor(conn, gem.Effect, gem.Level, gem.Quality), false,
                () =>
                {
                    action = Interlocked.Increment(ref conn.ActionCount);
                    target = m.Walk.Position(now);
                    (here, stop, arrive) = conn.Walk.Approach(target.X, target.Y, weapon.Range, now, tolerance: m.StopRange - 2);
                }, out var approachTicket, reserve: false, requestedAt: requestedAt, skillId: use.SkillId)) return;
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
            if (!TryBeginCooldownUse(conn, gem.Effect, CooldownFor(conn, gem.Effect, gem.Level, gem.Quality), false,
                () => { }, out var cooldownUse, actionToken: action, skillId: use.SkillId)) return;
            await Send(remote, stream, ciphers,
                InstanceProtocol.ActionStarted(PlayerId, stop.X, stop.Y, target.X, target.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
                $"0x01ab player skill 0x{use.SkillId:x4} {gem.Name} on monster 0x{use.TargetId:x} ({m.Label}) from ({stop.X}, {stop.Y}) to ({target.X}, {target.Y}), flags 0x{use.Flags:x4} (t={conn.Clock.Elapsed.TotalSeconds:0.000})",
                still: () => cooldownUse?.CommitNativeUse(conn, use.SkillId) ?? PlayerActionCurrent(conn, action));
            _ = CastGemSkill(remote, stream, ciphers, conn, use.SkillId, stop, target, use.TargetId, sequence: use.Sequence,
                actionToken: action, cooldownUse: cooldownUse);
            return;
        }
        double castAt = conn.Clock.Elapsed.TotalSeconds;
        int castAction = 0;
        (int X, int Y) aim = default, stand = default;
        if (!TryBeginCooldownUse(conn, gem.Effect, gem.Name is null ? null : CooldownFor(conn, gem.Effect, gem.Level, gem.Quality), false,
            () =>
            {
                castAction = Interlocked.Increment(ref conn.ActionCount);
                aim = m.Walk.Position(castAt);
                stand = conn.Walk.Position(castAt);
                conn.Walk.Order(stand.X, stand.Y, castAt);
            }, out var spellUse, requestedAt: requestedAt, skillId: use.SkillId)) return;
        await Send(remote, stream, ciphers,
            InstanceProtocol.ActionStarted(PlayerId, stand.X, stand.Y, aim.X, aim.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId),
            $"0x01ab player skill 0x{use.SkillId:x4} cast on monster 0x{use.TargetId:x} ({m.Label}) from ({stand.X}, {stand.Y}) to ({aim.X}, {aim.Y}), flags 0x{use.Flags:x4}",
            still: () => spellUse?.CommitNativeUse(conn, use.SkillId) ?? PlayerActionCurrent(conn, castAction));
        _ = CastGemSkill(remote, stream, ciphers, conn, use.SkillId, stand, aim, use.TargetId, sequence: use.Sequence,
            actionToken: spellUse?.ActionToken ?? castAction, cooldownUse: spellUse);
    }

    // A gem skill's cast: Righteous Fire puts its buff on, any other skill launches its projectile (if it has one). False when
    // it was refused (cooldown, mana), so its use is not confirmed to the client.
    static async Task<bool> CastGemSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (int X, int Y) from, (int X, int Y) to, uint targetId = 0, ushort sequence = 0,
        int? actionToken = null, CooldownUseTicket? cooldownUse = null, bool locationInstant = false)
    {
        int action = actionToken ?? conn.ActionCount;
        lock (conn.Monsters)
            if (actionToken is not null && (conn.ActionCount != action || conn.HitRecovery.Active(conn.Clock.Elapsed.TotalSeconds))) return false;
        if (InTown(conn) || conn.PlayerLife == 0) return false;
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
        bool Current() => cooldownUse?.Current(conn) ?? (actionToken is null || conn.ActionCount == actionToken);
        if (!Current()) return false;
        if (locationInstant && (gem.Name is null || !InstantLocationSkill.Supported(Skills, gem.Effect))) return false;
        if (gem.Name is not null && Skills.IsSpell(gem.Effect) && !Skills.IsAttack(gem.Effect)
            && CastMs(conn, gem.Effect, gem.Level, gem.Quality, skillId) is null)
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: invalid cast duration (no effect or mana cost)");
            await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana} (invalid cast duration, cost back)");
            return false;
        }
        // A gem whose requirements the character's level does not meet is disabled: its skill stays on the skill bar but
        // cannot be used (the client shows the gem red but still sends the use; the server withholds the action, see
        // OnClientMessages). Attributes are not checked yet: their formula is not fitted (19-gems.md "Open").
        if (gem.Name is not null && GemDisabled(conn, gem.Effect, skillId) is { } disabled)
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: {disabled} (disabled)");
            await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana} (disabled skill, cost back)");
            return false;
        }
        if (gem.Name is not null)
        {
            var supported = SupportedSkill(conn, skillId);
            gem.Level = supported.Level;
            gem.Quality = supported.Quality;
        }
        // Vaal souls are not gathered yet (Roadmap "Gems later"), so a Vaal skill never has the souls it needs.
        if (gem.Name is not null && Skills.VaalSouls(gem.Effect, gem.Level) is int souls and > 0)
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: needs {souls} Vaal souls, has 0");
            return false;
        }
        // Eligible uses arrive with their one original preflight reservation. Never reserve again after an await.
        if (cooldownUse is not null)
        {
            if (!cooldownUse.TryExecute(conn, gem.Effect)) return false;
            if (!cooldownUse.Instant) await SendNativeCooldownRefresh(remote, stream, ciphers, conn);
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
                ready = double.IsFinite(duration) && AttackTiming.Ready(now, next, duration);
                if (ready) conn.NextAttackAt = AttackTiming.After(now, next, duration);
            }
            if (!ready)
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: ignored, {next - now:0.000} s before the next attack (t={now:0.000})");
                return false;
            }
        }
        if (gem.Name is not null && Supports.Of(gem.Effect)?.Types.Contains(47) == true)
            conn.NextChannelHitAt = conn.Clock.Elapsed.TotalSeconds + SupportedAttackSeconds(conn, skillId);
        if (gem.Name is not null && SupportedReservation(conn, skillId) is { } reservation)
        {
            if (!Current()) return false;
            await ToggleAura(remote, stream, ciphers, conn, skillId, gem.Effect, gem.Name, reservation, SupportedSkill(conn, skillId).Level, SupportedSkill(conn, skillId).Quality);
            return true;
        }
        if (gem.Name is not null)
        {
            SkillCost cost;
            bool paid;
            lock (conn.Monsters)
            {
                if (!Current()) return false;
                paid = PaySkillCost(conn, gem.Effect, gem.Level, gem.Quality, out cost, skillId);
            }
            if (!paid)
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: cannot pay {cost.Mana} mana and {cost.Life} life");
                return false;
            }
            if (cost.Mana > 0)
                await SendPlayerMana(remote, stream, ciphers, conn,
                    $"0x01b1 player mana {conn.PlayerMana} (-{cost.Mana} for {gem.Name}) (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
            if (cost.Life > 0)
                await Send(remote, stream, ciphers, PlayerLifeUpdate(conn),
                    $"0x01b1 player life {conn.PlayerLife} (-{cost.Life} cost for {gem.Name})");
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
            if (CastMs(conn, gem.Effect, gem.Level, gem.Quality, skillId) is not int castMs) return false;
            _ = Task.Delay(castMs).ContinueWith(async _ =>
            {
                if (!conn.Closed && PlayerActionCurrent(conn, action) && Current())
                    await OpenTownPortal(remote, stream, ciphers, conn, from.X, from.Y, "Portal skill");
            });
            return true;
        }
        if (gem.Name is not null && CompanionForEffect(conn, gem.Effect) is { } companion)
        {
            if (CastMs(conn, gem.Effect, gem.Level, gem.Quality, skillId) is not int summonCastMs) return false;
            await ActivateCompanion(remote, stream, ciphers, conn, companion, summonCastMs);
        }
        else if (gem.Name is not null && IsRighteousFire(gem.Effect))
            await ActivateRighteousFire(remote, stream, ciphers, conn, skillId, gem.Effect, gem.Level, gem.Quality, gem.Gem);
        else
            await LaunchProjectile(remote, stream, ciphers, conn, skillId, from, to, targetId, sequence: sequence,
                actionToken: actionToken, cooldownUse: cooldownUse);
        return true;
    }

    // An instant skill (C→S 0x191): its effect, then S→C 0x1ce, which has the client run the skill's action without animation
    // and consume a use of it: the skill bar's cooldown starts (the tooltip showed the cooldown, the bar did not run it).
    internal static async Task UseInstantSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, uint targetId, (int X, int Y) at)
    {
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is not null && InstantLocationSkill.Supported(Skills, gem.Effect))
        {
            await UseInstantSkillAtLocation(remote, stream, ciphers, conn, skillId,
                ((uint)at.X, (uint)at.Y), targetObjectId: targetId);
            return;
        }
        if (gem.Name is null || !InstantLocationSkill.ObjectTargetSupported(Skills, gem.Effect)) return;
        int epoch;
        lock (conn.Monsters) epoch = conn.ProjectileEpoch;
        if (!TryBeginCooldownUse(conn, gem.Effect, gem.Name is null ? null : CooldownFor(conn, gem.Effect, gem.Level, gem.Quality),
            true, () => { }, out var cooldownUse, skillId: skillId)) return;
        if (await CastGemSkill(remote, stream, ciphers, conn, skillId, at, at, targetId, cooldownUse: cooldownUse))
        {
            await Send(remote, stream, ciphers, InstanceProtocol.InstantAction(PlayerId, skillId, at.X, at.Y, targetId),
                $"0x01ce player skill 0x{skillId:x4}: instant action (its use and cooldown)",
                still: () => conn.ProjectileActive(epoch) && conn.PlayerLife > 0 && !conn.Paused
                    && (cooldownUse?.CommitNativeUse(conn, skillId) ?? true));
            await SendNativeCooldownRefresh(remote, stream, ciphers, conn);
        }
    }

    static async Task UseInstantSkillAtLocation(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId, (uint X, uint Y) aim, uint? targetObjectId = null)
    {
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
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
        if (!TryBeginCooldownUse(conn, gem.Effect, CooldownFor(conn, gem.Effect, gem.Level, gem.Quality),
            true, () => { }, out var cooldownUse, skillId: skillId)) return;
        if (!await CastGemSkill(remote, stream, ciphers, conn, skillId, from, to, cooldownUse: cooldownUse, locationInstant: true)) return;
        bool accepted = false;
        // Keep the raw aim: IceDash resolves it from the client's position when 0x1ce runs. Sending an already rounded
        // endpoint can normalize a short diagonal twice and move another cell. Resolve the server copy at this gate too.
        await Send(remote, stream, ciphers, [],
            $"0x01ce player skill 0x{skillId:x4}: instant {(targetObjectId is uint id ? $"on 0x{id:x}" : $"location ({aim.X}, {aim.Y})")}, movement only", atGate: () =>
            {
                lock (conn.Monsters)
                {
                    var current = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
                    if (current != gem || GemDisabled(conn, gem.Effect, skillId) is not null
                        || cooldownUse?.Current(conn) == false) return null;
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
    static async Task ToggleAura(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, int effect, string name, (int Flat, int Percent) reservation, int level, int quality)
    {
        if (SkillRestrictionReason(conn, effect) is { } restricted)
        {
            Console.WriteLine($"[gateway] {remote} aura {name}: {restricted}");
            return;
        }
        string? effectId = Skills.EffectId(effect);
        if (effectId is null || !AuraCatalog.Supported(effectId))
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {name}: reservation is known but this aura's effect is not implemented yet");
            return;
        }
        AuraCatalog.Aura? ended = null;
        AuraCatalog.Aura? started = null;
        lock (conn.Monsters)
        {
            if (conn.Auras.Remove(skillId, out var old))
                ended = old;
            else if (conn.Auras.Values.FirstOrDefault(a => a.EffectId == effectId) is { } same)
            {
                // One aura of a kind at a time: a second gem of the same aura can't start it.
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {name}: refused, {same.Name} already runs (skill 0x{same.SkillId:x4})");
                return;
            }
            else
            {
                var aura = new AuraCatalog.Aura(skillId, effect, effectId, name, conn.AllocateBuff(),
                    AuraCatalog.BuffRow(effectId), reservation.Flat, reservation.Percent, level, quality, SourceBuff: conn.AllocateBuff(), ReserveLife: ReservesLife(conn));
                conn.Auras[skillId] = aura;
                int reserved = AuraCatalog.RequiredMana(conn.Pools, conn.Auras.Values);
                int lifeReserved = AuraCatalog.RequiredLife(conn.Pools, conn.Auras.Values);
                if (reserved > conn.Pools.Mana || lifeReserved >= conn.Pools.Life)
                {
                    conn.Auras.Remove(skillId);
                    Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {name}: cannot reserve {reserved} mana and {lifeReserved} life");
                    return;
                }
                started = aura;
                conn.PlayerMana = (uint)Math.Min(conn.PlayerMana, (uint)(conn.Pools.Mana - reserved));
            }
            conn.Pools = PoolsFor(conn, conn.Pools.Level);
            conn.PlayerLife = Math.Min(conn.PlayerLife, UnreservedLife(conn));
            conn.ApplyMovementSpeed();
        }

        CharacterSaves.SetAuraActive(CharacterName(conn), effectId, started is not null);
        if (ended is not null)
        {
            foreach (var packet in AuraCatalog.EndPackets(ended, PlayerId))
                await Send(remote, stream, ciphers, packet,
                    $"0x01c2 player aura {ended.Name} ends");
        }
        if (started is not null)
        {
            foreach (var packet in AuraBuffs(started))
                await Send(remote, stream, ciphers, packet,
                    $"0x01c1 player aura {started.Name} (BuffDefinitions row {started.BuffRow})");
        }
        await SendCharacterStats(remote, stream, ciphers, conn, started is not null ? $"aura {name} on" : $"aura {ended!.Name} off");
        await SendPlayerMana(remote, stream, ciphers, conn,
            $"0x01b1 player mana {conn.PlayerMana}/{conn.Pools.Mana}, {ReservedMana(conn)} reserved");
        await Send(remote, stream, ciphers, PlayerLifeUpdate(conn), $"0x01b1 player life {conn.PlayerLife}, {ReservedLife(conn)} reserved");
    }

    static IEnumerable<byte[]> AuraBuffs(AuraCatalog.Aura aura) =>
        AuraCatalog.StartPackets(aura, PlayerId, Skills.ActionHash(aura.Effect));

    // Experimental: a projectile gem skill's projectile. After the cast time the server creates the Projectiles.dat object
    // named after the effect (Fireball → Metadata/Projectiles/Fireball) just ahead of the caster, wakes it and moves it to the
    // aimed point and on to its range with 0x1ba at the row's ProjectileSpeed (world units per second, 250/23 per grid
    // cell), stopping at the first wall or monster on its path; a monster takes the skill's damage packet
    // (SkillCatalog.HitDamage, after conversion). It explodes there; then the object is removed.
    static async Task LaunchProjectile(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (int X, int Y) from, (int X, int Y) to, uint targetId = 0, ushort sequence = 0,
        int? actionToken = null, CooldownUseTicket? cooldownUse = null)
    {
        int action = actionToken ?? cooldownUse?.ActionToken ?? conn.ActionCount;
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is null) { Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4}: no gem skill"); return; }
        var supportedAtRelease = SupportedSkill(conn, skillId);
        gem.Level = supportedAtRelease.Level;
        gem.Quality = supportedAtRelease.Quality;
        if (Skills.ProjectileFor(gem.Effect) is not { } projectile)
        {
            // Not a projectile skill (strikes, slams, areas): its hit lands on the monsters around the aimed point.
            await AreaSkillHit(remote, stream, ciphers, conn, skillId, gem, from, to, sequence: sequence,
                actionToken: actionToken, cooldownUse: cooldownUse);
            return;
        }
        var projectileHitPlan = SkillHitPlan.ForSkill(Skills, gem.Effect, gem.Level, gem.Quality);
        if (!projectileHitPlan.CanHit) return;
        int epoch;
        lock (conn.Monsters) epoch = cooldownUse?.ProjectileEpoch ?? conn.ProjectileEpoch;
        // Released at the action's contact keyframe. An attack gem (Burning Arrow, Caustic Arrow …) plays the weapon's
        // attack1 animation, which the client stretches to the weapon's attack time: its contact is attack1_bow's 399/1066
        // (0.374 for the Ranger) of that time. A spell plays its spell_projectile clip instead (Dex: 699/1133 = 0.617 of
        // the cast). Using the spell fraction for a bow sent the arrow 0.617 of the action after the swing had already
        // lowered the bow (live 2026-10-25: the client's create arrived ~600 ms after 0x1ab, while attack1_bow's contact is
        // ~0.37 of it).
        var cls = Skills.ClassFor(conn.ClassRow);
        bool attack = gem.Name is not null && Skills.IsAttack(gem.Effect);
        int? castDuration = attack ? 0 : CastMs(conn, gem.Effect, gem.Level, gem.Quality, skillId);
        if (castDuration is null) return;
        double releaseDelay = attack
            ? AttackContactDelay(conn, SupportedAttackSeconds(conn, skillId), approach: false)
            : castDuration.Value / 1000.0 * Animations.SpellRelease(cls.AnimatedObject);
        if (!double.IsFinite(releaseDelay)) return;
        if (!await WaitProjectileTime(conn, conn.Clock.Elapsed.TotalSeconds + Math.Max(0.001, releaseDelay), epoch)
            || (cooldownUse?.Instant != true && !PlayerActionCurrent(conn, action)) || cooldownUse?.Current(conn) == false)
        { Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} cancelled before release"); return; }
        // A worn skill effect (its skill_art_variation) flies its own projectile and explodes in its own art (SkillArt).
        int variation = SkillArtVariation(conn, gem.Gem);
        projectile = (Art.Projectile(projectile.Id, variation), projectile.Speed);
        // The fireball explosion is only right for the Fireball spell; a bow's arrow (Burning Arrow, Caustic Arrow …)
        // sticks and disappears on hit, so it gets no impact. Its burning/debuff is the monster's, not the arrow's.
        string impact = projectile.Id.Contains("Fireball")
            ? Art.AnimatedInPlaceOf(DefaultProjectileImpactAo, variation)
            : "none";
        // Keep the player's final speed separate from the skill's raw stats: the panel already includes the player's
        // base speed, while a gem's own base speed (including quality) adds to that total instead of replacing it.
        var playerSpeedStats = CharacterPlayerStats(conn);
        var skillSpeedStats = SupportedSkill(conn, skillId).Stats;
        int PlayerSpeedStat(string name) => Stats.Key(name) is uint key ? playerSpeedStats.GetValueOrDefault(key) : 0;
        int SkillSpeedStat(string name) => Stats.Key(name) is uint key ? skillSpeedStats.GetValueOrDefault(key) : 0;
        float speed = ProjectileSpeed.CellsPerSecond(projectile.Speed, PlayerSpeedStat, SkillSpeedStat,
            Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED"));
        // Fireball's quality changes the actual explosion targets as well as its client skill radius.
        int impactRadius = Skills.EffectId(gem.Effect) == "Fireball"
            ? SupportedRadius(conn, skillId, spell: true) : 0;
        var chargeUse = new ChargeUse();
        Dictionary<uint, int>? impactPlayer = null, impactStats = null, impactOwn = null;
        SupportedGem? impactGem = null;
        DamagePacket? impactPacket = null;
        int impactChance = 0, impactMultiplier = 100;
        await FlyProjectile(remote, stream, ciphers, conn, (projectile.Id, speed), from, to, impact: impact, impactRadius: impactRadius, damageTo: m =>
        {
            // The player's stats (passives, items, auras) under the skill's own, as GemSkills sends them. Keep the hit's
            // types separate so conversion and per-type mitigation apply. An attack gem (Burning Arrow, Caustic Arrow …) has
            // no spell damage of its own: its hit is the weapon's, scaled by the skill's damage effectiveness, with the
            // attack's crit — the same split AreaSkillHit makes for the non-projectile gems.
            // One impact/explosion has one player snapshot: a charge gained from an earlier target cannot strengthen
            // another target of the same committed contact while the explosion is still being resolved.
            var player = impactPlayer ??= FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2);
            if (impactPacket is null)
            {
                impactGem = SupportedSkill(conn, skillId);
                impactOwn = impactGem.Stats;
                var characterInputs = CharacterPlayerStats(conn);
                impactPacket = SupportedHit(conn, skillId, characterInputs, impactGem);
                impactStats = SupportedStatsFor(conn, skillId, player);
                impactChance = SupportedCriticalChance(conn, skillId, player, impactGem);
                impactMultiplier = SupportedCriticalMultiplier(conn, skillId, impactGem);
            }
            var own = impactOwn!;
            var stats = impactStats!;
            bool spell = projectileHitPlan.Kind == SkillHitKind.Spell;
            if (!SkillRestrictions.From(name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0)
                .DealsDamage(spell)) return (0, false);
            var rolls = GamblerFor(stats);
            if (MonsterBlocks(m, Random.Shared, spell)) return (Blocked, false);
            var hit = rolls.RollOutgoingHit(impactPacket, Random.Shared);
            bool critical = rolls.IsCritical(impactChance, Random.Shared, OnLowLife(conn));
            if (critical) hit = hit.Critical(CriticalMultiplierOn(m, impactMultiplier));
            hit = ApplyPassiveDamageTaken(conn, m, ApplyMonsterModDamageTaken(m,
                rolls.MitigateOutgoingHit(hit,
                    HitResistancesFor(conn, m, player, own, critical, attack: !spell), Random.Shared), spell));
            int damage = (int)Math.Round(hit.MinTotal);
            GainRageOnHit(conn, stats, attack: !spell, melee: Skills.IsMelee(gem.Effect), critical);
            return (damage, critical);
        }, epoch: epoch, actionToken: cooldownUse is null || cooldownUse.Instant ? null : action,
            onApplied: (target, applied, critical) => ChargeHitCommitted(conn, target, applied, critical,
                projectileHitPlan.Kind == SkillHitKind.Spell, MeleeAttack(conn, gem.Effect), chargeUse,
                impactPlayer!, Skills.EffectId(gem.Effect), impactOwn, epoch));
    }

    // The radius in cells of a gem skill's hit around its aimed point (AreaSkillHit): its own
    // active_skill_base_area_of_effect_radius, else Cyclone's 16 or the generic 12, grown by the player's area stats
    // (SkillArea). The radius stat is in cells (Righteous Fire's 18 is its 1.8 m, verified against the real game 2026-10-23).
    static int SkillRadius(ClientState conn, bool spell, int effect, int level, int quality)
    {
        var areaStats = Skills.EffectStatsFor(effect, level, quality,
            FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2));
        int Get(string name) => Stats.Key(name) is uint k ? areaStats.GetValueOrDefault(k) : 0;
        return SkillArea.Radius(SkillArea.BaseRadius(Skills.EffectId(effect), Get), Get, spell, combined: true);
    }

    static int SupportedRadius(ClientState conn, ushort skillId, bool spell)
    {
        var gem = SupportedSkill(conn, skillId);
        var stats = SupportedStatsFor(conn, skillId, FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2));
        int Get(string name) => Stats.Key(name) is uint k ? stats.GetValueOrDefault(k) : 0;
        return SkillArea.Radius(SkillArea.BaseRadius(Skills.EffectId(gem.Effect), Get), Get, spell, combined: true);
    }

    // Whether a living monster stands within a gem attack's hit radius of a ground point. The client sends a point with a
    // monster on it as C→S 0x25 (not C→S 0x24 on the object), so an attack-move at such a point has to become a swing.
    static bool MonsterAtAim(ClientState conn, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        int x, int y, double at)
    {
        int radius = SupportedRadius(conn, gem.Id, spell: false);
        lock (conn.Monsters)
            return conn.Monsters.Any(m => !m.Value.Dead && m.Value.Life > 0 && !m.Value.Owned
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
        var supportedAtContact = SupportedSkill(conn, skillId);
        gem.Level = supportedAtContact.Level;
        gem.Quality = supportedAtContact.Quality;
        var skillStats = SupportedSkill(conn, skillId).Stats;
        // An attack gem's share of the weapon's damage ("Deals 77.3% of base Attack Damage"): the hit below uses it.
        int effectiveness = Skills.DamageEffectiveness(gem.Effect, gem.Level);
        int Stat(string name) => Stats.Key(name) is uint k ? skillStats.GetValueOrDefault(k) : 0;
        if (Stat("base_deal_no_damage") != 0) return;
        var hitPlan = SkillHitPlan.ForSkill(Skills, gem.Effect, gem.Level, gem.Quality);
        if (!hitPlan.CanHit) return;
        bool spell = hitPlan.Kind == SkillHitKind.Spell;
        var weapon = WeaponFor(conn);
        int? castDuration = spell ? CastMs(conn, gem.Effect, gem.Level, gem.Quality, skillId) : 0;
        if (castDuration is null) return;
        double delay;
        // An attack lands at its contact keyframe; the player swings where it stands, so without the walk-in offset.
        lock (conn.Monsters) delay = spell ? castDuration.Value / 1000.0
            : AttackContactDelay(conn, SupportedAttackSeconds(conn, skillId), approach: false);
        if (!double.IsFinite(delay)) return;
        int radius = SupportedRadius(conn, skillId, spell);
        // A melee skill aimed far away still lands in front of the player.
        var centre = !spell && Dist(from, to) > radius + weapon.Range ? from : to;
        int action = actionToken ?? cooldownUse?.ActionToken ?? conn.ActionCount;
        int epoch;
        double contactAt;
        lock (conn.Monsters)
        {
            epoch = cooldownUse?.ProjectileEpoch ?? conn.ProjectileEpoch;
            contactAt = conn.Clock.Elapsed.TotalSeconds + (immediate ? 0 : Math.Max(0.05, delay));
        }
        // A channel's hits (Cyclone, C→S 0x197) land at once, where the player is.
        var appliedHits = new List<(uint Id, SpawnedMonster Target, MonsterHit Hit, int Damage, bool Critical)>();
        var chargeUse = new ChargeUse();
        while (true)
        {
            if (!await WaitProjectileTime(conn, contactAt, epoch)) return;
            lock (conn.Monsters)
            {
                if (!conn.ProjectileActive(epoch) || conn.PlayerLife == 0
                    || cooldownUse?.Instant != true && conn.ActionCount != action || !Current()) return;
                if (conn.Paused) continue;
                double now = conn.Clock.Elapsed.TotalSeconds;
                var targets = conn.Monsters.Where(e => !e.Value.Dead && e.Value.Life > 0 && !e.Value.Owned
                    && Dist(e.Value.Walk.Position(now), centre) <= radius).ToArray();
                var player = FinalPlayerStats(conn).ToDictionary(kv => kv.Item1, kv => kv.Item2);
                var supported = SupportedSkill(conn, skillId);
                skillStats = supported.Stats;
                var characterInputs = CharacterPlayerStats(conn);
                int criticalChance = SupportedCriticalChance(conn, skillId, player, supported);
                int criticalMultiplier = SupportedCriticalMultiplier(conn, skillId, supported);
                var packet = SupportedHit(conn, skillId, characterInputs, supported);
                var stats = SupportedStatsFor(conn, skillId, player);
                if (!SkillRestrictions.From(name => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0)
                    .DealsDamage(spell)) return;
                DateTimeOffset hitAt = DateTimeOffset.UtcNow;
                // One area contact commits all its targets before any notification waits on the network. A new action
                // can cancel an unreleased effect, but cannot cancel half of an already committed explosion.
                foreach (var (id, m) in targets)
                {
                    if (MonsterBlocks(m, Random.Shared, spell)) continue;
                    var rolls = GamblerFor(stats);
                    var hit = rolls.RollOutgoingHit(packet, Random.Shared);
                    bool critical = rolls.IsCritical(criticalChance, Random.Shared, OnLowLife(conn));
                    if (critical) hit = hit.Critical(CriticalMultiplierOn(m, criticalMultiplier));
                    hit = ApplyPassiveDamageTaken(conn, m, ApplyMonsterModDamageTaken(m,
                        rolls.MitigateOutgoingHit(hit,
                            HitResistancesFor(conn, m, player, skillStats, critical, attack: !spell), Random.Shared), spell));
                    int damage = (int)Math.Round(hit.MinTotal);
                    if (ApplyMonsterHitLocked(conn, m, damage) is not { } applied) continue;
                    ChargeHitCommitted(conn, m, applied, critical, spell, MeleeAttack(conn, gem.Effect), chargeUse,
                        player, Skills.EffectId(gem.Effect), skillStats, epoch);
                    if (critical) ReserveWolfLocked(conn, hitAt);
                    GainRageOnHit(conn, stats, attack: !spell, melee: Skills.IsMelee(gem.Effect), critical);
                    appliedHits.Add((id, m, applied, damage, critical));
                }
                break;
            }
        }
        foreach (var applied in appliedHits)
            await NotifyMonsterHit(remote, stream, ciphers, conn, applied.Id, applied.Target, applied.Hit, applied.Damage,
                $"{gem.Name}{(applied.Critical ? " (critical)" : "")}", melee: !spell, sequence: sequence, critical: applied.Critical);
        Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: {(spell ? "spell" : "attack")} hit {appliedHits.Count} monster(s) within {radius} cells of ({centre.X}, {centre.Y}) (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
    }

    // A projectile's flight (LaunchProjectile): created just ahead of from, aimed at to, it flies on
    // to its range and stops at the first wall or monster on its path. With an impact radius it damages every hostile
    // monster in that explosion once, including the collision target; otherwise it retains the single-target hit.
    internal static async Task FlyProjectile(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        (string Id, float CellsPerSecond) projectile, (int X, int Y) from, (int X, int Y) to, Func<SpawnedMonster, (int Damage, bool Critical)>? damageTo,
        int epoch, bool log = true, string? impact = null, int impactRadius = 0, int? actionToken = null,
        Action<SpawnedMonster, MonsterHit, bool>? onApplied = null, OrdinaryBowShot? ordinaryShot = null)
    {
        uint id;
        lock (conn.Monsters) id = conn.NextObjectId++;
        bool Active() => conn.ProjectileActive(epoch);
        if (!Active()) return;
        async Task<bool> SendVisual(byte[] bytes, string? line, Action? commit = null)
        {
            bool sent = false;
            while (!sent && Active())
            {
                await Send(remote, stream, ciphers, bytes, line,
                    still: () => sent = conn.TryCommitProjectileVisual(epoch, commit));
                if (!sent) await Task.Delay(16);
            }
            return sent;
        }
        double dx = to.X - from.X, dy = to.Y - from.Y, aim = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // One launch-time speed drives collision sweeps, the client's glide duration and the server's flight clock.
        float cellsPerSecond = projectile.CellsPerSecond;
        if (!float.IsFinite(cellsPerSecond) || cellsPerSecond < 0) return;
        // It leaves a little ahead of the caster (Game_PROJECTILE_AHEAD cells, default 1.5) and flies toward the clicked
        // point or monster and on to Game_PROJECTILE_RANGE cells (default 120, ~1.2 s at the real speed), clamped to the
        // area. The Positioned x/y are whole grid cells, so an offset under one cell rounds back onto the player's own
        // cell: the arrow then spawns inside the character and only becomes visible after it has flown out of the body.
        double ahead = double.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_AHEAD"), inv, out var pa) ? pa : 1.5;
        double range = double.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_RANGE"), inv, out var pr) && pr > 0 ? pr : 120;
        ahead = Math.Min(ahead, aim - 1);
        from = ((int)Math.Round(from.X + dx / aim * ahead), (int)Math.Round(from.Y + dy / aim * ahead));
        double length = Math.Max(range - ahead, 1);
        if (TerrainHeights.Size(conn.Layout.Id, conn.Seed) is (int w, int h))
            foreach (var (d, lo, hi) in new[] { (dx, -from.X, w - 1 - from.X), (dy, -from.Y, h - 1 - from.Y) })
                if (d != 0) length = Math.Min(length, Math.Max(0, (d > 0 ? hi : lo) / (d / aim)));
        cellsPerSecond = ProjectileSpeed.ForFlightDistance(cellsPerSecond, length + 1);
        // Static walls cap the route; monsters are swept from observed positions during flight, not selected at launch.
        double hitRadius = double.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_HIT_RADIUS"), inv, out var phr) && phr > 0 ? phr : 4;
        to = ((int)Math.Round(from.X + dx / aim * length), (int)Math.Round(from.Y + dy / aim * length));
        bool hitWall;
        (to, hitWall) = ProjectileFlight.ClipWall(from, to,
            (x, y) => ProjectileGrid.Blocks(conn.Layout.Id, conn.Seed, x, y) == true);
        dx = to.X - from.X; dy = to.Y - from.Y;
        double cells = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        // Positioned rotation = atan2(dx, -dy) (the player's own moves, traced live 2026-10-18: facing (+15, -18) read 0.668)
        // Height: the captured terrain height plus Game_PROJECTILE_HEIGHT steps of 7.8125 world units (default 5, the
        // height the live server's arrow flew at: i32 -5, f32 +39.0625). The protocol carries no player z (C→S
        // 0x25/0x24 have only x,y, and the player's Positioned follows the terrain), so natural ground is the supported
        // case; without a capture the bare offset is used. A platform/doodad is not in the terrain grid (the hideout's
        // waypoint platform read z -78.37 while the terrain under it was 0..2 steps); that needs a server-side
        // world/surface height model (issue #182).
        int up = int.TryParse(Environment.GetEnvironmentVariable("Game_PROJECTILE_HEIGHT"), out int u) ? u : 5;
        int startHeight = (TerrainHeights.At(conn.Layout.Id, conn.Seed, from.X, from.Y) ?? 0) + up;
        // Positioned's marker and 0x1ba's endpoint are -worldZ / 7.8125, whereas ProjectileObject.Height
        // takes worldZ / 7.8125. A mismatched glide endpoint makes the native interpolation drift vertically.
        var height = new ProjectileHeight(startHeight);
        var obj = new ProjectileObject(projectile.Id, from.X, from.Y, (float)Math.Atan2(dx, -dy), Height: height.ObjectHeight);
        uint type = InstanceEntities.ObjectTypeHash(projectile.Id);
        // All observations below are taken with the monster lock held, including the initial observation when the
        // glide reaches the send gate. The game clock, not time spent waiting for the gate, starts the flight.
        List<ProjectileFlight.Target> Targets(double at) => [.. conn.Monsters
            .Where(m => !m.Value.Dead && m.Value.Life > 0 && !m.Value.Owned && (impactRadius <= 0 || m.Value.Hostile))
            .Select(m => new ProjectileFlight.Target(m.Key, m.Value.Walk.Position(at), m.Value.Walk))];
        ProjectileFlight? flight = null;
        bool released = false;
        try
        {
            while (!released && Active() && (actionToken is not int original || OrdinaryBowReleaseCurrent(conn, original, epoch)))
            {
                await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, type, obj.Blob(), headerU32: 0xffffffff), !log ? null :
                    $"0x02b9 create projectile 0x{id:x} {projectile.Id} type=0x{type:x8} at ({from.X}, {from.Y})", still: () =>
                    {
                        lock (conn.Monsters)
                        {
                            if (!Active() || (actionToken is int token && (!OrdinaryBowReleaseCurrent(conn, token, epoch) || conn.Paused))) return false;
                            conn.ProjectileObjects.Add(id);
                            released = true;
                            return true;
                        }
                    });
                if (!released) await Task.Delay(16);
            }
            if (!released) return;
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), !log ? null :
                $"0x01a9 projectile 0x{id:x} enters wake zone", still: Active);
            await Send(remote, stream, ciphers, height.MoveTo(id, to.X, to.Y, (float)(cells / cellsPerSecond)), !log ? null :
                $"0x01ba projectile 0x{id:x} to ({to.X}, {to.Y}), {cells:0.0} cells at {cellsPerSecond:0.0} cells/s, height marker {height.Marker} (world z {height.WorldZ})" +
                (hitWall ? ", route ends at a wall" : ""), still: () =>
                {
                    lock (conn.Monsters)
                    {
                        if (!Active()) return false;
                        double at = conn.Clock.Elapsed.TotalSeconds;
                        flight = new(from, to, cellsPerSecond, at, hitRadius, hitWall, Targets(at));
                        return true;
                    }
                });
            if (flight is null) return;
            ProjectileFlight.Step step = default;
            bool committed = false;
            SpawnedMonster? ordinaryTarget = null;
            MonsterHit? ordinaryHit = null;
            int ordinaryDamage = 0;
            bool ordinaryCritical = false;
            while (true)
            {
                await Task.Delay(16);
                if (ordinaryShot is not null)
                {
                    // Accuracy and the accepted contact's impale/life effects share the actual relocation gate.
                    // A queued sample invalidated by pause/transfer consumes no RNG or target state. A miss can
                    // accept a later contact in this same sweep, without an intermediate packet or melee hit.
                    await Send(remote, stream, ciphers, [], null, atGate: () =>
                    {
                        lock (conn.Monsters)
                        {
                            if (!Active() || conn.Paused) return null;
                            double now = conn.Clock.Elapsed.TotalSeconds;
                            bool Accept(uint targetId) => conn.Monsters.TryGetValue(targetId, out var monster)
                                && !monster.Dead && monster.Life > 0
                                && ordinaryShot.Hits(conn.FlaskGrounds.Evasion(targetId,
                                    monster.Mods.Effects.Evasion(Monsters.BaseEvasion(monster.Level) ?? 0)), Random.Shared,
                                    OrdinaryBowAccuracyAt(conn, monster, ordinaryShot, now));
                            step = flight.Advance(now, Targets(now), Accept);
                            if (!step.Finished || (step.TargetId == 0 && !step.Wall)) return null;
                            if (step.TargetId != 0 && conn.Monsters.TryGetValue(step.TargetId, out ordinaryTarget)
                                && !ordinaryTarget.Dead && ordinaryTarget.Life > 0)
                            {
                                (ordinaryHit, ordinaryDamage, ordinaryCritical) =
                                    CommitOrdinaryBowHitLocked(conn, ordinaryTarget, ordinaryShot, Random.Shared, epoch);
                            }
                            committed = true;
                            int x = (int)Math.Round(step.Position.X), y = (int)Math.Round(step.Position.Y);
                            if (log) Console.WriteLine($"[gateway] {remote} => arrow 0x{id:x} impact at ({x}, {y}) on {(step.Wall ? "wall" : $"monster 0x{step.TargetId:x}")} (t={step.At:0.000})");
                            return height.StopAt(id, x, y);
                        }
                    });
                    if (!Active()) return;
                    if (step.Finished) break;
                    continue;
                }
                lock (conn.Monsters)
                {
                    if (!Active()) return;
                    if (conn.Paused) continue;
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    step = flight.Advance(now, Targets(now));
                }
                if (step.Finished) break;
            }
            if (step.TargetId != 0 || step.Wall)
            {
                int x = (int)Math.Round(step.Position.X), y = (int)Math.Round(step.Position.Y);
                // 0x1b7 copies the fixed pair directly: retain the same marker/world Z as creation, while
                // 0x1ba takes the signed world-Z steps. Do not resample terrain and jump the impact vertically.
                var appliedHits = new List<(uint Id, SpawnedMonster Target, MonsterHit Hit, int Damage, bool Critical)>();
                if (ordinaryShot is not null && ordinaryHit is { } appliedOrdinary && ordinaryTarget is not null)
                    appliedHits.Add((step.TargetId, ordinaryTarget, appliedOrdinary, ordinaryDamage, ordinaryCritical));
                while (!committed && Active())
                {
                    // 0x1b7 relocates and cancels the existing glide (0x141e31200 -> 0x141e35250). Commit the hit
                    // under the same gate and short monster lock; a pause/transfer while waiting defers/cancels it.
                    await Send(remote, stream, ciphers, height.StopAt(id, x, y), !log ? null :
                        $"0x01b7 projectile 0x{id:x} impact at ({x}, {y}) on {(step.Wall ? "wall" : $"monster 0x{step.TargetId:x}")} (t={step.At:0.000})", still: () =>
                        {
                            lock (conn.Monsters)
                            {
                                if (!Active() || conn.Paused) return false;
                                if (damageTo is not null)
                                {
                                    IReadOnlyList<(uint Id, SpawnedMonster Monster)> targets = impactRadius > 0
                                        ? ProjectileImpactTargets.Explosion(conn.Monsters, (x, y), impactRadius, step.At)
                                        : step.TargetId != 0 && conn.Monsters.GetValueOrDefault(step.TargetId) is { Dead: false, Life: > 0 } direct
                                            ? [(step.TargetId, direct)] : [];
                                    DateTimeOffset hitAt = DateTimeOffset.UtcNow;
                                    foreach (var (monsterId, target) in targets)
                                    {
                                        var (damage, critical) = damageTo(target);
                                        if (damage != Blocked && ApplyMonsterHitLocked(conn, target, damage) is { } hit)
                                        {
                                            onApplied?.Invoke(target, hit, critical);
                                            if (critical) ReserveWolfLocked(conn, hitAt);
                                            appliedHits.Add((monsterId, target, hit, damage, critical));
                                        }
                                    }
                                }
                                committed = true;
                                return true;
                            }
                        });
                    if (!committed) await Task.Delay(16);
                }
                if (!committed) return;
                foreach (var (monsterId, target, applied, damage, critical) in appliedHits)
                    await NotifyMonsterHit(remote, stream, ciphers, conn, monsterId, target, applied, damage,
                        $"{projectile.Id}{(critical ? " (critical)" : "")}", log, critical: critical);
                if (log && impactRadius > 0)
                    Console.WriteLine($"[gateway] {remote} Fireball impact at ({x}, {y}), radius {impactRadius}: {appliedHits.Count} hostile target(s)");
                impact = Environment.GetEnvironmentVariable("Game_PROJECTILE_IMPACT_AO") ?? impact ?? DefaultProjectileImpactAo;
                if (damageTo is not null && impact != "none")
                {
                    if (!await SendVisual(InstanceEntities.AnimatedModel(id, ProjectileObject.AnimatedComponentIndex, impact),
                        $"0x02ba projectile 0x{id:x} explodes ({impact})")) return;
                    if (!await WaitProjectileTime(conn, conn.Clock.Elapsed.TotalSeconds + 1.5, epoch)) return;
                }
            }
            await SendVisual(InstanceProtocol.RemoveObject(id), $"0x02bb remove projectile 0x{id:x}",
                () => conn.ProjectileObjects.Remove(id));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } // The old area's connection has gone.
    }

    // Release and terminal visual lifetimes share the game clock and area epoch with an active flight.
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

    // A hit on a living monster: life is mutated once under Monsters, then notifications await outside the lock.
    static async Task HitMonster(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m,
        int damage, string by, bool log = true, bool melee = false, ushort sequence = 0, Action? onApplied = null,
        bool critical = false, DateTimeOffset? hitAt = null)
    {
        MonsterHit? hit;
        lock (conn.Monsters)
        {
            hit = ApplyMonsterHitLocked(conn, m, damage);
            if (hit is not null && critical) ReserveWolfLocked(conn, hitAt ?? DateTimeOffset.UtcNow);
            if (hit is not null) onApplied?.Invoke();
        }
        if (hit is { } applied) await NotifyMonsterHit(remote, stream, ciphers, conn, id, m, applied, damage, by, log, melee, sequence, critical: critical);
    }

    // The short state mutation is shared with projectile impact; no awaits under Monsters.
    static MonsterHit? ApplyMonsterHitLocked(ClientState conn, SpawnedMonster m, int damage, bool playerHit = true)
    {
        if (m.Dead || m.Life == 0 || m.Companion is not null) return null;
        (m.Life, bool starts) = TakeDamageLocked(m, damage);
        if (playerHit) conn.LastEnemyHitAt = conn.Clock.Elapsed.TotalSeconds;
        if (m.Life == 0) { m.Aggro = false; m.Dead = true; }
        return new(m.Life, starts);
    }

    static async Task NotifyMonsterHit(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m,
        MonsterHit hit, int damage, string by, bool log = true, bool melee = false, ushort sequence = 0,
        bool playerKill = true, bool critical = false)
    {
        if (playerKill && critical) _ = GainFlaskChargesOnCrit(remote, stream, ciphers, conn);
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
        await SendMonsterLife(remote, stream, ciphers, conn, id, m,
            !log && life > 0 ? null : $"0x01b1 monster 0x{id:x} hit by {by} for {damage}, life {life}/{m.MaxLife}{(life == 0 ? " (dies)" : "")}");
        if (life == 0) await MonsterKilled(remote, stream, ciphers, conn, id, m, playerKill);
        if (playerKill && critical) await RefreshSummons(remote, stream, ciphers, conn);
        if (stageStarts) _ = RunBossStage(remote, stream, ciphers, conn, id, m);
    }

    // Snapshot at the send gate, after earlier notifications finish: simultaneous minion hits must never make the
    // native pool move backwards to an older hit's life. The same rule covers a boss refill racing a queued hit.
    static Task SendMonsterLife(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        uint id, SpawnedMonster monster, string? log)
    {
        byte[] packet = InstanceProtocol.LifeUpdate(id, 0);
        return Send(remote, stream, ciphers, packet, log, still: () =>
        {
            lock (conn.Monsters)
            {
                if (conn.Closed || conn.LeavingArea || !conn.Monsters.TryGetValue(id, out var current)
                    || !ReferenceEquals(current, monster)) return false;
                InstanceProtocol.LifeUpdate(id, monster.Life).CopyTo(packet, 0);
                return true;
            }
        });
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

    // Native TakeHit owns the client's predictive cancellation and recovery lockout. Commit the matching server state
    // inside the send gate, so death or another recovery while this packet waits cannot send an obsolete flinch.
    static async Task SendPlayerHitRecovery(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        int damage)
    {
        if (damage <= 0) return;
        var stats = CharacterPlayerStats(conn);
        if (stats.Any(s => s.Value != 0 && PlayerHitRecovery.UnsupportedStat(Stats.Name(s.Key)))) return;
        // Skill immunity can depend on a native action's contact phase, which this server does not track yet.
        // Conservatively skip builds with any equipped skill carrying it, rather than stun an uninterruptible phase.
        if (GemSkillEffects(conn).Any(g => Skills.EffectStats(g.Effect, g.Level, g.Quality)
            .Any(s => s.Item2 != 0 && Stats.Name(s.Item1).StartsWith("skill_cannot_be_stunned", StringComparison.Ordinal)))) return;
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        if (PlayerHasEnergyShieldForRecovery(conn, Get)) return;
        double chance = PlayerHitRecovery.Chance(damage, conn.PlayerMaxLife, Get, conn.EnergyShield.Current);
        float seconds = PlayerHitRecovery.Duration(Get);
        if (seconds <= 0 || Random.Shared.NextDouble() * 100 >= chance) return;
        await Send(remote, stream, ciphers,
            InstanceProtocol.ActorAnimation(PlayerId, seconds, PlayerHitRecovery.TakeHitAnimation),
            $"0x01d1 player TakeHit recovery {seconds:0.000} s ({chance:0.#}% chance, {damage} physical damage)",
            still: () =>
            {
                lock (conn.Monsters)
                {
                    if (conn.Closed || conn.PlayerLife == 0 || !conn.HitRecovery.Begin(conn.Clock.Elapsed.TotalSeconds, seconds)) return false;
                    Interlocked.Increment(ref conn.ActionCount);
                    conn.ChannelSkill = 0;
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    conn.NextAttackAt = now;
                    var at = conn.Walk.Position(now);
                    conn.Walk.Teleport(at.X, at.Y, now);
                    return true;
                }
            });
    }

    static bool PlayerActionCurrent(ClientState conn, int action)
    {
        lock (conn.Monsters)
            return !conn.Closed && !conn.LeavingArea && conn.PlayerLife > 0
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
        await SendMonsterLife(remote, stream, ciphers, conn, id, m, $"0x01b1 monster 0x{id:x} ({m.Label}) life refilled to {life}");
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
    // enough, it drops its items around where it fell (ItemDrops), and its corpse is removed 10 s later.
    static async Task MonsterKilled(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m,
        bool playerKill = true)
    {
        double now = conn.Clock.Elapsed.TotalSeconds;
        (int X, int Y) at, player;
        bool alive;
        lock (conn.Monsters) { at = m.Walk.Position(now); player = conn.Walk.Position(now); alive = conn.PlayerLife > 0; }
        if (alive && m.Hostile && playerKill)
        {
            var stats = CharacterPlayerStats(conn);
            int seconds = Stats.Key("gain_adrenaline_for_X_seconds_on_kill") is uint key ? stats.GetValueOrDefault(key) : 0;
            if (GrantAdrenaline(conn, seconds * 1000))
                await RefreshTemporaryPassives(remote, stream, ciphers, conn);
        }
        _ = Task.Delay(10_000).ContinueWith(async _ =>
        {
            bool removed;
            lock (conn.Monsters) removed = conn.Monsters.Remove(id);
            if (removed && !conn.Closed)
                await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove dead monster 0x{id:x}");
        });
        if (m.Variety is not { } variety || !m.Hostile) return;
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
