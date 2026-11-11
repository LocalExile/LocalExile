using System.Net.Sockets;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

static partial class Game
{
    // WandProjectile's own rig plays its impact (index 0, 500 ms) with the WandTrailImpact sound and wand_impact particles.
    internal static readonly ProjectilePresentation OrdinaryWandPresentation = new()
    {
        ImpactClip = OrdinaryRangedAttack.WandImpactAnimation,
        ImpactLifetime = ProjectileRuntime.Time(OrdinaryRangedAttack.WandImpactSeconds),
    };

    // DefaultArrow's rig has no impact clip (Projectiles.dat gives it stuck and bounce art the client plays only for its own
    // arrows): the stopped arrow object stays in its target or wall for a second, then goes.
    internal static readonly ProjectilePresentation OrdinaryArrowPresentation = new()
    {
        StayOnImpact = true,
        ImpactLifetime = ProjectileRuntime.Time(OrdinaryRangedAttack.ArrowStuckSeconds),
    };

    // An ordinary bow or wand shot on the shared runtime. The release waits for the weapon's contact (attack1_bow, or
    // the ACT-selected wand clip for this input's sequence), then freezes damage, chance and multiplier; conditional
    // critical rules read the live owner at impact, and target evasion and impales resolve at contact.
    internal static Task ScheduleOrdinaryRanged(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        (int X, int Y) aim, double attackSeconds, int action, int epoch, string projectileId, ushort sequence,
        SkillCriticalRoll criticalRoll, double? swingAt = null)
    {
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        session.Advance();
        // A lockstep client's shot leaves on its own frame tick (LockstepShotAt), counted from the start of the frame its
        // action began in: a walk-up's swing frame, else the frame now being sent, which the echo after this goes in. The
        // hit then lands with the client's release, and a hit recovery, freeze or death before it cancels both; the lead
        // is only for a predictive client's server object.
        bool lockstep = conn.LockstepClient;
        double acceptedAt = swingAt ?? (lockstep ? LockstepFrameStart(conn) : conn.Clock.Elapsed.TotalSeconds);
        var (delay, release) = OrdinaryRangedContact(conn, attackSeconds, projectileId, sequence, clientClock: lockstep);
        session.Schedule(ProjectileRuntime.Time(lockstep ? LockstepShotAt(acceptedAt, delay) : acceptedAt + AttackReleaseDelay(delay)),
            () => OrdinaryRangedReleaseCurrent(conn, action, epoch)
                && OrdinaryRangedAttack.ProjectileId(WeaponProfileFor(conn, activeSet: true)) == projectileId,
            () => CaptureOrdinaryRangedProjectile(conn, session, aim, projectileId, sequence, criticalRoll, release));
        return Task.CompletedTask;
    }

