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

public static partial class Game
{
    // Record the first paid use of a channel before asynchronous output. Under conn.Monsters.
    public static int BeginPaidChannel(ClientState conn, ushort skill, int effect, int action, ushort sequence)
    {
        int generation = ++conn.ChannelGeneration;
        conn.ChannelSkill = skill;
        conn.ChannelSkillSequence = sequence;
        conn.LastChannelUpdateAt = conn.Clock.Elapsed.TotalSeconds;
        if (conn.LockstepClient) conn.LockstepSkill.Channelling(sequence);
        conn.PaidChannel = CombatLifetime is { IsActive: true } rules
            ? new(skill, effect, action, generation, rules) : null;
        return generation;
    }

    // Sample both moving actors at the hit time, after the approach and contact delay.
    public static (int Accuracy, bool Unique, bool CloseRange) AttackAccuracyAt(
        ClientState conn, SpawnedMonster target, double at)
    {
        bool unique = target.Rarity == MonsterRarity.Unique;
        bool close = TargetAccuracy.InCloseRange(Dist(conn.Walk.Position(at), target.Walk.Position(at)));
        return (TargetAccuracy.AgainstTarget(PlayerAccuracy(conn), conn.Pools.AccuracyMoreVsUniquePct,
            conn.Pools.AccuracyMoreInCloseRangePct, unique, close), unique, close);
    }

    // Both accuracy checks use the same target context. Only the first check consumes the defender's entropy.
    public static int AttackHitChanceAt(ClientState conn, uint id, SpawnedMonster target, double at,
        AttackAccuracyProfile profile, IReadOnlyDictionary<uint, int>? player = null)
    {
        int evasion = target.Effects.Evasion(Monsters.BaseEvasion(target.Level) ?? 0);
        evasion = MonsterBlindRating(conn, id, target, evasion, at, player);
        bool close = TargetAccuracy.InCloseRange(Dist(conn.Walk.Position(at), target.Walk.Position(at)));
        return profile.Chance(evasion, target.Rarity == MonsterRarity.Unique, close);
    }

