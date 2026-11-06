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
        double acceptedAt = swingAt ?? conn.Clock.Elapsed.TotalSeconds;
        // A shot the client draws itself (its record has the count) lands its hit on the client's own release; the lead
        // is only for an object the server sends.
        bool clientDrawn = conn.ClientDrawsOrdinaryShots;
        var (delay, release) = OrdinaryRangedContact(conn, attackSeconds, projectileId, sequence, clientDrawn);
        session.Schedule(ProjectileRuntime.Time(acceptedAt + (clientDrawn ? delay : AttackReleaseDelay(delay))),
            () => OrdinaryRangedReleaseCurrent(conn, action, epoch)
                && OrdinaryRangedAttack.ProjectileId(WeaponProfileFor(conn, activeSet: true)) == projectileId,
            () =>
            {
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
                { SkillStats = SkillsFor(conn).EffectStats(effect, 1).ToDictionary(s => s.Item1, s => s.Item2),
                    VsBlindedCriticalChance = AttackCriticalChanceVsBlinded(conn, character, release: true),
                    VsProfaneCriticalChance = AttackCriticalChanceVsProfane(conn, character, release: true),
                    Penetration = captured.Penetration, ResistanceMods = captured.ResistanceMods, ImpaleChance = captured.ImpaleChance,
                    ImpaleSettings = captured.ImpaleSettings, AccuracyProfile = captured.AccuracyProfile,
                    Ignite = MonsterIgnites.Settings.From(ailmentRows, "main_hand", Get),
                    Poison = MonsterPoisons.Settings.From(ailmentRows, "main_hand", Get, WeaponFor(conn).LocalSources),
                    Bleed = BleedSettings(ailmentRows, "main_hand", Get, WeaponFor(conn).LocalSources, melee: false, bow: !wand),
                    CriticalRoll = criticalRoll, Leech = captured.Leech,
                    AccuracyMoreUnique = conn.Pools.AccuracyMoreVsUniquePct, AccuracyMoreClose = conn.Pools.AccuracyMoreInCloseRangePct,
                    AlwaysHits = Get("global_always_hit") != 0, FarShot = Get("player_far_shot") != 0,
                    PointBlank = Get("keystone_point_blank") != 0,
                    ReturnMultiplier = ProjectileReturn.DamageMore(Get), Blind = BlindInputs(character),
                    // The object flies at the height the clip releases it (the bow's nock, the wand's tip).
                    Presentation = (wand ? OrdinaryWandPresentation : OrdinaryArrowPresentation) with { LaunchHeight = release?.HeightSteps },
                    // The target's blood and particles on contact, matched to this shot's action (0x1af, kind 3).
                    HitPacket = true, ActionSequence = sequence, ClientPredicted = clientDrawn };
                var profile = NativeVolley(OrdinaryRangedProfile(conn, projectileId, weapon.Bow, Get), Get);
                var origin = ProjectileSession.Vector(conn.Walk.Position(session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond));
                var target = ProjectileSession.Vector(aim);
                // The shot leaves from the clip's contact keyframe, in front of the body along the aim (the Ranger's bow
                // 5.5 cells, a wand's tip 7.5), else 1.5 cells ahead; never past half way to the aim, so a close target
                // is not behind the release. The lateral part of the keyframe is not applied (ContactPoint).
                double ahead = release is { ForwardCells: > 0 } point ? point.ForwardCells : OrdinaryRangedAttack.ReleaseAheadCells;
                origin += (target - origin).Unit * Math.Min(ahead, (target - origin).Length / 2);
                return new(profile, new(PlayerId, epoch), new(PlayerId, epoch), 1, origin, target)
                    { Payload = shot, ReturnAnchor = new(PlayerId, epoch) };
            });
        return Task.CompletedTask;
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
    // then draws those shots itself: the server's release creates no object for them from here on (ClientPredicted).
    internal static IReadOnlyList<GrantedSkill> PlayerDefaultSkills(ClientState conn, IReadOnlyList<(uint, int)> stats,
        IReadOnlyDictionary<uint, int> character)
    {
        var counts = OrdinaryRangedCountStats(conn, character);
        var keys = counts.Select(s => s.Item1).ToHashSet();
        var skills = SkillsFor(conn).PlayerDefaults([.. stats.Where(s => !keys.Contains(s.Item1)), .. counts]);
        conn.DefaultAttackRecordLockstep = conn.LockstepClient;
        conn.ClientDrawsOrdinaryShots = counts.Count > 0 && skills.Count > 0;
        return skills;
    }

    // The player's record goes out before the client has shown it runs lockstep (a lag byte or a request), so it has no
    // count; it goes again once it has, if a wand or bow is in hand.
    internal static async Task SendLockstepDefaultAttack(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn)
    {
        if (!conn.LockstepClient || conn.DefaultAttackRecordLockstep || !conn.PlayerSent) return;
        conn.DefaultAttackRecordLockstep = true;
        if (OrdinaryRangedAttack.ProjectileId(WeaponProfileFor(conn, activeSet: true)) is null) return;
        await SendSkills(remote, stream, ciphers, conn);
    }
}