    internal static ProjectileUse CaptureOrdinaryRangedProjectile(ClientState conn, ProjectileSession session,
        (int X, int Y) aim, string projectileId, ushort sequence, SkillCriticalRoll criticalRoll, ContactPoint? release)
    {
        // The client draws the shot when the record it has carries this projectile's count, as it stands at the
        // release: a record of the other weapon (a swap whose skills have not gone yet) fires nothing there, so
        // the server sends the object.
        bool clientDrawn = conn.ClientDrawnShot == projectileId;
        var character = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
        int RawCritical(string name) => IsCriticalProhibition(name) ? 0 : Get(name);
        int effect = SkillEffectId(conn, SkillCatalog.FirstSkillId) is { } id ? SkillsFor(conn).EffectRow(id) : -1;
        var weapon = WeaponProfileFor(conn, activeSet: true);
        int criticalChance = CriticalProhibited(conn, attack: true)
            ? AttackBaseCriticalStrike.Chance(!weapon.Unarmed, weapon.Critical, conn.Pools.CriticalStrikeChancePct, RawCritical)
            : conn.Pools.AttackCriticalStrikeChance;
        var captured = OrdinaryRangedShot.Capture(WeaponFor(conn), character, SkillsFor(conn).EffectStats(effect, 1), Stats,
            PlayerAccuracy(conn, character), criticalChance, conn.Pools.CriticalStrikeMultiplier);
        bool wand = projectileId == OrdinaryRangedAttack.Wand;
        var ailmentRows = AilmentPanelStats.Compute(new("main_hand", Spell: false),
            SupportedAttackDamage.Raw(WeaponFor(conn), Get), DamageStats.Conversion(character, Stats, attack: true), Get);
        var shot = new ProjectileOffense(captured.Hit, false, captured.Accuracy, captured.CriticalChance,
            captured.CriticalMultiplier, wand ? "ordinary wand" : "ordinary bow")
        {
            SkillStats = SkillsFor(conn).EffectStats(effect, 1).ToDictionary(s => s.Item1, s => s.Item2),
            VsBlindedCriticalChance = AttackCriticalChanceVsBlinded(conn, character, release: true),
            VsProfaneCriticalChance = AttackCriticalChanceVsProfane(conn, character, release: true),
            Penetration = captured.Penetration, ResistanceMods = captured.ResistanceMods, ImpaleChance = captured.ImpaleChance,
            ImpaleSettings = captured.ImpaleSettings, AccuracyProfile = captured.AccuracyProfile,
            Ignite = MonsterIgnites.Settings.From(ailmentRows, "main_hand", Get),
            Poison = MonsterPoisons.Settings.From(ailmentRows, "main_hand", Get, WeaponFor(conn).LocalSources,
                projectileAttack: true),
            Bleed = BleedSettings(ailmentRows, "main_hand", Get, WeaponFor(conn).LocalSources, melee: false, bow: !wand,
                projectileAttack: true),
            MaimChance = Get("projectile_attacks_chance_to_maim_on_hit_%_if_you_have_beast_minion"),
            CriticalRoll = criticalRoll, Leech = captured.Leech,
            AccuracyMoreUnique = conn.Pools.AccuracyMoreVsUniquePct, AccuracyMoreClose = conn.Pools.AccuracyMoreInCloseRangePct,
            AlwaysHits = Get("global_always_hit") != 0, FarShot = Get("player_far_shot") != 0,
            PointBlank = Get("keystone_point_blank") != 0,
            ReturnMultiplier = ProjectileReturn.DamageMore(Get), Blind = BlindInputs(character),
            // The object flies at the height the clip releases it (the bow's nock, the wand's tip).
            Presentation = (wand ? OrdinaryWandPresentation : OrdinaryArrowPresentation) with { LaunchHeight = release?.HeightSteps },
            // The target's blood and particles on contact, matched to this shot's action (0x1af, kind 3).
            HitPacket = true, ActionSequence = sequence, ClientPredicted = clientDrawn
        };
        var origin = ProjectileSession.Vector(conn.Walk.Position(session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond));
        var target = ProjectileSession.Vector(aim);
        // The shot leaves from the clip's contact keyframe, in front of the body along the aim (the Ranger's bow
        // 5.5 cells, a wand's tip 7.5), else 1.5 cells ahead; never past half way to the aim, so a close target
        // is not behind the release. The lateral part of the keyframe is not applied (ContactPoint).
        double ahead = release is { ForwardCells: > 0 } point ? point.ForwardCells : OrdinaryRangedAttack.ReleaseAheadCells;
        origin += (target - origin).Unit * Math.Min(ahead, (target - origin).Length / 2);
        var profile = OrdinaryRangedProfile(conn, projectileId, weapon.Bow, Get);
        profile = clientDrawn ? ClientDrawnProfile(conn, profile, Get, sequence, target - origin) : NativeVolley(profile, Get);
        return new(profile, new(PlayerId, session.Conn.ProjectileEpoch), new(PlayerId, session.Conn.ProjectileEpoch), 1,
            origin, target)
            { Payload = shot, ReturnAnchor = new(PlayerId, session.Conn.ProjectileEpoch) };
    }

    // The ordinary shot's flight as the server simulates it, and with it the count the client's record carries.
    static ProjectileProfile OrdinaryRangedProfile(ClientState conn, string projectileId, bool bow, Func<string, int> get)
    {
        var ranged = SkillsFor(conn).Projectile(projectileId);
        double speed = ProjectileSpeed.CellsPerSecond(ranged?.Speed ?? 3260, get, _ => 0,
            Environment.GetEnvironmentVariable("Game_PROJECTILE_SPEED"));
        return GatewayProjectileDefaults.Resolve(ranged?.Id ?? projectileId, get, speed, attack: true, arrows: bow);
    }