    // A ground ranged shot reserves pacing, its action token and stationary walk together with the input-time gate.
    public static bool TryBeginRangedAttack(ClientState conn, double requestedAt, double duration,
        ushort skill, ushort sequence, out (int Action, int X, int Y, SkillCriticalRoll CriticalRoll) started)
    {
        started = default;
        using (ProjectileMutation(conn))
        {
            double now = ProjectileWorldTime(conn);
            if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.Paused || conn.PlayerLife == 0
                || PlayerActionBlocked(conn, requestedAt, now)
                || !AttackTiming.Ready(now, conn.NextAttackAt, duration)) return false;
            requestedAt = Math.Max(requestedAt, conn.ProjectileRuntime?.Runtime.Now / 120000d ?? requestedAt);
            conn.NextAttackAt = AttackTiming.After(now, conn.NextAttackAt, duration);
            int action = Interlocked.Increment(ref conn.ActionCount);
            var (x, y) = conn.Walk.Position(requestedAt);
            conn.Walk.Order(x, y, requestedAt);
            conn.ChannelSkill = skill;
            conn.ChannelSkillSequence = sequence;
            started = (action, x, y, CaptureSkillCriticalRoll(conn, skill));
            return true;
        }
    }

    // Whether an attack may start now: the shared attack timer (the default attack and the attack gem skills) is over, or
    // the use is no more than a quarter of an attack early (the client's jitter). Does not reserve the timer: an attack used
    // on a monster waits for the walk into reach, and CastGemSkill reserves it when the swing starts.
    public static double SkillAttackSeconds(ClientState conn, int effect, int level) =>
        Math.Max(TickSeconds, SkillAttackTiming.ForSkill(SkillsFor(conn), effect, level, WeaponFor(conn),
            CharacterPlayerStats(conn)) / PlayerActionFactor(conn));

    public static bool AttackReady(ClientState conn, ushort skillId, int effect, int level, out double seconds)
    {
        // The supported skill's own speed includes its gem/support inputs; the generic fallback is for callers without
        // a skill id (ordinary weapon timing). Keep the gate and the interval CastGemSkill reserves on the same value.
        seconds = skillId != 0 ? SupportedAttackSeconds(conn, skillId) : SkillAttackSeconds(conn, effect, level);
        double now = conn.Clock.Elapsed.TotalSeconds;
        lock (conn.Monsters) return AttackTiming.Ready(now, conn.NextAttackAt, seconds);
    }

    // The 0x1af hit angle (0..255 = a full turn), the direction the hit comes from (attacker → target) in the client's
    // facing convention atan2(dx, −dy), like Positioned's rotation.
    public static byte HitAngle(int dx, int dy)
    {
        double a = Math.Atan2(dx, -dy);
        if (a < 0) a += 2 * Math.PI;
        return (byte)((int)Math.Round(a * 256.0 / (2 * Math.PI)) & 0xff);
    }

    // The 0x1af presentation type (code & 7; 3.29.3c table 0x1435d7520: 0 Physical, 1 Cold, 2 Fire, 3 Lightning,
    // 4 Chaos): the hit's largest damage component, Physical on a tie or an empty packet. One type, not the amounts.
    public static byte HitCode(GameServer.Core.World.DamagePacket packet)
    {
        var type = GameServer.Core.World.DamageType.Physical;
        double most = double.NegativeInfinity;
        foreach (var (candidate, range) in packet.Components)
            if (range.Min > most) { most = range.Min; type = candidate; }
        return type switch
        {
            GameServer.Core.World.DamageType.Cold => 1,
            GameServer.Core.World.DamageType.Fire => 2,
            GameServer.Core.World.DamageType.Lightning => 3,
            GameServer.Core.World.DamageType.Chaos => 4,
            _ => 0,
        };
    }

    // A positive offset on the hit's contact timing (seconds): the client starts its swing a beat after the server's 0x1ab
    // echo (the walk estimate leads the client's own walk, so the contact keyframe alone leaves the blood early). A fixed
    // offset is a compromise over the variable walk-sync; Game_HIT_OFFSET_MS overrides it (150 ms here covers most of the
    // walk-into-range error without overshooting a swing). A value that is not a finite number keeps the default.
    public static double HitContactOffsetSeconds() =>
        (double.TryParse(Environment.GetEnvironmentVariable("Game_HIT_OFFSET_MS"), System.Globalization.CultureInfo.InvariantCulture, out var hitOff)
            && double.IsFinite(hitOff) ? hitOff : 150) / 1000.0;

    // How much earlier than its contact an attack's server-created projectile is released. The client started the clip
    // before the server read the action, and the release then waits for a 30 Hz tick and the output queue: Frida
    // 2026-10-31 had Burning Arrow's object about 75 ms after the client's flash at the contact, the default arrow about
    // 50 ms after the contact. Game_RELEASE_LEAD_MS overrides it; a value that is not a finite number keeps the default.
    public static double AttackReleaseDelay(double contactDelay) => Math.Max(0, contactDelay
        - (double.TryParse(Environment.GetEnvironmentVariable("Game_RELEASE_LEAD_MS"), System.Globalization.CultureInfo.InvariantCulture, out var lead)
            && double.IsFinite(lead) ? lead : 60) / 1000.0);

    // The attack's contact time in seconds (AttackTiming.ContactDelay): the attack time scaled to the active weapon's attack1
    // contact keyframe, plus the walk-in offset for an attack that approached its target first, and always before the next
    // attack may start (which would cancel the hit).
    public static double AttackContactDelay(ClientState conn, double attackSeconds, bool approach)
    {
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject;
        double contact = Animations.AttackContact(ao, WeaponProfileFor(conn, activeSet: true));
        return AttackTiming.ContactDelay(attackSeconds, contact, approach ? HitContactOffsetSeconds() : 0);
    }

    // A gem melee attack's contact: the skill's own rig clip when the data has one (Ground Slam's groundslam_maul, 0.633
    // of the attack), else the class's attack1 keyframe (AttackContactDelay: 0.692 for the Str rig's attack1_maul, the
    // default attack's own swing). The clip is taken to stretch to the attack time, as the client plays it, so the
    // server's hit lands where the client's weapon visually hits. clip/fraction come back for the sweep's log.
    public static double GemAttackContactDelay(ClientState conn, int effect, double attackSeconds,
        out string? clip, out double fraction)
    {
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject;
        var weapon = WeaponProfileFor(conn, activeSet: true);
        if (SkillsFor(conn).EffectId(effect) is { } effectId
            && Animations.SkillAttackContact(ao, weapon, SkillClipName(effectId)) is { } found)
        {
            (clip, fraction) = (found.Name, found.Contact);
            return AttackTiming.ContactDelay(attackSeconds, found.Contact, 0);
        }
        clip = null;
        fraction = Animations.AttackContact(ao, weapon);
        return AttackTiming.ContactDelay(attackSeconds, fraction, 0);
    }

    // The rig clip a skill is named by: its GrantedEffects Id without separators ("GroundSlam" → "groundslam",
    // "Cleave" → "cleave"), which is how the rigs name groundslam_maul and cleave_maul.
    public static string SkillClipName(string effectId) =>
        new([.. effectId.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    public static (double Gap, bool Keyframe) GemAttackStrikeGap(ClientState conn, string? contactClip)
    {
        if (contactClip is not null)
        {
            string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject;
            var weapon = WeaponProfileFor(conn, activeSet: true);
            if (Animations.SkillAttackContactAt(ao, weapon, contactClip, 0) is { } first
                && Animations.SkillAttackContactAt(ao, weapon, contactClip, 1) is { } second && second.Contact > first.Contact)
                return (second.Contact - first.Contact, true);
        }
        return (0.5, false);
    }

    // PlayerActionCurrent's state half, without its action-token test: a gem melee swing keeps its hit when the player
    // only walks on (AreaSkillHit's holdsItsSwing), while a death, an area change, a retirement, a pause, a freeze or a
    // stun's hit recovery still ends it.
    public static bool PlayerStateAllowsAction(ClientState conn)
    {
        lock (conn.Monsters)
            return !conn.Closed && !conn.LeavingArea && !conn.RageRetired && conn.PlayerLife > 0 && !conn.Paused
                && conn.ElementalAilments.ActionFactor(conn.Clock.Elapsed.TotalSeconds) > 0
                && !conn.HitRecovery.Active(conn.Clock.Elapsed.TotalSeconds);
    }

    // The default attack's contact: its stance clip as the client plays it (DefaultAttackClip.ContactSeconds: a one-hand
    // sword's lands 0.56 s into a 0.645 s swing, not at the attack1 keyframe's 0.45). The walk-in compensation comes
    // from the actual approach result, rather than adding walk-in latency to a stationary swing; a lockstep client walks
    // up on the server's start as the estimate does (no lead to compensate), and the server paces its repeats, so no
    // early use cancels the hit.
    public static double DefaultAttackContactDelay(ClientState conn, double attackSeconds,
        (int X, int Y) from, (int X, int Y) stop, double departedAt, double arrivedAt, ushort sequence)
    {
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject;
        double contact = Animations.DefaultAttackContactSeconds(ao, WeaponProfileFor(conn, activeSet: true), sequence, attackSeconds) / attackSeconds;
        bool approach = !conn.LockstepClient && arrivedAt > departedAt && stop != from;
        return AttackTiming.ContactDelay(attackSeconds, contact, approach ? HitContactOffsetSeconds() : 0, early: !conn.LockstepClient);
    }

    // The player's predictive walk-up retry timer (0x141e95400: Actor +0x278 = 0.05 s for Player;
    // 0x141e957f0 retries the pending walk-up in mode 1 when it expires). The action has no pursuit deadline.
    // A lockstep walk-up runs on its frames (StepWalkUp).
    const double SwingFollowSeconds = .05;

    // The projectile grid value a ranged attack's line to its target must hold, the action's precision for the walk-up and
    // the swing (0x141e95400 → MoveTo +0x528): 3 for the bow's and the wand's default attack, 1 for a one-hand sword's
    // and for a Move (live 2026-11-03, the client's searches). Attack gems' are taken to be the bow's.
    const int RangedPrecision = 3;

    static int AttackPrecision(ClientState conn) =>
        OrdinaryRangedAttack.ProjectileId(WeaponProfileFor(conn, activeSet: true)) is not null ? RangedPrecision : 1;

    // The walk-up's own goal, as the client's action tests it before the swing: in reach, the monster's size (StopRange - 2,
    // its ObjectSize - 1) widening it, with the line to the monster's body on the projectile grid at the order's precision.
    static bool SwingInReach(ClientState conn, SpawnedMonster m, (int X, int Y) from, (int X, int Y) target, double reach) =>
        conn.Walk.InReachOf(from, target, (int)reach, m.StopRange - 2);

    // The walk-up's max range: the weapon's range less one, as the client's action gives its MoveTo (0x141e95400: max
    // range +0x168() − 1, min range +0x158() − 1, each from 1 when 0; tolerance +0x180, precision +0x170; live
    // 2026-11-03: max range 10 for the Rusted Sword's 11, 119 for a bow's 120), as a monster's chase walk-up takes it.
    // The swing's hit still tests the whole range (+ 2).
    static int WalkUpRange(SkillCatalog.Unarmed weapon) => Math.Max(weapon.Range, 1) - 1;

    // A predictive client's walk-up to a monster for a swing at it, from where the estimate has the player at `now`. Swing:
    // the swing starts at once, when MoveTo finds its goal where the player stands (0x141e81c20 → 0x141e81950). A walk-up
    // with steps swings once it is over at a cell in reach. A lockstep client's is PlaceLockstepAttack's.
    public static ((int X, int Y) Here, (int X, int Y) Stop, double At, bool Swing) WalkUpToSwing(ClientState conn,
        SpawnedMonster m, (int X, int Y) target, SkillCatalog.Unarmed weapon, double now, int precision)
    {
        int reach = WalkUpRange(weapon);
        var (here, stop, at) = conn.Walk.Approach(target.X, target.Y, reach, now, tolerance: m.StopRange - 2, precision);
        return (here, stop, at, SwingInReach(conn, m, here, target, reach) || at <= now && SwingInReach(conn, m, stop, target, reach));
    }

    // Walks up to a monster until it is in reach, whenever it moves or bodies cut the way: the client's action keeps going
    // until then, whatever the first path said, and its tick paths it again. A walk-up that swings at once (WalkUpToSwing's
    // Swing) returns at once. A lockstep client's walk-up is the step pass's (LockstepAttackStart), and this only waits for
    // its decision. Returns where the swing is swung from, when the walk-up got there and when the swing began (on a
    // lockstep client both the start of the frame it began in); null when the walk-up failed or another action replaced it.
    public static async Task<((int X, int Y) Stop, double Arrive, double Begun)?> FollowMonsterToSwing(ClientState conn,
        SpawnedMonster m, SkillCatalog.Unarmed weapon, Func<bool> current, Func<double, double, Task<bool>> until,
        (int X, int Y) stop, double arrive, bool swing, double now, int precision = 1, LockstepAttack? lockstep = null)
    {
        if (lockstep is not null)
        {
            // Woken by the step pass's decision, or every 16 ms to see whether another action took over.
            while (!lockstep.Decided.IsCompleted && current() && !conn.Closed && !conn.LeavingArea)
            {
                var woke = await Task.WhenAny(lockstep.Decided, AreaWait(PlayerUpdateRole.Player, 16));
                if (woke != lockstep.Decided) await woke;   // a stopped area's wait throws, as every AreaWait does
            }
            return lockstep.Decided.IsCompleted && await lockstep.Decided is { } decided ? (decided.From, decided.Start, decided.Start) : null;
        }
        int reach = WalkUpRange(weapon);
        if (swing) return (stop, arrive, now);
        while (true)
        {
            double next = arrive > now ? Math.Min(arrive, now + SwingFollowSeconds) : now + SwingFollowSeconds;
            if (!await until(next, now) || !current()) return null;
            using (ProjectileMutation(conn))
            {
                now = ProjectileWorldTime(conn);
                if (!current()) return null;
                var target = m.Walk.Position(now);
                bool done = m.Life == 0;
                if (now >= arrive && (done || SwingInReach(conn, m, stop, target, reach))) return (stop, arrive, now);
                if (done) continue;
                (_, stop, arrive, swing) = WalkUpToSwing(conn, m, target, weapon, now, precision);
                if (swing) return (stop, arrive, now);
            }
        }
    }

    // The player uses a skill on a monster (C→S 0x24, e.g. the default attack on a left click). The client walks into range
    // first, so the server walks its estimate too (WalkUpToSwing, reach = the weapon's MaxAttackDistance in cells less one,
    // centre to centre) and only then starts the swing: it echoes the action aimed at the monster (0x1ab, flags | 0x80 +
    // target id) and, at the contact point (AttackContactDelay: the attack animation's contact keyframe), lowers the monster's life (0x1b1) by the unarmed damage
    // (Characters.dat MinDamage..MaxDamage raised by the increased melee physical damage stats). Any later action (a move, another click) cancels both.
    // One attack per attack time (WeaponSpeed ms scaled by the player's attack speed stats, a quarter early allowed for
    // jitter); earlier repeats of 0x24 are ignored. At 0 the client's Life component runs its death; the corpse stays.
    public static async Task Attack(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        InstanceTransfer.UseSkillOnObject use, SpawnedMonster m, double requestedAt)
    {
        if (m.OwnerId is not null) return;
        int projectileEpoch;
        lock (conn.Monsters) projectileEpoch = conn.ProjectileEpoch;
        var weapon = WeaponFor(conn);
        string? ordinaryProjectile = OrdinaryRangedAttack.ProjectileId(WeaponProfileFor(conn, activeSet: true));
        bool ranged = ordinaryProjectile is not null;
        int precision = ranged ? RangedPrecision : 1;
        double now = conn.Clock.Elapsed.TotalSeconds, duration;
        lock (conn.Monsters) duration = Math.Max(TickSeconds,
            weapon.AttackSeconds(CharacterPlayerStats(conn)) / PlayerActionFactor(conn));
        // The client sends one 0x24 per swing while the button is held; jitter makes the next one arrive a little before a
        // full attack time, so accept it up to a quarter of an attack early.
        lock (conn.Monsters)
            if ((ranged && conn.Paused) || !AttackTiming.Ready(now, conn.NextAttackAt, duration))
            {
                Console.WriteLine($"[gateway] {remote} <= 0x0024 skill 0x{use.SkillId:x4} seq 0x{use.Sequence:x4} ignored: {conn.NextAttackAt - now:0.000} s before the next attack");
                return;
            }
        int action;
        (int X, int Y) target, here, stop;
        double arrive;
        bool atOnce;
        double departedAt;
        using (ProjectileMutation(conn))
        {
            now = ProjectileWorldTime(conn);
            departedAt = now;
            if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0
                || PlayerActionBlocked(conn, requestedAt, now)) return;
            action = Interlocked.Increment(ref conn.ActionCount);
            target = m.Walk.Position(now);
            // A lockstep client's walk-up is placed where its 0x1ab goes out (LockstepAttackStart).
            if (conn.LockstepClient) (here, stop, arrive, atOnce) = (conn.Walk.Position(now), conn.Walk.Position(now), now, false);
            else (here, stop, arrive, atOnce) = WalkUpToSwing(conn, m, target, weapon, now, precision);
        }
        // "no path": no cell in reach with the line to the monster was found (nor the line from here at precision 1), so the
        // estimate stands; the swing then waits for the monster to come into reach (FollowMonsterToSwing) or is called off.
        string walkUp = arrive <= now && !atOnce ? " (no path)" : "";
        if (!conn.LockstepClient)
            Console.WriteLine($"[gateway] {remote} <= 0x0024 skill 0x{use.SkillId:x4} seq 0x{use.Sequence:x4} on 0x{use.TargetId:x}: player ({here.X}, {here.Y}), " +
                $"monster ({target.X}, {target.Y}), distance {Dist(here, target):0.0}; swing from ({stop.X}, {stop.Y}) in {arrive - now:0.00} s{walkUp}, attack time {duration:0.000} s");
        // A lockstep client's action starts now and walks up by itself (Lockstep.cs); the swing below then says nothing.
        var lockstep = await LockstepAttackStart(remote, stream, ciphers, conn, use, m, weapon, precision, action, "attack");
        bool started = lockstep is not null;
        bool Current() => conn.ActionCount == action && (!ranged || OrdinaryRangedReleaseCurrent(conn, action, projectileEpoch));
        async Task<bool> Until(double at, double from)
        {
            if (ranged) return await WaitProjectileTime(conn, at, projectileEpoch);
            await Task.Delay(TimeSpan.FromSeconds(at - from));
            return true;
        }
        if (await FollowMonsterToSwing(conn, m, weapon, Current, Until, stop, arrive, atOnce, now, precision, lockstep) is not { } swing)
        {
            Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} cancelled before the swing");
            LockstepAttackOver(conn, action);
            return;
        }
        (stop, arrive, double begun) = swing;
        now = conn.Clock.Elapsed.TotalSeconds;
        // A lockstep client's swing began at the start of its goal frame (LockstepAttackSwings), tested against the monster's
        // cell then; the hit and the next attack count from there.
        if (!started) begun = now;
        SkillCriticalRoll criticalRoll;
        bool inReach;
        lock (conn.Monsters)
        {
            target = m.Walk.Position(begun);
            // The swing holds a lockstep player from when it began until the client lets a Move cut it short
            // (LockstepAttackSwings); the next attack waits the attack time (NextAttackAt). On a lockstep client the
            // next attack, the hit and the release take the attack time the swing began with, as the hold does.
            if (started) duration = LockstepAttackSeconds(conn, use.SkillId);
            conn.NextAttackAt = AttackTiming.After(begun, conn.NextAttackAt, duration);
            criticalRoll = CaptureSkillCriticalRoll(conn, SkillCatalog.FirstSkillId);
            inReach = SwingInReach(conn, m, stop, target, weapon.Range + 2);
        }
        LockstepAttackOver(conn, action);
        if (!inReach)
        {
            Console.WriteLine($"[gateway] {remote} attack on 0x{use.TargetId:x} out of reach: {Dist(stop, target):0.0} > {weapon.Range}");
            return;
        }
        // As GGG does for every action but Move: the client counts unanswered actions and would ask for a resync.
        byte[][] actionRecords = [InstanceProtocol.ActionStarted(PlayerId, stop.X, stop.Y, target.X, target.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId), InstanceProtocol.ActionEnded(PlayerId)];
        string actionLog = $"0x01ab player skill 0x{use.SkillId:x4} on monster 0x{use.TargetId:x} ({m.Label}, life {m.Life}/{m.MaxLife}), attack time {duration:0.000} s";
        if (ordinaryProjectile is { } projectileId)
        {
            // The runtime schedules the release first, anchored to this acceptance (a lockstep client's to its swing's
            // start, as the melee hit is); the echo follows through the gate.
            await LaunchOrdinaryRanged(remote, stream, ciphers, conn, target, duration, action, projectileEpoch, projectileId, use.Sequence,
                criticalRoll, swingAt: started ? begun : null);
            if (!started && !await SendOrdinaryRangedAction(remote, stream, ciphers, conn, actionRecords, actionLog, action, projectileEpoch)) return;
        }
        else if (!started) await SendWorldRecords(remote, stream, ciphers, conn, PlayerId, () => actionRecords, actionLog, still: () => PlayerActionCurrent(conn, action));
        if (m.Life == 0) return;
        // A ranged target is an aim, not a reserved hit: the released arrow/bolt owns collision and damage.
        if (ranged) return;
        // A Strike's spirit strikes and previous-location strike are chosen as the swing starts, as the client chooses them
        // (StrikeTargets): the default attack is a Strike.
        int attackEffect = SkillEffectId(conn, SkillCatalog.FirstSkillId) is { } attackId ? SkillsFor(conn).EffectRow(attackId) : -1;
        var ownPenetration = attackEffect >= 0
            ? SkillsFor(conn).EffectStats(attackEffect, 1).ToDictionary(kv => kv.Item1, kv => kv.Item2) : null;
        List<uint> spirit;
        (int X, int Y)? echo;
        byte[]? memoryBuff;
        using (ProjectileMutation(conn))
            (spirit, echo, memoryBuff) = StrikeExtrasLocked(conn, use.SkillId, attackEffect, ownPenetration ?? new Dictionary<uint, int>(), stop, target,
                use.Sequence, use.TargetId, ProjectileWorldTime(conn));
        if (memoryBuff is not null)
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, memoryBuff, $"0x01c1 player buff skill_location_memory for skill 0x{use.SkillId:x4}");
        // The hit lands at the attack animation's contact keyframe (data/animation-contacts.json), not a fixed half: the
        // client stretches the clip to the attack time, so the blood and the weapon impact sound line up with the swing.
        double contact = DefaultAttackContactDelay(conn, duration, here, stop, departedAt, arrive, use.Sequence);
        await Task.Delay(TimeSpan.FromSeconds(started ? Math.Max(0, begun + contact - conn.Clock.Elapsed.TotalSeconds) : contact));
        await WhilePaused(conn);
        // One use, one Fortification and one Rage gain (as one AreaSkillHit sweep), whichever of its hits lands first.
        bool fortified = false, raged = false;
        if (!await HitTarget(use.TargetId, m, target)) return;
        List<(uint Id, SpawnedMonster M, (int X, int Y) At)> extras;
        lock (conn.Monsters)
        {
            double now2 = conn.Clock.Elapsed.TotalSeconds;
            extras = [.. spirit.Where(id => conn.Monsters.TryGetValue(id, out var s) && !s.Dead && s.Life > 0)
                .Select(id => (id, conn.Monsters[id], conn.Monsters[id].Walk.Position(now2)))];
            // The previous location's strike: the monster nearest it within the weapon's reach (a single-target hit, as the
            // default attack's own).
            if (echo is { } at && conn.Monsters.Where(e => e.Key != use.TargetId && !spirit.Contains(e.Key) && e.Value.Hostile
                    && !e.Value.Dead && e.Value.Life > 0 && Dist(e.Value.Walk.Position(now2), at) <= weapon.Range + LivingCollision.Size(e.Value))
                .OrderBy(e => Dist(e.Value.Walk.Position(now2), at)).Select(e => (e.Key, e.Value)).FirstOrDefault() is (var echoId, { } echoed))
                extras.Add((echoId, echoed, echoed.Walk.Position(now2)));
        }
        foreach (var (extraId, extra, at) in extras)
            if (!await HitTarget(extraId, extra, at)) return;

        // One hit of this swing on one monster: accuracy, block, impale, crit and on-hit as the target's own. False when a
        // later action replaced the swing.
        async Task<bool> HitTarget(uint targetId, SpawnedMonster m, (int X, int Y) target)
        {
            uint life;
            bool stageStarts = false;
            // Existing impales trigger one separate reflected hit and consume one remaining hit each. The actual
            // direct physical roll may then store a new instance; it never consumes that new instance itself.
            double stored, impaleStored;
            int impaleStacks, damage;
            bool impaled, critical;
            string? resistance;
            List<byte[]>? ignited = null, bled = null;
            string igniteLog = "", bleedLog = "";
            StunHit stun;
            using (ProjectileMutation(conn))
            {
                if (m.Life == 0) return true;
                if (!PlayerActionCurrent(conn, action) || conn.Paused)
                {
                    Console.WriteLine($"[gateway] {remote} attack on 0x{targetId:x} cancelled by a later action");
                    return false;
                }
                // Settle Rage at accepted contact before this hit's gain. It is a final multiplier on the attack packet,
                // never on an existing impale bonus or on generic damage/DoT.
                var player = CharacterPlayerStats(conn);
                var accuracy = AttackAccuracyProfile.From(conn.Pools, player, Stats, ownPenetration, weapon.LocalSources)
                    with { Rating = PlayerAccuracy(conn, player) };
                double contactAt = conn.Clock.Elapsed.TotalSeconds;
                int hitChance = AttackHitChanceAt(conn, targetId, m, contactAt, accuracy, player);
                if (!accuracy.CannotBeEvaded && !m.AttackEvasion.Hits(hitChance, contactAt, conn.AttackAccuracyRandom))
                {
                    Console.WriteLine($"[gateway] {remote} attack on 0x{targetId:x} evaded ({hitChance}% chance to hit)");
                    return true;
                }
                if (MonsterBlocks(m, Random.Shared, spell: false))   // Stalwart: nothing of the hit happens
                {
                    Console.WriteLine($"[gateway] {remote} attack on 0x{targetId:x} ({m.Label}) blocked");
                    return true;
                }
                var pen = Penetration.Of(player, Stats, attack: true, ownPenetration, weapon.LocalSources);
                var resistanceMods = HitResistMods(player, ownPenetration);
                var hit = AttackHitConverted(weapon, player, WeaponProfileFor(conn, activeSet: true).Melee,
                    ConversionFor(conn, attackEffect, 1, attack: true));
                if (WeaponProfileFor(conn, activeSet: true).Melee)
                    hit = WithMeleeProximityLocked(conn, player, ownPenetration, m, hit, contactAt);
                var gambler = GamblerFor(player);
                var ownRolled = GameEvents.WithOutgoingDamage(conn, player, m, (gambler with { DamageMorePct = 0 }).RollOutgoingHit(hit, Random.Shared));
                critical = RollCriticalStrike(conn, CriticalChanceOn(conn, targetId, conn.Pools.AttackCriticalStrikeChance,
                        AttackCriticalChanceVsBlinded(conn, player), AttackCriticalChanceVsProfane(conn, player)), ownPenetration, skillRoll: criticalRoll)
                    && GameServer.Core.World.EvasionEntropy.ConfirmsCritical(hitChance, conn.AttackAccuracyRandom);
                int sourceCriticalMultiplier = AttackCriticalMultiplier(conn, player);
                int criticalMultiplier = CriticalMultiplierOn(m, sourceCriticalMultiplier);
                double at = ProjectileWorldTime(conn);
                (damage, resistance, var mitigated) = MonsterHitDamage(m, ownRolled, critical, attack: true, at, resistanceMods, pen,
                    gambler.InvertsResistance(Random.Shared));
                if (critical) damage = DamageModel.CriticalDamage(damage, criticalMultiplier);
                LeechOnHitLocked(conn, m, MonsterTakenComponents(m, mitigated, spell: false, at), damage,
                    HitLeechSources(player, true, ownPenetration, weapon.LocalSources));
                (damage, stored, impaled) = WithImpaleHitLocked(conn, m, damage, ownRolled, critical, sourceCriticalMultiplier,
                    ImpaleStats.From(player, Stats, attack: true, ownPenetration, weapon.LocalSources, critical));
                impaleStacks = m.Impale.Stacks;
                impaleStored = m.Impale.Bonus;
                var fortificationDamage = RawFortificationDamage(ownRolled, critical, criticalMultiplier);
                bool fortifiedThisHit = !fortified && (fortified = FortificationOnHitLocked(conn, m, fortificationDamage,
                    WeaponProfileFor(conn, activeSet: true).Melee, player, ownPenetration));
                stun = OwnerStun(conn, m, damage, mitigated, WeaponProfileFor(conn, activeSet: true).Melee, spell: false, critical, player, ownPenetration)
                    with { FortificationDamage = fortificationDamage, Fortified = fortifiedThisHit };
                var rampageSource = RampageOnMeleeHitLocked(conn, m, WeaponProfileFor(conn, activeSet: true).Melee, player);
                (m.Life, stageStarts) = TakeOwnerHitLocked(conn, m, damage, Cull.Of(player, ownPenetration, critical, spell: false),
                    rampageSource, player);
                if (stageStarts)
                {
                    CommitBossStage(remote, stream, ciphers, conn, targetId, m, ProjectileWorldTime(conn));
                    stageStarts = false;
                }
                life = m.Life;
                conn.LastEnemyHitAt = m.PlayerHitAt = conn.Clock.Elapsed.TotalSeconds;
                // The "recently" stats (issue #504): hit an enemy, and crit with it when the roll was a critical one.
                conn.RecentEvents.Record(StatConditions.HitEnemy, conn.Clock.Elapsed.TotalSeconds);
                if (critical) conn.RecentEvents.Record(StatConditions.Crit, conn.Clock.Elapsed.TotalSeconds);
                if (life == 0) { m.Aggro = false; m.Dead = true; CollectVaalSoulAtDeathLocked(conn, m); }
                AilmentsOnHitLocked(conn, m, mitigated, spell: false, critical, criticalMultiplier, player, ownPenetration,
                    at, Random.Shared, poison: DefaultAttackPoison(conn, player));
                if (life > 0 && DefaultAttackIgnite(conn, player) is var defaultIgnite && defaultIgnite.CanIgnite(ownRolled)
                    && IgniteOnHitLocked(conn, targetId, m, defaultIgnite, critical, at) is { } packets)
                    (ignited, igniteLog) = (packets, IgnitedLog(targetId, m, critical, at));
                if (life > 0 && ownRolled[DamageType.Physical].Max > 0
                    && BleedOnHitLocked(conn, targetId, m, DefaultAttackBleed(conn, player), critical, at) is { } cut)
                    (bled, bleedLog) = (cut, BleedingLog(targetId, m, critical, at));
                TriggerElementalRelicLocked(conn, m, hit: damage > 0, killed: life == 0);
                if (!raged) raged = RageOnHitLocked(conn, attack: true, melee: WeaponProfileFor(conn, activeSet: true).Melee, critical);
                InflictMonsterBlind(conn, targetId, m, ResourceEffectsTime(conn), BlindInputs(player));
            }
            ChargesOnHit(conn, m, critical, spell: false);
            if (critical) _ = GainFlaskChargesOnCrit(remote, stream, ciphers, conn);   // the hit landed
            if (critical) await MaybeCreateProfaneGround(remote, stream, ciphers, conn, target);
            // At 0 the client's Life tick (0x141e70730) stops notifying its listeners, so the mini life bar would keep the last
            // value: hide it with the stat its listener watches (hide_mini_life_bar).
            if (life == 0)
                await SendWorldRecord(remote, stream, ciphers, conn, targetId, InstanceEntities.StatsUpdate(targetId, [(StatKeys.HideMiniLifeBar, 1)]),
                    $"0x01b3 monster 0x{targetId:x} hide_mini_life_bar = 1");
            // The hit reaction: one of the rig's TakeHit clips and its SoundEvent (0x1b2).
            await SendFlinch(remote, stream, ciphers, conn, targetId, m, stun, log: true);
            // The hit result for the client's damage/effect pipeline (0x1af): the client applies the hit locally (the target's
            // blood, the weapon impact sound "bonk") and 0x1b1 stays authoritative on life. flags 0x4201 is the real server's
            // player-melee hit (bit 0 hit, 0x200 damage, 0x4000 attack), kind 0x03 = action hit with hit data, the angle drives
            // the directional blood.
            var order = HitOrder(conn, PlayerId);
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionHit(PlayerId, use.Sequence, targetId, flags: 0x4201, kind: 0x03,
                    angle: HitAngle(target.X - stop.X, target.Y - stop.Y), orderKey: order.Key, checkpoint: order.Hit),
                $"0x01af player hit on monster 0x{targetId:x} (sequence 0x{use.Sequence:x4})");
            await SendWorldRecord(remote, stream, ciphers, conn, targetId, InstanceProtocol.LifeUpdate(targetId, life, orderKey: order.Key, checkpoint: order.Life),
                $"0x01b1 monster 0x{targetId:x} hit for {damage}{(critical ? " (critical)" : "")}" +
                (resistance is { } note ? $", {note}" : "") +
                (stored > 0 ? $", impale +{stored:0.#} ({impaleStacks} stack(s))" : "") +
                (impaled ? $", impaled ({impaleStacks} stack(s), {impaleStored:0.#} stored)" : "") +
                $", life {life}/{m.MaxLife}{(life == 0 ? " (dies)" : "")}");
            await SendDotBuff(remote, stream, ciphers, ignited, igniteLog);
            await SendDotBuff(remote, stream, ciphers, bled, bleedLog);
            if (life == 0) await MonsterKilled(remote, stream, ciphers, conn, targetId, m);
            if (stageStarts) _ = RunBossStage(remote, stream, ciphers, conn, targetId, m);
            if (m.ThresholdReached) await QuestThreshold(remote, stream, ciphers, conn, targetId, m);
            return true;
        }
    }

    // A gem skill used on a monster (C→S 0x24 with a skill id other than the default attack's). A spell stops and casts from
    // where the player stands: the server echoes the action from there, aimed at the monster, so the client plays the cast.
    // An attack gem (Holy Strike, Ground Slam …) is used like the default attack (see Attack): the client walks into reach
    // first, so the server walks its estimate too and only echoes the swing — and pays the skill's cost — on arrival. Before
    // 2026-10-25 the attack cast at the click from out of reach: the mana was gone at once and the hit landed around the
    // player, hitting nothing. CastGemSkill pays the cost and runs the skill's effect (projectile, area hit, aura, self-cast).
    public static async Task CastOn(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        InstanceTransfer.UseSkillOnObject use, SpawnedMonster m, double requestedAt)
    {
        if (m.OwnerId is not null) return;
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == use.SkillId);
        if (gem.Name is not null && GemSkillInstanceDisabled(conn, gem.Effect, use.SkillId) is not null) return;
        if (gem.Name is not null && !ProjectilePathAvailable(remote, conn, use.SkillId, gem.Effect)) return;
        // A movement attack (Flicker Strike, Leap Slam) closes the distance itself, and an attack used with "always attack
        // without moving" (Flags bit 0) swings from where the player stands: both keep the immediate cast.
        if (gem.Name is not null && !IsTotemSkill(conn, use.SkillId) && SkillsFor(conn).IsAttack(gem.Effect) && !SkillsFor(conn).IsMovement(gem.Effect) && (use.Flags & 0x1) == 0)
        {
            if (!AttackReady(conn, use.SkillId, gem.Effect, gem.Level, out double attackSeconds))
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x}: ignored, the previous attack is still running (attack time {attackSeconds:0.00} s) (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
                return;
            }
            var weapon = WeaponFor(conn);
            int precision = AttackPrecision(conn);
            double now = conn.Clock.Elapsed.TotalSeconds;
            int action = 0;
            (int X, int Y) target = default, here = default, stop = default;
            double arrive = now;
            bool atOnce = false;
            if (!TryBeginSkillCooldownUse(conn, use.SkillId, gem.Effect,
                CooldownForSkill(conn, use.SkillId), false,
                () =>
                {
                    now = ProjectileWorldTime(conn);
                    action = Interlocked.Increment(ref conn.ActionCount);
                    target = m.Walk.Position(now);
                    // A lockstep client's walk-up is placed where its 0x1ab goes out (LockstepAttackStart).
                    if (conn.LockstepClient) (here, stop, arrive, atOnce) = (conn.Walk.Position(now), conn.Walk.Position(now), now, false);
                    else (here, stop, arrive, atOnce) = WalkUpToSwing(conn, m, target, weapon, now, precision);
                }, out var approachTicket, reserve: false, requestedAt: requestedAt)) return;
            if (!conn.LockstepClient)
                Console.WriteLine($"[gateway] {remote} <= 0x0024 skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x}: player ({here.X}, {here.Y}), " +
                    $"monster ({target.X}, {target.Y}), distance {Dist(here, target):0.0}; swing from ({stop.X}, {stop.Y}) in {arrive - now:0.00} s (t={now:0.000})");
            // A lockstep client's action starts now and walks up by itself (Lockstep.cs); the swing below then says nothing.
            var lockstep = await LockstepAttackStart(remote, stream, ciphers, conn, use, m, weapon, precision, action, gem.Name);
            bool started = lockstep is not null;
            async Task<bool> Walked(double at, double from)
            {
                await Task.Delay(TimeSpan.FromSeconds(at - from));
                return true;
            }
            if (await FollowMonsterToSwing(conn, m, weapon, () => conn.ActionCount == action && approachTicket?.Current(conn) != false,
                Walked, stop, arrive, atOnce, now, precision, lockstep) is not { } swing)
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x} cancelled before the swing");
                LockstepAttackOver(conn, action);
                return;
            }
            (stop, arrive, double begun) = swing;
            now = conn.Clock.Elapsed.TotalSeconds;
            // A lockstep client's swing began at the start of its goal frame, tested against the monster's cell then.
            lock (conn.Monsters) target = m.Walk.Position(started ? begun : now);
            if (!SwingInReach(conn, m, stop, target, weapon.Range + 2))
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x} out of reach: {Dist(stop, target):0.0} > {weapon.Range}");
                LockstepAttackOver(conn, action);
                return;
            }
            if (!TryBeginSkillCooldownUse(conn, use.SkillId, gem.Effect,
                CooldownForSkill(conn, use.SkillId), false,
                () => { }, out var cooldownUse, actionToken: action))
            {
                LockstepAttackOver(conn, action);
                return;
            }
            // The swing's hold was set when it began (LockstepAttackSwings); its timer, hit and release count from then
            // too, as the default attack's do.
            LockstepAttackOver(conn, action);
            double? swingAt = started ? begun : null;
            bool acknowledged = false;
            bool projectileRuntime = UsesProjectileRuntime(gem.Effect);
            if (projectileRuntime) _ = CastGemSkill(remote, stream, ciphers, conn, use.SkillId, stop, target,
                use.TargetId, sequence: use.Sequence, actionToken: action, cooldownUse: cooldownUse, swingAt: swingAt);
            if (started)
                acknowledged = SkillAcknowledgementAllowed(conn, gem.Effect, use.SkillId,
                    () => cooldownUse?.CommitNativeUse(conn, use.SkillId) ?? PlayerActionCurrent(conn, action));
            else await SendWorldRecords(remote, stream, ciphers, conn, PlayerId, () => [InstanceProtocol.ActionStarted(PlayerId, stop.X, stop.Y, target.X, target.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId), InstanceProtocol.ActionEnded(PlayerId)],
                $"0x01ab player skill 0x{use.SkillId:x4} {gem.Name} on monster 0x{use.TargetId:x} ({m.Label}) from ({stop.X}, {stop.Y}) to ({target.X}, {target.Y}), flags 0x{use.Flags:x4} (t={conn.Clock.Elapsed.TotalSeconds:0.000})",
                still: () => acknowledged = SkillAcknowledgementAllowed(conn, gem.Effect, use.SkillId,
                    () => cooldownUse?.CommitNativeUse(conn, use.SkillId) ?? PlayerActionCurrent(conn, action)));
            if (acknowledged) await SendNativeCooldownRefresh(remote, stream, ciphers, conn);
            if (!projectileRuntime && !acknowledged) return;
            if (!projectileRuntime) _ = CastGemSkill(remote, stream, ciphers, conn, use.SkillId, stop, target, use.TargetId, sequence: use.Sequence,
                actionToken: action, cooldownUse: cooldownUse, swingAt: swingAt);
            return;
        }
        double castAt = conn.Clock.Elapsed.TotalSeconds;
        int castAction = 0;
        (int X, int Y) aim = default, stand = default, landing = default;
        int chargeBonus = gem.Name is null ? 0 : ShieldChargeBonusPct(conn, use.SkillId, gem.Effect), chargeToken = 0;
        bool moves = gem.Name is not null && chargeBonus == 0 && LandsAtAim(conn, gem.Effect);
        // Flicker Strike's client action strikes without moving the player: the server teleports it (FlickerStrike).
        bool flicker = moves && SkillsFor(conn).ActionHash(gem.Effect) == FlickerStrike.Action, flickers = false;
        // The monster Flicker Strike goes to, which may not be the clicked one (FlickerStrike.Pick): its strike's target.
        uint struckId = use.TargetId;
        (uint Id, (int X, int Y) At, (int X, int Y) Landing)? flickerTo = null;
        bool noFlickerTarget = false;
        // With no monster in range, Flicker Strike is not used: no cost, cooldown use or charge (the client may still
        // send it for a monster it has not yet seen die).
        bool FlickerAdmit()
        {
            double t = ProjectileWorldTime(conn);
            flickerTo = FlickerDestination(conn, gem.Effect, use.SkillId, conn.Walk.Position(t), m.Walk.Position(t), use.TargetId, t);
            noFlickerTarget = flickerTo is null;
            return !noFlickerTarget;
        }
        if (!TryBeginSkillCooldownUse(conn, use.SkillId, gem.Effect,
            gem.Name is null ? null : CooldownForSkill(conn, use.SkillId), false,
            () =>
            {
                castAt = ProjectileWorldTime(conn);
                castAction = Interlocked.Increment(ref conn.ActionCount);
                aim = m.Walk.Position(castAt);
                stand = conn.Walk.Position(castAt);
                conn.Walk.Order(stand.X, stand.Y, castAt);
                // A movement skill aimed at a monster travels as one aimed at the ground does.
                if (chargeBonus != 0) chargeToken = BeginShieldCharge(conn, use.SkillId, chargeBonus, aim.X, aim.Y, castAt);
                else if (flicker && flickerTo is { } to)
                {
                    (aim, landing, flickers, struckId) = (to.At, to.Landing, true, to.Id);
                    conn.Walk.Teleport(landing.X, landing.Y, castAt);
                }
                else if (moves)
                {
                    landing = MovementLanding(conn, gem.Effect, stand, aim, use.SkillId, use.Flags);
                    conn.Walk.Teleport(landing.X, landing.Y, castAt);
                }
            }, out var spellUse, requestedAt: requestedAt, admit: flicker ? FlickerAdmit : null))
        {
            if (noFlickerTarget) await FlickerRefused(remote, stream, ciphers, conn, gem.Name!);
            return;
        }
        if (chargeToken != 0) _ = RunShieldCharge(remote, stream, ciphers, conn, chargeToken, conn.Walk.ArrivesAt,
            use.SkillId, use.Sequence, spellUse?.ActionToken ?? castAction, spellUse);
        bool spellAcknowledged = false;
        bool spellProjectileRuntime = UsesProjectileRuntime(gem.Effect);
        if (spellProjectileRuntime) _ = CastGemSkill(remote, stream, ciphers, conn, use.SkillId, stand, aim,
            use.TargetId, sequence: use.Sequence, actionToken: spellUse?.ActionToken ?? castAction, cooldownUse: spellUse);
        await SendWorldRecords(remote, stream, ciphers, conn, PlayerId, () => [InstanceProtocol.ActionStarted(PlayerId, stand.X, stand.Y, aim.X, aim.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId), InstanceProtocol.ActionEnded(PlayerId)],
            $"0x01ab player skill 0x{use.SkillId:x4} cast on monster 0x{use.TargetId:x} ({m.Label}) from ({stand.X}, {stand.Y}) to ({aim.X}, {aim.Y}), flags 0x{use.Flags:x4} (t={castAt:0.000})",
            still: () => spellAcknowledged = SkillAcknowledgementAllowed(conn, gem.Effect, use.SkillId,
                () => spellUse?.CommitNativeUse(conn, use.SkillId) ?? PlayerActionCurrent(conn, castAction)));
        if (flickers && spellAcknowledged)
        {
            await FlickerArt(remote, stream, ciphers, conn, gem.Gem, stand, landing);
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.Teleport(PlayerId, landing.X, landing.Y),
                $"0x01b7 {gem.Name} to ({landing.X}, {landing.Y}) for the monster at ({aim.X}, {aim.Y}) (t={castAt:0.000})");
        }
        if (flicker && spellAcknowledged) await GrantFlickerSpeed(remote, stream, ciphers, conn, use.SkillId);
        if (spellAcknowledged) await SendNativeCooldownRefresh(remote, stream, ciphers, conn);
        if (moves) ResyncAfter(remote, stream, ciphers, conn, MovementSeconds(conn, gem.Effect, gem.Level), gem.Name!);
        if (!spellProjectileRuntime && !spellAcknowledged) return;
        if (!spellProjectileRuntime) _ = CastGemSkill(remote, stream, ciphers, conn, use.SkillId, flicker ? landing : stand, aim, struckId,
            sequence: use.Sequence, actionToken: spellUse?.ActionToken ?? castAction, cooldownUse: spellUse);
    }

    // A gem skill's cast: Righteous Fire puts its buff on, any other skill launches its projectile (if it has one). False when
    // it was refused (cooldown, mana), so its use is not confirmed to the client. An attack's timer, hit and release
    // count from swingAt when its swing began before now (a lockstep walk-up's goal frame, CastOn), else from now.
    public static async Task<bool> CastGemSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (int X, int Y) from, (int X, int Y) to, uint targetId = 0, ushort sequence = 0,
        int? actionToken = null, CooldownUseTicket? cooldownUse = null, bool locationInstant = false, double? swingAt = null)
    {
        int action = actionToken ?? conn.ActionCount;
        int recoveryGeneration = 0;
        int? channelGeneration = null;
        VaalUseReservation? vaalUse = null;
        lock (conn.Monsters)
            if (actionToken is not null && (conn.ActionCount != action || conn.ElementalAilments.ActionFactor(conn.Clock.Elapsed.TotalSeconds) == 0 || conn.HitRecovery.Active(conn.Clock.Elapsed.TotalSeconds))) return false;
        if (InTown(conn) || conn.PlayerLife == 0) return false;
        await EndGracePeriod(remote, stream, ciphers, conn, action: true);
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
        bool Current() => (cooldownUse?.Current(conn) ?? (actionToken is null || PlayerActionCurrent(conn, action)))
            && (vaalUse is null || VaalAdmissionCurrent(conn, vaalUse))
            && (channelGeneration is not int generation || ChannelCurrent(conn, skillId, action, generation));
        if (!Current()) return false;
        if (gem.Name is not null && !ProjectilePathAvailable(remote, conn, skillId, gem.Effect)) return false;
        if (locationInstant && (gem.Name is null || !InstantLocationSkill.Supported(SkillsFor(conn), gem.Effect))) return false;
        if (gem.Name is not null && SkillsFor(conn).IsSpell(gem.Effect) && !SkillsFor(conn).IsAttack(gem.Effect)
            && CastMs(conn, gem.Effect, skillId) is null)
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
        bool vaalCast = gem.Name is not null && SkillsFor(conn).VaalBudget(gem.Effect, gem.Level) is not null;
        if (vaalCast && (vaalUse = ExecuteVaalUse(conn, skillId, gem.Effect)) is null)
        {
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: no executable paid Vaal admission");
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
        else if (gem.Name is not null && CooldownForSkill(conn, skillId) is not null)
            return false;
        // Multi-use and unsupported cooldown mechanics retain their existing raw-deadline behavior.
        else if (gem.Name is not null && SkillsFor(conn).CooldownMs(gem.Effect, gem.Level) is int cooldown and > 0)
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
        // Paid Vaal admission already reserved this swing atomically with its cost, before the native echo.
        if (vaalUse is null && gem.Name is not null && !IsTotemSkill(conn, skillId) && SkillsFor(conn).IsAttack(gem.Effect))
        {
            double duration = SupportedAttackSeconds(conn, skillId);
            double now = swingAt ?? conn.Clock.Elapsed.TotalSeconds, next;
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
        // A link gem (Soul Link …) links its target at the end of its cast; it has no upfront cost, only its cost over time.
        if (gem.Name is not null && IsLinkSkill(conn, gem.Effect))
            return await CastLink(remote, stream, ciphers, conn, skillId, gem.Effect, targetId, Current);
        if (gem.Name is not null && ToggledReservation(conn, skillId) is { } reservation)
        {
            if (!Current()) return false;
            if (SkillsFor(conn).EffectId(gem.Effect) == PetrifiedBlood.EffectId)
                return await TogglePetrifiedBlood(remote, stream, ciphers, conn, skillId, gem.Effect, gem.Name, reservation,
                    SupportedSkill(conn, skillId).Level, SupportedSkill(conn, skillId).Quality);
            return await ToggleAura(remote, stream, ciphers, conn, skillId, gem.Effect, gem.Name, reservation,
                SupportedSkill(conn, skillId).Level, SupportedSkill(conn, skillId).Quality);
        }
        SkillCriticalRoll? criticalRoll = vaalUse?.CriticalRoll;
        if (gem.Name is not null && !vaalCast)
        {
            uint cost, lifeCost;
            bool paid;
            int? channelWalk = ChannelWalkPercent(conn, skillId, gem.Effect);
            lock (conn.Monsters)
            {
                if (!Current()) return false;
                // A cooldown use paid when it was accepted (TryBeginCooldownUseCore) is not paid again.
                if (cooldownUse?.Paid is { } prepaid) (paid, cost, lifeCost) = (true, prepaid.Mana, prepaid.Life);
                else paid = PayMana(conn, skillId, out cost, out lifeCost);
                if (paid) criticalRoll = CaptureSkillCriticalRoll(conn, skillId);
                if (paid && Supports.Of(gem.Effect)?.Types.Contains(47) == true)
                {
                    channelGeneration = BeginPaidChannel(conn, skillId, gem.Effect, action, sequence);
                    bool walkChanges = channelWalk is not null || conn.ChannelMovement.Skill != 0;
                    conn.ChannelMovement = channelWalk is int walkPercent ? (skillId, walkPercent) : default;
                    if (walkChanges) conn.ApplyMovementSpeed();
                    if (GolemStat(SupportedSkill(conn, skillId).Stats, "cast_while_channelling_time_ms") > 0)
                        BeginChannelTriggers(conn, ProjectilesFor(remote, stream, ciphers, conn), skillId,
                            action, conn.ChannelGeneration, to, conn.Clock.Elapsed.TotalSeconds);
                }
                // Capture at paid admission, before output or release scheduling can catch up other hits.
                recoveryGeneration = conn.ActionInterruptionGeneration;
            }
            if (!paid)
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: not enough mana or life " +
                    $"({conn.PlayerMana} < {cost}, {conn.PlayerLife} life for {lifeCost})");
                return false;
            }
            if (cost > 0 || lifeCost > 0)
            {
                if (UsesProjectileRuntime(gem.Effect)) ProjectilesFor(remote, stream, ciphers, conn)
                    .Enqueue(() => SendSkillCost(remote, stream, ciphers, conn, lifeCost, $"projectile cost paid: {gem.Name}"));
                else await SendSkillCost(remote, stream, ciphers, conn, lifeCost,
                    $"0x01b1 player cost {conn.PlayerMana} mana, {conn.PlayerLife} life (-{cost} mana, -{lifeCost} life for {gem.Name}) (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
            }
        }
        if (vaalCast)
        {
            recoveryGeneration = vaalUse!.RecoveryGeneration;
            await PublishVaalUse(remote, stream, ciphers, conn, vaalUse);
        }
        if (!Current()) return false;
        if (channelGeneration is not null)
        {
            lock (conn.Monsters)
            {
                if (!Current()) return false;
                if (SupportedChannelSeconds(conn, skillId) is not double interval)
                { conn.ChannelSkill = 0; conn.ChannelGeneration++; conn.PaidChannel = null; return false; }
                conn.NextChannelHitAt = conn.Clock.Elapsed.TotalSeconds + interval;
                _ = RunChannel(remote, stream, ciphers, conn, skillId, gem, action);
            }
        }
        // Location instant acceptance handles only Frostblink's repositioning, without inventing its departure/arrival
        // hits or chilled ground. In particular do not route it through the generic single-area hit approximation.
        if (locationInstant) return true;
        if (gem.Name is not null && (SkillsFor(conn).EffectId(gem.Effect) is "DetonateMines" or "GemDetonateMines" or "TriggeredDetonateMines"))
        { lock (conn.Monsters) DetonateMinesLocked(conn, AreaTime(conn)); return true; }
        if (gem.Name is not null && PlacedKind(SupportedSkill(conn, skillId)) is not (false, false))
            return await PlaceSkill(remote, stream, ciphers, conn, skillId, from, to, recoveryGeneration, Current);
        if (gem.Name is not null)
        {
            if (SkillsFor(conn).IsTravel(gem.Effect)) { QueueFlaskTrigger(conn, FlaskEnchantments.Trigger.Travel); LoyaltyTravelSkillTriggered(conn); }
            if (SkillsFor(conn).IsGuard(gem.Effect)) QueueFlaskTrigger(conn, FlaskEnchantments.Trigger.Guard);
        }
        // The Portal skill: a town portal, as the Portal Scroll's, once its cast (2.5 s) is over.
        if (actionToken is not null && !PlayerActionCurrent(conn, action)) return false;
        if (gem.Name is not null)
            foreach (var handler in GameEvents.SkillCastAt.For(conn))
                if (handler(conn, SkillsFor(conn).EffectId(gem.Effect), skillId, to,
                    () => !conn.Closed && PlayerActionCurrent(conn, action) && Current()) is { } accepted) return accepted;
        if (gem.Name is not null && GameEvents.RunSkillCast(conn, SkillsFor(conn).EffectId(gem.Effect), skillId, from,
                () => !conn.Closed && PlayerActionCurrent(conn, action) && Current()) is { } taken) return taken;
        if (gem.Name is not null && CurseCatalog.All.ContainsKey(SkillsFor(conn).EffectId(gem.Effect) ?? ""))
            return await CastCurse(remote, stream, ciphers, conn, skillId, to, targetId, recoveryGeneration, Current);
        if (gem.Name is not null && MinionUtility(SkillsFor(conn).EffectId(gem.Effect)))
            return await CastMinionUtility(conn, skillId, to, targetId, recoveryGeneration, Current);
        if (gem.Name is not null && GolemData.Find(SkillsFor(conn).EffectId(gem.Effect)) is { } golem)
            return await CastGolem(remote, stream, ciphers, conn, skillId, golem, to, recoveryGeneration, Current);
        if (gem.Name is not null && SummonData.Find(SkillsFor(conn).EffectId(gem.Effect)) is { } summon)
            return await CastSummon(remote, stream, ciphers, conn, skillId, summon, to, targetId, recoveryGeneration, Current);
        if (gem.Name is not null && SkillsFor(conn).EffectId(gem.Effect) == "Desecrate")
            return await Desecrate(remote, stream, ciphers, conn, skillId, to, Current);
        if (gem.Name is not null && IsTotemSkill(conn, skillId) && TotemVariety(conn, gem.Effect) is { } totem)
        {
            _ = PlaceTotem(remote, stream, ciphers, conn, skillId, gem.Effect, gem.Level, totem, to, Current);
            return true;
        }
        if (gem.Name is not null && UseWarcryInstance(remote, stream, ciphers, conn, skillId, gem.Effect)) return true;
        if (gem.Name is not null && IsBladefallTrarthus(gem.Effect))
            return await ActivateBladefallTrarthus(remote, stream, ciphers, conn, skillId);
        if (gem.Name is not null && IsRighteousFire(gem.Effect))
            await ActivateRighteousFire(remote, stream, ciphers, conn, skillId, gem.Effect, gem.Level, gem.Quality, gem.Gem);
        else
            await LaunchProjectile(remote, stream, ciphers, conn, skillId, from, to, recoveryGeneration, targetId, sequence: sequence,
                actionToken: actionToken, cooldownUse: cooldownUse, criticalRoll: criticalRoll, channelGeneration: channelGeneration,
                swingAt: swingAt);
        return true;
    }

    // Lockstep instant replies join the ordinary action sequence. At 0x14219c1f0, flag 0x100 reaches
    // 0x141e98bb0 and suppresses local action-event dispatch; 0x20 is only the transported target marker.
    public static byte[] PlayerInstantAction(ClientState conn, ushort skill, (int X, int Y) aim, uint? target = null)
    {
        lock (conn.Monsters)
            return InstanceProtocol.InstantAction(PlayerId, skill, aim.X, aim.Y, target,
                conn.LockstepClient ? NextLockstepSequence(conn) : (ushort)0xffff,
                conn.LockstepClient ? (ushort)0x101 : (ushort)0);
    }

    // An instant skill (C→S 0x191): its effect, then S→C 0x1ce, which has the client run the skill's action without animation
    // and consume a use of it: the skill bar's cooldown starts (the tooltip showed the cooldown, the bar did not run it).
    public static async Task UseInstantSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, uint targetId, (int X, int Y) at)
    {
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is not null && GemSkillInstanceDisabled(conn, gem.Effect, skillId) is not null) return;
        if (gem.Name is not null && InstantLocationSkill.Supported(SkillsFor(conn), gem.Effect))
        {
            await UseInstantSkillAtLocation(remote, stream, ciphers, conn, skillId, default, targetId);
            return;
        }
        if (gem.Name is null || !ObjectInstantSupported(conn, skillId, gem.Effect)) return;
        int epoch;
        lock (conn.Monsters)
        {
            epoch = conn.ProjectileEpoch;
            if (!conn.ProjectileActive(epoch) || conn.Paused || !conn.PlayerSent || conn.PendingTransfer is not null) return;
        }
        if (!TryBeginSkillCooldownUse(conn, skillId, gem.Effect,
            CooldownForSkill(conn, skillId),
            true, () => { }, out var cooldownUse)) return;
        if (await CastGemSkill(remote, stream, ciphers, conn, skillId, at, at, targetId, cooldownUse: cooldownUse))
        {
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, [],
                $"0x01ce player skill 0x{skillId:x4}: instant action (its use and cooldown)",
                atGate: () =>
                {
                    lock (conn.Monsters)
                    {
                        if (!conn.ProjectileActive(epoch) || conn.PlayerLife <= 0 || conn.Paused
                            || ActorSkillInstanceDisabled(conn, gem.Effect, skillId) is not null
                            || cooldownUse?.CommitNativeUse(conn, skillId) == false) return null;
                        return PlayerInstantAction(conn, skillId, at, targetId);
                    }
                });
            await SendNativeCooldownRefresh(remote, stream, ciphers, conn);
        }
    }

    static bool ObjectInstantSupported(ClientState conn, ushort skill, int effect) =>
        InstantLocationSkill.ObjectTargetSupported(SkillsFor(conn), effect)
        || !SkillsFor(conn).IsMovement(effect) && GolemStat(SupportedSkill(conn, skill).Stats, "base_skill_is_instant") != 0;

    public static async Task UseInstantSkillAtLocation(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, ushort skillId, (uint X, uint Y) aim, uint? targetObjectId = null)
    {
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is null || GemSkillInstanceDisabled(conn, gem.Effect, skillId) is not null) return;
        if (!InstantLocationSkill.Supported(SkillsFor(conn), gem.Effect))
        {
            // A self buff aimed at empty ground uses 0x190 too; it still applies at its caster.
            if (ObjectInstantSupported(conn, skillId, gem.Effect))
                await UseInstantSkill(remote, stream, ciphers, conn, skillId, PlayerId, conn.Walk.Position(AreaTime(conn)));
            return;
        }
        var own = SkillsFor(conn).EffectStats(gem.Effect, gem.Level, gem.Quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
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
            CooldownForSkill(conn, skillId),
            true, () => { }, out var cooldownUse)) return;
        if (!await CastGemSkill(remote, stream, ciphers, conn, skillId, from, to, cooldownUse: cooldownUse, locationInstant: true)) return;
        bool accepted = false;
        // Keep the raw aim: IceDash resolves it from the client's position when 0x1ce runs. Sending an already rounded
        // endpoint can normalize a short diagonal twice and move another cell. Resolve the server copy at this gate too.
        await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, [],
            $"0x01ce player skill 0x{skillId:x4}: instant {(targetObjectId is uint id ? $"on 0x{id:x}" : $"location ({aim.X}, {aim.Y})")}, movement only", atGate: () =>
            {
                using (ProjectileMutation(conn))
                {
                    var current = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
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
                    return PlayerInstantAction(conn, skillId, rawAim, targetObjectId);
                }
            });
        if (accepted)
        {
            if (SkillsFor(conn).IsTravel(gem.Effect)) { QueueFlaskTrigger(conn, FlaskEnchantments.Trigger.Travel); LoyaltyTravelSkillTriggered(conn); }
            await SendNativeCooldownRefresh(remote, stream, ciphers, conn);
        }
    }

    // Aura skills toggle: the first cast reserves their mana and puts their buff on the player, the next cast turns them
    // off. The reserved mana is sent in the Life pool and in every later 0x1b1 mana update; the client does not derive it.
    public static async Task<bool> ToggleAura(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, int effect, string name, (int Flat, int Percent) reservation, int level, int quality)
    {
        string? effectId = SkillsFor(conn).EffectId(effect);
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
                || OwnAuraSkillRestricted(conn, effect, skillId) || CastingSpell(conn, effect) && SpellsDisabled(conn)) return false;
            if (effectId == "TemporalRift" && conn.Auras.ContainsKey(skillId))
            {
                var packets = RecallTemporalRiftLocked(conn);
                if (packets.Length > 0) conn.Minions.OutputQueue.Add(new(packets, "Temporal Rift recalls resources and position"));
                started = conn.Auras[skillId];
            }
            else if (AuraCatalog.IsStance(effectId) && conn.Auras.ContainsKey(skillId))
            {
                conn.Stance = AuraCatalog.OtherStance(conn.Stance);
                foreach (var active in conn.Auras.Values.Where(a => AuraCatalog.IsStance(a.EffectId)).ToArray())
                    conn.Auras[active.SkillId] = active with { Stance = conn.Stance,
                        BuffRow = AuraCatalog.BuffRow(active.EffectId, conn.Stance), Buff = conn.NextBuff++,
                        SourceBuff = active.SourceBuff is not null ? conn.NextBuff++ : null };
                CharacterSaves.SetStance(CharacterName(conn), conn.Stance);
                started = conn.Auras[skillId];
            }
            else if (conn.Auras.Remove(skillId, out var old))
            { ended = old; EndReservationRuntimeLocked(conn, old); }
            else if (conn.Auras.Values.FirstOrDefault(a => a.EffectId == effectId) is { } same)
            {
                // One aura of a kind at a time: a second gem of the same aura can't start it.
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {name}: refused, {same.Name} already runs (skill 0x{same.SkillId:x4})");
                return false;
            }
            else
            {
                // Generosity: the supported aura's own increased effect, and "Supported Auras do not affect you".
                var (supportEffect, affectsSelf) = AuraSupport(conn, skillId);
                var context = ReservationContext(conn, skillId);
                string? stance = AuraCatalog.IsStance(effectId) ? conn.Stance : null;
                var aura = new AuraCatalog.Aura(skillId, effect, effectId, name, conn.NextBuff++,
                    AuraCatalog.BuffRow(effectId, stance), reservation.Flat, reservation.Percent, level, quality,
                    SourceBuff: OwnAuraRestriction.IsAura(SupportedSkill(conn, skillId).Stats) ? conn.NextBuff++ : null,
                    SupportEffectPct: supportEffect, AffectsSelf: affectsSelf, Stance: stance,
                    ReservesLife: context.Life, ReservationEfficiencyPct: context.Efficiency);
                conn.Auras[skillId] = aura;
                if (!ReservationBudgetFits(conn))
                {
                    conn.Auras.Remove(skillId);
                    Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {name}: insufficient unreserved {(context.Life ? "life" : "mana")}");
                    return false;
                }
                started = aura;
                ClampReservedResources(conn);
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

    public static IEnumerable<byte[]> AuraBuffs(AuraCatalog.Aura aura) =>
        AuraCatalog.StartPackets(aura, PlayerId, Skills.ActionHash(aura.Effect));

    public static async Task DisableReservations(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, string name)
    {
        lock (conn.Monsters)
        {
            bool all = name.Equals("all", StringComparison.OrdinalIgnoreCase);
            var matches = conn.Auras.Values.Where(a => all
                || a.EffectId.Equals(name, StringComparison.OrdinalIgnoreCase) || a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            var saved = CharacterSaves.Load(CharacterName(conn));
            var remembered = (saved?.ActiveAuras ?? []).Where(id => all || id.Equals(name, StringComparison.OrdinalIgnoreCase)
                || GemSkillEffects(conn).Any(g => SkillsFor(conn).EffectId(g.Effect) == id
                    && g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
            bool petrified = (all || name.Equals("Petrified Blood", StringComparison.OrdinalIgnoreCase)
                || name.Equals(PetrifiedBlood.EffectId, StringComparison.OrdinalIgnoreCase))
                && (conn.PetrifiedBlood.Active || saved?.PetrifiedBlood == true);
            if (!all && matches.Length == 0 && remembered.Length == 0 && !petrified)
                throw new ArgumentException("No active reservation matches that name; use /auras to list them.");
            foreach (string id in remembered) CharacterSaves.SetAuraActive(CharacterName(conn), id, false);
            if (petrified)
            { conn.PetrifiedBlood.Suspend(); CharacterSaves.SetPetrifiedBlood(CharacterName(conn), false); }
            foreach (var aura in matches)
            {
                conn.Auras.Remove(aura.SkillId);
                CharacterSaves.SetAuraActive(CharacterName(conn), aura.EffectId, false);
                EndReservationRuntimeLocked(conn, aura);
            }
            conn.AuraStatsDirty = true;
            conn.Pools = BuildPoolsFor(conn, conn.Pools.Level);
            conn.ApplyMovementSpeed();
        }
        await SendOwnAuraChanges(remote, stream, ciphers, conn);
        await SendCharacterStats(remote, stream, ciphers, conn, "reservation disabled");
        await SendPlayerMana(remote, stream, ciphers, conn);
    }

    // Every projectile uses the same scheduler and simulator. Definitions change rules, never execution paths.
    public static Task LaunchProjectile(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (int X, int Y) from, (int X, int Y) to, int recoveryGeneration, uint targetId = 0, ushort sequence = 0,
        int? actionToken = null, CooldownUseTicket? cooldownUse = null, SkillCriticalRoll? criticalRoll = null,
        int? channelGeneration = null, double? swingAt = null)
    {
        var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is null) return Task.CompletedTask;
        if (ArrowRain(SkillsFor(conn).EffectId(gem.Effect)))
        {
            var rainCritical = criticalRoll ?? CaptureSkillCriticalRoll(conn, skillId);
            var session = ProjectilesFor(remote, stream, ciphers, conn);
            session.Advance();
            lock (conn.Monsters)
            {
                var source = SupportedSkill(conn, skillId);
                double release = (swingAt ?? conn.Clock.Elapsed.TotalSeconds) + AttackReleaseDelay(AttackContactDelay(conn, SupportedAttackSeconds(conn, skillId), false));
                session.Runtime.Schedule(Math.Max(session.Runtime.Now, GameServer.Core.Projectiles.ProjectileRuntime.Time(release)), () =>
                {
                    if (session.Active && conn.PlayerLife > 0 && conn.ActionInterruptionGeneration == recoveryGeneration
                        && SameTriggerSource(source, SupportedSkill(conn, skillId)) && GemSkillInstanceDisabled(conn, gem.Effect, skillId) is null)
                        ReleaseArrowRain(session, skillId, gem, to, rainCritical, sequence);
                });
            }
            return Task.CompletedTask;
        }
        // Shield Charge hits along its own walk and where it ends (RunShieldCharge), not once where it started.
        if (ShieldChargeBonusPct(conn, skillId, gem.Effect) != 0) return Task.CompletedTask;
        // Winter Orb's orb fires its own volleys while the channel runs (WinterOrb.cs), not once at the cast.
        if (IsWinterOrb(gem.Effect)) return Task.CompletedTask;
        criticalRoll ??= CaptureSkillCriticalRoll(conn, skillId);
        if (IsLightningStrike(gem.Effect))
            return ScheduleLightningStrike(remote, stream, ciphers, conn, skillId, gem, from, to, targetId,
                recoveryGeneration, cooldownUse, criticalRoll,
                sequence, swingAt);
        if (UsesProjectileRuntime(gem.Effect))
            return ScheduleGemProjectile(remote, stream, ciphers, conn, skillId, gem, to,
                actionToken ?? cooldownUse?.ActionToken ?? conn.ActionCount, recoveryGeneration, cooldownUse, criticalRoll,
                sequence, swingAt);
        // A channelled skill (Cyclone, ActiveSkillType 47) hits as it starts: its spin is already in motion, and the later
        // sweeps follow on the attack timer (RunChannel).
        bool channelled = Supports.Of(gem.Effect)?.Types.Contains(47) == true;
        // A spell's repeats (SpellRepeats) land again a cast time apart, with the use's critical roll; a melee attack's
        // Multistrike repeats are hits of the same use, spread over its attack time with their own more damage.
        int spellRepeats = SpellRepeats(conn, skillId, gem.Effect);
        int meleeRepeats = channelled || spellRepeats > 0 ? 0 : MeleeAttackRepeats(conn, skillId);
        double repeatInterval = meleeRepeats > 0 ? SupportedAttackSeconds(conn, skillId) / (meleeRepeats + 1) : 0;
        // Fist of War: every ancestral_slam_interval_duration the next slam use is the big hit (more damage and area).
        double fistMore = 1, fistRadius = 1;
        bool spell = SkillsFor(conn).IsSpell(gem.Effect);
        if (!spell && !channelled)
        {
            var fistStats = SupportedSkill(conn, skillId).Stats;
            int Fist(string name) => Stats.Key(name) is uint key ? fistStats.GetValueOrDefault(key) : 0;
            if (Fist("support_ancestral_slam_big_hit_max_count") > 0)
            {
                double interval = Math.Max(0, Fist("ancestral_slam_interval_duration")) / 1000.0;
                double now = conn.Clock.Elapsed.TotalSeconds;
                bool ready;
                lock (conn.Monsters)
                {
                    ready = now >= conn.FistOfWarAt.GetValueOrDefault(skillId, double.NegativeInfinity);
                    if (ready) conn.FistOfWarAt[skillId] = now + interval;
                }
                if (ready)
                {
                    fistMore = 1 + Fist("support_ancestral_slam_big_hit_damage_with_hits_and_ailments_+%_final") / 100.0;
                    fistRadius = 1 + Fist("support_ancestral_slam_big_hit_area_+%") / 100.0;
                }
            }
        }
        var sacrifice = new SacrificeUse(conn, skillId, gem.Effect, gem.Gem);
        // Spell Cascade: the spell also lands in the areas beside the target, one per side along the aim and, with
        // support_spell_cascade_sideways, one per side across it. Each centre is one effective radius from the aim
        // (live 2026-11-04: Reap at the character's area of effect had radius 28.5 and repeats 28-29 cells apart; the
        // client draws each effect half a radius toward the caster, so the hit centre is the aim +- one radius).
        var cascadeAreas = new List<(int X, int Y)>();
        {
            var cascadeStats = SupportedSkill(conn, skillId).Stats;
            int Cascade(string name) => Stats.Key(name) is uint key ? cascadeStats.GetValueOrDefault(key) : 0;
            int sides = spell ? Cascade("support_spell_cascade_number_of_cascades_per_side") : 0;
            double dx = to.X - from.X, dy = to.Y - from.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (sides > 0 && length > 0)
            {
                int radius = SkillRadius(conn, cascadeStats, spell, gem.Effect);
                bool sideways = Cascade("support_spell_cascade_sideways") != 0;
                double ax = dx / length, ay = dy / length;
                for (int side = 1; side <= sides && radius > 0; side++)
                {
                    int alongX = (int)Math.Round(ax * radius * side), alongY = (int)Math.Round(ay * radius * side);
                    cascadeAreas.Add((to.X + alongX, to.Y + alongY));
                    cascadeAreas.Add((to.X - alongX, to.Y - alongY));
                    if (sideways)
                    {
                        cascadeAreas.Add((to.X - alongY, to.Y + alongX));
                        cascadeAreas.Add((to.X + alongY, to.Y - alongX));
                    }
                }
            }
        }
        var jobs = new List<Task>();
        // Discharge: one use, one count of its charges, for every repeat and cascade area (DischargeUse).
        var discharge = DischargeUseFor(conn, gem.Effect, gem.Level, gem.Quality);
        bool darkBargain = IsDarkBargain(conn, gem.Effect);
        // Storm Call sets a marker at every landing (each repeat and cascade area) that strikes later (StormCall.cs).
        if (IsStormCall(SkillsFor(conn).EffectId(gem.Effect)))
        {
            for (int repeat = 0; repeat <= spellRepeats; repeat++)
                foreach (var area in cascadeAreas.Prepend(to))
                    jobs.Add(PlaceStormCall(remote, stream, ciphers, conn, skillId, gem, area, repeat, actionToken, cooldownUse,
                        criticalRoll, sacrifice));
            return Task.WhenAll(jobs);
        }
        for (int repeat = 0; repeat <= Math.Max(spellRepeats, meleeRepeats); repeat++)
        {
            // Dark Bargain explodes at skeletons (or the player), not at the aim: each repeat pays and chains again.
            if (darkBargain)
            {
                jobs.Add(DarkBargainCast(remote, stream, ciphers, conn, skillId, gem, to, repeat, actionToken, cooldownUse, criticalRoll, sacrifice));
                continue;
            }
            void Hit((int X, int Y) centre) => jobs.Add(AreaSkillHit(remote, stream, ciphers, conn, skillId, gem, from,
                centre, immediate: channelled, sequence: sequence, actionToken: actionToken, cooldownUse: cooldownUse,
                criticalRoll: criticalRoll, channelGeneration: channelGeneration, targetId: targetId, repeat: repeat, swingAt: swingAt,
                repeatDelay: repeat * repeatInterval, radiusMore: fistRadius, sacrifice: sacrifice,
                damageMore: fistMore * (meleeRepeats > 0 ? MeleeRepeatDamageMore(conn, skillId, repeat) : 1), discharge: discharge));
            Hit(to);
            foreach (var area in cascadeAreas) Hit(area);
        }
        return Task.WhenAll(jobs);
    }

    // A channelled skill's hits (Cyclone): one sweep around the player every attack time (SupportedAttackSeconds: the
    // skill's own speed, its supports and the player's attack speed, as the tooltip shows) for as long as the channel
    // runs. The hits used to wait for the client's position updates (C→S 0x197), which do not come at that rate, so a
    // short channel landed one late sweep. The channel ends with C→S 0x199, any other action, mana, death or travel, or
    // when the client's 0x197 updates stop for ChannelSilenceSeconds (so a lost 0x199 cannot keep a channel spinning on
    // its own). A held Winter Orb is the exception: its client sends 0x198 only when the aimed cell changes, so a still
    // cursor leaves none, and the orb must keep charging the stages the bar is counting (ChannelCurrent).
    public static double FocusedChannelMore(double seconds, int perSecond) =>
        1 + Math.Clamp(Math.Floor(seconds) * perSecond, 0, 60) / 100d;

    public const double ChannelSilenceSeconds = 1.0;

    public static async Task RunChannel(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, int action)
    {
        int generation = conn.ChannelGeneration;
        double channelStarted = conn.Clock.Elapsed.TotalSeconds;
        bool Running() => ChannelCurrent(conn, skillId, action, generation);
        // Winter Orb's orb and its firing loop live in WinterOrb.cs: the loop starts with the channel and outlives it,
        // while this channel's cast ticks only build the orb's stages.
        uint orbEffect = 0;
        try
        {
            if (IsWinterOrb(gem.Effect))
                orbEffect = await WinterOrbStart(remote, stream, ciphers, conn, skillId, gem, action);
            while (true)
            {
                double wait = conn.NextChannelHitAt - conn.Clock.Elapsed.TotalSeconds;
                if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait));
                await WhilePaused(conn);
                if (!Running()) return;
                double now = conn.Clock.Elapsed.TotalSeconds;
                uint cost, lifeCost;
                using (conn.Performance.Lock(conn.Monsters, "channel"))
                {
                    if (!Running() || conn.Paused || now < conn.NextChannelHitAt) continue;
                    // A freeze or a stun's hit recovery interrupts the channelled action: no stage is paid or swept until it
                    // ends. The client's updates are dropped meanwhile, so a longer one ends the channel by silence.
                    if (PlayerActionBlocked(conn, now, now)) { conn.NextChannelHitAt = now + TickSeconds; continue; }
                    var activeGem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == skillId);
                    if (activeGem.Name is null || activeGem.Effect != gem.Effect
                        || GemSkillInstanceDisabled(conn, activeGem.Effect, skillId) is not null)
                    { conn.ChannelSkill = 0; conn.ChannelGeneration++; return; }
                    gem = activeGem;
                    conn.Performance.RecordMilliseconds(LatencyPhase.ChannelLate, (now - conn.NextChannelHitAt) * 1000);
                    if (SupportedChannelSeconds(conn, skillId) is not double interval)
                    { conn.ChannelSkill = 0; conn.ChannelGeneration++; conn.PaidChannel = null; return; }
                    conn.NextChannelHitAt = Math.Max(conn.NextChannelHitAt, now - 0.05) + interval;
                    // Payment and admission share the channel's generation check.
                    if (!PayMana(conn, skillId, out cost, out lifeCost))
                    { conn.ChannelSkill = 0; conn.ChannelGeneration++; conn.PaidChannel = null; }
                    else conn.PaidChannel = CombatLifetime is { IsActive: true } rules
                        ? new(skillId, gem.Effect, action, generation, rules) : null;
                }
                if (!Running()) { await SendPlayerMana(remote, stream, ciphers, conn, "channel stopped"); return; }
                if (cost > 0 || lifeCost > 0) await SendSkillCost(remote, stream, ciphers, conn, lifeCost, $"channel cost {cost} mana, {lifeCost} life");
                if (!Running()) return;
                // Winter Orb's orb pelts nearby enemies on its own loop (WinterOrb.cs); it does not sweep around the
                // player. This cast tick only builds a stage, so a faster caster charges the orb sooner.
                if (IsWinterOrb(gem.Effect))
                {
                    WinterOrbStageStep(conn);
                    await WinterOrbPresentStages(remote, stream, ciphers, conn);
                    continue;
                }
                var at = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
                await AreaSkillHit(remote, stream, ciphers, conn, skillId, gem, at, at,
                    immediate: true, sequence: conn.ChannelSkillSequence, actionToken: action, channelGeneration: generation,
                    damageMore: FocusedChannelMore(now - channelStarted,
                        GolemStat(SupportedSkill(conn, skillId).Stats, "support_focus_channel_damage_+%_final_per_second_channelling_up_to_60%")));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        finally
        {
            // Winter Orb's orb outlives its channel (WinterOrb.cs): it keeps firing on its decaying stages until the
            // duration they leave runs out, and death or an area change retires it with the connection. It has no object
            // of its own to remove: the client draws the orb from the stage buff.
            if (orbEffect != 0) WinterOrbRelease(conn, generation, action);
        }
    }

    // Cyclone moves while it is channelled: its final movement modifier (cyclone_movement_speed_+%_final, from the gem's
    // stats with its supports); null for a channel that stands (Blade Flurry …).
    public static int? ChannelWalkPercent(ClientState conn, ushort skillId, int effect) =>
        SkillArea.IsCyclone(SkillsFor(conn).EffectId(effect))
            ? GolemStat(SupportedSkill(conn, skillId).Stats, "cyclone_movement_speed_+%_final") : null;

    // C→S 0x197: the point the held channel is aimed at now (the cursor's cell), not the player's cell: it can be the
    // whole screen away the moment the channel starts. The client walks Cyclone's player toward it at the movement speed
    // with the skill's final modifier, as an order re-aimed on every update; the sweeps follow that walk (RunChannel).
    // A channel that stands keeps the player where it is.
    public static void ChannelAimedAt(ClientState conn, int x, int y, double at)
    {
        AimChannelTriggers(conn, (x, y));
        if (conn.ChannelMovement.Skill != 0 && conn.ChannelMovement.Skill == conn.ChannelSkill) conn.Walk.Order(x, y, at);
    }

    // C→S 0x199: the button let go. The channel ends, and a walking one stops where the player is (it was walking only
    // as part of the channel); the walk gets its usual speed back.
    public static void ChannelReleased(ClientState conn, double at)
    {
        lock (conn.Monsters)
        {
            if (conn.ChannelMovement.Skill != 0 && conn.ChannelMovement.Skill == conn.ChannelSkill)
            {
                var (x, y) = conn.Walk.Position(at);
                conn.Walk.Order(x, y, at);
            }
            conn.ChannelSkill = 0;
            conn.ChannelGeneration++;
        }
    }

    public static bool ChannelCurrent(ClientState conn, ushort skill, int action, int generation)
    {
        lock (conn.Monsters)
            return !conn.Closed && !conn.LeavingArea && !conn.RageRetired && conn.PlayerLife > 0
                && conn.ChannelSkill == skill && conn.ActionCount == action && conn.ChannelGeneration == generation
                // Winter Orb's channel is held by the button, not by the aim: a still cursor keeps the aimed cell and the
                // client sends no 0x198, so the aim silence must not release an orb the player is still charging. Before
                // this a still hold released at five stages while the bar kept counting (live 2026-11-10).
                && (conn.OrbRuntime is { Channelling: true } orb && orb.SkillId == skill
                    || conn.Clock.Elapsed.TotalSeconds - conn.LastChannelUpdateAt <= ChannelSilenceSeconds);
    }

    public static bool PaidChannelCurrent(ClientState conn, ChannelPaymentReceipt receipt)
    {
        lock (conn.Monsters)
        {
            if (!CurrentGameWork || !ReferenceEquals(receipt, conn.PaidChannel)
                || !ReferenceEquals(receipt.Rules, CombatLifetime) || !receipt.Rules.IsActive || conn.Paused
                || !ChannelCurrent(conn, receipt.Skill, receipt.Action, receipt.Generation)
                || PlayerActionBlocked(conn, conn.Clock.Elapsed.TotalSeconds, conn.Clock.Elapsed.TotalSeconds)) return false;
            var gem = GemSkillEffects(conn).FirstOrDefault(g => g.Id == receipt.Skill);
            return gem.Name is not null && gem.Effect == receipt.Effect
                && Supports.Of(gem.Effect)?.Types.Contains(47) == true
                && GemSkillInstanceDisabled(conn, gem.Effect, receipt.Skill) is null;
        }
    }

    // The radius in cells of a gem skill's hit around its aimed point (AreaSkillHit): its own
    // active_skill_base_area_of_effect_radius, else Cyclone's 16 or the generic 12, grown by the player's area stats
    // (SkillArea). The radius stat is in cells (Righteous Fire's 18 is its 1.8 m, verified against the real game 2026-10-23).
    public static int SkillRadius(ClientState conn, IReadOnlyDictionary<uint, int> skillStats, bool spell, int effect)
    {
        var areaStats = MergeSkillStats(AreaPlayerStats(conn), skillStats);
        int Get(string name) => Stats.Key(name) is uint k ? areaStats.GetValueOrDefault(k) : 0;
        return SkillArea.Radius(SkillArea.BaseRadius(SkillsFor(conn).EffectId(effect), Get), Get, spell);
    }

    // An area spell's centre: the aimed point, but a self-targeted spell (Ice Nova, Discharge, Vaal Cold Snap) goes off
    // around the caster wherever it was aimed, as the client's action does (FUN_1419439e0). A Nova among them goes off at
    // the aim when the caster has nova_spells_cast_at_target_location (Devout of Lunaris: "Nova Spells Cast at the
    // targeted location instead of around you"; the client's 0x2a6e).
    public static (int X, int Y) SpellCentre(ClientState conn, int effect, (int X, int Y) from, (int X, int Y) to) =>
        !SkillsFor(conn).IsSelfTargeted(effect) || SkillsFor(conn).IsNova(effect)
            && Stats.Key("nova_spells_cast_at_target_location") is uint key && CharacterPlayerStats(conn).GetValueOrDefault(key) != 0
            ? to : from;

    // A gem skill without a projectile (strikes, slams, area spells): a first, simple hit (2026-10-23). It lands once, after
    // the attack contact (attacks) or the area cast clip's contact (spells: AnimationContacts.SpellAreaRelease), on every
    // living monster within the skill's area radius
    // (active_skill_base_area_of_effect_radius, else AreaSkillRadius cells, grown by area of effect: SkillArea) of the aimed point, or of the player for a
    // point too far for a melee skill. Attacks deal the weapon's hit as the default attack does, spells their own damage
    // (SkillCatalog.HitDamage) with the player's stats; both convert, crit and roll per monster. An attack's hit carries
    // the skill's damage effectiveness (SkillCatalog.DamageEffectiveness). Not modelled yet: the skills' own shapes (waves,
    // cones, chains) and stun.
    public static Task AreaSkillHit(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) from, (int X, int Y) to,
        bool immediate = false, ushort sequence = 0, int? actionToken = null, CooldownUseTicket? cooldownUse = null, bool? melee = null,
        double damageMore = 1, uint excludeMonster = 0, SkillCriticalRoll? criticalRoll = null, int? channelGeneration = null,
        uint targetId = 0, int repeat = 0, int? radiusOverride = null, ISet<uint>? once = null, double repeatDelay = 0,
        double radiusMore = 1, SacrificeUse? sacrifice = null, double? swingAt = null, Func<bool>? triggeredCurrent = null,
        DischargeUse? discharge = null, int scaledBlastChaos = 0, uint? singleStrikeTarget = null, Action<uint>? onHit = null,
        ProjectileSession? scheduledSession = null) =>
        AreaSkillHitStrike(remote, stream, ciphers, conn, skillId, gem, from, to, immediate, sequence, actionToken, cooldownUse,
            melee, damageMore, excludeMonster, criticalRoll, channelGeneration, targetId, repeat, radiusOverride, once,
            repeatDelay, radiusMore, sacrifice, swingAt, triggeredCurrent, discharge, 0, null, null, null, scaledBlastChaos,
            singleStrikeTarget, onHit, scheduledSession);

    private static async Task AreaSkillHitStrike(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) from, (int X, int Y) to,
        bool immediate, ushort sequence, int? actionToken, CooldownUseTicket? cooldownUse, bool? melee,
        double damageMore, uint excludeMonster, SkillCriticalRoll? criticalRoll, int? channelGeneration,
        uint targetId, int repeat, int? radiusOverride, ISet<uint>? once, double repeatDelay,
        double radiusMore, SacrificeUse? sacrifice, double? swingAt, Func<bool>? triggeredCurrent, DischargeUse? discharge,
        int strikeIndex, double? firstContactAt, double? ruthlessMore, ISet<uint>? priorTargets, int scaledBlastChaos,
        uint? singleStrikeTarget, Action<uint>? onHit, ProjectileSession? scheduledSession)
    {
        var paidVaal = SkillsFor(conn).VaalBudget(gem.Effect, gem.Level) is null ? null : conn.VaalUse;
        var hitPlan = SkillHitPlan.ForSkill(SkillsFor(conn), gem.Effect, gem.Level, gem.Quality);
        bool spell = hitPlan.Kind == SkillHitKind.Spell;
        // A gem melee swing keeps its hit when the player only walks on: the client queues a Move until its clip's
        // cut-in instead of running it (Lockstep.cs, measured live 2026-11-03), so the Move the server hears first is a
        // queued order, not a replacement of the swing. Everything else keeps the action-token rule (a Move cancels the
        // swing), and a real interruption (death, area change, pause, freeze, stun recovery) still ends this one.
        bool holdsItsSwing = !spell && !immediate && channelGeneration is null && SkillsFor(conn).IsMelee(gem.Effect);
        bool preservesSwing = strikeIndex > 0 && !spell && channelGeneration is null && SkillsFor(conn).IsMelee(gem.Effect);
        bool TokenCurrent() => holdsItsSwing || preservesSwing
            ? PlayerStateAllowsAction(conn)
            : cooldownUse?.Current(conn) ?? (actionToken is null || conn.ActionCount == actionToken);
        bool Current() => (triggeredCurrent?.Invoke() ?? true) && TokenCurrent()
            && (SkillsFor(conn).VaalBudget(gem.Effect, gem.Level) is null
                || paidVaal is not null && VaalAdmissionCurrent(conn, paidVaal))
            && (channelGeneration is not int generation || ChannelCurrent(conn, skillId, actionToken ?? conn.ActionCount, generation));
        if (!Current()) return;
        var criticalTrigger = spell ? null : CaptureCriticalTriggerSource(conn, skillId);
        sacrifice ??= new SacrificeUse(conn, skillId, gem.Effect, gem.Gem, triggered: triggeredCurrent is not null);
        var skillStats = SupportedSkill(conn, skillId).Stats;
        // An attack gem's share of the weapon's damage ("Deals 177.3% of base Attack Damage"): the hit below uses it.
        int effectiveness = SkillsFor(conn).DamageEffectiveness(gem.Effect, gem.Level);
        int Stat(string name) => Stats.Key(name) is uint k ? skillStats.GetValueOrDefault(k) : 0;
        if (Stat("base_deal_no_damage") != 0) return;
        if (!hitPlan.CanHit)
        {
            // Contagion has no hit: its cast puts a chaos damage over time on the monsters in its area (MonsterContagions.cs).
            if (ContagionCast(conn, skillId, gem.Effect) is { } contagion)
                await CastContagion(conn, gem.Effect, skillId, from, to,
                    contagion with { SpreadAo = ContagionSkinAo(conn, gem.Gem) }, repeat, radiusMore,
                    actionToken ?? cooldownUse?.ActionToken ?? conn.ActionCount, cooldownUse?.Instant == true, Current);
            return;
        }
        // Discharge: the charges it spends are counted as the use starts; with none it deals nothing.
        if (hitPlan.SpendsCharges) discharge ??= new(RemovableCharges(conn));
        if (discharge is not null && discharge.Total == 0)
        {
            Console.WriteLine($"[gateway] {remote} {gem.Name}: no removable charges, nothing discharged");
            return;
        }
        criticalRoll ??= CaptureSkillCriticalRoll(conn, skillId);
        var weapon = WeaponFor(conn);
        int? castDuration = spell ? CastMs(conn, gem.Effect, skillId) : 0;
        if (castDuration is null) return;
        // Ruthless: whether this use is its skill's Ruthless Blow, counted as it starts.
        ruthlessMore ??= spell ? 1 : RuthlessBlowMore(conn, skillId, Stat);
        double ruthless = ruthlessMore.Value;
        double delay;
        // A spell lands at its area cast clip's contact keyframe (a repeat a whole cast later each), an attack at its
        // swing's own clip (GemAttackContactDelay; the player swings where it stands, so without the walk-in offset).
        string? contactClip = null;
        double contactFraction = 0;
        lock (conn.Monsters) delay = spell
            ? castDuration.Value / 1000.0 * (repeat + Animations.SpellAreaRelease(SkillsFor(conn).ClassFor(conn.ClassRow).AnimatedObject))
            : GemAttackContactDelay(conn, gem.Effect, SupportedAttackSeconds(conn, skillId), out contactClip, out contactFraction)
                + repeatDelay;
        // A caller's own shape (Shield Charge's path and end, ShieldCharge.cs) replaces the skill's area radius.
        int radius = (int)Math.Round((radiusOverride ?? SkillRadius(conn, skillStats, spell, gem.Effect)) * radiusMore);
        // Ground Slam's wave: the skills whose action is the data's "DistanceScaledGroundSlam" travel forward from where
        // the player stands instead of landing one circle (GroundSlamWave; its length, half angle and direction are read
        // once here, so the log after the sweep can report them). Vaal Ground Slam's own skill is a different action, so
        // the Vaal blast keeps its radial hit.
        bool wave = !immediate && !spell && SkillsFor(conn).ActionHash(gem.Effect) == GroundSlamWave.Action;
        double waveLength = wave ? GroundSlamWave.Length(radius) : 0;
        double waveHalfAngle = wave ? GroundSlamWave.HalfAngle(Stat) : 0;
        (double X, double Y) waveDir = wave ? GroundSlamWave.Direction(from, to) : (0, 0);
        // A wave starts where the player stands and only takes its direction from the aim; a melee skill aimed far away
        // still lands in front of the player, and a sweep around it (MeleeAreaCentre); a self-targeted spell goes off
        // around the caster.
        var centre = wave || VaalExecutor(conn, gem.Effect) ? from
            : spell ? SpellCentre(conn, gem.Effect, from, to)
            : MeleeAreaCentre.Of(SkillsFor(conn).ActionHash(gem.Effect), from, to, radius + weapon.Range);
        int action = actionToken ?? cooldownUse?.ActionToken ?? conn.ActionCount;
        // A later action (a Move, another use) replaces the swing; nothing of it lands from then on. The ordinary attack
        // says so at its own contact (Attack's HitTarget); this says so for a gem skill's sweep, whose drop was otherwise
        // invisible in the log (the live tests cannot count what a swing did without it).
        bool Replaced()
        {
            if (triggeredCurrent is not null || cooldownUse?.Instant == true || conn.ActionCount == action) return false;
            // A gem melee swing survives the Move the client queued behind its swing (holdsItsSwing): the walk order
            // stands, the hit still lands, and only a real interruption (which PlayerStateAllowsAction refuses) drops it.
            if ((holdsItsSwing || preservesSwing) && PlayerStateAllowsAction(conn))
            {
                Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: the swing keeps its hit over the "
                    + $"queued move order (action {action} -> {conn.ActionCount})");
                return false;
            }
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: the swing was replaced by action "
                + $"{conn.ActionCount} before its hit (action {action}), no hit lands");
            return true;
        }
        if (Replaced()) return;
        // A Strike's spirit strikes and previous-location strike, chosen as it starts, as the client chooses them
        // (StrikeTargets). Each monster takes one hit of the use, whichever of them reaches it.
        List<uint> spirit = [];
        (int X, int Y)? echo = null;
        byte[]? memoryBuff = null;
        if (!spell && channelGeneration is null && singleStrikeTarget is null && strikeIndex == 0)
            using (ProjectileMutation(conn, advance: scheduledSession is null))
                (spirit, echo, memoryBuff) = StrikeExtrasLocked(conn, skillId, gem.Effect, skillStats, from, centre, sequence,
                    targetId, ProjectileWorldTime(conn));
        if (memoryBuff is not null)
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, memoryBuff, $"0x01c1 player buff skill_location_memory for skill 0x{skillId:x4}");
        // A channel's hits (Cyclone, C→S 0x197) land at once, where the player is. A swing that began before now
        // (swingAt) lands its contact after its start.
        if (swingAt is { } swung) delay -= conn.Clock.Elapsed.TotalSeconds - swung;
        if (!immediate)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.05, delay)));
            await WhilePaused(conn);
            if (Replaced() || conn.Closed || !Current()) return;
        }
        int addedChaos;
        bool sacrificedLife = false;
        double contactAt = firstContactAt ?? conn.Clock.Elapsed.TotalSeconds;
        int strikes = SkillMultiStrike.StrikeCount(SkillsFor(conn).EffectId(gem.Effect), Stat("base_skill_number_of_additional_hits"),
            immediate, channelGeneration is not null, spell, wave);
        ISet<uint>? alreadyHit = priorTargets ?? SkillMultiStrike.SnapshotPriorTargets(once, strikes);
        List<(uint Id, SpawnedMonster M)> targets;
        int sweep;
        // Melee Splash victims and their damage multiplier (see MeleeSplash).
        var splashMore = new Dictionary<uint, double>();
        // Ice Nova's ring reaches each victim in turn (IceNovaRing) and Ground Slam's wave does the same by distance
        // (GroundSlamWave); a channel's sweep lands at once.
        bool ring = !immediate && SkillsFor(conn).ActionHash(gem.Effect) == IceNovaRing.Action;
        int artVariation = ring ? SkillArtVariation(conn, gem.Gem) : 0;
        double sweepStart = 0;
        double[] reach = [];
        int epoch = conn.ProjectileEpoch;
        using (ProjectileMutation(conn, advance: scheduledSession is null))
        {
            double now = ProjectileWorldTime(conn);
            if (!Current() || !sacrifice.Current) return;
            addedChaos = sacrifice.AddedChaos(() => sacrificedLife = true);
            // A freeze or stun that landed after this stage was paid interrupts it before the sweep.
            if (channelGeneration is not null
                && PlayerActionBlocked(conn, conn.Clock.Elapsed.TotalSeconds, conn.Clock.Elapsed.TotalSeconds)) return;
            // Queued mana output may outlast movement: resolve a channel at its current position.
            if (channelGeneration is not null) centre = conn.Walk.Position(now);
            var found = conn.Monsters.Select(e => (e.Key, e.Value, At: e.Value.Walk.Position(now)))
                .Select(e => (e.Key, e.Value, e.At, Distance: Dist(e.At, centre)))
                .Where(e => e.Key != excludeMonster && e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0 && e.Value.OwnerId is null
                    && (singleStrikeTarget is null || e.Key == singleStrikeTarget)
                    && e.Distance <= (wave ? waveLength : radius)
                    && (strikeIndex > 0 ? !SkillMultiStrike.WasHitBeforeUse(alreadyHit, e.Key) : once?.Contains(e.Key) != true)
                    && (!wave || GroundSlamWave.Covers(waveDir, from, e.At, waveHalfAngle, waveLength)))
                .ToList();
            // Hits that may reach each monster only once across several sweeps record it here.
            if (once is not null) foreach (var f in found) once.Add(f.Key);
            if (ring || wave)
            {
                found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                reach = wave
                    ? [.. found.Select(f => GroundSlamWave.AdvanceSeconds(f.Distance))]
                    : [.. found.Select(f => IceNovaRing.Reach(f.Distance, radius, artVariation))];
                sweepStart = conn.Clock.Elapsed.TotalSeconds;
            }
            targets = [.. found.Select(f => (f.Key, f.Value))];
            sweep = targets.Count;
            // shortcut: spirit and echo targets come after the ring's own and get no ring delay; time them by their
            // distance too once the client's rule for them is read.
            var struck = targets.Select(t => t.Id).ToHashSet();
            foreach (uint id in spirit)
                if (struck.Add(id) && conn.Monsters.TryGetValue(id, out var s) && !s.Dead && s.Life > 0) targets.Add((id, s));
            if (echo is { } at)
                targets.AddRange(conn.Monsters.Where(e => e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0
                    && Dist(e.Value.Walk.Position(now), at) <= radius && struck.Add(e.Key)).Select(e => (e.Key, e.Value)));
            // Melee Splash: each struck enemy splashes onto the enemies around it, for the support's less damage.
            if (!spell)
            {
                var merged = SupportedStats(conn, skillId);
                int SplashStat(string name) => Stats.Key(name) is uint key ? merged.GetValueOrDefault(key) : 0;
                if (MeleeSplash.Settings(SplashStat) is { } splash)
                    foreach (var (_, m) in targets.ToArray())
                    {
                        var splashAt = m.Walk.Position(now);
                        foreach (var (sid, sm) in conn.Monsters)
                            if (sm.Hostile && !sm.Dead && sm.Life > 0 && sm.OwnerId is null && struck.Add(sid)
                                && Dist(sm.Walk.Position(now), splashAt) <= splash.Radius)
                            {
                                targets.Add((sid, sm));
                                splashMore[sid] = splash.More;
                                once?.Add(sid);
                            }
                    }
            }
        }
        if (sacrificedLife)
            await Send(remote, stream, ciphers, [], null, atGate: sacrifice.LifePacket);
        bool fortificationGranted = false; // One simultaneous sweep, independent of notification latency.
        bool rageGranted = false; // Only a positive gain takes this sweep's latch.
        double bloodMore = BloodChargeDamageMore(conn, Stat);
        int landed = 0;
        // One sweep, one hit: its offensive inputs are read once, at the first contact (inside the projectile mutation, after
        // earlier contacts caught up), and rolled per monster. They were computed again for every monster (a whole stat
        // calculation each), which spread one sweep's hits over a second in a crowd.
        Dictionary<uint, int>? player = null;
        Penetration.PerType pen = default;
        HitResistances.Mods mods = default;
        DamagePacket sweepHit = default!;
        int sweepMultiplier = 0;
        AttackAccuracyProfile accuracy = default;
        LeechSources? leech = null;
        ImpaleStats.Settings impale = default, criticalImpale = default;
        MonsterIgnites.Settings ignite = default, bleed = default;
        MonsterPoisons.Settings poison = default;
        // Every monster gets its hit before the sweep's deaths run their loot, experience and quest work, and before a
        // Mirrored monster's reflected ailments reach the player (its freeze would end the action between two targets).
        var deaths = new List<(uint Id, SpawnedMonster M)>();
        var reflected = new List<MonsterElementalAilments.Effect>();
        for (int victim = 0; victim < targets.Count; victim++)
        {
            var (id, m) = targets[victim];
            // A ring and a wave arrive over time: the victim nearest the caster (or the player) first, each one when its
            // front reaches it. A retired wave (death, area change, a closed connection) stops the rest of the sweep.
            if ((ring || wave) && victim < reach.Length && reach[victim] > 0
                && !await WaitProjectileTime(conn, sweepStart + reach[victim], epoch))
            {
                if (wave) Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: the wave was retired "
                    + $"{conn.Clock.Elapsed.TotalSeconds - sweepStart:0.###} s in, {targets.Count - victim} monster(s) left unhit");
                break;
            }
            if (wave && victim < reach.Length)
                Console.WriteLine($"[gateway] {remote} {gem.Name} wave reaches 0x{id:x} at {reach[victim] * GroundSlamWave.CellsPerSecond:0.#} cells {reach[victim]:0.###} s after the slam");
            MonsterHit? applied;
            int damage;
            bool critical;
            string? resistance;
            List<byte[]>? ignited = null, bled = null;
            string igniteLog = "", bleedLog = "";
            StunHit stun;
            using (ProjectileMutation(conn, advance: scheduledSession is null))
            {
                // Catch up earlier projectile contacts before reading Rage or any other live offensive input. Stopping
                // the sweep keeps the kills it already queued. A released nova or slam wave keeps travelling when the
                // player moves on: its action was checked at the release.
                if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0 || conn.Paused
                    || !(ring || wave) && (!Current() || !holdsItsSwing && !preservesSwing && cooldownUse is null && triggeredCurrent is null && !PlayerActionCurrent(conn, action))) break;
                if (m.Dead || m.Life == 0 || !conn.Monsters.TryGetValue(id, out var current) || !ReferenceEquals(current, m)) continue;
                bool freshPlayer = player is null;
                if (player is null)
                {
                    player = CharacterPlayerStats(conn);
                    // A shield skill swings the shield (ShieldWeapon), which carries no local modifiers of the main hand.
                    var shield = spell ? null : ShieldWeapon(conn, skillId);
                    var local = spell || shield is not null ? null : MainHandLocalSources(conn);
                    pen = Penetration.Of(player, Stats, attack: !spell, skillStats, local);
                    mods = HitResistMods(player, skillStats);
                    sweepHit = SupportedHitFor(conn, skillId, suppress: true, weapon: shield, charges: discharge?.Charges, addedChaos: addedChaos);
                    if (ruthless != 1) sweepHit = sweepHit.Scale(ruthless);
                    if (scaledBlastChaos > 0) sweepHit = sweepHit.Add(DamageType.Chaos, new(scaledBlastChaos, scaledBlastChaos));
                    if (spell && repeat > 0) sweepHit = sweepHit.Scale(GreaterEchoHitMore(repeat, Stat));
                    sweepMultiplier = SupportedCriticalMultiplier(conn, skillId);
                    if (!spell) accuracy = SupportedAccuracy(conn, skillId);
                    leech = HitLeechSources(player, !spell, skillStats, local);
                    impale = ImpaleStats.From(player, Stats, !spell, skillStats, local);
                    criticalImpale = ImpaleStats.From(player, Stats, !spell, skillStats, local, critical: true);
                    ignite = SupportedIgnite(conn, skillId, spell, player, shield);
                    bleed = SupportedBleed(conn, skillId, spell, player, shield, melee: SkillsFor(conn).IsMelee(gem.Effect));
                    poison = SupportedPoison(conn, skillId, spell, player, shield, addedChaos);
                }
                var contactHit = sweepHit;
                if (VaalExecutor(conn, gem.Effect))
                {
                    double distance = Dist(conn.Walk.Position(ProjectileWorldTime(conn)), m.Walk.Position(ProjectileWorldTime(conn)));
                    contactHit = contactHit.Scale(1 + Stat("groundslam_damage_to_close_targets_+%_final")
                        / 100.0 * Math.Max(0, 1 - distance / Math.Max(1, radius)));
                }
                if (!spell && SkillsFor(conn).IsMelee(gem.Effect))
                    contactHit = WithMeleeProximityLocked(conn, player, skillStats, m, contactHit, ProjectileWorldTime(conn));
                int hitChance = spell ? 100 : AttackHitChanceAt(conn, id, m, ProjectileWorldTime(conn), accuracy, player);
                if (!spell && !accuracy.CannotBeEvaded && !m.AttackEvasion.Hits(hitChance, ProjectileWorldTime(conn), conn.AttackAccuracyRandom)) continue;
                if (MonsterBlocks(m, Random.Shared, spell, ProjectileWorldTime(conn), skillStats))
                {
                    Console.WriteLine($"[gateway] {remote} {gem.Name} on 0x{id:x} ({m.Label}) blocked");
                    continue;
                }
                var gambler = GamblerFor(player);
                var rolled = WithConditionalSupportDamageLocked(conn, skillStats, !spell, m,
                    GameEvents.WithOutgoingDamage(conn, player, m, (gambler with { DamageMorePct = 0 })
                        .ForSkill(Stat, spell, finalRepeat: repeat == SpellRepeats(conn, skillId, gem.Effect))
                        .RollOutgoingHit(contactHit, Random.Shared)),
                    ProjectileWorldTime(conn));
                // Hallow: an attack against an enemy affected by Hallowing Flame gains its share of physical damage as
                // extra fire, and removes the flame.
                if (!spell && m.HallowingFlameUntil > ProjectileWorldTime(conn) && rolled[DamageType.Physical].Min is > 0 and var flamePhys)
                {
                    double flameFire = flamePhys * m.HallowingFlamePct / 100.0;
                    rolled = rolled.Add(DamageType.Fire, new(flameFire, flameFire));
                    m.HallowingFlameUntil = 0;
                }
                // The shared roll meets each target's current chance: a source can change between targets.
                int criticalChance = CriticalChanceOn(conn, id, SupportedCriticalChance(conn, skillId),
                    SupportedCriticalChanceVsBlinded(conn, skillId, release: false),
                    SupportedCriticalChanceVsProfane(conn, skillId, release: false));
                critical = RollCriticalStrike(conn, criticalChance, skillStats,
                        attack: !spell, skillRoll: criticalRoll, player: player)
                    && (spell || GameServer.Core.World.EvasionEntropy.ConfirmsCritical(hitChance, conn.AttackAccuracyRandom));
                int multiplier = CriticalMultiplierOn(m, sweepMultiplier);
                double at = ProjectileWorldTime(conn);
                (damage, resistance, var mitigated) = MonsterHitDamage(m, rolled, critical, attack: !spell, at, mods, pen,
                    gambler.InvertsResistance(Random.Shared));
                var taken = MonsterTakenComponents(m, mitigated, spell, at);
                if (critical) damage = DamageModel.CriticalDamage(damage, multiplier);
                double more = damageMore * bloodMore;
                if (splashMore.TryGetValue(id, out double splashScale)) more *= splashScale;
                if (more != 1) damage = (int)Math.Floor(damage * Math.Max(0, more));
                LeechOnHitLocked(conn, m, taken, damage, leech!);
                damage = WithImpaleHitLocked(conn, m, damage, rolled, critical, sweepMultiplier,
                    critical ? criticalImpale : impale, more).Damage;
                stun = OwnerStun(conn, m, damage, mitigated, !spell && SkillsFor(conn).IsMelee(gem.Effect), spell, critical, player, skillStats);
                // Only the first victim has a freshly built player snapshot. After notification awaits, resolve the
                // current raw source so a refund between victims cannot keep granting from the sweep's old snapshot.
                var rampagePlayer = freshPlayer ? player : null;
                var rampageSource = RampageOnMeleeHitLocked(conn, m, !spell && SkillsFor(conn).IsMelee(gem.Effect), rampagePlayer);
                applied = ApplyMonsterHitLocked(conn, m, damage, rampageSource, rampagePlayer,
                    cull: Cull.Of(player, skillStats, critical, spell));
                if (applied is not null)
                {
                    if (!spell) ReleaseCriticalTrigger(conn, ProjectilesFor(remote, stream, ciphers, conn),
                        criticalTrigger, critical, m.Hostile, m.Walk.Position(at), at);
                    else ArcaneSurgeOnSpellHitLocked(conn, player, m);
                    if (critical) conn.RecentEvents.Record(StatConditions.Crit, at);
                    landed++;
                    // Item Quantity Support: the kill's loot rolls read the supported skill's share.
                    if (Stats.Key("base_killed_monster_dropped_item_quantity_+%") is uint quantityKey)
                        m.KilledWithItemQuantityPct = skillStats.GetValueOrDefault(quantityKey);
                    // Item Rarity Support: the same kill's drops read the skill's rarity share.
                    if (Stats.Key("base_killed_monster_dropped_item_rarity_+%") is uint rarityKey)
                        m.KilledWithItemRarityPct = skillStats.GetValueOrDefault(rarityKey);
                    // Soul Ascension: this player hit on a rare or unique enemy eats a soul (its own cooldown).
                    SoulEaterHit(conn, m);
                    // Devour: a killing blow recovers the support's life/mana.
                    if (applied.Value.Life == 0) DevourOnKill(conn, skillStats);
                    AilmentsOnHitLocked(conn, m, mitigated, spell, critical, multiplier, player, skillStats, at, Random.Shared,
                        reflected, poison, sourceSkill: skillId);
                    // As a projectile's hit: a hit with fire that leaves the monster alive may ignite it, an attack with
                    // physical damage bleed it.
                    if (m.Life > 0 && ignite.CanIgnite(rolled)
                        && IgniteOnHitLocked(conn, id, m, ignite, critical, at) is { } packets)
                        (ignited, igniteLog) = (packets, IgnitedLog(id, m, critical, at));
                    if (m.Life > 0 && rolled[DamageType.Physical].Max > 0
                        && BleedOnHitLocked(conn, id, m, bleed, critical, at) is { } cut)
                        (bled, bleedLog) = (cut, BleedingLog(id, m, critical, at));
                }
                if (applied is { StageStarts: true } staged)
                {
                    CommitBossStage(remote, stream, ciphers, conn, id, m, ProjectileWorldTime(conn));
                    applied = staged with { StageStarts = false };
                }
                if (applied is not null)
                {
                    var sweepFortificationDamage = RawFortificationDamage(rolled, critical, multiplier);
                    bool fortifiedThisHit = !fortificationGranted && (fortificationGranted = FortificationOnHitLocked(conn, m,
                        sweepFortificationDamage, !spell && SkillsFor(conn).IsMelee(gem.Effect), player, skillStats));
                    stun = stun with { FortificationDamage = sweepFortificationDamage, Fortified = fortifiedThisHit };
                    if (!rageGranted) rageGranted = RageOnHitLocked(conn, attack: !spell,
                        melee: !spell && SkillsFor(conn).IsMelee(gem.Effect), critical, skillStats, player: player);
                    InflictMonsterBlind(conn, id, m, ResourceEffectsTime(conn), BlindInputs(player, skillStats));
                    if (!spell && SkillsFor(conn).IsMelee(gem.Effect))
                    {
                        CombatRushOnHit(conn, skillStats, at);
                        HallowOnMeleeHit(conn, m, skillStats, at);
                        ExcommunicateOnMeleeHit(conn, m, skillStats, at);
                    }
                }
            }
            if (applied is not { } hit) continue;
            onHit?.Invoke(id);
            if (scheduledSession is { } scheduled)
            {
                using (ProjectileMutation(conn, advance: false))
                {
                    ChargesOnHit(conn, m, critical, spell, gem.Effect, gem.Level, gem.Quality, primary: true);
                    if (reflected.Count > 0 && player is not null)
                        ReflectAilmentsLocked(conn, reflected, ProjectileWorldTime(conn), player, Random.Shared);
                    CommitLightningStrikeMeleeHit(scheduled, id, m, hit, damage, critical, stun, sequence);
                }
                scheduled.Enqueue(() => SendDotBuff(remote, stream, ciphers, ignited, igniteLog));
                scheduled.Enqueue(() => SendDotBuff(remote, stream, ciphers, bled, bleedLog));
                return;
            }
            if (critical) _ = GainFlaskChargesOnCrit(remote, stream, ciphers, conn);
            if (critical) await MaybeCreateProfaneGround(remote, stream, ciphers, conn, m.Walk.Position(ProjectileWorldTime(conn)));
            await NotifyMonsterHit(remote, stream, ciphers, conn, id, m, hit, damage,
                $"{gem.Name}{(critical ? " (critical)" : "")}{(resistance is null ? "" : $", {resistance}")}",
                melee: melee ?? !spell, sequence: sequence, deaths: deaths, stun: stun);
            await SendDotBuff(remote, stream, ciphers, ignited, igniteLog);
            await SendDotBuff(remote, stream, ciphers, bled, bleedLog);
            ChargesOnHit(conn, m, critical, spell, gem.Effect, gem.Level, gem.Quality, primary: id == targets[0].Id);
        }
        if (reflected.Count > 0 && player is not null)
            using (ProjectileMutation(conn, advance: scheduledSession is null)) ReflectAilmentsLocked(conn, reflected, ProjectileWorldTime(conn), player, Random.Shared);
        foreach (var (deadId, dead) in deaths) await MonsterKilled(remote, stream, ciphers, conn, deadId, dead);
        // The wave reports its own shape (the tunables to calibrate, GroundSlamWave) instead of the circle; a gem melee
        // attack reports the clip its contact came from (GemAttackContactDelay), which the live check can compare with the
        // client's animation.
        string shape = wave
            ? $"in a wave {waveLength:0.#} cells long from ({from.X}, {from.Y}) toward ({waveDir.X:0.##}, {waveDir.Y:0.##}), "
                + $"half angle {waveHalfAngle:0.#} deg at {GroundSlamWave.CellsPerSecond:0.#} cells/s"
            : $"within {radius} cells of ({centre.X}, {centre.Y})";
        string contact = holdsItsSwing
            ? $", contact {contactFraction:0.###} of the attack ({(contactClip is null ? "the attack1 fallback" : contactClip)})"
            : "";
        Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: {(spell ? "spell" : "attack")} hit {targets.Count} monster(s) {shape}{contact} (t={conn.Clock.Elapsed.TotalSeconds:0.000})");
        // An immediate (triggered) spell has no cast clip to draw Discharge's explosion: the server draws it.
        if (discharge is not null
            && SpendDischargedCharges(remote, conn, gem.Name, gem.Gem, discharge, Stat("discharge_chance_not_to_consume_charges_%"), targets.Count)
            && immediate && spell)
            await ShowTriggeredDischarge(remote, stream, ciphers, conn, gem.Gem, discharge, centre, radius);
        if (BloodCharges.Uses(Stat) && player is not null)
        {
            bool changed;
            lock (conn.Monsters) changed = conn.PlayerLife > 0 && SettleBloodChargesLocked(conn, player, deaths.Count, landed - deaths.Count);
            if (changed) await SendBloodCharges(remote, stream, ciphers, conn, $"{gem.Name}: {landed} hit, {deaths.Count} killed");
        }
        if (strikeIndex == 0 && strikes > 1)
        {
            (double gap, bool keyframe) = GemAttackStrikeGap(conn, contactClip);
            double next = contactAt + gap * SupportedAttackSeconds(conn, skillId);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, next - conn.Clock.Elapsed.TotalSeconds)));
            await WhilePaused(conn);
            if (Replaced() || conn.Closed || !Current()) return;
            Console.WriteLine($"[gateway] {remote} skill 0x{skillId:x4} {gem.Name}: strike 2 at {next:0.000} "
                + $"({(keyframe ? contactClip : "modelled half-attack gap")})");
            await AreaSkillHitStrike(remote, stream, ciphers, conn, skillId, gem, from, to, immediate: true,
                sequence: sequence, actionToken: actionToken, cooldownUse: cooldownUse, melee: melee, damageMore: damageMore,
                excludeMonster: excludeMonster, criticalRoll: criticalRoll, channelGeneration: channelGeneration, targetId: targetId,
                repeat: repeat, radiusOverride: radiusOverride, once: null, repeatDelay: repeatDelay, radiusMore: radiusMore,
                sacrifice: sacrifice, swingAt: null, triggeredCurrent: triggeredCurrent, discharge: discharge,
                strikeIndex: 1, firstContactAt: next, ruthlessMore: ruthlessMore, priorTargets: alreadyHit, scaledBlastChaos: scaledBlastChaos,
                singleStrikeTarget: singleStrikeTarget, onHit: onHit, scheduledSession: scheduledSession);
        }
    }

    public static async Task<bool> WaitProjectileTime(ClientState conn, double until, int epoch)
    {
        while (true)
        {
            lock (conn.Monsters)
            {
                if (!conn.ProjectileActive(epoch) || conn.RageRetired) return false;
                if (!conn.Paused && conn.Clock.Elapsed.TotalSeconds >= until) return true;
            }
            await Task.Delay(16);
        }
    }

    // A hit on a living monster: life is mutated once under Monsters, then notifications await outside the lock. False when
    // the monster was dead already or the action was replaced (the hit did not land).
    public static async Task<bool> HitMonster(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m,
        int damage, string by, bool log = true, bool melee = false, ushort sequence = 0, int? actionToken = null,
        CooldownUseTicket? cooldownUse = null, Action? accepted = null, Func<int>? damageAtContact = null,
        List<(uint Id, SpawnedMonster M)>? deaths = null)
    {
        MonsterHit? hit;
        using (ProjectileMutation(conn))
        {
            if (conn.Closed || conn.LeavingArea || conn.RageRetired || conn.PlayerLife == 0 || conn.Paused) return false;
            if (cooldownUse is not null ? !cooldownUse.Current(conn) : actionToken is int token && !PlayerActionCurrent(conn, token)) return false;
            if (m.Dead || m.Life == 0 || !conn.Monsters.TryGetValue(id, out var current) || !ReferenceEquals(current, m)) return false;
            if (damageAtContact is not null) damage = damageAtContact();
            hit = HitUnlessBlockedLocked(conn, m, damage);
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
    // Without a time the target's chill and shock do not count: a player hit path uses MonsterHitDamage.
    public static int MonsterDamage(SpawnedMonster m, DamagePacket hit, Random random, Penetration.PerType pen) =>
        MonsterTakes(m, hit, random, spell: false, pen);

    // Resolve an already-rolled packet using its single critical draw; target damage taken follows mitigation. Without
    // the target's chill and shock: a player hit path uses MonsterHitDamage.
    public static (int Damage, string? Resistance) MonsterDamage(SpawnedMonster m, DamagePacket rolled,
        bool critical, bool attack, HitResistances.Mods mods, Penetration.PerType pen = default, bool invertElemental = false)
        => MonsterDamage(m, rolled, critical, attack, mods, pen, out _, invertElemental);

    public static (int Damage, string? Resistance) MonsterDamage(SpawnedMonster m, DamagePacket rolled,
        bool critical, bool attack, HitResistances.Mods mods, Penetration.PerType pen, out DamagePacket taken, bool invertElemental = false)
    {
        var (damage, resistance, mitigated) = MonsterHitDamage(m, rolled, critical, attack, double.NaN, mods, pen, invertElemental);
        taken = MonsterTakenComponents(m, mitigated, spell: !attack);
        return (damage, resistance);
    }

    // A player's hit as MonsterDamage, with the target's damage taken at now (its shock, Glacial Wave's chill) and the
    // resisted packet that AilmentsOnHitLocked needs: every player hit path calls both (surfcaster.md, Hit paths).
    public static (int Damage, string? Resistance, DamagePacket Mitigated) MonsterHitDamage(SpawnedMonster m, DamagePacket rolled,
        bool critical, bool attack, double now, HitResistances.Mods mods, Penetration.PerType pen = default,
        bool invertElemental = false)
    {
        var described = new List<string>();
        var strike = new HitResistances.Strike(Critical: critical, Attack: attack);
        var mitigated = rolled.Select((type, range) =>
        {
            var value = MonsterHitResistance(m, type, pen, now);
            if (rolled[type].Max > 0 && HitResistances.Describe(type, value, strike, mods) is { } note) described.Add(note);
            double effective = HitResistances.FinalValue(type, value, strike, mods, invertElemental);
            return range.Scale(Math.Max(0, 1 - effective / 100));
        });
        return (MonsterTakesMitigated(m, mitigated, spell: !attack, now),
            described.Count > 0 ? string.Join(", ", described) : null, mitigated);
    }

    public static HitResistances.Mods HitResistMods(IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int>? skill = null)
    {
        var a = HitResistances.Mods.From(player, Stats.Key);
        var b = skill is null ? default : HitResistances.Mods.From(skill, Stats.Key);
        return new(a.CriticalIgnoreElemental | b.CriticalIgnoreElemental,
            a.AttackCriticalIgnoreElemental | b.AttackCriticalIgnoreElemental,
            a.NonCriticalPenetration + b.NonCriticalPenetration, a.CannotPenetrateOrIgnore | b.CannotPenetrateOrIgnore);
    }

    // The short state mutation is shared with projectile impact; no awaits under Monsters. A minion's hit (allyAt: where
    // it hits from) is not the player's hit for his hit-recently.
    public static MonsterHit? ApplyMonsterHitLocked(ClientState conn, SpawnedMonster m, int damage,
        bool? rampageSource = null, IReadOnlyDictionary<uint, int>? player = null, (int X, int Y)? allyAt = null,
        Cull cull = default)
    {
        if (m.Dead || m.Life == 0) return null;
        uint before = m.Life;
        (m.Life, bool starts) = TakeOwnerHitLocked(conn, m, damage, cull, rampageSource, player);
        if (m.Life == 0) m.Overkill = (int)Math.Clamp((long)damage - before, 0, int.MaxValue);
        if (allyAt is null)
        {
            conn.LastEnemyHitAt = m.PlayerHitAt = ProjectileWorldTime(conn);
            conn.RecentEvents.Record(StatConditions.HitEnemy, m.PlayerHitAt);
        }
        if (m.Life == 0) { m.Aggro = false; m.Dead = true; CollectVaalSoulAtDeathLocked(conn, m); }
        TriggerElementalRelicLocked(conn, m, hit: damage > 0, killed: m.Life == 0, allyAt);
        if (allyAt is null && damage > 0 && !conn.Reservations.Secondary) ReservationHitLocked(conn, m);
        return new(m.Life, starts);
    }

    public static async Task NotifyMonsterHit(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m,
        MonsterHit hit, int damage, string by, bool log = true, bool melee = false, ushort sequence = 0,
        List<(uint Id, SpawnedMonster M)>? deaths = null, byte hitCode = 0, StunHit? stun = null)
    {
        var (life, stageStarts) = hit;
        if (life == 0)
        {
            await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]),
                !log ? null : $"0x01b3 monster 0x{id:x} hide_mini_life_bar = 1");
            if (m.Poisons.Buff is ushort pbuff)
            {
                await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.RemoveBuff(id, pbuff), null);
                m.Poisons.Buff = null;
            }
        }
        // The hit result for the client's damage/effect pipeline (0x1af): a melee hit plays the target's blood and the weapon
        // impact sound "bonk" locally, 0x1b1 stays authoritative on life. Spells and projectiles have their own impact and skip it.
        // The life update follows its hit message in the attacker's turn; one with no hit message stays where it was.
        var order = melee ? HitOrder(conn, PlayerId) : default;
        if (melee)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            var (px, py) = conn.Walk.Position(now);
            var (mx, my) = m.Walk.Position(now);
            await SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceProtocol.ActionHit(PlayerId, sequence, id, flags: 0x4201, kind: 0x03, code: hitCode, angle: HitAngle(mx - px, my - py),
                    orderKey: order.Key, checkpoint: order.Hit),
                !log ? null : $"0x01af player hit on monster 0x{id:x} (skill, sequence 0x{sequence:x4})");
        }
        // The hit reaction: one of the rig's TakeHit clips and its SoundEvent (0x1b2), as in Attack.
        await SendFlinch(remote, stream, ciphers, conn, id, m, stun ?? new(damage), log);
        await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceProtocol.LifeUpdate(id, life, orderKey: order.Key, checkpoint: order.Life),
            !log && life > 0 ? null : $"0x01b1 monster 0x{id:x} hit by {by} for {damage}, life {life}/{m.MaxLife}{(life == 0 ? " (dies)" : "")}");
        // One sweep's deaths (loot, experience, quests) wait until every monster of the sweep has its hit (AreaSkillHit).
        if (life == 0) { if (deaths is not null) deaths.Add((id, m)); else await MonsterKilled(remote, stream, ciphers, conn, id, m); }
        if (stageStarts) _ = RunBossStage(remote, stream, ciphers, conn, id, m);
        if (m.ThresholdReached) await QuestThreshold(remote, stream, ciphers, conn, id, m);
    }

    // The hit reaction: one of the rig's TakeHit clips and its SoundEvent, S→C 0x1b2 kind 0 (see Attack and HitMonster).
    // A killing blow gets none: the client's Life component runs the death instead, and the flinch would only be cut off.
    // The gates (HitReaction) keep it a stun-like reaction rather than a loop; see that class. The monster is checked again
    // inside the send gate: a hit that kills it meanwhile marks it Dead before it sends its own 0x1b1, so the flinch either
    // goes out before the death or not at all.
    public static Task SendFlinch(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        SpawnedMonster m, StunHit stun, bool log) => SendMonsterFlinch(remote, stream, ciphers, conn, id, m, stun, log);

    public sealed record PlayerRecoveryIntent(int Epoch, int Action, double Until, float Seconds);

    // Cancellation belongs to the hit boundary; the native animation is a projection of that committed interval.
    public static PlayerRecoveryIntent? CommitPlayerHitRecoveryLocked(ClientState conn, int damage, double now)
    {
        if (damage <= 0) return null;
        var stats = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        void Trace(string reason)
        {
            if (Get(AntiquarianEffects.StunRecoverySource) > 0)
                Console.WriteLine($"[gateway] Turtle Shell stun: {reason}");
        }
        // The panel publishes the per-Brutal-Charge threshold coefficient on every character; the charges held (converted
        // Endurance Charges, ChargeEffects) are already in stun_threshold_+%, so it is not an unsupported stat.
        if (stats.FirstOrDefault(s => s.Value != 0 && PlayerHitRecovery.UnsupportedStat(Stats.Name(s.Key))) is { Key: > 0 } unsupported)
        { Trace("unsupported stat " + Stats.Name(unsupported.Key)); return null; }
        // Skill immunity can depend on a native action's contact phase, which this server does not track yet.
        // Conservatively skip builds with any equipped skill carrying it, rather than stun an uninterruptible phase.
        if (GemSkillEffects(conn).Any(g => SkillsFor(conn).EffectStats(g.Effect, g.Level, g.Quality)
            .Any(s => s.Item2 != 0 && Stats.Name(s.Item1).StartsWith("skill_cannot_be_stunned", StringComparison.Ordinal))))
        { Trace("unsupported skill immunity"); return null; }
        if (PlayerHasEnergyShieldForRecovery(conn, Get)) { Trace("energy shield present"); return null; }
        // A freeze already holds the player, and its action factor would stretch the recovery a hundredfold.
        if (conn.ElementalAilments.ActionFactor(now) == 0) { Trace("frozen"); return null; }
        double chance = PlayerHitRecovery.Chance(damage, conn.PlayerMaxLife, Get);
        float seconds = PlayerHitRecovery.Duration(Get, PlayerActionFactor(conn));
        if (seconds <= 0 || Random.Shared.NextDouble() * 100 >= chance
            || conn.Closed || conn.PlayerLife == 0 || !conn.HitRecovery.Begin(now, seconds))
        { Trace($"not accepted (chance {chance:0.###}%, duration {seconds:0.###} s)"); return null; }
        Trace($"accepted ({seconds:0.###} s, {Get(AntiquarianEffects.StunRecoverySource)}% Life over one second)");
        conn.RecentEvents.Record(StatConditions.BeenStunned, now);
        conn.Antiquarian.Stunned(now, conn.PlayerMaxLife, Get(AntiquarianEffects.StunRecoverySource));
        int action = Interlocked.Increment(ref conn.ActionCount);
        conn.ChannelSkill = 0; conn.NextAttackAt = now;
        // A predictive client stops for the recovery and then goes on with its walk (live 2026-11-01: 30 cells apart when
        // this ended it). A lockstep one ends its walk in the frame the 0x1d1 reaches it (PlayerHitRecoveryRecordLocked).
        if (!conn.LockstepClient) conn.Walk.Pause(now, seconds);
        return new(conn.ProjectileEpoch, action, conn.HitRecovery.Until, seconds);
    }

    public static Task SendPlayerHitRecovery(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        PlayerRecoveryIntent recovery) => SendWorldRecord(remote, stream, ciphers, conn, PlayerId, [], "0x01d1 player TakeHit recovery", atGate: () =>
        {
            lock (conn.Monsters) return PlayerHitRecoveryRecordLocked(conn, recovery, conn.Clock.Elapsed.TotalSeconds);
        });

    // The recovery's 0x1d1, written in its send gate (under conn.Monsters). The client's TakeHit handler (0x141e9c700) ends
    // the player's current action, and on a lockstep client nothing takes the walk up again: it stops after the steps of
    // the frame the message arrives in (the deferred stop of 0x141e38420) and stands, keeping its step's fraction, until
    // its next order (tick trace 2026-11-10: 14 frames, to the held Move that followed the recovery). So the walk ends
    // here, in the frame being sent, not when the hit was committed.
    public static byte[]? PlayerHitRecoveryRecordLocked(ClientState conn, PlayerRecoveryIntent recovery, double now)
    {
        double remaining = recovery.Until - now;
        if (!conn.ProjectileActive(recovery.Epoch) || conn.PlayerLife == 0 || conn.Paused
            || conn.ActionCount != recovery.Action || remaining <= 0) return null;
        if (conn.LockstepClient) conn.Walk.Cancel(now);
        // The stun pins the player where the server has it: GGG sends the resync in the same tick as every stun.
        return [.. InstanceProtocol.ActorAnimation(PlayerId, (float)Math.Min(remaining, recovery.Seconds), PlayerHitRecovery.TakeHitAnimation),
            .. ResyncPacketLocked(conn, now)];
    }

    public static bool PlayerActionCurrent(ClientState conn, int action)
    {
        lock (conn.Monsters)
            return !conn.Closed && !conn.LeavingArea && !conn.RageRetired && conn.PlayerLife > 0
                && conn.ActionCount == action && conn.ElementalAilments.ActionFactor(conn.Clock.Elapsed.TotalSeconds) > 0 && !conn.HitRecovery.Active(conn.Clock.Elapsed.TotalSeconds);
    }

    // A hit's damage off a monster's life (under the Monsters lock): the new life, and whether the hit starts its boss stage
    // (BossFights; the stage is then taken, so it runs once).
    public static (uint Life, bool StageStarts) TakeDamageLocked(SpawnedMonster m, int damage)
    {
        // While the stage runs the boss cannot die: its life fills up at the end anyway. The humanoid King also waits for
        // the maze/treant phase before the treant form takes over; a burst cannot skip it. Its floor is 50% life (the
        // phase 2 trigger), while the treant's emergence grace refuses all damage. A Mystic Fetish is immune until the
        // King's ritual reaches it.
        if (m.Invulnerable) return (m.Life, false);
        if (m.LifePhases is { } phases) return (phases.Damage(m.Life, m.MaxLife, damage), false);
        if (m.StageRunning || m.KingPhaseLocked)
            return ((uint)Math.Max(m.KingPhaseLocked ? (long)m.MaxLife * 50 / 100 : 1,
                (long)m.Life - damage), false);
        // A quest threshold (Kitava): the hit that reaches it leaves the monster alive; it never dies.
        if (m.Threshold is { } threshold)
        {
            long left = Math.Max(1, (long)m.Life - damage);
            if (left * 100 <= (long)m.MaxLife * threshold.LifePct) m.ThresholdReached = true;
            return ((uint)left, false);
        }
        var (life, starts) = BossFights.Damage(m.Life, m.MaxLife, damage, m.StageTaken ? null : m.Stage);
        if (starts)
        {
            m.StageTaken = m.StageRunning = true;
            m.SwingGeneration = null;
            m.ActionGeneration++;   // no queued ordinary order may revive after this stage finishes
        }
        return (life, starts);
    }

    // A boss's stage (BossFights): it stops and uses its stage skill in place (0x1ab, the skill's animation: Hillock pulls
    // out his sword), then its Transitionable steps to the stage's state (0x2bc: the sword in his hand) and its life fills
    // up (0x1b1).
    public static async Task RunBossStage(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m)
    {
        var stage = m.Stage!;
        while (!m.StageActionStarted)
        {
            lock (conn.Monsters) if (!MonsterPresent(conn, id, m) || !m.StageRunning) return;
            await Send(remote, stream, ciphers, [], null, atGate: () =>
            {
                lock (conn.Monsters)
                {
                    if (!MonsterPresent(conn, id, m) || !m.StageRunning || m.StageActionStarted || conn.Paused) return null;
                    double now = AreaTime(conn);
                    var at = m.Walk.Position(now);
                    m.Walk.Order(at.X, at.Y, now);
                    m.LastTarget = null;
                    m.BusyUntil = now + stage.CastSeconds;
                    m.StageEffectAt = now + stage.EffectSeconds(m.Variety is { } variety
                        ? SkillsFor(conn).MonsterContact(variety.Id, "Throw", 0xffff) : null);
                    m.StageActionStarted = true;
                    Console.WriteLine($"[gateway] {remote} => monster 0x{id:x} ({m.Label}) starts its stage at " +
                        $"{m.Life}/{m.MaxLife} life, game time {now:0.000}; effect at {m.StageEffectAt:0.000}, recovery at {m.BusyUntil:0.000}");
                    return InstanceProtocol.ActionStarted(id, at.X, at.Y, at.X, at.Y, StageSkillId, 0xffff, conn.MonsterOrderFlags);
                }
            });
            if (!m.StageActionStarted) await AreaWait(PlayerUpdateRole.MonsterAction, 16);
        }
        await CompleteMonsterStage(remote, stream, ciphers, conn, id, m);
    }

    // A boss whose variety has the big life bar (MonsterVarieties BossHealthBar) shows it at the top of the screen once it
    // aggroes: its StateMachine's boss_life_bar (Metadata/Monsters/BossLifeBar.ot) and aggro (Hillock's aggro effect) go to 1.
    public static async Task ShowBossLifeBar(string remote, NetworkStream stream, ConnectionCiphers ciphers, uint id, SpawnedMonster m, ClientState conn)
    {
        if (m.Variety is not { } v || m.States is not { } states) return;
        int index = v.ComponentsOrBase.ToList().IndexOf("StateMachine");
        if (index < 0) return;
        states = states.Set("boss_life_bar", 1).Set("aggro", 1);
        lock (conn.Monsters) m.States = states;
        await SendWorldRecord(remote, stream, ciphers, conn, id, InstanceEntities.StateMachineUpdate(id, (byte)index, states),
            $"0x02bc monster 0x{id:x} ({m.Label}) StateMachine: boss_life_bar 1, aggro 1");
    }

    const double CorpseBlockDelaySeconds = .165;
    // How long a kill waits for its death message on a lockstep client before the monster is taken as dead in the frame
    // then being sent (a kill whose life update is never written).
    const double CorpseDeathWaitSeconds = 1;

    // Where a hit and the life it leaves go in a lockstep client's frame. GGG writes both into the attacker's own turn:
    // keyed by the attacker (or by its projectile), the hit at checkpoint 0x7000 or 0x7001 and the target's life update
    // with the same key two checkpoints up, never at the frame's start (recordings of 2026-11-04: 842 and 251 hits keyed
    // by their actor at those two checkpoints; of the hits with their target's life update in the same frame, 245 and
    // 102 have it on the hit's key at the hit's checkpoint + 2). There an actor's order always came before its own hit
    // of the same frame (475 and 24 frames, none the other way). Written at the frame's start, key 0, the player's hit
    // could come before his own order of that frame, and the client's action gate (0x141e941a0) then refuses the order
    // with its +0x238 count raised and takes it in the Actor's tick: a Move's first step came a frame late (live
    // 2026-11-10: 3 of 3 Moves written after the player's own hit, none of 2 190 other orders). A predictive client
    // applies a message on arrival and keeps key 0.
    public const ushort HitCheckpoint = 0x7000, HitLifeCheckpoint = 0x7002;
    public static (uint Key, ushort Hit, ushort Life) HitOrder(ClientState conn, uint attacker) =>
        conn.LockstepClient ? (attacker, HitCheckpoint, HitLifeCheckpoint) : default;

    // The ticks of its death clip that a corpse's body lasts on a lockstep client, the death's own frame included. The
    // client's Actor clears the body from Positioned (+0x1e5 & 8, what its path searches and step checks read) when the
    // clip reaches its remove_collidability event (0x141e9a160), and the clip gains the frame's length each tick from
    // the frame the death is applied in; a clip without the event clears it with the death (0x141e9c200). 150 ms, most
    // monsters' value, is the fifth tick. Not traced: an event exactly on a tick, and a death clip played at another
    // speed.
    public static int CorpseBodyTicks(int? unblockMs)
    {
        if (unblockMs is not { } ms) return 0;
        float at = ms / 1000f, played = 0, length = FrameMs / 1000f;
        int ticks = 0;
        while (played < at && ticks < 100_000) { played += length; ticks++; }
        return ticks;
    }

    // The client keeps routing around a corpse until its body is cleared. A predictive client is told 165 ms after the
    // kill (S→C 0x2b2, as GGG sends it). A lockstep client's copy is exact: its monster walks until the death message's
    // frame and its body lasts the death clip's ticks from there (ClientState.Corpse), so the body is kept at the kill
    // or at the death message, whichever comes first (KeepCorpseLocked), its clock starts where the death is written
    // (CorpseDeathsWritten), and the 0x2b2 follows in the frame after the client's own clear, where it changes nothing.
    // Differs from GGG: its 0x2b2 comes 4 or 5 frames after a 150 ms death, 0 after one without the event and 54 or 55
    // after Hillock's 1833 ms, by its own world's count of the clip; ours counts as the client does and sends it a frame
    // after that (5, 0 and 56 frames).
    public static void StopCorpseBlocking(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m)
    {
        bool kept;
        lock (conn.Monsters) kept = KeepCorpseLocked(remote, stream, ciphers, conn, id, m);
        if (!kept) SendAfter(CorpseBlockDelaySeconds, () => CorpseCleared(remote, stream, ciphers, conn, id, m));
    }

    // Keeps the killed monster's body on a lockstep client and starts what ends it: the wait for its death message's
    // frame and, a death clip later, the 0x2b2. Called by the kill and by the death message's own write, in whichever
    // order they come: the periodic view of the monsters can write the death a frame before the hit's own message, and
    // either can go out before the kill's bookkeeping gets here (live 2026-11-10: a corpse whose clock started with the
    // second message blocked a frame longer than the client's, and a monster standing against it took another way
    // round; two whose messages both went out first blocked for the guard's whole second). False when there is no body
    // to keep, or its death was written and its body is gone again. Under conn.Monsters.
    static bool KeepCorpseLocked(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m)
    {
        if (conn.CorpseWalks(id, m)) return true;
        int ticks = CorpseBodyTicks(m.Variety is { } variety ? Monsters.CorpseUnblockMs(variety.Id) : 150);
        if (!conn.RetainCorpseBody(id, m, ticks) || conn.CorpseDeath(id, m) is not { } death) return false;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await Task.WhenAny(death, Task.Delay(TimeSpan.FromSeconds(CorpseDeathWaitSeconds))) != death)
                    lock (conn.Monsters)
                        if (conn.LockstepFrame is { } frame && conn.CorpseAwaitsDeathOf(id))
                        {
                            StepWalkers(conn, frame.Start);
                            conn.CorpseDied(id, frame);
                        }
                if (!death.IsCompletedSuccessfully) return;
                await conn.FrameFrom(death.Result + ticks * (FrameMs / 1000d));
                await CorpseCleared(remote, stream, ciphers, conn, id, m);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
        return true;
    }

    static Task CorpseCleared(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m) =>
        SendWorldRecord(remote, stream, ciphers, conn, id, [], $"0x02b2 corpse of 0x{id:x} stops blocking", atGate: () =>
        {
            using (ProjectileMutation(conn, phase: "send"))
            {
                if (conn.Closed || conn.LeavingArea || !ReferenceEquals(conn.Monsters.GetValueOrDefault(id), m))
                {
                    conn.ReleaseCorpseBody(id, m);
                    return null;
                }
                StepWalkers(conn, ProjectileWorldTime(conn));
                conn.ReleaseCorpseBody(id, m);
                return InstanceProtocol.StopBlocking(id);
            }
        });

    // In a lockstep client's send gate, for the records about to be admitted to the frame being sent: a monster whose
    // life reaches 0 in one of them dies on the client in this frame (the client applies the 0x1b1 before the frame's
    // ticks), if this is the first such message it is written. Its corpse is kept here when the kill has not kept it
    // yet; its walk is stepped up to the frame and ends with the frame's steps, and its body's clock starts.
    public static void CorpseDeathsWritten(ClientState conn, OutputBatch batch)
    {
        if (!batch.Records.Any(record => record.LifeWritten is not null)) return;
        lock (conn.Monsters)
        {
            if (conn.LockstepFrame is not { } frame) return;
            foreach (var record in batch.Records)
            {
                if (record.LifeWritten is not { } life || record.Object is not { } target
                    || !conn.Monsters.TryGetValue(target.Id, out var m)) continue;
                var wire = conn.Wire;
                bool kept = life == 0 && !conn.DeathWritten(target.Id, m)
                    && KeepCorpseLocked(wire.Remote, wire.Stream, wire.Ciphers, conn, target.Id, m);
                conn.NoteLifeWritten(target.Id, m, life);
                if (!kept || !conn.CorpseAwaitsDeathOf(target.Id)) continue;
                StepWalkers(conn, frame.Start);
                conn.CorpseDied(target.Id, frame);
            }
        }
    }

    // A monster died (its life reached 0 from the player's attack or skill): the player gains its experience when near
    // enough and it drops its items around where it fell (ItemDrops). Its corpse stays where it fell for as long as the
    // player is in the area (it stays in Monsters as Dead: no AI, body cleared by StopCorpseBlocking); leaving does not save it.
    // A minion's kill (byMinion) is not the player's for his on-kill effects.
    public static async Task MonsterKilled(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster m,
        bool byMinion = false)
        => await MonsterKilledBy(remote, stream, ciphers, conn, id, m, playerKill: !byMinion);

    static async Task MonsterKilledBy(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        uint id, SpawnedMonster m, bool playerKill)
    {
        StopCorpseBlocking(remote, stream, ciphers, conn, id, m);
        if (SpreadContagion(conn, id, m) is { } contagion)
            await ShowContagionSpread(remote, stream, ciphers, conn, contagion.At, contagion.Radius, contagion.Ao);
        double now = conn.Clock.Elapsed.TotalSeconds;
        (int X, int Y) at, player;
        bool alive, blocked;
        lock (conn.Monsters)
        {
            at = m.Walk.Position(now); player = conn.Walk.Position(now); alive = conn.PlayerLife > 0;
            blocked = HardcoreProgressBlocked(conn);
        }
        await SendVaalSouls(remote, stream, ciphers, conn);
        // Every kill in a survivors run feeds the run's own experience, whoever landed it (survivors-mode.md).
        // A dead Hardcore character's kills (an ignite still burning) make no progress, as a projectile's (CommitProjectileKill).
        if (blocked || m.Variety is not { } variety || !m.Hostile) return;
        // A Mystic Fetish is a destructible arena object: breaking it interrupts the King's ritual and gives no
        // experience, loot or quest progress of its own.
        if (KingInMists.IsFetish(variety)) return;
        var killer = PlayerOf(conn);
        var monster = ModMonster(m, at);
        if (playerKill)
        {
            await GameEvents.RunMonsterKilled(new(conn, monster));
            if (OnslaughtOnKill(conn)) await SendKillOnslaught(remote, stream, ciphers, conn);
            ChargesOnKill(conn, m);
            RecoverOnKill(conn, m);
            // "Killed an enemy recently" (issue #504). The variant stats (a cursed enemy, a shattered one, a frozen one)
            // need a fact about the victim the server does not track yet, so only the plain event is recorded and those
            // ids stay NotModelled rather than being answered wrongly.
            bool corpseConsumed;
            lock (conn.Monsters)
            {
                now = conn.Clock.Elapsed.TotalSeconds;
                conn.RecentEvents.Record(StatConditions.KilledEnemy, now);
                corpseConsumed = ConsumeCorpseOnKillLocked(conn, id, m, now);
            }
            bool explodedCorpse = await ExplodeOnKill(remote, stream, ciphers, conn, id, m, at);
            if (corpseConsumed && !explodedCorpse)
                await Game.SendWorldRemoval(remote, stream, ciphers, conn, id,
                    $"0x02bd corpse 0x{id:x} ({m.Label}) consumed by the killing blow");
        }
        // Soul Eater eats the souls of anything killed nearby, the player's kill or not.
        SoulEaterSoul(conn, id, at);
        await SetQuestFlags(remote, stream, ciphers, conn, QuestsOf(conn).OnMonsterKilled(conn.Layout.Id, variety.Id));
        // The party's kill: every living player within the experience range takes their share
        // (Progression.PartyMultiplier), the killer included. The presence of a private area is the party (an area is
        // entered as the party's instance).
        var partyShares = PartyExperienceShares(conn, at);
        ShareKill(conn, id, m, variety, at, partyShares);
        await GameEvents.RunKill(new(conn, m, variety.Id, at) { PlayerKill = playerKill });
        // A summoned monster whose skill says so gives neither experience nor items, and so does a boss's first stage
        // (BossMods: Malachai, Shakari and Piety's escape give no experience; their own rows say so).
        if (!m.NoRewards && !BossMods.NoRewards(variety) && alive && Dist(at, player) <= ExperienceRange && Monsters.BaseExperience(variety, m.Level) is int baseXp)
        {
            var (xp, gemXp) = KillExperience(killer, monster, baseXp, variety.ExperienceMultiplier, m.Rarity,
                conn.Pools.Level, m.Level, PartyExperienceShareOf(partyShares, conn));
            // The gems gain their share of the kill only (not a quest's level or /add_xp), at the character's cap too:
            // 10% of the kill's experience before the level penalty.
            await GainGemExperience(remote, stream, ciphers, conn, gemXp);
            await GainExperience(remote, stream, ciphers, conn, xp, $"{m.Rarity} {m.Label} 0x{id:x}");
        }
        // A quest kill that levels the character up (a1q1: Hillock → at least level 2), wherever the player stands.
        if (QuestsOf(conn).LevelAfterKill(conn.Layout.Id, variety.Id) is int level and > 1 && level <= CharacterStats.LevelStarts.Count)
        {
            uint needed = CharacterStats.LevelStarts[level - 1], have;
            lock (conn.Monsters) have = conn.Experience;
            if (have < needed) await GainExperience(remote, stream, ciphers, conn, needed - have, $"quest: {m.Label} killed, level {level}");
        }
        await GainKillCharges(remote, stream, ciphers, conn, m.Rarity, $"{m.Rarity} {m.Label} 0x{id:x} killed");
        if (!m.NoRewards) await DropLoot(remote, stream, ciphers, conn, m, at);
    }

    // The monster's items (ItemDrops.Roll) as WorldItem objects scattered around where it died, on walkable cells when the
    // area's walk map is known. Like dropped items they lie in the instance (InstanceStore).
    public static async Task DropLoot(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, SpawnedMonster m, (int X, int Y) at)
    {
        if (Environment.GetEnvironmentVariable("Game_DROPS") == "0") return;
        // A stage of a boss that gives no items (Malachai's first form, Abberath, Shavronne's tower, Maligaro's souls).
        if (BossMods.GivesNoItems(m.Variety)) return;
        // A survivors run keeps the ground clear: no loot, no gold, nothing reaches the client.
        if (!GameEvents.AllowsLoot(conn)) return;
        var balance = LeagueFor(conn).Rules;
        // The mods' changes (Program.Mods.cs): their multipliers on top of the league's, their own items besides.
        var modded = GameEvents.RunDrops(conn, ModMonster(m, at));
        List<ItemDrops.Drop> drops = [];
        // The King in the Mists always drops one of four uniques, plus its support gems and card. Its humanoid form is
        // removed without a death, so only the treant (Boss3) reaches here.
        if (KingInMists.IsBoss3(m.Variety))
            drops.AddRange(DropsFor(conn).KingInMists(m.Level, Random.Shared));
        if (modded is not { Cancel: true })
        {
            if (m.Rarity == MonsterRarity.Unique && m.Variety is { } boss)
                drops.AddRange(DropsFor(conn).EncounterJewels(boss.Id, m.Level, Random.Shared, DropDataProvider.Default.Current));
            // A generic map drop (approximate rates): use the open map's tier in a map, tier 1 in the campaign. The same
            // tier lets the rolls add their base card/scarab drops in a map.
            int mapTier = MapDevice.OpenOf(CharacterName(conn)) is { } openMap && openMap.Area.Hash16 == conn.Layout.Hash
                ? openMap.Area.Tier : 0;
            drops.AddRange(DropsFor(conn).Roll(m.Level, m.Rarity, Random.Shared, IncreasedItemRarity(conn, m),
                balance.ItemDropMultiplier * (modded?.Quantity ?? 1) * ItemQuantityFactor(conn, m), balance.EquipmentRarityMultiplier * (modded?.Rarity ?? 1), mapTier));
            drops.AddRange(PartyCurrency(conn, drops, Random.Shared));
            if (DropsFor(conn).Map(m.Level, m.Rarity, mapTier, Random.Shared) is { } mapDrop) drops.Add(mapDrop);
            // And a pile of gold (ItemDrops.Gold), picked up into the stash's gold.
            if (DropsFor(conn).Gold(m.Level, m.Rarity, Random.Shared,
                balance.GoldMultiplier * SystemSettings.Default.GoldDropMultiplier * (modded?.Gold ?? 1)) is { } gold) drops.Add(gold);
            drops.AddRange(ExtraDropsFromMap(conn, m.Level, m.Rarity, Random.Shared));
        }
        foreach (string query in modded?.Items ?? [])
        {
            if (ModDrop(conn, query, m.Level) is { } extra) drops.Add(extra);
            else Console.WriteLine($"[mods] a mod's drop \"{query}\" is no item type or unique the server can create");
        }
        foreach (var drop in ItemDrops.Stacked(drops))
        {
            // A ring around the corpse, 3 to 6 cells out.
            double angle = Random.Shared.NextDouble() * Math.Tau, r = 3 + Random.Shared.NextDouble() * 3;
            int x = at.X + (int)Math.Round(Math.Cos(angle) * r), y = at.Y + (int)Math.Round(Math.Sin(angle) * r);
            if (conn.Layout.Walkable is { } walks && !walks(x, y)) (x, y) = at;
            var ground = new WorldItemObject(x, y, drop.Item);
            uint id = conn.Instance.Ground.Add(ground, conn);
            await SendGroundItem(remote, stream, ciphers, conn, id, ground,
                $"0x02bb create world item 0x{id:x} {drop.Type.Name} (item level {drop.Item.ItemLevel}) dropped by {m.Label} at ({x}, {y})", justDropped: true);
        }
    }
}
