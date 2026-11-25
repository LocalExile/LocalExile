using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

public static partial class Game
{
    // WandProjectile's own rig plays its impact (index 0, 500 ms) with the WandTrailImpact sound and wand_impact particles.
    public static readonly ProjectilePresentation OrdinaryWandPresentation = new()
    {
        ImpactClip = OrdinaryRangedAttack.WandImpactAnimation,
        ImpactLifetime = ProjectileRuntime.Time(OrdinaryRangedAttack.WandImpactSeconds),
    };

    // DefaultArrow's rig has no impact clip (Projectiles.dat gives it stuck and bounce art the client plays only for its own
    // arrows): the stopped arrow object stays in its target or wall for a second, then goes.
    public static readonly ProjectilePresentation OrdinaryArrowPresentation = new()
    {
        StayOnImpact = true,
        ImpactLifetime = ProjectileRuntime.Time(OrdinaryRangedAttack.ArrowStuckSeconds),
    };

    // An ordinary bow or wand shot on the shared runtime. The release waits for the weapon's contact (attack1_bow, or
    // the ACT-selected wand clip for this input's sequence), then freezes damage, chance and multiplier; conditional
    // critical rules read the live owner at impact, and target evasion and impales resolve at contact.
    public static Task ScheduleOrdinaryRanged(Player player,
        (int X, int Y) aim, double attackSeconds, int action, int epoch, string projectileId, ushort sequence,
        SkillCriticalRoll criticalRoll, double? swingAt = null)
    {
        var session = ProjectilesFor(player);
        session.Advance();
        // A lockstep client's shot leaves on its own frame tick (LockstepShotAt), counted from the start of the frame its
        // action began in: a walk-up's swing frame, else the frame now being sent, which the echo after this goes in. The
        // hit then lands with the client's release, and a hit recovery, freeze or death before it cancels both; the lead
        // is only for a predictive client's server object.
        bool lockstep = player.Connection.LockstepClient;
        double acceptedAt = swingAt ?? (lockstep ? LockstepFrameStart(player) : AreaTime(player));
        var (delay, release) = OrdinaryRangedContact(player, attackSeconds, projectileId, sequence, clientClock: lockstep);
        session.Schedule(ProjectileRuntime.Time(lockstep ? LockstepShotAt(acceptedAt, delay) : acceptedAt + AttackReleaseDelay(delay)),
            () => OrdinaryRangedReleaseCurrent(player, action, epoch)
                && OrdinaryRangedAttack.ProjectileId(WeaponProfileFor(player, activeSet: true)) == projectileId,
            () => CaptureOrdinaryRangedProjectile(player, session, aim, projectileId, sequence, criticalRoll, release));
        return Task.CompletedTask;
    }

