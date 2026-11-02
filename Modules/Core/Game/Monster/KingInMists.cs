using System.Net.Sockets;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Data.Terrain.Generation;
using static Game;

/// <summary>
/// The King in the Mists, Crux of Nothingness (WorldAreas AzmeriLeagueBoss, level 83). The arena's WorldAreas row names
/// the humanoid VoodooKingBoss2; the fight brings VoodooKingBoss3 (the treant) when the three Mystic Fetishes have been
/// activated. This controller owns both forms: the ordinary monster AI skips them so every action is serialized here.
/// <para>Data: MonsterVarieties 10150 (Boss2: 1800% life, 210% damage, 1.5 s attack, reach 15, boss bar) and the
/// VoodooKingBoss3 row (1050% life, 60% energy shield, 255% damage). At level 83 the map-boss scaling gives 45,614,731
/// life for the humanoid and about 26.6M for the treant; resistances are 50/50/50/30. The mechanics and the full skill
/// list are in Agents/docs/game/king-in-the-mists.md.</para>
/// </summary>
static class KingInMists
{
    public const string Boss2 = "Metadata/Monsters/LeagueAzmeri/VoodooKingBoss/VoodooKingBoss2";
    public const string Boss3 = "Metadata/Monsters/LeagueAzmeri/VoodooKingBoss/VoodooKingBoss3";
    const string PoisonRat = "Metadata/Monsters/LeagueAzmeri/VoodooKingBoss/Critters/PoisonRat";
    const string ExplosiveToad = "Metadata/Monsters/LeagueAzmeri/VoodooKingBoss/Critters/ExplosiveToad";
    const string DeathEffigy = "Metadata/Monsters/LeagueAzmeri/VoodooKingBoss/VoodooKingDeathEffigy";
    const string GuidingLight = "Metadata/Monsters/LeagueAzmeri/GuidingLight/GuidingLightDaemonMaze_";
    const string WerewolfMaze = "Metadata/Monsters/LeagueAzmeri/Werewolves/WerewolfMaze";
    const string MeleeSpiderMaze = "Metadata/Monsters/LeagueAzmeri/Spiders/MeleeSpiderMaze";
    internal const string MysticFetish = "Metadata/Monsters/LeagueAzmeri/VoodooKingBoss/VoodooKingBoss2RitualPillar";
    internal const string CentralShrine = "Metadata/Monsters/LeagueAzmeri/VoodooKingBoss/RitualPillar2";
    internal const string CentralShrineModel = "Metadata/Terrain/Doodads/Leagues/Azmeri/HugeRitualShrine01_01.ao";

    // The official fetish create blobs at the arena's mapboss point (162, 174): (232, 490), (368, 491) and (298, 606),
    // i.e. the offsets below, with their Positioned rotations 5.23599, 1.0472 and 3.14159 rad. The cells are blocked
    // on the walk map (the fetishes stand on the platforms), so they must not be snapped with Layout.Near. The central
    // HugeRitualShrine is at (162, 162).
    static readonly (int X, int Y)[] FetishOffsets = [(-68, -52), (68, -51), (-2, 64)];
    static readonly float[] FetishRotations = [5.23599f, 1.0472f, 3.14159f];
    // Official 0x4014 return points relative to the fetish centre: (222,484), (378,485) and (298,618) for the
    // fetishes at (232,490), (368,491) and (298,606). They sit on the outer platform edge, away from the arena centre.
    static readonly (int X, int Y)[] FetishStandOffsets = [(-10, -6), (10, -6), (0, 12)];

    // The humanoid can never be killed in phase 1: its life is floored at 50%, where the maze always starts.
    const int PhaseTwoLifePercent = 50;
    // Phase-1 fetish rituals are life-gated. A single hit may cross several thresholds at once; only the first one
    // starts a ritual, and the later crossed thresholds are skipped so the King does not chain-teleport through all
    // three fetishes. The actual trigger percentages still need a 0x1b1 life capture; these are the requested gates.
    static readonly int[] RitualLifePercents = [92, 73, 55];

    // The basic Move action (ActionTypes HASH16 & 0x3fff): the treant has MovementSpeed 18 and must close the distance
    // itself; the arena is large enough that a stationary boss looks stalled between its ranged casts.
    const ushort MoveSkill = 0x2909;
    // The humanoid's short repositioning actions. Official phase 1 uses them while the player is near: 0x4009 blinks
    // away (teleport_01), 0x400a-0x400d are the forward/backward/left/right move clips.
    const ushort BossTeleportFlee = 0x4009, BossDashForward = 0x400a, BossDashBackward = 0x400b,
        BossDashLeft = 0x400c, BossDashRight = 0x400d;
    // A native move_left/move_right/move_bwd clip moves the model ~24 cells (measured 2026-11-01: 23.8-24.4 cells on
    // every dash). The old 19 came from the rig's 208.7 world units converted with the wrong scale, so each settle
    // pulled the model ~5 cells back after the clip.
    const double DashCells = 24;

    // GrantedEffects rows (poedb / MonsterVarieties GrantedEffectsKeys). The default attack is row 420. The humanoid's
    // first action in the official capture is CTS1BossFightStart (row 6227), a 2.5 s intro during which it cannot be hit.
    const ushort BossStartSkill = 0x4000;
    const int BossStartEffect = 6227;
    // The official actor ids of the two forms' default attacks (MeleeAtAnimationSpeedUnique). They are not the
    // player/generic 0x0102: using that id made the client play an unarmed/absent clip and the King T-posed.
    internal const ushort Boss2AttackSkill = 0x4001;
    internal const ushort Boss3AttackSkill = 0x4000;
    // Restore the fight in steps. BasicOnly trims the actor's skill table to the default attack, CTS1BossFightStart and
    // the three basic attacks (no ritual, effigy, hex or phase transition); Game_KING_BASIC=0 uses the full table.
    // While BasicOnly runs, phase 1 makes only the default attack decision, so the attack and its clip can be verified
    // before the rest of the fight is restored. AiEnabled defaults on now; Game_KING_AI=0 keeps the boss standing still
    // after the intro for debugging.
    static readonly bool BasicOnly = Environment.GetEnvironmentVariable("Game_KING_BASIC") != "0";
    static readonly bool AiEnabled = Environment.GetEnvironmentVariable("Game_KING_AI") != "0";
    const int MeleeEffect = 420, VineSlamEffect = 7597, VineCascadeEffect = 7596, SpellProjectile2Effect = 3893,
        SpellProjectile3Effect = 3894, TeleportFleeEffect = 12420, SummonEffigyEffect = 8336, SummonDeathAuraEffigyEffect = 8342,
        BugBombEffect = 1898, BugRendEffect = 10510, BugRendSingleEffect = 10511, MazeEffect = 8341,
        BecomeTreantEffect = 8340, RitualEffect = 7568, ReturnFromEdgeEffect = 8343, HexNoMovementEffect = 8337,
        HexNoStandingEffect = 8338, PunishmentEffect = 12437, BugBombImpactEffect = 7608, SwarmPulseEffect = 10354,
        DashForwardEffect = 12422, DashBackwardEffect = 12423, DashLeftEffect = 12424, DashRightEffect = 12425;

    // The actor's skill ids are the official capture's 0x4000+ ids, not invented ids in the player's gem-skill range
    // (0x0103..0x0142): the client's skill manager resolves an id globally, so a King action that reused a gem id
    // (Vine Slam 0x0104 = Flame Dash, Haste 0x0105, ...) crashed the client at the action (0xc0000005, live
    // 2026-10-31). The default attack uses the official MeleeAtAnimationSpeedUnique ids below, not the player 0x0102.
    sealed record Kit(ushort Projectile, ushort Cascade, ushort Slam, ushort Effigy, ushort HexMove, ushort HexStand,
        ushort TeleportFlee, ushort Ritual, ushort Maze, ushort BecomeTreant, ushort ReturnFromEdge, ushort Punishment,
        ushort BugBomb, ushort BugRend, ushort BugRendSingle, ushort BugBombImpact, ushort SwarmPulse)
    {
        // Humanoid VoodooKingBoss2 (official ids 0x4001-0x4014; 0x4001 is its default attack).
        public static readonly Kit Humanoid = new(0x4002, 0x4003, 0x4004, 0x4005, 0x4006, 0x4007, 0x4009, 0x400e,
            0x400f, 0x4013, 0x4014, 0x4012, 0, 0, 0, 0, 0);
        // Treant VoodooKingBoss3 (its own 0x4000-0x400b table; the same numeric ids mean different skills).
        public static readonly Kit Treant = new(0x4001, 0, 0x4002, 0x4007, 0x4003, 0x4004, 0, 0, 0, 0, 0, 0x4006,
            0x4005, 0x4009, 0x400a, 0x4008, 0x400b);
    }

    // The fetish hazards are actions on the fetish actor, not on the King. The official fetish manager has ids
    // 0x4000-0x4003 for RatSwarm, CorruptedBloodDegen and SummonFrogSwamps (row 5766 is the CGE variant).
    internal const ushort FetishRatSkill = 0x4000, FetishBloodSkill = 0x4001, FetishSwampSkill = 0x4002,
        FetishBloodAuraSkill = 0x4003;
    static readonly int[] FetishEffects = [10351, 10352, 10353, 5766, 5767];

    // BuffDefinitions rows: Portent Vapors, Branching Affliction, the two ritual hexes, Mystic Miasma and the invisible
    // cannot_be_damaged buff that covers the humanoid's start channel.
    internal const ushort PortentVaporsRow = 2409, BranchingAfflictionRow = 2371, MysticMiasmaRow = 2401,
        CannotBeDamagedRow = 350;
    // Phase-1 hexes: Ritual of Meditation (0x4006, row 2358) and Ritual of Dance (0x4007, row 2359).
    internal const ushort HexNoMovementRow = 2358, HexNoStandingRow = 2359;
    // Unlike the invisible row 350, row 7 (god_mode / "Invulnerability") is visible and uses the client's
    // invulnerability.dds buff icon. Use it for the ritual so the immunity has a visual state on the boss.
    internal const ushort RitualImmunityRow = 7;

    public static bool IsBoss2(MonsterCatalog.Variety? variety) =>
        variety is not null && variety.Id.Equals(Boss2, StringComparison.Ordinal);

    public static bool IsBoss3(MonsterCatalog.Variety? variety) =>
        variety is not null && variety.Id.Equals(Boss3, StringComparison.Ordinal);

    public static bool IsBoss(MonsterCatalog.Variety? variety) => IsBoss2(variety) || IsBoss3(variety);

    /// <summary>A Mystic Fetish: a stationary shrine the fight spawns and activates, not an ordinary AI monster.</summary>
    public static bool IsFetish(MonsterCatalog.Variety? variety) =>
        variety is not null && variety.Id.Equals(MysticFetish, StringComparison.Ordinal);

    /// <summary>The maze's Guiding Light daemon: a moving visual guide with no ordinary AI.</summary>
    public static bool IsGuidingLight(MonsterCatalog.Variety? variety) =>
        variety is not null && variety.Id.Equals(GuidingLight, StringComparison.Ordinal);

    /// <summary>The King, his fetishes, effigies and critters, and the Guiding Lights: the fight plays their actions.</summary>
    public static bool IsActor(MonsterCatalog.Variety? variety) => IsGuidingLight(variety)
        || variety is not null && variety.Id.StartsWith("Metadata/Monsters/LeagueAzmeri/VoodooKingBoss/", StringComparison.Ordinal);

