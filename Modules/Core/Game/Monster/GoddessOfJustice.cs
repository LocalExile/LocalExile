using System.Net.Sockets;
using GameServer.Core.Diagnostics;
using GameServer.Core.Projectiles;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>
/// The Goddess of Justice on Izaro's back (IzaroGoddess): she follows him, turns to the player, lobs her Spirit Mortars
/// and throws her Guardians' mortars. The ordinary AI never moves her (she is out of it for good); this is her whole fight.
/// </summary>
static class GoddessOfJustice
{
    // Her actor's skill for the turn (the official monsters' ids start at 0x4000, KingInMists.Kit).
    const ushort FaceSkillId = 0x4000;

    // How often her loop looks.
    const double TickSeconds = 0.1;

    public static bool IsGoddess(MonsterCatalog.Variety? variety) => variety?.Id == IzaroGoddess.Variety;

    /// <summary>The actor's skill list: her default skill, then the turn, with the 0x1ab id it names.</summary>
    internal static IReadOnlyList<GrantedSkill>? SkillsFor(ClientState conn, MonsterCatalog.Variety variety, byte level, MonsterAttack attack, GrantedSkill? skill)
    {
        if (!IsGoddess(variety)) return null;
        var catalog = Game.SkillsFor(conn);
        int effect = IzaroGoddess.FaceEffect;
        // A spell's cast-time stats: the client's cast-duration path crashed on an actor record with only attack stats
        // (Monsters.cs, the King's Death Effigy).
        var stats = (attack with { SkillId = FaceSkillId, Spell = true, Seconds = catalog.CastTimeMs(effect) / 1000.0 }).SkillStats();
        if (catalog.FindByRow(FaceSkillId, effect, catalog.MonsterEffectLevel(effect, level), stats) is not { } face) return null;
        return skill is null ? [face] : [skill, face];
    }

    // What she is doing: the end of her cast (no turn cuts it).
    sealed class Casting { public double Until; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SpawnedMonster, Casting> Casts = new();