    public static ProjectileUse CaptureOrdinaryRangedProjectile(Player player, ProjectileSession session,
        (int X, int Y) aim, string projectileId, ushort sequence, SkillCriticalRoll criticalRoll, ContactPoint? release)
    {
        // A delayed release reads current combat state once, independently of the input handler's earlier snapshot.
        using var snapshot = new PlayerStatScope(player, fresh: true);
        // The client draws the shot when the record it has carries this projectile's count, as it stands at the
        // release: a record of the other weapon (a swap whose skills have not gone yet) fires nothing there, so
        // the server sends the object.
        bool clientDrawn = player.Connection.ClientDrawnShot == projectileId;
        var character = CharacterPlayerStats(player);
        int Get(uint key) => character.GetValueOrDefault(key);
        int RawCritical(uint name) => IsCriticalProhibition(name) ? 0 : Get(name);
        int effect = SkillEffectId(player, SkillCatalog.FirstSkillId) is { } id ? SkillsFor(player).EffectRow(id) : -1;
        var weapon = WeaponProfileFor(player, activeSet: true);
        // Two wands alternate: this use's hand (AttackHands) fires its own bolt.
        var hand = DefaultAttackHand(player, sequence);
        var sourceWeapon = HandWeapon(player, hand);
        int handAccuracy = HandAccuracy(player, hand, character);
        int criticalChance = CriticalProhibited(player, attack: true)
            ? AttackBaseCriticalStrike.Chance(!weapon.Unarmed, HandWeaponCritical(player, hand), player.Pools.CriticalStrikeChancePct, RawCritical)
            : HandAttackCriticalChance(player, hand, character);
        var captured = OrdinaryRangedShot.Capture(sourceWeapon, character, SkillsFor(player).EffectStats(effect, 1), Stats,
            handAccuracy, criticalChance, player.Pools.CriticalStrikeMultiplier);
        bool wand = projectileId == OrdinaryRangedAttack.Wand;
        long additionalBlindedIncreased = PlayerHitDamage.VsBlindedDamageStat is var blindedDamageKey
            ? character.GetValueOrDefault(blindedDamageKey) : 0;
        var blindedCapture = additionalBlindedIncreased == 0 ? null
            : OrdinaryRangedShot.Capture(sourceWeapon, character, SkillsFor(player).EffectStats(effect, 1), Stats,
                handAccuracy, criticalChance, player.Pools.CriticalStrikeMultiplier,
                additionalBlindedIncreased);
        var raw = SupportedAttackDamage.Raw(sourceWeapon, Get, new(handAccuracy));
        var conversion = SupportedAttackDamage.WithWeaponGains(
            DamageStats.Conversion(character, Stats, attack: true), sourceWeapon, Get);
        var ailmentContext = new AilmentPanelStats.Context("main_hand", Spell: false) { WeaponStats = sourceWeapon.LocalSources };
        var ailmentRows = AilmentPanelStats.Compute(ailmentContext, raw, conversion, Get);
        var blindedRows = blindedCapture is null ? null
            : AilmentPanelStats.Compute(ailmentContext, raw, conversion, Get, additionalBlindedIncreased);
        var shot = new ProjectileOffense(captured.Hit, false, captured.Accuracy, captured.CriticalChance,
            captured.CriticalMultiplier, wand ? "ordinary wand" : "ordinary bow")
        {
            WeaponAttack = true,
            TinctureKill = TinctureKillBonusFor(player, character, hand),
            NoExtraCriticalDamage = CriticalHitsDealNoExtraDamage(character),
            TargetCritical = TargetCriticalBonuses.From(character, null),
            Spider = !HasSpiderDamage(Get) && !HasLowLifeDamage(Get) && !HasTypedTargetDamage(Get)
                && !HasContactDamage(Get) && !HasLocalTargetDamage(sourceWeapon.LocalSources)
                && Get(Stat.AttackDamagePlusPctVsMaimedEnemies) == 0 ? null
                : new(raw, conversion, new Dictionary<uint, int>(character),
                new Dictionary<uint, int>(character), true, false, false, true, [], new HashSet<int> { 0, 2 }, 1,
                WieldingWand: sourceWeapon.WieldingWand, Volatile: false,
                Local: sourceWeapon.LocalSources is null ? null : new Dictionary<uint, int>(sourceWeapon.LocalSources),
                Bow: !wand, WeaponIncreased: SupportedAttackDamage.WeaponIncrease(sourceWeapon, Get),
                WeaponTypedIncreases: WeaponTypeDamage.Of(sourceWeapon, Get))
            {
                Armed = WeaponAttributeDamage.Armed(sourceWeapon), OffHand = hand == AttackHand.Off,
            },
            SkillStats = SkillsFor(player).EffectStats(effect, 1).ToDictionary(s => s.Item1, s => s.Item2),
            VsBlindedCriticalChance = AttackCriticalChanceVsBlinded(player, character, release: true, hand: hand),
            VsGroundCriticalChance = AttackCriticalChanceVsGround(player, character, release: true, hand: hand),
            Penetration = captured.Penetration, ResistanceMods = captured.ResistanceMods, ImpaleChance = captured.ImpaleChance,
            ImpaleSettings = captured.ImpaleSettings, AccuracyProfile = captured.AccuracyProfile,
            Ignite = MonsterIgnites.Settings.From(ailmentRows, "main_hand", Get),
            Poison = MonsterPoisons.Settings.From(ailmentRows, "main_hand", Get, sourceWeapon.LocalSources,
                projectileAttack: true),
            Bleed = BleedSettings(ailmentRows, "main_hand", Get, sourceWeapon.LocalSources, melee: false, bow: !wand,
                projectileAttack: true),
            VsBlinded = blindedCapture is null || blindedRows is null ? null : new(blindedCapture.Hit, null,
                MonsterIgnites.Settings.From(blindedRows, "main_hand", Get),
                BleedSettings(blindedRows, "main_hand", Get, sourceWeapon.LocalSources, melee: false, bow: !wand,
                    projectileAttack: true),
                MonsterPoisons.Settings.From(blindedRows, "main_hand", Get, sourceWeapon.LocalSources,
                    projectileAttack: true)),
            MaimChance = Get(Stat.ProjectileAttacksChanceToMaimOnHitPctIfYouHaveBeastMinion),
            CriticalRoll = criticalRoll, Leech = captured.Leech, WeaponLocal = sourceWeapon.LocalSources,
            AccuracyMoreUnique = player.Pools.AccuracyMoreVsUniquePct, AccuracyMoreClose = player.Pools.AccuracyMoreInCloseRangePct,
            LocalStunThresholdReduction = sourceWeapon.LocalSources?.GetValueOrDefault(Stat.LocalStunThresholdReductionPlusPct) ?? 0,
            AlwaysHits = Get(Stat.GlobalAlwaysHit) != 0, FarShot = Get(Stat.PlayerFarShot) != 0,
            PointBlank = Get(Stat.KeystonePointBlank) != 0,
            ReturnMultiplier = ProjectileReturn.DamageMore(Get), Blind = BlindHitInput.Capture(character),
            // The object flies at the height the clip releases it (the bow's nock, the wand's tip).
            Presentation = (wand ? OrdinaryWandPresentation : OrdinaryArrowPresentation) with { LaunchHeight = release?.HeightSteps },
            // The target's blood and particles on contact, matched to this shot's action (0x1af, kind 3).
            HitPacket = true, ActionSequence = sequence, ClientPredicted = clientDrawn
        };
        var origin = ProjectileSession.Vector(player.Walk.Position(session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond));
        var target = ProjectileSession.Vector(aim);
        // 3.29.3c client spawn forward offset (0x141ba7840 -> 0x141ba7c30): 30 world units (~2.76 cells) for ordinary
        // bows/wands (projectile_uses_contact_position is 0 on PlayerMelee), clamped so a close aim is not behind origin.
        double ahead = NativeProjectileForwardCells(Get, release);
        if (ahead > 0)
            origin += (target - origin).Unit * Math.Min(ahead, (target - origin).Length / 2);
        var profile = OrdinaryRangedProfile(player, projectileId, weapon.Bow, Get);
        profile = clientDrawn ? ClientDrawnProfile(player, profile, Get, sequence, target - origin)
            : NativeVolley(profile, Get, sequence, target - origin, player.Id);
        return new(profile, new(player.Id, session.Player.ProjectileEpoch), new(player.Id, session.Player.ProjectileEpoch), 1,
            origin, target)
            { Payload = shot, ReturnAnchor = new(player.Id, session.Player.ProjectileEpoch) };
    }