    /// <summary>The actor skill list for either form, built from the GrantedEffects rows the client must know to play the
    /// action's own animation. The ids are also the 0x1ab ids this controller sends.</summary>
    internal static IReadOnlyList<GrantedSkill>? SkillsFor(ClientState conn, MonsterCatalog.Variety variety, byte level,
        MonsterAttack attack)
    {
        if (!IsBoss(variety)) return null;
        var list = new List<GrantedSkill>();
        void Add(ushort id, int effect, MonsterAttack use)
        {
            var stats = use.Projectile is null
                ? use.SkillStats()
                : MonsterProjectileSkillStats(conn, effect, level, use);
            if (Game.SkillsFor(conn).FindByRow(id, effect, level, stats) is { } skill) list.Add(skill);
        }
        if (IsBoss2(variety))
        {
            var k = Kit.Humanoid;
            // The .ot's Actor block declares CTS1BossFightStart a leveled_starting_effect, and the official create blob
            // lists it first (id 0x4000) with the default attack second (0x4001). The client takes the first record as
            // the starting effect: it is what starts the humanoid in the .act's stance2 channel at spawn.
            Add(BossStartSkill, BossStartEffect, NoDamage(attack, BossStartSkill, 2.5));
            Add(Boss2AttackSkill, MeleeEffect, attack);
            // The actor needs these records even in the basic stage: the client's skill lookup resolves 0x4009 and
            // 0x400a-0x400d through the actor's granted list, and without them the teleport/dash packets move the object
            // with no clip (live 2026-11-01).
            Add(k.TeleportFlee, TeleportFleeEffect, NoDamage(attack, k.TeleportFlee, 1.33));
            Add(BossDashForward, DashForwardEffect, NoDamage(attack, BossDashForward, 0.5));
            Add(BossDashBackward, DashBackwardEffect, NoDamage(attack, BossDashBackward, 0.5));
            Add(BossDashLeft, DashLeftEffect, NoDamage(attack, BossDashLeft, 0.5));
            Add(BossDashRight, DashRightEffect, NoDamage(attack, BossDashRight, 0.5));
            Add(k.Slam, VineSlamEffect, Slam(attack, k.Slam));
            Add(k.Cascade, VineCascadeEffect, Cascade(attack, k.Cascade));
            Add(k.Projectile, SpellProjectile2Effect, Projectile2(attack, k.Projectile));
            if (!BasicOnly)
            {
                Add(k.Effigy, SummonEffigyEffect, Effigy(attack, k.Effigy));
                Add(k.Maze, MazeEffect, NoDamage(attack, k.Maze, 2.5));
                Add(k.BecomeTreant, BecomeTreantEffect, NoDamage(attack, k.BecomeTreant, 2.5));
                Add(k.Ritual, RitualEffect, NoDamage(attack, k.Ritual, 2.5));
                Add(k.ReturnFromEdge, ReturnFromEdgeEffect, NoDamage(attack, k.ReturnFromEdge, 1.5));
                Add(k.HexMove, HexNoMovementEffect, NoDamage(attack, k.HexMove, 1.5));
                Add(k.HexStand, HexNoStandingEffect, NoDamage(attack, k.HexStand, 1.5));
                Add(k.Punishment, PunishmentEffect, Punishment(attack, k.Punishment));
            }
        }
        else
        {
            var k = Kit.Treant;
            Add(Boss3AttackSkill, MeleeEffect, attack);
            Add(k.Slam, VineSlamEffect, Slam(attack, k.Slam));
            Add(k.Projectile, SpellProjectile3Effect, Projectile3(attack, k.Projectile));
            Add(k.BugBomb, BugBombEffect, BugBomb(attack, k.BugBomb));
            // The official 3.29.3b capture uses VoodooKingBugRend/BugRendSingle and BugSoulrend projectiles; the old
            // live PlayAnimation failure came from a stale Debug build, not from this skill.
            Add(k.BugRend, BugRendEffect, BugRend(attack, k.BugRend));
            Add(k.BugRendSingle, BugRendSingleEffect, BugRendSingle(attack, k.BugRendSingle));
            Add(k.Effigy, SummonDeathAuraEffigyEffect, Effigy(attack, k.Effigy));
            Add(k.HexMove, HexNoMovementEffect, NoDamage(attack, k.HexMove, 1.5));
            Add(k.HexStand, HexNoStandingEffect, NoDamage(attack, k.HexStand, 1.5));
            Add(k.Punishment, PunishmentEffect, Punishment(attack, k.Punishment));
            Add(k.BugBombImpact, BugBombImpactEffect, NoDamage(attack, k.BugBombImpact, 1.0));
            Add(k.SwarmPulse, SwarmPulseEffect, NoDamage(attack, k.SwarmPulse, 1.0));
        }
        return list;
    }

    /// <summary>The Mystic Fetish actor's full granted-effect list. The King controller sends the hazard actions to the
    /// fetish actor itself, as the official server does (RatSwarm, CorruptedBloodDegen, SummonFrogSwamps).</summary>
    internal static IReadOnlyList<GrantedSkill> FetishSkillsFor(ClientState conn, MonsterCatalog.Variety variety, byte level,
        MonsterAttack attack)
    {
        if (!IsFetish(variety)) return [];
        var list = new List<GrantedSkill>();
        for (int i = 0; i < FetishEffects.Length; i++)
        {
            ushort id = (ushort)(FetishRatSkill + i);
            // The fetish's granted effects are spells: they need their own cast-time stats, not the attack duration.
            var stats = (attack with { Spell = true }).SkillStats();
            if (Game.SkillsFor(conn).FindByRow(id, FetishEffects[i], level, stats) is { } skill) list.Add(skill);
        }
        return list;
    }

    public static void Begin(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id)
    {
        SpawnedMonster? m;
        lock (conn.Monsters) conn.Monsters.TryGetValue(id, out m);
        if (m is null || !IsBoss(m.Variety)) return;
        // A new form starts with no fetish stacks; the previous buff's own duration removes its icon.
        conn.KingPortentStacks = 0;
        conn.KingPortentUntil = double.NegativeInfinity;
        _ = new Fight(remote, stream, ciphers, conn, id, m).Run();
    }

    static MonsterAttack Slam(MonsterAttack attack, ushort skill) => With(attack, skill, 1.0, 22,
        ConvertPhysical(attack.Damage.Scale(1.9), 30));

    static MonsterAttack Cascade(MonsterAttack attack, ushort skill) => With(attack, skill, 1.0, 52,
        ConvertPhysical(attack.Damage.Scale(1.4), 25));

    static MonsterAttack Projectile2(MonsterAttack attack, ushort skill) => With(attack, skill, 1.0, 50,
        attack.Damage.Scale(0.4).Add(DamageType.Chaos, attack.Damage[DamageType.Physical].Scale(0.15)))
        with { Projectile = new SkillCatalog.MonsterProjectileInfo("Metadata/Projectiles/AzmeriVoodooKingSpellProj", 800, Lobbed: false, 1), Spell = true };

    static MonsterAttack Projectile3(MonsterAttack attack, ushort skill) => With(attack, skill, 1.0, 50,
        attack.Damage.Scale(0.45).Add(DamageType.Chaos, attack.Damage[DamageType.Physical].Scale(0.45)))
        with { Projectile = new SkillCatalog.MonsterProjectileInfo("Metadata/Projectiles/AzmeriVoodooKing3SpellProj", 800, Lobbed: false, 1), Spell = true };

    // The official fetish approach is VoodooKingTeleportFlee (0x4009), not the plain VoodooKingTeleport cast: it is the
    // movement skill whose own clip carries the model to the action's target coordinates.
    static MonsterAttack Effigy(MonsterAttack attack, ushort skill) => NoDamage(attack, skill, 1.0);

    static MonsterAttack BugBomb(MonsterAttack attack, ushort skill) => With(attack, skill, 1.0, 45,
        attack.Damage.Scale(0.55).Add(DamageType.Chaos, attack.Damage[DamageType.Physical].Scale(0.18)))
        with { Projectile = new SkillCatalog.MonsterProjectileInfo("Metadata/Projectiles/BugBomb", 400, Lobbed: true, 2) { AreaRadius = 9 }, Spell = true };

    // The official cast releases six BugSoulrend projectiles (Projectiles.dat: collision radius 3, speed 200).
    static MonsterAttack BugRend(MonsterAttack attack, ushort skill) => With(attack, skill, 1.0, 48,
        attack.Damage.Scale(0.8))
        with { Projectile = new SkillCatalog.MonsterProjectileInfo("Metadata/Projectiles/BugSoulrend", 200, Lobbed: false, 3) { Count = 6 }, Spell = true };

    static MonsterAttack BugRendSingle(MonsterAttack attack, ushort skill) => With(attack, skill, 1.0, 48,
        attack.Damage.Scale(0.8))
        with { Projectile = new SkillCatalog.MonsterProjectileInfo("Metadata/Projectiles/BugSoulrend", 200, Lobbed: false, 3), Spell = true };

    static MonsterAttack Punishment(MonsterAttack attack, ushort skill) => With(attack, skill, 1.0, 40,
        attack.Damage.Scale(0.9)) with { Spell = true };

    static MonsterAttack NoDamage(MonsterAttack attack, ushort skill, double seconds) =>
        With(attack, skill, seconds, 0, DamagePacket.Empty) with { Spell = true };

    static MonsterAttack With(MonsterAttack attack, ushort skill, double seconds, double range, DamagePacket damage) =>
        attack with { SkillId = skill, Seconds = seconds, Range = range, Damage = damage, Projectile = null, Release = null };

    static DamagePacket ConvertPhysical(DamagePacket packet, int percent)
    {
        var physical = packet[DamageType.Physical];
        var converted = physical.Scale(percent / 100.0);
        return packet.Select((type, range) => type == DamageType.Physical ? range.Scale(1 - percent / 100.0) : range)
            .Add(DamageType.Chaos, converted);
    }

    sealed class Fight(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster boss)
    {
        (int X, int Y) _origin;
        int _phase = IsBoss3(boss.Variety) ? 3 : 1;
        int _nextAltar;
        ushort? _portentBuff;
        readonly uint?[] _fetishes = new uint?[3];
        readonly ushort?[] _fetishBuffs = new ushort?[3];
        readonly bool[] _fetishUsed = new bool[3];
        uint? _centralShrine;
        StateMachineData? _centralShrineStates;
        double _nextSlam, _nextCascade, _nextProjectile, _nextEffigy, _nextBomb, _nextRend, _nextChase;
        double _nextPunishment, _graceUntil, _nextBlink, _nextStrafe, _nextApproach, _nextBackward, _nextStep;
        double _nextRitualAt, _nextHex;
        bool _strafeLeft;
        int _strafeChain;
        // Dashes since the last blink. The blink-away is allowed after two dashes, so it cannot chain blink -> strafe
        // -> instant blink, but it also is not starved by a ready strafe (live 2026-11-01).
        int _dashesSinceBlink;
        int _ritualIndex, _hexIndex;
        // Official phase-1 dialogue (Client.txt 2026-11-01 11:44-11:47, aligned with the trace's action starts). The
        // client picks a random bark per skill, so each pool rotates through the lines captured for that skill.
        static readonly string[] SlamBarks = ["Sprout!", "Invasive growth!", "Grow!"];
        static readonly string[] EffigyBarks = ["Agrik armach!", "Dark effigy!"];
        static readonly string[] RitualBarks = ["Bid the darkness!", "Nothingness approaches!"];
        int _slamBark, _effigyBark;
        ushort? _ritualBuff;
        bool _barShown, _mazeStarted;
        Kit K => _phase == 3 ? Kit.Treant : Kit.Humanoid;
        int LifePercent => (int)((long)boss.Life * 100 / Math.Max(1u, boss.MaxLife));
        ushort NextSeq() => conn.NextActionSequence();