    /// <summary>Her place kept on Izaro's, her turns to the player and her volleys while <paramref name="fighting"/> holds.</summary>
    internal static async Task Run(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster goddess,
        SpawnedMonster izaro, Func<bool> fighting)
    {
        var cast = Casts.GetValue(goddess, _ => new());
        double nextFace = 0, nextVolley = conn.Clock.Elapsed.TotalSeconds + IzaroGoddess.MortarCooldown, facing = double.NaN;
        try
        {
            while (fighting())
            {
                await Task.Delay(TimeSpan.FromSeconds(TickSeconds));
                if (!fighting()) return;
                bool face = false, volley = false;
                double angle;
                lock (conn.Monsters)
                {
                    if (goddess.Dead || izaro.Dead) return;
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    var at = izaro.Walk.Position(now);
                    goddess.Walk.Teleport(at.X, at.Y, now);
                    var player = conn.Walk.Position(now);
                    angle = Math.Atan2(player.Y - at.Y, player.X - at.X);
                    if (now < cast.Until || conn.Paused || conn.PlayerLife == 0 || InGracePeriod(conn)) continue;
                    double turn = double.IsNaN(facing) ? double.PositiveInfinity : Math.Abs(Math.IEEERemainder(angle - facing, 2 * Math.PI));
                    face = now >= nextFace && turn * 180 / Math.PI >= IzaroGoddess.FaceDegrees;
                    volley = now >= nextVolley && Dist(at, player) <= IzaroGoddess.MortarReach;
                    if (volley)
                    {
                        nextVolley = now + IzaroGoddess.MortarCooldown;
                        cast.Until = now + IzaroGoddess.VolleySeconds;
                    }
                    if (face || volley) (nextFace, facing) = (now + IzaroGoddess.FaceSeconds, angle);
                }
                if (face || volley) await Face(remote, stream, ciphers, conn, id, goddess);
                if (volley) await Volley(remote, stream, ciphers, conn, id, goddess, fighting);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} the Goddess of Justice stopped: {ServerLog.Describe(ex)}"); }
    }

    // GoddessOfJusticeFaceLastTarget at the player: the client turns her there at her own pace.
    static async Task Face(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster goddess)
    {
        int action;
        lock (conn.Monsters) action = ++goddess.ActionGeneration;
        await SendMonsterAction(remote, stream, ciphers, conn, id, goddess, action, () =>
        {
            double now = conn.Clock.Elapsed.TotalSeconds;
            var (from, player) = (goddess.Walk.Position(now), conn.Walk.Position(now));
            return [.. InstanceProtocol.ActionEnded(id),
                .. InstanceProtocol.ActionStarted(id, from.X, from.Y, player.X, player.Y, FaceSkillId, conn.NextActionSequence(), conn.MonsterOrderFlags, targetObjectId: PlayerId)];
        });
    }

    // Her volley: the volly clip, a Spirit Mortar at each of its contacts, each to where the player stands at that moment.
    static async Task Volley(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster goddess, Func<bool> fighting)
    {
        double started = conn.Clock.Elapsed.TotalSeconds;
        await Send(remote, stream, ciphers, InstanceProtocol.ActorAnimation(id, (float)IzaroGoddess.VolleySeconds, IzaroGoddess.VolleyAnimation),
            $"0x01d1 the Goddess of Justice 0x{id:x} lobs her Spirit Mortars");
        var mortar = MortarAttack(conn, goddess);
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        session.Advance();
        foreach (double contact in IzaroGoddess.VolleyContacts)
            session.Schedule(ProjectileRuntime.Time(started + contact), () => fighting() && conn.PlayerLife > 0 && !InGracePeriod(conn),
                () => Mortar(session, conn, id, goddess, IzaroGoddess.SpiritMortar, conn.Walk.Position(session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond),
                    new HostileProjectileOffense(id, goddess, mortar, goddess.ActionGeneration, "the Goddess of Justice's Spirit Mortar")));
    }

    // LabyrinthSpiritMortar: fire, its FloatStatsValues of the monster damage at her level, landing round its cell.
    static MonsterAttack MortarAttack(ClientState conn, SpawnedMonster goddess)
    {
        double baseDamage = Monsters.BaseDamage(goddess.Level) ?? 1;
        bool high = Game.SkillsFor(conn).MonsterEffectLevel(IzaroGoddess.MortarEffect, goddess.Level) >= 3;
        var damage = DamagePacket.Of(DamageType.Fire, baseDamage * (high ? 0.8 : 0.6), baseDamage * (high ? 1.2 : 1));
        var projectile = new SkillCatalog.MonsterProjectileInfo(IzaroGoddess.SpiritMortar, IzaroGoddess.MortarSpeed, true, 2) { AreaRadius = IzaroGoddess.MortarRadius };
        return (goddess.Attack ?? new MonsterAttack(SkillCatalog.FirstSkillId, IzaroGoddess.VolleySeconds, IzaroGoddess.MortarReach, damage))
            with { Damage = damage, Spell = true, Scripted = true, Projectile = projectile, Release = null };
    }

    /// <summary>Her Guardians' mortars, one to each spot, as she casts (her channel clip). They hit nothing; the seconds each
    /// flies, after which its circle opens.</summary>
    internal static async Task<double[]> ThrowGuardians(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        SpawnedMonster goddess, IReadOnlyList<(int X, int Y)> spots)
    {
        var cast = Casts.GetValue(goddess, _ => new());
        double now = conn.Clock.Elapsed.TotalSeconds;
        lock (conn.Monsters) cast.Until = Math.Max(cast.Until, now + 0.5);
        await Send(remote, stream, ciphers, [.. InstanceProtocol.ActionEnded(id), .. InstanceProtocol.ActorAnimation(id, 0.5f, IzaroGoddess.SummonAnimation)],
            $"0x01d1 the Goddess of Justice 0x{id:x} calls her Guardians");
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        session.Advance();
        var seconds = new double[spots.Count];
        var label = $"the Goddess of Justice's guardian mortar 0x{id:x}";
        for (int i = 0; i < spots.Count; i++)
        {
            var spot = spots[i];
            (int X, int Y) from;
            lock (conn.Monsters) from = goddess.Walk.Position(now);
            seconds[i] = Math.Max(1, Dist(from, spot)) / Speed();
            session.Schedule(ProjectileRuntime.Time(now), () => !conn.Closed && !conn.LeavingArea,
                () => Mortar(session, conn, id, goddess, IzaroGoddess.GuardianMortar, spot,
                    new ObjectProjectileOffense(label, (_, _) => ProjectileHitResult.Miss) { Quiet = true }));
        }
        return seconds;
    }

    static double Speed() => ProjectileSpeed.CellsPerSecond(IzaroGoddess.MortarSpeed, _ => 0, _ => 0);

    // A mortar from her (her place, the volley's height) to a cell: a damaging one lands its area there.
    static ProjectileUse Mortar(ProjectileSession session, ClientState conn, uint id, SpawnedMonster goddess, string metadata, (int X, int Y) target,
        object payload)
    {
        double now = session.Runtime.Now / (double)ProjectileRuntime.QuantaPerSecond;
        var from = ProjectileSession.Vector(goddess.Walk.Position(now));
        var to = ProjectileSession.Vector(target);
        double speed = Speed(), distance = Math.Max(1, (to - from).Length);
        bool damages = payload is HostileProjectileOffense;
        var profile = new ProjectileProfile(metadata)
        {
            Speed = speed, Radius = 2, Count = 1, Lobbed = true, Range = distance, DirectDamage = false,
            EndArea = damages ? new ProjectileAreaRecipe("Goddess of Justice mortar", IzaroGoddess.MortarRadius) : null,
            EndAreaReasons = damages ? new HashSet<ProjectileEndReason> { ProjectileEndReason.Landing, ProjectileEndReason.Terrain } : null,
        };
        var presentation = (MonsterProjectilePresentation(Game.SkillsFor(conn).Clips(metadata), distance / speed) ?? new())
            with { LaunchHeight = new ContactPoint(0, 0, IzaroGoddess.VolleyHeight).HeightSteps };
        var owner = session.ActorKey(id, goddess);
        return new ProjectileUse(profile, owner, owner, 2, from, to)
        {
            Payload = payload switch
            {
                HostileProjectileOffense hostile => hostile with { Presentation = presentation },
                ObjectProjectileOffense other => other with { Presentation = presentation },
                _ => payload,
            },
        };
    }
}
