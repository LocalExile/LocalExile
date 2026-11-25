using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using WalkEstimator = GameServer.Core.World.WalkEstimator;
using Progression = GameServer.Core.World.Progression;
using DamageModel = GameServer.Core.World.DamageModel;
using TargetAccuracy = GameServer.Core.World.TargetAccuracy;
using DamageType = GameServer.Core.World.DamageType;
using DamageTypes = GameServer.Core.World.DamageTypes;
using DamagePacket = GameServer.Core.World.DamagePacket;
using DamageRange = GameServer.Core.World.DamageRange;
using HitResistances = GameServer.Core.World.HitResistances;
using RandomRolls = GameServer.Core.World.RandomRolls;

public static partial class Game
{
    // Record the first paid use of a channel before asynchronous output. Under conn.Monsters.
    public static int BeginPaidChannel(Player player, ushort skill, int effect, int action, ushort sequence)
    {
        int generation = ++player.ChannelGeneration;
        player.ChannelSkill = skill;
        player.ChannelSkillSequence = sequence;
        player.LastChannelUpdateAt = AreaTime(player);
        if (player.Connection.LockstepClient) player.LockstepSkill.Channelling(sequence);
        player.PaidChannel = CombatLifetime is { IsActive: true } rules
            ? new(skill, effect, action, generation, rules) { StartedAt = player.LastChannelUpdateAt } : null;
        return generation;
    }

    // Sample both moving actors at the hit time, after the approach and contact delay.
    public static (int Accuracy, bool Unique, bool CloseRange) AttackAccuracyAt(
        Player player, SpawnedMonster target, double at)
    {
        bool unique = target.Rarity == MonsterRarity.Unique;
        bool close = TargetAccuracy.InCloseRange(Dist(player.Walk.Position(at), target.Walk.Position(at)));
        return (TargetAccuracy.AgainstTarget(PlayerAccuracy(player), player.Pools.AccuracyMoreVsUniquePct,
            player.Pools.AccuracyMoreInCloseRangePct, unique, close), unique, close);
    }

    // Both accuracy checks use the same target context. Only the first check consumes the defender's entropy.
    public static int AttackHitChanceAt(Player player, uint id, SpawnedMonster target, double at,
        AttackAccuracyProfile profile, IReadOnlyDictionary<uint, int>? playerStats = null)
    {
        int evasion = target.Effects.Evasion(Monsters.BaseEvasion(target.Variety, target.Level) ?? 0);
        evasion = MonsterBlindRating(player, id, target, evasion, at, playerStats);
        bool close = TargetAccuracy.InCloseRange(Dist(player.Walk.Position(at), target.Walk.Position(at)));
        return profile.Chance(evasion, target.Rarity == MonsterRarity.Unique, close);
    }

    // A ground ranged shot reserves pacing, its action token and stationary walk together with the input-time gate.
    public static bool TryBeginRangedAttack(Player player, double requestedAt, double duration,
        ushort skill, ushort sequence, out (int Action, int X, int Y, SkillCriticalRoll CriticalRoll) started)
    {
        started = default;
        using (ProjectileMutation(player))
        {
            double now = ProjectileWorldTime(player);
            if (player.Closed || player.LeavingArea || player.RageRetired || player.Paused || player.PlayerLife == 0
                || PlayerActionBlocked(player, requestedAt, now)
                || !AttackTiming.Ready(now, player.NextAttackAt, duration)) return false;
            requestedAt = Math.Max(requestedAt, player.ProjectileRuntime?.Runtime.Now / 120000d ?? requestedAt);
            player.NextAttackAt = AttackTiming.After(now, player.NextAttackAt, duration);
            int action = Interlocked.Increment(ref player.ActionCount);
            var (x, y) = player.Walk.Position(requestedAt);
            player.Walk.Order(x, y, requestedAt);
            player.ChannelSkill = skill;
            player.ChannelSkillSequence = sequence;
            started = (action, x, y, CaptureSkillCriticalRoll(player, skill));
            return true;
        }
    }

    // Whether an attack may start now: the shared attack timer (the default attack and the attack gem skills) is over, or
    // the use is no more than a quarter of an attack early (the client's jitter). Does not reserve the timer: an attack used
    // on a monster waits for the walk into reach, and CastGemSkill reserves it when the swing starts.
    public static double SkillAttackSeconds(Player player, int effect, int level) =>
        Math.Max(TickSeconds, SkillAttackTiming.ForSkill(SkillsFor(player), effect, level, WeaponFor(player),
            CharacterPlayerStats(player), actionSpeed: PlayerActionFactor(player)));

