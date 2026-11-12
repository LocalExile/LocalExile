using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;

namespace GameServer.Modules.Shrines;

/// <summary>
/// The Cold shrine's effect, the Freezing shrine: GrantedEffects 101 <c>ShrineIceNova</c> while the shrine stands
/// unclaimed and 102 <c>ShrineIceNovaPlayer</c> for the player's buff once it is claimed (both ActiveSkill 78, Ice Nova).
/// Shrines row Cold: 45 s, BuffTemplates 394. The cast is the shrine daemon's (MonsterVarieties 2920,
/// Metadata/Monsters/Daemon/ShrineDaemonCold, AttackSpeed 1000), whose level hit is the nova's damage.
/// <para>Its stat set (101/102, identical): <c>active_skill_base_area_of_effect_radius</c> 26 with
/// <c>active_skill_area_of_effect_radius_+%_final</c> 15, so 29 cells; <c>base_skill_effect_duration</c> 4000; the
/// implicits <c>ice_nova_places_ground_ice</c> (chilled ground under the nova) and <c>force_lite_skill_effects</c>.</para>
/// <para>It draws two pieces, both at the size their own art is made for (<see cref="NovaArtUnits"/> and
/// <see cref="GroundArtUnits"/>): the frozen ground the implicit leaves, placed once and static, and the nova's frost
/// burst above it, cast on the daemon's cadence, which is the effect's pulse.</para>
/// <para>Same shape as the Lightning shrine: while unclaimed the nova is cast on the statue and its cold damage is the
/// player's hazard; once claimed it follows the player, damaging the monsters and chilling them.</para>
/// </summary>
sealed class IceNovaEffect : IShrineEffect
{
    /// <summary>GrantedEffects row of the standing shrine's Ice Nova (<c>ShrineIceNova</c>).</summary>
    internal const int Skill = 101;

    /// <summary>GrantedEffects row of the claimed shrine's Ice Nova (<c>ShrineIceNovaPlayer</c>).</summary>
    internal const int PlayerSkill = 102;

    /// <summary>MonsterVarieties id of the shrine's own daemon (row 2920).</summary>
    internal const string Daemon = "Metadata/Monsters/Daemon/ShrineDaemonCold";

    /// <summary>MiscAnimated 14 `IceNovaEffect` (<c>icenova_02/ice_nova.ao</c>): the frost burst of the nova. The
    /// alternatives are the cracked-ice ring of `IceNovaEffectNew` (3849, `icenova_03`, ±157.653) and the arctic layer of
    /// `IceNovaArctic` (360, ±418.265, cracks below the ground).</summary>
    internal const string NovaArt = "Metadata/Effects/Spells/icenova_02/ice_nova.ao";

    /// <summary>The burst's radius in world units, at the size the client draws the art: <c>Art/Models/Effects/
    /// icenova_02/icenova.sm</c> has a BoundingBox of ±137.7 (~12.7 cells). The mist reaches past those bounds on its own;
    /// enlarging the art to the 30 cells the hit covers magnified that too, which is what drew it across the screen, so
    /// the art is kept at its own size and only the damage keeps the skill's area.</summary>
    internal const float NovaArtUnits = 137.7f;

    /// <summary>Its one clip, from <c>icenova_02/icenova.amd</c>: once, 833 ms.</summary>
    internal const int NovaArtMs = 833;

    /// <summary>World units per cell in the client's tables (250/23).</summary>
    internal const double UnitsPerCell = 250d / 23;

    /// <summary>How long the burst outlives its own clip. An effect object carries no alpha, so the pulse is the gap
    /// between one burst going and the next one coming, not a fade: the art is removed a moment after its clip ends and
    /// the following cast is a new object.</summary>
    internal const double NovaArtMargin = 0.05;

    /// <summary>MiscAnimated 3871 `AfflictionDemonColdSpearGroundDegen`: the frozen ground the skill's
    /// <c>ice_nova_places_ground_ice</c> implicit leaves under the nova. It is the effect's second piece and it does not
    /// pulse: one patch, drawn once, staying while the shrine stands or the buff lasts.</summary>
    internal const string GroundArt =
        "Metadata/Effects/Spells/monsters_effects/League_Affliction/affliction_demon_ice/chilled_ground.ao";

