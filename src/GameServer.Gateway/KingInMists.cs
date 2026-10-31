using System.Net.Sockets;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Data.Terrain.Generation;

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

    // The arena.arm object points, relative to its "mapboss" point (162, 174): the three Mystic Fetishes
    // (94, 122), (230, 123) and (160, 238), and the central HugeRitualShrine (162, 162). The client places the room
    // unturned in AzmeriLeagueBoss seed 1 (the unique map's seed), which is why the offsets are fixed here.
    static readonly (int X, int Y)[] FetishOffsets = [(-68, -52), (68, -51), (-2, 64)];

    // The basic Move action (ActionTypes HASH16 & 0x3fff): the treant has MovementSpeed 18 and must close the distance
    // itself; the arena is large enough that a stationary boss looks stalled between its ranged casts.
    const ushort MoveSkill = 0x2909;

    // GrantedEffects rows (poedb / MonsterVarieties GrantedEffectsKeys). The default attack is row 420.
    const int MeleeEffect = 420, VineSlamEffect = 7597, VineCascadeEffect = 7596, SpellProjectile2Effect = 3893,
        SpellProjectile3Effect = 3894, TeleportEffect = 12419, SummonEffigyEffect = 8336, SummonDeathAuraEffigyEffect = 8342,
        BugBombEffect = 1898, BugRendEffect = 10510, BugRendSingleEffect = 10511, MazeEffect = 8341,
        BecomeTreantEffect = 8340, RitualEffect = 7568, ReturnFromEdgeEffect = 8343, HexNoMovementEffect = 8337,
        HexNoStandingEffect = 8338, PunishmentEffect = 12437, BugBombImpactEffect = 7608, SwarmPulseEffect = 10354;

    // Server skill ids (Actor skill list entries). They must not collide with basic actions; 0x0103 is the ordinary
    // boss stage id and 0x0114 is a basic action in 3.29.3b. The default attack is SkillCatalog.FirstSkillId.
    internal const ushort SlamSkill = 0x0104, CascadeSkill = 0x0105, TeleportSkill = 0x0107, EffigySkill = 0x0108,
        BugBombSkill = 0x0109, BugRendSkill = 0x010b, MazeSkill = 0x010c, TreantSkill = 0x010d,
        Projectile2Skill = 0x010e, Projectile3Skill = 0x010f, RitualSkill = 0x0110, ReturnFromEdgeSkill = 0x0111,
        BugRendSingleSkill = 0x0112, PunishmentSkill = 0x0113, HexNoMovementSkill = 0x0115, HexNoStandingSkill = 0x0116,
        BugBombImpactSkill = 0x0117, SwarmPulseSkill = 0x0118;

    // The fetish hazards are actions on the fetish actor, not on the King. Their effects are the five rows of
    // MonsterVarieties 10152's granted list (RatSwarm, CorruptedBloodDegen, SummonFrogSwamps and the two CGE variants).
    internal const ushort FetishRatSkill = 0x0120, FetishBloodSkill = 0x0121, FetishSwampSkill = 0x0122,
        FetishBloodAuraSkill = 0x0123, FetishSwampAuraSkill = 0x0124;
    static readonly int[] FetishEffects = [10351, 10352, 10353, 5766, 5767];

    // BuffDefinitions rows: Portent Vapors, Branching Affliction, the two ritual hexes and Mystic Miasma.
    internal const ushort PortentVaporsRow = 2409, BranchingAfflictionRow = 2371, RitualMeditationRow = 2358,
        RitualDanceRow = 2359, MysticMiasmaRow = 2401;

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
                : Program.MonsterProjectileSkillStats(conn, effect, level, use);
            if (Program.SkillsFor(conn).FindByRow(id, effect, level, stats) is { } skill) list.Add(skill);
        }
        Add(SkillCatalog.FirstSkillId, MeleeEffect, attack);
        if (IsBoss2(variety))
        {
            Add(SlamSkill, VineSlamEffect, Slam(attack));
            Add(CascadeSkill, VineCascadeEffect, Cascade(attack));
            Add(Projectile2Skill, SpellProjectile2Effect, Projectile2(attack));
            Add(TeleportSkill, TeleportEffect, Teleport(attack));
            Add(EffigySkill, SummonEffigyEffect, Effigy(attack));
            Add(MazeSkill, MazeEffect, NoDamage(attack, MazeSkill, 2.5));
            Add(TreantSkill, BecomeTreantEffect, NoDamage(attack, TreantSkill, 2.5));
            Add(RitualSkill, RitualEffect, NoDamage(attack, RitualSkill, 2.5));
            Add(ReturnFromEdgeSkill, ReturnFromEdgeEffect, NoDamage(attack, ReturnFromEdgeSkill, 1.5));
            Add(HexNoMovementSkill, HexNoMovementEffect, NoDamage(attack, HexNoMovementSkill, 1.5));
            Add(HexNoStandingSkill, HexNoStandingEffect, NoDamage(attack, HexNoStandingSkill, 1.5));
            Add(PunishmentSkill, PunishmentEffect, Punishment(attack));
        }
        else
        {
            Add(SlamSkill, VineSlamEffect, Slam(attack));
            Add(Projectile3Skill, SpellProjectile3Effect, Projectile3(attack));
            Add(BugBombSkill, BugBombEffect, BugBomb(attack));
            // The official 3.29.3b capture uses VoodooKingBugRend/BugRendSingle and BugSoulrend projectiles; the old
            // live PlayAnimation failure came from a stale Debug build, not from this skill.
            Add(BugRendSkill, BugRendEffect, BugRend(attack));
            Add(BugRendSingleSkill, BugRendSingleEffect, BugRendSingle(attack));
            Add(EffigySkill, SummonDeathAuraEffigyEffect, Effigy(attack));
            Add(HexNoMovementSkill, HexNoMovementEffect, NoDamage(attack, HexNoMovementSkill, 1.5));
            Add(HexNoStandingSkill, HexNoStandingEffect, NoDamage(attack, HexNoStandingSkill, 1.5));
            Add(PunishmentSkill, PunishmentEffect, Punishment(attack));
            Add(BugBombImpactSkill, BugBombImpactEffect, NoDamage(attack, BugBombImpactSkill, 1.0));
            Add(SwarmPulseSkill, SwarmPulseEffect, NoDamage(attack, SwarmPulseSkill, 1.0));
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
            var stats = attack.SkillStats();
            if (Program.SkillsFor(conn).FindByRow(id, FetishEffects[i], level, stats) is { } skill) list.Add(skill);
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

    static MonsterAttack Slam(MonsterAttack attack) => With(attack, SlamSkill, 1.0, 22,
        ConvertPhysical(attack.Damage.Scale(1.9), 30));

    static MonsterAttack Cascade(MonsterAttack attack) => With(attack, CascadeSkill, 1.0, 52,
        ConvertPhysical(attack.Damage.Scale(1.4), 25));

    static MonsterAttack Projectile2(MonsterAttack attack) => With(attack, Projectile2Skill, 1.0, 50,
        attack.Damage.Scale(0.4).Add(DamageType.Chaos, attack.Damage[DamageType.Physical].Scale(0.15)))
        with { Projectile = new SkillCatalog.MonsterProjectileInfo("Metadata/Projectiles/AzmeriVoodooKingSpellProj", 800, Lobbed: false, 1) };

    static MonsterAttack Projectile3(MonsterAttack attack) => With(attack, Projectile3Skill, 1.0, 50,
        attack.Damage.Scale(0.45).Add(DamageType.Chaos, attack.Damage[DamageType.Physical].Scale(0.45)))
        with { Projectile = new SkillCatalog.MonsterProjectileInfo("Metadata/Projectiles/AzmeriVoodooKing3SpellProj", 800, Lobbed: false, 1) };

    static MonsterAttack Teleport(MonsterAttack attack) => NoDamage(attack, TeleportSkill, 1.0);

    static MonsterAttack Effigy(MonsterAttack attack) => NoDamage(attack, EffigySkill, 1.0);

    static MonsterAttack BugBomb(MonsterAttack attack) => With(attack, BugBombSkill, 1.0, 45,
        attack.Damage.Scale(0.55).Add(DamageType.Chaos, attack.Damage[DamageType.Physical].Scale(0.18)))
        with { Projectile = new SkillCatalog.MonsterProjectileInfo("Metadata/Projectiles/BugBomb", 400, Lobbed: true, 2) { AreaRadius = 9 } };

    // The official cast releases six BugSoulrend projectiles (Projectiles.dat: collision radius 3, speed 200).
    static MonsterAttack BugRend(MonsterAttack attack) => With(attack, BugRendSkill, 1.0, 48,
        attack.Damage.Scale(0.8))
        with { Projectile = new SkillCatalog.MonsterProjectileInfo("Metadata/Projectiles/BugSoulrend", 200, Lobbed: false, 3) { Count = 6 } };

    static MonsterAttack BugRendSingle(MonsterAttack attack) => With(attack, BugRendSingleSkill, 1.0, 48,
        attack.Damage.Scale(0.8))
        with { Projectile = new SkillCatalog.MonsterProjectileInfo("Metadata/Projectiles/BugSoulrend", 200, Lobbed: false, 3) };

    static MonsterAttack Punishment(MonsterAttack attack) => With(attack, PunishmentSkill, 1.0, 40,
        attack.Damage.Scale(0.9));

    static MonsterAttack NoDamage(MonsterAttack attack, ushort skill, double seconds) =>
        With(attack, skill, seconds, 0, DamagePacket.Empty);

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
        int _altars, _nextAltar;
        ushort? _portentBuff;
        readonly uint?[] _fetishes = new uint?[3];
        uint? _centralShrine;
        StateMachineData? _centralShrineStates;
        double _nextSlam, _nextCascade, _nextProjectile, _nextTeleport, _nextEffigy, _nextBomb, _nextRend, _nextChase;
        double _nextHex, _nextPunishment, _graceUntil;
        bool _barShown, _mazeStarted, _hexDance;

        public async Task Run()
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            _origin = conn.KingOrigin ?? boss.Walk.Position(now);
            _nextTeleport = now + 8;
            _nextSlam = now + 4;
            _nextCascade = now + 6;
            _nextProjectile = now + 3;
            _nextEffigy = now + 12;
            _nextBomb = now + 6;
            _nextRend = now + 10;
            _nextHex = now + 15;
            _nextPunishment = now + 15;
            _graceUntil = _phase == 3 ? now + 7 : 0;
            try
            {
                await FindOrSpawnFetishes();
                await SpawnCentralShrine();
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
                    if (!_barShown)
                    {
                        _barShown = true;
                        await Program.ShowBossLifeBar(remote, stream, ciphers, id, boss, conn);
                    }
                    if (now < boss.FlinchUntil || now < boss.BusyUntil || boss.StageRunning) continue;
                    if (_phase == 3)
                    {
                        // The treant's emergence grace: no action and no damage until it ends.
                        if (now < _graceUntil) continue;
                        if (boss.KingGrace) boss.KingGrace = false;
                    }
                    else if (!_mazeStarted && (long)boss.Life * 100 <= (long)boss.MaxLife * 30)
                    {
                        // Phase 2 starts at 30% life, not when the fetishes are used: the humanoid is already locked
                        // to this floor by TakeDamageLocked.
                        _mazeStarted = true;
                        await RunMaze(now);
                        return;
                    }
                    if (_phase == 1) await PhaseOne(now, distance, px, py);
                    else await PhaseThree(now, distance, px, py);
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

        async Task PhaseOne(double now, double distance, int px, int py)
        {
            if (_altars < 3 && now >= _nextTeleport)
            {
                await TeleportToAltar(now);
                return;
            }
            if (distance <= 24 && now >= _nextSlam)
            {
                _nextSlam = now + 15;
                await Use(Slam(boss.Attack!), area: true, radius: 22, "Vine Slam");
                return;
            }
            if (distance <= 52 && now >= _nextCascade)
            {
                _nextCascade = now + 10;
                await Use(Cascade(boss.Attack!), area: true, radius: 18, "Vine Cascade");
                return;
            }
            // The humanoid is immobile (MovementSpeed 0); its effigy is its only stationary summon in phase 1.
            if (now >= _nextEffigy)
            {
                _nextEffigy = now + 25;
                await Cast(EffigySkill, "summons an effigy");
                await Summon(DeathEffigy, 1);
                return;
            }
            if (now >= _nextHex)
            {
                _nextHex = now + 25;
                await CastHex(HexNoMovementSkill, RitualMeditationRow, "casts Ritual of Meditation");
                return;
            }
            if (distance <= 15)
            {
                await Use(boss.Attack!, area: false, radius: 0, "default attack");
                return;
            }
            if (now >= _nextProjectile)
            {
                _nextProjectile = now + 4;
                await Use(Projectile2(boss.Attack!), area: false, radius: 0, "spell projectile");
            }
        }

        async Task PhaseThree(double now, double distance, int px, int py)
        {
            if (distance <= 22 && now >= _nextSlam)
            {
                _nextSlam = now + 15;
                await Use(Slam(boss.Attack!), area: true, radius: 22, "Treant Slam");
                return;
            }
            if (distance <= 50 && now >= _nextBomb)
            {
                _nextBomb = now + 20;
                await Use(BugBomb(boss.Attack!), area: false, radius: 0, "Bug Bomb");
                return;
            }
            // Bugrend fires from the central ritual altar, not from the boss, so distance does not gate it.
            if (now >= _nextRend)
            {
                _nextRend = now + 10;
                await Use(BugRend(boss.Attack!), area: false, radius: 0, "Bugrend");
                return;
            }
            if (now >= _nextEffigy)
            {
                _nextEffigy = now + 20;
                await Cast(EffigySkill, "summons an effigy");
                await Summon(DeathEffigy, 1);
                return;
            }
            if (now >= _nextHex)
            {
                _nextHex = now + 25;
                _hexDance = !_hexDance;
                if (_hexDance)
                    await CastHex(HexNoStandingSkill, RitualDanceRow, "casts Ritual of Dance");
                else
                    await CastHex(HexNoMovementSkill, RitualMeditationRow, "casts Ritual of Meditation");
                return;
            }
            if (now >= _nextPunishment)
            {
                _nextPunishment = now + 20;
                await Use(Punishment(boss.Attack!), area: false, radius: 0, "VoodooKingPunishment");
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
                await Use(Projectile3(boss.Attack!), area: false, radius: 0, "spell projectile");
                return;
            }
            // The treant is slow but not stationary (MovementSpeed 18): it walks after a player that kites it instead of
            // standing at the altar between casts.
            if (distance > 16) await Chase(now, px, py, distance);
        }

        // A skill that only plays an animation (the effigy summon): no contact and no damage, unlike MonsterSwing.
        async Task Cast(ushort skill, string label)
        {
            double startedAt = conn.Clock.Elapsed.TotalSeconds;
            int action;
            lock (conn.Monsters)
            {
                action = ++boss.ActionGeneration;
                boss.BusyUntil = Math.Max(boss.BusyUntil, startedAt + 1.2);
            }
            Console.WriteLine($"[gateway] {remote} King in the Mists 0x{id:x} phase {_phase}: {label}");
            await Program.SendMonsterAction(remote, stream, ciphers, conn, id, boss, action,
                () =>
                {
                    double at = conn.Clock.Elapsed.TotalSeconds;
                    var here = boss.Walk.Position(at);
                    boss.Walk.Order(here.X, here.Y, at);   // a cast stops the chase
                    return InstanceProtocol.ActionStarted(id, here.X, here.Y, here.X, here.Y, skill,
                        Program.MonsterActionSequence, 0x0408);
                },
                $"0x01ab King in the Mists {label}");
        }

        // Ritual of Meditation/Dance: the official actor plays the hex and applies the matching 8 s debuff. The
        // imprisonment/maim movement rule is not modelled, so this is the animation/icon half of the ritual. The row's
        // client duration is not taken from the AddBuff payload, so remove the icon explicitly after the eight seconds.
        async Task CastHex(ushort skill, ushort row, string label)
        {
            await Cast(skill, label);
            ushort buff;
            lock (conn.Monsters) buff = conn.NextBuff++;
            await Program.Send(remote, stream, ciphers,
                InstanceEntities.AddBuff(Program.PlayerId, buff, row, Program.PlayerId,
                    u68: BitConverter.SingleToUInt32Bits(8f)),
                $"0x01c1 player {label}");
            _ = RemoveBuffAfter(buff, 8);
        }

        async Task RemoveBuffAfter(ushort buff, double seconds)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds));
                if (conn.Closed) return;
                await Program.Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(Program.PlayerId, buff),
                    "0x01c2 ritual hex expires");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
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
            await Program.SendMonsterAction(remote, stream, ciphers, conn, id, boss, action,
                () =>
                {
                    // Order only when the action actually reaches the gate: a flinch between the decision and the send
                    // invalidates it, and the server walk must not advance under a cancelled move.
                    double sentAt = conn.Clock.Elapsed.TotalSeconds;
                    var here = boss.Walk.Position(sentAt);
                    boss.Walk.Order(tx, ty, sentAt);
                    return InstanceProtocol.ActionStarted(id, here.X, here.Y, tx, ty, MoveSkill,
                        Program.MonsterActionSequence, 0x0408);
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
                boss.BusyUntil = Math.Max(boss.BusyUntil, startedAt + attack.Seconds);
            }
            Console.WriteLine($"[gateway] {remote} King in the Mists 0x{id:x} phase {_phase}: {label}");
            await Program.MonsterSwing(remote, stream, ciphers, conn, id, boss, action, attack, area, radius);
        }

        // The official phase-1 loop is: teleport to a fetish, channel GSVoodooKingRitual into it, then
        // EASVoodooKingReturnFromEdge back to the centre. The teleport and return are actions with their own one-second
        // clips; moving the object (0x1b7) at the action's departure and ending the action before the next one keeps the
        // model from being left in a teleport/channel pose. A hit on the King or the fetish dying during the channel
        // interrupts it: the fetish is not activated and no hazard is summoned.
        async Task TeleportToAltar(double now)
        {
            int which = NextFetish();
            if (which < 0)
            {
                _altars = 3;
                return;
            }
            var (fx, fy) = FetishPoint(which);
            var (x, y) = BossStandPoint(fx, fy);
            var at = boss.Walk.Position(now);
            boss.BusyUntil = now + 2.6;
            boss.LastTarget = null;
            int action;
            lock (conn.Monsters) action = ++boss.ActionGeneration;
            if (!await Program.SendMonsterAction(remote, stream, ciphers, conn, id, boss, action,
                () => InstanceProtocol.ActionStarted(id, at.X, at.Y, x, y, TeleportSkill, Program.MonsterActionSequence, 0x0408),
                $"0x01ab King in the Mists teleports next to Mystic Fetish {which + 1} at ({x}, {y})")) return;
            // Teleport's clip is one second (GrantedEffects CastTime 1000). Move the object at the departure end, then
            // end the action so the ritual does not replace a still-running teleport clip.
            await Task.Delay(750);
            if (!Present()) return;
            double movedAt = conn.Clock.Elapsed.TotalSeconds;
            lock (conn.Monsters) boss.Walk.Teleport(x, y, movedAt);
            await Program.Send(remote, stream, ciphers, InstanceProtocol.Teleport(id, x, y),
                $"0x01b7 King in the Mists teleports to ({x}, {y})");
            await Task.Delay(350);
            if (!Present()) return;
            bool teleportFlinching;
            lock (conn.Monsters) teleportFlinching = conn.Clock.Elapsed.TotalSeconds < boss.FlinchUntil;
            if (!teleportFlinching)
                await Program.Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(id),
                    "0x01ac King in the Mists teleport ends");

            if (_fetishes[which] is not { } fetishId) return;
            var (tx, ty) = FetishPoint(which);
            int channel;
            double startedAt;
            uint startLife;
            lock (conn.Monsters)
            {
                startedAt = conn.Clock.Elapsed.TotalSeconds;
                var here = boss.Walk.Position(startedAt);
                boss.Walk.Order(here.X, here.Y, startedAt);   // a channel stops the teleport/chase
                channel = ++boss.ActionGeneration;
                boss.BusyUntil = Math.Max(boss.BusyUntil, startedAt + 3.2);
                startLife = boss.Life;
            }
            Console.WriteLine($"[gateway] {remote} King in the Mists 0x{id:x} phase {_phase}: channels into Mystic Fetish {which + 1}");
            if (!await Program.SendMonsterAction(remote, stream, ciphers, conn, id, boss, channel,
                () => InstanceProtocol.ActionStarted(id, x, y, tx, ty, RitualSkill,
                    Program.MonsterActionSequence, 0x0408, targetObjectId: fetishId),
                $"0x01ab King in the Mists channels GSVoodooKingRitual into Mystic Fetish {which + 1}"))
            {
                // A hit between the teleport and the channel invalidated it. Let the flinch finish, return to the
                // centre and retry later instead of leaving the model at the fetish.
                while (Present() && conn.Clock.Elapsed.TotalSeconds < boss.FlinchUntil) await Task.Delay(50);
                if (Present()) await ReturnToCentre();
                _nextTeleport = conn.Clock.Elapsed.TotalSeconds + 5;
                return;
            }

            bool interrupted = false;
            double until = startedAt + 2.5;
            while (conn.Clock.Elapsed.TotalSeconds < until)
            {
                await Task.Delay(50);
                if (!Present()) return;
                if (conn.Paused) continue;
                lock (conn.Monsters)
                    if (boss.Dead || boss.Life < startLife || boss.ActionGeneration != channel)
                        interrupted = true;
                if (!FetishAlive(which)) interrupted = true;
                if (interrupted) break;
            }

            bool flinching;
            lock (conn.Monsters) flinching = conn.Clock.Elapsed.TotalSeconds < boss.FlinchUntil;
            if (!flinching)
                await Program.Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(id),
                    interrupted ? "0x01ac King in the Mists ritual interrupted" : "0x01ac King in the Mists ritual ends");
            if (!interrupted)
            {
                await ActivateFetish(which);
                await AdvanceCentralShrine();
                await CastFetish(which);
            }
            // A hit reaction owns the actor until its recovery ends; starting the return action before that would
            // replace the flinch and leave the model at the fetish.
            while (Present() && conn.Clock.Elapsed.TotalSeconds < boss.FlinchUntil) await Task.Delay(50);
            if (!Present()) return;
            await ReturnToCentre();
            _nextAltar = (which + 1) % _fetishes.Length;
            if (interrupted)
            {
                // The ritual did not count: the King tries another fetish later.
                _nextTeleport = conn.Clock.Elapsed.TotalSeconds + 5;
                return;
            }
            _altars++;
            _nextTeleport = conn.Clock.Elapsed.TotalSeconds + 10;
            conn.KingPortentStacks = Math.Min(3, conn.KingPortentStacks + 1);
            conn.KingPortentUntil = conn.Clock.Elapsed.TotalSeconds + 30;
            await AddPortentBuff(conn.KingPortentUntil);
            await Summon(PoisonRat, 2 + which);
            // The third fetish is the last one: "the darkness, exultant". The phase itself changes at 30% life (Run),
            // not here.
        }

        // The official ReturnFromEdge clip is also one second (Teleport, granted effect 8343). Start it at the fetish,
        // move the object back to the arena centre at departure, and end the action before the next decision.
        async Task ReturnToCentre()
        {
            double startedAt = conn.Clock.Elapsed.TotalSeconds;
            var here = boss.Walk.Position(startedAt);
            var (cx, cy) = _origin;
            if (Distance(here.X, here.Y, cx, cy) <= 2) return;
            int action;
            lock (conn.Monsters)
            {
                action = ++boss.ActionGeneration;
                boss.BusyUntil = Math.Max(boss.BusyUntil, startedAt + 2.0);
            }
            if (!await Program.SendMonsterAction(remote, stream, ciphers, conn, id, boss, action,
                () => InstanceProtocol.ActionStarted(id, here.X, here.Y, cx, cy, ReturnFromEdgeSkill,
                    Program.MonsterActionSequence, 0x0408, targetObjectId: id),
                $"0x01ab King in the Mists returns from the Mystic Fetish to ({cx}, {cy})")) return;
            await Task.Delay(650);
            if (!Present()) return;
            double movedAt = conn.Clock.Elapsed.TotalSeconds;
            lock (conn.Monsters) boss.Walk.Teleport(cx, cy, movedAt);
            await Program.Send(remote, stream, ciphers, InstanceProtocol.Teleport(id, cx, cy),
                $"0x01b7 King in the Mists returns to the arena centre at ({cx}, {cy})");
            await Task.Delay(350);
            if (!Present()) return;
            bool flinching;
            lock (conn.Monsters) flinching = conn.Clock.Elapsed.TotalSeconds < boss.FlinchUntil;
            if (!flinching)
                await Program.Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(id),
                    "0x01ac King in the Mists return ends");
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
            if (Program.Monsters.Find(MysticFetish) is not { Supported: true } variety)
            {
                Console.WriteLine($"[gateway] {remote} King in the Mists: {MysticFetish} is not supported");
                return;
            }
            for (int i = 0; i < _fetishes.Length; i++)
            {
                if (_fetishes[i] is not null) continue;
                var (x, y) = FetishPoint(i);
                _fetishes[i] = await Program.SpawnMonster(remote, stream, ciphers, conn, variety, boss.Level,
                    MonsterRarity.Normal, x, y, friendly: false, log: true, alone: true, unrisen: false);
            }
        }

        (int X, int Y) FetishPoint(int which)
        {
            var (dx, dy) = FetishOffsets[which];
            return conn.Layout.Near((_origin.X + dx, _origin.Y + dy));
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

        // The King stands a few cells in front of the fetish, on the platform, instead of inside the shrine's footprint.
        (int X, int Y) BossStandPoint(int fx, int fy)
        {
            double dx = _origin.X - fx, dy = _origin.Y - fy;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance < 1) return (fx, fy);
            return conn.Layout.Near(((int)Math.Round(fx + dx / distance * 8), (int)Math.Round(fy + dy / distance * 8)));
        }

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
            var (x, y) = fetish.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            if (which == 0) await SummonAt(PoisonRat, 3, x, y);
            else if (which >= 2) await SummonAt(ExplosiveToad, 2, x, y);
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
            using (Program.ProjectileMutation(conn))
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
            if (Program.Monsters.Find(GuidingLight) is { Supported: true } variety)
                return await Program.SpawnMonster(remote, stream, ciphers, conn, variety, boss.Level, MonsterRarity.Normal,
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
                        light.CollisionPosition = (path[at].X, path[at].Y);
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
            if (Program.Monsters.Find(WerewolfMaze) is { Supported: true } wolf)
            {
                var (x, y) = conn.Layout.Near((middle.X + 4, middle.Y + 4));
                await Program.SpawnMonster(remote, stream, ciphers, conn, wolf, boss.Level, MonsterRarity.Normal,
                    x, y, log: false, alone: false);
            }
            if (Program.Monsters.Find(MeleeSpiderMaze) is { Supported: true } spider)
            {
                var (x, y) = conn.Layout.Near((middle.X - 4, middle.Y - 4));
                await Program.SpawnMonster(remote, stream, ciphers, conn, spider, boss.Level, MonsterRarity.Normal,
                    x, y, log: false, alone: false);
            }
        }

        // Phase 2: the staff slam, the maze portal and the run to the exit portal. The humanoid stays at 30% life; the
        // treant only emerges after the player comes back through the maze.
        async Task RunMaze(double now)
        {
            boss.BusyUntil = double.PositiveInfinity;   // the phase controller owns the King until phase 3
            await Cast(SlamSkill, "slams his staff");
            await Task.Delay(1000);
            if (!Present()) return;
            await Cast(MazeSkill, "opens the maze");
            await Task.Delay(1000);
            if (!Present()) return;
            var plan = MazePlanFor(conn);
            var entrance = plan?.Entrance ?? MazePoint(conn, exit: false) ?? conn.Layout.Near((_origin.X, _origin.Y + 80));
            await TeleportPlayer(entrance.X, entrance.Y);
            await AddBranchingAffliction(120);
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
            await BecomeTreant(conn.Clock.Elapsed.TotalSeconds);
        }

        async Task AddPortentBuff(double until)
        {
            if (conn.KingPortentStacks <= 0) return;
            if (_portentBuff is { } old)
                await Program.Send(remote, stream, ciphers, InstanceEntities.RemoveBuff(Program.PlayerId, old),
                    "0x01c2 player Portent Vapors refreshed");
            ushort buff;
            lock (conn.Monsters) buff = conn.NextBuff++;
            _portentBuff = buff;
            await Program.Send(remote, stream, ciphers,
                InstanceEntities.AddBuff(Program.PlayerId, buff, PortentVaporsRow, Program.PlayerId,
                    u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0.1, until - conn.Clock.Elapsed.TotalSeconds)),
                    stacks: (ushort)conn.KingPortentStacks),
                $"0x01c1 player Portent Vapors {conn.KingPortentStacks} stack(s)");
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
            await Program.SendMonsterAction(remote, stream, ciphers, conn, id, boss, action,
                () => InstanceProtocol.ActionStarted(id, at.X, at.Y, at.X, at.Y, SlamSkill, Program.MonsterActionSequence, 0x0408),
                "0x01ab King in the Mists slams his staff");
            await Task.Delay(1000);
            if (!Present()) return;
            lock (conn.Monsters) action = ++boss.ActionGeneration;
            await Program.SendMonsterAction(remote, stream, ciphers, conn, id, boss, action,
                () => InstanceProtocol.ActionStarted(id, at.X, at.Y, at.X, at.Y, TreantSkill, Program.MonsterActionSequence, 0x0408),
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
            if (Program.Monsters.Find(Boss3) is not { Supported: true } variety)
            {
                Console.WriteLine($"[gateway] {remote} King in the Mists: {Boss3} is not supported");
                return;
            }
            await Program.SpawnMonster(remote, stream, ciphers, conn, variety, boss.Level, MonsterRarity.Unique,
                at.X, at.Y, name: "The King in the Mists", log: true, alone: true);
        }

        async Task Summon(string varietyId, int count)
        {
            var (x, y) = boss.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            await SummonAt(varietyId, count, x, y);
        }

        async Task SummonAt(string varietyId, int count, int x, int y)
        {
            if (Program.Monsters.Find(varietyId) is not { Supported: true } variety) return;
            for (int i = 0; i < count; i++)
            {
                var at = conn.Layout.Near((x + 6 + i * 4, y + 6 - i * 3));
                await Program.SpawnMonster(remote, stream, ciphers, conn, variety, boss.Level, MonsterRarity.Normal,
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