    // The ordinary shot's flight as the server simulates it, and with it the count the client's record carries.
    static ProjectileProfile OrdinaryRangedProfile(Player player, string projectileId, bool bow, Func<uint, int> get)
    {
        var ranged = SkillsFor(player).Projectile(projectileId);
        double speed = ProjectileSpeed.CellsPerSecond(ranged?.Speed ?? 3260, get, _ => 0,
            Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED"));
        return GatewayProjectileDefaults.Resolve(ranged?.Id ?? projectileId, get, speed, attack: true, arrows: bow);
    }

    /// <summary>
    /// The default attack's record stats that let a lockstep client draw its own wand bolt or arrow: its release
    /// (<c>0x141ba7c30</c>) fires nothing without a count, which <c>0x141ba8b00</c> reads from
    /// virtual_total_number_of_arrows for a bow with skill_can_fire_arrows and from virtual_total_number_of_projectiles
    /// otherwise, and the flight resource (<c>0x141acc8d0</c>) is the bow's arrow or WandProjectile. The count is the one
    /// the server simulates; a bow's record also carries the arrow totals, as GGG's bow records do. Empty for melee and on
    /// a predictive client (its release is not traced: a predictive Burning Arrow with a count drew no arrow, 2026-10-31).
    /// </summary>
    public static IReadOnlyList<(uint, int)> OrdinaryRangedCountStats(Player player, IReadOnlyDictionary<uint, int> character)
    {
        var weapon = WeaponProfileFor(player, activeSet: true);
        if (!player.Connection.LockstepClient || OrdinaryRangedAttack.ProjectileId(weapon) is not { } projectileId) return [];
        int Get(uint key) => character.GetValueOrDefault(key);
        int n = OrdinaryRangedProfile(player, projectileId, weapon.Bow, Get).Count;
        return weapon.Bow
            ? [(Stat.TotalNumberOfProjectilesToFire, n), (Stat.VirtualTotalNumberOfProjectiles, n),
                (Stat.TotalNumberOfArrowsToFire, n), (Stat.VirtualTotalNumberOfArrows, n)]
            : [(Stat.TotalNumberOfProjectilesToFire, n), (Stat.VirtualTotalNumberOfProjectiles, n)];
    }

    // The default attack's record, with the ordinary shot's count on a lockstep client (OrdinaryRangedCountStats), which
    // then draws those shots itself: a release of that projectile creates no object from here on (ClientPredicted). The
    // record is built in the send gate that writes it (SendSkills, the charge and buff updates), so ClientDrawnShot is
    // the record the client has by the frame of a release, which reads it then.
    public static IReadOnlyList<GrantedSkill> PlayerDefaultSkills(Player player, IReadOnlyList<(uint, int)> stats,
        IReadOnlyDictionary<uint, int> character)
    {
        var counts = OrdinaryRangedCountStats(player, character);
        var keys = counts.Select(s => s.Item1).ToHashSet();
        var skills = SkillsFor(player).PlayerDefaults([.. stats.Where(s => !keys.Contains(s.Item1)), .. counts]);
        player.Connection.DefaultAttackRecordLockstep = player.Connection.LockstepClient;
        player.Connection.ClientDrawnShot = counts.Count > 0 && skills.Count > 0
            ? OrdinaryRangedAttack.ProjectileId(WeaponProfileFor(player, activeSet: true)) : null;
        return skills;
    }

    // The player's record goes out before the client has shown it runs lockstep (a lag byte or a request), so it has no
    // count; it goes again once it has, if a wand or bow is in hand. Beside the frames, which must not wait for it.
    public static void SendLockstepDefaultAttack(Player player)
    {
        if (!player.Connection.LockstepClient || player.Connection.DefaultAttackRecordLockstep || !player.Connection.PlayerSent) return;
        player.Connection.DefaultAttackRecordLockstep = true;
        if (OrdinaryRangedAttack.ProjectileId(WeaponProfileFor(player, activeSet: true)) is null) return;
        _ = AreaFrames.Start(async () =>
        {
            try { await SendSkills(player); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        });
    }

    // The start of the lockstep frame now being sent, which a player action echoed now begins in.
    static double LockstepFrameStart(Player player)
    {
        return player.Connection.LockstepFrame?.Start ?? AreaTime(player);
    }

    /// <summary>
    /// When a lockstep client's own shot leaves: on the frame tick in which its clip clock passes the release, the
    /// first whole number of frames (at least one) after the start of the frame its action began in. Live 2026-11-05,
    /// from the order's frame start: the wand's 0.367 s release on frame 12 (38 of 39), the bow's 0.268 s on frame 9
    /// (36 of 37), each within about 4 ms of the tick.
    /// </summary>
    public static double LockstepShotAt(double frameStart, double delay)
    {
        double frame = FrameMs / 1000d;
        return frameStart + Math.Max(1, Math.Ceiling(delay / frame - 1e-6)) * frame;
    }
}