    /// <summary>The ground art's radius: MiscAnimated 3871's <c>__col4</c> is 0 (it is not an area art), so the mesh's
    /// BoundingBox ±244.4 (z -347.615 … 4.65676) is what it is drawn at, scale 1.</summary>
    internal const float GroundArtUnits = 244.4f;

    /// <summary>How far, in cells, the caster may walk before the frozen ground is laid again under them: half its own
    /// radius, so the patch is always the ground he is standing on rather than one he has left behind.</summary>
    internal const double GroundFollowCells = 7;

    /// <summary>The chill the nova leaves, and the duration it falls back to when the skill carries none.</summary>
    internal const int ChillPct = 30;
    internal const double FallbackSeconds = 4;
    internal const double FallbackCastSeconds = 1;
    internal const double FallbackRadius = 26;

    readonly List<(uint Object, double Until)> _arts = [];
    double _nextCast;
    uint _ground;
    (int X, int Y) _groundAt;

    public Task OnSpawn(ShrineContext ctx, ShrineInstance shrine)
    {
        var conn = ctx.Conn;
        int duration = Stat(Own(conn, Skill), "base_skill_effect_duration");
        Console.WriteLine($"[gateway] shrine {shrine.Definition.Id}: Ice Nova {Skill}/{PlayerSkill}, daemon " +
            $"{(Monsters.Find(Daemon) is null ? "MISSING" : "found")}, reaches {Radius(conn):0.##} cells " +
            $"(burst {NovaArtUnits / UnitsPerCell:0.#} cells), chill {ChillSeconds(conn):0.##} s, duration {duration} ms");
        return Task.CompletedTask;
    }