        public async Task Run()
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            _origin = conn.KingOrigin ?? boss.Walk.Position(now);
            // A kept instance restores the monsters where the player left them, so a re-entered phase-1 fight would
            // start the intro on the King's old cell (live 2026-11-01 20:26: (301,587) instead of the mapboss point
            // (300,542)). Every start belongs at the arena centre, like a fresh create.
            var (startX, startY) = boss.Walk.Position(now);
            if (_phase == 1 && Distance(startX, startY, _origin.X, _origin.Y) > 4)
            {
                boss.Walk.Teleport(_origin.X, _origin.Y, now);
                await Program.Send(remote, stream, ciphers, InstanceProtocol.Teleport(id, _origin.X, _origin.Y),
                    $"0x01b7 King in the Mists starts at the arena centre ({_origin.X}, {_origin.Y})");
            }
            Console.WriteLine($"[gateway] {remote} King in the Mists 0x{id:x} controller: phase {_phase}, BasicOnly={BasicOnly}, AI={AiEnabled}");
            _nextSlam = now + 4;
            _nextCascade = now + 6;
            _nextProjectile = now + 3;
            _nextEffigy = now + 12;
            _nextBomb = now + 6;
            _nextRend = now + 10;
            _nextPunishment = now + 15;
            // The official humanoid's first phase-1 blink is about 2.3 s after the intro ends; the strafe cadence is
            // shorter once the player is in range.
            _nextBlink = now + 7.0;
            _nextStrafe = now + 8.0;
            _nextApproach = now + 6.5;
            _nextBackward = now + 4.0;
            _nextStep = now + 5.0;
            _nextRitualAt = now + 1.0;
            _nextHex = now + 20.0;
            // The official capture spawns the humanoid at 44.236 s, sends CTS1BossFightStart at 46.286 s (a 2 s delay so
            // the actor exists and is visible first) and is first attacked at 48.694 s. Keep it invulnerable through the
            // intro; the 2.5 s intro clip plus the 2 s settle is 4.5 s.
            _graceUntil = _phase == 3 ? now + 7 : double.PositiveInfinity;
            if (_phase == 1) boss.KingGrace = true;
            try
            {
                await FindOrSpawnFetishes();
                await SpawnCentralShrine();
                // The arena room objects (platform shield, descension object, RitualBossPortal) need their own component
                // layouts; the generic EffectObject blob aborted the client on entry (0xe0000001 in KERNELBASE, live
                // 2026-10-31), so they are not created until their types are ported.
                // The client creates the humanoid in the default stance, so the .act's stance2 channel (call_loop_01,
                // rig clip 3) is started explicitly. 0x1b2 kind 0 plays a clip on the actor's animation controller; the
                // client bounds-checks the index. The intro then plays its end (call_end_01, clip 2) over the 2.5 s cast.
                if (_phase == 1)
                {
                    // The client has no state flag for "introduced but not fighting yet", so the intro carries the
                    // invisible BuffDefinitions row 350 (base_cannot_be_damaged) for its whole clip. Team stays 1: the
                    // official create's team 0 turned the overhead bar green and the model vanished when it switched
                    // back (live 2026-11-01), so the client-side immunity is a buff instead.
                    ushort introInvulnerability = NextBuff();
                    await Program.Send(remote, stream, ciphers,
                        InstanceEntities.AddBuff(id, introInvulnerability, CannotBeDamagedRow, id),
                        $"0x01c1 King in the Mists 0x{id:x} intro immunity (BuffDefinitions row {CannotBeDamagedRow})");
                    // The mini life bar above the actor (the red unit bar, not the boss bar): hidden until the intro
                    // ends, because the client has no other flag for "spawned but not fighting yet".
                    await Program.Send(remote, stream, ciphers,
                        InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 1)]),
                        $"0x01b3 King in the Mists 0x{id:x} hides its overhead life bar during the intro");
                    await Program.Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(id, 0, 3, 1f),
                        $"0x01b2 King in the Mists 0x{id:x} channels (call_loop_01)");
                    // The .ot's CTS1BossFightStart bark (TheKingInTheMists_ProximityEncounterRandom): the official
                    // client shows it as the King's chat line at spawn.
                    await Program.Send(remote, stream, ciphers,
                        InstanceChat.ChatLine("Mharu an koriak!", "The King in the Mists"),
                        "0x0017 King in the Mists: Mharu an koriak!");
                    await Task.Delay(2000);
                    if (!Present()) return;
                    // The official 0x4000 uses flags 0x0800, header u16 0xD000 and no target object (its +0x20 target is
                    // the previous message's stale actor id). It is the only action in the capture with those fields.
                    var intro = Cast(BossStartSkill, "starts the fight", seconds: 2.5, flags: 0x0800, header: 0xD000);
                    await Task.Delay(50);
                    await Program.Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(id, 0, 2, 0.4f),
                        $"0x01b2 King in the Mists 0x{id:x} ends the channel (call_end_01)");
                    // Wait the whole clip even when a hit cancels the client-side action: the intro owns the next 2.5 s
                    // and the life bar stays closed until it ends.
                    await Task.WhenAll(intro, Task.Delay(2500));
                    if (!Present()) return;
                    await Say("Protect the ritual!");
                    await Program.Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(id, introInvulnerability),
                        $"0x01c2 King in the Mists 0x{id:x} intro immunity ends");
                    await Program.Send(remote, stream, ciphers,
                        InstanceEntities.StatsUpdate(id, [(StatKeys.HideMiniLifeBar, 0)]),
                        $"0x01b3 King in the Mists 0x{id:x} shows its overhead life bar");
                    // The intro is over: damage now counts and the boss bar may open.
                    _graceUntil = conn.Clock.Elapsed.TotalSeconds;
                    boss.KingGrace = false;
                }
                while (true)
                {
                    await Task.Delay(50);
                    if (!Present()) return;
                    if (conn.Paused) continue;
                    now = conn.Clock.Elapsed.TotalSeconds;
                    if (conn.KingPortentStacks > 0 && now >= conn.KingPortentUntil)
                    {
                        conn.KingPortentStacks = 0;
                        if (_portentBuff is { } expired)
                        {
                            _portentBuff = null;
                            await Program.Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(Program.PlayerId, expired),
                                "0x01c2 player Portent Vapors expires");
                        }
                    }
                    var (mx, my) = boss.Walk.Position(now);
                    var (px, py) = conn.Walk.Position(now);
                    double distance = Distance(mx, my, px, py);
                    if (!boss.Aggro)
                    {
                        if (distance > boss.AggroRange) continue;
                        lock (conn.Monsters) boss.Aggro = true;
                    }
                    if (!_barShown && now >= _graceUntil)
                    {
                        _barShown = true;
                        await ShowBossLifeBar(remote, stream, ciphers, id, boss, conn);
                    }
                    if (_phase == 1 && now < _graceUntil) continue;
                    if (_phase == 1 && boss.KingGrace) boss.KingGrace = false;
                    if (_phase == 3)
                    {
                        // The treant's emergence grace: no action and no damage until it ends.
                        if (now < _graceUntil) continue;
                        if (boss.KingGrace) boss.KingGrace = false;
                    }
                    // The start is being rebuilt step by step: after the intro the actor stays alive and damageable but
                    // makes no AI decision (no attack, teleport, ritual, effigy or phase transition).
                    if (!AiEnabled) continue;
                    if (now < boss.FlinchUntil || now < boss.BusyUntil || boss.StageRunning) continue;
                    if (_phase == 3)
                    {
                        await PhaseThree(now, distance, px, py);
                        continue;
                    }
                    if (!BasicOnly && !_mazeStarted && LifePercent <= PhaseTwoLifePercent)
                    {
                        // The humanoid cannot be killed in phase 1: TakeDamageLocked floors it at 50%, and this check
                        // hands the fight to the maze as soon as that floor is reached, regardless of how many
                        // fetish thresholds a single hit crossed.
                        _mazeStarted = true;
                        Console.WriteLine($"[gateway] {remote} King in the Mists 0x{id:x} phase 1 ends at {LifePercent}% life: maze");
                        await RunMaze(now);
                        return;
                    }
                    await PhaseOne(now, distance, px, py);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // The connection or area went away.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[gateway] {remote} King in the Mists 0x{id:x} failed: {ServerLog.Describe(ex)}");
            }
        }

        enum MoveSync { None, Glide }
        // The official phase-1 movement: TeleportFlee sends the destination first, then the current position, and the
        // client plays teleport_01; the three dashes send the player first, then the current position, and play
        // move_left/move_right/move_bwd. The native clip moves the model; a trailing packet only syncs the server.
        // `place` computes the landing cell from the player position at the send gate: the player can cross 10+ cells
        // during the 120 ms action-end settle, and a target computed from the older loop position made the clip and the
        // settle point in different directions (live 2026-11-01: a 40-cell backward dash correction).
        async Task MoveWithSkill(ushort skill, string label, int fromX, int fromY, Func<int, int, (int X, int Y)> place,
            double actionSeconds, bool playerFirst, MoveSync sync, uint? target = null, ushort header = 0x7001)
        {
            await ClearActionBeforeAnimation($"0x01ac King in the Mists ends the previous action before it {label}");
            if (!Present()) return;
            double startedAt = conn.Clock.Elapsed.TotalSeconds;
            var (playerX, playerY) = conn.Walk.Position(startedAt);
            var (toX, toY) = place(playerX, playerY);
            if (playerFirst)
            {
                // The native move clips displace the model relative to its facing. A cast leaves the facing wherever the
                // previous action put it, which made a strafe veer ~30 degrees from the server's perpendicular target
                // (live 2026-11-01); face the player explicitly before the clip.
                await Program.Send(remote, stream, ciphers,
                    InstanceProtocol.Orientation(id, Facing(fromX, fromY, playerX, playerY), locked: false),
                    $"0x01b9 King in the Mists faces the player before it {label}");
            }
            int action;
            lock (conn.Monsters)
            {
                action = ++boss.ActionGeneration;
                boss.BusyUntil = Math.Max(boss.BusyUntil, startedAt + actionSeconds + 0.5);
                boss.Walk.Order(fromX, fromY, startedAt);
            }
            if (!await SendMonsterAction(remote, stream, ciphers, conn, id, boss, action, () =>
                playerFirst
                    ? InstanceProtocol.ActionStarted(id, playerX, playerY, fromX, fromY, skill, NextSeq(), 0,
                        targetObjectId: target, header: header)
                    : InstanceProtocol.ActionStarted(id, toX, toY, fromX, fromY, skill, NextSeq(), 0,
                        targetObjectId: target, header: header),
                $"0x01ab King in the Mists {label}")) return;
            await Task.Delay(TimeSpan.FromSeconds(actionSeconds));
            if (!Present()) return;
            if (sync == MoveSync.None)
            {
                // The clip's MoveEventType moves the client model, but its exact landing can drift from the server
                // target after repeated strafes. End the clip action first, then settle the client on the server cell
                // with a fast 0x1ba glide; without this the next teleport can look like a 2-3 cell jump because the two
                // positions disagree.
                bool settleFlinching;
                lock (conn.Monsters) settleFlinching = conn.Clock.Elapsed.TotalSeconds < boss.FlinchUntil;
                if (!settleFlinching)
                    await Program.Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(id),
                        $"0x01ac King in the Mists {label} ends before settle");
                await Task.Delay(80);
                if (!Present()) return;
                double nativeAt = conn.Clock.Elapsed.TotalSeconds;
                lock (conn.Monsters) boss.Walk.Teleport(toX, toY, nativeAt);
                await Program.Send(remote, stream, ciphers, InstanceProtocol.MoveTo(id, toX, toY, 0.1f, InstanceProtocol.FollowTerrain),
                    $"0x01ba King in the Mists {label} settle at ({toX}, {toY})");
                await Task.Delay(120);
                Console.WriteLine($"[gateway] {remote} King in the Mists {label}: native move to ({toX}, {toY})");
                return;
            }
            // 0x1b7 on a monster plays the client's own teleport effect (the fire column) on top of teleport_01.
            // 0x1ba glides the object to the same cell without that effect; the speed is high enough to be a snap.
            bool flinching;
            lock (conn.Monsters) flinching = conn.Clock.Elapsed.TotalSeconds < boss.FlinchUntil;
            if (!flinching)
                await Program.Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(id),
                    $"0x01ac King in the Mists {label} ends");
            double movedAt = conn.Clock.Elapsed.TotalSeconds;
            lock (conn.Monsters) boss.Walk.Teleport(toX, toY, movedAt);
            await Program.Send(remote, stream, ciphers, InstanceProtocol.MoveTo(id, toX, toY, 0.1f, InstanceProtocol.FollowTerrain),
                $"0x01ba King in the Mists {label} to ({toX}, {toY})");
        }

        // The client ignores a new 0x1b2 animation (and its 0x1b7 teleport) while the actor still has the previous
        // action. End it first and give the client one frame to settle, otherwise the model is moved by the packet
        // without any blink/teleport clip.
        async Task ClearActionBeforeAnimation(string label)
        {
            bool flinching;
            lock (conn.Monsters) flinching = conn.Clock.Elapsed.TotalSeconds < boss.FlinchUntil;
            if (flinching) return;
            await Program.Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(id), label);
            // The client's clip can still be finishing when the action ends; a longer gap keeps the next native move from
            // starting late and being cut by the settle.
            await Task.Delay(220);
        }

        async Task BlinkAway(double now, int px, int py)
        {
            var (mx, my) = boss.Walk.Position(now);
            double dx = mx - px, dy = my - py;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1) { dx = 0; dy = 1; length = 1; }
            // A point is usable only inside the room and not on a blocked cell. Near() can snap far away when the
            // wanted point lies on a platform, so the distance from the wanted point is checked as well.
            bool Sane(int x, int y) => x >= 0 && y >= 0
                && (conn.Layout.GridW <= 0 || x < conn.Layout.GridW)
                && (conn.Layout.GridH <= 0 || y < conn.Layout.GridH)
                && Distance(_origin.X, _origin.Y, x, y) <= 115
                && conn.Layout.WalkableFor(x, y, 0) && !conn.Barrier.Contains((x, y));
            bool Usable(int x, int y, int wantX, int wantY) => Sane(x, y)
                && Distance(mx, my, x, y) >= 10 && Distance(x, y, wantX, wantY) <= 18
                // An away-blink must not land on or next to the player, even after a centre fallback.
                && Distance(px, py, x, y) >= 25;

            int wantX = (int)Math.Round(px + dx / length * 52);
            int wantY = (int)Math.Round(py + dy / length * 52);
            var (tx, ty) = conn.Layout.Near((wantX, wantY));
            if (!Usable(tx, ty, wantX, wantY))
            {
                wantX = (int)Math.Round(px - dx / length * 52);
                wantY = (int)Math.Round(py - dy / length * 52);
                (tx, ty) = conn.Layout.Near((wantX, wantY));
            }
            bool centerFallback = false;
            if (!Usable(tx, ty, wantX, wantY))
            {
                // Platforms around the edge can leave no usable orbit. Fall back to a point relative to the arena
                // centre on the side away from the player; after the blink a nearby player triggers a strafe.
                centerFallback = true;
                wantX = _origin.X + (int)Math.Round(dx / length * 35);
                wantY = _origin.Y + (int)Math.Round(dy / length * 35);
                (tx, ty) = conn.Layout.Near((wantX, wantY));
                if (!Sane(tx, ty) || Distance(mx, my, tx, ty) < 10 || Distance(px, py, tx, ty) < 25)
                {
                    _nextBlink = conn.Clock.Elapsed.TotalSeconds + 1.0;
                    return;
                }
            }
            _strafeChain = 0;
            _dashesSinceBlink = 0;
            await ClearActionBeforeAnimation("0x01ac King in the Mists ends the previous action before the blink");
            if (!Present()) return;
            // Play only the actor's teleport_01 clip (0x1b2 kind 0), not the 0x4009 skill action: the action also runs
            // the skill's ActiveSkill teleport effect, which is the extra fire column. The clip index 35 is
            // teleport_01 (1.333 s); the cell update then follows with a fast glide.
            double startedAt = conn.Clock.Elapsed.TotalSeconds;
            lock (conn.Monsters)
            {
                ++boss.ActionGeneration;
                boss.BusyUntil = Math.Max(boss.BusyUntil, startedAt + 1.8);
                boss.Walk.Order(mx, my, startedAt);
            }
            await Program.Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(id, 0, 35, 1f),
                $"0x01b2 King in the Mists blinks away (teleport_01)");
            // teleport_01 is two-stage: it burrows at the origin, then the flee_target/contact event at ~0.70 s is the
            // split point, and the dig-out plays at the destination. Move the object there between the stages; moving
            // at the end made the client play both stages at the old cell and only then jump.
            await Task.Delay(TimeSpan.FromSeconds(0.70));
            if (!Present()) return;
            double blinkAt = conn.Clock.Elapsed.TotalSeconds;
            lock (conn.Monsters) boss.Walk.Teleport(tx, ty, blinkAt);
            await Program.Send(remote, stream, ciphers, InstanceProtocol.Teleport(id, tx, ty),
                $"0x01b7 King in the Mists blink to ({tx}, {ty})");
            await Task.Delay(TimeSpan.FromSeconds(0.63));
            if (!Present()) return;
            // Lock the cell a second time after the clip. The first 0x1b7 can move the rendered model while the
            // client's actor/path position still points at the old cell; a following strafe then starts from there
            // and lands on the player. This final same-cell update commits the new actor position.
            double settleAt = conn.Clock.Elapsed.TotalSeconds;
            lock (conn.Monsters) boss.Walk.Teleport(tx, ty, settleAt);
            await Program.Send(remote, stream, ciphers, InstanceProtocol.Teleport(id, tx, ty),
                $"0x01b7 King in the Mists blink settle at ({tx}, {ty})");
            _nextBlink = conn.Clock.Elapsed.TotalSeconds + 3.0;
            // A centre-fallback can land close to the player: go straight into the strafe phase from there.
            if (centerFallback && Distance(px, py, tx, ty) <= 38)
                _nextStrafe = conn.Clock.Elapsed.TotalSeconds;
            else
                _nextStrafe = Math.Max(_nextStrafe, conn.Clock.Elapsed.TotalSeconds + 1.8);
        }

        async Task Strafe(double now, int px, int py, double distance)
        {
            var (mx, my) = boss.Walk.Position(now);
            bool left = _strafeLeft;
            _strafeLeft = !left;
            // The client's move_left clip moves toward (dy, -dx) and move_right toward (-dy, dx), where (dx, dy) is the
            // player-to-King vector (mx - px, my - py): the two clips are the opposite of the first sign convention
            // tried live on 2026-11-01, when every strafe played to one side and the settle dragged the model ~40 cells
            // back to the other. The direction is recomputed at the send gate from the player cell the action carries,
            // so a player crossing several cells during the action-end settle cannot skew the landing.
            // The official phase 1 often pairs a left and a right dash back-to-back (24.94/25.43 s, 75.64/76.13 s).
            // Chain exactly two: the second ends the chain and restores the normal cadence.
            bool chain = _strafeChain == 0;
            _strafeChain = chain ? 1 : 0;
            await MoveWithSkill(left ? BossDashLeft : BossDashRight, left ? "strafes left" : "strafes right",
                mx, my, (playerX, playerY) =>
                {
                    double dx = mx - playerX, dy = my - playerY;
                    double length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                    double nx = left ? dy / length : -dy / length;
                    double ny = left ? -dx / length : dx / length;
                    int tx = (int)Math.Round(mx + nx * DashCells), ty = (int)Math.Round(my + ny * DashCells);
                    if (!conn.Layout.WalkableFor(tx, ty, 0) || conn.Barrier.Contains((tx, ty)))
                        (tx, ty) = conn.Layout.Near((tx, ty));
                    return (tx, ty);
                }, actionSeconds: 0.65, playerFirst: true, sync: MoveSync.None, target: Program.PlayerId);
            ++_dashesSinceBlink;
            if (chain)
            {
                // The first dash's 0.5 s clip is over; allow the paired dash immediately instead of waiting the
                // normal 2.2 s cooldown. MoveWithSkill's post-clip padding is cleared for this one case.
                _nextStrafe = conn.Clock.Elapsed.TotalSeconds;
                lock (conn.Monsters) boss.BusyUntil = conn.Clock.Elapsed.TotalSeconds;
            }
            else
                _nextStrafe = conn.Clock.Elapsed.TotalSeconds + 2.2;
            // A strafe can close the distance to the player; do not chain it straight into a blink. The dash is the
            // escape action for this beat, the blink only becomes available again after the strafe settles.
            _nextBlink = Math.Max(_nextBlink, conn.Clock.Elapsed.TotalSeconds + 2.5);
        }

        async Task ApproachBlink(double now, int px, int py, double distance)
        {
            var (mx, my) = boss.Walk.Position(now);
            double length = Math.Max(1, distance);
            double ux = (px - mx) / length, uy = (py - my) / length;
            // Stop ~22 cells before the player, inside the boss's ranged attack band, not on top of them.
            int wantX = (int)Math.Round(px - ux * 24);
            int wantY = (int)Math.Round(py - uy * 24);
            // Cap a single approach jump: from 50+ cells away the old target teleported the model 34 cells at once.
            // Two shorter approaches read as movement rather than one sharp teleport.
            double wantDistance = Distance(mx, my, wantX, wantY);
            if (wantDistance > 24)
            {
                double f = 24 / wantDistance;
                wantX = (int)Math.Round(mx + (wantX - mx) * f);
                wantY = (int)Math.Round(my + (wantY - my) * f);
            }
            var (tx, ty) = conn.Layout.Near((wantX, wantY));
            bool sane = tx >= 0 && ty >= 0
                && (conn.Layout.GridW <= 0 || tx < conn.Layout.GridW)
                && (conn.Layout.GridH <= 0 || ty < conn.Layout.GridH)
                && Distance(_origin.X, _origin.Y, tx, ty) <= 115
                && conn.Layout.WalkableFor(tx, ty, 0) && !conn.Barrier.Contains((tx, ty))
                && Distance(mx, my, tx, ty) >= 18
                // Do not land inside the player: Layout.Near can pull the 24-cell approach point down to a
                // platform edge only ~12 cells from the player, which reads as a teleport onto them.
                && Distance(px, py, tx, ty) >= 18;
            if (!sane)
            {
                _nextApproach = conn.Clock.Elapsed.TotalSeconds + 1.0;
                return;
            }
            await ClearActionBeforeAnimation("0x01ac King in the Mists ends the previous action before the approach blink");
            if (!Present()) return;
            _dashesSinceBlink = 0;
            double startedAt = conn.Clock.Elapsed.TotalSeconds;
            lock (conn.Monsters)
            {
                ++boss.ActionGeneration;
                boss.BusyUntil = Math.Max(boss.BusyUntil, startedAt + 1.8);
                boss.Walk.Order(mx, my, startedAt);
            }
            await Program.Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(id, 0, 35, 1f),
                "0x01b2 King in the Mists approach blink (teleport_01)");
            await Task.Delay(TimeSpan.FromSeconds(0.70));
            if (!Present()) return;
            double blinkAt = conn.Clock.Elapsed.TotalSeconds;
            lock (conn.Monsters) boss.Walk.Teleport(tx, ty, blinkAt);
            await Program.Send(remote, stream, ciphers, InstanceProtocol.Teleport(id, tx, ty),
                $"0x01b7 King in the Mists approach blink to ({tx}, {ty})");
            await Task.Delay(TimeSpan.FromSeconds(0.63));
            if (!Present()) return;
            double settleAt = conn.Clock.Elapsed.TotalSeconds;
            lock (conn.Monsters) boss.Walk.Teleport(tx, ty, settleAt);
            await Program.Send(remote, stream, ciphers, InstanceProtocol.Teleport(id, tx, ty),
                $"0x01b7 King in the Mists approach settle at ({tx}, {ty})");
            _nextApproach = conn.Clock.Elapsed.TotalSeconds + 2.0;
            _nextBlink = Math.Max(_nextBlink, conn.Clock.Elapsed.TotalSeconds + 1.5);
        }

        async Task DashBackward(double now, int px, int py, double distance)
        {
            var (mx, my) = boss.Walk.Position(now);
            await MoveWithSkill(BossDashBackward, "dashes backward", mx, my,
                (playerX, playerY) =>
                {
                    double length = Math.Max(1, Math.Sqrt((mx - playerX) * (mx - playerX) + (my - playerY) * (my - playerY)));
                    int tx = (int)Math.Round(mx + (mx - playerX) / length * DashCells);
                    int ty = (int)Math.Round(my + (my - playerY) / length * DashCells);
                    if (!conn.Layout.WalkableFor(tx, ty, 0) || conn.Barrier.Contains((tx, ty)))
                        (tx, ty) = conn.Layout.Near((tx, ty));
                    return (tx, ty);
                },
                actionSeconds: 0.65, playerFirst: true, sync: MoveSync.None, target: Program.PlayerId);
            ++_dashesSinceBlink;
            _nextBackward = conn.Clock.Elapsed.TotalSeconds + 2.5;
            // Same as a strafe: the backward dash is the escape action; let it settle before another blink.
            _nextBlink = Math.Max(_nextBlink, conn.Clock.Elapsed.TotalSeconds + 2.0);
        }

        async Task StepToward(double now, int px, int py, double distance)
        {
            var (mx, my) = boss.Walk.Position(now);
            double length = Math.Max(1, distance);
            // Keep a ~20-cell working distance: step in when farther, step out when too close.
            int wantX = (int)Math.Round(px + (mx - px) / length * 20);
            int wantY = (int)Math.Round(py + (my - py) / length * 20);
            var (tx, ty) = conn.Layout.Near((wantX, wantY));
            if (Distance(mx, my, tx, ty) < 3) { _nextStep = conn.Clock.Elapsed.TotalSeconds + 1.5; return; }
            double startedAt = conn.Clock.Elapsed.TotalSeconds;
            // 0x2909 is the official move order, but the client does not path this immobile humanoid with it: the
            // server's forced teleport at the end left the model ~20 cells behind, and the next settle or teleport
            // dragged it across the arena (live 2026-11-01, `KingInMists` step at 20:12:52). Move both sides with one
            // 0x1ba glide instead; the server commits the target when the glide ends.
            const double seconds = 0.45;
            lock (conn.Monsters)
            {
                ++boss.ActionGeneration;
                boss.BusyUntil = Math.Max(boss.BusyUntil, startedAt + seconds + 0.3);
            }
            await Program.Send(remote, stream, ciphers,
                InstanceProtocol.MoveTo(id, tx, ty, (float)seconds, InstanceProtocol.FollowTerrain),
                $"0x01ba King in the Mists steps toward ({tx}, {ty}) in {seconds:0.00} s");
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            if (!Present()) return;
            double at = conn.Clock.Elapsed.TotalSeconds;
            lock (conn.Monsters) boss.Walk.Teleport(tx, ty, at);
            _nextStep = conn.Clock.Elapsed.TotalSeconds + 1.8;
        }

        // The humanoid has no unarmed strike: when the player closes in it blinks away, and at medium range it strafes
        // left/right. Both actions play the official rig clips and move the object after the clip. Beyond ~42 cells it
        // closes with the approach blink. The movement runs in both modes; BasicOnly only stops the later spell and
        // ritual decisions.
        // A dash is only useful if its landing cell is walkable and actually moves the King; a corner otherwise makes
        // him repeat a strafe into the wall. Blink is the fallback when neither dash is usable.
        bool BackwardPoint(double now, int px, int py, out int tx, out int ty)
        {
            var (mx, my) = boss.Walk.Position(now);
            double dx = mx - px, dy = my - py;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1) { dx = 0; dy = 1; length = 1; }
            tx = (int)Math.Round(mx + dx / length * DashCells);
            ty = (int)Math.Round(my + dy / length * DashCells);
            if (!conn.Layout.WalkableFor(tx, ty, 0) || conn.Barrier.Contains((tx, ty)))
                (tx, ty) = conn.Layout.Near((tx, ty));
            return conn.Layout.WalkableFor(tx, ty, 0) && !conn.Barrier.Contains((tx, ty))
                && Distance(mx, my, tx, ty) >= 10;
        }

        bool StrafePoint(double now, int px, int py, out int tx, out int ty)
        {
            var (mx, my) = boss.Walk.Position(now);
            double dx = mx - px, dy = my - py;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1) { dx = 0; dy = 1; length = 1; }
            double nx = _strafeLeft ? dy / length : -dy / length;
            double ny = _strafeLeft ? -dx / length : dx / length;
            tx = (int)Math.Round(mx + nx * DashCells);
            ty = (int)Math.Round(my + ny * DashCells);
            if (!conn.Layout.WalkableFor(tx, ty, 0) || conn.Barrier.Contains((tx, ty)))
                (tx, ty) = conn.Layout.Near((tx, ty));
            return conn.Layout.WalkableFor(tx, ty, 0) && !conn.Barrier.Contains((tx, ty))
                && Distance(mx, my, tx, ty) >= 10;
        }

        async Task<bool> TryMove(double now, double distance, int px, int py)
        {
            if (distance > 42 && now >= _nextApproach)
            {
                await ApproachBlink(now, px, py, distance);
                return true;
            }
            // Escape priority: backward dash at melee range, then the blink-away, then a strafe. The blink was the last
            // resort behind a ready strafe, so the King strafed forever and never teleported away or cast (live
            // 2026-11-01). Two dashes since the previous blink keep the "dash -> blink" escape without a
            // blink -> strafe -> instant blink chain.
            if (distance <= 22 && now >= _nextBackward && BackwardPoint(now, px, py, out _, out _))
            {
                await DashBackward(now, px, py, distance);
                return true;
            }
            if (distance <= 42 && now >= _nextBlink && _dashesSinceBlink >= 2)
            {
                await BlinkAway(now, px, py);
                return true;
            }
            if (distance <= 42 && now >= _nextStrafe && StrafePoint(now, px, py, out _, out _))
            {
                await Strafe(now, px, py, distance);
                return true;
            }
            // 0x2909 is the one ordinary move order in the capture (10.49 s, interrupted by the next cast). Use it as
            // a low-priority step to hold ~20 cells when the other movement actions are cooling down.
            if (distance <= 42 && now >= _nextStep)
            {
                await StepToward(now, px, py, distance);
                return true;
            }
            return false;
        }

        // 0x4006/0x4007 Ritual of Meditation/Dance. The raw 0x1ab action and the fetish-style channel crash
        // StartChannelling, so play the boss's own hex_01 clip and apply the player's debuff row directly. The debuff's
        // 8 s duration is carried by 0x1c1; the imprisonment/maim rule itself is not modelled yet.
        async Task Hex(double now, int px, int py)
        {
            bool move = _hexIndex++ % 2 == 0;
            var (mx, my) = boss.Walk.Position(now);
            lock (conn.Monsters) boss.BusyUntil = Math.Max(boss.BusyUntil, now + 1.8);
            await Program.Send(remote, stream, ciphers,
                InstanceProtocol.Orientation(id, Facing(mx, my, px, py), locked: false),
                $"0x01b9 King in the Mists faces the player for the {(move ? "Meditation" : "Dance")} hex");
            await Program.Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(id, 0, 11, 1f),
                "0x01b2 King in the Mists hex_01");
            // Official hex barks: 0x4007 (no standing allowed) is "Keep running!", 0x4006 "You are seen!".
            await Say(move ? "You are seen!" : "Keep running!");
            await Task.Delay(700);
            if (!Present()) return;
            ushort buff = NextBuff();
            ushort row = move ? HexNoMovementRow : HexNoStandingRow;
            await Program.Send(remote, stream, ciphers,
                InstanceEntities.AddBuff(Program.PlayerId, buff, row, id,
                    u68: BitConverter.SingleToUInt32Bits(8f)),
                $"0x01c1 player {(move ? "Ritual of Meditation" : "Ritual of Dance")} (BuffDefinitions row {row})");
            Console.WriteLine($"[gateway] {remote} King in the Mists 0x{id:x} phase 1: {(move ? "Ritual of Meditation" : "Ritual of Dance")} hex");
            _nextHex = conn.Clock.Elapsed.TotalSeconds + 25.0;
        }

        async Task PhaseOne(double now, double distance, int px, int py)
        {
            // The spell projectile is the official phase-1 filler (22 casts in the 2026-10-31 capture), so it goes
            // before the movement decisions: with movement first, a ready strafe or step took nearly every decision
            // and the King never cast (live 2026-11-01).
            if (!BasicOnly && distance > 15 && now >= _nextProjectile)
            {
                _nextProjectile = now + 4;
                await Use(Projectile2(boss.Attack!, K.Projectile), area: false, radius: 0, "spell projectile");
                return;
            }
            if (await TryMove(now, distance, px, py)) return;
            if (BasicOnly) return;
            // Life-gated fetish rituals. A hit that crosses several thresholds starts only the first eligible ritual;
            // TeleportToAltar then moves _ritualIndex past every threshold the same hit already crossed.
            if (_ritualIndex < RitualLifePercents.Length && LifePercent <= RitualLifePercents[_ritualIndex]
                && LifePercent > PhaseTwoLifePercent && now >= _nextRitualAt)
            {
                await TeleportToAltar(now, _ritualIndex);
                return;
            }
            if (distance <= 24 && now >= _nextSlam)
            {
                _nextSlam = now + 15;
                await Say(SlamBarks[_slamBark++ % SlamBarks.Length]);
                await Use(Slam(boss.Attack!, K.Slam), area: true, radius: 22, "Vine Slam");
                return;
            }
            if (distance <= 52 && now >= _nextCascade)
            {
                _nextCascade = now + 10;
                await Say("Thorngrip!");
                await Use(Cascade(boss.Attack!, K.Cascade), area: true, radius: 18, "Vine Cascade");
                return;
            }
            // The humanoid is immobile (MovementSpeed 0); its effigy is its only stationary summon in phase 1.
            if (!BasicOnly && now >= _nextEffigy)
            {
                _nextEffigy = now + 25;
                await Say(EffigyBarks[_effigyBark++ % EffigyBarks.Length]);
                await Cast(K.Effigy, "summons an effigy");
                await Summon(DeathEffigy, 1);
                return;
            }
            if (now >= _nextHex)
            {
                await Hex(now, px, py);
                return;
            }
            if (distance <= 15)
            {
                await Use(boss.Attack!, area: false, radius: 0, "default attack");
                return;
            }
        }

        async Task PhaseThree(double now, double distance, int px, int py)
        {
            if (distance <= 22 && now >= _nextSlam)
            {
                _nextSlam = now + 15;
                await Use(Slam(boss.Attack!, K.Slam), area: true, radius: 22, "Treant Slam");
                return;
            }
            if (distance <= 50 && now >= _nextBomb)
            {
                _nextBomb = now + 20;
                await Use(BugBomb(boss.Attack!, K.BugBomb), area: false, radius: 0, "Bug Bomb");
                return;
            }
            // Bugrend fires from the central ritual altar, not from the boss, so distance does not gate it.
            if (now >= _nextRend)
            {
                _nextRend = now + 10;
                await Use(BugRend(boss.Attack!, K.BugRend), area: false, radius: 0, "Bugrend");
                return;
            }
            if (now >= _nextEffigy)
            {
                _nextEffigy = now + 20;
                await Cast(K.Effigy, "summons an effigy");
                await Summon(DeathEffigy, 1);
                return;
            }
            if (now >= _nextPunishment)
            {
                _nextPunishment = now + 20;
                await Use(Punishment(boss.Attack!, K.Punishment), area: false, radius: 0, "VoodooKingPunishment");
                return;
            }
            if (distance <= 16)
            {
                await Use(boss.Attack!, area: false, radius: 0, "default attack");
                return;
            }
            if (now >= _nextProjectile)
            {
                _nextProjectile = now + 4;
                await Use(Projectile3(boss.Attack!, K.Projectile), area: false, radius: 0, "spell projectile");
                return;
            }
            // The treant is slow but not stationary (MovementSpeed 18): it walks after a player that kites it instead of
            // standing at the altar between casts.
            if (distance > 16) await Chase(now, px, py, distance);
        }

        async Task Say(string text) =>
            await Program.Send(remote, stream, ciphers,
                InstanceChat.ChatLine(text, "The King in the Mists"), $"0x0017 King in the Mists: {text}");

        // A skill that only plays an animation (the effigy summon): no contact and no damage, unlike MonsterSwing.
        async Task Cast(ushort skill, string label, uint? target = null, double seconds = 1.5,
            ushort flags = 0x0408, ushort header = 0)
        {
            double startedAt = conn.Clock.Elapsed.TotalSeconds;
            int action;
            lock (conn.Monsters)
            {
                action = ++boss.ActionGeneration;
                // Leave a settle gap after the clip: back-to-back casts kept the client's actor action state busy while a
                // new action arrived, which is the state the channel crash came from (live 2026-10-31).
                boss.BusyUntil = Math.Max(boss.BusyUntil, startedAt + seconds + 0.5);
            }
            Console.WriteLine($"[gateway] {remote} King in the Mists 0x{id:x} phase {_phase}: {label}");
            await SendMonsterAction(remote, stream, ciphers, conn, id, boss, action,
                () =>
                {
                    double at = conn.Clock.Elapsed.TotalSeconds;
                    var here = boss.Walk.Position(at);
                    boss.Walk.Order(here.X, here.Y, at);   // a cast stops the chase
                    return InstanceProtocol.ActionStarted(id, here.X, here.Y, here.X, here.Y, skill,
                        NextSeq(), flags, targetObjectId: target, header: header);
                },
                $"0x01ab King in the Mists {label}");
        }

        // The treant walks to a stop point a short distance from the player, so its slams and default attacks can follow.
        // The order is refreshed when the player has moved away from the previous target or the walk ended.
        async Task Chase(double now, int px, int py, double distance)
        {
            if (now < _nextChase) return;
            _nextChase = now + 0.7;
            var (mx, my) = boss.Walk.Position(now);
            const double stop = 12;
            double f = Math.Clamp((distance - stop) / distance, 0, 1);
            var (tx, ty) = ((int)Math.Round(px + (mx - px) * f), (int)Math.Round(py + (my - py) * f));
            if (!conn.Layout.WalkableFor(tx, ty, 0) || conn.Barrier.Contains((tx, ty))) (tx, ty) = (px, py);
            if (boss.LastTarget is { } last && Distance(tx, ty, last.X, last.Y) < 2 && now < boss.Walk.ArrivesAt) return;
            boss.LastTarget = (tx, ty);
            boss.SwingGeneration = null;
            int action;
            lock (conn.Monsters) action = ++boss.ActionGeneration;
            bool sent = await SendMonsterAction(remote, stream, ciphers, conn, id, boss, action,
                () =>
                {
                    // Order only when the action actually reaches the gate: a flinch between the decision and the send
                    // invalidates it, and the server walk must not advance under a cancelled move.
                    double sentAt = conn.Clock.Elapsed.TotalSeconds;
                    var here = boss.Walk.Position(sentAt);
                    boss.Walk.Order(tx, ty, sentAt);
                    return InstanceProtocol.ActionStarted(id, here.X, here.Y, tx, ty, MoveSkill,
                        NextSeq(), 0x0408);
                },
                $"0x01ab King in the Mists moves toward the player at ({tx}, {ty})");
        }

        async Task Use(MonsterAttack attack, bool area, double radius, string label)
        {
            int action;
            double startedAt = conn.Clock.Elapsed.TotalSeconds;
            lock (conn.Monsters)
            {
                var at = boss.Walk.Position(startedAt);
                boss.Walk.Order(at.X, at.Y, startedAt);   // a cast stops the chase
                action = ++boss.ActionGeneration;
                // The action's own duration owns the next decision even when a hit reaction cancels the contact wait: a
                // failed/aborted use must not turn into a 20 Hz retry loop.
                boss.BusyUntil = Math.Max(boss.BusyUntil, startedAt + attack.Seconds + 0.5);
            }
            Console.WriteLine($"[gateway] {remote} King in the Mists 0x{id:x} phase {_phase}: {label}");
            var aim = conn.Walk.Position(startedAt);
            await MonsterSwing(remote, stream, ciphers, conn, id, boss, action, attack, area, radius, NextSeq());
            // The spell projectile's cast animation steps the model toward the action target until it is ~18 cells away
            // (the client's action range): every captured cast ended ~12 cells toward the player, and the next dash then
            // started from a cell the server did not share, so its settle pulled the model up to 55 cells (live
            // 2026-11-01). Follow the same landing on the server; no packet is needed, the model is already there.
            if (attack.Projectile is not null)
            {
                double at = conn.Clock.Elapsed.TotalSeconds;
                var (bx, by) = boss.Walk.Position(at);
                double d = Distance(bx, by, aim.X, aim.Y);
                if (d > 18.5)
                {
                    double f = 18.0 / d;
                    int lx = (int)Math.Round(aim.X + (bx - aim.X) * f);
                    int ly = (int)Math.Round(aim.Y + (by - aim.Y) * f);
                    lock (conn.Monsters) boss.Walk.Teleport(lx, ly, at);
                    Console.WriteLine($"[gateway] {remote} King in the Mists follows the cast step to ({lx}, {ly})");
                }
            }
        }

        // The official channel lasts about 4-6.5 s (0x400e until 0x4014 in the captures). The client-side immunity is
        // the same invisible cannot_be_damaged buff as the intro; RitualGrace stops damage, ailments, leech and hit
        // reaction on the server for the same window.
        async Task BeginRitualImmunity()
        {
            if (boss.RitualGrace) return;
            boss.RitualGrace = true;
            ushort buff = NextBuff();
            _ritualBuff = buff;
            await Program.Send(remote, stream, ciphers,
                InstanceEntities.AddBuff(id, buff, RitualImmunityRow, id),
                $"0x01c1 King in the Mists 0x{id:x} ritual immunity (BuffDefinitions row {RitualImmunityRow}, invulnerability icon)");
        }

        async Task EndRitualImmunity()
        {
            boss.RitualGrace = false;
            if (_ritualBuff is not { } buff) return;
            _ritualBuff = null;
            if (!Present()) return;
            await Program.Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(id, buff),
                $"0x01c2 King in the Mists 0x{id:x} ritual immunity ends");
        }

        // The official phase-1 loop is: teleport to a fetish, channel GSVoodooKingRitual into it, then
        // EASVoodooKingReturnFromEdge back to the centre. The teleport and return are actions with their own one-second
        // clips; the action is ended before 0x1b7 so the client, which ignores a teleport during the action, moves the
        // model with the server. Only the fetish's destruction interrupts the channel: ordinary damage to the King must
        // not cancel it, or a fast build makes the fight look like a teleport that never leaves the centre.
        async Task TeleportToAltar(double now, int ritualIndex)
        {
            int which = NearestFetish(now);
            if (which < 0)
            {
                _ritualIndex = RitualLifePercents.Length;
                return;
            }
            Console.WriteLine($"[gateway] {remote} King in the Mists 0x{id:x} phase 1: ritual {ritualIndex + 1}/{RitualLifePercents.Length} at {LifePercent}% life (gate {RitualLifePercents[ritualIndex]}%)");
            // Immunity must cover the teleport itself: the official King is already untouchable while he leaves for
            // the fetish. Starting it only after the 0x1b7 left a ~2 s window in which a burst still damaged him
            // (live 2026-11-01: 36,617,509 -> 36,608,271 before the immunity buff).
            await BeginRitualImmunity();
            bool interrupted = false;
            try
            {
                var (fx, fy) = FetishPoint(which);
                var (x, y) = BossStandPoint(which);
                var at = boss.Walk.Position(now);
                boss.BusyUntil = now + 2.6;
                boss.LastTarget = null;
                await ClearActionBeforeAnimation("0x01ac King in the Mists ends the previous action before the ritual teleport");
                if (!Present()) return;
                int action;
                lock (conn.Monsters) action = ++boss.ActionGeneration;
                if (!await SendMonsterAction(remote, stream, ciphers, conn, id, boss, action,
                    () => InstanceProtocol.ActionStarted(id, at.X, at.Y, x, y, K.TeleportFlee, NextSeq(), 0x0408),
                    $"0x01ab King in the Mists teleports next to Mystic Fetish {which + 1} at ({x}, {y})")) return;
                // TeleportFlee's clip is one second (GrantedEffects CastTime 1000). End the action before moving the object:
                // the client ignores a 0x1b7 that arrives while the actor is still in that action, which left its model at
                // the arena centre while the server fought from the fetish (live 2026-10-31).
                await Task.Delay(750);
                if (!Present()) return;
                bool teleportFlinching;
                lock (conn.Monsters) teleportFlinching = conn.Clock.Elapsed.TotalSeconds < boss.FlinchUntil;
                if (!teleportFlinching)
                    await Program.Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(id),
                        "0x01ac King in the Mists teleport ends");
                // The client can still be one frame behind the action end and ignore a 0x1b7 that arrives in the same
                // tick (the model then stays on the central shrine while the server casts from the fetish). Give it a
                // short settle, then repeat the cell with a 0x1ba glide so the model is definitely at the fetish.
                await Task.Delay(150);
                if (!Present()) return;
                double movedAt = conn.Clock.Elapsed.TotalSeconds;
                lock (conn.Monsters) boss.Walk.Teleport(x, y, movedAt);
                await Program.Send(remote, stream, ciphers, InstanceProtocol.Teleport(id, x, y),
                    $"0x01b7 King in the Mists teleports to ({x}, {y})");
                await Task.Delay(120);
                if (!Present()) return;
                await Program.Send(remote, stream, ciphers, InstanceProtocol.MoveTo(id, x, y, 0.1f, InstanceProtocol.FollowTerrain),
                    $"0x01ba King in the Mists teleport settle at ({x}, {y})");
                // The official TeleportFlee -> first ritual gap is ~1.7 s. Starting the channel 150 ms after the position
                // update still had the client's teleport action settling.
                await Task.Delay(1200);
                if (!Present()) return;

                if (_fetishes[which] is null) return;
                // The client's `0x400e` channel start is the StartChannelling crash path (0xc0000005 at 0x19e0daf).
                // Play the official rig clips directly instead: call_start_01 (4), call_loop_01 (3) and
                // call_end_01 (2). The ritual itself stays server-side, and the fetish StateMachine `active` update is
                // also withheld for now (it enters the same channel path with no action).
                double startedAt;
                lock (conn.Monsters)
                {
                    startedAt = conn.Clock.Elapsed.TotalSeconds;
                    var here = boss.Walk.Position(startedAt);
                    boss.Walk.Order(here.X, here.Y, startedAt);   // a channel stops the teleport/chase
                    ++boss.ActionGeneration;
                    boss.BusyUntil = Math.Max(boss.BusyUntil, startedAt + 4.0);
                }
                Console.WriteLine($"[gateway] {remote} King in the Mists 0x{id:x} phase {_phase}: channels into Mystic Fetish {which + 1}");
                // The channel animation is directional: face the fetish, not the direction of the TeleportFlee action or
                // the player. The official 0x400e targets the fetish and its beam goes into it.
                await Program.Send(remote, stream, ciphers,
                    InstanceProtocol.Orientation(id, Facing(x, y, fx, fy), locked: true),
                    $"0x01b9 King in the Mists faces Mystic Fetish {which + 1}");
                // The ritual bark belongs to the start of call_start_01, not to the teleport: Client.txt 11:45:27
                // "Bid the darkness!" and 11:45:50 "Nothingness approaches!" both land on clip 4.
                await Say(RitualBarks[ritualIndex % RitualBarks.Length]);
                await Program.Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(id, 0, 4, 1f),
                    "0x01b2 King in the Mists ritual start (call_start_01)");
                // call_start_01 is 1.733 s in the rig; the official capture keeps it for its full length (the first
                // ritual's 14 ms transition is a queued-action artifact, the second one holds the whole clip).
                await Task.Delay(1700);
                if (!Present()) return;
                // The fetish stays immune through the channel; its immunity (and hidden bar) is released when the
                // whole ritual animation is over, before the return teleport.
                await Program.Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(id, 0, 3, 1f),
                    "0x01b2 King in the Mists ritual channel (call_loop_01)");

                double until = startedAt + 3.4;   // 1.7 s start + 1.7 s loop
                while (conn.Clock.Elapsed.TotalSeconds < until)
                {
                    await Task.Delay(50);
                    if (!Present()) return;
                    if (conn.Paused) continue;
                    // Keep the visual channel running for its full window even if the player destroys the fetish; the
                    // ritual fails (no hazard, no Portent) but the King's immunity/cast remains readable.
                    if (boss.Dead || !FetishAlive(which)) interrupted = true;
                }
                if (!interrupted) await CastFetish(which);
                await Program.Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(id, 0, 2, 0.4f),
                    "0x01b2 King in the Mists ritual end (call_end_01)");
                // The official client holds call_end_01 for ~1 s before the return teleport.
                await Task.Delay(1000);
                if (!Present()) return;
                // The fetish's activation belongs to the ritual animation: it only becomes hostile and attackable
                // once call_end_01 has finished, not while the King is still channelling.
                await MakeFetishVulnerable(which);
                // A hit reaction owns the actor until its recovery ends; starting the return action before that would
                // replace the flinch and leave the model at the fetish.
                while (Present() && conn.Clock.Elapsed.TotalSeconds < boss.FlinchUntil) await Task.Delay(50);
                if (!Present()) return;
                await ReturnToCentre();
            }
            finally
            {
                await EndRitualImmunity();
            }
            _nextAltar = (which + 1) % _fetishes.Length;
            if (interrupted)
            {
                // The ritual did not count: the King tries the same threshold again later.
                _nextRitualAt = conn.Clock.Elapsed.TotalSeconds + 5;
                return;
            }
            _fetishUsed[which] = true;
            // A single hit that crossed more than one gate starts only the first ritual and consumes the rest: the
            // King must not chain-jump through all three fetishes from one burst. A hit that crossed one gate keeps the
            // next gate available.
            int hp = LifePercent;
            int crossed = 0;
            for (int i = ritualIndex; i < RitualLifePercents.Length; i++)
                if (RitualLifePercents[i] >= hp) crossed++;
            _ritualIndex = crossed > 1 ? RitualLifePercents.Length : ritualIndex + 1;
            while (_ritualIndex < RitualLifePercents.Length && RitualLifePercents[_ritualIndex] >= hp)
                _ritualIndex++;
            Console.WriteLine($"[gateway] {remote} King in the Mists 0x{id:x} ritual done at {hp}% life ({crossed} gate(s) crossed); next "
                + (_ritualIndex < RitualLifePercents.Length ? $"{RitualLifePercents[_ritualIndex]}%" : "phase 2 at 50%"));
            if (_ritualIndex >= RitualLifePercents.Length)
            {
                await Say("The darkness, exultant!");
                await Task.Delay(500);
                await Say("Nothingness approaches!");
            }
            _nextRitualAt = conn.Clock.Elapsed.TotalSeconds + 2.0;
            conn.KingPortentStacks = Math.Min(3, conn.KingPortentStacks + 1);
            conn.KingPortentUntil = conn.Clock.Elapsed.TotalSeconds + 30;
            // The Portent Vapors client buff (row 2409) is not applied: it was the last packet before another crash.
            await Summon(PoisonRat, 2 + which);
        }

        // Official ReturnFromEdge (0x4014) is a client channel and crashed StartChannelling even with a fresh
        // sequence. Until that transition is understood, play the teleport_01 rig clip and move the object with 0x1b7:
        // the model returns to the centre and no channel action is started.
        async Task ReturnToCentre()
        {
            double startedAt = conn.Clock.Elapsed.TotalSeconds;
            var here = boss.Walk.Position(startedAt);
            var (cx, cy) = _origin;
            // If the player is standing on the central return point, do not teleport onto them: shift the return
            // point 12 cells away from the player along the centre-to-player line.
            var (ppx, ppy) = conn.Walk.Position(startedAt);
            double pd = Distance(cx, cy, ppx, ppy);
            if (pd < 10)
            {
                double ux = pd < 1 ? 0 : (ppx - cx) / pd;
                double uy = pd < 1 ? 1 : (ppy - cy) / pd;
                (cx, cy) = conn.Layout.Near(((int)Math.Round(cx - ux * 12), (int)Math.Round(cy - uy * 12)));
            }
            if (Distance(here.X, here.Y, cx, cy) <= 2) return;
            lock (conn.Monsters) boss.BusyUntil = Math.Max(boss.BusyUntil, startedAt + 1.3);
            await ClearActionBeforeAnimation("0x01ac King in the Mists ends the previous action before the return teleport");
            if (!Present()) return;
            await Program.Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(id, 0, 35, 1f),
                "0x01b2 King in the Mists return teleport (teleport_01)");
            await Task.Delay(700);
            if (!Present()) return;
            double movedAt = conn.Clock.Elapsed.TotalSeconds;
            lock (conn.Monsters)
            {
                boss.ActionGeneration++;
                boss.Walk.Teleport(cx, cy, movedAt);
            }
            await Program.Send(remote, stream, ciphers, InstanceProtocol.Teleport(id, cx, cy),
                $"0x01b7 King in the Mists returns to the arena centre at ({cx}, {cy})");
            var (px, py) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            await Program.Send(remote, stream, ciphers, InstanceProtocol.Orientation(id, Facing(cx, cy, px, py), locked: false),
                "0x01b9 King in the Mists unlocks its facing after the ritual");
            await Task.Delay(500);
            if (!Present()) return;
        }

        // The arena's room doodads are server objects, not terrain: the official trace creates the platform shield,
        // the descension object and the RitualBossPortal at 44.236 s, before CTS1BossFightStart. Without them the arena
        // looks empty and the intro has no presentation. Positions are arena.arm's room points relative to mapboss
        // (162,174); (0,0) is the room origin for the platform shield.
        async Task SpawnArenaDoodads()
        {
            int ox = _origin.X - 162, oy = _origin.Y - 174;
            async Task Add(int roomX, int roomY, string type, string model, float scale = 1)
            {
                uint id = conn.NextObjectId++;
                var obj = new EffectObject(ox + roomX, oy + roomY, model) { Scale = scale };
                await Program.Send(remote, stream, ciphers,
                    InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(type), obj.Blob()),
                    $"0x02b9 create arena object 0x{id:x} {type} at ({ox + roomX}, {oy + roomY})");
                await Program.Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id),
                    $"0x01a9 arena object 0x{id:x} enters wake zone");
            }
            await Add(0, 0, "Metadata/Terrain/Leagues/Azmeri/Objects/Boss/FinalBoss/PlatformVisualBlocker",
                "Metadata/Effects/Spells/monsters_effects/League_Azmeri/monster_fx/vodoo_king_boss/arenaShields.ao");
            await Add(161, 219, "Metadata/MiscellaneousObjects/Descendancy/RitualDescensionObject",
                "Metadata/Terrain/Doodads/Leagues/DualAscendancy/RitualAscendancyObject/RitualAscendancy_Object_01.ao");
            await Add(162, 66, "Metadata/MiscellaneousObjects/RitualBossPortal",
                "Metadata/Effects/Microtransactions/Town_Portals/AzmeriVoodooPortal/AzmeriVoodooPortal.ao");
        }

        // The central HugeRitualShrine01_01.ao actor is a room object, not a doodad: the official server creates
        // RitualPillar2 at the arena.arm object point (mapboss-relative (0, -12)). Its active_pillars state is the
        // number of completed fetish rituals, so its first update is the shrine's start animation.
        async Task SpawnCentralShrine()
        {
            if (_centralShrine is not null) return;
            var (x, y) = conn.Layout.Near((_origin.X, _origin.Y - 12));
            uint shrineId = conn.NextObjectId++;
            var states = new StateMachineData([0], 0, ["active_pillars"]);
            _centralShrine = shrineId;
            _centralShrineStates = states;
            var obj = new RitualPillarObject(x, y, 0f, CentralShrineModel, states);
            await Program.Send(remote, stream, ciphers,
                InstanceEntities.CreateObject(shrineId, InstanceEntities.ObjectTypeHash(CentralShrine), obj.Blob()),
                $"0x02b9 create central HugeRitualShrine 0x{shrineId:x} at ({x}, {y}), state active_pillars = 0");
            await Program.Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(shrineId),
                $"0x01a9 central HugeRitualShrine 0x{shrineId:x} enters wake zone");
        }

        async Task AdvanceCentralShrine()
        {
            if (_centralShrine is not { } shrineId || _centralShrineStates is not { } states) return;
            int value = Math.Min(3, states.SharedStates[0] + 1);
            states = states.Set("active_pillars", value);
            _centralShrineStates = states;
            await Program.Send(remote, stream, ciphers,
                InstanceEntities.StateMachineUpdate(shrineId, RitualPillarObject.StateMachineComponentIndex, states),
                $"0x02ba central HugeRitualShrine 0x{shrineId:x} active_pillars = {value}");
        }

        // The three Mystic Fetishes are room objects of arena.arm (not doodads), so the server has to create them. They
        // are hostile, stationary and destructible; PlanMonsterAi skips their variety.
        async Task FindOrSpawnFetishes()
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            lock (conn.Monsters)
                foreach (var (fetishId, m) in conn.Monsters)
                    if (IsFetish(m.Variety))
                    {
                        var (mx, my) = m.Walk.Position(now);
                        int best = 0;
                        double bestDistance = double.PositiveInfinity;
                        for (int i = 0; i < _fetishes.Length; i++)
                        {
                            var (px, py) = FetishPoint(i);
                            double d = Distance(mx, my, px, py);
                            if (d >= bestDistance) continue;
                            bestDistance = d;
                            best = i;
                        }
                        if (bestDistance < 20) _fetishes[best] = fetishId;
                    }
            if (Monsters.Find(MysticFetish) is not { Supported: true } variety)
            {
                Console.WriteLine($"[gateway] {remote} King in the Mists: {MysticFetish} is not supported");
                return;
            }
            for (int i = 0; i < _fetishes.Length; i++)
            {
                if (_fetishes[i] is not null) continue;
                var (x, y) = FetishPoint(i);
                _fetishes[i] = await SpawnMonster(remote, stream, ciphers, conn, variety, boss.Level,
                    MonsterRarity.Normal, x, y, friendly: false, log: true, alone: true, unrisen: false,
                    rotation: FetishRotations[i]);
            }
            // The fetish is immune until the King's ritual reaches it: the same invisible cannot_be_damaged buff and
            // hidden overhead bar as the humanoid's intro (also for fetishes restored with a saved instance). The
            // controller clears both in MakeFetishVulnerable.
            for (int i = 0; i < _fetishes.Length; i++)
            {
                if (_fetishes[i] is not { } fetishId || _fetishBuffs[i] is not null) continue;
                lock (conn.Monsters)
                    if (conn.Monsters.TryGetValue(fetishId, out var fetish))
                        fetish.FetishGrace = true;
                ushort buff = NextBuff();
                _fetishBuffs[i] = buff;
                await Program.Send(remote, stream, ciphers,
                    InstanceEntities.AddBuff(fetishId, buff, CannotBeDamagedRow, fetishId),
                    $"0x01c1 Mystic Fetish {i + 1} intro immunity (BuffDefinitions row {CannotBeDamagedRow})");
                await Program.Send(remote, stream, ciphers,
                    InstanceEntities.StatsUpdate(fetishId, [(StatKeys.HideMiniLifeBar, 1)]),
                    $"0x01b3 Mystic Fetish {i + 1} hides its overhead life bar until the ritual");
            }
        }

        async Task MakeFetishVulnerable(int which)
        {
            if (_fetishes[which] is not { } fetishId) return;
            lock (conn.Monsters)
                if (conn.Monsters.TryGetValue(fetishId, out var fetish))
                    fetish.FetishGrace = false;
            if (_fetishBuffs[which] is { } buff)
            {
                _fetishBuffs[which] = null;
                await Program.Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(fetishId, buff),
                    $"0x01c2 Mystic Fetish {which + 1} immunity ends");
            }
            await Program.Send(remote, stream, ciphers,
                InstanceEntities.StatsUpdate(fetishId, [(StatKeys.HideMiniLifeBar, 0)]),
                $"0x01b3 Mystic Fetish {which + 1} shows its overhead life bar");
        }

        (int X, int Y) FetishPoint(int which)
        {
            var (dx, dy) = FetishOffsets[which];
            return (_origin.X + dx, _origin.Y + dy);
        }

        bool FetishAlive(int which)
        {
            if (_fetishes[which] is not { } fetishId) return false;
            lock (conn.Monsters)
                return conn.Monsters.TryGetValue(fetishId, out var fetish) && !fetish.Dead && fetish.Life > 0;
        }

        int NextFetish()
        {
            for (int k = 0; k < _fetishes.Length; k++)
            {
                int which = (_nextAltar + k) % _fetishes.Length;
                if (FetishAlive(which)) return which;
            }
            return -1;
        }

        // Pick the nearest fetish the player has not seen used yet, so the teleport happens in view rather than on a far
        // platform. The three thresholds still consume three distinct fetishes; if the nearest unused one was destroyed,
        // fall back to any surviving fetish.
        int NearestFetish(double now)
        {
            var (px, py) = conn.Walk.Position(now);
            int best = -1;
            double bestDistance = double.MaxValue;
            for (int pass = 0; pass < 2 && best < 0; pass++)
            {
                for (int i = 0; i < _fetishes.Length; i++)
                {
                    if (!FetishAlive(i) || (pass == 0 && _fetishUsed[i])) continue;
                    var (fx, fy) = FetishPoint(i);
                    double d = Distance(px, py, fx, fy);
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = i;
                    }
                }
            }
            return best;
        }

        // The King stands on the outer platform edge, matching the official 0x4014 return points exactly. Do not snap
        // through Layout.Near: the platform edge cells are blocked on the walk map and that moved the King 5-7 cells
        // farther away (live 2026-11-01: fetish 1 at (232,490) produced (217,477) instead of the official (222,484)).
        (int X, int Y) BossStandPoint(int which)
        {
            var (fx, fy) = FetishPoint(which);
            var (dx, dy) = FetishStandOffsets[which];
            return (fx + dx, fy + dy);
        }

        // Native Positioned faces -Y at zero, +X at pi/2.
        static float Facing(int fromX, int fromY, int toX, int toY) =>
            MathF.Atan2(toX - fromX, fromY - toY);

        async Task ActivateFetish(int which)
        {
            if (_fetishes[which] is not { } fetishId) return;
            SpawnedMonster? fetish;
            lock (conn.Monsters) conn.Monsters.TryGetValue(fetishId, out fetish);
            if (fetish?.Variety is not { } variety || fetish.States is not { } states) return;
            int index = variety.ComponentsOrBase.ToList().IndexOf("StateMachine");
            if (index < 0) return;
            states = states.Set("active", 1);
            lock (conn.Monsters) fetish.States = states;
            await Program.Send(remote, stream, ciphers, InstanceEntities.StateMachineUpdate(fetishId, (byte)index, states),
                $"0x02ba Mystic Fetish {which + 1} activates");
        }


        // The official fetishes cast their own hazard (RatSwarm, CorruptedBloodDegen, SummonFrogSwamps) when the King's
        // ritual reaches them. The client's RitualPillar.act does not carry the SpellAreaOfEffect animation in its default
        // stance, so do not send a raw 0x1ab for it; the state-machine activation is the visible ritual. Spawn the hazard
        // actors themselves instead.
        async Task CastFetish(int which)
        {
            if (_fetishes[which] is not { } fetishId) return;
            SpawnedMonster? fetish;
            lock (conn.Monsters) conn.Monsters.TryGetValue(fetishId, out fetish);
            if (fetish is null) return;
            double now = conn.Clock.Elapsed.TotalSeconds;
            var (fx, fy) = fetish.Walk.Position(now);
            var (px, py) = conn.Walk.Position(now);
            if (which == 0)
            {
                // RatSwarm: the official first fetish sends PoisonRat actors at the player/arena.
                await SummonAt(PoisonRat, 3, fx, fy);
            }
            else if (which == 1)
            {
                // CorruptedBloodDegen: red boiling-blood patches around the player, then an extra cast at the fetish.
                for (int i = 0; i < 3; i++)
                {
                    var (bx, by) = conn.Layout.Near((px + (i - 1) * 7, py + (i % 2 == 0 ? 5 : -5)));
                    await SpawnEffect(bx, by,
                        "Metadata/Effects/Spells/monsters_effects/League_Ritual/ritual_rune/blood_pool.ao", 1f);
                }
            }
            else
            {
                // SummonFrogSwamps: a Viscous Swamp under the player plus ExplosiveToad actors.
                var (sx, sy) = conn.Layout.Near((px, py));
                await SpawnEffect(sx, sy,
                    "Metadata/Effects/Spells/monsters_effects/League_Azmeri/monsters/PictFemaleStaff/swamp_pool.ao", 1f);
                await SummonAt(ExplosiveToad, 2, sx, sy);
            }
        }

        async Task AddBranchingAffliction(double seconds)
        {
            ushort buff;
            lock (conn.Monsters) buff = conn.NextBuff++;
            await Program.Send(remote, stream, ciphers,
                InstanceEntities.AddBuff(Program.PlayerId, buff, BranchingAfflictionRow, Program.PlayerId,
                    u68: BitConverter.SingleToUInt32Bits((float)seconds)),
                "0x01c1 player Branching Affliction (maze)");
        }

        async Task TeleportPlayer(int x, int y)
        {
            double at = conn.Clock.Elapsed.TotalSeconds;
            using (ProjectileMutation(conn))
            {
                Interlocked.Increment(ref conn.ActionCount);
                conn.Walk.Teleport(x, y, at);
            }
            await Program.Send(remote, stream, ciphers, InstanceProtocol.Teleport(Program.PlayerId, x, y),
                $"0x01b7 player to ({x}, {y})");
        }

        async Task<uint> SpawnEffect(int x, int y, string ao, float scale)
        {
            uint effectId = conn.NextObjectId++;
            var effect = new EffectObject(x, y, ao) { Scale = scale };
            await Program.Send(remote, stream, ciphers,
                InstanceEntities.CreateObject(effectId, InstanceEntities.ObjectTypeHash(EffectObject.TypePath), effect.Blob()),
                $"0x02b9 effect {ao} at ({x}, {y})");
            await Program.Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(effectId),
                "0x01a9 effect enters the wake zone");
            return effectId;
        }

        sealed record MazePlan((int X, int Y) Entrance, (int X, int Y) Exit, List<(int X, int Y)> Path);

        // A real route through the hedge-maze rooms: BFS over the placed maze rooms, connecting only rooms whose centers
        // are reachable from one another through the actual walk map. This gives the Guiding Lights a walkable line
        // instead of a straight fallback through the hedge walls.
        static MazePlan? MazePlanFor(ClientState client)
        {
            var level = ServerLevels.For(client.Layout.Id, client.Seed);
            if (level is null) return null;
            var rooms = level.Rooms
                .Where(r => r.Path.Contains("/TreantArena/Rooms/maze_", StringComparison.OrdinalIgnoreCase)).ToList();
            var starts = rooms.Where(r => r.Path.EndsWith("/maze_entrance.arm", StringComparison.OrdinalIgnoreCase)).ToList();
            var exitRoom = rooms.FirstOrDefault(r => r.Path.EndsWith("/maze_exit.arm", StringComparison.OrdinalIgnoreCase));
            if (starts.Count == 0 || exitRoom is null) return null;
            var arena = client.KingOrigin ?? (client.Layout.SpawnX, client.Layout.SpawnY);
            (int X, int Y) Center(PlacedArmRoom room) =>
                RoomCell(client, room, AreaPopulation.LocalToWorld(room.X, room.Y, room.Code,
                    room.Width / 2f, room.Height / 2f));
            var centers = rooms.ToDictionary(r => r, Center);
            var startRoom = starts.MinBy(r => Distance(arena.X, arena.Y, centers[r].X, centers[r].Y))!;
            bool Walkable(int x, int y) => client.Layout.WalkableFor(x, y, 0) && !client.Barrier.Contains((x, y));
            List<(int X, int Y)> Route((int X, int Y) a, (int X, int Y) b) =>
                ClientPathfinder.FindPath(a, b, Walkable, client.Layout.GridW, client.Layout.GridH,
                    limit: ClientPathfinder.SearchLimit.Expansions1000);
            var previous = new Dictionary<PlacedArmRoom, PlacedArmRoom?> { [startRoom] = null };
            var queue = new Queue<PlacedArmRoom>();
            queue.Enqueue(startRoom);
            while (queue.Count > 0 && !previous.ContainsKey(exitRoom))
            {
                var room = queue.Dequeue();
                var a = centers[room];
                foreach (var other in rooms)
                {
                    if (previous.ContainsKey(other)) continue;
                    var b = centers[other];
                    if (Math.Abs(a.X - b.X) > 90 || Math.Abs(a.Y - b.Y) > 90) continue;
                    if (Distance(a.X, a.Y, b.X, b.Y) > 90) continue;
                    var path = Route(a, b);
                    if (path.Count < 2 || Distance(path[^1].X, path[^1].Y, b.X, b.Y) > 6) continue;
                    previous[other] = room;
                    queue.Enqueue(other);
                }
            }
            if (!previous.ContainsKey(exitRoom)) return null;
            var roomPath = new List<PlacedArmRoom>();
            for (PlacedArmRoom? room = exitRoom; room is not null; room = previous[room])
                roomPath.Add(room);
            roomPath.Reverse();
            var route = new List<(int X, int Y)> { centers[roomPath[0]] };
            for (int i = 1; i < roomPath.Count; i++)
            {
                var path = Route(centers[roomPath[i - 1]], centers[roomPath[i]]);
                if (path.Count > 1) route.AddRange(path.Skip(1));
                else route.Add(centers[roomPath[i]]);
            }
            return new MazePlan(centers[startRoom], centers[exitRoom], route);
        }

        static (int X, int Y)? MazePoint(ClientState client, bool exit)
        {
            var level = ServerLevels.For(client.Layout.Id, client.Seed);
            if (level is null) return null;
            string suffix = exit ? "/maze_exit.arm" : "/maze_entrance.arm";
            var rooms = level.Rooms.Where(r => r.Path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rooms.Count == 0) return null;
            var center = client.KingOrigin ?? (client.Layout.SpawnX, client.Layout.SpawnY);
            var room = exit
                ? rooms[0]
                : rooms.MinBy(r => (double)(r.X - center.X) * (r.X - center.X) + (double)(r.Y - center.Y) * (r.Y - center.Y))!;
            // The entrance's telepad and the exit's portal object are at these room-local cells (maze_entrance.arm and
            // maze_exit.arm).
            var world = AreaPopulation.LocalToWorld(room.X, room.Y, room.Code, exit ? 80 : 34, exit ? 80 : 34);
            return RoomCell(client, room, world);
        }

        // The exact .arm telepad/portal cell can be a wall or a non-walkable decoration. Scan the room's world box for
        // the walkable cell closest to it instead of letting Near() snap outside the room.
        static (int X, int Y) RoomCell(ClientState client, PlacedArmRoom room, (int X, int Y) world)
        {
            var near = client.Layout.Near(world);
            if (Distance(near.X, near.Y, world.X, world.Y) <= 12) return near;
            var best = near;
            double bestDistance = Distance(near.X, near.Y, world.X, world.Y);
            int x0 = Math.Max(0, room.X - room.Width), x1 = Math.Min(client.Layout.GridW - 1, room.X + room.Width);
            int y0 = Math.Max(0, room.Y - room.Height), y1 = Math.Min(client.Layout.GridH - 1, room.Y + room.Height);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    if (!client.Layout.WalkableFor(x, y, 0) || client.Barrier.Contains((x, y))) continue;
                    double d = Distance(x, y, world.X, world.Y);
                    if (d >= bestDistance) continue;
                    bestDistance = d;
                    best = (x, y);
                }
            return best;
        }

        // The official maze uses GuidingLightDaemonMaze_ actors, not generic effect objects. Spawn the real monster when
        // the catalog supports it, with the effect wisp as a fallback.
        async Task<uint> SpawnGuidingLight(int x, int y)
        {
            if (Monsters.Find(GuidingLight) is { Supported: true } variety)
                return await SpawnMonster(remote, stream, ciphers, conn, variety, boss.Level, MonsterRarity.Normal,
                    x, y, friendly: true, log: false, alone: true, unrisen: false);
            return await SpawnEffect(x, y, "Metadata/Effects/Spells/wand_wisp_support/ao/wisp.ao", 1f);
        }

        async Task<(List<(uint Id, int At)> Wisps, List<(int X, int Y)> Path)> SpawnWisps(int ex, int ey, int px, int py,
            List<(int X, int Y)>? route = null)
        {
            var path = route is { Count: > 2 } ? route : ClientPathfinder.FindPath((ex, ey), (px, py),
                (x, y) => conn.Layout.WalkableFor(x, y, 0) && !conn.Barrier.Contains((x, y)),
                conn.Layout.GridW, conn.Layout.GridH, limit: ClientPathfinder.SearchLimit.Expansions1000);
            if (path.Count <= 2)
            {
                // The player's local path window is too small for the full hedge maze; keep the Guiding Lights on a
                // straight visual trail towards the exit as a fallback while the player navigates the walls.
                const int steps = 80;
                path = [.. Enumerable.Range(0, steps + 1).Select(i => (
                    (int)Math.Round(ex + (px - ex) * (double)i / steps),
                    (int)Math.Round(ey + (py - ey) * (double)i / steps)))];
            }
            var wisps = new List<(uint Id, int At)>();
            const int count = 8;
            for (int i = 0; i < count; i++)
            {
                int at = Math.Min(path.Count - 1, i * path.Count / count);
                uint wisp = await SpawnGuidingLight(path[at].X, path[at].Y);
                wisps.Add((wisp, at));
            }
            return (wisps, path);
        }

        async Task MoveWisps(List<(uint Id, int At)> wisps, List<(int X, int Y)> path, int step)
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            for (int i = 0; i < wisps.Count; i++)
            {
                var (wisp, at) = wisps[i];
                at = Math.Min(path.Count - 1, at + step);
                wisps[i] = (wisp, at);
                lock (conn.Monsters)
                    if (conn.Monsters.TryGetValue(wisp, out var light))
                    {
                        light.Walk.Teleport(path[at].X, path[at].Y, now);
                    }
                await Program.Send(remote, stream, ciphers, InstanceProtocol.Teleport(wisp, path[at].X, path[at].Y), null);
            }
        }

        // The official maze is populated with Wildwood Werewolves and Bloodtainter Arachnids as well as the Guiding
        // Lights. Keep two hostile examples near the middle of the route; they use the ordinary monster AI.
        async Task SpawnMazeMobs(List<(int X, int Y)> path)
        {
            if (path.Count < 4) return;
            var middle = path[path.Count / 2];
            if (Monsters.Find(WerewolfMaze) is { Supported: true } wolf)
            {
                var (x, y) = conn.Layout.Near((middle.X + 4, middle.Y + 4));
                await SpawnMonster(remote, stream, ciphers, conn, wolf, boss.Level, MonsterRarity.Normal,
                    x, y, log: false, alone: false);
            }
            if (Monsters.Find(MeleeSpiderMaze) is { Supported: true } spider)
            {
                var (x, y) = conn.Layout.Near((middle.X - 4, middle.Y - 4));
                await SpawnMonster(remote, stream, ciphers, conn, spider, boss.Level, MonsterRarity.Normal,
                    x, y, log: false, alone: false);
            }
        }

        // Phase 2: the staff slam, the maze portal and the run to the exit portal. The humanoid leaves at 50% life and
        // is re-staged at 25% when the player returns; the treant only emerges after the maze.
        async Task RunMaze(double now)
        {
            boss.BusyUntil = double.PositiveInfinity;   // the phase controller owns the King until phase 3
            await Say("Embrace nothingness!");
            await Cast(K.Slam, "slams his staff", seconds: 2.5);
            await Task.Delay(1000);
            if (!Present()) return;
            // The second captured maze bark ("Embrace nothingness!" in the 2026-10-31 fight, "The border of my realm
            // grows closer..." in the 2026-11-01 one) lands on the maze skill.
            await Say("The border of my realm grows closer...");
            await Cast(K.Maze, "opens the maze", seconds: 2.5);
            await Task.Delay(1000);
            if (!Present()) return;
            var plan = MazePlanFor(conn);
            var entrance = plan?.Entrance ?? MazePoint(conn, exit: false) ?? conn.Layout.Near((_origin.X, _origin.Y + 80));
            await TeleportPlayer(entrance.X, entrance.Y);
            await Say("A sliver of what my home feels like.");
            await AddBranchingAffliction(120);
            double mazeAt = conn.Clock.Elapsed.TotalSeconds;
            bool saidWisps = false, saidDraiocht = false;
            var exit = plan?.Exit ?? MazePoint(conn, exit: true) ?? conn.Layout.Near((_origin.X + 80, _origin.Y));
            uint portal = await SpawnEffect(exit.X, exit.Y,
                "Metadata/Effects/Microtransactions/Town_Portals/AzmeriVoodooPortal/AzmeriVoodooPortal.ao", 1f);
            var (wisps, path) = await SpawnWisps(entrance.X, entrance.Y, exit.X, exit.Y, plan?.Path);
            await SpawnMazeMobs(path);
            Console.WriteLine($"[gateway] {remote} King in the Mists: maze from ({entrance.X}, {entrance.Y}) to ({exit.X}, {exit.Y}), {path.Count} path cells");
            double deadline = conn.Clock.Elapsed.TotalSeconds + 120, nextMove = 0;
            while (Present() && conn.PlayerLife > 0 && !conn.Closed && conn.Clock.Elapsed.TotalSeconds < deadline)
            {
                await Task.Delay(150);
                double at = conn.Clock.Elapsed.TotalSeconds;
                // The captured maze taunts: the wisps line ~8 s after the entry, the Draiocht line later on.
                if (!saidWisps && at - mazeAt >= 8)
                {
                    saidWisps = true;
                    await Say("Ata... ore Wisps maah!");
                }
                if (!saidDraiocht && at - mazeAt >= 30)
                {
                    saidDraiocht = true;
                    await Say("The Draiocht cannot help you here!");
                }
                if (at >= nextMove)
                {
                    nextMove = at + 0.4;
                    await MoveWisps(wisps, path, 1);
                }
                var (wx, wy) = conn.Walk.Position(at);
                if (Distance(wx, wy, exit.X, exit.Y) <= 5) break;
            }
            if (!Present() || conn.Closed || conn.PlayerLife == 0) return;
            var (ax, ay) = conn.KingOrigin ?? _origin;
            var back = conn.Layout.Near((ax, ay - 92));
            await TeleportPlayer(back.X, back.Y);
            // The body disintegrates at the central shrine: put the humanoid back on the mapboss point before the
            // transformation, then let BecomeTreant replace it.
            var (bx, by) = conn.KingOrigin ?? _origin;
            boss.Walk.Teleport(bx, by, conn.Clock.Elapsed.TotalSeconds);
            await Program.Send(remote, stream, ciphers, InstanceProtocol.Teleport(id, bx, by),
                $"0x01b7 King in the Mists returns to the arena centre at ({bx}, {by})");
            foreach (var (wisp, _) in wisps)
            {
                lock (conn.Monsters) conn.Monsters.Remove(wisp);
                await Program.Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(wisp), null);
            }
            await Program.Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(portal), null);
            // The official create blob after the maze has the humanoid at 25% of its maximum life before 0x4013. The
            // treant itself then appears with a fresh, full 25.2M life bar (VoodooKingBoss3), so this value belongs to
            // the short-lived humanoid husk only.
            uint huskLife = (uint)((long)boss.MaxLife * 25 / 100);
            lock (conn.Monsters) boss.Life = huskLife;
            await Program.Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(id, huskLife),
                $"0x01b1 King in the Mists humanoid husk set to 25% ({huskLife}/{boss.MaxLife}) before the transformation");
            await BecomeTreant(conn.Clock.Elapsed.TotalSeconds);
        }

        async Task BecomeTreant(double now)
        {
            boss.BusyUntil = now + 10;
            var at = boss.Walk.Position(now);
            int action;
            lock (conn.Monsters) action = ++boss.ActionGeneration;
            // The staff hits the floor, then the humanoid body disintegrates and the treant emerges. The player is already
            // back in the arena (RunMaze). Keep the humanoid for the full morph clip; the earlier 2.5 s swap cut the
            // animation and made the model change look instantaneous.
            await SendMonsterAction(remote, stream, ciphers, conn, id, boss, action,
                () => InstanceProtocol.ActionStarted(id, at.X, at.Y, at.X, at.Y, K.Slam, NextSeq(), 0x0408),
                "0x01ab King in the Mists slams his staff");
            await Task.Delay(1000);
            if (!Present()) return;
            lock (conn.Monsters) action = ++boss.ActionGeneration;
            await SendMonsterAction(remote, stream, ciphers, conn, id, boss, action,
                () => InstanceProtocol.ActionStarted(id, at.X, at.Y, at.X, at.Y, K.BecomeTreant, NextSeq(), 0x0408),
                "0x01ab King in the Mists becomes a treant");
            await Task.Delay(5500);
            if (!Present()) return;
            if (!Present()) return;
            at = boss.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            lock (conn.Monsters)
            {
                boss.Dead = true;
                boss.Life = 0;
                conn.Monsters.Remove(id);
            }
            await Program.Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id),
                $"0x02bb remove the King's humanoid form 0x{id:x}");
            if (_portentBuff is { } stale)
            {
                _portentBuff = null;
                await Program.Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(Program.PlayerId, stale),
                    "0x01c2 player Portent Vapors ends with the humanoid form");
            }
            if (Monsters.Find(Boss3) is not { Supported: true } variety)
            {
                Console.WriteLine($"[gateway] {remote} King in the Mists: {Boss3} is not supported");
                return;
            }
            await SpawnMonster(remote, stream, ciphers, conn, variety, boss.Level, MonsterRarity.Unique,
                at.X, at.Y, name: "The King in the Mists", log: true, alone: true);
            await Say("The Wildwood's power... is mine!");
        }

        async Task Summon(string varietyId, int count)
        {

            var (x, y) = boss.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            await SummonAt(varietyId, count, x, y);
        }

        async Task SummonAt(string varietyId, int count, int x, int y)
        {
            if (Monsters.Find(varietyId) is not { Supported: true } variety) return;
            for (int i = 0; i < count; i++)
            {
                var at = conn.Layout.Near((x + 6 + i * 4, y + 6 - i * 3));
                await SpawnMonster(remote, stream, ciphers, conn, variety, boss.Level, MonsterRarity.Normal,
                    at.X, at.Y, log: false, alone: true);
            }
        }

        ushort NextBuff()
        {
            lock (conn.Monsters) return conn.NextBuff++;
        }

        bool Present()
        {
            lock (conn.Monsters)
                return !conn.Closed && !conn.LeavingArea && !conn.RageRetired && !boss.Dead && boss.Life > 0
                    && conn.Monsters.TryGetValue(id, out var current) && ReferenceEquals(current, boss);
        }

        static double Distance(int ax, int ay, int bx, int by) =>
            Math.Sqrt((double)(ax - bx) * (ax - bx) + (double)(ay - by) * (ay - by));
    }
}