    public static bool AttackReady(Player player, ushort skillId, int effect, int level, out double seconds, ushort sequence = 0)
    {
        // The supported skill's own speed includes its gem/support inputs; the generic fallback is for callers without
        // a skill id (ordinary weapon timing). Keep the gate and the interval CastGemSkill reserves on the same value:
        // both take the use's hand (AttackHands, from its sequence).
        seconds = skillId != 0
            ? (ShieldWeapon(player, skillId) is { } shieldWeapon
                ? SupportedOffHandAttackSeconds(player, skillId, shieldWeapon)
                : SupportedAttackSeconds(player, skillId, sequence))
            : SkillAttackSeconds(player, effect, level);
        double now = LockstepScheduledAt(player, AreaTime(player));
        return AttackTiming.Ready(now, player.NextAttackAt, seconds);
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

    // Encodes the hit-size variation (0..2) into bits 3..4 of 0x1af's code byte (DamageParticleEffects Variation:
    // e.g. Fire 2 -> default_fire_damage.pet, 10 -> default_fire_damage2.pet, 18 -> default_fire_damage3.pet).
    public static byte HitCodeWithVariation(byte baseCode, int damage, uint maxLife, bool heavy = false)
    {
        int variation = heavy || (maxLife > 0 && damage * 100L >= (long)maxLife * 40) ? 2
            : maxLife > 0 && damage * 100L >= (long)maxLife * 15 ? 1
            : 0;
        return (byte)((baseCode & 7) | (variation << 3));
    }

    public static byte HitCode(GameServer.Core.World.DamagePacket packet, int damage, uint maxLife, bool heavy = false) =>
        HitCodeWithVariation(HitCode(packet), damage, maxLife, heavy);

    // The 0x1af primary damage-type flag bit (0x0001 Physical, 0x0002 Cold, 0x0004 Fire, 0x0008 Lightning, 0x0010 Chaos),
    // which selects the client's elemental impact sound & damage particle path (FUN_141602cd0).
    public static ushort HitTypeFlag(byte hitCode) => (ushort)(1 << (hitCode & 7));

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
    public static double AttackContactDelay(Player player, double attackSeconds, bool approach)
    {
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject;
        double contact = Animations.AttackContact(ao, AnimationWeaponProfileFor(player));
        return AttackTiming.ContactDelay(attackSeconds, contact, approach ? HitContactOffsetSeconds() : 0);
    }

    // A gem melee attack's contact: the exact single clip its ACT stance names (animation_skill_clips: Heavy Strike), then
    // the skill's own rig clip when the data has one (Ground Slam's groundslam_maul, 0.633
    // of the attack), else the class's attack1 keyframe (AttackContactDelay: 0.692 for the Str rig's attack1_maul, the
    // default attack's own swing). The clip is taken to stretch to the attack time, as the client plays it, so the
    // server's hit lands where the client's weapon visually hits. clip/fraction come back for the sweep's log.
    public static double GemAttackContactDelay(Player player, int effect, double attackSeconds,
        out string? clip, out double fraction)
    {
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject;
        var weapon = AnimationWeaponProfileFor(player);
        // A transfigured gem (Id + AltX / AltY) swings its base gem's clip: Holy Sweep of Hammerfalls (SweepAltX) shares
        // Sweep's Animation row (173) and its sweep_maul, whose contact is 350 of 1375 ms; without it the hit fell back
        // to attack1's 0.69 and landed long after the swing (live 2026-11-09).
        if (SkillsFor(player).EffectId(effect) is { } effectId
            && (Animations.ActAttackContact(ao, weapon, effectId)
                ?? Animations.SkillAttackContact(ao, weapon, SkillClipName(effectId))
                ?? (TransfiguredBase(effectId) is { } baseId ? Animations.SkillAttackContact(ao, weapon, SkillClipName(baseId)) : null)) is { } found)
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
    // A transfigured gem's base GrantedEffects Id ("SweepAltX" → "Sweep"), null for any other.
    public static string? TransfiguredBase(string effectId) =>
        effectId.Length > 4 && (effectId.EndsWith("AltX", StringComparison.Ordinal) || effectId.EndsWith("AltY", StringComparison.Ordinal))
            ? effectId[..^4] : null;

    public static string SkillClipName(string effectId) =>
        new([.. effectId.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    public static (double Gap, bool Keyframe) GemAttackStrikeGap(Player player, string? contactClip)
    {
        if (contactClip is not null)
        {
            string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject;
            var weapon = AnimationWeaponProfileFor(player);
            if (Animations.SkillAttackContactAt(ao, weapon, contactClip, 0) is { } first
                && Animations.SkillAttackContactAt(ao, weapon, contactClip, 1) is { } second && second.Contact > first.Contact)
                return (second.Contact - first.Contact, true);
        }
        return (0.5, false);
    }

    // PlayerActionCurrent's state half, without its action-token test: a gem melee swing keeps its hit when the player
    // only walks on (AreaSkillHit's holdsItsSwing), while a death, an area change, a retirement, a pause, a freeze or a
    // stun's hit recovery still ends it.
    public static bool PlayerStateAllowsAction(Player player)
    {
        return !player.Closed && !player.LeavingArea && !player.RageRetired && player.PlayerLife > 0 && !player.Paused
            && player.ElementalAilments.ActionFactor(AreaTime(player)) > 0
            && !player.HitRecovery.Active(AreaTime(player));
    }

    // The default attack's contact: its stance clip as the client plays it (DefaultAttackClip.ContactSeconds: a one-hand
    // sword's lands 0.56 s into a 0.645 s swing, not at the attack1 keyframe's 0.45). The walk-in compensation comes
    // from the actual approach result, rather than adding walk-in latency to a stationary swing; a lockstep client walks
    // up on the server's start as the estimate does (no lead to compensate), and the server paces its repeats, so no
    // early use cancels the hit.
    public static double DefaultAttackContactDelay(Player player, double attackSeconds,
        (int X, int Y) from, (int X, int Y) stop, double departedAt, double arrivedAt, ushort sequence)
    {
        string ao = Environment.GetEnvironmentVariable("Game_PLAYER_AO") ?? SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject;
        double contact = Animations.DefaultAttackContactSeconds(ao, AnimationWeaponProfileFor(player), sequence, attackSeconds) / attackSeconds;
        bool approach = !player.Connection.LockstepClient && arrivedAt > departedAt && stop != from;
        return AttackTiming.ContactDelay(attackSeconds, contact, approach ? HitContactOffsetSeconds() : 0, early: !player.Connection.LockstepClient);
    }

    // The player's predictive walk-up retry timer (0x141e95400: Actor +0x278 = 0.05 s for Player;
    // 0x141e957f0 retries the pending walk-up in mode 1 when it expires). The action has no pursuit deadline.
    // A lockstep walk-up runs on its frames (StepWalkUp).
    const double SwingFollowSeconds = .05;

    // The projectile grid value a ranged attack's line to its target must hold, the action's precision for the walk-up and
    // the swing (0x141e95400 → MoveTo +0x528): 3 for the bow's and the wand's default attack, 1 for a one-hand sword's
    // and for a Move (live 2026-11-03, the client's searches). Attack gems' are taken to be the bow's.
    const int RangedPrecision = 3;

    static int AttackPrecision(Player player) =>
        OrdinaryRangedAttack.ProjectileId(WeaponProfileFor(player, activeSet: true)) is not null ? RangedPrecision : 1;

    // The walk-up's own goal, as the client's action tests it before the swing: in reach, the monster's size (StopRange - 2,
    // its ObjectSize - 1) widening it, with the line to the monster's body on the projectile grid at the order's precision.
    static bool SwingInReach(Player player, SpawnedMonster m, (int X, int Y) from, (int X, int Y) target, double reach) =>
        player.Walk.InReachOf(from, target, (int)reach, m.StopRange - 2);

    // The walk-up's max range: the weapon's range less one, as the client's action gives its MoveTo (0x141e95400: max
    // range +0x168() − 1, min range +0x158() − 1, each from 1 when 0; tolerance +0x180, precision +0x170; live
    // 2026-11-03: max range 10 for the Rusted Sword's 11, 119 for a bow's 120), as a monster's chase walk-up takes it.
    // The swing's hit still tests the whole range (+ 2).
    static int WalkUpRange(SkillCatalog.Unarmed weapon) => Math.Max(weapon.Range, 1) - 1;

    // Effective melee swing reach in cells: weapon/unarmed base range plus character and skill melee strike range bonuses.
    public static int MeleeSkillReach(Player player, SkillCatalog.Unarmed weapon, int effect,
        IReadOnlyDictionary<uint, int>? skillStats = null)
    {
        if (!weapon.Melee) return weapon.Range;
        var ps = CharacterPlayerStats(player);
        int Val(uint k) => ps.GetValueOrDefault(k) + (skillStats?.GetValueOrDefault(k) ?? 0);
        int bonus = Val(Stat.MeleeRangePlus) + Val(Stat.MeleeWeaponRangePlus);
        string cls = weapon.ItemClass ?? "";
        if (cls is "Staff" or "Warstaff") bonus += Val(Stat.MeleeRangePlusWithStaff);
        else if (cls is "One Hand Axe" or "Two Hand Axe") bonus += Val(Stat.MeleeRangePlusWithAxe);
        else if (cls is "One Hand Sword" or "Thrusting One Hand Sword" or "Two Hand Sword") bonus += Val(Stat.MeleeRangePlusWithSword);
        else if (cls is "One Hand Mace" or "Sceptre" or "Two Hand Mace") bonus += Val(Stat.MeleeRangePlusWithMace);
        else if (cls is "Dagger" or "Rune Dagger") bonus += Val(Stat.MeleeRangePlusWithDagger);
        else if (cls is "Claw") bonus += Val(Stat.MeleeRangePlusWithClaw);
        if (cls.StartsWith("Two Hand", StringComparison.Ordinal) || cls is "Staff" or "Warstaff")
            bonus += Val(Stat.MeleeRangePlusWithTwoHanded);
        else if (!string.IsNullOrEmpty(cls))
            bonus += Val(Stat.MeleeRangePlusWithOneHanded);
        if (OffhandWeaponFor(player) is not null) bonus += Val(Stat.MeleeRangePlusWhileDualWielding);
        else if (ActiveOffHand(player)?.Type?.Class == "Shield") bonus += Val(Stat.MeleeRangePlusWhileWieldingShield);
        return Math.Max(1, weapon.Range + bonus);
    }

    static double MeleeAreaSwingReach(Player player, SkillCatalog.Unarmed weapon, int effect,
        IReadOnlyDictionary<uint, int> skillStats, int swingReach)
    {
        double reach = swingReach + 2;
        if (!weapon.Melee || !SkillsFor(player).IsArea(effect) || SkillsFor(player).IsStrike(effect)) return reach;
        int radius = SkillRadius(player, skillStats, spell: false, effect);
        string? effectId = SkillsFor(player).EffectId(effect);
        double areaReach = SkillsFor(player).ActionHash(effect) == GroundSlamWave.Action
            ? GroundSlamWave.Length(radius, skillStats.GetValueOrDefault(Stat.ActiveSkillBaseRadiusPlus))
            : effectId is "IceCrash" or "IceCrashAltX" or "IceCrashAltY"
                ? SkillRadius(player, skillStats.GetValueOrDefault(Stat.ActiveSkillBaseTertiaryAreaOfEffectRadius) is > 0 and var t ? t : 31,
                    skillStats, spell: false, effect) + swingReach
                : radius + swingReach;
        return Math.Max(reach, areaReach);
    }

    // A predictive client's walk-up to a monster for a swing at it, from where the estimate has the player at `now`. Swing:
    // the swing starts at once, when MoveTo finds its goal where the player stands (0x141e81c20 → 0x141e81950). A walk-up
    // with steps swings once it is over at a cell in reach. A lockstep client's is PlaceLockstepAttack's.
    public static ((int X, int Y) Here, (int X, int Y) Stop, double At, bool Swing) WalkUpToSwing(Player player,
        SpawnedMonster m, (int X, int Y) target, SkillCatalog.Unarmed weapon, double now, int precision)
    {
        int reach = WalkUpRange(weapon);
        var (here, stop, at) = player.Walk.Approach(target.X, target.Y, reach, now, tolerance: m.StopRange - 2, precision);
        return (here, stop, at, SwingInReach(player, m, here, target, reach) || at <= now && SwingInReach(player, m, stop, target, reach));
    }

    // Walks up to a monster until it is in reach, whenever it moves or bodies cut the way: the client's action keeps going
    // until then, whatever the first path said, and its tick paths it again. A walk-up that swings at once (WalkUpToSwing's
    // Swing) returns at once. A lockstep client's walk-up is the step pass's (LockstepAttackStart), and this only waits for
    // its decision. Returns where the swing is swung from, when the walk-up got there and when the swing began (on a
    // lockstep client both the start of the frame it began in); null when the walk-up failed or another action replaced it.
    public static async Task<((int X, int Y) Stop, double Arrive, double Begun)?> FollowMonsterToSwing(Player player,
        SpawnedMonster m, SkillCatalog.Unarmed weapon, Func<bool> current, Func<double, double, Task<bool>> until,
        (int X, int Y) stop, double arrive, bool swing, double now, int precision = 1, LockstepAttack? lockstep = null)
    {
        if (lockstep is not null)
        {
            // Woken by the step pass's decision, or every 16 ms to see whether another action took over.
            while (!lockstep.Decided.IsCompleted && current() && !player.Closed && !player.LeavingArea)
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
            using (ProjectileMutation(player))
            {
                now = ProjectileWorldTime(player);
                if (!current()) return null;
                var target = m.Walk.Position(now);
                bool done = m.Life == 0;
                if (now >= arrive && (done || SwingInReach(player, m, stop, target, reach))) return (stop, arrive, now);
                if (done) continue;
                (_, stop, arrive, swing) = WalkUpToSwing(player, m, target, weapon, now, precision);
                if (swing) return (stop, arrive, now);
            }
        }
    }

    // An attack gem used at the ground out of the weapon's reach (an attack-move): the client's action walks up to the
    // weapon's range less one of the aim and swings there by itself, with no word to the server (07 §5: an aim 17 cells
    // off with range 10 walked 8 and 5 cells, then swung, near a monster and on empty ground). Without this the server
    // only walked, and that swing hit nothing. On the estimate's arrival, if no other action replaced the walk, the server
    // swings at the aim as CastOn does after its walk-up: it pays, echoes the swing from where it stands and runs the skill.
    public static async Task SwingAtGroundOnArrival(Player player,
        InstanceProtocol.UseSkillAtLocation use, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem,
        int action, double arrive)
    {
        try
        {
            while (true)
            {
                if (player.Closed || player.LeavingArea || !PlayerActionCurrent(player, action)) return;
                if (!player.Paused && AreaTime(player) >= arrive) break;
                await AreaDelay(player, 0.016);
            }
            if (!AttackReady(player, use.SkillId, gem.Effect, gem.Level, out _, use.Slot)) return;
            (int X, int Y) aim = (use.X, use.Y);
            (int X, int Y) stop = default;
            var groundWeapon = WeaponFor(player);
            var groundStats = SupportedSkill(player, use.SkillId).Stats;
            int groundSwingReach = MeleeSkillReach(player, groundWeapon, gem.Effect, groundStats);
            double groundAdmitReach = MeleeAreaSwingReach(player, groundWeapon, gem.Effect, groundStats, groundSwingReach);
            // Not in reach of the aim (no path, an aim off the map, a body in the way): the client's action ends without
            // a swing, so nothing is paid or echoed, as CastOn's SwingInReach refuses it for a monster. The reach is the
            // weapon's range plus the same 2 cells of slack CastOn and Attack give; a normal arrival stops at range - 1.
            if (!TryBeginSkillCooldownUse(player, use.SkillId, gem.Effect, CooldownForSkill(player, use.SkillId), false,
                () => stop = player.Walk.Position(ProjectileWorldTime(player)), out var cooldownUse, actionToken: action,
                admit: () => !GroundAttackWalks(player.Walk.Position(ProjectileWorldTime(player)), aim, groundAdmitReach)))
                return;
            bool projectileRuntime = UsesProjectileRuntime(gem.Effect);
            if (projectileRuntime) _ = CastGemSkill(player, use.SkillId, stop, aim,
                sequence: use.Slot, actionToken: action, cooldownUse: cooldownUse);
            bool acknowledged = false;
            await SendWorldRecords(player, player.Id, () => [InstanceProtocol.ActionStarted(player.Id, stop.X, stop.Y,
                aim.X, aim.Y, use.SkillId, use.Slot, use.Flags), InstanceProtocol.ActionEnded(player.Id)],
                $"0x01ab player skill 0x{use.SkillId:x4} {gem.Name} at ({aim.X}, {aim.Y}) from ({stop.X}, {stop.Y}) after its walk-up (t={AreaTime(player):0.000})",
                still: () => acknowledged = SkillAcknowledgementAllowed(player, gem.Effect, use.SkillId,
                    () => cooldownUse?.CommitNativeUse(player, use.SkillId) ?? PlayerActionCurrent(player, action)));
            if (acknowledged) await SendNativeCooldownRefresh(player);
            if (!projectileRuntime && acknowledged) _ = CastGemSkill(player, use.SkillId, stop, aim,
                sequence: use.Slot, actionToken: action, cooldownUse: cooldownUse);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    // The player uses a skill on a monster (C→S 0x24, e.g. the default attack on a left click). The client walks into range
    // first, so the server walks its estimate too (WalkUpToSwing, reach = the weapon's MaxAttackDistance in cells less one,
    // centre to centre) and only then starts the swing: it echoes the action aimed at the monster (0x1ab, flags | 0x80 +
    // target id) and, at the contact point (AttackContactDelay: the attack animation's contact keyframe), lowers the monster's life (0x1b1) by the unarmed damage
    // (Characters.dat MinDamage..MaxDamage raised by the increased melee physical damage stats). Any later action (a move, another click) cancels both.
    // One attack per attack time (WeaponSpeed ms scaled by the player's attack speed stats, a quarter early allowed for
    // jitter); earlier repeats of 0x24 are ignored. At 0 the client's Life component runs its death; the corpse stays.
    public static async Task Attack(Player player,
        InstanceTransfer.UseSkillOnObject use, SpawnedMonster m, double requestedAt)
    {
        if (m.OwnerId is not null) return;
        int projectileEpoch;
        projectileEpoch = player.ProjectileEpoch;
        // Dual wielding: this use's hand (AttackHands, from the client's sequence) gives its weapon, time and hit.
        var hand = DefaultAttackHand(player, use.Sequence);
        var weapon = HandWeapon(player, hand);
        var weaponContactSource = SkeletonWeaponContactSource(player, hand, weapon.LocalSources);
        string handNote = HandNote(player, hand);
        string? ordinaryProjectile = OrdinaryRangedAttack.ProjectileId(WeaponProfileFor(player, activeSet: true));
        bool ranged = ordinaryProjectile is not null;
        if (weapon.Melee && !ranged)
            weapon = weapon with { Range = MeleeSkillReach(player, weapon, effect: -1) };
        int precision = ranged ? RangedPrecision : 1;
        double now = AreaTime(player), duration;
        duration = Math.Max(TickSeconds,
            DefaultAttackSeconds(player, hand, CharacterPlayerStats(player)) / PlayerActionFactor(player));
        // The client sends one 0x24 per swing while the button is held; jitter makes the next one arrive a little before a
        // full attack time, so accept it up to a quarter of an attack early.
        if ((ranged && player.Paused) || !AttackTiming.Ready(now, player.NextAttackAt, duration))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0024 skill 0x{use.SkillId:x4} seq 0x{use.Sequence:x4} ignored: {player.NextAttackAt - now:0.000} s before the next attack");
            return;
        }
        int action;
        (int X, int Y) target, here, stop;
        double arrive;
        bool atOnce;
        double departedAt;
        using (ProjectileMutation(player))
        {
            now = ProjectileWorldTime(player);
            departedAt = now;
            if (player.Closed || player.LeavingArea || player.RageRetired || player.PlayerLife == 0
                || PlayerActionBlocked(player, requestedAt, now)) return;
            action = Interlocked.Increment(ref player.ActionCount);
            target = m.Walk.Position(now);
            // A lockstep client's walk-up is placed where its 0x1ab goes out (LockstepAttackStart).
            if (player.Connection.LockstepClient) (here, stop, arrive, atOnce) = (player.Walk.Position(now), player.Walk.Position(now), now, false);
            else (here, stop, arrive, atOnce) = WalkUpToSwing(player, m, target, weapon, now, precision);
        }
        // "no path": no cell in reach with the line to the monster was found (nor the line from here at precision 1), so the
        // estimate stands; the swing then waits for the monster to come into reach (FollowMonsterToSwing) or is called off.
        string walkUp = arrive <= now && !atOnce ? " (no path)" : "";
        if (!player.Connection.LockstepClient)
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0024 skill 0x{use.SkillId:x4} seq 0x{use.Sequence:x4} on 0x{use.TargetId:x}: player ({here.X}, {here.Y}), " +
                $"monster ({target.X}, {target.Y}), distance {Dist(here, target):0.0}; swing from ({stop.X}, {stop.Y}) in {arrive - now:0.00} s{walkUp}, attack time {duration:0.000} s");
        // A lockstep client's action starts now and walks up by itself (Lockstep.cs); the swing below then says nothing.
        var lockstep = await LockstepAttackStart(player, use, m, weapon, precision, action, "attack");
        bool started = lockstep is not null;
        bool Current() => player.ActionCount == action && (!ranged || OrdinaryRangedReleaseCurrent(player, action, projectileEpoch));
        async Task<bool> Until(double at, double from)
        {
            if (ranged) return await WaitProjectileTime(player, at, projectileEpoch);
            await AreaDelay(player, at - from);
            return true;
        }
        if (await FollowMonsterToSwing(player, m, weapon, Current, Until, stop, arrive, atOnce, now, precision, lockstep) is not { } swing)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} attack on 0x{use.TargetId:x} cancelled before the swing");
            player.Connection.Performance.Count("use cancelled before the swing");
            LockstepAttackOver(player, action);
            return;
        }
        (stop, arrive, double begun) = swing;
        now = AreaTime(player);
        // A lockstep client's swing began at the start of its goal frame (LockstepAttackSwings), tested against the monster's
        // cell then; the hit and the next attack count from there.
        if (!started) begun = now;
        SkillCriticalRoll criticalRoll;
        bool inReach;
        target = m.Walk.Position(begun);
        // The swing holds a lockstep player from when it began until the client lets a Move cut it short
        // (LockstepAttackSwings); the next attack waits the attack time (NextAttackAt). On a lockstep client the
        // next attack, the hit and the release take the attack time the swing began with, as the hold does.
        if (started) duration = LockstepAttackSeconds(player, use.SkillId, use.Sequence);
        player.NextAttackAt = AttackTiming.After(begun, player.NextAttackAt, duration);
        criticalRoll = CaptureSkillCriticalRoll(player, SkillCatalog.FirstSkillId);
        inReach = SwingInReach(player, m, stop, target, weapon.Range + 2);
        LockstepAttackOver(player, action);
        if (!inReach)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} attack on 0x{use.TargetId:x} out of reach: {Dist(stop, target):0.0} > {weapon.Range}");
            return;
        }
        bool cryExerted;
        RecordUsedSkillLocked(player, begun);
        cryExerted = ConsumeIntimidatingCryExertLocked(player, weapon.Melee && !ranged);
        PhaseRunOnSkillUse(player, use.SkillId, duration);
        SpiritBurstOnSkillUse(player, target, begun);
        // As GGG does for every action but Move: the client counts unanswered actions and would ask for a resync.
        byte[][] actionRecords = [InstanceProtocol.ActionStarted(player.Id, stop.X, stop.Y, target.X, target.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId), InstanceProtocol.ActionEnded(player.Id)];
        string actionLog = $"0x01ab player skill 0x{use.SkillId:x4} on monster 0x{use.TargetId:x} ({m.Label}, life {m.Life}/{m.MaxLife}), attack time {duration:0.000} s{handNote}";
        if (ordinaryProjectile is { } projectileId)
        {
            // The runtime schedules the release first, anchored to this acceptance (a lockstep client's to its swing's
            // start, as the melee hit is); the echo follows through the gate.
            await LaunchOrdinaryRanged(player, target, duration, action, projectileEpoch, projectileId, use.Sequence,
                criticalRoll, swingAt: started ? begun : null);
            if (!started && !await SendOrdinaryRangedAction(player, actionRecords, actionLog, action, projectileEpoch)) return;
        }
        else if (!started) await SendWorldRecords(player, player.Id, () => actionRecords, actionLog, still: () => PlayerActionCurrent(player, action));
        if (m.Life == 0) return;
        // A ranged target is an aim, not a reserved hit: the released arrow/bolt owns collision and damage.
        if (ranged) return;
        // A Strike's spirit strikes and previous-location strike are chosen as the swing starts, as the client chooses them
        // (StrikeTargets): the default attack is a Strike.
        int attackEffect = SkillEffectId(player, SkillCatalog.FirstSkillId) is { } attackId ? SkillsFor(player).EffectRow(attackId) : -1;
        var ownPenetration = attackEffect >= 0
            ? SkillsFor(player).EffectStats(attackEffect, 1).ToDictionary(kv => kv.Item1, kv => kv.Item2) : null;
        List<uint> spirit;
        (int X, int Y)? echo;
        byte[]? memoryBuff;
        using (ProjectileMutation(player))
            (spirit, echo, memoryBuff) = StrikeExtrasLocked(player, use.SkillId, attackEffect, ownPenetration ?? new Dictionary<uint, int>(), stop, target,
                use.Sequence, use.TargetId, ProjectileWorldTime(player));
        if (memoryBuff is not null)
            await SendWorldRecord(player, player.Id, memoryBuff, $"0x01c1 player buff skill_location_memory for skill 0x{use.SkillId:x4}");
        // The hit lands at the attack animation's contact keyframe (data/animation-contacts.json), not a fixed half: the
        // client stretches the clip to the attack time, so the blood and the weapon impact sound line up with the swing.
        double contact = DefaultAttackContactDelay(player, duration, here, stop, departedAt, arrive, use.Sequence);
        await AreaDelay(player, started ? Math.Max(0, begun + contact - AreaTime(player)) : contact);
        await WhilePaused(player);
        // One use, one Fortification and one Rage gain (as one AreaSkillHit sweep), whichever of its hits lands first.
        bool fortified = false, raged = false;
        if (!await HitTarget(use.TargetId, m, target)) return;
        List<(uint Id, SpawnedMonster M, (int X, int Y) At)> extras;
        double now2 = AreaTime(player);
        extras = [.. spirit.Where(id => player.Area.Monsters.TryGetValue(id, out var s) && !s.Dead && s.Life > 0)
            .Select(id => (id, player.Area.Monsters[id], player.Area.Monsters[id].Walk.Position(now2)))];
        // The previous location's strike: the monster nearest it within the weapon's reach (a single-target hit, as the
        // default attack's own).
        if (echo is { } echoAt && player.Area.MonstersNear(echoAt, weapon.Range).Where(e => e.Key != use.TargetId && !spirit.Contains(e.Key) && e.Value.Hostile
                && !e.Value.Dead && e.Value.Life > 0 && Dist(e.Value.Walk.Position(now2), echoAt) <= weapon.Range + LivingCollision.Size(e.Value))
            .OrderBy(e => Dist(e.Value.Walk.Position(now2), echoAt)).Select(e => (e.Key, e.Value)).FirstOrDefault() is (var echoId, { } echoed))
            extras.Add((echoId, echoed, echoed.Walk.Position(now2)));
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
            byte hitCode = 0;
            bool impaled, critical;
            string? resistance;
            List<byte[]>? ignited = null, bled = null;
            string igniteLog = "", bleedLog = "";
            StunHit stun;
            SpawnedMonster.NativeKnockback? nativeKnockback = null;
            using (ProjectileMutation(player))
            {
                if (m.Life == 0) return true;
                if (!PlayerActionCurrent(player, action) || player.Paused)
                {
                    Console.WriteLine($"[gateway] {player.Connection.Remote} attack on 0x{targetId:x} cancelled by a later action");
                    return false;
                }
                // A message-level scope can predate the swing; contact reads a fresh caller snapshot.
                using var contactStats = new PlayerStatScope(player, fresh: true);
                // Settle Rage at accepted contact before this hit's gain. It is a final multiplier on the attack packet,
                // never on an existing impale bonus or on generic damage/DoT.
                var playerStats = CharacterPlayerStats(player);
                var accuracy = AttackAccuracyProfile.From(player.Pools, playerStats, Stats, ownPenetration, weapon.LocalSources)
                    with { Rating = HandAccuracy(player, hand, playerStats) };
                IEnumerable<(uint, int)> conversionStats = ownPenetration is null ? [] :
                    ownPenetration.Select(pair => (pair.Key, pair.Value));
                var conversion = SupportedAttackDamage.WithWeaponGains(
                    DamageStats.Conversion(playerStats, conversionStats, Stats, attack: true), weapon,
                    key => playerStats.GetValueOrDefault(key));
                var prehit = DefaultMeleePrehitProfileLocked(player, targetId, m, playerStats, weapon, conversion,
                    ownPenetration, ProjectileWorldTime(player), accuracy.Rating);
                if (prehit is null)
                {
                    Console.WriteLine($"[gateway] {player.Connection.Remote} attack on 0x{targetId:x} cancelled: the target is no longer " +
                        "the registered living hostile monster");
                    return true;
                }
                double contactAt = AreaTime(player);
                if (m.Effects[Stat.BaseCannotBeDamaged] > 0
                    || MapEssences.ProximityShieldBlocksLocked(player, m, null)
                    || MonsterProximityShieldBlocksLocked(player, m, null))
                    return true;
                int hitChance = AttackHitChanceAt(player, targetId, m, contactAt, accuracy, playerStats);
                if (!accuracy.CannotBeEvaded && !m.AttackEvasion.Hits(hitChance, contactAt, player.AttackAccuracyRandom))
                {
                    Console.WriteLine($"[gateway] {player.Connection.Remote} attack on 0x{targetId:x} evaded ({hitChance}% chance to hit)");
                    return true;
                }
                if (MonsterBlocks(m, AreaRng(player), spell: false))   // Stalwart: nothing of the hit happens
                {
                    Console.WriteLine($"[gateway] {player.Connection.Remote} attack on 0x{targetId:x} ({m.Label}) blocked");
                    return true;
                }
                var pen = Penetration.Of(playerStats, Stats, attack: true, ownPenetration, weapon.LocalSources);
                var resistanceMods = HitResistMods(playerStats, ownPenetration);
                var hit = prehit.Hit;
                if (weapon.Melee)
                    hit = WithMeleeProximityLocked(player, playerStats, ownPenetration, m, hit, contactAt);
                var gambler = GamblerFor(playerStats);
                var ownRolled = GameEvents.WithOutgoingDamage(player, playerStats, m,
                    WithIntimidatingCryExert(gambler with { DamageMorePct = 0 }, cryExerted)
                        .ForWeapon(weapon.LocalSources).RollOutgoingHit(hit, AreaRng(player)));
                critical = RollCriticalStrike(player, CriticalChanceOn(player, targetId, HandAttackCriticalChance(player, hand, playerStats),
                        AttackCriticalChanceVsBlinded(player, playerStats, hand: hand), AttackCriticalChanceVsGround(player, playerStats, hand: hand)),
                        ownPenetration, skillRoll: criticalRoll)
                    && GameServer.Core.World.EvasionEntropy.ConfirmsCritical(hitChance, player.AttackAccuracyRandom);
                long weaponMultiplier = (long)AttackCriticalMultiplier(player, playerStats)
                    + ClawDaggerCriticalBonus(weapon, playerStats, ownPenetration);
                int sourceCriticalMultiplier = CriticalMultiplierWithWeapon(
                    CriticalHitMultiplier(weaponMultiplier, playerStats, ownPenetration), m, weapon.LocalSources);
                double at = ProjectileWorldTime(player);
                int criticalMultiplier = CriticalMultiplierOn(m, sourceCriticalMultiplier, at, playerStats, ownPenetration);
                (damage, resistance, var mitigated) = MonsterHitDamage(m, ownRolled, critical, attack: true, at, resistanceMods, pen,
                    gambler.InvertsResistance(AreaRng(player)), AreaRng(player));
                hitCode = HitCode(mitigated);
                if (critical) damage = DamageModel.CriticalDamage(damage, criticalMultiplier);
                int chaosDamage = MonsterHitChaosDamage(m, mitigated, spell: false, at, damage);
                LeechOnHitTargetLocked(player, m, MonsterTakenComponents(m, mitigated, spell: false, at), damage,
                    HitLeechSources(playerStats, true, ownPenetration, weapon.LocalSources, weapon.Melee), targetId);
                (damage, stored, impaled) = WithImpaleHitLocked(player, m, damage, ownRolled, critical,
                    sourceCriticalMultiplier + FullLifeCriticalMultiplier(m, playerStats, ownPenetration),
                    WeaponImpaleSettings(playerStats, Stats, attack: true, ownPenetration, weapon, critical));
                impaleStacks = m.Impale.Stacks;
                impaleStored = m.Impale.Bonus;
                var fortificationDamage = RawFortificationDamage(ownRolled, critical, criticalMultiplier);
                var fortifySkill = WithStrikeFortifySkillStat(playerStats, ownPenetration, weapon.Melee);
                bool fortifiedThisHit = !fortified && (fortified = FortificationOnHitLocked(player, m, fortificationDamage,
                    weapon.Melee, playerStats, fortifySkill));
                if (fortifiedThisHit) AfterFortificationGainLocked(player, playerStats);
                stun = OwnerStun(player, m, damage, mitigated, weapon.Melee, spell: false, critical, playerStats, ownPenetration,
                    weaponClass: weapon.ItemClass ?? "")
                    with { FortificationDamage = fortificationDamage, Fortified = fortifiedThisHit };
                uint lifeBefore = m.Life;
                var rampageSource = RampageOnMeleeHitLocked(player, m, weapon.Melee, playerStats);
                int lifeDamage = AbsorbMonsterEnergyShieldLocked(player.Area, m, damage, chaosDamage, contactAt);
                (m.Life, stageStarts) = TakeOwnerHitLocked(player, m, lifeDamage,
                    Cull.Of(playerStats, ownPenetration, critical, spell: false).WithWeapon(weapon.LocalSources), rampageSource, playerStats);
                CaptureUnhingedHitKillLocked(player, m, playerHit: true);
                CaptureEquippedHitExplosionLocked(m, playerStats);
                if (stageStarts)
                {
                    CommitBossStage(player, targetId, m, ProjectileWorldTime(player));
                    stageStarts = false;
                }
                life = m.Life;
                if (weapon.Melee) GainValourFromMeleeLocked(player, m, life == 0, at, playerStats);
                player.LastEnemyHitAt = m.PlayerHitAt = AreaTime(player);
                // The "recently" stats (issue #504): hit an enemy, and crit with it when the roll was a critical one.
                player.RecentEvents.RecordHit(m.PlayerHitAt, critical, attack: true);
                GainConvergenceOnHitLocked(player, m, ProjectileWorldTime(player), playerStats);
                if (ElementalOverloadCritLocked(player, SkillCatalog.FirstSkillId, null, playerStats, critical) is { } eoBuff)
                    ProjectilesFor(player).Packet(eoBuff, "elemental overload (default attack)");
                if (life == 0) { m.Aggro = false; m.Dead = true; CleanupDeadMonsterLocked(player.Area, targetId, m); CollectVaalSoulAtDeathLocked(player, m); }
                else if (m.Hostile)
                {
                    var pos = m.Walk.Position(contactAt);
                    if (!(m.ReturningHome && m.SpawnAnchor != default && Dist(pos, m.SpawnAnchor) > 2.0 * Math.Max(40, m.AggroRange)))
                    {
                        bool wasAggro = m.Aggro;
                        m.Aggro = true;
                        m.ReturningHome = false;
                        if (!wasAggro) AlertPackLocked(player, m, pos, contactAt);
                    }
                }
                AilmentsOnHitLocked(player, targetId, m, mitigated, spell: false, critical, criticalMultiplier, playerStats,
                    ownPenetration, at, AreaRng(player), poison: prehit.Poison,
                    blind: BlindHitInput.Capture(playerStats), attackGains: AttackAilmentGains.Capture(playerStats, attack: true));
                nativeKnockback = TakeNativeKnockbackLocked(m);
                if (life > 0 && prehit.Ignite.CanIgnite(ownRolled)
                    && IgniteOnHitLocked(player, targetId, m, prehit.Ignite,
                        critical, at, attackGains: AttackAilmentGains.Capture(playerStats, attack: true)) is { } packets)
                    (ignited, igniteLog) = (packets, IgnitedLog(targetId, m, critical, at));
                if (life > 0 && ownRolled[DamageType.Physical].Max > 0
                    && BleedOnHitLocked(player, targetId, m, prehit.Bleed, critical, at,
                        BleedReflected(playerStats)) is { } cut)
                    (bled, bleedLog) = (cut, BleedingLog(targetId, m, critical, at));
                TriggerElementalRelicLocked(player, m, hit: damage > 0, killed: life == 0);
                if (!raged) raged = RageOnHitWithWeaponLocked(player, attack: true, melee: weapon.Melee, critical, weapon.ItemClass);
                if (weapon.ItemClass is not null) RecordWeaponContactLocked(player, at, weaponContactSource);
                if (critical) ItemMinionsOnWeaponCriticalLocked(player, weaponContactSource, m, at);
                if (life == 0) RecordTinctureKill(m, TinctureKillBonusFor(player, playerStats, hand));
                if (player.TinctureEffects.AnyEffective) TinctureOnWeaponHitLocked(player, playerStats, m, lifeBefore, at, AreaRng(player));
            }
            ChargesOnHit(player, m, critical, spell: false);
            if (critical) _ = GainFlaskChargesOnCrit(player);   // the hit landed
            if (critical) await MaybeCreateProfaneGround(player, target);
            // At 0 the client's Life tick (0x141e70730) stops notifying its listeners, so the mini life bar would keep the last
            // value: hide it with the stat its listener watches (hide_mini_life_bar).
            if (life == 0)
                { SetStats(player, targetId, [(Stat.HideMiniLifeBar, 1)]); Note($"0x01b3 monster 0x{targetId:x} hide_mini_life_bar = 1"); }
            // The hit reaction: one of the rig's TakeHit clips and its SoundEvent (0x1b2).
            await SendFlinch(player, targetId, m, stun, log: true);
            // The hit result for the client's damage/effect pipeline (0x1af): the client applies the hit locally (the target's
            // blood, the weapon impact sound "bonk") and 0x1b1 stays authoritative on life. flags 0x4201 is the real server's
            // player-melee hit (bit 0 hit, 0x200 damage, 0x4000 attack), kind 0x03 = action hit with hit data, the angle drives
            // the directional blood.
            var order = HitOrder(player, player.Id);
            ushort hitFlags = (ushort)(0x4201 | (critical ? 0x0020 : 0));
            byte hitKind = 0x03, hitAngle = HitAngle(target.X - stop.X, target.Y - stop.Y);
            ApplyNativeKnockback(nativeKnockback, ref hitFlags, ref hitKind, ref hitAngle, out ushort hitU3c, out ushort hitU3e);
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionHit(player.Id, use.Sequence, targetId,
                    flags: hitFlags, kind: hitKind, code: hitCode,
                    angle: hitAngle, u3c: hitU3c, u3e: hitU3e, orderKey: order.Key, checkpoint: order.Hit),
                $"0x01af player hit on monster 0x{targetId:x} (sequence 0x{use.Sequence:x4})");
            Note($"0x01b1 monster 0x{targetId:x} hit for {damage}{(critical ? " (critical)" : "")}{handNote}" +
                (resistance is { } note ? $", {note}" : "") +
                (stored > 0 ? $", impale +{stored:0.#} ({impaleStacks} stack(s))" : "") +
                (impaled ? $", impaled ({impaleStacks} stack(s), {impaleStored:0.#} stored)" : "") +
                $", life {life}/{m.MaxLife}{(life == 0 ? " (dies)" : "")}");
            await SendDotBuff(player, ignited, igniteLog);
            await SendDotBuff(player, bled, bleedLog);
            if (life == 0) await MonsterKilled(player, targetId, m);
            if (stageStarts) _ = RunBossStage(player, targetId, m);
            if (m.ThresholdReached) await QuestThreshold(player, targetId, m);
            return true;
        }
    }

    /// <summary>The immutable default-melee hit and damaging-ailment outputs chosen for one contact.</summary>
    public sealed record DefaultMeleePrehitProfile(DamagePacket Hit, MonsterIgnites.Settings Ignite,
        MonsterIgnites.Settings Bleed, MonsterPoisons.Settings Poison);

    /// <summary>The immutable hit and damaging-ailment outputs chosen for one AreaSkillHit victim.</summary>
    public sealed record AreaSkillDamageProfile(DamagePacket Hit, MonsterIgnites.Settings Ignite,
        MonsterIgnites.Settings Bleed, MonsterPoisons.Settings Poison);

    // Build the default melee contact's damage and ailment inputs from one saved weapon and caster snapshot. The caller
    // holds Monsters and uses the result through the contact without reading the player or weapon again.
    public static DefaultMeleePrehitProfile? DefaultMeleePrehitProfileLocked(Player player, uint targetId,
        SpawnedMonster target, IReadOnlyDictionary<uint, int> playerStats, SkillCatalog.Unarmed weapon,
        GameServer.Core.World.DamageConversion conversion, IReadOnlyDictionary<uint, int>? own, double at, int? accuracy = null)
    {
        if (!target.Hostile || target.Dead || target.Life == 0
            || !player.Area.Monsters.TryGetValue(targetId, out var registered) || !ReferenceEquals(registered, target))
            return null;
        long additionalIncreased = 0;
        if (Blinded(player, targetId, at) && PlayerHitDamage.VsBlindedDamageStat is var key)
            additionalIncreased = (long)playerStats.GetValueOrDefault(key) + (own?.GetValueOrDefault(key) ?? 0);
        int webs = target.SpiderWebs.Count(at);
        additionalIncreased += SpiderDamageIncrease(n => GolemStat(playerStats, n), webs);
        playerStats = SpiderAttackStats(playerStats, webs);
        bool lowLife = EnemyOnLowLife(target);
        int Get(uint key) => GolemStat(playerStats, key);
        var contact = ContactDamage(player, target, at, playerStats, weapon.LocalSources);
        var modifiers = TargetModifiers(Get, lowLife, target.MaimedUntil > at,
            weapon.Melee && Get(Stat.MeleeDamagePlusPctAtCloseRange) != 0 && InCloseRange(player, target, at),
            claw: lowLife && WeaponIdentity.CountsAs(weapon.ItemClass, weapon.LocalSources, "Claw"), contact: contact);
        playerStats = TypedTargetDamageStats(playerStats, target.Ignites.IgnitedAt(at), target.Bleeds.IgnitedAt(at), lowLife);
        playerStats = WithElementalOverload(player, SkillCatalog.FirstSkillId, playerStats);
        int rating = accuracy ?? PlayerAccuracy(player, playerStats);
        // The swinging hand's rating: an off-hand swing uses its own local accuracy (accuracy-scaled added damage).
        var hit = AttackHitConverted(weapon, TargetHitStats(playerStats, contact), weapon.Melee, conversion, new(rating),
            additionalIncreased + modifiers.HitIncrease);
        if (modifiers.HitMore != 1) hit = hit.Scale(modifiers.HitMore);
        long ailmentIncrease = additionalIncreased + modifiers.AilmentIncrease;
        var ignite = DefaultAttackIgnite(player, playerStats, weapon, ailmentIncrease, conversion, rating);
        var bleed = DefaultAttackBleed(player, playerStats, weapon, ailmentIncrease, conversion, rating);
        var poison = DefaultAttackPoison(player, playerStats, weapon, ailmentIncrease, conversion, rating);
        return modifiers.AilmentMore == 1 ? new(hit, ignite, bleed, poison) : new(hit,
            RepeatAilment(ignite, modifiers.AilmentMore), RepeatAilment(bleed, modifiers.AilmentMore),
            RepeatAilment(poison, modifiers.AilmentMore));
    }

    // A gem skill used on a monster (C→S 0x24 with a skill id other than the default attack's). A spell stops and casts from
    // where the player stands: the server echoes the action from there, aimed at the monster, so the client plays the cast.
    // An attack gem (Holy Strike, Ground Slam …) is used like the default attack (see Attack): the client walks into reach
    // first, so the server walks its estimate too and only echoes the swing — and pays the skill's cost — on arrival. Before
    // 2026-10-25 the attack cast at the click from out of reach: the mana was gone at once and the hit landed around the
    // player, hitting nothing. CastGemSkill pays the cost and runs the skill's effect (projectile, area hit, aura, self-cast).
    public static async Task CastOn(Player player,
        InstanceTransfer.UseSkillOnObject use, SpawnedMonster m, double requestedAt)
    {
        if (m.OwnerId is not null) return;
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == use.SkillId);
        if (gem.Name is null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{use.SkillId:x4}: unknown dynamic skill id (action not started)");
            return;
        }
        if (gem.Name is not null && GemSkillInstanceDisabled(player, gem.Effect, use.SkillId) is not null) return;
        if (gem.Name is not null && !ProjectilePathAvailable(player, use.SkillId, gem.Effect)) return;
        if (gem.Name is not null && !VaalMinionTargetAvailable(player, use.SkillId, use.TargetId)) return;
        // A movement attack (Flicker Strike, Leap Slam) closes the distance itself, and an attack used with "always attack
        // without moving" (Flags bit 0) swings from where the player stands: both keep the immediate cast.
        // A trapped or mined attack is thrown from where the player stands, at the throwing time (Messages.cs).
        bool thrown = gem.Name is not null && PlacedKind(SupportedSkill(player, use.SkillId)) is not { Trap: false, Mine: false };
        if (gem.Name is not null && !thrown && !IsTotemSkill(player, use.SkillId) && SkillsFor(player).IsAttack(gem.Effect) && !SkillsFor(player).IsMovement(gem.Effect) && (use.Flags & 0x1) == 0)
        {
            if (!AttackReady(player, use.SkillId, gem.Effect, gem.Level, out double attackSeconds, use.Sequence))
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x}: ignored, the previous attack is still running (attack time {attackSeconds:0.00} s) (t={AreaTime(player):0.000})");
                return;
            }
            // The reach of the weapon this use swings (AttackHands): a dual-wielded off hand walks up to its own.
            var weapon = HandWeapon(player, AttackHandFor(player, use.SkillId, use.Sequence));
            var supportedStats = SupportedSkill(player, use.SkillId).Stats;
            int swingReach = MeleeSkillReach(player, weapon, gem.Effect, supportedStats);
            weapon = weapon with { Range = swingReach };
            double admitReach = MeleeAreaSwingReach(player, weapon, gem.Effect, supportedStats, swingReach);
            int precision = AttackPrecision(player);
            double now = AreaTime(player);
            int action = 0;
            (int X, int Y) target = default, here = default, stop = default;
            double arrive = now;
            bool atOnce = false;
            if (!TryBeginSkillCooldownUse(player, use.SkillId, gem.Effect,
                CooldownForSkill(player, use.SkillId), false,
                () =>
                {
                    now = ProjectileWorldTime(player);
                    action = Interlocked.Increment(ref player.ActionCount);
                    target = m.Walk.Position(now);
                    // A lockstep client's walk-up is placed where its 0x1ab goes out (LockstepAttackStart).
                    if (player.Connection.LockstepClient) (here, stop, arrive, atOnce) = (player.Walk.Position(now), player.Walk.Position(now), now, false);
                    else (here, stop, arrive, atOnce) = WalkUpToSwing(player, m, target, weapon, now, precision);
                }, out var approachTicket, reserve: false, requestedAt: requestedAt)) return;
            if (!player.Connection.LockstepClient)
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0024 skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x}: player ({here.X}, {here.Y}), " +
                    $"monster ({target.X}, {target.Y}), distance {Dist(here, target):0.0}; swing from ({stop.X}, {stop.Y}) in {arrive - now:0.00} s (t={now:0.000})");
            // A lockstep client's action starts now and walks up by itself (Lockstep.cs); the swing below then says nothing.
            var lockstep = await LockstepAttackStart(player, use, m, weapon, precision, action, gem.Name);
            bool started = lockstep is not null;
            async Task<bool> Walked(double at, double from)
            {
                await AreaDelay(player, at - from);
                return true;
            }
            if (await FollowMonsterToSwing(player, m, weapon, () => player.ActionCount == action && approachTicket?.Current(player) != false,
                Walked, stop, arrive, atOnce, now, precision, lockstep) is not { } swing)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x} cancelled before the swing");
                player.Connection.Performance.Count("use cancelled before the swing");
                LockstepAttackOver(player, action);
                return;
            }
            (stop, arrive, double begun) = swing;
            now = AreaTime(player);
            // A lockstep client's swing began at the start of its goal frame, tested against the monster's cell then.
            target = m.Walk.Position(started ? begun : now);
            if (!SwingInReach(player, m, stop, target, admitReach))
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x} out of reach: {Dist(stop, target):0.0} > {admitReach:0.#}");
                LockstepAttackOver(player, action);
                return;
            }
            if (!TryBeginSkillCooldownUse(player, use.SkillId, gem.Effect,
                CooldownForSkill(player, use.SkillId), false,
                () => { }, out var cooldownUse, actionToken: action))
            {
                LockstepAttackOver(player, action);
                return;
            }
            // The swing's hold was set when it began (LockstepAttackSwings); its timer, hit and release count from then
            // too, as the default attack's do.
            LockstepAttackOver(player, action);
            double? swingAt = started ? begun : now;
            bool acknowledged = false;
            bool projectileRuntime = UsesProjectileRuntime(gem.Effect);
            Task<bool>? attackTask = projectileRuntime ? CastGemSkill(player, use.SkillId, stop, target,
                use.TargetId, sequence: use.Sequence, actionToken: action, cooldownUse: cooldownUse, swingAt: swingAt) : null;
            if (attackTask is { IsCompletedSuccessfully: true, Result: false }) return;
            if (started)
                acknowledged = SkillAcknowledgementAllowed(player, gem.Effect, use.SkillId,
                    () => cooldownUse?.CommitNativeUse(player, use.SkillId) ?? PlayerActionCurrent(player, action));
            else await SendWorldRecords(player, player.Id, () => [InstanceProtocol.ActionStarted(player.Id, stop.X, stop.Y, target.X, target.Y, use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId), InstanceProtocol.ActionEnded(player.Id)],
                $"0x01ab player skill 0x{use.SkillId:x4} {gem.Name} on monster 0x{use.TargetId:x} ({m.Label}) from ({stop.X}, {stop.Y}) to ({target.X}, {target.Y}), flags 0x{use.Flags:x4} (t={AreaTime(player):0.000})",
                still: () => acknowledged = SkillAcknowledgementAllowed(player, gem.Effect, use.SkillId,
                    () => cooldownUse?.CommitNativeUse(player, use.SkillId) ?? PlayerActionCurrent(player, action)));
            if (acknowledged) await SendNativeCooldownRefresh(player);
            if (!projectileRuntime && !acknowledged) return;
            if (!projectileRuntime) _ = CastGemSkill(player, use.SkillId, stop, target, use.TargetId, sequence: use.Sequence,
                actionToken: action, cooldownUse: cooldownUse, swingAt: swingAt);
            return;
        }
        bool leapSkill = gem.Name is not null && !IsTotemSkill(player, use.SkillId) && IsLeapSlam(player, gem.Effect);
        int chargeBonus = gem.Name is null ? 0 : ShieldChargeBonusPct(player, use.SkillId, gem.Effect);
        bool shieldCharge = chargeBonus != 0;
        bool immediateAttack = gem.Name is not null && !thrown && !IsTotemSkill(player, use.SkillId)
            && SkillsFor(player).IsAttack(gem.Effect) && (!SkillsFor(player).IsMovement(gem.Effect) || shieldCharge);
        if (immediateAttack && !AttackReady(player, use.SkillId, gem.Effect, gem.Level, out double immediateAttackSeconds, use.Sequence))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x}: ignored, the previous attack is still running (attack time {immediateAttackSeconds:0.00} s) (t={AreaTime(player):0.000})");
            return;
        }
        double castAt = AreaTime(player);
        int castAction = 0;
        (int X, int Y) aim = default, stand = default, landing = default;
        bool moves = gem.Name is not null && !shieldCharge && LandsAtAim(player, gem.Effect);
        if (gem.Name is not null && IsDashEffect(SkillsFor(player).EffectId(gem.Effect)))
        {
            (int X, int Y) dashAim;
            dashAim = m.Walk.Position(ProjectileWorldTime(player));
            if (!TryCommitDashUse(player, use.SkillId, gem.Effect, dashAim, out var dashUse, requestedAt, flags: use.Flags))
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{use.SkillId:x4} {gem.Name}: Dash refused before object admission");
                return;
            }
            if (!await CastGemSkill(player, use.SkillId, dashUse!.From, dashAim,
                    use.TargetId, use.Sequence, dashReceipt: dashUse)) return;
            bool dashAcknowledged = false;
            await Send(player,
                [.. InstanceProtocol.ActionStarted(player.Id, dashUse.From.X, dashUse.From.Y, dashAim.X, dashAim.Y,
                    use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId), .. InstanceProtocol.ActionEnded(player.Id)],
                $"0x01ab player skill 0x{use.SkillId:x4} {gem.Name} on 0x{use.TargetId:x}: accepted Dash from " +
                $"({dashUse.From.X}, {dashUse.From.Y}) to ({dashUse.To.X}, {dashUse.To.Y}) (t={dashUse.AcceptedAt:0.000})",
                still: () => dashAcknowledged = dashUse.ConfirmNativeUse(player));
            if (!dashAcknowledged) return;
            await SendNativeCooldownRefresh(player);
            ResyncAfter(player, MovementSeconds(player, gem.Effect, gem.Level), gem.Name);
            return;
        }
        // Flicker Strike's client action strikes without moving the player: the server teleports it (FlickerStrike).
        bool flicker = moves && SkillsFor(player).ActionHash(gem.Effect) == FlickerStrike.Action, flickers = false;
        // The monster Flicker Strike goes to, which may not be the clicked one (FlickerStrike.Pick): its strike's target.
        uint struckId = use.TargetId;
        (uint Id, (int X, int Y) At, (int X, int Y) Landing)? flickerTo = null;
        bool noFlickerTarget = false;
        // With no monster in range, Flicker Strike is not used: no cost, cooldown use or charge (the client may still
        // send it for a monster it has not yet seen die).
        bool FlickerAdmit()
        {
            double t = ProjectileWorldTime(player);
            flickerTo = FlickerDestination(player, gem.Effect, use.SkillId, player.Walk.Position(t), m.Walk.Position(t), use.TargetId, t);
            noFlickerTarget = flickerTo is null;
            return !noFlickerTarget;
        }
        LeapSlam.Use? leap = null;
        void StartCast()
        {
            bool lockstep = player.Connection.LockstepClient;
            castAt = leap?.At ?? ProjectileWorldTime(player);
            double actionAt = lockstep && player.Connection.LockstepFrame is { } frame ? frame.Start : castAt;
            if (lockstep) StepWalkers(player, actionAt);
            AdvanceShieldChargeForUse(player, actionAt, immediateAttack);
            if (chargeBonus == 0) EndShieldChargeIfInterrupted(player);
            castAction = Interlocked.Increment(ref player.ActionCount);
            aim = m.Walk.Position(actionAt);
            stand = player.Walk.Position(actionAt);
            if (chargeBonus == 0)
            {
                if (lockstep) player.Walk.Cancel(actionAt);
                else player.Walk.Order(stand.X, stand.Y, castAt);
            }
            // A movement skill aimed at a monster travels as one aimed at the ground does.
            if (leap is not null) CommitLeapSlam(player, leap);
            else if (flicker && flickerTo is { } to)
            {
                (aim, landing, flickers, struckId) = (to.At, to.Landing, true, to.Id);
                player.Walk.Teleport(landing.X, landing.Y, castAt);
            }
            else if (moves && chargeBonus == 0)
            {
                landing = MovementLanding(player, gem.Effect, stand, aim, use.SkillId, use.Flags);
                player.Walk.Teleport(landing.X, landing.Y, castAt);
            }
        }
        CooldownUseTicket? spellUse;
        bool admitted;
        if (leapSkill)
            admitted = TryBeginAttackUse(player, use.SkillId, gem.Effect, requestedAt,
                _ => StartCast(), out spellUse,
                at => (leap = PlanLeapSlam(player, use.SkillId, gem.Effect, m.Walk.Position(at), at)) is not null);
        else admitted = TryBeginSkillCooldownUse(player, use.SkillId, gem.Effect,
            gem.Name is null ? null : CooldownForSkill(player, use.SkillId), false,
            StartCast, out spellUse, requestedAt: requestedAt, admit: flicker ? FlickerAdmit : null);
        if (!admitted)
        {
            if (noFlickerTarget) await FlickerRefused(player, gem.Name!);
            return;
        }
        Action? startCharge = null;
        bool spellAcknowledged = false;
        bool spellProjectileRuntime = UsesProjectileRuntime(gem.Effect) || leapSkill || shieldCharge;
        double? immediateSwingAt = leap?.At ?? (immediateAttack || player.Connection.LockstepClient
            ? LockstepScheduledAt(player, castAt) : null);
        bool movedByCast = flickers && landing != stand || moves && chargeBonus == 0 && landing != stand;
        Task<bool>? spellTask = spellProjectileRuntime ? CastGemSkillWithAdmission(player,
            shieldCharge ? start => startCharge = start : null, use.SkillId, stand, aim,
            use.TargetId, sequence: use.Sequence, actionToken: spellUse?.ActionToken ?? castAction, cooldownUse: spellUse,
            swingAt: immediateSwingAt, leap: leap, movedByCast: movedByCast) : null;
        if (shieldCharge && !await spellTask!) return;
        if (spellTask is { IsCompletedSuccessfully: true, Result: false }) return;
        byte[][] ActionRecords()
        {
            Interlocked.Exchange(ref startCharge, null)?.Invoke();
            return [InstanceProtocol.ActionStarted(player.Id, stand.X, stand.Y, aim.X, aim.Y,
                use.SkillId, use.Sequence, use.Flags, targetObjectId: use.TargetId), InstanceProtocol.ActionEnded(player.Id)];
        }
        bool Acknowledge() => spellAcknowledged = SkillAcknowledgementAllowed(player, gem.Effect, use.SkillId,
            () => spellUse?.CommitNativeUse(player, use.SkillId) ?? PlayerActionCurrent(player, castAction));
        bool predictedLeap = PredictsLeapSlam(player, SkillsFor(player).EffectId(gem.Effect), use.Sequence);
        string acknowledgement = $"{(predictedLeap ? "0x01ac predicted Leap Slam acknowledged" : "0x01ab player skill")} 0x{use.SkillId:x4} cast on monster 0x{use.TargetId:x} ({m.Label}) from ({stand.X}, {stand.Y}) to ({aim.X}, {aim.Y}), flags 0x{use.Flags:x4} (t={castAt:0.000})";
        if (predictedLeap)
            await Send(player, [], acknowledgement, still: Acknowledge,
                atGate: () => LeapSlamAcknowledgement(player, [.. ActionRecords().SelectMany(p => p)]));
        else
            await SendWorldRecords(player, player.Id, ActionRecords, acknowledgement, still: Acknowledge);
        if (shieldCharge && !spellAcknowledged) return;
        if (flickers && spellAcknowledged)
        {
            await FlickerArt(player, gem.Gem, stand, landing);
            await SendWorldRecord(player, player.Id, InstanceProtocol.Teleport(player.Id, landing.X, landing.Y),
                $"0x01b7 {gem.Name} to ({landing.X}, {landing.Y}) for the monster at ({aim.X}, {aim.Y}) (t={castAt:0.000})");
        }
        if (flicker && spellAcknowledged) await GrantFlickerSpeed(player, use.SkillId);
        if (spellAcknowledged && movedByCast && IsFlameDashEffect(SkillsFor(player).EffectId(gem.Effect)))
            await SendWorldRecord(player, player.Id,
                FlameDashGroundSegment(player, use.SkillId, gem.Effect, use.Sequence, stand, landing),
                $"0x02c8 Flame Dash burning ground ({stand.X}, {stand.Y}) -> ({landing.X}, {landing.Y})");
        if (spellAcknowledged) await SendNativeCooldownRefresh(player);
        if (leap is { } jump)
            ResyncAfter(player,
                Math.Max(0, jump.At + jump.Timing.End - AreaTime(player)), gem.Name!);
        else if (moves) ResyncAfter(player,
            MovementSeconds(player, gem.Effect, gem.Level, use.SkillId), gem.Name!);
        if (!spellProjectileRuntime && !spellAcknowledged) return;
        if (!spellProjectileRuntime) _ = CastGemSkill(player, use.SkillId, flicker ? landing : stand, aim, struckId,
            sequence: use.Sequence, actionToken: spellUse?.ActionToken ?? castAction, cooldownUse: spellUse, swingAt: immediateSwingAt,
            movedByCast: movedByCast);
    }

    /// <summary>One Dash whose source, resources, cooldown, displacement and recent events committed together. The past
    /// event is immutable; delayed publication and acknowledgement still require the original owner and source to be
    /// current. They never move, pay or record again.</summary>
    public sealed record DashUseReceipt(Player Player, GameServer.Modding.IModuleLifetime Lifetime,
        ushort SkillId, int Effect, GemSkillIds.Gem Source, int ActionToken, int ProjectileEpoch, double AcceptedAt,
        (int X, int Y) From, (int X, int Y) To,
        SingleUseCooldownState Cooldown, int UseVersion, uint ManaCost, uint LifeCost)
    {
        bool paymentPublished, completed, nativeCommitted;
        public IReadOnlyList<(SingleUseCooldownState State, int Version, ushort Skill)> SharedUses { get; init; } = [];
        public bool CooldownReset { get; init; }
        public int ResetBaseMilliseconds { get; init; }

        public bool Matches(ushort skillId, int effect) => SkillId == skillId && Effect == effect;

        public bool PublishPayment()
        {
            if (paymentPublished) return false;
            paymentPublished = true;
            return true;
        }

        public bool Complete()
        {
            if (completed) return false;
            completed = true;
            return true;
        }

        public bool ConfirmNativeUse(Player player)
        {
            if (!ReferenceEquals(Player, player)) return false;
            if (nativeCommitted || !CurrentLocked(player)) return false;
            if (CooldownReset)
                Cooldown.SynchronizeNativeReset(SkillId, ResetBaseMilliseconds);
            else
                Cooldown.ConfirmNativeUse(UseVersion, SkillId, synchronizeProgress: false);
            foreach (var (state, version, skill) in SharedUses)
                state.ConfirmNativeUse(version, skill, synchronizeProgress: true);
            nativeCommitted = true;
            return true;
        }

        public bool Current(Player player)
        {
            if (!ReferenceEquals(Player, player)) return false;
            return CurrentLocked(player);
        }

        public bool CurrentLocked(Player player) =>
            ReferenceEquals(Player, player) && CurrentGameRules && ReferenceEquals(Lifetime, CombatLifetime)
            && player.ProjectileActive(ProjectileEpoch) && player.PlayerLife > 0 && !player.Paused
            && player.PendingTransfer is null && player.ActionCount == ActionToken
            && CurrentDashSourceLocked(player, SkillId, Effect, out _)?.Identity == Source;
    }

    // Requirements and their equipment/inactive snapshot are refreshed before the source list's Active filter. A stale
    // panel cache may retain the grant id, but neither an unmet host nor a removed source can admit or revive Dash work.
    static PlayerSkillSource? CurrentDashSourceLocked(Player player, ushort skillId, int effect, out SupportedGem supported)
    {
        supported = new(-1, 1, 0, [], new Dictionary<uint, int>());
        var equipment = ItemsOf(player).EquipmentSnapshot();
        var requirements = CurrentGemRequirements(player, equipment);
        var source = PlayerSkillSourcesAt(CharacterName(player), equipment, requirements.Inactive, LeagueFor(player),
                GrantedSkillsOf(player), WornGrantedSkillsOf(player, equipment))
            .FirstOrDefault(s => s.Id == skillId && s.Effect == effect && s.Active);
        if (source is null || !IsDashEffect(SkillsFor(player).EffectId(effect))) return null;
        supported = SupportedSkillAt(player, skillId, requirements);
        if (supported.Effect != effect || !supported.RequirementsMet
            || ActorSkillInstanceDisabled(player, effect, skillId) is not null
            || WeaponRestricted(player, effect) is not null
            || SkillsFor(player).EffectId(effect) is { } effectId
                && ProjectileDefinition(effectId)?.Unavailable?.Invoke(player) is not null)
            return null;
        return source;
    }

    // Dash admission is its own transaction because unlike the ordinary cooldown ticket its movement is part of the
    // accepted use. Resolve the exact current source, landing, readiness and cost first; only then spend cooldown and
    // resources and commit displacement plus its original-time recents in the same Monsters critical section.
    public static bool TryCommitDashUse(Player player, ushort skillId, int effect, (int X, int Y) aim,
        out DashUseReceipt? receipt, double? requestedAt = null, bool beginAction = true, ushort flags = 0)
    {
        receipt = null;
        using (ProjectileMutation(player))
        {
            using var sourceAdmission = AscendancyPolicies.Enter(player, player.Character.MainAscendancy.Selection);
            if (!CurrentGameRules || player.Closed || player.LeavingArea || player.RageRetired || player.PlayerLife == 0
                || player.Paused || InTown(player) || requestedAt is double asked
                    && PlayerActionBlocked(player, asked, AreaTime(player)))
                return false;
            var source = CurrentDashSourceLocked(player, skillId, effect, out var supported);
            if (source is null) return false;
            var cooldown = CooldownPlan(player, supported, CharacterPlayerStats(player));
            int key = CooldownKey(player, effect, skillId);
            double acceptedAt = ProjectileWorldTime(player);
            if (!TryDashLanding(player, effect, aim, out var from, out var to, acceptedAt, skillId, flags)) return false;

            if (cooldown is not { } plan || !CanPaySkillCost(player, skillId)) return false;
            var state = CooldownStateLocked(player, key, plan, acceptedAt);
            if (!state.Ready
                || acceptedAt < player.SkillReadyAt.GetValueOrDefault(key))
                return false;
            var peers = SharedCooldownPeers(player, effect, key, plan, acceptedAt);
            if (!PayMana(player, skillId, out uint manaCost, out uint lifeCost)) return false;

            // Nothing below can refuse: payment, cooldown use, action, displacement and recents are one commit.
            state.Refresh(acceptedAt, plan.RecoveryRate);
            state.Capacity(plan.Uses);
            bool cooldownReset = CooldownResetTriggered(player, effect, skillId, AreaRng(player));
            if (!cooldownReset) _ = state.TryUse(acceptedAt, plan);
            player.SkillReadyAt[key] = acceptedAt + state.RemainingSeconds;
            var sharedUses = cooldownReset ? [] : SpendSharedCooldownPeers(player, key, effect, skillId, plan, acceptedAt, peers);
            int action = beginAction ? Interlocked.Increment(ref player.ActionCount) : player.ActionCount;
            if (beginAction) player.Walk.Order(from.X, from.Y, acceptedAt);
            player.Walk.Teleport(to.X, to.Y, acceptedAt);
            RecordAcceptedGemUseAt(player, effect, to, acceptedAt);
            receipt = new(player, CombatLifetime!, skillId, effect, source.Identity, action, player.ProjectileEpoch,
                acceptedAt, from, to, state, state.UseVersion, manaCost, lifeCost)
            {
                SharedUses = sharedUses,
                CooldownReset = cooldownReset,
                ResetBaseMilliseconds = cooldownReset ? plan.BaseMilliseconds : 0,
            };
            return true;
        }
    }

    public static async Task<bool> PublishDashUse(Player player, DashUseReceipt receipt)
    {
        if (!receipt.Current(player)) return false;
        if (receipt.PublishPayment() && (receipt.ManaCost > 0 || receipt.LifeCost > 0))
            await SendSkillCost(player, receipt.LifeCost,
                $"0x01b1 player cost {player.PlayerMana} mana, {player.PlayerLife} life " +
                $"(-{receipt.ManaCost} mana, -{receipt.LifeCost} life for Dash) (t={receipt.AcceptedAt:0.000})");
        if (!receipt.Current(player) || !receipt.Complete()) return false;
        if (SkillsFor(player).IsTravel(receipt.Effect))
            {
                if (!receipt.CurrentLocked(player)) return false;
                QueueFlaskTrigger(player, FlaskEnchantments.Trigger.Travel);
            }
        if (!receipt.Current(player)) return false;
        foreach (var handler in GameEvents.SkillCastAt.For(player))
            if (handler(player, SkillsFor(player).EffectId(receipt.Effect), receipt.SkillId, receipt.To,
                    () => receipt.Current(player)) is not null)
                break;
        _ = GameEvents.RunSkillCast(player, SkillsFor(player).EffectId(receipt.Effect), receipt.SkillId,
            receipt.From, () => receipt.Current(player));
        return receipt.Current(player);
    }

    // A gem skill's cast: Righteous Fire puts its buff on, any other skill launches its projectile (if it has one). False when
    // it was refused (cooldown, mana), so its use is not confirmed to the client. An attack's timer, hit and release
    // count from swingAt when its swing began before now (a lockstep walk-up's goal frame, CastOn), else from now.
    public static Task<bool> CastGemSkill(Player player,
        ushort skillId, (int X, int Y) from, (int X, int Y) to, uint targetId = 0, ushort sequence = 0,
        int? actionToken = null, CooldownUseTicket? cooldownUse = null, bool locationInstant = false,
        DashUseReceipt? dashReceipt = null, double? swingAt = null, LeapSlam.Use? leap = null, Action? guardStart = null,
        bool movedByCast = false) =>
        CastGemSkillWithAdmission(player, null, skillId, from, to, targetId, sequence, actionToken, cooldownUse,
            locationInstant, dashReceipt, swingAt, leap, guardStart, movedByCast);

    static async Task<bool> CastGemSkillWithAdmission(Player player, Action<Action>? onShieldChargeReady,
        ushort skillId, (int X, int Y) from, (int X, int Y) to, uint targetId = 0, ushort sequence = 0,
        int? actionToken = null, CooldownUseTicket? cooldownUse = null, bool locationInstant = false,
        DashUseReceipt? dashReceipt = null, double? swingAt = null, LeapSlam.Use? leap = null, Action? guardStart = null,
        bool movedByCast = false)
    {
        int action = actionToken ?? player.ActionCount;
        int recoveryGeneration = 0;
        int? channelGeneration = null;
        VaalUseReservation? vaalUse = null;
        SupportedGem? beastCompanion = null;
        PlacedCastAdmission? placedAdmission = null;
        SupportedGem? paidIntensity = null, paidMomentum = null;
        var momentumTravel = player.Walk.ObserveTravel(AreaTime(player));
        var baseSkill = BaseSupportedSkill(player, skillId);
        if (SkillsFor(player).EffectId(baseSkill.Effect) is TwisterEffect or CatStealthCryEffect) return false;
        if (actionToken is not null && (player.ActionCount != action || player.ElementalAilments.ActionFactor(AreaTime(player)) == 0 || player.HitRecovery.Active(AreaTime(player)))) return false;
        if (VaalArcticArmourActive(player, AreaTime(player)) && cooldownUse?.Instant != true && !locationInstant
            && !SkillsFor(player).IsInstant(baseSkill.Effect)) return false;
        if (!TryCapturePlacedCastAdmissionLocked(player, skillId, out placedAdmission)) return false;
        if (InTown(player) || player.PlayerLife == 0) return false;
        // Direct callers do not go through the action handler that already reserves placed-skill cooldowns.
        if (actionToken is null && cooldownUse is null && !locationInstant && placedAdmission is { } placement
            && CooldownForSkill(player, skillId) is { } placedCooldown
            && !TryBeginSkillCooldownUse(player, skillId, placement.Effect, placedCooldown, false, () => { },
                out cooldownUse, requestedAt: AreaTime(player),
                admit: () => PlacedCastAdmissionCurrent(player, placement) && GemSkillInstanceDisabled(player, placement.Effect, skillId) is null))
            return false;
        await EndGracePeriod(player, action: true);
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is null) return false;
        if (VaalArcticArmourActive(player, AreaTime(player)) && cooldownUse?.Instant != true && !locationInstant
            && !SkillsFor(player).IsInstant(gem.Effect)) return false;
        if (!PlacedCastFamilyCurrent(player, skillId, placedAdmission)) return false;
        if (IsDashEffect(SkillsFor(player).EffectId(gem.Effect)))
        {
            if (dashReceipt is null)
            {
                if (cooldownUse is not null || !TryCommitDashUse(player, skillId, gem.Effect, to, out dashReceipt,
                        beginAction: false))
                    return false;
            }
            if (dashReceipt is null || !dashReceipt.Matches(skillId, gem.Effect)) return false;
            return await PublishDashUse(player, dashReceipt);
        }
        bool Current()
        {
            return !player.Closed && !player.LeavingArea && !player.RageRetired
                && (cooldownUse?.Current(player) ?? (actionToken is null || PlayerActionCurrent(player, action)))
                && (vaalUse is null || VaalAdmissionCurrent(player, vaalUse))
                && (channelGeneration is not int generation || ChannelCurrent(player, skillId, action, generation));
        }
        // The placement's own gate: upstream's model (the polling wait, SourceCurrent(), the lifetime checks) is kept
        // whole, and the one clause it must NOT inherit is the action token. That wait is 0.6-1.0 s and the client lets
        // the player act long before it is over, so PlayerActionCurrent's `conn.ActionCount == action` cancelled
        // placements whose mana had already been paid (live 2026-11-09: six of seven casts). "The player acted" is not
        // "the placement skill went away". What still cancels a placement is everything else: the skill's own cooldown
        // ticket and Vaal admission here, and inside PlaceTotem the combat lifetime, SourceCurrent() (the gem's host,
        // supports and physical identity), CurrentGameWork, death, an area change and a closed connection. With no
        // cooldown ticket this predicate is therefore true, and those checks are what hold the placement honest: it is
        // not unconditional, it just no longer dies because the player moved.
        bool TotemPlacementCurrent()
        {
            return !player.Closed && !player.LeavingArea && !player.RageRetired
                && (cooldownUse?.Current(player) ?? true)
                && (vaalUse is null || VaalAdmissionCurrent(player, vaalUse))
                && (channelGeneration is not int generation || ChannelCurrent(player, skillId, action, generation));
        }
        if (!Current()) return false;
        if (gem.Name is not null && !ProjectilePathAvailable(player, skillId, gem.Effect)) return false;
        if (locationInstant && (gem.Name is null || !InstantLocationSkill.Supported(SkillsFor(player), gem.Effect))) return false;
        bool spell = CastingSpell(player, gem.Effect);
        int? spellCastMs = spell ? CastMs(player, gem.Effect, skillId) : null;
        if (spell && spellCastMs is null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {gem.Name}: invalid cast duration (no effect or mana cost)");
            await SendPlayerMana(player, $"0x01b1 player mana {player.PlayerMana} (invalid cast duration, cost back)");
            return false;
        }
        // Shared backstop for actor restrictions and gem requirements, before cooldown/payment/effect admission.
        if (GemSkillInstanceDisabled(player, gem.Effect, skillId) is { } disabled)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {gem.Name}: {disabled} (disabled)");
            await SendPlayerMana(player, $"0x01b1 player mana {player.PlayerMana} (disabled skill, cost back)");
            return false;
        }
        if (gem.Name is not null && SelfBuffRefused(player, gem.Effect) is { } refused)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {gem.Name}: {refused}");
            return false;
        }
        if (IsSummonBeastialCompanionEffect(SkillsFor(player).EffectId(gem.Effect)))
        {
            beastCompanion = SupportedSkill(player, skillId);
            if (beastCompanion.Effect != gem.Effect
                || !BeastCompanionAdmissionCurrent(player, beastCompanion, skillId))
                return false;
        }
        bool vaalCast = SkillsFor(player).VaalBudget(gem.Effect, gem.Level) is not null;
        if (placedAdmission is { } placedPreflight && !PlacedCastAdmissionCurrent(player, placedPreflight))
            return false;
        if (vaalCast && (vaalUse = ExecuteVaalUse(player, skillId, gem.Effect)) is null)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {gem.Name}: no executable paid Vaal admission");
            return false;
        }
        if (gem.Name is not null && SkillsFor(player).EffectId(gem.Effect) == ApproachingFlamesEffect
            && (InTown(player) || SupportedSkill(player, skillId).WornGrant is not { EffectId: ApproachingFlamesEffect } flameSource
                || !FlameSourceCurrent(player, flameSource)))
            return false;
        bool shieldCharge = gem.Name is not null && ShieldChargeBonusPct(player, skillId, gem.Effect) != 0;
        if (cooldownUse is null && SkillsFor(player).EffectId(gem.Effect) == VengefulCryEffect
            && !TryBeginSkillCooldownUse(player, skillId, gem.Effect, CooldownForSkill(player, skillId),
                instant: false, () => { }, out cooldownUse, actionToken)) return false;
        // Eligible uses arrive with their one original preflight reservation. Never reserve again after an await.
        if (cooldownUse is not null)
        {
            if (!cooldownUse.TryExecute(player, gem.Effect)) return false;
            if (!cooldownUse.Instant)
            {
                if (UsesProjectileRuntime(gem.Effect) || leap is not null || shieldCharge) ProjectilesFor(player)
                    .Enqueue(() => SendNativeCooldownRefresh(player));
                else await SendNativeCooldownRefresh(player);
            }
            if (!Current()) return false;
        }
        else if (gem.Name is not null && CooldownForSkill(player, skillId) is not null)
            return false;
        // Multi-use and unsupported cooldown mechanics retain their existing raw-deadline behavior.
        else if (gem.Name is not null && SkillsFor(player).CooldownMs(gem.Effect, gem.Level) is int cooldown and > 0)
        {
            double now = AreaTime(player), readyAt;
            int key = CooldownKey(player, gem.Effect, skillId);
            readyAt = player.SkillReadyAt.GetValueOrDefault(key);
            if (now >= readyAt)
            {
                player.SkillReadyAt[key] = now + cooldown / 1000.0;
                player.SingleUseCooldowns.Remove(key);
            }
            if (now < readyAt)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {gem.Name}: on cooldown for {(readyAt - now) * 1000:0} ms more");
                return false;
            }
        }
        if (gem.Name is not null && BannerAuraRow(SkillsFor(player).EffectId(gem.Effect)) != 0)
        {
            bool accepted = await ToggleBanner(player, skillId, Current);
            if (accepted) RecordAcceptedGemUse(player, gem.Effect, to);
            return accepted;
        }
        if (gem.Name is not null && CooldownTriggerController(SkillsFor(player).EffectId(gem.Effect)) != 0)
        {
            bool accepted = await ToggleCooldownTrigger(player, skillId, Current);
            if (accepted) RecordAcceptedGemUse(player, gem.Effect, to);
            return accepted;
        }
        if (gem.Name is not null && SkillsFor(player).EffectId(gem.Effect) == ApproachingFlamesEffect)
        {
            bool accepted = await ToggleApproachingFlames(player, skillId, Current);
            if (accepted) RecordAcceptedGemUse(player, gem.Effect, to);
            return accepted;
        }
        if (gem.Name is not null && SkillsFor(player).EffectId(gem.Effect) == UnhingeEffect)
            return await ToggleUnhinged(player, skillId, gem.Effect, Current);
        if (gem.Name is not null && SkillsFor(player).EffectId(gem.Effect) == MadnessEffect)
        {
            bool accepted = await ToggleMadness(player, skillId, gem.Effect, Current, cooldownUse);
            if (accepted) RecordAcceptedGemUse(player, gem.Effect, to);
            return accepted;
        }
        // An attack gem swings with the weapon's attack time, like the default attack (see Attack): while the button is held
        // the client keeps aiming it, and every use used to pay its cost and start a swing (live 2026-10-25: Holy Strike on
        // the right mouse button, twelve swings a second at 6 mana each). The timer is shared with the default attack, so
        // two attacks cannot start at once either.
        // Paid Vaal admission already reserved this swing atomically with its cost, before the native echo.
        if (cooldownUse?.Attack is { } admittedAttack
            && (admittedAttack.Skill != skillId || cooldownUse.Effect != gem.Effect)) return false;
        double? reservedNextAttackAt = null;
        // A trapped or mined attack is a throw: it neither waits for the weapon's attack time nor reserves it.
        if (vaalUse is null && cooldownUse?.Attack is null && gem.Name is not null
            && !IsTotemSkill(player, skillId) && SkillsFor(player).IsAttack(gem.Effect)
            && PlacedKind(SupportedSkill(player, skillId)) is { Trap: false, Mine: false })
        {
            double duration = ShieldWeapon(player, skillId) is { } shieldWeapon
                ? SupportedOffHandAttackSeconds(player, skillId, shieldWeapon)
                : SupportedAttackSeconds(player, skillId, sequence);
            double now = swingAt ?? AreaTime(player), next;
            bool ready;
            next = player.NextAttackAt;
            ready = AttackTiming.Ready(now, next, duration);
            if (ready)
            {
                reservedNextAttackAt = next;
                player.NextAttackAt = AttackTiming.After(now, next, duration);
            }
            if (!ready)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {gem.Name}: ignored, {next - now:0.000} s before the next attack (t={now:0.000})");
                return false;
            }
        }
        // A link gem (Soul Link …) links its target at the end of its cast; it has no upfront cost, only its cost over time.
        void RecordSpellWindup()
        {
            if (actionToken is null || locationInstant || placedAdmission is not null || spellCastMs is not > 0
                || SkillsFor(player).IsInstant(gem.Effect) || SkillsFor(player).IsChannelled(gem.Effect)
                || IsTotemSkill(player, skillId)) return;
            if (CharacterPlayerStats(player).GetValueOrDefault(Stat.AvoidInterruptionWhileCastingPct) <= 0) return;

            double duration = spellCastMs.Value / 1000d * (1d + SpellRepeats(player, skillId, gem.Effect));
            SpellWindups.AddOrUpdate(player, new(action, player.ActionInterruptionGeneration, player.ProjectileEpoch,
                AreaTime(player) + duration));
        }
        if (gem.Name is not null && IsLinkSkill(player, gem.Effect))
        {
            RecordSpellWindup();
            return await CastLink(player, skillId, gem.Effect, targetId, Current);
        }
        if (placedAdmission is null && !vaalCast && gem.Name is not null && PlacedKind(SupportedSkill(player, skillId)) is { Trap: false, Mine: false }
            && SupportedReservationCost(player, skillId) is { } reservation)
        {
            if (!Current()) return false;
            bool accepted;
            if (StanceCatalog.IsSource(SkillsFor(player).EffectId(gem.Effect)))
            {
                if (cooldownUse?.Stance is not { } stanceUse) return false;
                accepted = await PublishStanceUse(player, stanceUse, gem.Name);
            }
            else if (SkillsFor(player).EffectId(gem.Effect) == PetrifiedBlood.EffectId)
                accepted = await TogglePetrifiedBlood(player, skillId, gem.Effect, gem.Name, reservation,
                    SupportedSkill(player, skillId).Level, SupportedSkill(player, skillId).Quality);
            else
                accepted = await ToggleAura(player, skillId, gem.Effect, gem.Name, reservation,
                    SupportedSkill(player, skillId).Level, SupportedSkill(player, skillId).Quality);
            if (accepted) RecordAcceptedGemUse(player, gem.Effect, to);
            return accepted;
        }
        (int X, int Y) dashFrom = default, dashTo = default;
        bool dash = gem.Name is not null && IsDashEffect(SkillsFor(player).EffectId(gem.Effect));
        if (dash)
        {
            if (!TryDashLanding(player, gem.Effect, to, out dashFrom, out dashTo)) return false;
        }
        SkillCriticalRoll? criticalRoll = vaalUse?.CriticalRoll;
        if (gem.Name is not null && !vaalCast)
        {
            uint cost, lifeCost;
            bool paid;
            int? channelWalk = ChannelWalkPercent(player, skillId, gem.Effect);
            {
                if (!Current()) return false;
                if (dash && !TryDashLanding(player, gem.Effect, to, out dashFrom, out dashTo)) return false;
                using var paymentScope = new PlayerStatScope(player, fresh: true);
                var paymentGem = SupportedSkill(player, skillId);
                if (placedAdmission is { } admission)
                {
                    if (!PlacedCastAdmissionCurrentLocked(player, admission, paymentGem)) return false;
                }
                else
                {
                    var currentKind = PlacedKind(paymentGem);
                    if (currentKind.Trap || currentKind.Mine) return false;
                }
                // A cooldown use paid when it was accepted (TryBeginCooldownUseCore) is not paid again.
                if (cooldownUse?.Paid is { } prepaid) (paid, cost, lifeCost) = (true, prepaid.Mana, prepaid.Life);
                else paid = PayMana(player, skillId, out cost, out lifeCost);
                if (paid && dash) player.Walk.Teleport(dashTo.X, dashTo.Y, ProjectileWorldTime(player));
                if (paid)
                {
                    RecordSpellWindup();
                    if (!locationInstant && placedAdmission is null)
                    {
                        if (paymentGem.Stats.GetValueOrDefault(Stat.VirtualSkillGainsIntensity) != 0) paidIntensity = paymentGem;
                        if (paymentGem.Stats.GetValueOrDefault(Stat.SupportMomentumMaxStacks) > 0
                            && !IsTotemSkill(player, skillId)) paidMomentum = paymentGem;
                    }
                    criticalRoll = CaptureSkillCriticalRoll(player, skillId);
                    // Reserve the whole paid use before cost publication can yield to another held input.
                    if (placedAdmission is null && !IsTotemSkill(player, skillId) && SkillsFor(player).IsAttack(gem.Effect)
                        && !SkillsFor(player).IsChannelled(gem.Effect) && !shieldCharge)
                        ReserveMeleeRepeats(player, skillId, SupportedAttackSeconds(player, skillId, sequence));
                }
                if (paid && cost > 0 && placedAdmission is null)
                    RecordPaidBowAttackLocked(player, skillId, paymentGem, cost, to);
                if (paid && beastCompanion is { } beast)
                    QueueBeastCompanionLocked(player, beast, skillId, actionToken);
                // The native Holy Flame skill is tagged Channel for the totem's payload. Its owner places it with a
                // single cast. Starting an owner channel would tie the placement to the input that movement clears.
                if (paid && Supports.Of(gem.Effect)?.Types.Contains(47) == true && !IsTotemSkill(player, skillId))
                {
                    channelGeneration = BeginPaidChannel(player, skillId, gem.Effect, action, sequence);
                    if (paidMomentum is { } channelMomentum)
                    {
                        GainMomentumOnUse(player, skillId, channelMomentum, action, channelGeneration, AreaTime(player));
                        paidMomentum = null;
                    }
                    bool walkChanges = channelWalk is not null || player.ChannelMovement.Skill != 0;
                    player.ChannelMovement = channelWalk is int walkPercent ? (skillId, walkPercent) : default;
                    if (walkChanges) player.ApplyMovementSpeed();
                    player.LastLockstepChannelAim = null;
                    player.LockstepChannelAimPending = HeldLockstepMovingChannel(player);
                    if (GolemStat(SupportedSkill(player, skillId).Stats, Stat.CastWhileChannellingTimeMs) > 0)
                        BeginChannelTriggers(player, ProjectilesFor(player), skillId,
                            action, player.ChannelGeneration, to, AreaTime(player));
                }
                // Capture at paid admission, before output or release scheduling can catch up other hits.
                recoveryGeneration = player.ActionInterruptionGeneration;
            }
            if (!paid)
            {
                if (reservedNextAttackAt is double prevNext) player.NextAttackAt = prevNext;
                Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {gem.Name}: not enough mana or life " +
                    $"({player.PlayerMana} < {cost}, {player.PlayerLife} life for {lifeCost})");
                return false;
            }
            if (cost > 0 || lifeCost > 0)
            {
                if (UsesProjectileRuntime(gem.Effect) || leap is not null || shieldCharge) ProjectilesFor(player)
                    .Enqueue(() => SendSkillCost(player, lifeCost, $"skill cost paid: {gem.Name}"));
                else await SendSkillCost(player, lifeCost,
                    $"0x01b1 player cost {player.PlayerMana} mana, {player.PlayerLife} life (-{cost} mana, -{lifeCost} life for {gem.Name}) (t={AreaTime(player):0.000})");
            }
        }
        if (gem.Name is not null) ChargesOnSkillUse(player, gem.Effect, vaalCast);
        if (vaalCast)
        {
            recoveryGeneration = vaalUse!.RecoveryGeneration;
            if (Current()) RecordSpellWindup();
            await PublishVaalUse(player, vaalUse);
        }
        if (!Current() || gem.Name is not null && GemSkillInstanceDisabled(player, gem.Effect, skillId) is not null)
            return false;
        if (!PlacedCastFamilyCurrent(player, skillId, placedAdmission))
            return false;
        if (!locationInstant && gem.Name is not null && placedAdmission is null) RecordAcceptedGemUse(player, gem.Effect, to);
        if (gem.Name is not null) PhaseRunOnSkillUse(player, skillId);
        var intensity = CaptureIntensityCast(player, skillId, paidIntensity);
        using var intensityScope = intensity is null ? null : new IntensityScope(intensity);
        bool CompleteSupportedUse(bool accepted)
        {
            if (!accepted) return false;
            intensity?.Complete();
            if (paidMomentum is { } momentum)
            {
                double now = AreaTime(player);
                bool movedDuringUse = movedByCast || (leap is not null && leap.Landing != leap.From)
                    || player.Walk.ObserveTravel(now) != momentumTravel;
                GainMomentumOnUse(player, skillId, momentum, action, channelGeneration, now, movedDuringUse);
            }
            return true;
        }
        if (beastCompanion is not null) return true;
        if (channelGeneration is not null)
        {
            if (!Current()) return false;
            if (SupportedChannelSeconds(player, skillId) is not double interval)
            { player.ChannelSkill = 0; player.ChannelGeneration++; player.PaidChannel = null; return false; }
            player.NextChannelHitAt = AreaTime(player) + (IsSnipe(gem.Effect) ? interval / 2 : interval);
            _ = RunChannel(player, skillId, gem, action, to);
        }
        // The instant's two areas are resolved at the endpoints accepted by the outgoing movement record.
        if (locationInstant) return true;
        if (gem.Name is not null && IsMineDetonation(SkillsFor(player).EffectId(gem.Effect)))
        { DetonateMinesLocked(player, AreaTime(player)); return true; }
        if (placedAdmission is { } placed)
            return await PlaceSkill(player, from, to, placed, recoveryGeneration, Current);
        if (gem.Name is not null)
        {
            if (SkillsFor(player).IsTravel(gem.Effect)) { QueueFlaskTrigger(player, FlaskEnchantments.Trigger.Travel); LoyaltyTravelSkillTriggered(player); }
            if (SkillsFor(player).IsGuard(gem.Effect)) QueueFlaskTrigger(player, FlaskEnchantments.Trigger.Guard);
        }
        // The Portal skill: a town portal, as the Portal Scroll's, once its cast (2.5 s) is over.
        if (actionToken is not null && !PlayerActionCurrent(player, action)) return false;
        if (gem.Name is not null)
            foreach (var handler in GameEvents.SkillCastAt.For(player))
                if (handler(player, SkillsFor(player).EffectId(gem.Effect), skillId, to,
                    () => !player.Closed && PlayerActionCurrent(player, action) && Current()) is { } accepted) return CompleteSupportedUse(accepted);
        if (gem.Name is not null && GameEvents.RunSkillCast(player, SkillsFor(player).EffectId(gem.Effect), skillId, from,
                () => !player.Closed && PlayerActionCurrent(player, action) && Current()) is { } taken) return CompleteSupportedUse(taken);
        if (gem.Name is not null && CurseCatalog.All.ContainsKey(SkillsFor(player).EffectId(gem.Effect) ?? "")
            && !IsTotemSkill(player, skillId))
            return CompleteSupportedUse(await CastCurse(player, skillId, to, targetId, recoveryGeneration, Current));
        if (gem.Name is not null && IsVaalMinion(SkillsFor(player).EffectId(gem.Effect)))
            return await RepeatSummonCast(player, skillId, recoveryGeneration, Current, false,
                () => ReleaseVaalMinions(player, skillId, to, Current, targetId));
        if (gem.Name is not null && MinionUtility(SkillsFor(player).EffectId(gem.Effect)))
            return await CastMinionUtility(player, skillId, to, targetId, recoveryGeneration, Current);
        if (gem.Name is not null && !IsTotemSkill(player, skillId) && GolemData.Find(SkillsFor(player).EffectId(gem.Effect)) is { } golem)
            return await CastGolem(player, skillId, golem, to, recoveryGeneration, Current);
        if (gem.Name is not null && !IsTotemSkill(player, skillId)
            && SkillsFor(player).EffectId(gem.Effect) is "LightningWarp" or VaalLightningWarpEffect)
            return CompleteSupportedUse(ScheduleLightningWarp(player, gem, to, Current, criticalRoll));
        if (gem.Name is not null && SkillsFor(player).EffectId(gem.Effect) == ShaperMemoryEffect)
            return CompleteSupportedUse(await CastShaperMemory(player, skillId, to, Current));
        if (gem.Name is not null && !IsTotemSkill(player, skillId) && SummonData.Find(SkillsFor(player).EffectId(gem.Effect)) is { } summon)
            return await CastSummon(player, skillId, summon, to, targetId, recoveryGeneration, Current);
        if (gem.Name is not null && SkillsFor(player).EffectId(gem.Effect) == "Desecrate")
            return CompleteSupportedUse(await Desecrate(player, skillId, to, Current));
        if (gem.Name is not null && IsTotemSkill(player, skillId) && TotemVariety(player, gem.Effect) is { } totem)
        {
            _ = PlaceTotem(player, skillId, gem.Effect, gem.Level, totem,
                TotemPlacementPoint(player, skillId, from, to), TotemPlacementCurrent);
            return true;
        }
        if (gem.Name is not null && UseWarcryInstance(player, skillId, gem.Effect)) return true;
        if (gem.Name is not null && IsBrandSkill(player, gem.Effect))
            return CompleteSupportedUse(await CastBrand(player, skillId, gem, from, to, recoveryGeneration, Current, criticalRoll));
        if (gem.Name is not null && IsBrandRecall(player, gem.Effect))
            return CompleteSupportedUse(await CastBrandRecall(player, skillId, gem, Current));
        if (gem.Name is not null && IsBrandsurge(player, gem.Effect))
            return CompleteSupportedUse(await CastBrandsurge(player, skillId, gem, from, to, recoveryGeneration, Current, criticalRoll));
        if (gem.Name is not null && SkillsFor(player).EffectId(gem.Effect) == CallOfSteelEffect)
            return CompleteSupportedUse(ScheduleCallOfSteel(player, skillId, recoveryGeneration, Current));
        if (gem.Name is not null && IsBladefallTrarthus(gem.Effect))
            return CompleteSupportedUse(await ActivateBladefallTrarthus(player, skillId));
        if (vaalUse is not null && SkillsFor(player).EffectId(gem.Effect) == VaalArcticArmourEffect)
        {
            StartVaalArcticArmour(player, skillId, CastMs(player, gem.Effect, skillId) ?? SkillsFor(player).CastTimeMs(gem.Effect), AreaTime(player));
            return true;
        }
        if (gem.Name is not null && vaalUse is not null && VaalImmortalCallSpell(player, gem.Effect))
        {
            var session = ProjectilesFor(player);
            StartVaalImmortalCall(player, session, skillId, AreaTime(player));
            return true;
        }
        if (gem.Name is not null && vaalUse is not null && VaalAuras.ContainsKey(SkillsFor(player).EffectId(gem.Effect) ?? ""))
            return await ActivateVaalAura(player, skillId, Current);
        if (gem.Name is not null && vaalUse is not null && SkillsFor(player).EffectId(gem.Effect) is { } auraEffect
            && TimedVaalAuraBuffRows.TryGetValue(auraEffect, out ushort auraRow))
            return await ActivateTimedVaalAura(player, skillId, auraEffect, auraRow, Current);
        if (gem.Name is not null && IsBlessing(SupportedSkill(player, skillId)))
            return ScheduleBlessing(player, skillId, recoveryGeneration, Current);
        // A guard skill cast from the skill bar starts its buff in CastSelfBuff. An instant cast passes guardStart, and
        // the caller starts it after the instant action, so the buff, its stats and its session records cannot take
        // the send gate first. Its readiness was admitted before any cost or cooldown use was spent (Cooldowns.cs).
        if (gem.Name is not null && guardStart is not null && GuardSpellEffect(SkillsFor(player).EffectId(gem.Effect)))
            return true;
        // The self buffs a player casts by hand: the guards, Phase Run, Blood Rage.
        if (gem.Name is not null && await CastSelfBuff(player, skillId, gem.Effect)) return true;
        if (gem.Name is not null && IsRighteousFire(gem.Effect))
            await ActivateRighteousFire(player, skillId, gem.Effect, gem.Level, gem.Quality, gem.Gem);
        else
            await LaunchProjectileForUse(player, onShieldChargeReady, skillId, from, to, recoveryGeneration, targetId, sequence: sequence,
                actionToken: actionToken, cooldownUse: cooldownUse, criticalRoll: criticalRoll, channelGeneration: channelGeneration,
                swingAt: cooldownUse?.Attack?.At ?? swingAt, leap: leap, previousNextAttackAt: reservedNextAttackAt);
        return CompleteSupportedUse(true);
    }

    // Record only after payment/cooldown/effect admission. The instant-location path calls this after its own
    // final 0x1ce at-gate commit, so a stale or cancelled Frostblink cannot activate a recent event.
    static void RecordAcceptedGemUse(Player player, int effect, (int X, int Y) aim) =>
        RecordAcceptedGemUseAt(player, effect, aim, AreaTime(player));

    internal static void RecordAcceptedGemUseAt(Player player, int effect, (int X, int Y) aim, double acceptedAt)
    {
        RecordUsedSkillLocked(player, acceptedAt);
        bool movement = SkillsFor(player).IsMovement(effect);
        if (movement) player.RecentEvents.Record(StatConditions.UsedMovementSkill, acceptedAt);
        if (movement && IsDashEffect(SkillsFor(player).EffectId(effect)))
            player.RecentEvents.Record(StatConditions.UsedDash, acceptedAt);
        if (SkillsFor(player).HasType(effect, "Fire"))
            player.RecentEvents.Record(StatConditions.UsedFireSkill, acceptedAt);
        if (SkillsFor(player).HasType(effect, "Cold"))
            player.RecentEvents.Record(StatConditions.UsedColdSkill, acceptedAt);
        if (SkillsFor(player).HasType(effect, "Minion"))
            player.RecentEvents.Record(UsedMinionSkillRecently, acceptedAt);
        if (SkillsFor(player).IsBrand(effect))
            RecordBrandSkillUsedLocked(player, acceptedAt);
        SpiritBurstOnSkillUse(player, aim, acceptedAt);
        TriggerCraftedWeaponSpells(player, aim, acceptedAt);
    }

    // Lockstep instant replies join the ordinary action sequence. At 0x14219c1f0, flag 0x100 reaches
    // 0x141e98bb0 and suppresses local action-event dispatch; 0x20 is only the transported target marker.
    public static byte[] PlayerInstantAction(Player player, ushort skill, (int X, int Y) aim, uint? target = null)
    {
        return InstanceProtocol.InstantAction(player.Id, skill, aim.X, aim.Y, target,
            player.Connection.LockstepClient ? NextLockstepSequence(player) : (ushort)0xffff,
            player.Connection.LockstepClient ? (ushort)0x101 : (ushort)0);
    }

    // An instant skill (C→S 0x191): its effect, then S→C 0x1ce, which has the client run the skill's action without animation
    // and consume a use of it: the skill bar's cooldown starts (the tooltip showed the cooldown, the bar did not run it).
    public static async Task UseInstantSkill(Player player,
        ushort skillId, uint targetId, (int X, int Y) at)
    {
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId);
        bool guard = gem.Name is not null && GuardSpellEffect(SkillsFor(player).EffectId(gem.Effect));
        if (gem.Name is not null && GemSkillInstanceDisabled(player, gem.Effect, skillId) is not null) return;
        if (gem.Name is not null && InstantLocationSkill.Supported(SkillsFor(player), gem.Effect))
        {
            await UseInstantSkillAtLocation(player, skillId, default, targetId);
            return;
        }
        if (gem.Name is null || !ObjectInstantSupported(player, gem.Effect)) return;
        var cooldownPlan = CooldownForSkill(player, skillId);
        int epoch;
        epoch = player.ProjectileEpoch;
        if (!player.ProjectileActive(epoch) || player.Paused || !player.Connection.PlayerSent || player.PendingTransfer is not null) return;
        if (!TryBeginSkillCooldownUse(player, skillId, gem.Effect,
            cooldownPlan,
            true, () => { }, out var cooldownUse)) return;
        // The guard starts after the instant action, so the action's record carries no guard work before it and the
        // guard's one lock entry goes out right behind it (StartGuard publishes it).
        Action? guardStart = guard
            ? () =>
            {
                var session = ProjectilesFor(player);
                StartTriggeredGuard(player, session, skillId, AreaTime(player));
            }
            : null;
        if (await CastGemSkill(player, skillId, at, at, targetId, cooldownUse: cooldownUse,
                guardStart: guardStart))
        {
            // A guard's cooldown is server-owned and held at full while its buff runs. Publish that held rate-0 entry
            // before the instant action: the client skips its press prediction (startCooldown) when a zero-rate entry
            // already exists, so the bar's ring does not sweep before the freeze (issue #1127). The instant action's
            // native use refresh then briefly restores the recovery rate, which StartTriggeredGuard's held entry behind
            // it freezes again; the remaining time stays full throughout, so neither flicker moves the ring.
            if (guard && GuardCooldownPreEntry(player, skillId, gem.Effect) is { Length: > 0 } preEntry)
                await Send(player, preEntry, "0x02bc guard cooldown held before its instant action");
            bool committed = false;
            await SendWorldRecord(player, player.Id, [],
                $"0x01ce player skill 0x{skillId:x4}: instant action (its use and cooldown)",
                atGate: () =>
                {
                    if (!player.ProjectileActive(epoch) || player.PlayerLife <= 0 || player.Paused
                        || ActorSkillInstanceDisabled(player, gem.Effect, skillId) is not null
                        || cooldownUse?.CommitNativeUse(player, skillId) == false) return null;
                    committed = true;
                    return PlayerInstantAction(player, skillId, at, targetId);
                });
            if (committed) guardStart?.Invoke();
            await SendNativeCooldownRefresh(player);
        }
    }

    static bool ObjectInstantSupported(Player player, int effect) =>
        InstantLocationSkill.ObjectTargetSupported(SkillsFor(player), effect) || InPlaceSelfBuff(player, effect)
        // The client's 0x2064 (skill_is_instant) is what EffectStats publishes from IsInstant; reading the type here
        // skips a full supported-skill stat build (51 ms on a live press, measured 2026-11-18).
        || !SkillsFor(player).IsMovement(effect) && (SkillsFor(player).IsInstant(effect)
            // Vaal Immortal Call has a 0 cast time but no Instant active-skill type (only InstantShiftAttackForLeftMouse),
            // so accept the instant request (0x191) for it as well as the ordinary cast.
            || VaalImmortalCallSpell(player, effect));

    public static async Task UseInstantSkillAtLocation(Player player, ushort skillId, (uint X, uint Y) aim, uint? targetObjectId = null)
    {
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is null || GemSkillInstanceDisabled(player, gem.Effect, skillId) is not null) return;
        if (!InstantLocationSkill.Supported(SkillsFor(player), gem.Effect))
        {
            // A self buff aimed at empty ground uses 0x190 too; it still applies at its caster.
            if (ObjectInstantSupported(player, gem.Effect))
                await UseInstantSkill(player, skillId, player.Id, player.Walk.Position(AreaTime(player)));
            return;
        }
        var own = SkillsFor(player).EffectStats(gem.Effect, gem.Level, gem.Quality).ToDictionary(kv => kv.Item1, kv => kv.Item2);
        int epoch;
        (int X, int Y) from, to;
        // GemSkills places the skill's own stat over the player's; use the same inputs for native distance resolution.
        Func<uint, int> CurrentStats()
        {
            var playerStats = FinalPlayerStats(player).ToDictionary(kv => kv.Item1, kv => kv.Item2);
            return name => name is var k ? own.TryGetValue(k, out int value) ? value : playerStats.GetValueOrDefault(k) : 0;
        }
        epoch = player.ProjectileEpoch;
        if (targetObjectId is uint target)
        {
            if (!InstantLocationSkill.TryResolveObject(player, epoch, target, InTown(player), CurrentStats(),
                out from, out to, out _)) return;
        }
        else if (!InstantLocationSkill.TryResolve(player, epoch, aim, InTown(player), CurrentStats(), out from, out to)) return;
        if (!TryBeginSkillCooldownUse(player, skillId, gem.Effect,
            CooldownForSkill(player, skillId),
            true, () => { }, out var cooldownUse)) return;
        if (!await CastGemSkill(player, skillId, from, to, cooldownUse: cooldownUse, locationInstant: true)) return;
        bool accepted = false;
        // Keep the raw aim: IceDash resolves it from the client's position when 0x1ce runs. Sending an already rounded
        // endpoint can normalize a short diagonal twice and move another cell. Resolve the server copy at this gate too.
        await SendWorldRecord(player, player.Id, [],
            $"0x01ce player skill 0x{skillId:x4}: instant {(targetObjectId is uint id ? $"on 0x{id:x}" : $"location ({aim.X}, {aim.Y})")}", atGate: () =>
            {
                using (ProjectileMutation(player))
                {
                    var current = GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId);
                    if (current != gem || GemSkillInstanceDisabled(player, gem.Effect, skillId) is not null) return null;
                    (int X, int Y) rawAim;
                    from = player.Walk.Position(ProjectileWorldTime(player));
                    if (targetObjectId is uint target)
                    {
                        if (!InstantLocationSkill.TryCommitObject(player, epoch, target, InTown(player), CurrentStats(),
                            out rawAim, out to)) return null;
                    }
                    else
                    {
                        if (!InstantLocationSkill.TryCommit(player, epoch, aim, InTown(player), CurrentStats(), out to)) return null;
                        rawAim = ((int)aim.X, (int)aim.Y);
                    }
                    if (cooldownUse?.CommitNativeUse(player, skillId) == false) return null;
                    accepted = true;
                    return PlayerInstantAction(player, skillId, rawAim, targetObjectId);
                }
            });
        if (accepted)
        {
            RecordAcceptedGemUse(player, gem.Effect, to);
            if (SkillsFor(player).IsTravel(gem.Effect)) { QueueFlaskTrigger(player, FlaskEnchantments.Trigger.Travel); LoyaltyTravelSkillTriggered(player); }
            await FrostblinkAreas(player, gem, from, to, cooldownUse);
            await SendNativeCooldownRefresh(player);
        }
    }

    public static async Task FrostblinkAreas(Player player,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) from, (int X, int Y) to,
        CooldownUseTicket? use)
    {
        var own = SupportedSkill(player, gem.Id).Stats;
        var playerStats = CharacterPlayerStats(player);
        int Own(uint name) => GolemStat(own, name);
        int Player(uint name) => GolemStat(playerStats, name);
        int groundRadius = SkillRadius(player, 16, own, spell: true, gem.Effect, playerStats);
        double duration = SkillDurationModifiers.Seconds(Own(Stat.BaseSkillEffectDuration), Player, Own);
        var crit = CaptureSkillCriticalRoll(player, gem.Id);
        var sacrifice = new SacrificeUse(player, gem.Id, gem.Effect, gem.Gem);
        if (use?.Current(player) != true) return;
        double until = AreaTime(player) + duration;
        const string art = "Metadata/Effects/Spells/ground_effects_v3/chilled/chilled_grd.ao";
        AddChilledGroundLocked(player, from, until, groundRadius, art);
        AddChilledGroundLocked(player, to, until, groundRadius, art);
        foreach (var centre in new[] { from, to })
            await AreaSkillHit(player, gem.Id, gem, centre, centre,
                immediate: true, cooldownUse: use, criticalRoll: crit, sacrifice: sacrifice);
    }

    static async Task FrostblinkWintryBlastCast(Player player, ushort skillId,
        (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) aim, uint? targetId,
        int repeat, int? actionToken, CooldownUseTicket? cooldownUse, SkillCriticalRoll? criticalRoll,
        SacrificeUse sacrifice, double repeatDelay, double damageMore, double? swingAt)
    {
        int? castDuration = CastMs(player, gem.Effect, skillId);
        if (castDuration is null) return;
        double delay = castDuration.Value / 1000.0
            * (repeat + Animations.SpellAreaRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject))
            + repeatDelay;
        if (swingAt is { } swung) delay -= AreaTime(player) - swung;
        await AreaDelay(player, Math.Max(0.05, delay));
        await WhilePaused(player);
        (int X, int Y) origin, landing;
        using (ProjectileMutation(player))
        {
            if (player.Closed || player.LeavingArea || player.PlayerLife <= 0 || InTown(player)) return;
            if (cooldownUse is not null ? !cooldownUse.Current(player) : actionToken is int token && player.ActionCount != token)
                return;
            double now = ProjectileWorldTime(player);
            origin = player.Walk.Position(now);
            var own = SupportedSkill(player, skillId).Stats;
            var playerStats = CharacterPlayerStats(player);
            int travelPct = GolemStat(own, Stat.SkillMaximumTravelDistancePlusPct)
                + GolemStat(playerStats, Stat.SkillMaximumTravelDistancePlusPct);
            var resolvedAim = aim;
            if (targetId is uint tid && player.Area.Monsters.TryGetValue(tid, out var target)
                && !target.Dead && target.Life > 0 && target.Variety is { ObjectSize: >= 1 and <= 128 } variety)
            {
                var targetPos = target.Walk.Position(now);
                if (InstantLocationSkill.ObjectAim(origin, targetPos, 1, variety.ObjectSize - 1) is { } trace)
                    resolvedAim = trace.End;
                else
                    resolvedAim = targetPos;
            }
            if (!InstantLocationSkill.TryResolveLanding(player, origin, resolvedAim, travelPct, allowTerrainFallback: true, out landing))
                landing = origin;
            if (landing != origin)
                player.Walk.Teleport(landing.X, landing.Y, now);
        }
        foreach (var centre in landing != origin ? new[] { origin, landing } : new[] { origin })
            await AreaSkillHit(player, skillId, gem, centre, centre, immediate: true,
                actionToken: actionToken, cooldownUse: cooldownUse, criticalRoll: criticalRoll,
                repeat: repeat, sacrifice: sacrifice, damageMore: damageMore);
    }

    // Aura skills toggle: the first cast reserves their mana and puts their buff on the player, the next cast turns them
    // off. The reserved mana is sent in the Life pool and in every later 0x1b1 mana update; the client does not derive it.
    public static async Task<bool> ToggleAura(Player player,
        ushort skillId, int effect, string name, SkillCatalog.ReservationCost reservation, int level, int quality)
    {
        string? effectId = SkillsFor(player).EffectId(effect);
        if (effectId is null || !AuraCatalog.Supported(effectId))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {name}: reservation is known but this aura's effect is not implemented yet");
            return false;
        }
        // Both stance gems have one atomic source/cooldown/action admission. A direct generic toggle would bypass it.
        if (StanceCatalog.IsSource(effectId)) return false;
        AuraCatalog.Aura? ended = null;
        AuraCatalog.Aura? started = null;
        // Locked commit backstop: a source may have changed after dispatch/cooldown preflight.
        if (player.Closed || player.LeavingArea || player.PlayerLife == 0 || InTown(player)
            || EternalBlessingDisables(player, skillId) || OwnAuraSkillRestricted(player, effect, skillId)
            || CastingSpell(player, effect) && SpellsDisabled(player)) return false;
        var supported = SupportedSkill(player, skillId);
        if (effectId == "TemporalRift" && player.Auras.ContainsKey(skillId))
        {
            var packets = RecallTemporalRiftLocked(player);
            if (packets.Length > 0) player.Minions.OutputQueue.Add(new(packets, "Temporal Rift recalls resources and position"));
            started = player.Auras[skillId];
        }
        else if (player.Auras.Remove(skillId, out var old))
        { ended = old; EndReservationRuntimeLocked(player, old); }
        else if (player.Auras.Values.FirstOrDefault(a => a.EffectId == effectId) is { } same
            && !(IsBlessingSkill(supported) && IsBlessingAura(same))
            && !(supported.Stats.GetValueOrDefault(Stat.SkillAuraAlsoDisablesNonBlessingManaReservationSkills) != 0
                && AuraSupport(player, skillId).AffectsSelf && OrdinaryManaAura(same)))
        {
            // One aura of a kind at a time: a second gem of the same aura can't start it.
            Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {name}: refused, {same.Name} already runs (skill 0x{same.SkillId:x4})");
            return false;
        }
        else if (AuraCatalog.IsAspect(effectId) && player.Auras.Values.Any(a => AuraCatalog.IsAspect(a.EffectId)))
            return false;
        else
        {
            if (!GuardianBlessingAvailableLocked(player, supported)) return false;
            var (supportEffect, affectsSelf) = AuraSupport(player, skillId);
            var context = ReservationContext(player, skillId);
            string? stance = AuraCatalog.IsStance(effectId) ? player.Stance : null;
            bool hasSource = OwnAuraRestriction.IsAura(supported.Stats);
            var (artVariation, hideVisual, sourceVisual, buffVisual) =
                ReservationCosmeticVisuals(player, skillId, effectId, hasSource);
            var aura = new AuraCatalog.Aura(skillId, effect, effectId, name, player.NextBuff++,
                AuraCatalog.BuffRow(effectId, stance), reservation.ManaFlat, reservation.ManaPercent,
                Level: level, Quality: quality, LifeFlat: reservation.LifeFlat, LifePercent: reservation.LifePercent,
                SourceBuff: hasSource ? player.NextBuff++ : null,
                WornGrant: supported.WornGrant,
                SkillStats: [.. supported.Stats.Select(kv => (kv.Key, kv.Value))],
                SupportEffectPct: supportEffect, AffectsSelf: affectsSelf, Stance: stance,
                ReservesLife: context.Life, ReservationEfficiencyPct: context.Efficiency, IsHerald: IsHeraldAura(effectId),
                ArtVariation: artVariation, HideVisual: hideVisual, SourceVisual: sourceVisual, BuffVisual: buffVisual);
            aura = WithCasterAuraStamps(aura, CharacterStatSources(player).Stats);
            bool blessing = IsBlessingAura(aura);
            bool eternal = EternalBlessingAura(aura);
            var proposedAuras = player.Auras.Values.Where(a => (!blessing || !IsBlessingAura(a))
                && (!eternal || !OrdinaryManaAura(a))).Append(aura).ToArray();
            long reserved = (long)AuraCatalog.RequiredMana(player.Pools, proposedAuras)
                + (eternal ? 0 : PetrifiedBlood.RequiredMana(player.Pools, player.PetrifiedBlood))
                + PlacedSkills.Reserved(player.Pools, player.Placed.All.Values, false)
                + CooldownTriggerReservation(player, life: false);
            var lifeReservation = LifeReservationFor(player, proposedAuras);
            int requiredLife = lifeReservation.Required;
            if (reserved > player.Pools.Mana || requiredLife >= player.Pools.Life)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {name}: needs {reserved} mana and " +
                    $"{requiredLife} life reserved, maximum is {player.Pools.Mana} mana and {player.Pools.Life} life");
                return false;
            }
            if (blessing) EndOtherBlessingsLocked(player);
            player.Auras[skillId] = aura;
            StartGuardianBlessingLocked(player, aura, supported);
            if (eternal) SuspendManaReservedAurasLocked(player);
            started = aura;
            ClampReservedResources(player);
        }
        player.Pools = PoolsFor(player, player.Pools.Level);
        player.EnergyShield.SetMaximum(player.Pools.EnergyShield);
        player.ApplyMovementSpeed();
        player.AuraStatsDirty = true;
        CharacterSaves.SetAuraActive(CharacterName(player), effectId, started is not null);

        await SendOwnAuraChanges(player);
        await SendCharacterStats(player, PlayerStatChange.Auras);
        await SendPlayerMana(player,
            $"0x01b1 player mana {player.PlayerMana}/{player.Pools.Mana}, {ReservedMana(player)} reserved");
        await SendPlayerEnergyShield(player);
        // The toggle's skill refresh can empty the aura's own bound slot (live 2026-11-22: Malevolence with Eternal
        // Blessing left Q), and the full refresh above resends the skill list without the bar, so the saved bar goes out
        // again after it.
        await RestoreSkillBarAfterActorUpdate(player);
        return true;
    }

    static ushort OwnAuraBuffTag(AuraCatalog.Aura aura) =>
        StanceCatalog.SourceFamily(aura.EffectId) == StanceCatalog.BloodAndSand
            ? (ushort)BuffTag.BloodAndSand : (ushort)0;

    public static IEnumerable<Buff> AuraBuffs(uint id, AuraCatalog.Aura aura, double? seconds = null)
    {
        ushort tag = OwnAuraBuffTag(aura);
        if (seconds is not double duration)
        {
            foreach (var buff in AuraCatalog.Buffs(aura, id, Skills.ActionHash(aura.Effect), aura.AffectsSelf ? null : (byte?)0, tag))
                yield return buff;
            yield break;
        }
        if (aura.SourceBuff is not null)
            yield return new(aura.BuffRow, id, aura.SkillId, AuraSource: true, DisplayState: 0, Visual: aura.SourceVisual, Tag: tag);
        yield return new(aura.BuffRow, id, aura.SkillId, U68: Buff.Seconds(duration),
            ActiveSkill: Skills.ActionHash(aura.Effect) is ushort hash ? (aura.SkillId, hash) : null,
            Visual: aura.BuffVisual, Tag: tag);
    }

    public static async Task DisableReservations(Player player, string name)
    {
        bool all = name.Equals("all", StringComparison.OrdinalIgnoreCase);
        string compactName = string.Concat(name.Where(c => !char.IsWhiteSpace(c)));
        string stanceFamily = compactName.Equals(StanceCatalog.BloodAndSand, StringComparison.OrdinalIgnoreCase)
            ? StanceCatalog.BloodAndSand
            : compactName.Equals(StanceCatalog.FleshAndStone, StringComparison.OrdinalIgnoreCase)
                ? StanceCatalog.FleshAndStone : "";
        bool MatchesSource(string effectId) =>
            stanceFamily.Length > 0 && StanceCatalog.SourceFamily(effectId) == stanceFamily;
        var matches = player.Auras.Values.Where(a => all
            || a.EffectId.Equals(name, StringComparison.OrdinalIgnoreCase)
            || MatchesSource(a.EffectId)
            || a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        var saved = CharacterSaves.Load(CharacterName(player));
        var remembered = (saved?.ActiveAuras ?? []).Where(id => all || id.Equals(name, StringComparison.OrdinalIgnoreCase)
            || MatchesSource(id)
            || GemSkillEffects(player).Any(g => SkillsFor(player).EffectId(g.Effect) == id
                && g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        bool petrified = (all || name.Equals("Petrified Blood", StringComparison.OrdinalIgnoreCase)
            || name.Equals(PetrifiedBlood.EffectId, StringComparison.OrdinalIgnoreCase))
            && (player.PetrifiedBlood.Active || saved?.PetrifiedBlood == true);
        if (!all && matches.Length == 0 && remembered.Length == 0 && !petrified)
            throw new ArgumentException("No active reservation matches that name; use /auras to list them.");
        foreach (string id in remembered) CharacterSaves.SetAuraActive(CharacterName(player), id, false);
        if (petrified)
        { player.PetrifiedBlood.Suspend(); CharacterSaves.SetPetrifiedBlood(CharacterName(player), false); }
        foreach (var aura in matches)
        {
            player.Auras.Remove(aura.SkillId);
            CharacterSaves.SetAuraActive(CharacterName(player), aura.EffectId, false);
            EndReservationRuntimeLocked(player, aura);
        }
        if (matches.Any(a => StanceCatalog.IsStance(a.EffectId)))
            ReconcileStanceSelectionLocked(player, persist: true);
        player.AuraStatsDirty = true;
        player.Pools = BuildPoolsFor(player, player.Pools.Level);
        player.ApplyMovementSpeed();
        await SendOwnAuraChanges(player);
        await SendCharacterStats(player, PlayerStatChange.Auras);
        await SendPlayerMana(player);
    }

    public static Task LaunchProjectile(Player player,
        ushort skillId, (int X, int Y) from, (int X, int Y) to, int recoveryGeneration, uint targetId = 0, ushort sequence = 0,
        int? actionToken = null, CooldownUseTicket? cooldownUse = null, SkillCriticalRoll? criticalRoll = null,
        int? channelGeneration = null, double? swingAt = null, LeapSlam.Use? leap = null) =>
        LaunchProjectileForUse(player, null, skillId, from, to, recoveryGeneration, targetId, sequence, actionToken,
            cooldownUse, criticalRoll, channelGeneration, swingAt, leap);

    static Task LaunchProjectileForUse(Player player, Action<Action>? onShieldChargeReady,
        ushort skillId, (int X, int Y) from, (int X, int Y) to, int recoveryGeneration, uint targetId = 0, ushort sequence = 0,
        int? actionToken = null, CooldownUseTicket? cooldownUse = null, SkillCriticalRoll? criticalRoll = null,
        int? channelGeneration = null, double? swingAt = null, LeapSlam.Use? leap = null, double? previousNextAttackAt = null)
    {
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId);
        if (gem.Name is null) return Task.CompletedTask;
        // Overheat Support: "consume all Fortification on use" - once for the use, before its hit or any projectile it
        // releases, so every ailment this use inflicts gets the more damage the consumed stacks paid for.
        var overheatStats = SupportedSkill(player, skillId).Stats;
        OverheatConsumeLocked(player, skillId,
            name => name is var key ? overheatStats.GetValueOrDefault(key) : 0,
            AreaTime(player));
        if (ArrowRain(SkillsFor(player).EffectId(gem.Effect)))
        {
            var rainCritical = criticalRoll ?? CaptureSkillCriticalRoll(player, skillId);
            var session = ProjectilesFor(player);
            session.Advance();
            var source = SupportedSkill(player, skillId);
            double release = (swingAt ?? AreaTime(player)) + AttackReleaseDelay(AttackContactDelay(player, SupportedAttackSeconds(player, skillId), false));
            session.Runtime.Schedule(Math.Max(session.Runtime.Now, GameServer.Core.Projectiles.ProjectileRuntime.Time(release)), () =>
            {
                if (session.Active && player.PlayerLife > 0 && player.ActionInterruptionGeneration == recoveryGeneration
                    && SameTriggerSource(source, SupportedSkill(player, skillId)) && GemSkillInstanceDisabled(player, gem.Effect, skillId) is null)
                    ReleaseArrowRain(session, skillId, gem, to, rainCritical, sequence);
            });
            return Task.CompletedTask;
        }
        // Winter Orb's orb fires its own volleys while the channel runs (WinterOrb.cs), not once at the cast.
        if (IsWinterOrb(gem.Effect)) return Task.CompletedTask;
        // Snipe charges stages while channelling and fires on release (Snipe.cs), not at channel start.
        if (IsSnipe(gem.Effect)) return Task.CompletedTask;
        bool cryExerted;
        cryExerted = ConsumeIntimidatingCryExertLocked(player,
            channelGeneration is null && IntimidatingCryCanExert(player, skillId, gem.Effect));
        // The paid charge owns one exertion decision for every path hit and its landing.
        int chargeBonus = ShieldChargeBonusPct(player, skillId, gem.Effect);
        if (chargeBonus != 0)
        {
            void StartCharge()
            {
                int chargeAction = actionToken ?? player.ActionCount;
                int token = BeginShieldCharge(player, skillId, chargeBonus, to.X, to.Y, AreaTime(player), chargeAction,
                    out bool charged, targetObjectId: targetId, previousNextAttackAt: previousNextAttackAt);
                _ = RunShieldChargeHits(player, token, charged, skillId, sequence, chargeAction, cooldownUse, cryExerted, criticalRoll);
            }
            if (onShieldChargeReady is null) StartCharge();
            else onShieldChargeReady(StartCharge);
            return Task.CompletedTask;
        }
        criticalRoll ??= CaptureSkillCriticalRoll(player, skillId);
        if (GatewayProjectileSkillProfiles.IsMoltenStrike(SkillsFor(player).EffectId(gem.Effect)))
            return ScheduleMoltenStrike(player, skillId, gem, from, to, recoveryGeneration,
                targetId, sequence, actionToken, cooldownUse, criticalRoll, swingAt, cryExerted);
        if (IsLightningStrike(gem.Effect) || IsFrostBlades(gem.Effect))
            return ScheduleProjectileStrike(player, skillId, gem, from, to, targetId,
                recoveryGeneration, cooldownUse, criticalRoll,
                sequence, swingAt, cryExerted);
        if (IsSmite(gem.Effect))
            return ScheduleSmite(player, skillId, gem, from, to, targetId,
                recoveryGeneration, cooldownUse, criticalRoll, sequence, swingAt, cryExerted);
        if (IsStaticStrike(gem.Effect))
            return ScheduleStaticStrike(player, skillId, gem, from, to, targetId,
                recoveryGeneration, cooldownUse, criticalRoll, sequence, swingAt, cryExerted);
        var supported = SupportedSkill(player, skillId);
        int seals = UnleashSeals(player, skillId, supported, ProjectileWorldTime(player), spend: true);
        double reoccurrenceMore = Math.Max(0, 1 + GolemStat(supported.Stats, Stat.SupportSpellRapidFireRepeatUseDamagePlusPctFinal) / 100d);
        if (UsesProjectileRuntime(gem.Effect) && !IsVolcanicFissure(gem.Effect))
            return ScheduleGemProjectile(player, skillId, gem, to,
                actionToken ?? cooldownUse?.ActionToken ?? player.ActionCount, recoveryGeneration, cooldownUse, criticalRoll,
                sequence, swingAt, cryExerted, seals, reoccurrenceMore);
        // A channelled skill (Cyclone, ActiveSkillType 47) hits as it starts: its spin is already in motion, and the later
        // sweeps follow on the attack timer (RunChannel).
        bool channelled = Supports.Of(gem.Effect)?.Types.Contains(47) == true;
        // A spell's repeats (SpellRepeats) land again a cast time apart, with the use's critical roll; a melee attack's
        // Multistrike repeats are swings of the same paid use, each a full attack time apart.
        int spellRepeats = SpellRepeats(player, skillId, gem.Effect);
        int meleeRepeats = channelled || spellRepeats > 0 ? 0 : MeleeAttackRepeats(player, skillId);
        double repeatInterval = meleeRepeats > 0 ? SupportedAttackSeconds(player, skillId, sequence) : 0;
        if (meleeRepeats > 0) actionToken ??= player.ActionCount;
        var repeatStats = meleeRepeats > 0 ? SupportedSkill(player, skillId).Stats : null;
        double? repeatRuthless = repeatStats is null ? null : RuthlessBlowMore(player, skillId,
            key => repeatStats.GetValueOrDefault(key));
        // Fist of War: every ancestral_slam_interval_duration the next slam use is the big hit (more damage and area).
        double fistMore = 1, fistRadius = 1;
        bool spell = SkillsFor(player).IsSpell(gem.Effect);
        if (!spell && !channelled)
        {
            var fistStats = SupportedSkill(player, skillId).Stats;
            int Fist(uint key) => fistStats.GetValueOrDefault(key);
            if (Fist(Stat.SupportAncestralSlamBigHitMaxCount) > 0)
            {
                double interval = Math.Max(0, Fist(Stat.AncestralSlamIntervalDuration)) / 1000.0;
                double now = AreaTime(player);
                bool ready;
                ready = now >= player.FistOfWarAt.GetValueOrDefault(skillId, double.NegativeInfinity);
                if (ready) player.FistOfWarAt[skillId] = now + interval;
                if (ready)
                {
                    fistMore = 1 + Fist(Stat.SupportAncestralSlamBigHitDamageWithHitsAndAilmentsPlusPctFinal) / 100.0;
                    fistRadius = SkillArea.RadiusMultiplier(Fist(Stat.SupportAncestralSlamBigHitAreaPlusPct));
                }
            }
        }
        if (leap is not null && IsLeapSlam(player, gem.Effect))
            return ScheduleLeapSlam(player, skillId, gem, leap, criticalRoll,
                sequence, fistMore, fistRadius, cryExerted);
        var sacrifice = new SacrificeUse(player, skillId, gem.Effect, gem.Gem);
        // Spell Cascade: the spell also lands in the areas beside the target, one per side along the aim and, with
        // support_spell_cascade_sideways, one per side across it. Each centre is one effective radius from the aim
        // (live 2026-11-04: Reap at the character's area of effect had radius 28.5 and repeats 28-29 cells apart; the
        // client draws each effect half a radius toward the caster, so the hit centre is the aim +- one radius).
        List<(int X, int Y)> cascadeAreas = spell ? SpellCascadeAreas(player, gem.Effect, supported.Stats, from, to) : [];
        var jobs = new List<Task>();
        // Discharge: one use, one count of its charges, for every repeat and cascade area (DischargeUse).
        var discharge = DischargeUseFor(player, gem.Effect, gem.Level, gem.Quality);
        bool darkBargain = IsDarkBargain(player, gem.Effect);
        bool wintryBlast = SkillsFor(player).EffectId(gem.Effect) == "FrostblinkAltX";
        // Storm Call sets a marker at every landing (each repeat and cascade area) that strikes later (StormCall.cs).
        bool stormCall = IsStormCall(SkillsFor(player).EffectId(gem.Effect));
        var firestorm = IsFirestorm(player, gem.Effect) && CastMs(player, gem.Effect, skillId) is int stormCast
            ? (Session: ProjectilesFor(player), Cast: stormCast) : ((ProjectileSession Session, int Cast)?)null;
        bool CanReleaseStorm() => cooldownUse?.Current(player) ?? (actionToken is null || player.ActionCount == actionToken);
        double sealGap = seals == 0 ? 0 : (CastMs(player, gem.Effect, skillId) ?? 0) / 10000d;
        for (int release = 0; release <= Math.Max(spellRepeats, meleeRepeats) + seals; release++)
        {
            int repeat = Math.Max(0, release - seals);
            bool reoccurrence = release > 0 && release <= seals;
            double sealDelay = Math.Min(release, seals) * sealGap;
            double more = reoccurrence ? reoccurrenceMore : 1;
            if (firestorm is { } storm)
            {
                double windup = sealDelay + storm.Cast / 1000d
                    * (repeat + Animations.SpellAreaRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject));
                foreach (var area in cascadeAreas.Prepend(to))
                    storm.Session.ScheduleFirestorm(gem, area, windup, CanReleaseStorm, sacrifice, criticalRoll, repeat, more);
                continue;
            }
            if (stormCall)
            {
                foreach (var area in cascadeAreas.Prepend(to))
                    jobs.Add(PlaceStormCall(player, skillId, gem, area, repeat, actionToken, cooldownUse,
                        criticalRoll, sacrifice, sealDelay, more));
                continue;
            }
            // Dark Bargain explodes at skeletons (or the player), not at the aim: each repeat pays and chains again.
            if (darkBargain)
            {
                jobs.Add(DarkBargainCast(player, skillId, gem, to, repeat, actionToken, cooldownUse, criticalRoll, sacrifice, sealDelay, more));
                continue;
            }
            // Frostblink of Wintry Blast (IceDash, 850 ms cast): teleports at the cast contact frame using the native
            // 32/42-cell IceDash landing and hits both ends without creating chilled ground.
            if (wintryBlast)
            {
                jobs.Add(FrostblinkWintryBlastCast(player, skillId, gem, to, targetId, repeat, actionToken, cooldownUse,
                    criticalRoll, sacrifice, repeat * repeatInterval + sealDelay, more, swingAt));
                continue;
            }
            void Hit((int X, int Y) centre) => jobs.Add(AreaSkillHitForUse(repeatRuthless, player, skillId, gem, from,
                centre, immediate: channelled, sequence: sequence, actionToken: actionToken, cooldownUse: cooldownUse,
                criticalRoll: criticalRoll, channelGeneration: channelGeneration, targetId: targetId, repeat: repeat, swingAt: swingAt,
                repeatDelay: repeat * repeatInterval + sealDelay, radiusMore: fistRadius, sacrifice: sacrifice,
                damageMore: fistMore * more * (meleeRepeats > 0 ? MeleeRepeatDamageMore(player, skillId, repeat) : 1), discharge: discharge, cryExerted: cryExerted));
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

    sealed class ChannelEndTracker { public int FinishedGeneration = -1; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Player, ChannelEndTracker> ChannelEnds = new();

    public static bool IsDivineIre(Player player, int effect) =>
        SkillsFor(player).EffectId(effect) is "DivineIre" or "DivineIreAltX" or "DivineIreAltY" or "DivineIreRoyale";

    public static async Task EmitPredictiveChannelFinished(Player player, int generation, ushort sequence)
    {
        if (player.Closed || player.LeavingArea || player.Connection.LockstepClient) return;
        var tracker = ChannelEnds.GetOrCreateValue(player);
        lock (tracker)
        {
            if (generation <= tracker.FinishedGeneration) return;
            tracker.FinishedGeneration = generation;
        }
        await SendWorldRecord(player, player.Id, InstanceProtocol.ChannelFinished(player.Id, sequence),
            $"0x01ae channel 0x{sequence:x4} ended");
    }

    public static async Task RunChannel(Player player,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, int action,
        (int X, int Y) initialAim = default)
    {
        int generation = player.ChannelGeneration;
        ushort channelSequence = player.ChannelSkillSequence;
        double channelStarted = AreaTime(player);
        bool Running() => ChannelCurrent(player, skillId, action, generation);
        // Winter Orb's orb and its firing loop live in WinterOrb.cs: the loop starts with the channel and outlives it,
        // while this channel's cast ticks only build the orb's stages.
        uint orbEffect = 0;
        bool snipe = IsSnipe(gem.Effect);
        bool firstSnipeStage = snipe;
        bool divineIre = IsDivineIre(player, gem.Effect);
        int divineIreMaxStages = divineIre && SkillsFor(player).EffectId(gem.Effect) == "DivineIreRoyale" ? 20 : 10;
        int divineIreStages = 0;
        try
        {
            if (IsWinterOrb(gem.Effect))
                orbEffect = await WinterOrbStart(player, skillId, gem, action);
            else if (snipe)
                await SnipeStart(player, skillId, gem, action, initialAim);
            else if (divineIre)
            {
                divineIreStages = 1;
                await SendWorldRecord(player, player.Id,
                    InstanceEntities.StatsUpdate(player.Id, [(Stat.DivineTempestCurrentNumberOfStages, divineIreStages)]),
                    $"0x01b3 Divine Ire stage {divineIreStages}");
            }
            // Overheat's per-3 value, read once for the channel rather than a skill build on every tick.
            const uint overheatStat = Stat.SupportOverheatAilmentDamagePlusPctFinalPer3FortificationConsumed;
            int overheatPerThree = overheatStat is var overheatKey
                ? SupportedSkill(player, skillId).Stats.GetValueOrDefault(overheatKey) : 0;
            while (true)
            {
                if (!Running()) return;
                double wait = player.NextChannelHitAt - AreaTime(player);
                if (wait > 0)
                {
                    if (snipe)
                    {
                        try { await Task.Delay(TimeSpan.FromSeconds(wait), SnipeWaitToken(player, generation, action)); }
                        catch (OperationCanceledException) { }
                    }
                    else if (!player.Frames.Running && AreaUpdateContext.Value is { Schedule: { } schedule })
                        await schedule.Wait((int)Math.Ceiling(wait * 1000), CancellationToken.None);
                    else await AreaDelay(player, wait);
                }
                await WhilePaused(player);
                if (!Running()) return;
                double now = AreaTime(player);
                uint cost, lifeCost;
                if (!Running() || player.Paused || now < player.NextChannelHitAt) continue;
                // A freeze or a stun's hit recovery interrupts the channelled action: no stage is paid or swept until it
                // ends. The client's updates are dropped meanwhile, so a longer one ends the channel by silence.
                if (PlayerActionBlocked(player, now, now)) { player.NextChannelHitAt = now + TickSeconds; continue; }
                var activeGem = GemSkillEffects(player).FirstOrDefault(g => g.Id == skillId);
                if (activeGem.Name is null || activeGem.Effect != gem.Effect
                    || GemSkillInstanceDisabled(player, activeGem.Effect, skillId) is not null)
                { player.ChannelSkill = 0; player.ChannelGeneration++; return; }
                gem = activeGem;
                player.Connection.Performance.RecordMilliseconds(LatencyPhase.ChannelLate, (now - player.NextChannelHitAt) * 1000);
                if (SupportedChannelSeconds(player, skillId) is not double interval)
                { player.ChannelSkill = 0; player.ChannelGeneration++; player.PaidChannel = null; return; }
                player.NextChannelHitAt = Math.Max(player.NextChannelHitAt, now - 0.05) + interval;
                // Payment and admission share the channel's generation check.
                if (firstSnipeStage)
                {
                    // The cast already paid for the first stage.
                    cost = lifeCost = 0;
                    firstSnipeStage = false;
                }
                else if (!PayMana(player, skillId, out cost, out lifeCost, 100, useSpell: false))
                { player.ChannelSkill = 0; player.ChannelGeneration++; player.PaidChannel = null; }
                else
                {
                    if (CombatLifetime is not { IsActive: true } rules) player.PaidChannel = null;
                    else
                    {
                        double started = player.PaidChannel is { } prior && prior.Generation == generation
                            && prior.Action == action && prior.Skill == skillId && ReferenceEquals(prior.Rules, rules)
                            && double.IsFinite(prior.StartedAt) ? prior.StartedAt : now;
                        player.PaidChannel = new(skillId, gem.Effect, action, generation, rules) { StartedAt = started };
                    }
                    if (cost > 0 && SkillsFor(player).IsAttack(gem.Effect) && WeaponProfileFor(player, activeSet: true).Bow)
                    {
                        var channelAim = snipe && SnipeRuntimes.TryGetValue(player, out var snipeState)
                            ? snipeState.Aim : initialAim;
                        RecordPaidBowAttackLocked(player, skillId, SupportedSkill(player, skillId), cost, channelAim);
                    }
                    // Overheat: every paid channel tick is a use, so it consumes the Fortification gained since the
                    // last one; the first tick's use already consumed in LaunchProjectile.
                    if (overheatPerThree > 0)
                        OverheatConsumeLocked(player, skillId, name => name == overheatStat ? overheatPerThree : 0, now);
                }
                if (!Running()) { await SendPlayerMana(player, "channel stopped"); return; }
                if (cost > 0 || lifeCost > 0) await SendSkillCost(player, lifeCost, $"channel cost {cost} mana, {lifeCost} life");
                if (!Running()) return;
                // Winter Orb's orb pelts nearby enemies on its own loop (WinterOrb.cs); it does not sweep around the
                // player. This cast tick only builds a stage, so a faster caster charges the orb sooner.
                if (IsWinterOrb(gem.Effect))
                {
                    WinterOrbStageStep(player);
                    await WinterOrbPresentStages(player);
                    continue;
                }
                if (snipe)
                {
                    SnipeStageStep(player, generation, action);
                    await SnipePresentStages(player);
                    continue;
                }
                if (divineIre && divineIreStages < divineIreMaxStages)
                {
                    divineIreStages++;
                    await SendWorldRecord(player, player.Id,
                        InstanceEntities.StatsUpdate(player.Id, [(Stat.DivineTempestCurrentNumberOfStages, divineIreStages)]),
                        $"0x01b3 Divine Ire stage {divineIreStages}");
                }
                var at = player.Walk.Position(AreaTime(player));
                await AreaSkillHit(player, skillId, gem, at, at,
                    immediate: true, sequence: player.ChannelSkillSequence, actionToken: action, channelGeneration: generation,
                    damageMore: FocusedChannelMore(now - channelStarted,
                        GolemStat(SupportedSkill(player, skillId).Stats, Stat.SupportFocusChannelDamagePlusPctFinalPerSecondChannellingUpTo60Pct)));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        finally
        {
            // Winter Orb's orb outlives its channel (WinterOrb.cs): it keeps firing on its decaying stages until the
            // duration they leave runs out, and death or an area change retires it with the connection. It has no object
            // of its own to remove: the client draws the orb from the stage buff.
            if (orbEffect != 0) WinterOrbRelease(player, generation, action);
            if (snipe) await SnipeRelease(player, generation, action);
            if (!player.Connection.LockstepClient)
                await EmitPredictiveChannelFinished(player, generation, channelSequence);
            if (divineIreStages > 0 && !player.Closed && !player.LeavingArea
                && (player.ChannelSkill == 0 || player.ChannelGeneration == generation))
                await SendWorldRecord(player, player.Id,
                    InstanceEntities.StatsUpdate(player.Id, [(Stat.DivineTempestCurrentNumberOfStages, 0)]),
                    "0x01b3 Divine Ire stages cleared");
        }
    }

    // Cyclone moves while it is channelled: its final movement modifier (cyclone_movement_speed_+%_final, from the gem's
    // stats with its supports); null for a channel that stands (Blade Flurry …).
    public static int? ChannelWalkPercent(Player player, ushort skillId, int effect) =>
        SkillArea.IsCyclone(SkillsFor(player).EffectId(effect))
            ? GolemStat(SupportedSkill(player, skillId).Stats, Stat.CycloneMovementSpeedPlusPctFinal) : null;

    // C→S 0x197: the point the held channel is aimed at now (the cursor's cell), not the player's cell: it can be the
    // whole screen away the moment the channel starts. The client walks Cyclone's player toward it at the movement speed
    // with the skill's final modifier, as an order re-aimed on every update; the sweeps follow that walk (RunChannel).
    // A channel that stands keeps the player where it is.
    public static void ChannelAimedAt(Player player, int x, int y, double at)
    {
        AimChannelTriggers(player, (x, y));
        SnipeAimedAt(player, (x, y));
        if (player.ChannelMovement.Skill != 0 && player.ChannelMovement.Skill == player.ChannelSkill) player.Walk.Order(x, y, at);
    }

    // C→S 0x193 (class 0x1431767e8, writer 0x140911210, sender 0x14011ecb0): retarget an active held or channelled
    // action onto an object in the area. Applies in both lockstep and predictive modes; safely ignored when idle,
    // blocked, or when the target object is absent or dead.
    public static async Task RetargetAction(Player player, uint targetId, long readAt = 0)
    {
        if (player.Closed || player.LeavingArea) return;
        (int X, int Y)? fallbackPos = null;
        if (targetId != player.Id)
        {
            if (player.Npcs.TryGetValue(targetId, out var npc)) fallbackPos = (npc.X, npc.Y);
            if (fallbackPos is null && player.Instance?.Ground.Get(targetId) is { } ground)
                fallbackPos = (ground.GridX, ground.GridY);
        }

        bool aimLockstepChannel = false;
        {
            double now = AreaTime(player);
            double at = Math.Max(player.ClockAt(readAt), player.ProjectileRuntime?.Runtime.Now / 120000d ?? 0);
            if (player.Closed || player.LeavingArea || PlayerActionBlocked(player, at, now))
                return;

            (int X, int Y) aim;
            bool isMonster = false;
            if (player.Area.Monsters.TryGetValue(targetId, out var monster))
            {
                if (monster.Dead || monster.Life == 0) return;
                aim = monster.Walk.Position(at);
                isMonster = true;
            }
            else if (targetId == player.Id)
                aim = player.Walk.Position(at);
            else if (fallbackPos is { } pos)
                aim = pos;
            else
                return;

            bool lockstepActive = player.LockstepHold.Active || player.LockstepSkill.Active;
            if (player.Connection.LockstepClient || lockstepActive)
            {
                player.LockstepHold.Update(aim);
                if (player.LockstepSkill.Held)
                {
                    player.LockstepSkill.Retarget(isMonster ? targetId : null, aim);
                    if (player.ChannelSkill != 0 && player.LockstepSkill.Channel == player.ChannelSkillSequence)
                    {
                        ChannelAimedAt(player, aim.X, aim.Y, at);
                        player.LastChannelUpdateAt = now;
                        if (HeldLockstepMovingChannel(player))
                        {
                            player.LastLockstepChannelAim = (aim, at);
                            player.LockstepChannelAimPending = false;
                        }
                    }
                    aimLockstepChannel = player.LockstepSkill.Channel is not null;
                }
            }
            else if (player.ChannelSkill != 0)
            {
                ChannelAimedAt(player, aim.X, aim.Y, at);
                player.LastChannelUpdateAt = now;
            }
        }
        if (aimLockstepChannel)
            await AimLockstepChannel(player);
    }

    // C→S 0x199: the button let go. The channel ends, and a walking one stops where the player is (it was walking only
    // as part of the channel); the walk gets its usual speed back.
    public static void ChannelReleased(Player player, double at)
    {
        if (player.ChannelMovement.Skill != 0 && player.ChannelMovement.Skill == player.ChannelSkill)
        {
            var (x, y) = player.Walk.Position(at);
            player.Walk.Order(x, y, at);
        }
        player.ChannelSkill = 0;
        player.ChannelGeneration++;
        SnipeWakeChannel(player);
    }

    public static bool ChannelCurrent(Player player, ushort skill, int action, int generation)
    {
        return !player.Closed && !player.LeavingArea && !player.RageRetired && player.PlayerLife > 0
            && player.ChannelSkill == skill && player.ActionCount == action && player.ChannelGeneration == generation
            // Winter Orb's channel is held by the button, not by the aim: a still cursor keeps the aimed cell and the
            // client sends no 0x198, so the aim silence must not release an orb the player is still charging. Before
            // this a still hold released at five stages while the bar kept counting (live 2026-11-10).
            && (player.OrbRuntime is { Channelling: true } orb && orb.SkillId == skill
                // Lockstep records the held button and its release. A stationary aim can be silent for the whole hold.
                || player.Connection.LockstepClient && player.LockstepSkill.Held && player.LockstepSkill.Skill == skill
                    && player.LockstepSkill.Channel == player.ChannelSkillSequence
                || IsSnipeChannelling(player, skill)
                || player.PaidChannel is { } paid && paid.Skill == skill
                    && (SkillsFor(player).EffectId(paid.Effect) is "LightningTendrils" or "LightningTendrilsAltX" or "LightningTendrilsAltY" or "LightningTendrilsRoyale"
                        || IsDivineIre(player, paid.Effect))
                || AreaTime(player) - player.LastChannelUpdateAt <= ChannelSilenceSeconds);
    }

    public static bool PaidChannelCurrent(Player player, ChannelPaymentReceipt receipt)
    {
        if (!CurrentGameWork || !ReferenceEquals(receipt, player.PaidChannel)
            || !ReferenceEquals(receipt.Rules, CombatLifetime) || !receipt.Rules.IsActive || player.Paused
            || !ChannelCurrent(player, receipt.Skill, receipt.Action, receipt.Generation)
            || PlayerActionBlocked(player, AreaTime(player), AreaTime(player))) return false;
        var gem = GemSkillEffects(player).FirstOrDefault(g => g.Id == receipt.Skill);
        return gem.Name is not null && gem.Effect == receipt.Effect
            && Supports.Of(gem.Effect)?.Types.Contains(47) == true
            && GemSkillInstanceDisabled(player, gem.Effect, receipt.Skill) is null;
    }

    static List<(int X, int Y)> SpellCascadeAreas(Player player, int effect, IReadOnlyDictionary<uint, int> stats,
        (int X, int Y) from, (int X, int Y) to)
    {
        var areas = new List<(int X, int Y)>();
        int sides = stats.GetValueOrDefault(Stat.SupportSpellCascadeNumberOfCascadesPerSide);
        if (sides <= 0 || from == to) return areas;
        int radius = SkillRadius(player, stats, spell: true, effect);
        if (radius <= 0) return areas;
        bool sideways = stats.GetValueOrDefault(Stat.SupportSpellCascadeSideways) != 0;
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double ax = dx / length, ay = dy / length;
        for (int side = 1; side <= sides; side++)
        {
            int alongX = (int)Math.Round(ax * radius * side);
            int alongY = (int)Math.Round(ay * radius * side);
            areas.Add((to.X + alongX, to.Y + alongY));
            areas.Add((to.X - alongX, to.Y - alongY));
            if (sideways)
            {
                areas.Add((to.X - alongY, to.Y + alongX));
                areas.Add((to.X + alongY, to.Y - alongX));
            }
        }
        return areas;
    }

    // The radius in cells of a gem skill's hit around its aimed point (AreaSkillHit): its own
    // active_skill_base_area_of_effect_radius, else Cyclone's 16 or the generic 12, grown by the player's area stats
    // (SkillArea). The radius stat is in cells (Righteous Fire's 18 is its 1.8 m, verified against the real game 2026-10-23).
    public static int SkillRadius(Player player, IReadOnlyDictionary<uint, int> skillStats, bool spell, int effect)
    {
        var areaStats = SkillAreaStats(player, skillStats, effect);
        int Get(uint k) => areaStats.GetValueOrDefault(k);
        return SkillArea.Radius(SkillArea.BaseRadius(SkillsFor(player).EffectId(effect), Get), Get, spell);
    }

    // A gem skill's secondary radius in cells (active_skill_base_secondary_area_of_effect_radius: Holy Sweep's hammers'
    // 12), grown by the player's area stats as the main one is; 0 for a skill without one.
    public static int SkillSecondaryRadius(Player player, IReadOnlyDictionary<uint, int> skillStats, bool spell, int effect = -1)
    {
        var areaStats = effect >= 0 ? SkillAreaStats(player, skillStats, effect) : MergeSkillStats(AreaPlayerStats(player), skillStats);
        int Get(uint k) => areaStats.GetValueOrDefault(k);
        int baseRadius = Get(Stat.ActiveSkillBaseSecondaryAreaOfEffectRadius);
        return baseRadius > 0 ? SkillArea.Radius(baseRadius, Get, spell) : 0;
    }

    // An area spell's centre: the aimed point, but a self-targeted spell (Ice Nova, Discharge, Vaal Cold Snap) goes off
    // around the caster wherever it was aimed, as the client's action does (FUN_1419439e0). A Nova among them goes off at
    // the aim when the caster has nova_spells_cast_at_target_location (Devout of Lunaris: "Nova Spells Cast at the
    // targeted location instead of around you"; the client's 0x2a6e).
    public static (int X, int Y) SpellCentre(Player player, int effect, (int X, int Y) from, (int X, int Y) to) =>
        !SkillsFor(player).IsSelfTargeted(effect) || SkillsFor(player).IsNova(effect)
            && Stat.NovaSpellsCastAtTargetLocation is var key && CharacterPlayerStats(player).GetValueOrDefault(key) != 0
            ? to : from;

    // A gem skill without a projectile (strikes, slams, area spells): a first, simple hit (2026-10-23). It lands once, after
    // the attack contact (attacks) or the area cast clip's contact (spells: AnimationContacts.SpellAreaRelease), on every
    // living monster within the skill's area radius
    // (active_skill_base_area_of_effect_radius, else AreaSkillRadius cells, grown by area of effect: SkillArea) of the aimed point, or of the player for a
    // point too far for a melee skill. Attacks deal the weapon's hit as the default attack does, spells their own damage
    // (SkillCatalog.HitDamage) with the player's stats; both convert, crit and roll per monster. An attack's hit carries
    // the skill's damage effectiveness (SkillCatalog.DamageEffectiveness). Not modelled yet: the skills' own shapes (waves,
    // cones, chains) and stun.
    public static Task AreaSkillHit(Player player,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) from, (int X, int Y) to,
        bool immediate = false, ushort sequence = 0, int? actionToken = null, CooldownUseTicket? cooldownUse = null, bool? melee = null,
        double damageMore = 1, uint excludeMonster = 0, SkillCriticalRoll? criticalRoll = null, int? channelGeneration = null,
        uint targetId = 0, int repeat = 0, int? radiusOverride = null, ISet<uint>? once = null, double repeatDelay = 0,
        double radiusMore = 1, SacrificeUse? sacrifice = null, double? swingAt = null, Func<bool>? triggeredCurrent = null,
        DischargeUse? discharge = null, int scaledBlastChaos = 0, uint? singleStrikeTarget = null, Action<uint>? onHit = null,
        ProjectileSession? scheduledSession = null, bool? cryExerted = null, long addedChaos = 0, long baseChaos = 0, HolySweepHammers.Settings? hammer = null) =>
        AreaSkillHitForUse(null, player, skillId, gem, from, to, immediate, sequence, actionToken,
            cooldownUse, melee, damageMore, excludeMonster, criticalRoll, channelGeneration, targetId, repeat,
            radiusOverride, once, repeatDelay, radiusMore, sacrifice, swingAt, triggeredCurrent, discharge, scaledBlastChaos,
            singleStrikeTarget, onHit, scheduledSession, cryExerted, addedChaos, baseChaos, hammer);

    static Task AreaSkillHitForUse(double? ruthlessMore, Player player,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) from, (int X, int Y) to,
        bool immediate = false, ushort sequence = 0, int? actionToken = null, CooldownUseTicket? cooldownUse = null, bool? melee = null,
        double damageMore = 1, uint excludeMonster = 0, SkillCriticalRoll? criticalRoll = null, int? channelGeneration = null,
        uint targetId = 0, int repeat = 0, int? radiusOverride = null, ISet<uint>? once = null, double repeatDelay = 0,
        double radiusMore = 1, SacrificeUse? sacrifice = null, double? swingAt = null, Func<bool>? triggeredCurrent = null,
        DischargeUse? discharge = null, int scaledBlastChaos = 0, uint? singleStrikeTarget = null, Action<uint>? onHit = null,
        ProjectileSession? scheduledSession = null, bool? cryExerted = null, long addedChaos = 0, long baseChaos = 0, HolySweepHammers.Settings? hammer = null, SpawnedMonster? brandedTarget = null,
        Func<uint, (int X, int Y), bool>? targetFilter = null, int extraStunThresholdReduction = 0, int extraStunDurationPct = 0,
        int extraKnockbackDistancePct = 0, bool removeStunAndKnockback = false, bool offHandWeapon = false) =>
        AreaSkillHitStrike(player, skillId, gem, from, to, immediate, sequence, actionToken, cooldownUse,
            melee, damageMore, excludeMonster, criticalRoll, channelGeneration, targetId, repeat, radiusOverride, once,
            repeatDelay, radiusMore, sacrifice, swingAt, triggeredCurrent, discharge, 0, null, ruthlessMore, null, scaledBlastChaos,
            singleStrikeTarget, onHit, scheduledSession, cryExerted, addedChaos, baseChaos, hammer, brandedTarget,
            targetFilter, extraStunThresholdReduction, extraStunDurationPct, extraKnockbackDistancePct, removeStunAndKnockback, offHandWeapon);

    private static async Task AreaSkillHitStrike(Player player,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, (int X, int Y) from, (int X, int Y) to,
        bool immediate, ushort sequence, int? actionToken, CooldownUseTicket? cooldownUse, bool? melee,
        double damageMore, uint excludeMonster, SkillCriticalRoll? criticalRoll, int? channelGeneration,
        uint targetId, int repeat, int? radiusOverride, ISet<uint>? once, double repeatDelay,
        double radiusMore, SacrificeUse? sacrifice, double? swingAt, Func<bool>? triggeredCurrent, DischargeUse? discharge,
        int strikeIndex, double? firstContactAt, double? ruthlessMore, ISet<uint>? priorTargets, int scaledBlastChaos,
        uint? singleStrikeTarget, Action<uint>? onHit, ProjectileSession? scheduledSession, bool? cryExerted, long prepaidRawExtra, long baseChaos, HolySweepHammers.Settings? hammer, SpawnedMonster? brandedTarget = null,
        Func<uint, (int X, int Y), bool>? targetFilter = null, int extraStunThresholdReduction = 0, int extraStunDurationPct = 0,
        int extraKnockbackDistancePct = 0, bool removeStunAndKnockback = false, bool offHandWeapon = false)
    {
        var paidVaal = SkillsFor(player).VaalBudget(gem.Effect, gem.Level) is null ? null : player.VaalUse;
        var hitPlan = SkillHitPlan.ForSkill(SkillsFor(player), gem.Effect, gem.Level, gem.Quality);
        bool spell = hitPlan.Kind == SkillHitKind.Spell;
        // A gem melee swing keeps its hit when the player only walks on: the client queues a Move until its clip's
        // cut-in instead of running it (Lockstep.cs, measured live 2026-11-03), so the Move the server hears first is a
        // queued order, not a replacement of the swing. Everything else keeps the action-token rule (a Move cancels the
        // swing), and a real interruption (death, area change, pause, freeze, stun recovery) still ends this one.
        bool holdsItsSwing = repeat == 0 && !spell && !immediate && channelGeneration is null && SkillsFor(player).IsMelee(gem.Effect);
        bool preservesSwing = repeat == 0 && strikeIndex > 0 && !spell && channelGeneration is null && SkillsFor(player).IsMelee(gem.Effect);
        int interruption = player.ActionInterruptionGeneration;
        bool TokenCurrent() => holdsItsSwing || preservesSwing
            ? PlayerStateAllowsAction(player)
            : cooldownUse?.Current(player) ?? (actionToken is null || player.ActionCount == actionToken);
        bool Current() => (triggeredCurrent?.Invoke() ?? true) && TokenCurrent()
            && (repeat == 0 || spell || player.ActionInterruptionGeneration == interruption)
            && (SkillsFor(player).VaalBudget(gem.Effect, gem.Level) is null
                || paidVaal is not null && VaalAdmissionCurrent(player, paidVaal))
            && (channelGeneration is not int generation || ChannelCurrent(player, skillId, actionToken ?? player.ActionCount, generation));
        if (!Current()) return;
        var criticalTrigger = spell ? null : CaptureCriticalTriggerSource(player, skillId);
        var meleeKillTrigger = spell ? null : CaptureMeleeKillTriggerSource(player, skillId);
        sacrifice ??= new SacrificeUse(player, skillId, gem.Effect, gem.Gem, triggered: triggeredCurrent is not null);
        var admittedGem = SupportedSkill(player, skillId);
        var skillStats = admittedGem.Stats;
        // Vaal Heavy Strike's action knocks back its target on contact.
        if (SkillsFor(player).EffectId(gem.Effect) == "VaalHeavyStrike")
            skillStats = new Dictionary<uint, int>(skillStats) { [global::Stat.GlobalKnockback] = 1 };
        bool retaliation = SkillsFor(player).IsRetaliation(gem.Effect);
        // An attack gem's share of the weapon's damage ("Deals 177.3% of base Attack Damage"): the hit below uses it.
        int effectiveness = SkillsFor(player).DamageEffectiveness(gem.Effect, gem.Level);
        int Stat(uint k) => skillStats.GetValueOrDefault(k);
        if (Stat(global::Stat.BaseDealNoDamage) != 0) return;
        double chargeAttackMore = 1, chargeRadius = 1;
        int perChargeDamage = Stat(global::Stat.ActiveSkillAttackDamagePlusPctFinalPerEnduranceCharge);
        int perChargeArea = Stat(global::Stat.ActiveSkillAreaOfEffectPlusPctFinalPerEnduranceCharge);
        if (!spell && (perChargeDamage != 0 || perChargeArea != 0))
        {
            int charges = cooldownUse?.EnduranceChargesAfterCost ?? ChargesOf(player).Effective(ChargeType.Endurance);
            chargeAttackMore = Math.Max(0, 1 + perChargeDamage * (double)charges / 100);
            chargeRadius = SkillArea.RadiusMultiplier(ActorScale.Saturate((long)perChargeArea * charges));
        }
        if (!hitPlan.CanHit)
        {
            if (IsBane(player, gem.Effect))
                await CastBane(player, skillId, admittedGem, from, to, repeat, radiusMore, Current);
            else if (ContagionCast(player, skillId, gem.Effect) is { } contagion)
                await CastContagion(player, gem.Effect, skillId, from, to,
                    contagion with { SpreadAo = ContagionSkinAo(player, gem.Gem) }, repeat, radiusMore,
                    actionToken ?? cooldownUse?.ActionToken ?? player.ActionCount, cooldownUse?.Instant == true, Current);
            if (Current()) CastingIntensity(player, skillId)?.Complete();
            return;
        }
        cryExerted ??= ConsumeIntimidatingCryExertLocked(player,
            !immediate && triggeredCurrent is null && repeat == 0 && channelGeneration is null
                && IntimidatingCryCanExert(player, skillId, gem.Effect));
        // Discharge: the charges it spends are counted as the use starts; with none it deals nothing.
        if (hitPlan.SpendsCharges) discharge ??= new(RemovableCharges(player));
        if (discharge is not null && discharge.Total == 0)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} {gem.Name}: no removable charges, nothing discharged");
            return;
        }
        criticalRoll ??= CaptureSkillCriticalRoll(player, skillId);
        // A dual-wielded attack's hand for this use (AttackHands): its weapon, time, hit, chance and local modifiers.
        var hand = spell ? AttackHand.Main : AttackHandFor(player, skillId, sequence);
        var itemCriticalTrigger = spell ? null : CaptureItemCriticalTriggerSource(player, skillId, hand);
        var weapon = HandWeapon(player, hand);
        // Read once per use: the per-victim hit log and the target-conditional critical chances reuse them.
        string handNote = spell ? "" : HandNote(player, hand);
        int? offCritical = hand != AttackHand.Main && OffhandWeaponFor(player) is not null
            ? HandWeaponCritical(player, AttackHand.Off) : null;
        int? swingCritical = hand == AttackHand.Off ? offCritical : null;
        int? castDuration = spell && !immediate ? CastMs(player, gem.Effect, skillId) : 0;
        if (castDuration is null) return;
        // Ruthless: whether this use is its skill's Ruthless Blow, counted as it starts.
        // A holy hammer (hammer: RunHolyHammers) is a hit of its sweep's use, not a use of its own.
        ruthlessMore ??= spell || hammer is not null ? 1 : RuthlessBlowMore(player, skillId, Stat);
        double ruthless = ruthlessMore.Value;
        double delay;
        // A spell lands at its area cast clip's contact keyframe (a repeat a whole cast later each), an attack at its
        // swing's own clip (GemAttackContactDelay; the player swings where it stands, so without the walk-in offset).
        string? contactClip = null;
        double contactFraction = 0;
        delay = spell
            ? castDuration.Value / 1000.0 * (repeat + Animations.SpellAreaRelease(SkillsFor(player).ClassFor(player.Character.ClassRow).AnimatedObject)) + repeatDelay
            : GemAttackContactDelay(player, gem.Effect, SupportedAttackSeconds(player, skillId, hand), out contactClip, out contactFraction)
                + repeatDelay;
        // A caller's own shape (Shield Charge's path and end, ShieldCharge.cs) replaces the skill's area radius.
        // Greater Spell Echo: each repeat of the spell has more increased area than the one before.
        var areaStats = skillStats;
        if (spell && repeat > 0 && Stat(SkillArea.IncreasedPerRepeat) is int perRepeat and not 0)
            areaStats = new Dictionary<uint, int>(skillStats) { [SkillArea.BaseIncreased] = skillStats.GetValueOrDefault(SkillArea.BaseIncreased) + perRepeat * repeat };
        bool divineBlast = SkillsFor(player).EffectId(gem.Effect) is "DivineBlast" or "DivineBlastAltX";
        double phaseDamageMore = divineBlast && immediate && scheduledSession is not null
            ? Math.Max(0, 1 + Stat(global::Stat.DiscusSlamDamagePlusPctFinalForShockwave) / 100d) : 1;
        int radius = (int)Math.Round((radiusOverride ?? (divineBlast && !immediate
            ? SkillRadius(player, Stat(global::Stat.ActiveSkillBaseSecondaryAreaOfEffectRadius), areaStats, spell: false, gem.Effect)
            : SkillRadius(player, areaStats, spell, gem.Effect))) * radiusMore * chargeRadius);
        // Ground Slam's wave: the skills whose action is the data's "DistanceScaledGroundSlam" travel forward from where
        // the player stands instead of landing one circle (GroundSlamWave; its length, half angle and direction are read
        // once here, so the log after the sweep can report them). Vaal Ground Slam's own skill is a different action, so
        // the Vaal blast keeps its radial hit.
        bool wave = !immediate && !spell && SkillsFor(player).ActionHash(gem.Effect) == GroundSlamWave.Action;
        bool fissure = !immediate && IsVolcanicFissure(gem.Effect);
        double waveLength = wave ? GroundSlamWave.Length(radius, skillStats.GetValueOrDefault(global::Stat.ActiveSkillBaseRadiusPlus)) : 0;
        int waveCloseDamage = wave ? Stat(global::Stat.GroundslamDamageToCloseTargetsPlusPctFinal) : 0;
        double waveHalfAngle = wave ? GroundSlamWave.HalfAngle(Stat) : 0;
        (double X, double Y) waveDir = wave ? GroundSlamWave.Direction(from, to) : (0, 0);
        // A wave starts where the player stands and only takes its direction from the aim; a melee skill aimed far away
        // still lands in front of the player, and a sweep around it (MeleeAreaCentre); a self-targeted spell goes off
        // around the caster.
        var centre = IsSmite(gem.Effect) && singleStrikeTarget is null ? to
            : wave || SkillsFor(player).EffectId(gem.Effect) is "VaalGroundSlam" or "VaalSweep" ? from
            : spell ? SpellCentre(player, gem.Effect, from, to)
            : MeleeAreaCentre.Of(SkillsFor(player).ActionHash(gem.Effect), from, to, radius + MeleeSkillReach(player, weapon, gem.Effect, skillStats));
        int action = actionToken ?? cooldownUse?.ActionToken ?? player.ActionCount;
        if (divineBlast) centre = to;
        // A later action (a Move, another use) replaces the swing; nothing of it lands from then on. The ordinary attack
        // says so at its own contact (Attack's HitTarget); this says so for a gem skill's sweep, whose drop was otherwise
        // invisible in the log (the live tests cannot count what a swing did without it).
        bool Replaced()
        {
            if (triggeredCurrent is not null || cooldownUse?.Instant == true || player.ActionCount == action) return false;
            // A gem melee swing survives the Move the client queued behind its swing (holdsItsSwing): the walk order
            // stands, the hit still lands, and only a real interruption (which PlayerStateAllowsAction refuses) drops it.
            if ((holdsItsSwing || preservesSwing) && PlayerStateAllowsAction(player))
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {gem.Name}: the swing keeps its hit over the "
                    + $"queued move order (action {action} -> {player.ActionCount})");
                return false;
            }
            Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {gem.Name}: the swing was replaced by action "
                + $"{player.ActionCount} before its hit (action {action}), no hit lands");
            return true;
        }
        if (Replaced()) return;
        // A Strike's spirit strikes and previous-location strike, chosen as it starts, as the client chooses them
        // (StrikeTargets). Each monster takes one hit of the use, whichever of them reaches it.
        List<uint> spirit = [];
        (int X, int Y)? echo = null;
        byte[]? memoryBuff = null;
        if (!spell && channelGeneration is null && singleStrikeTarget is null && strikeIndex == 0 && hammer is null && !IsSmite(gem.Effect))
            using (ProjectileMutation(player, advance: scheduledSession is null))
                (spirit, echo, memoryBuff) = StrikeExtrasLocked(player, skillId, gem.Effect, skillStats, from, centre, sequence,
                    targetId, ProjectileWorldTime(player));
        if (memoryBuff is not null)
            await SendWorldRecord(player, player.Id, memoryBuff, $"0x01c1 player buff skill_location_memory for skill 0x{skillId:x4}");
        // A channel's hits (Cyclone, C→S 0x197) land at once, where the player is. A swing that began before now
        // (swingAt) lands its contact after its start.
        if (swingAt is { } swung) delay -= AreaTime(player) - swung;
        if (!immediate)
        {
            await AreaDelay(player, Math.Max(0.05, delay));
            await WhilePaused(player);
            if (Replaced() || player.Closed || !Current()) return;
        }
        int addedChaos;
        double contactAt = firstContactAt ?? AreaTime(player);
        int strikes = SkillMultiStrike.StrikeCount(SkillsFor(player).EffectId(gem.Effect), Stat(global::Stat.BaseSkillNumberOfAdditionalHits),
            immediate, channelGeneration is not null, spell, wave);
        ISet<uint>? alreadyHit = priorTargets ?? SkillMultiStrike.SnapshotPriorTargets(once, strikes);
        List<(uint Id, SpawnedMonster M)> targets;
        int sweep;
        // Melee Splash victims and their damage multiplier (see MeleeSplash).
        var splashMore = new Dictionary<uint, double>();
        // Ice Nova's ring reaches each victim in turn (IceNovaRing) and Ground Slam's wave does the same by distance
        // (GroundSlamWave); a channel's sweep lands at once.
        bool ring = !immediate && SkillsFor(player).ActionHash(gem.Effect) == IceNovaRing.Action;
        int frostboltCap = ring ? Stat(global::Stat.IceNovaNumberOfFrostBoltsToCastOn) : 0;
        double frostboltMore = 1;
        bool hexblast = IsHexblastEffect(SkillsFor(player).EffectId(gem.Effect));
        bool hexblastExpanded = false;
        int artVariation = ring ? SkillArtVariation(player, gem.Gem) : 0;
        double sweepStart = 0;
        double[] reach = [];
        int epoch = player.ProjectileEpoch;
        using (ProjectileMutation(player, advance: scheduledSession is null))
        {
            double now = ProjectileWorldTime(player);
            if (!Current() || !sacrifice.Current) return;
            addedChaos = sacrifice.AddedChaos();
            // A freeze or stun that landed after this stage was paid interrupts it before the sweep.
            if (channelGeneration is not null
                && PlayerActionBlocked(player, AreaTime(player), AreaTime(player))) return;
            CastingIntensity(player, skillId)?.Complete();
            if (divineBlast && !immediate && strikeIndex == 0)
                ProjectilesFor(player).PlaceDivineBlastOrb(gem, centre, admittedGem, criticalRoll);
            // Queued mana output may outlast movement: resolve a channel at its current position.
            if (channelGeneration is not null) centre = player.Walk.Position(now);
            (int X, int Y) end = fissure ? VolcanicFissureSweep.End(player.Layout, from, to, radius) : default;
            double reachLimit = wave ? waveLength : fissure ? Dist(from, end) : radius;
            // Ice Nova of Frostbolts expands from up to N active player Frostbolt projectiles near the aimed point,
            // with its native damage multiplier, instead of only expanding once around the caster.
            List<(int X, int Y)>? centres = null;
            if (frostboltCap > 0 && player.ProjectileRuntime is { Active: true } session)
            {
                var frostbolts = session.Runtime.Projectiles
                    .Where(p => p.Use.Owner.Id == player.Id
                        && p.Use.Payload is ProjectileOffense { FromTotem: false, FromTrapOrMine: false } shot
                        && SkillsFor(player).EffectId(shot.Effect) is "FrostBolt" or "FrostBoltAltX" or "FrostBoltAltY" or "FrostBoltRoyale")
                    .Select(p => (X: (int)Math.Round(p.Position.X), Y: (int)Math.Round(p.Position.Y)))
                    .Where(p => Dist(to, p) <= Math.Max(60, radius + 30))
                    .OrderBy(p => Dist(to, p))
                    .Take(frostboltCap)
                    .ToList();
                if (frostbolts.Count > 0)
                {
                    centres = frostbolts;
                    frostboltMore = Math.Max(0, 1 + Stat(global::Stat.IceNovaDamageWhenCastOnFrostboltPlusPctFinal) / 100.0);
                }
            }
            IEnumerable<(uint Key, SpawnedMonster Value, (int X, int Y) At, double Distance)> TargetsAt((int X, int Y) c) =>
                player.Area.Monsters.Select(e => (e.Key, e.Value, At: e.Value.Walk.Position(now)))
                .Select(e => (e.Key, e.Value, e.At, Distance: fissure
                    ? VolcanicFissureSweep.ContactDistance(from, end, e.At, radius) : Dist(e.At, c)))
                .Where(e => e.Key != excludeMonster && e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0 && e.Value.OwnerId is null
                    && (singleStrikeTarget is null || e.Key == singleStrikeTarget)
                    && e.Distance <= reachLimit
                    && (strikeIndex > 0 ? !SkillMultiStrike.WasHitBeforeUse(alreadyHit, e.Key) : once?.Contains(e.Key) != true)
                    && (!wave || GroundSlamWave.Covers(waveDir, from, e.At, waveHalfAngle, waveLength))
                    && (targetFilter is null || targetFilter(e.Key, e.At)));
            var found = (centres is null ? TargetsAt(centre) : centres.SelectMany(TargetsAt)).ToList();
            if (hexblast)
            {
                var primary = targetId != 0 ? found.Find(entry => entry.Key == targetId)
                    : found.OrderBy(entry => entry.Distance).ThenBy(entry => entry.Key).FirstOrDefault();
                found.Clear();
                if (primary.Value is not null)
                {
                    found.Add(primary);
                    centre = primary.At;
                    if (HexblastHex(primary.Value, now) is not null)
                        found.AddRange(TargetsAt(primary.At).Where(entry => entry.Key != primary.Key));
                }
            }
            // Hits that may reach each monster only once across several sweeps record it here.
            if (once is not null) foreach (var f in found) once.Add(f.Key);
            if (ring || wave || fissure)
            {
                found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                reach = wave || fissure
                    ? [.. found.Select(f => GroundSlamWave.AdvanceSeconds(f.Distance))]
                    : [.. found.Select(f => IceNovaRing.Reach(f.Distance, radius, artVariation))];
                sweepStart = AreaTime(player);
            }
            if (fissure)
                ScheduleFissureEruption(player, ProjectilesFor(player), skillId, gem, from, end,
                    now, epoch, criticalRoll, sequence, repeat, damageMore, cryExerted == true);
            targets = [.. found.Select(f => (f.Key, f.Value))];
            sweep = targets.Count;
            // shortcut: spirit and echo targets come after the ring's own and get no ring delay; time them by their
            // distance too once the client's rule for them is read.
            var struck = targets.Select(t => t.Id).ToHashSet();
            foreach (uint id in spirit)
                if (struck.Add(id) && player.Area.Monsters.TryGetValue(id, out var s) && !s.Dead && s.Life > 0) targets.Add((id, s));
            if (echo is { } at)
                targets.AddRange(player.Area.MonstersNear(at, radius).Where(e => e.Value.Hostile && !e.Value.Dead && e.Value.Life > 0
                    && Dist(e.Value.Walk.Position(now), at) <= radius && struck.Add(e.Key)).Select(e => (e.Key, e.Value)));
            // Melee Splash: each struck enemy splashes onto the enemies around it, for the support's less damage.
            if (!spell && hammer is null)
            {
                var merged = SkillAreaStats(player, SupportedSkill(player, skillId).Stats, gem.Effect);
                int SplashStat(uint key) => merged.GetValueOrDefault(key);
                if (MeleeSplash.Settings(SplashStat) is { } splash)
                    foreach (var (_, m) in targets.ToArray())
                    {
                        var splashAt = m.Walk.Position(now);
                        foreach (var (sid, sm) in player.Area.MonstersNear(splashAt, splash.Radius))
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
        // One simultaneous sweep, independent of notification latency. A holy hammer's hits are melee area hits that can
        // grant Fortification (the wiki's Holy Sweep of Hammerfalls); Rage stays with its sweep.
        bool fortificationGranted = false;
        bool rageGranted = hammer is not null; // Only a positive gain takes this sweep's latch.
        // The enemies this sweep struck and where, for Holy Sweep's hammers (RunHolyHammers).
        var hammerTargets = new List<(uint Id, (int X, int Y) At)>();
        double bloodMore = BloodChargeDamageMore(player, Stat);
        int landed = 0;
        // One sweep, one hit: its offensive inputs are read once, at the first contact (inside the projectile mutation, after
        // earlier contacts caught up), and rolled per monster. They were computed again for every monster (a whole stat
        // calculation each), which spread one sweep's hits over a second in a crowd.
        Dictionary<uint, int>? playerStats = null;
        IReadOnlyDictionary<uint, int>? ailmentSkillStats = null;
        Penetration.PerType pen = default;
        HitResistances.Mods mods = default;
        AreaSkillDamageProfile sweepProfile = default!;
        AreaSkillDamageProfile? blindedProfile = null;
        SpiderDamageSource? sweepSpider = null, sweepOffSpider = null;
        double mainHandMore = 1, dualWieldMore = 1;
        int sweepMultiplier = 0;
        AttackAccuracyProfile accuracy = default;
        LeechSources? leech = null;
        bool weaponAttack = false;
        Item? weaponContactSource = null, secondWeaponContactSource = null;
        TinctureKillBonus? tinctureKill = null;
        IReadOnlyDictionary<uint, int>? weaponLocal = null;
        string? sweepWeaponClass = null, secondSweepWeaponClass = null;
        ImpaleStats.Settings impale = default, criticalImpale = default;
        // Every monster gets its hit before the sweep's deaths run their loot, experience and quest work, and before a
        // Mirrored monster's reflected ailments reach the player (its freeze would end the action between two targets).
        var deaths = new List<(uint Id, SpawnedMonster M)>();
        var reflected = new List<MonsterElementalAilments.Effect>();
        PlayerStatScope? sweepStats = null;
        try
        {
            for (int victim = 0; victim < targets.Count; victim++)
            {
                if (hexblast && victim > 0 && !hexblastExpanded) break;
                var (id, m) = targets[victim];
                // A ring and a wave arrive over time: the victim nearest the caster (or the player) first, each one when its
                // front reaches it. A retired wave (death, area change, a closed connection) stops the rest of the sweep.
                if ((ring || wave || fissure) && victim < reach.Length && reach[victim] > 0
                    && !await WaitProjectileTime(player, sweepStart + reach[victim], epoch))

                {
                    if (wave) Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {gem.Name}: the wave was retired "
                        + $"{AreaTime(player) - sweepStart:0.###} s in, {targets.Count - victim} monster(s) left unhit");
                    break;
                }
                if (wave && victim < reach.Length)
                    Console.WriteLine($"[gateway] {player.Connection.Remote} {gem.Name} wave reaches 0x{id:x} at {reach[victim] * GroundSlamWave.CellsPerSecond:0.#} cells {reach[victim]:0.###} s after the slam");
                MonsterHit? applied;
                byte hitCode;
                int damage;
                bool critical;
                string? resistance;
                List<byte[]>? ignited = null, bled = null;
                string igniteLog = "", bleedLog = "";
                StunHit stun;
                SpawnedMonster.NativeKnockback? nativeKnockback = null;
                using (ProjectileMutation(player, advance: scheduledSession is null))
                {
                    // Catch up earlier projectile contacts before reading any live offensive input. The first accepted
                    // target opens one fresh character scope, which every victim reuses until this sweep's loop exits.
                    if (player.Closed || player.LeavingArea || player.RageRetired || player.PlayerLife == 0 || player.Paused
                        || !(ring || wave || fissure || hammer is not null) && (!Current() || !holdsItsSwing && !preservesSwing && cooldownUse is null && triggeredCurrent is null && !PlayerActionCurrent(player, action))) break;
                    if (m.Dead || m.Life == 0 || !m.Hostile
                        || !player.Area.Monsters.TryGetValue(id, out var current) || !ReferenceEquals(current, m)) continue;
                    bool freshPlayer = playerStats is null;
                    if (playerStats is null)
                    {
                        sweepStats = new PlayerStatScope(player, fresh: true);
                        playerStats = new Dictionary<uint, int>(CharacterPlayerStats(player));
                        // Capture the admitted gem and actual source weapon once. The ordinary main hand keeps its local
                        // sources; a shield skill's off hand intentionally carries none of the main hand's local values.
                        var shield = spell ? null : ShieldWeapon(player, skillId);
                        // A dual-wielded use swings its hand's weapon (AttackHands); both hands at once add the off hand's
                        // hit to the main hand's (SupportedSwingHit), each from its own captured inputs.
                        var swingWeapon = shield ?? OffHandSwing(player, hand);
                        var sourceWeapon = shield ?? HandWeapon(player, hand);
                        if (!spell) sweepWeaponClass = sourceWeapon.ItemClass ?? "";
                        var local = spell || shield is not null ? null : sourceWeapon.LocalSources;
                        weaponLocal = local;
                        weaponAttack = !spell && shield is null && sourceWeapon.ItemClass is not null && SkillsFor(player).IsAttack(gem.Effect);
                        if (weaponAttack)
                        {
                            tinctureKill = TinctureKillBonusFor(player, playerStats, hand);
                            weaponContactSource = SkeletonWeaponContactSource(player, hand, local);
                        }
                        if (SupportedHitInputsFor(player, skillId, weapon: sourceWeapon, charges: discharge?.Charges, capturedGem: admittedGem, area: IsSmite(gem.Effect) ? singleStrikeTarget is null : null)
                            is not { } capturedInputs) break;
                        var bothOff = shield is null && hand == AttackHand.Both ? OffhandWeaponFor(player) : null;
                        var offInputs = bothOff is not null
                            ? SupportedHitInputsFor(player, skillId, weapon: bothOff, charges: discharge?.Charges, capturedGem: admittedGem)
                            : null;
                        if (weaponAttack && offInputs is not null)
                        {
                            secondWeaponContactSource = SkeletonWeaponContactSource(player, AttackHand.Off, bothOff?.LocalSources);
                            secondSweepWeaponClass = bothOff?.ItemClass;
                        }
                        // Sacrifice's prepaid chaos is flat added damage. Apply the admitted skill's effectiveness
                        // before the shared hit and ailment ancestry, so increases, more multipliers and prohibition
                        // rules see it exactly as they see any other spell-added damage.
                        if (addedChaos > 0 || prepaidRawExtra > 0 || baseChaos > 0)
                        {
                            double added = baseChaos + ((double)addedChaos + prepaidRawExtra) * AddedEffectiveness(player, admittedGem.Effect, admittedGem.Level);
                            capturedInputs = capturedInputs with
                            {
                                Raw = capturedInputs.Raw.Add(DamageType.Chaos, new(added, added)),
                            };
                            offInputs = offInputs is null ? null : offInputs with
                            {
                                Raw = offInputs.Raw.Add(DamageType.Chaos, new(added, added)),
                            };
                        }
                        long additionalIncreased = PlayerHitDamage.VsBlindedDamageStat is var key
                            ? (long)playerStats.GetValueOrDefault(key) + admittedGem.Stats.GetValueOrDefault(key) : 0;
                        AreaSkillDamageProfile Profile(long increased)
                        {
                            var hit = shield is not null ? SupportedHitFrom(capturedInputs, suppress: true, additionalIncreased: increased)
                                : SupportedSwingHit(player, skillId, hand, w => SupportedHitFrom(w is not null && offInputs is not null
                                    ? offInputs : capturedInputs, suppress: true, additionalIncreased: increased));
                            if (ruthless != 1) hit = hit.Scale(ruthless);
                            if (scaledBlastChaos > 0) hit = hit.Add(DamageType.Chaos, new(scaledBlastChaos, scaledBlastChaos));
                            return new(hit,
                                SupportedIgnite(player, skillId, spell, playerStats, swingWeapon, increased, capturedInputs),
                                SupportedBleed(player, skillId, spell, playerStats, swingWeapon,
                                    melee: SkillsFor(player).IsMelee(gem.Effect), increased, capturedInputs),
                                SupportedPoison(player, skillId, spell, playerStats, swingWeapon, increased, capturedInputs));
                        }
                        sweepProfile = Profile(0);
                        double targetEffectiveness = AddedEffectiveness(player, admittedGem.Effect, admittedGem.Level);
                        sweepSpider = CaptureSpiderDamage(player, capturedInputs, targetEffectiveness, context: true, local: local,
                            bow: false, offHand: hand == AttackHand.Off, required: HasLocalTargetDamage(bothOff?.LocalSources));
                        if (sweepSpider is not null && !spell && shield is null)
                        {
                            if (hand != AttackHand.Off)
                                (mainHandMore, dualWieldMore) = HandDamageMore(player, admittedGem.Stats);
                            else
                                (_, dualWieldMore) = HandDamageMore(player, admittedGem.Stats);
                            if (offInputs is not null)
                                sweepOffSpider = CaptureSpiderDamage(player, offInputs, targetEffectiveness, context: true,
                                    local: OffhandWeaponFor(player)?.LocalSources, bow: false, offHand: true, required: true);
                        }
                        if (additionalIncreased != 0) blindedProfile = Profile(additionalIncreased);
                        pen = Penetration.Of(playerStats, Stats, attack: !spell, skillStats, local);
                        mods = HitResistMods(playerStats, skillStats);
                        sweepMultiplier = SupportedCriticalMultiplier(player, skillId, sourceWeapon);
                        if (!spell) accuracy = SupportedAccuracy(player, skillId, hand);
                        leech = HitLeechSources(playerStats, !spell, skillStats, local, !spell && SkillsFor(player).IsMelee(gem.Effect));
                        impale = WeaponImpaleSettings(playerStats, Stats, !spell, skillStats, spell || shield is not null ? null : sourceWeapon);
                        criticalImpale = WeaponImpaleSettings(playerStats, Stats, !spell, skillStats, spell || shield is not null ? null : sourceWeapon, critical: true);
                        if (removeStunAndKnockback)
                        {
                            var adjusted = new Dictionary<uint, int>(skillStats)
                            {
                                [global::Stat.BaseGlobalChanceToKnockbackPct] = -playerStats.GetValueOrDefault(global::Stat.BaseGlobalChanceToKnockbackPct),
                            };
                            ailmentSkillStats = adjusted;
                        }
                        else if (extraKnockbackDistancePct != 0 || (offHandWeapon && skillStats.GetValueOrDefault(global::Stat.GlobalKnockback) != 0))
                        {
                            var adjusted = new Dictionary<uint, int>(skillStats);
                            if (extraKnockbackDistancePct != 0)
                                adjusted[global::Stat.KnockbackDistancePlusPct] = adjusted.GetValueOrDefault(global::Stat.KnockbackDistancePlusPct) + extraKnockbackDistancePct;
                            if (offHandWeapon && adjusted.GetValueOrDefault(global::Stat.GlobalKnockback) != 0
                                && adjusted.GetValueOrDefault(global::Stat.BaseGlobalChanceToKnockbackPct) < 100)
                                adjusted[global::Stat.BaseGlobalChanceToKnockbackPct] = 100;
                            ailmentSkillStats = adjusted;
                        }
                        else
                            ailmentSkillStats = skillStats;
                        if (retaliation)
                        {
                            uint durationKey = global::Stat.RetaliationSkillsAllAilmentDurationPlusPct;
                            long duration = (long)playerStats.GetValueOrDefault(durationKey) + skillStats.GetValueOrDefault(durationKey);
                            if (duration != 0)
                            {
                                uint ailmentDurationKey = global::Stat.BaseAllAilmentDurationPlusPct;
                                ailmentSkillStats = new Dictionary<uint, int>(ailmentSkillStats)
                                {
                                    [ailmentDurationKey] = ActorScale.Saturate(ailmentSkillStats.GetValueOrDefault(ailmentDurationKey) + duration),
                                };
                            }
                        }
                    }
                    // The target's condition is chosen before accuracy, crit RNG, on-hit hooks and the hit's own Blind.
                    var profile = Blinded(player, id) && blindedProfile is not null
                        ? blindedProfile : sweepProfile;
                    if (sweepSpider is { } spider)
                    {
                        double hitAt = ProjectileWorldTime(player);
                        int webs = m.SpiderWebs.Count(hitAt);
                        bool maimed = m.MaimedUntil > hitAt, lowLife = EnemyOnLowLife(m);
                        bool targetIgnited = m.Ignites.IgnitedAt(hitAt), bleeding = m.Bleeds.IgnitedAt(hitAt);
                        bool close = spider.Melee && spider.Stats.GetValueOrDefault(global::Stat.MeleeDamagePlusPctAtCloseRange) != 0
                            && InCloseRange(player, m, hitAt);
                        bool blinded = Blinded(player, id, hitAt);
                        var conditionalDamage = ContactDamage(player, m, hitAt, spider.Stats, spider.Local);
                        var woven = SpiderDamageProfile(spider, webs, blinded, targetIgnited, bleeding,
                            lowLife, maimed, close, conditionalDamage);
                        var conditionalHit = mainHandMore == 1 ? woven.Hit : woven.Hit.Scale(mainHandMore);
                        if (sweepOffSpider is { } off)
                            conditionalHit = conditionalHit.Add(SpiderDamageProfile(off, webs, blinded, targetIgnited, bleeding,
                                lowLife, maimed, close, ContactDamage(player, m, hitAt, off.Stats, off.Local)).Hit);
                        double contactMore = ruthless * dualWieldMore;
                        if (contactMore != 1) conditionalHit = conditionalHit.Scale(contactMore);
                        if (scaledBlastChaos > 0) conditionalHit = conditionalHit.Add(DamageType.Chaos, new(scaledBlastChaos, scaledBlastChaos));
                        profile = new(conditionalHit, woven.Ignite, woven.Bleed, woven.Poison);
                    }
                    if (phaseDamageMore != 1)
                        profile = new(profile.Hit.Scale(phaseDamageMore), RepeatAilment(profile.Ignite, phaseDamageMore),
                            RepeatAilment(profile.Bleed, phaseDamageMore), RepeatAilment(profile.Poison, phaseDamageMore));
                    if (!spell && repeat > 0)
                    {
                        double repeatMore = MeleeRepeatDamageMore(player, skillId, repeat);
                        profile = profile with
                        {
                            Ignite = RepeatAilment(profile.Ignite, repeatMore),
                            Bleed = RepeatAilment(profile.Bleed, repeatMore),
                            Poison = RepeatAilment(profile.Poison, repeatMore),
                        };
                    }
                    double overexertionMore = OverexertionMore(skillStats, cryExerted == true);
                    if (overexertionMore != 1)
                        profile = profile with
                        {
                            Ignite = RepeatAilment(profile.Ignite, overexertionMore),
                            Bleed = RepeatAilment(profile.Bleed, overexertionMore),
                            Poison = RepeatAilment(profile.Poison, overexertionMore),
                        };
                    var hex = hexblast ? HexblastHex(m, ProjectileWorldTime(player)) : null;
                    double targetAilmentMore = hex is null ? 1
                        : Math.Max(0, 1 + Stat(global::Stat.HexblastAilmentDamagePlusPctFinalIfHexed) / 100d);
                    if (targetAilmentMore != 1)
                        profile = profile with
                        {
                            Ignite = RepeatAilment(profile.Ignite, targetAilmentMore),
                            Bleed = RepeatAilment(profile.Bleed, targetAilmentMore),
                            Poison = RepeatAilment(profile.Poison, targetAilmentMore),
                        };
                    var contactHit = profile.Hit;
                    if (frostboltMore != 1) contactHit = contactHit.Scale(frostboltMore);
                    if (chargeAttackMore != 1) contactHit = contactHit.Scale(chargeAttackMore);
                    if (spell && repeat > 0) contactHit = contactHit.Scale(GreaterEchoHitMore(repeat, Stat));
                    if (waveCloseDamage != 0 && victim < reach.Length)
                        contactHit = contactHit.Scale(GroundSlamWave.CloseHitMultiplier(
                            reach[victim] * GroundSlamWave.CellsPerSecond, waveLength, waveCloseDamage));
                    else if (SkillsFor(player).EffectId(gem.Effect) == "VaalGroundSlam")
                    {
                        double distance = Dist(player.Walk.Position(ProjectileWorldTime(player)), m.Walk.Position(ProjectileWorldTime(player)));
                        contactHit = contactHit.Scale(1 + Stat(global::Stat.GroundslamDamageToCloseTargetsPlusPctFinal)
                            / 100.0 * Math.Max(0, 1 - distance / Math.Max(1, radius)));
                    }
                    if (!spell && SkillsFor(player).IsMelee(gem.Effect))
                        contactHit = WithMeleeProximityLocked(player, playerStats, skillStats, m, contactHit, ProjectileWorldTime(player));
                    int hitChance = spell ? 100 : AttackHitChanceAt(player, id, m, ProjectileWorldTime(player), accuracy, playerStats);
                    if (!spell && !accuracy.CannotBeEvaded && hammer?.CannotBeEvaded != true && !m.AttackEvasion.Hits(hitChance, ProjectileWorldTime(player), player.AttackAccuracyRandom)) continue;
                    if (MonsterBlocks(m, AreaRng(player), spell, ProjectileWorldTime(player), skillStats))
                    {
                        Console.WriteLine($"[gateway] {player.Connection.Remote} {gem.Name} on 0x{id:x} ({m.Label}) blocked");
                        continue;
                    }
                    var gambler = GamblerFor(playerStats);
                    var rolled = WithConditionalSupportDamageLocked(player, skillStats, !spell, m,
                        GameEvents.WithOutgoingDamage(player, playerStats, m, WithIntimidatingCryExert((gambler with { DamageMorePct = 0 })
                            .ForSkill(Stat, spell, finalRepeat: repeat == SpellRepeats(player, skillId, gem.Effect),
                                intelligence: player.Pools.Intelligence), cryExerted == true)
                            .ForWeapon(weaponLocal).RollOutgoingHit(contactHit, AreaRng(player))),
                        ProjectileWorldTime(player));
                    double targetHitMore = hex is null ? 1
                        : Math.Max(0, 1 + Stat(global::Stat.HexblastHitDamagePlusPctFinalIfHexed) / 100d);
                    if (targetHitMore != 1) rolled = rolled.Scale(targetHitMore);
                    // Hallow: an attack against an enemy affected by Hallowing Flame gains its share of physical damage as
                    // extra fire, and removes the flame.
                    if (!spell && m.HallowingFlameUntil > ProjectileWorldTime(player) && rolled[DamageType.Physical].Min is > 0 and var flamePhys)
                    {
                        double flameFire = flamePhys * m.HallowingFlamePct / 100.0;
                        rolled = rolled.Add(DamageType.Fire, new(flameFire, flameFire));
                        m.HallowingFlameUntil = 0;
                    }
                    rolled = WithOverexertion(rolled, skillStats, cryExerted == true);
                    // The shared roll meets each target's current chance: a source can change between targets.
                    int criticalChance = CriticalChanceOn(player, id, SupportedCriticalChance(player, skillId, hand, offCritical),
                        SupportedCriticalChanceVsBlinded(player, skillId, release: false, swingCritical),
                        SupportedCriticalChanceVsGround(player, skillId, release: false, swingCritical));
                    // Read an existing bleed before this attack can inflict its own.
                    if (!spell && m.Bleeds.IgnitedAt(ProjectileWorldTime(player)))
                    {
                        long bleedingCrit = (long)Stat(global::Stat.CriticalStrikeChancePlusPctVsBleedingEnemies)
                            + playerStats.GetValueOrDefault(global::Stat.CriticalStrikeChancePlusPctVsBleedingEnemies);
                        if (bleedingCrit != 0)
                            criticalChance = CriticalChanceOn(player, id,
                                SupportedCriticalChance(player, skillId, hand, offCritical, bleedingCrit),
                                SupportedCriticalChanceVsBlinded(player, skillId, release: false, swingCritical, bleedingCrit),
                                SupportedCriticalChanceVsGround(player, skillId, release: false, swingCritical, bleedingCrit));
                    }
                    critical = RollCriticalStrike(player, criticalChance, skillStats,
                            attack: !spell, skillRoll: criticalRoll, playerStats: playerStats)
                        && (spell || GameServer.Core.World.EvasionEntropy.ConfirmsCritical(hitChance, player.AttackAccuracyRandom));
                    int sourceMultiplier = CriticalMultiplierWithWeapon(sweepMultiplier, m, weaponLocal);
                    double at = ProjectileWorldTime(player);
                    int multiplier = CriticalMultiplierOn(m, sourceMultiplier, at, playerStats, skillStats);
                    // Deferred manual hits use the same lifetime guard as automatic payloads. The native link
                    // resolver excludes triggered skills from these supports.
                    var resonance = WithResonanceHitLocked(player, skillStats, rolled, mods, pen, at);
                    rolled = resonance.Packet;
                    (damage, resistance, var mitigated) = MonsterHitDamage(m, rolled, critical, attack: !spell, at,
                        resonance.Mods, resonance.Penetration,
                        gambler.InvertsResistance(AreaRng(player)), AreaRng(player));
                    hitCode = HitCode(mitigated);
                    var taken = MonsterTakenComponents(m, mitigated, spell, at);
                    if (critical) damage = DamageModel.CriticalDamage(damage, multiplier);
                    double more = damageMore * bloodMore;
                    int perChill = GolemStat(skillStats, global::Stat.FrostblinkDamagePlusPctFinalPer5PctChillEffectOnTarget);
                    if (perChill > 0)
                    {
                        double chill = m.Ailments.Magnitude(MonsterElementalAilments.Kind.Chill, at);
                        if (chill >= 5) more *= 1 + Math.Floor(chill / 5.0) * perChill / 100.0;
                    }
                    if (ReferenceEquals(brandedTarget, m))
                        more *= Math.Max(0, 1 + GolemStat(skillStats, global::Stat.TriggerBrandSupportHitDamagePlusPctFinalVsBrandedEnemy) / 100.0);
                    if (splashMore.TryGetValue(id, out double splashScale)) more *= splashScale;
                    if (more != 1) damage = (int)Math.Floor(damage * Math.Max(0, more));
                    int chaosDamage = MonsterHitChaosDamage(m, mitigated, spell, at, damage);
                    LeechOnHitTargetLocked(player, m, taken, damage, leech!, id);
                    damage = WithImpaleHitLocked(player, m, damage, rolled, critical,
                        sourceMultiplier + FullLifeCriticalMultiplier(m, playerStats, skillStats),
                        critical ? criticalImpale : impale, more).Damage;
                    if (removeStunAndKnockback)
                        stun = default;
                    else
                    {
                        stun = OwnerStun(player, gem.Effect, m, damage, mitigated, !spell && SkillsFor(player).IsMelee(gem.Effect), spell, critical,
                            playerStats, skillStats, localStunReduction: offHandWeapon ? 0 : null, weaponClass: sweepWeaponClass);
                        long thresholdReduction = extraStunThresholdReduction;
                        long stunDuration = extraStunDurationPct;
                        if (retaliation)
                        {
                            long Bonus(uint key) => (long)playerStats.GetValueOrDefault(key) + skillStats.GetValueOrDefault(key);
                            // The native modifier is negative for reduced enemy threshold.
                            thresholdReduction -= Bonus(global::Stat.RetaliationSkillStunThresholdPlusPct);
                            stunDuration += Bonus(global::Stat.RetaliationSkillStunDurationPlusPct);
                        }
                        if (thresholdReduction != 0 || stunDuration != 0)
                            stun = stun with
                            {
                                ThresholdReductionPct = ActorScale.Saturate(stun.ThresholdReductionPct + thresholdReduction),
                                DurationPct = ActorScale.Saturate(stun.DurationPct + stunDuration),
                            };
                    }
                    // Only the first victim has a freshly built player snapshot. After notification awaits, resolve the
                    // current raw source so a refund between victims cannot keep granting from the sweep's old snapshot.
                    var rampagePlayer = freshPlayer ? playerStats : null;
                    var rampageSource = RampageOnMeleeHitLocked(player, m, !spell && SkillsFor(player).IsMelee(gem.Effect), rampagePlayer);
                    uint lifeBefore = m.Life;
                    applied = ApplyMonsterHitLocked(player, m, damage, rampageSource, rampagePlayer, allyAt: null,
                        cull: Cull.Of(playerStats, skillStats, critical, spell).WithWeapon(weaponLocal), cullPlayer: playerStats,
                        chaosDamage: chaosDamage);
                    if (applied is not null)
                    {
                        if (applied.Value.Life < lifeBefore)
                            RecordResonanceHitLocked(player, m, skillStats, rolled, at);
                        CrueltyOnHitLocked(player, skillId, damage, lifeBefore, m.MaxLife, skillStats, at);
                        if (!spell)
                        {
                            ReleaseCriticalTrigger(player, ProjectilesFor(player),
                                criticalTrigger, critical, m.Hostile, m.Walk.Position(at), at);
                            ReleaseItemCriticalTrigger(player, ProjectilesFor(player),
                                itemCriticalTrigger, critical, m.Hostile, m.Walk.Position(at), at);
                        }
                        else ArcaneSurgeOnSpellHitLocked(player, playerStats, m);
                        if (!spell && applied.Value.Life == 0) ReleaseMeleeKillTrigger(player,
                            ProjectilesFor(player), meleeKillTrigger, true, m.Hostile, m.Walk.Position(at), at);
                        player.RecentEvents.RecordCriticalOutcome(at, critical, attack: hitPlan.Kind == SkillHitKind.Attack);
                        if (ElementalOverloadCritLocked(player, skillId, SkillsFor(player).EffectId(gem.Effect), playerStats, critical)
                            is { } eoBuff)
                            ProjectilesFor(player).Packet(eoBuff, $"elemental overload on skill 0x{skillId:x4}");
                        landed++;
                        if (hammer is null) hammerTargets.Add((id, m.Walk.Position(at)));
                        // Item Quantity Support: the kill's loot rolls read the supported skill's share.
                        if (global::Stat.BaseKilledMonsterDroppedItemQuantityPlusPct is var quantityKey)
                            m.KilledWithItemQuantityPct = skillStats.GetValueOrDefault(quantityKey);
                        if (global::Stat.BaseKilledMonsterDroppedItemRarityPlusPct is var rarityKey)
                            m.KilledWithItemRarityPct = skillStats.GetValueOrDefault(rarityKey);
                        SoulEaterHit(player, m);
                        if (weaponAttack)
                        {
                            RecordWeaponContactLocked(player, at, weaponContactSource, secondWeaponContactSource);
                            if (critical)
                            {
                                ItemMinionsOnWeaponCriticalLocked(player, weaponContactSource, m, at);
                                if (!ReferenceEquals(weaponContactSource, secondWeaponContactSource))
                                    ItemMinionsOnWeaponCriticalLocked(player, secondWeaponContactSource, m, at);
                            }
                        }
                        if (applied.Value.Life == 0) RecordTinctureKill(m, tinctureKill);
                        if (!spell && player.TinctureEffects.AnyEffective) TinctureOnWeaponHitLocked(player, playerStats, m, lifeBefore, at, AreaRng(player));
                        // Devour: a killing blow recovers the support's life/mana and consumes the corpse (the kill removes it).
                        if (applied.Value.Life == 0) DevourOnKill(player, m, skillStats);
                        AilmentsOnHitLocked(player, id, m, mitigated, spell, critical, multiplier, playerStats, ailmentSkillStats!, at,
                            AreaRng(player), reflected: reflected, poison: profile.Poison, sourceSkill: skillId,
                            blind: BlindHitInput.Capture(playerStats, skillStats),
                            hitOrigin: IsLeapSlam(player, gem.Effect) ? centre : null,
                            attackGains: AttackAilmentGains.Capture(playerStats, SkillsFor(player).IsAttack(gem.Effect)));
                        if (GolemStat(skillStats, global::Stat.RemoveChillOnEnemyWhenYouHit) != 0)
                            m.Ailments.RemoveActive(MonsterElementalAilments.Kind.Chill, at);
                        if (scheduledSession is not null || (melee ?? !spell) || damage > 0)
                            nativeKnockback = TakeNativeKnockbackLocked(m);
                        // As a projectile's hit: a hit with fire that leaves the monster alive may ignite it, an attack with
                        // physical damage bleed it.
                        if (m.Life > 0 && (critical ? profile.Ignite.CriticalMax : profile.Ignite.Max) > 0
                            && IgniteOnHitLocked(player, id, m, profile.Ignite, critical, at,
                                attackGains: AttackAilmentGains.Capture(playerStats, SkillsFor(player).IsAttack(gem.Effect))) is { } packets)
                            (ignited, igniteLog) = (packets, IgnitedLog(id, m, critical, at));
                        if (m.Life > 0 && rolled[DamageType.Physical].Max > 0
                            && BleedOnHitLocked(player, id, m, profile.Bleed, critical, at,
                                BleedReflected(playerStats)) is { } cut)
                            (bled, bleedLog) = (cut, BleedingLog(id, m, critical, at));
                        if (hex is not null)
                        {
                            if (victim == 0) hexblastExpanded = true;
                            int keep = Stat(global::Stat.HexblastPctChanceToNotConsumeHex);
                            if (keep < 100 && (keep <= 0 || !RandomRolls.Chance(keep, 100, AreaRng(player))))
                                ConsumeHexblastHex(player, id, m, hex);
                        }
                    }
                    if (applied is { StageStarts: true } staged)
                    {
                        CommitBossStage(player, id, m, ProjectileWorldTime(player));
                        applied = staged with { StageStarts = false };
                    }
                    if (applied is not null)
                    {
                        var fortificationDamage = RawFortificationDamage(rolled, critical, multiplier);
                        bool strike = !spell && SkillsFor(player).IsStrike(gem.Effect);
                        var fortifySkill = WithStrikeFortifySkillStat(playerStats, skillStats, strike);
                        bool fortifiedThisHit = !fortificationGranted && (fortificationGranted = FortificationOnHitLocked(player, m,
                            fortificationDamage, !spell && SkillsFor(player).IsMelee(gem.Effect), playerStats, fortifySkill,
                            movement: SkillsFor(player).IsMovement(gem.Effect)));
                        if (fortifiedThisHit) AfterFortificationGainLocked(player, playerStats);
                        stun = stun with { FortificationDamage = fortificationDamage, Fortified = fortifiedThisHit };
                        if (!rageGranted) rageGranted = RageOnHitWithWeaponLocked(player, attack: !spell,
                            melee: !spell && SkillsFor(player).IsMelee(gem.Effect), critical, sweepWeaponClass,
                            skillStats, playerStats: playerStats, secondWeaponClass: secondSweepWeaponClass);
                        if (!spell && SkillsFor(player).IsMelee(gem.Effect))
                        {
                            GainValourFromMeleeLocked(player, m, applied.Value.Life == 0, at, playerStats);
                            CombatRushOnHit(player, skillStats, at);
                            HallowOnMeleeHit(player, m, skillStats, at);
                            ExcommunicateOnMeleeHit(player, m, skillStats, at);
                        }
                    }
                }
                if (applied is not { } hit) continue;
                onHit?.Invoke(id);
                if (scheduledSession is { } scheduled)
                {
                    using (ProjectileMutation(player, advance: false))
                    {
                        ChargesOnHit(player, m, critical, spell, gem.Effect, gem.Level, gem.Quality, primary: victim == 0, skillStats: skillStats);
                        if (reflected.Count > 0 && playerStats is not null)
                            ReflectPendingAilmentsLocked(player, reflected, ProjectileWorldTime(player), playerStats, AreaRng(player));
                        CommitScheduledMeleeHit(scheduled, id, m, hit, damage, critical, stun, sequence, gem.Name,
                            settleKill: singleStrikeTarget is not null, hitCode: hitCode, knockback: nativeKnockback);
                        if (singleStrikeTarget is null && hit.Life == 0) deaths.Add((id, m));
                    }
                    scheduled.Enqueue(() => SendDotBuff(player, ignited, igniteLog));
                    scheduled.Enqueue(() => SendDotBuff(player, bled, bleedLog));
                    continue;
                }
                if (critical) _ = GainFlaskChargesOnCrit(player);
                if (critical) await MaybeCreateProfaneGround(player, m.Walk.Position(ProjectileWorldTime(player)));
                await NotifyMonsterHit(player, id, m, hit, damage,
                    $"{gem.Name}{(hammer is null ? "" : " (holy hammer)")}{(critical ? " (critical)" : "")}{handNote}{(resistance is null ? "" : $", {resistance}")}",
                    melee: melee ?? !spell, sequence: sequence, deaths: deaths, hitCode: hitCode, stun: stun, critical: critical,
                    impactArt: !spell && hammer is null ? MeleeImpactArt(SkillsFor(player).EffectId(gem.Effect)) : null,
                    knockback: nativeKnockback);
                await SendDotBuff(player, ignited, igniteLog);
                await SendDotBuff(player, bled, bleedLog);
                ChargesOnHit(player, m, critical, spell, gem.Effect, gem.Level, gem.Quality, primary: id == targets[0].Id, skillStats: skillStats);
            }
        }
        finally
        {
            if (sweepStats is { } stats) stats.Dispose();
        }
        if (reflected.Count > 0 && playerStats is not null)
            using (ProjectileMutation(player, advance: scheduledSession is null)) ReflectPendingAilmentsLocked(player, reflected, ProjectileWorldTime(player), playerStats, AreaRng(player));
        foreach (var (deadId, dead) in deaths)
            if (scheduledSession is { } scheduled)
                CommitProjectileKill(scheduled, deadId, dead, scheduled.Runtime.Now, AreaRng(player));
            else await MonsterKilled(player, deadId, dead);
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
        Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {gem.Name}: {(hammer is not null ? "holy hammer" : spell ? "spell" : "attack")} hit {targets.Count} monster(s) {shape}{contact}{handNote} (t={AreaTime(player):0.000})");
        // An immediate (triggered) spell has no cast clip to draw Discharge's explosion: the server draws it.
        if (discharge is not null
            && SpendDischargedCharges(player, gem.Name, gem.Gem, discharge, Stat(global::Stat.DischargeChanceNotToConsumeChargesPct), targets.Count)
            && immediate && spell)
            await ShowTriggeredDischarge(player, gem.Gem, discharge, centre, radius);
        // Holy Sweep's hammers fall on enemies this sweep struck; Hammerfalls' waves fall around the player's cell, as
        // the client centres them (its controller's +0x35c is the owner's cell).
        if (hammer is null && !spell && HolySweepHammers.For(Stat) is { } hammers)
            _ = RunHolyHammers(player, skillId, gem, hammers, skillStats, from, to, radius,
                hammerTargets, damageMore, sequence, repeat, criticalRoll);
        if (BloodCharges.Uses(Stat) && playerStats is not null)
        {
            bool changed;
            changed = player.PlayerLife > 0 && SettleBloodChargesLocked(player, playerStats, deaths.Count, landed - deaths.Count);
            if (changed)
            {
                if (scheduledSession is { } scheduled)
                    scheduled.Enqueue(() => SendBloodCharges(player, $"{gem.Name}: {landed} hit, {deaths.Count} killed"));
                else await SendBloodCharges(player, $"{gem.Name}: {landed} hit, {deaths.Count} killed");
            }
        }
        if (strikeIndex == 0 && strikes > 1)
        {
            (double gap, bool keyframe) = GemAttackStrikeGap(player, contactClip);
            double next = contactAt + gap * SupportedAttackSeconds(player, skillId, hand);
            await AreaDelay(player, Math.Max(0, next - AreaTime(player)));
            await WhilePaused(player);
            if (Replaced() || player.Closed || !Current()) return;
            Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {gem.Name}: strike 2 at {next:0.000} "
                + $"({(keyframe ? contactClip : "modelled half-attack gap")})");
            await AreaSkillHitStrike(player, skillId, gem, from, to, immediate: true,
                sequence: sequence, actionToken: actionToken, cooldownUse: cooldownUse, melee: melee, damageMore: damageMore,
                excludeMonster: excludeMonster, criticalRoll: criticalRoll, channelGeneration: channelGeneration, targetId: targetId,
                repeat: repeat, radiusOverride: radiusOverride, once: null, repeatDelay: repeatDelay, radiusMore: radiusMore,
                sacrifice: sacrifice, swingAt: null, triggeredCurrent: triggeredCurrent, discharge: discharge,
                strikeIndex: 1, firstContactAt: next, ruthlessMore: ruthlessMore, priorTargets: alreadyHit, scaledBlastChaos: scaledBlastChaos,
                singleStrikeTarget: singleStrikeTarget, onHit: onHit, scheduledSession: scheduledSession, cryExerted: cryExerted, prepaidRawExtra: prepaidRawExtra, baseChaos: baseChaos, hammer: hammer, brandedTarget: brandedTarget,
                targetFilter: targetFilter, extraStunThresholdReduction: extraStunThresholdReduction, extraStunDurationPct: extraStunDurationPct,
                extraKnockbackDistancePct: extraKnockbackDistancePct, removeStunAndKnockback: removeStunAndKnockback, offHandWeapon: offHandWeapon);
        }
    }

    // Holy Sweep's hammers (HolySweepHammers): each falls on its point and slams FallSeconds later, an AreaSkillHit of
    // its own around that point (the secondary radius, grown by area of effect) for the sweep's hit with the hammers'
    // less damage. Holy Sweep's land on up to holy_sweep_number_of_holy_bolts_to_create enemies the sweep struck, once
    // each; Hammerfalls' waves fall on the monsters around where it swung (HolySweepHammers.Wave), one wave every rate
    // (faster with the main hand's attack speed) while its duration lasts. Both draw as the client draws its hammers, from
    // the use's sequence, in the client's grid order (ClientEntityGrid). They outlive the swing (a later action does not
    // stop them); a death, an area change or a closed connection does. The hammer hits send no 0x1af: the client draws
    // its own hammers (11-skills.md §9a). Its Holy Sweep candidates are the enemies its own hit roll accepted, which can
    // differ from the server's struck ones. The hammers are hits of the sweep's use, so they share its critical roll.
    static async Task RunHolyHammers(Player player,
        ushort skillId, (ushort Id, int Effect, string Name, int Level, int Quality, uint Gem) gem, HolySweepHammers.Settings hammers,
        IReadOnlyDictionary<uint, int> skillStats, (int X, int Y) centre, (int X, int Y) facing, int sweepRadius,
        List<(uint Id, (int X, int Y) At)> struck, double damageMore, ushort sequence, int repeat, SkillCriticalRoll? criticalRoll)
    {
        try
        {
            int epoch = player.ProjectileEpoch;
            double start = AreaTime(player);
            var areaStats = SkillAreaStats(player, skillStats, gem.Effect);
            int Area(uint k) => areaStats.GetValueOrDefault(k);
            int radius = SkillArea.Radius(hammers.BaseRadius, Area, spell: false);
            var waves = new List<(double At, List<(int X, int Y)> Points)>();
            // The swings this use repeats after this one (Multistrike), which the client's keys carry (HolySweepHammers).
            int repeatsLeft = Math.Max(0, MeleeAttackRepeats(player, skillId) - repeat);
            if (!hammers.Hammerfall)
            {
                List<(uint Id, (int X, int Y) At)> ordered;
                ordered = ClientEntityGrid.Ordered(player, struck, s => s.Id, s => s.At);
                var picked = HolySweepHammers.Pick(ordered, hammers.Count, sequence, player.Id,
                    s => HolySweepHammers.SweepAngle(centre, facing, s.At), repeatsLeft);
                if (picked.Count == 0) return;
                waves.Add((HolySweepHammers.FallSeconds, [.. picked.Select(p => p.At)]));
                Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {gem.Name}: {picked.Count} holy hammer(s) of "
                    + $"{hammers.Count} fall on {string.Join(", ", picked.Select(p => $"0x{p.Id:x}"))} of {struck.Count} struck, "
                    + $"{radius} cells each, {hammers.More:0.##}x damage{(hammers.CannotBeEvaded ? ", cannot be evaded" : "")}");
            }
            else
            {
                // The player's own and the skill's own increases each once: Area holds both.
                var playerStats = CharacterPlayerStats(player);
                int Player(uint k) => GolemStat(playerStats, k);
                int Own(uint k) => skillStats.GetValueOrDefault(k);
                double duration = SkillDurationModifiers.Seconds(hammers.DurationMs, Player, Own, DurationKinds(player, gem.Effect));
                // The rate the client's controller holds: the combined_main_hand_attack_speed_+% of the skill list it last
                // received, not the character's attack speed now (a Rage or charge change since is not in it until the
                // list goes out again).
                int attackSpeed = SentSkillStat(player, skillId, Stat.CombinedMainHandAttackSpeedPlusPct)
                    ?? SupportedMainHandAttackSpeedPct(player, skillId);
                double every = HolySweepHammers.WaveSeconds(hammers.RateSeconds, attackSpeed);
                var times = HolySweepHammers.WaveTimes(every, duration);
                Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {gem.Name}: hammerfall at ({centre.X}, {centre.Y}) "
                    + $"within {sweepRadius} cells for {duration:0.##} s, {times.Count} wave(s) of {hammers.Count} every "
                    + $"{every:0.###} s ({attackSpeed}% attack speed), {radius} cells each, {hammers.More:0.##}x damage");
                // Each wave picks its hammers among the monsters there at that moment (HolySweepHammers.Wave), and they
                // hit HammerfallHitSeconds later where they fell.
                var taken = new Dictionary<uint, int>();
                bool Walkable(int x, int y) => player.Layout.Walkable is not { } floor || floor(x, y) && !player.Barrier.Contains((x, y));
                for (int index = 0; index < times.Count; index++)
                {
                    double at = times[index];
                    if (!await WaitProjectileTime(player, start + at, epoch)) return;
                    List<(uint Id, (int X, int Y) At, int Size)> monsters = [];
                    double now = AreaTime(player);
                    foreach (var (id, m) in player.Area.Monsters)
                        if (m.Hostile && !m.Dead && m.Life > 0 && m.OwnerId is null)
                            monsters.Add((id, m.Walk.Position(now), Math.Clamp((m.Variety?.ObjectSize ?? 1) - 1, -1, 127)));
                    monsters = ClientEntityGrid.Ordered(player, monsters, m => m.Id, m => m.At);
                    var wave = HolySweepHammers.Wave(monsters, taken, centre, sweepRadius, hammers.Count, Walkable,
                        sequence, player.Id, index, repeatsLeft);
                    Console.WriteLine($"[gateway] {player.Connection.Remote} skill 0x{skillId:x4} {gem.Name}: hammer wave at {at:0.##} s: "
                        + string.Join(", ", wave.Select(h => h.Id is uint id ? $"0x{id:x}" : $"({h.At.X}, {h.At.Y})")));
                    if (!await WaitProjectileTime(player, start + at + HolySweepHammers.HammerfallHitSeconds, epoch)) return;
                    foreach (var (_, point) in wave)
                        await AreaSkillHit(player, skillId, gem, point, point, immediate: true, sequence: sequence,
                            melee: false, damageMore: damageMore * hammers.More, criticalRoll: criticalRoll, repeat: repeat,
                            radiusOverride: radius, hammer: hammers);
                }
                return;
            }
            foreach (var (at, points) in waves)
            {
                if (!await WaitProjectileTime(player, start + at, epoch)) return;
                foreach (var point in points)
                    await AreaSkillHit(player, skillId, gem, point, point, immediate: true, sequence: sequence,
                        melee: false, damageMore: damageMore * hammers.More, criticalRoll: criticalRoll, repeat: repeat,
                        radiusOverride: radius, hammer: hammers);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public static async Task<bool> WaitProjectileTime(Player player, double until, int epoch)
    {
        while (true)
        {
            if (!player.ProjectileActive(epoch) || player.RageRetired) return false;
            if (!player.Paused && AreaTime(player) >= until) return true;
            await AreaDelay(player, 0.016);
        }
    }

    // A hit on a living monster: life is mutated once in the area's frame, then notifications await outside the lock. False when
    // the monster was dead already or the action was replaced (the hit did not land).
    public static async Task<bool> HitMonster(Player player, uint id, SpawnedMonster m,
        int damage, string by, bool log = true, bool melee = false, ushort sequence = 0, int? actionToken = null,
        CooldownUseTicket? cooldownUse = null, Action? accepted = null, Func<int>? damageAtContact = null,
        List<(uint Id, SpawnedMonster M)>? deaths = null)
    {
        MonsterHit? hit;
        using (ProjectileMutation(player))
        {
            if (player.Closed || player.LeavingArea || player.RageRetired || player.PlayerLife == 0 || player.Paused) return false;
            if (cooldownUse is not null ? !cooldownUse.Current(player) : actionToken is int token && !PlayerActionCurrent(player, token)) return false;
            if (m.Dead || m.Life == 0 || !player.Area.Monsters.TryGetValue(id, out var current) || !ReferenceEquals(current, m)) return false;
            if (damageAtContact is not null) damage = damageAtContact();
            hit = HitUnlessBlockedLocked(player, m, damage);
            if (hit is { StageStarts: true } staged)
            {
                CommitBossStage(player, id, m, ProjectileWorldTime(player));
                hit = staged with { StageStarts = false };
            }
            if (hit is not null) accepted?.Invoke();
        }
        if (hit is { } applied) await NotifyMonsterHit(player, id, m, applied, damage, by, log, melee, sequence, deaths);
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
        bool invertElemental = false, Random? random = null, bool trapOrMine = false, int additionalTakenPct = 0)
    {
        var described = new List<string>();
        mods = ImpurityHitMods(m, now, mods);
        var strike = new HitResistances.Strike(Critical: critical, Attack: attack);
        var mitigated = rolled.Select((type, range) =>
        {
            var resistanceType = type;
            var value = MonsterHitResistance(m, type, pen, now);
            if (type == DamageType.Chaos && range.Max > 0
                && mods.ChaosSelection != HitResistances.ResistanceSelection.Default)
            {
                bool highest = mods.ChaosSelection == HitResistances.ResistanceSelection.Highest;
                int selected = DamageModel.Capped(value.Value, value.Maximum);
                foreach (var alternative in DamageTypes.ConversionOrder)
                {
                    if (!DamageTypes.IsElemental(alternative)) continue;
                    var candidate = MonsterHitResistance(m, alternative, pen, now);
                    int resistance = DamageModel.Capped(candidate.Value, candidate.Maximum);
                    if (highest ? resistance <= selected : resistance >= selected) continue;
                    selected = resistance;
                    resistanceType = alternative;
                    value = candidate;
                }
            }
            if (range.Max > 0 && HitResistances.Describe(resistanceType, value, strike, mods) is { } note) described.Add(note);
            double effective = HitResistances.FinalValue(resistanceType, value, strike, mods, invertElemental);
            return range.Scale(Math.Max(0, 1 - effective / 100));
        });
        int ignorePhysicalChance = ActorScale.Saturate((long)mods.IgnorePhysicalReductionChance
            + (attack ? mods.TincturedWeaponIgnorePhysicalReductionChance : 0));
        bool ignorePhys = mods.IgnorePhysicalReduction || (rolled[DamageType.Physical].Max > 0
            && GameServer.Core.World.RandomRolls.Chance(ignorePhysicalChance, 100, random ?? Random.Shared));
        int takenPct = additionalTakenPct + (trapOrMine ? BearTrapDamageTaken(m, now) : 0);
        int damage = MonsterTakesMitigatedCore(m, mitigated, spell: !attack, now, takenPct, ignorePhys, random);
        return (damage,
            described.Count > 0 ? string.Join(", ", described) : null, mitigated);
    }

    public static HitResistances.Mods HitResistMods(IReadOnlyDictionary<uint, int> player,
        IReadOnlyDictionary<uint, int>? skill = null)
    {
        var a = HitResistances.Mods.From(player, Stats.Key);
        var b = skill is null ? default : HitResistances.Mods.From(skill, Stats.Key);
        return new(a.CriticalIgnoreElemental | b.CriticalIgnoreElemental,
            a.AttackCriticalIgnoreElemental | b.AttackCriticalIgnoreElemental,
            a.NonCriticalPenetration + b.NonCriticalPenetration, a.CannotPenetrateOrIgnore | b.CannotPenetrateOrIgnore,
            IgnoreFire: Has(Stat.VirtualHitsIgnoreEnemyFireResistance),
            IgnoreChaos: Has(Stat.VirtualHitsIgnoreEnemyChaosResistance),
            IgnorePhysicalReductionChance: Value(Stat.HitsIgnoreEnemyMonsterPhysicalDamageReductionPctChance))
        {
            ChaosSelection = Has(Stat.ChaosDamageResistedByHighestResistance) ? HitResistances.ResistanceSelection.Highest
                : Has(Stat.ChaosDamageResistedByLowestResistance) ? HitResistances.ResistanceSelection.Lowest
                : HitResistances.ResistanceSelection.Default,
            TincturedWeaponIgnorePhysicalReductionChance = Has(Stat.TinctureApplied)
                ? Value(Stat.HitsIgnoreEnemyMonsterPhysicalDamageReductionPctChanceWithTincturedWeapons) : 0,
            CriticalPenetration = Value(Stat.CriticalStrikesPenetratesPctElementalResistancesWhileAffectedByZealotry),
        };

        bool Has(uint name) => name is var key
            && (player.GetValueOrDefault(key) != 0 || (skill?.GetValueOrDefault(key) ?? 0) != 0);
        int Value(uint key) => ActorScale.Saturate((long)player.GetValueOrDefault(key) + (skill?.GetValueOrDefault(key) ?? 0));
    }

    // The short state mutation is shared with projectile impact; no awaits in the area's frame. A minion's hit (allyAt: where
    // it hits from) is not the player's hit for his hit-recently.
    public static MonsterHit? ApplyMonsterHitLocked(Player player, SpawnedMonster m, int damage,
        bool? rampageSource = null, IReadOnlyDictionary<uint, int>? playerStats = null, (int X, int Y)? allyAt = null,
        Cull cull = default) =>
        ApplyMonsterHitLocked(player, m, damage, rampageSource, playerStats, allyAt, cull, null);

    static MonsterHit? ApplyTypedMonsterHitLocked(Player player, SpawnedMonster m, int damage, int chaosDamage,
        (int X, int Y)? allyAt = null, IReadOnlyDictionary<uint, int>? playerStats = null, Cull cull = default) =>
        ApplyMonsterHitLocked(player, m, damage, null, playerStats, allyAt, cull, null, chaosDamage);

    // The area-hit caller passes the attack's cached snapshot as `cullPlayer` for every victim: Rampage's `player` is
    // rebuilt only for the first target, so the two contexts must stay separate. This is an overload rather than an
    // extra optional parameter so the seven-argument entry point keeps its reflection-visible signature.
    public static MonsterHit? ApplyMonsterHitLocked(Player player, SpawnedMonster m, int damage,
        bool? rampageSource, IReadOnlyDictionary<uint, int>? playerStats, (int X, int Y)? allyAt,
        Cull cull, IReadOnlyDictionary<uint, int>? cullPlayer) =>
        ApplyMonsterHitLocked(player, m, damage, rampageSource, playerStats, allyAt, cull, cullPlayer, chaosDamage: 0);

    public static MonsterHit? ApplyMonsterHitLocked(Player player, SpawnedMonster m, int damage,
        bool? rampageSource, IReadOnlyDictionary<uint, int>? playerStats, (int X, int Y)? allyAt,
        Cull cull, IReadOnlyDictionary<uint, int>? cullPlayer, int chaosDamage) =>
        ApplyMonsterHitCoreLocked(player, m, damage, rampageSource, playerStats, allyAt, cull, cullPlayer, chaosDamage);

    static MonsterHit? ApplyMonsterHitCoreLocked(Player player, SpawnedMonster m, int damage,
        bool? rampageSource, IReadOnlyDictionary<uint, int>? playerStats, (int X, int Y)? allyAt,
        Cull cull, IReadOnlyDictionary<uint, int>? cullPlayer, int chaosDamage, ImpaleContact? impale = null)
    {
        if (m.Dead || m.Life == 0) return null;
        // The Divine shrine's aura: a monster wearing base_cannot_be_damaged (MonsterAuraEffect puts it in the monster's
        // EncounterStats, whether it was there when the shrine spawned or walked in later) takes no hit at all, so every
        // path through this funnel leaves its life alone.
        if (m.Effects[Stat.BaseCannotBeDamaged] > 0) return null;
        // A Misery essence's or Temporal rare's proximity shield blocks a hit from outside its ring; Scorn's avoid_damage_% can avoid the hit.
        if (MapEssences.ProximityShieldBlocksLocked(player, m, allyAt) || MonsterProximityShieldBlocksLocked(player, m, allyAt)) return null;
        if (damage > 0 && m.Effects[Stat.AvoidDamagePct] is > 0 and var avoid && Random.Shared.Next(100) < avoid) return null;
        double hitNow = ProjectileWorldTime(player);
        if (impale is { } contact)
            damage = ApplyImpaleContact(m, damage, contact.Physical, contact.At, hitNow, contact.Settings, contact.Random).Damage;
        int lifeDamage = AbsorbMonsterEnergyShieldLocked(player.Area, m, damage, chaosDamage, AreaTime(player));
        uint before = m.Life;
        if (allyAt is null) SpiderHitRewardsLocked(player, m, hitNow, playerStats);
        (m.Life, bool starts) = TakeOwnerHitLocked(player, m, lifeDamage, cull, rampageSource, playerStats, cullPlayer);
        if (m.Life == 0) m.Overkill = (int)Math.Clamp((long)lifeDamage - before, 0, int.MaxValue);
        if (m.Kit.Count > 0) BossKits.TakeDamage(m, (long)before - m.Life);
        CaptureUnhingedHitKillLocked(player, m, playerHit: allyAt is null);
        if (allyAt is null)
        {
            player.LastEnemyHitAt = m.PlayerHitAt = hitNow;
            player.RecentEvents.Record(StatConditions.HitEnemy, m.PlayerHitAt);
            GainConvergenceOnHitLocked(player, m, m.PlayerHitAt, playerStats);
        }
        if (m.Life == 0)
        {
            m.Aggro = false;
            m.Dead = true;
            CleanupDeadMonsterLocked(player.Area, m.ObjectId, m);
            CollectVaalSoulAtDeathLocked(player, m);
        }
        else if (m.Hostile)
        {
            var pos = m.Walk.Position(hitNow);
            if (!(m.ReturningHome && m.SpawnAnchor != default && Dist(pos, m.SpawnAnchor) > 2.0 * Math.Max(40, m.AggroRange)))
            {
                bool wasAggro = m.Aggro;
                m.Aggro = true;
                m.ReturningHome = false;
                m.LastTarget = null;
                if (!wasAggro) AlertPackLocked(player, m, pos, hitNow);
            }
        }
        TriggerElementalRelicLocked(player, m, hit: damage > 0, killed: m.Life == 0, allyAt);
        if (allyAt is null && damage > 0 && !player.Reservations.Secondary) ReservationHitLocked(player, m);
        return new(m.Life, starts);
    }

    // Keep the signature used by mods built against 0.6.0-beta.
    public static Task NotifyMonsterHit(Player player, uint id, SpawnedMonster m,
        MonsterHit hit, int damage, string by, bool log, bool melee, ushort sequence,
        List<(uint Id, SpawnedMonster M)>? deaths, byte hitCode, StunHit? stun, bool critical, string? impactArt) =>
        NotifyMonsterHit(player, id, m, hit, damage, by, log, melee, sequence, deaths, hitCode, stun, critical, impactArt,
            knockback: null);

    public static Task NotifyMonsterHit(Player player, uint id, SpawnedMonster m,
        MonsterHit hit, int damage, string by, bool log = true, bool melee = false, ushort sequence = 0,
        List<(uint Id, SpawnedMonster M)>? deaths = null, byte hitCode = 0, StunHit? stun = null, bool critical = false, string? impactArt = null,
        SpawnedMonster.NativeKnockback? knockback = null) =>
        NotifyMonsterHitCore(player, id, m, hit, damage, by, log, melee, sequence, deaths, hitCode, stun,
            critical: critical, impactArt: impactArt, knockback: knockback);

    static async Task NotifyMonsterHitCore(Player player, uint id, SpawnedMonster m,
        MonsterHit hit, int damage, string by, bool log, bool melee, ushort sequence,
        List<(uint Id, SpawnedMonster M)>? deaths, byte hitCode, StunHit? stun, bool critical, string? impactArt,
        SpawnedMonster.NativeKnockback? knockback = null)
    {
        var (life, stageStarts) = hit;
        if (life == 0)
        {
            SetStats(player, id, [(Stat.HideMiniLifeBar, 1)]); Note(!log ? null : $"0x01b3 monster 0x{id:x} hide_mini_life_bar = 1");
            EndDot(player, id, ref m.Poisons.Buff, m.Poisons.BuffSource);
        }
        // The hit result for the client's damage/effect pipeline (0x1af): a melee hit plays the target's blood and the weapon
        // impact sound "bonk" locally, 0x1b1 stays authoritative on life. Spells and projectiles have their own impact and skip it.
        // The life update follows its hit message in the attacker's turn; one with no hit message stays where it was.
        var order = melee ? HitOrder(player, player.Id) : default;
        if (melee)
        {
            double now = AreaTime(player);
            var (px, py) = player.Walk.Position(now);
            var (mx, my) = m.Walk.Position(now);
            await SendWorldRecord(player, player.Id, MeleeHitFeedback(player.Id, sequence, id, hitCode,
                    HitAngle(mx - px, my - py), critical, impactArt, order.Key, order.Hit, knockback),
                !log ? null : $"0x01af player hit on monster 0x{id:x} (skill, sequence 0x{sequence:x4})");
            if (impactArt is not null) await PresentMeleeImpact(player, impactArt, (px, py), (mx, my));
        }
        else if (damage > 0)
        {
            double now = AreaTime(player);
            var (px, py) = player.Walk.Position(now);
            var (mx, my) = m.Walk.Position(now);
            var hitOrder = HitOrder(player, player.Id);
            byte code = HitCodeWithVariation(hitCode, damage, m.MaxLife, critical || life == 0);
            ushort flags = (ushort)(0x4000 | HitTypeFlag(code) | (critical ? 0x0020 : 0));
            byte kind = 0x03, angle = HitAngle(mx - px, my - py);
            ApplyNativeKnockback(knockback, ref flags, ref kind, ref angle, out ushort u3c, out ushort u3e);
            await SendWorldRecord(player, player.Id, InstanceProtocol.ActionHit(player.Id, sequence == 0 ? (ushort)0xffff : sequence, id,
                    flags: flags, kind: kind, code: code, angle: angle, u3c: u3c, u3e: u3e, orderKey: hitOrder.Key, checkpoint: hitOrder.Hit),
                !log ? null : $"0x01af spell/area hit on monster 0x{id:x} (sequence 0x{sequence:x4})");
        }
        // The hit reaction: one of the rig's TakeHit clips and its SoundEvent (0x1b2), as in Attack.
        await SendFlinch(player, id, m, stun ?? new(damage), log);
        Note(!log && life > 0 ? null : $"0x01b1 monster 0x{id:x} hit by {by} for {damage}, life {life}/{m.MaxLife}{(life == 0 ? " (dies)" : "")}");
        // One sweep's deaths (loot, experience, quests) wait until every monster of the sweep has its hit (AreaSkillHit).
        if (life == 0) { if (deaths is not null) deaths.Add((id, m)); else await MonsterKilled(player, id, m); }
        if (stageStarts) _ = RunBossStage(player, id, m);
        if (m.ThresholdReached) await QuestThreshold(player, id, m);
    }

    // The hit reaction: one of the rig's TakeHit clips and its SoundEvent, S→C 0x1b2 kind 0 (see Attack and HitMonster).
    // A killing blow gets none: the client's Life component runs the death instead, and the flinch would only be cut off.
    // The gates (HitReaction) keep it a stun-like reaction rather than a loop; see that class. The monster is checked again
    // inside the send gate: a hit that kills it meanwhile marks it Dead before it sends its own 0x1b1, so the flinch either
    // goes out before the death or not at all.
    public static Task SendFlinch(Player player, uint id,
        SpawnedMonster m, StunHit stun, bool log) => SendMonsterFlinch(player, id, m, stun, log);

    public sealed record PlayerRecoveryIntent(int Epoch, int Action, double Until, float Seconds);

    sealed record SpellWindup(int Action, int Recovery, int Epoch, double Until);
    static readonly RuleTable<Player, SpellWindup> SpellWindups = new();

    static bool InSpellCast(Player player, double now) =>
        player.PaidChannel is { } channel && CastingSpell(player, channel.Effect) && PaidChannelCurrent(player, channel)
        || SpellWindups.TryGetValue(player, out var windup)
            && now < windup.Until && windup.Action == player.ActionCount
            && windup.Recovery == player.ActionInterruptionGeneration && windup.Epoch == player.ProjectileEpoch;

    // Cancellation belongs to the hit boundary; the native animation is a projection of that committed interval.
    public static PlayerRecoveryIntent? CommitPlayerHitRecoveryLocked(Player player, int damage, double now)
    {
        if (damage <= 0) return null;
        var stats = CharacterPlayerStats(player);
        int Get(uint key) => stats.GetValueOrDefault(key);
        void Trace(string reason)
        {
            if (Get(AntiquarianEffects.StunRecoverySource) > 0)
                Console.WriteLine($"[gateway] Turtle Shell stun: {reason}");
        }
        // The panel publishes the per-Brutal-Charge threshold coefficient on every character; the charges held (converted
        // Endurance Charges, ChargeEffects) are already in stun_threshold_+%, so it is not an unsupported stat.
        if (stats.FirstOrDefault(s => s.Value != 0 && PlayerHitRecovery.UnsupportedStat(s.Key)) is { Key: > 0 } unsupported)
        { Trace("unsupported stat " + Stats.Name(unsupported.Key)); return null; }
        // Skill immunity can depend on a native action's contact phase, which this server does not track yet.
        // Conservatively skip builds with any equipped skill carrying it, rather than stun an uninterruptible phase.
        if (GemSkillEffects(player).Any(g => SkillsFor(player).EffectStats(g.Effect, g.Level, g.Quality)
            .Any(s => s.Item2 != 0 && s.Item1 == Stat.SkillCannotBeStunned)))
        { Trace("unsupported skill immunity"); return null; }
        if (PlayerHasEnergyShieldForRecovery(player, Get)) { Trace("energy shield present"); return null; }
        // A freeze already holds the player, and its action factor would stretch the recovery a hundredfold.
        if (player.ElementalAilments.ActionFactor(now) == 0) { Trace("frozen"); return null; }
        // Soul of the Brine King: cannot be stunned if stunned or blocked a stunning hit in the past 2 seconds.
        if (Get(Stat.CannotBeStunnedIfHaveBeenStunnedOrBlockedStunningHitInPast2Seconds) != 0
            && now - player.LastStunnedOrBlockedStunAt < 2.0)
        { Trace("brine king stun immunity"); return null; }
        double chance = PlayerHitRecovery.Chance(damage, player.PlayerMaxLife, Get);
        int ignoreWhileCasting = Get(Stat.AvoidInterruptionWhileCastingPct);
        if (chance > 0 && ignoreWhileCasting > 0 && InSpellCast(player, now))
            chance *= (100 - Math.Min(ignoreWhileCasting, 100)) / 100d;
        float seconds = PlayerHitRecovery.Duration(Get, PlayerActionFactor(player));
        if (seconds <= 0 || AreaRng(player).NextDouble() * 100 >= chance
            || player.Closed || player.PlayerLife == 0 || !player.HitRecovery.Begin(now, seconds))
        { Trace($"not accepted (chance {chance:0.###}%, duration {seconds:0.###} s)"); return null; }
        Trace($"accepted ({seconds:0.###} s, {Get(AntiquarianEffects.StunRecoverySource)}% Life over one second)");
        player.LastStunnedOrBlockedStunAt = now;
        player.RecentEvents.Record(StatConditions.BeenStunned, now);
        player.Antiquarian.Stunned(now, player.PlayerMaxLife, Get(AntiquarianEffects.StunRecoverySource));
        CancelMeleeRepeats(player, now);
        int action = Interlocked.Increment(ref player.ActionCount);
        bool wasCharging = IsShieldChargeActive(player);
        EndShieldChargeIfInterrupted(player);
        player.ChannelSkill = 0; player.NextAttackAt = now;
        player.LockstepActionBackswingUntil = 0;
        player.LockstepActionScheduledAt = 0;
        // A predictive client stops for the recovery and then goes on with its walk (live 2026-11-01: 30 cells apart when
        // this ended it). A lockstep one ends its walk in the frame the 0x1d1 reaches it (PlayerHitRecoveryRecordLocked).
        if (!player.Connection.LockstepClient)
        {
            if (wasCharging) player.Walk.Cancel(now);
            else player.Walk.Pause(now, seconds);
        }
        RecordIncomingHitTriggers(player, 0, now, stunned: true);
        return new(player.ProjectileEpoch, action, player.HitRecovery.Until, seconds);
    }

    public static Task SendPlayerHitRecovery(Player player,
        PlayerRecoveryIntent recovery) => SendWorldRecord(player, player.Id, [], "0x01d1 player TakeHit recovery", atGate: () =>
        {
            return PlayerHitRecoveryRecordLocked(player, recovery, AreaTime(player));
        });

    // The recovery's 0x1d1, written in its send gate (under conn.Monsters). The client's TakeHit handler (0x141e9c700) ends
    // the player's current action, and on a lockstep client nothing takes the walk up again: it stops after the steps of
    // the frame the message arrives in (the deferred stop of 0x141e38420) and stands, keeping its step's fraction, until
    // its next order (tick trace 2026-11-10: 14 frames, to the held Move that followed the recovery). So the walk ends
    // here, in the frame being sent, not when the hit was committed.
    public static byte[]? PlayerHitRecoveryRecordLocked(Player player, PlayerRecoveryIntent recovery, double now)
    {
        double remaining = recovery.Until - now;
        if (!player.ProjectileActive(recovery.Epoch) || player.PlayerLife == 0 || player.Paused
            || player.ActionCount != recovery.Action || remaining <= 0) return null;
        float seconds = (float)Math.Min(remaining, recovery.Seconds);
        if (player.Connection.LockstepClient)
        {
            EndShieldChargeIfInterrupted(player);
            player.Walk.Cancel(now);
            if (player.Connection.LockstepFrame is { } frame)
                player.LockstepBusyUntil = Math.Max(player.LockstepBusyUntil, LockstepRecoveryEnds(frame, seconds));
        }
        // The stun pins the player where the server has it: GGG sends the resync in the same tick as every stun.
        return [.. InstanceProtocol.ActorAnimation(player.Id, seconds, PlayerHitRecovery.TakeHitAnimation),
            .. ResyncPacketLocked(player, now)];
    }

    public static bool PlayerActionCurrent(Player player, int action)
    {
        return !player.Closed && !player.LeavingArea && !player.RageRetired && player.PlayerLife > 0
            && player.ActionCount == action && player.ElementalAilments.ActionFactor(AreaTime(player)) > 0 && !player.HitRecovery.Active(AreaTime(player));
    }

    // A hit's damage off a monster's life (in the area's frame): the new life, and whether the hit starts its boss stage
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
            long floor = Math.Min(m.Life, Math.Max(1, (long)m.MaxLife * threshold.LifePct / 100));
            long left = Math.Max(floor, (long)m.Life - damage);
            if (left * 100 <= (long)m.MaxLife * threshold.LifePct) { m.ThresholdReached = true; m.PresentationChanged?.Invoke(); }
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
    public static async Task RunBossStage(Player player, uint id, SpawnedMonster m)
    {
        var stage = m.Stage!;
        while (!m.StageActionStarted)
        {
            if (!MonsterPresent(player, id, m) || !m.StageRunning) return;
            await Send(player, [], null, atGate: () =>
            {
                if (!MonsterPresent(player, id, m) || !m.StageRunning || m.StageActionStarted || player.Paused) return null;
                double now = AreaTime(player);
                var at = m.Walk.Position(now);
                m.Walk.Order(at.X, at.Y, now);
                m.LastTarget = null;
                m.BusyUntil = now + stage.CastSeconds;
                m.StageEffectAt = now + stage.EffectSeconds(m.Variety is { } variety
                    ? SkillsFor(player).MonsterContact(variety.Id, "Throw", 0xffff) : null);
                m.StageActionStarted = true;
                Console.WriteLine($"[gateway] {player.Connection.Remote} => monster 0x{id:x} ({m.Label}) starts its stage at " +
                    $"{m.Life}/{m.MaxLife} life, game time {now:0.000}; effect at {m.StageEffectAt:0.000}, recovery at {m.BusyUntil:0.000}");
                return [.. InstanceProtocol.ActionStarted(id, at.X, at.Y, at.X, at.Y, StageSkillId, m.NextSequence(), player.MonsterOrderFlags), .. InstanceProtocol.ActionEnded(id)];
            });
            if (!m.StageActionStarted) await AreaWait(PlayerUpdateRole.MonsterAction, 16);
        }
        await CompleteMonsterStage(player, id, m);
    }

    // A boss whose variety has the big life bar (MonsterVarieties BossHealthBar) shows it at the top of the screen once it
    // aggroes: its StateMachine's boss_life_bar (Metadata/Monsters/BossLifeBar.ot) and aggro (Hillock's aggro effect) go to 1.
    public static async Task ShowBossLifeBar(uint id, SpawnedMonster m, Player player)
    {
        await SendWorldRecord(player, id, [],
            $"0x02bc monster 0x{id:x} ({m.Label}) StateMachine: boss_life_bar 1, aggro 1",
            atGate: () => BossLifeBarRecord(player, id, m));
        if (m.Variety?.Id == "Metadata/Monsters/Brute/BossBrute")
        {
            ushort line = (ushort)AreaRng(player).Next(898, 905); // BrutusIntroRandom..BrutusIntro7
            await Send(player, InstanceNpcs.TextAudio(id, 7, line),
                $"0x01c7 Brutus intro dialogue {line}");
        }
        else if (m.Variety?.Id == "Metadata/Monsters/seawitchillusion/BossMerveil")
        {
            int transition = m.Variety.ComponentsOrBase.ToList().IndexOf("Transitionable");
            if (transition >= 0)
                await SendWorldRecord(player, id,
                    InstanceEntities.TransitionableUpdate(id, (byte)transition, 2),
                    $"0x02bc Merveil 0x{id:x} fight intro Transitionable → 2");
        }
    }

    internal static byte[]? BossLifeBarRecord(Player player, uint id, SpawnedMonster m)
    {
        if (!MonsterPresent(player, id, m) || !m.BossBarShown || MonsterAsleep(player, id)
            || m.Variety is not { } v || m.States is not { } states) return null;
        int index = v.ComponentsOrBase.ToList().IndexOf("StateMachine");
        if (index < 0) return null;
        var next = states.Set("boss_life_bar", 1).Set("aggro", 1);
        if (v.Id == "Metadata/Monsters/Brute/BossBrute") next = next.Set("dialogue_lines", 1);
        m.States = next;
        return InstanceEntities.StateMachineUpdate(id, (byte)index, m.States);
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
    // A component writes the change at the frame's end (AreaLife, AreaStats); what it was is only logged.
    public static void Note(string? log) { if (log is not null) Console.WriteLine($"[gateway] {log}"); }

    /// <summary>The entity's stats now: the Stats component writes the changed ones at the frame's start (AreaStats).</summary>
    public static void SetStats(Player player, uint id, IEnumerable<(uint Key, int Value)> stats)
    {
        foreach (var (key, value) in stats) player.Area.Stats.Set(id, key, value);
    }

    public static (uint Key, ushort Hit, ushort Life) HitOrder(Player player, uint attacker) =>
        player.Connection.LockstepClient ? (attacker, HitCheckpoint, HitLifeCheckpoint) : default;

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
    // frame and its body lasts the death clip's ticks from there (Player.Corpse), so the body is kept at the kill
    // or at the death message, whichever comes first (KeepCorpseLocked), its clock starts where the death is written
    // (CorpseDeathsWritten), and the 0x2b2 follows in the frame after the client's own clear, where it changes nothing.
    // Differs from GGG: its 0x2b2 comes 4 or 5 frames after a 150 ms death, 0 after one without the event and 54 or 55
    // after Hillock's 1833 ms, by its own world's count of the clip; ours counts as the client does and sends it a frame
    // after that (5, 0 and 56 frames).
    public static void StopCorpseBlocking(Player player, uint id, SpawnedMonster m)
    {
        RememberCursedCorpse(player, id, m);
        bool kept = KeepCorpseLocked(player, id, m);
        if (!kept)
        {
            // Match the variety's native remove_collidability clip timing on predictive clients instead of delaying
            // varieties without one by a fixed 165 ms.
            double delay = m.Variety is { } v ? (Monsters.CorpseUnblockMs(v.Id) ?? 0) / 1000d : CorpseBlockDelaySeconds;
            if (delay <= 0) _ = CorpseCleared(player, id, m);
            else SendAfter(player, delay, () => CorpseCleared(player, id, m));
        }
    }

    // Keeps the killed monster's body on a lockstep client and starts what ends it: the wait for its death message's
    // frame and, a death clip later, the 0x2b2. Called by the kill and by the death message's own write, in whichever
    // order they come: the periodic view of the monsters can write the death a frame before the hit's own message, and
    // either can go out before the kill's bookkeeping gets here (live 2026-11-10: a corpse whose clock started with the
    // second message blocked a frame longer than the client's, and a monster standing against it took another way
    // round; two whose messages both went out first blocked for the guard's whole second). False when there is no body
    // to keep, or its death was written and its body is gone again. Under conn.Monsters.
    static bool KeepCorpseLocked(Player player, uint id, SpawnedMonster m)
    {
        if (player.CorpseWalks(id, m)) return true;
        int ticks = CorpseBodyTicks(m.Variety is { } variety ? Monsters.CorpseUnblockMs(variety.Id) : 150);
        if (!player.RetainCorpseBody(id, m, ticks) || player.CorpseDeath(id, m) is not { } death) return false;
        _ = AreaFrames.Start(async () =>
        {
            try
            {
                if (await Task.WhenAny(death, AreaDelay(player, CorpseDeathWaitSeconds)) != death)
                    if (player.Connection.LockstepFrame is { } frame && player.CorpseAwaitsDeathOf(id))
                    {
                        StepWalkers(player, frame.Start);
                        player.CorpseDied(id, frame);
                    }
                if (!death.IsCompletedSuccessfully) return;
                // The clear waits for the frame that starts at the death plus its clip's frames.
                for (double at = death.Result + ticks * (FrameMs / 1000d); AreaTime(player) < at - 1e-6 && !player.Closed && !player.LeavingArea;)
                    await AreaWait(PlayerUpdateRole.MonsterAction, FrameMs);
                await CorpseCleared(player, id, m);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
        return true;
    }

    static Task CorpseCleared(Player player, uint id, SpawnedMonster m) =>
        SendWorldRecord(player, id, [], GameServer.Core.Diagnostics.ServerTrace.Combat || GameServer.Core.Diagnostics.ServerTrace.Ai
            ? $"0x02b2 corpse of 0x{id:x} stops blocking" : null, atGate: () =>
        {
            using (ProjectileMutation(player, phase: "send"))
            {
                if (player.Closed || player.LeavingArea || !ReferenceEquals(player.Area.Monsters.GetValueOrDefault(id), m))
                {
                    player.ReleaseCorpseBody(id, m);
                    return null;
                }
                StepWalkers(player, ProjectileWorldTime(player));
                player.ReleaseCorpseBody(id, m);
                return InstanceProtocol.StopBlocking(id);
            }
        });

    // In a lockstep client's send gate, for the records about to be admitted to the frame being sent: a monster whose
    // life reaches 0 in one of them dies on the client in this frame (the client applies the 0x1b1 before the frame's
    // ticks), if this is the first such message it is written. Its corpse is kept here when the kill has not kept it
    // yet; its walk is stepped up to the frame and ends with the frame's steps, and its body's clock starts.
    public static void CorpseDeathsWritten(Player player, OutputBatch batch)
    {
        if (!batch.Records.Any(record => record.LifeWritten is not null)) return;
        if (player.Connection.LockstepFrame is not { } frame) return;
        foreach (var record in batch.Records)
        {
            if (record.LifeWritten is not { } life || record.Object is not { } target) continue;
            NoteMonsterLifeWrittenLocked(player, target.Id, life, frame);
        }
    }

    // AreaLife publishes monster 0x01b1 life updates directly into Area.Delta (bypassing SendWorldBatch): observe them
    // here as each lockstep client's frame ends so corpse blocking starts immediately instead of waiting out the 1 s
    // CorpseDeathWaitSeconds timeout.
    public static void CorpseDeathsWritten(Player player, IReadOnlyList<AreaDelta.Record> delta)
    {
        if (player.Connection.LockstepFrame is not { } frame) return;
        foreach (var record in delta)
        {
            if (!record.For(player) || record.Bytes.Length < 21) continue;
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(record.Bytes) != InstanceProtocol.LifeUpdateId) continue;
            if (record.Bytes[20] != AreaLife.LifePool) continue;
            uint life = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(record.Bytes.AsSpan(12));
            NoteMonsterLifeWrittenLocked(player, record.Entity, life, frame);
        }
    }

    static void NoteMonsterLifeWrittenLocked(Player player, uint id, uint life, (double Start, double End) frame)
    {
        if (!player.Area.Monsters.TryGetValue(id, out var m)) return;
        bool kept = life == 0 && !player.DeathWritten(id, m) && KeepCorpseLocked(player, id, m);
        player.NoteLifeWritten(id, m, life);
        if (!kept || !player.CorpseAwaitsDeathOf(id)) return;
        StepWalkers(player, frame.Start);
        player.CorpseDied(id, frame);
    }

    // A monster died (its life reached 0 from the player's attack or skill): the player gains its experience when near
    // enough and it drops its items around where it fell (ItemDrops). Its corpse stays where it fell for as long as the
    // player is in the area (it stays in Monsters as Dead: no AI, body cleared by StopCorpseBlocking); leaving does not save it.
    // A minion's kill (byMinion) is not the player's for his on-kill effects.
    public static async Task MonsterKilled(Player player, uint id, SpawnedMonster m,
        bool byMinion = false, bool byHit = true)
    {
        await MonsterKilledBy(player, id, m, playerKill: !byMinion, byHit);
        // The map overlay's "X monsters remain" (S→C 0x1a0): one fewer of the area's monsters is alive. Its corpse stays
        // in the roster, so the count is the living ones (MonsterRemaining.cs).
        await SendRemainingMonsters(player);
    }

    static async Task MonsterKilledBy(Player player,
        uint id, SpawnedMonster m, bool playerKill, bool byHit = true)
    {
        // Guarantee the mini life bar is hidden for every kill path (AreaStats deduplicates when already set at the hit/DoT site).
        SetStats(player, id, [(Stat.HideMiniLifeBar, 1)]);
        if (m.HasCurses)
            using (ProjectileMutation(player, advance: false))
                if (CurrentGameWork && !player.Closed && !player.LeavingArea
                    && player.Area.Monsters.GetValueOrDefault(id) == m)
                    TransferMarksOnDeathLocked(player, m, ProjectileWorldTime(player));
        TinctureKillBonus? tinctureKill;
        // The lethal hit froze this decision before its notifications could yield. Consume only that exact receipt.
        bool destroyCorpse;
        tinctureKill = playerKill && byHit ? TakeTinctureKill(player, m) : null;
        destroyCorpse = playerKill && byHit && TakeUnhingedHitKillLocked(player, m);
        QueueEssenceDeathSpawnLocked(player, id, m);
        if (playerKill) QueueMadnessExplosionLocked(player, id, m);
        StopCorpseBlocking(player, id, m);
        DetonateExplosiveFusesOnDeath(m);
        if (SpreadContagion(player, id, m) is { } contagion)
            await ShowContagionSpread(player, contagion.At, contagion.Radius, contagion.Ao);
        double now = AreaTime(player);
        (int X, int Y) at, playerAt;
        bool alive, blocked;
        at = m.Walk.Position(now); playerAt = player.Walk.Position(now); alive = player.PlayerLife > 0;
        var explosionEquipment = playerKill ? CaptureKillEquipment(player, m, at, now) : default;
        blocked = HardcoreProgressBlocked(player);
        await SendVaalSouls(player);
        // Every kill in a survivors run feeds the run's own experience, whoever landed it (survivors-mode.md).
        // A dead Hardcore character's kills (an ignite still burning) make no progress, as a projectile's (CommitProjectileKill).
        if (blocked || m.Variety is not { } variety || !m.Hostile) return;
        // A Mystic Fetish is a destructible arena object: breaking it interrupts the King's ritual and gives no
        // experience, loot or quest progress of its own.
        if (KingInMists.IsFetish(variety)) return;
        var killer = PlayerOf(player);
        var monster = ModMonster(m, at);
        if (playerKill)
        {
            WitchbaneOnKill(player, m, AreaTime(player));
            if (MapDeathMarkDuration(player, m) is > 0 and var markMs)
                await ApplyMapDeathMarkOnKill(player, markMs);
            await GameEvents.RunMonsterKilled(new(player, monster));
            if (OnslaughtOnKill(player)) await SendKillOnslaught(player);
            ChargesOnKill(player, m);
            RecoverOnKill(player, m, tinctureKill);
            GainPhasingOnKill(player);
            // ChargesOnKill recorded this accepted kill before the explosion samples its conditions.
            bool corpseConsumed, devoured;
            now = AreaTime(player);
            // Devour's corpse is taken before the tattoo's roll: one killing blow consumes a corpse once.
            devoured = ConsumeDevouredCorpseLocked(player, id, m, now);
            corpseConsumed = devoured || ConsumeCorpseOnKillLocked(player, id, m, now);
            bool explodedCorpse = await ExplodeOnKill(player, id, m, at, explosionEquipment);
            if (corpseConsumed && !explodedCorpse)
                await Game.SendWorldRemoval(player, id,
                    $"0x02bd corpse 0x{id:x} ({m.Label}) {(devoured ? "devoured by the supported skill" : "consumed by the killing blow")}");
        }
        // Soul Eater eats the souls of anything killed nearby, the player's kill or not.
        SoulEaterSoul(player, id, at);
        // Kin-death charge grants (Dread Primates / Undying Grapplers) & generic CastsOnDeath skills.
        MonsterKits.GrantKinDeathChargesLocked(player.Area, id, m, now);
        _ = AreaFrames.Start(() => MonsterKits.ExecuteDeathSkills(player, id, m, at));
        bool firstBossKill = HasUnsetKillQuestFlag(player, variety.Id);
        if (!firstBossKill && player.Instance is { } killInstance)
        {
            foreach (var peer in killInstance.Presence.PlayersExcept(player.Id))
            {
                if (peer is not { Closed: false, LeavingArea: false } other || !other.Connection.PlayerSent
                    || HardcoreProgressBlocked(other)) continue;
                if (HasUnsetKillQuestFlag(other, variety.Id)) { firstBossKill = true; break; }
            }
        }
        await SetQuestFlags(player, QuestFlagsOnKill(player, variety.Id));
        // The party's kill: every living player within the experience range takes their share
        // (Progression.PartyMultiplier), the killer included. The presence of a private area is the party (an area is
        // entered as the party's instance).
        var partyShares = PartyExperienceShares(player, at);
        ShareKill(player, id, m, variety, at, partyShares);
        await GameEvents.RunKill(new(player, m, variety.Id, at) { PlayerKill = playerKill });
        // A summoned monster whose skill says so gives neither experience nor items, and so does a boss's first stage
        // (BossMods: Malachai, Shakari and Piety's escape give no experience; their own rows say so).
        if (!MonsterGivesNoRewards(m) && alive && Dist(at, playerAt) <= ExperienceRange)
        {
            var (xp, gemXp) = ComputeKillExperience(player, m, variety, monster, PartyExperienceShareOf(partyShares, player));
            // The gems gain their share of the kill only (not a quest's level or /add_xp), at the character's cap too:
            // 10% of the kill's experience before the level penalty.
            if (gemXp > 0) await GainGemExperience(player, gemXp);
            if (xp > 0) await GainExperience(player, xp, $"{m.Rarity} {m.Label} 0x{id:x}");
        }
        // A quest kill that levels the character up (a1q1: Hillock → at least level 2), wherever the player stands.
        if (QuestsOf(player).LevelAfterKill(player.Layout.Id, variety.Id) is int level and > 1 && level <= CharacterStats.LevelStarts.Count)
        {
            uint needed = CharacterStats.LevelStarts[level - 1], have;
            have = player.Character.Experience;
            if (have < needed) await GainExperience(player, needed - have, $"quest: {m.Label} killed, level {level}");
        }
        if (!MonsterGivesNoRewards(m))
            await GainKillCharges(player, m.Rarity, $"{m.Rarity} {m.Label} 0x{id:x} killed", tinctureKill?.FlaskChargesPct ?? 0);
        await DropLoot(player, m, at, firstBossKill);
        await PublishUnhingedCorpseDestruction(player, id, m, destroyCorpse);
    }

    // The monster's items (ItemDrops.Roll) as WorldItem objects scattered around where it died, on walkable cells when the
    // area's walk map is known. Like dropped items they lie in the instance (InstanceStore).
    public static async Task DropLoot(Player player, SpawnedMonster m, (int X, int Y) at, bool firstBossKill = false)
    {
        var rng = AreaRng(player);
        var stacked = RollKillLoot(player, m, at, rng, firstBossKill);
        if (stacked.Count == 0) return;
        foreach (var drop in stacked)
        {
            var (x, y) = ScatterDropPosition(player, at, rng);
            var ground = new WorldItemObject(x, y, drop.Item);
            uint id = player.Instance.Ground.Add(ground, player);
            await SendGroundItem(player, id, ground,
                $"0x02bb create world item 0x{id:x} {drop.Type.Name} (item level {drop.Item.ItemLevel}) dropped by {m.Label} at ({x}, {y})", justDropped: true);
        }
    }
}