    public async Task Tick(ShrineContext ctx, ShrineInstance shrine)
    {
        var conn = ctx.Conn;
        if (conn.Closed) return;
        double now = ctx.Now;
        bool standing = shrine.State == ShrineState.Inactive;
        var at = standing ? shrine.At : conn.Walk.Position(now);
        double radius = Radius(conn);
        var targets = new List<(uint Id, SpawnedMonster Monster)>();
        uint art = 0;
        lock (conn.Monsters)
        {
            if (conn.LeavingArea)
            {
                _arts.Clear();
                _ground = 0;
                _nextCast = 0;
                return;
            }
            foreach (var ended in _arts.Where(a => a.Until <= now).ToArray())
            {
                _arts.Remove(ended);
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(ended.Object), null));
            }
            if (now < _nextCast) return;
            _nextCast = now + CastSeconds(conn);
            art = QueueNova(conn, at, now);
            // The shrine is two effects: the frozen ground the skill's `ice_nova_places_ground_ice` leaves, which is
            // static and stays for as long as the shrine (or the claimed buff) does, and the nova above it, which is
            // cast once a second and is the pulse.
            EnsureGround(conn, at);
            foreach (var (id, m) in conn.Monsters)
                if (m is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }
                    && Dist(m.Walk.Position(now), at) <= radius)
                    targets.Add((id, m));
        }
        var packet = NovaDamage(conn);
        if (packet.MaxTotal <= 0) return;
        // The standing shrine's nova is the player's hazard; only the claimed one turns on the monsters around him.
        if (standing)
        {
            if (Dist(conn.Walk.Position(now), at) > radius) return;
            int damage = (int)((packet.MinTotal + packet.MaxTotal) / 2);
            if (damage <= 0) return;
            long before = conn.PlayerLife;
            // DamagePlayerHitTyped decides what reaches Life (it drains Ward, Energy Shield and Mana itself) and returns
            // it; the caller applies it, exactly as a monster hit does.
            int lifeDamage = DamagePlayerHitTyped(conn, 0, damage, 0, now, CharacterPlayerStats(conn), packet);
            conn.PlayerLife = (uint)Math.Max(0, before - lifeDamage);
            Console.WriteLine($"[gateway] {conn.Wire.Remote} shrine Ice Nova at ({at.X}, {at.Y}) hits the player for " +
                $"{damage}: life {before} -> {conn.PlayerLife}");
            if (conn.PlayerLife != (uint)before)
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife, conn.PlayerReservedLife),
                    "0x01b1 player hit by a shrine Ice Nova"));
            await SendPlayerEnergyShield(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn);
            // A shrine nova can kill: at zero life the client needs the death screen, exactly as a monster hit's lethal
            // damage does (PlayerDied). Without it the character stays standing at 0 life with no way to act or respawn.
            if (conn.PlayerLife == 0)
                await PlayerDied(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn,
                    $"0x0055 death screen ({conn.League ?? "?"} league, a shrine Ice Nova)");
            return;
        }
        foreach (var (id, m) in targets)
        {
            int dealt;
            lock (conn.Monsters)
            {
                if (m.Dead || m.Life == 0) continue;
                var mitigated = DamageModel.Mitigate(packet.Roll(Random.Shared),
                    type => MonsterHitResistance(m, type, default));
                dealt = MonsterTakesMitigated(m, mitigated, spell: true);
            }
            if (dealt <= 0) continue;
            if (!await HitMonster(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, id, m, dealt, "Ice Nova", log: false))
                continue;
            // The chill lands after the hit that lands it.
            lock (conn.Monsters)
                if (!m.Dead && m.Life > 0)
                    m.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Chill, ChillSeconds(conn), now,
                        n => 0, n => m.Effects[n], 1, ChillPct);
        }
        Console.WriteLine($"[gateway] {conn.Wire.Remote} shrine Ice Nova 0x{art:x} at ({at.X}, {at.Y}) {shrine.State}: " +
            $"radius {radius:0.##}, {targets.Count} monster(s) in it");
    }

    /// <summary>Claiming and expiring both end the art and the ground this effect laid.</summary>
    public Task OnActivation(ShrineContext ctx, ShrineInstance shrine) => Clear(ctx.Conn);

    public Task OnExpired(ShrineContext ctx, ShrineInstance shrine) => Clear(ctx.Conn);

    public Task OnMapUnload(ShrineContext ctx, ShrineInstance shrine)
    {
        _arts.Clear();
        _ground = 0;
        _nextCast = 0;
        return Task.CompletedTask;
    }

    Task Clear(ClientState conn)
    {
        if (conn.Closed || conn.LeavingArea) return Task.CompletedTask;
        lock (conn.Monsters)
        {
            foreach (var art in _arts) conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(art.Object), null));
            _arts.Clear();
            if (_ground != 0)
            {
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(_ground), null));
                _ground = 0;
            }
            _nextCast = 0;
        }
        return Task.CompletedTask;
    }

    /// <summary>The frozen ground under the nova: one object, placed once where the shrine casts from or where the claimed
    /// buff's caster stands, and kept there. It is replaced only when the caster has walked out from under it, which is
    /// the claimed buff following the player; nothing about it changes from tick to tick, so the shrine's pulse is the
    /// nova alone. Called under the connection's monsters lock.</summary>
    void EnsureGround(ClientState conn, (int X, int Y) at)
    {
        if (_ground != 0 && Dist(_groundAt, at) <= GroundFollowCells) return;
        if (_ground != 0) conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(_ground), null));
        _ground = conn.NextObjectId++;
        _groundAt = at;
        var ground = new EffectObject(at.X, at.Y, GroundArt)
        {
            Flags0 = EffectObject.FixedRotation,
            Scale = (float)GroundScale(),
        };
        conn.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(_ground,
            InstanceEntities.ObjectTypeHash(EffectObject.TypePath), ground.Blob()), null));
        conn.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(_ground), null));
    }

    /// <summary>The frozen ground's scale: the ring's own footprint on the ground art's radius (MiscAnimated 3871's
    /// <c>__col4</c> is 0, so the mesh's ±244.4 is what it is drawn at), so the frost fills the circle the pulse draws.</summary>
    static double GroundScale() => NovaArtUnits / GroundArtUnits;

    /// <summary>The nova's ring at the caster, on the art's own radius scaled up to the radius it hits in: the client
    /// grows Ice Nova's ring from the caster out to exactly that radius (the IceNova action's update, 11-skills), so the
    /// ring ends where the damage does. Returned is the object on screen now. Called under the connection's monsters
    /// lock.</summary>
    uint QueueNova(ClientState conn, (int X, int Y) at, double now)
    {
        // Authored size, as the client draws the art: the burst is the effect's own, not a copy of the area it hits.
        return SpawnArt(conn, at.X, at.Y, 1f, now + NovaArtMs / 1000.0 + NovaArtMargin);
    }

    /// <summary>The nova's art object. Called under the connection's monsters lock.</summary>
    uint SpawnArt(ClientState conn, int x, int y, float scale, double until)
    {
        uint id = conn.NextObjectId++;
        var art = new EffectObject(x, y, NovaArt) { Flags0 = EffectObject.FixedRotation, Scale = scale };
        conn.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
            art.Blob()), null));
        conn.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(id), null));
        // Nothing outlives its clip by more than a moment: the cast is 1000 ms, so a longer life would overlap the next
        // nova and read as one permanent cloud instead of a pulse.
        _arts.Add((id, until));
        return id;
    }

    /// <summary>The area the nova reaches, from the skill's own stats: 26 cells raised by its 15% final is 29.9, which
    /// the game reports as 30 (2.6 m base raised to 3 m; PoEDB lists 30). <see cref="SkillArea.Radius"/> truncates the
    /// cell count, so the shrine rounds its own radius rather than inheriting the one-cell loss.</summary>
    static double Radius(ClientState conn)
    {
        var stats = Own(conn, Skill);
        int baseRadius = Stat(stats, "active_skill_base_area_of_effect_radius");
        if (baseRadius <= 0) return FallbackRadius;
        double increased = Stat(stats, "base_skill_area_of_effect_+%") + Stat(stats, "skill_area_of_effect_+%")
            + Stat(stats, "spell_area_of_effect_+%");
        double final = Stat(stats, "active_skill_area_of_effect_radius_+%_final");
        return Math.Round(baseRadius * (1 + increased / 100.0) * (1 + final / 100.0));
    }

    /// <summary>How long the nova's chill lasts: the skill's <c>base_skill_effect_duration</c>.</summary>
    static double ChillSeconds(ClientState conn)
    {
        int ms = Stat(Own(conn, Skill), "base_skill_effect_duration");
        return ms > 0 ? ms / 1000.0 : FallbackSeconds;
    }

    /// <summary>How often the daemon casts: its attack time (1000 ms), or the skill's CastTime when longer.</summary>
    static double CastSeconds(ClientState conn)
    {
        double attack = Monsters.Find(Daemon) is { AttackMs: > 0 } daemon ? daemon.AttackMs / 1000.0 : 1;
        int cast = SkillsFor(conn).CastTimeMs(Skill);
        return Math.Max(attack, cast > 0 ? cast / 1000.0 : FallbackCastSeconds);
    }

    /// <summary>The nova's hit: the daemon's level hit as cold damage.</summary>
    static DamagePacket NovaDamage(ClientState conn)
    {
        if (Monsters.Find(Daemon) is not { } daemon) return DamagePacket.Empty;
        if (Monsters.BaseDamage(daemon, ShrineManager.AreaLevel(conn)) is not { } range) return DamagePacket.Empty;
        return DamagePacket.Of(DamageType.Cold, range.Item1, range.Item2);
    }

    static Dictionary<uint, int> Own(ClientState conn, int skill) =>
        SkillsFor(conn).EffectStats(skill, 1).ToDictionary(s => s.Item1, s => s.Item2);

    static int Stat(IReadOnlyDictionary<uint, int> stats, string name) =>
        Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
}