    /// <summary>
    /// The default attack's record stats that let a lockstep client draw its own wand bolt or arrow: its release
    /// (<c>0x141ba51a0</c>) fires nothing without a count, which <c>0x141ba6070</c> reads from
    /// virtual_total_number_of_arrows for a bow with skill_can_fire_arrows and from virtual_total_number_of_projectiles
    /// otherwise, and the flight resource (<c>0x141ac9e70</c>) is the bow's arrow or WandProjectile. The count is the one
    /// the server simulates; a bow's record also carries the arrow totals, as GGG's bow records do. Empty for melee and on
    /// a predictive client (its release is not traced: a predictive Burning Arrow with a count drew no arrow, 2026-10-31).
    /// </summary>
    internal static IReadOnlyList<(uint, int)> OrdinaryRangedCountStats(ClientState conn, IReadOnlyDictionary<uint, int> character)
    {
        var weapon = WeaponProfileFor(conn, activeSet: true);
        if (!conn.LockstepClient || OrdinaryRangedAttack.ProjectileId(weapon) is not { } projectileId) return [];
        int Get(string name) => Stats.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
        int n = OrdinaryRangedProfile(conn, projectileId, weapon.Bow, Get).Count;
        return weapon.Bow
            ? [(StatKeys.TotalProjectilesToFire, n), (StatKeys.VirtualTotalProjectiles, n),
                (StatKeys.TotalArrowsToFire, n), (StatKeys.VirtualTotalArrows, n)]
            : [(StatKeys.TotalProjectilesToFire, n), (StatKeys.VirtualTotalProjectiles, n)];
    }

    // The default attack's record, with the ordinary shot's count on a lockstep client (OrdinaryRangedCountStats), which
    // then draws those shots itself: a release of that projectile creates no object from here on (ClientPredicted). The
    // record is built in the send gate that writes it (SendSkills, the charge and buff updates), so ClientDrawnShot is
    // the record the client has by the frame of a release, which reads it then.
    internal static IReadOnlyList<GrantedSkill> PlayerDefaultSkills(ClientState conn, IReadOnlyList<(uint, int)> stats,
        IReadOnlyDictionary<uint, int> character)
    {
        var counts = OrdinaryRangedCountStats(conn, character);
        var keys = counts.Select(s => s.Item1).ToHashSet();
        var skills = SkillsFor(conn).PlayerDefaults([.. stats.Where(s => !keys.Contains(s.Item1)), .. counts]);
        conn.DefaultAttackRecordLockstep = conn.LockstepClient;
        conn.ClientDrawnShot = counts.Count > 0 && skills.Count > 0
            ? OrdinaryRangedAttack.ProjectileId(WeaponProfileFor(conn, activeSet: true)) : null;
        return skills;
    }

    // The player's record goes out before the client has shown it runs lockstep (a lag byte or a request), so it has no
    // count; it goes again once it has, if a wand or bow is in hand. Beside the frames, which must not wait for it.
    internal static void SendLockstepDefaultAttack(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn)
    {
        if (!conn.LockstepClient || conn.DefaultAttackRecordLockstep || !conn.PlayerSent) return;
        conn.DefaultAttackRecordLockstep = true;
        if (OrdinaryRangedAttack.ProjectileId(WeaponProfileFor(conn, activeSet: true)) is null) return;
        _ = Task.Run(async () =>
        {
            try { await SendSkills(remote, stream, ciphers, conn); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        });
    }

    // The start of the lockstep frame now being sent, which a player action echoed now begins in.
    static double LockstepFrameStart(ClientState conn)
    {
        lock (conn.Monsters) return conn.LockstepFrame?.Start ?? conn.Clock.Elapsed.TotalSeconds;
    }

    /// <summary>
    /// When a lockstep client's own shot leaves: on the frame tick in which its clip clock passes the release, the
    /// first whole number of frames (at least one) after the start of the frame its action began in. Live 2026-11-05,
    /// from the order's frame start: the wand's 0.367 s release on frame 12 (38 of 39), the bow's 0.268 s on frame 9
    /// (36 of 37), each within about 4 ms of the tick.
    /// </summary>
    internal static double LockstepShotAt(double frameStart, double delay)
    {
        double frame = FrameMs / 1000d;
        return frameStart + Math.Max(1, Math.Ceiling(delay / frame - 1e-6)) * frame;
    }
}
