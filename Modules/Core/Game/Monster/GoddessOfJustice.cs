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
    // Her actor's skills: the Spirit Mortar and the awakening beam (the official monsters' ids start at 0x4000, KingInMists.Kit).
    const ushort MortarSkillId = 0x4000, AwakenSkillId = 0x4001;

    // How often her loop looks.
    const double TickSeconds = 0.1;

    public static bool IsGoddess(MonsterCatalog.Variety? variety) => variety?.Id == IzaroGoddess.Variety;

    /// <summary>The actor's skill list: her default skill, the Spirit Mortar and the awakening, with the 0x1ab ids they
    /// name. The mortar's action (ActionTypes <c>LbBossSpiritMortar</c>, a projectile action: 0x1419a3c00 → 0x141a0bd30 on
    /// 0x1419f13b0) is the client's own: it plays her volley and fires the mortars, arc, trail and impact, only with a
    /// count on the record (as the Sand Spitter's, 11-skills.md), its <c>monster_projectile_variation</c> 0 or 1
    /// (0x141a0bb50: "Mortar Variation not recognized"), and with <c>distribute_projectiles_over_contact_points</c> the
    /// count is shared among the volly clip's contacts (release-and-repeat.md §6): without it each of the six fired all
    /// five (live). The awakening's (<c>AwakenGargoyle</c>, 0x141991090 → 0x141bf97f0) aims at its target object for its
    /// <c>skill_effect_duration</c> (0x141bf9a20).</summary>
    internal static IReadOnlyList<GrantedSkill>? SkillsFor(ClientState conn, MonsterCatalog.Variety variety, byte level, MonsterAttack attack, GrantedSkill? skill)
    {
        if (!IsGoddess(variety)) return null;
        var catalog = Game.SkillsFor(conn);
        // A spell's cast-time stats: the client's cast-duration path crashed on an actor record with only attack stats
        // (Monsters.cs, the King's Death Effigy).
        GrantedSkill? Spell(ushort id, int effect, IEnumerable<(uint, int)> extra)
        {
            int effectLevel = catalog.MonsterEffectLevel(effect, level);
            var own = (attack with { SkillId = id, Spell = true, Seconds = catalog.CastTimeMs(effect) / 1000.0 }).SkillStats().Concat(extra).ToList();
            var taken = own.Select(s => s.Item1).ToHashSet();
            foreach (string name in MonsterSkillStatsNotSent) if (Stats.Key(name) is uint key) taken.Add(key);
            return catalog.FindByRow(id, effect, effectLevel, [.. catalog.EffectStats(effect, effectLevel).Where(s => !taken.Contains(s.Item1)), .. own]);
        }
        int count = IzaroGoddess.MortarCount;
        List<(uint, int)> mortarStats = [(StatKeys.TotalProjectilesToFire, count), (StatKeys.VirtualTotalProjectiles, count), (StatKeys.VirtualTotalArrows, count)];
        foreach (var (name, value) in new[] { ("monster_projectile_variation", 0), ("distribute_projectiles_over_contact_points", 1) })
            if (Stats.Key(name) is uint key) mortarStats.Add((key, value));
        List<(uint, int)> awakenStats = Stats.Key("skill_effect_duration") is uint duration ? [(duration, (int)(IzaroGargoyles.BeamSeconds * 1000))] : [];
        var list = new List<GrantedSkill>();
        if (skill is not null) list.Add(skill);
        if (Spell(MortarSkillId, IzaroGoddess.MortarEffect, mortarStats) is { } mortar) list.Add(mortar);
        if (Spell(AwakenSkillId, IzaroGoddess.AwakenEffect, awakenStats) is { } awaken) list.Add(awaken);
        Console.WriteLine($"[gateway] the Goddess of Justice's skills: {string.Join(", ", list.Select(s => $"0x{s.Id:x4}"))}");
        return list.Count == 0 ? null : list;
    }

    // What she is doing: the end of her cast or beam; nothing else starts before it.
    sealed class Casting { public double Until; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SpawnedMonster, Casting> Casts = new();

    /// <summary>Her place kept on Izaro's and her volleys while <paramref name="fighting"/> holds.</summary>
    internal static async Task Run(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster goddess,
        SpawnedMonster izaro, Func<bool> fighting)
    {
        var cast = Casts.GetValue(goddess, _ => new());
        double nextVolley = conn.Clock.Elapsed.TotalSeconds + IzaroGoddess.MortarCooldown;
        try
        {
            while (fighting())
            {
                await Task.Delay(TimeSpan.FromSeconds(TickSeconds));
                if (!fighting()) return;
                bool volley;
                lock (conn.Monsters)
                {
                    if (goddess.Dead || izaro.Dead) return;
                    double now = conn.Clock.Elapsed.TotalSeconds;
                    var at = izaro.Walk.Position(now);
                    goddess.Walk.Teleport(at.X, at.Y, now);
                    if (now < cast.Until || conn.Paused || conn.PlayerLife == 0 || InGracePeriod(conn)) continue;
                    volley = now >= nextVolley && Dist(at, conn.Walk.Position(now)) <= IzaroGoddess.MortarReach;
                    if (volley)
                    {
                        nextVolley = now + IzaroGoddess.MortarCooldown;
                        cast.Until = now + IzaroGoddess.VolleySeconds;
                    }
                }
                if (volley) await Volley(remote, stream, ciphers, conn, id, goddess, fighting);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        { if (!conn.Closed) Console.WriteLine($"[gateway] {remote} the Goddess of Justice stopped: {ServerLog.Describe(ex)}"); }
    }

    // Waits until she is free (a volley or a beam plays out), then holds her for `seconds`. False when she is gone.
    static async Task<bool> Hold(ClientState conn, SpawnedMonster goddess, double seconds)
    {
        var cast = Casts.GetValue(goddess, _ => new());
        while (true)
        {
            lock (conn.Monsters)
            {
                if (goddess.Dead) return false;
                double now = conn.Clock.Elapsed.TotalSeconds;
                if (now >= cast.Until) { cast.Until = now + seconds; return true; }
            }
            await Task.Delay(100);
        }
    }

    /// <summary>Her beam to a gargoyle (AwakenGargoyle at it), for <paramref name="seconds"/>; nothing else meanwhile. False
    /// when she is gone.</summary>
    internal static async Task<bool> Awaken(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id,
        SpawnedMonster goddess, uint gargoyle, (int X, int Y) at, double seconds)
    {
        if (!await Hold(conn, goddess, seconds)) return false;
        int action;
        lock (conn.Monsters) action = ++goddess.ActionGeneration;
        return await SendMonsterAction(remote, stream, ciphers, conn, id, goddess, action, () =>
        {
            var from = goddess.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            return [.. InstanceProtocol.ActionEnded(id),
                .. InstanceProtocol.ActionStarted(id, from.X, from.Y, at.X, at.Y, AwakenSkillId, conn.NextActionSequence(), conn.MonsterOrderFlags, targetObjectId: gargoyle)];
        }, $"0x01ab the Goddess of Justice 0x{id:x} beams at gargoyle 0x{gargoyle:x}");
    }

    /// <summary>Her beam stops (its time is up, the gargoyle died, or the trial ended).</summary>
    internal static async Task EndAwaken(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster goddess)
    {
        lock (conn.Monsters) Casts.GetValue(goddess, _ => new()).Until = conn.Clock.Elapsed.TotalSeconds;
        await Send(remote, stream, ciphers, InstanceProtocol.ActionEnded(id), null);
    }

    // Her volley: the Spirit Mortar's action at the player's cell, which the client plays and fires (SkillsFor). The server
    // lands the same mortars there, unseen, one at each of the volly clip's contacts, for their hits.
    static async Task Volley(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, SpawnedMonster goddess, Func<bool> fighting)
    {
        int action;
        lock (conn.Monsters) action = ++goddess.ActionGeneration;
        double started = 0;
        (int X, int Y) aim = default;
        if (!await SendMonsterAction(remote, stream, ciphers, conn, id, goddess, action, () =>
        {
            started = conn.Clock.Elapsed.TotalSeconds;
            var from = goddess.Walk.Position(started);
            aim = conn.Walk.Position(started);
            return [.. InstanceProtocol.ActionEnded(id),
                .. InstanceProtocol.ActionStarted(id, from.X, from.Y, aim.X, aim.Y, MortarSkillId, conn.NextActionSequence(), conn.MonsterOrderFlags)];
        }, $"0x01ab the Goddess of Justice 0x{id:x} lobs her Spirit Mortars")) return;
        var mortar = MortarAttack(conn, goddess);
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        session.Advance();
        double cast = Game.SkillsFor(conn).CastTimeMs(IzaroGoddess.MortarEffect) / 1000.0;
        for (int i = 0; i < IzaroGoddess.MortarCount; i++)
        {
            double release = started + cast * IzaroGoddess.VolleyContacts[Math.Min(i, IzaroGoddess.VolleyContacts.Length - 1)] / IzaroGoddess.VolleySeconds;
            session.Schedule(ProjectileRuntime.Time(release), () => fighting() && conn.PlayerLife > 0 && !InGracePeriod(conn),
                () => Mortar(session, conn, id, goddess, IzaroGoddess.SpiritMortar, aim,
                    new HostileProjectileOffense(id, goddess, mortar, action, "the Goddess of Justice's Spirit Mortar"), hidden: true));
        }
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
        // Not during a volley or a beam.
        if (!await Hold(conn, goddess, 0.5)) return new double[spots.Count];
        double now = conn.Clock.Elapsed.TotalSeconds;
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
        object payload, bool hidden = false)
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
            // The client draws her Spirit Mortars itself (its action), so the server's are not shown.
            HideVisual = hidden,
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
