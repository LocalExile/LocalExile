using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;

namespace GameServer.Modules.Shrines;

/// <summary>
/// The Lightning shrine's effect: the Shrines row 1 (Lightning) summons MonsterVarieties 2918
/// (Metadata/Monsters/Daemon/ShrineDaemonLightning), whose GrantedEffects 1067 <c>ShrineShockNova</c> is the Shock Nova
/// its buff promises ("Shock Novas happen around you", BuffDefinitions 248 <c>shrine_lightning</c>). While the buff is up
/// the nova is cast around the player once per the daemon's attack time (MonsterVarieties AttackSpeed 1000).
/// <para>Shock Nova's own numbers are the skill's stat set (GrantedEffectStatSets 1075):
/// <c>active_skill_area_of_effect_radius_+%_final</c> 100 (SkillArea doubles the generic 12-cell radius),
/// <c>shock_nova_places_X_ground_lightning_patches</c> 5 and <c>base_skill_effect_duration</c> 4000, and its cast time
/// (GrantedEffects CastTime 750) is how long its art plays. The shrine skill carries no damage stats of its own: a
/// monster spell's damage is its variety's level hit (Monsters.BaseDamage), taken as lightning damage, the skill's element.
/// The nova hits hostile monsters only: the shrine's own effect never damages the player who used it.</para>
/// <para>The nova is drawn with the client's own Shock Nova art scaled to the radius it hits in, and leaves four seconds
/// of shocked ground (BuffDefinitions 232 <c>ground_lightning_shock</c>, 15% <c>base_damage_taken_+%_from_shock</c>) on a
/// ring around the caster: a monster standing in a patch is kept under that shock, and the player standing in one shows
/// the client's Shocked Ground icon.</para>
/// <para>Not modelled: Shock Nova's two components (an inner ring and the larger nova, which can both hit one monster);
/// the hit is one area hit, and each monster in it takes it once per cast.</para>
/// </summary>
sealed class ShockNovaEffect : IShrineEffect
{
    /// <summary>GrantedEffects row of the Lightning shrine's Shock Nova (<c>ShrineShockNova</c>).</summary>
    internal const int Skill = 1067;

    /// <summary>MonsterVarieties row of the shrine's own daemon: its level hit is the nova's damage, its attack time the
    /// cast cadence.</summary>
    internal const string Daemon = "2918";

    /// <summary>GroundEffectTypes 7 Lightning's BuffDefinitions row: 232 <c>ground_lightning_shock</c> ("Shocked Ground").</summary>
    internal const ushort GroundBuffRow = 232;

    /// <summary>Stat 16700 <c>base_damage_taken_+%_from_shock</c>, the shocked ground's own value (15% base).</summary>
    internal const int ShockPct = 15;

    /// <summary>The cast's two arts as the client draws them for a MONSTER's Shock Nova, which is what a shrine casts:
    /// MiscAnimated 5745 `MonsterShockNovaOuter` (the wide ring) and 5746 `MonsterShockNovaInner` (its inner pass). The
    /// player gem's pair (`ShockNovaScaledEffect` 3350 / `ShockNovaAlsoScaledEffect` 3351) is a different look, and
    /// `shock_nova/new/shock_nova.ao` was not Shock Nova at all: that file is 2546 `BetrayalTaserNet`.</summary>
    internal const string NovaArt = "Metadata/Effects/Spells/monsters_effects/act1/goatman/shock_nova_accurate.ao";

    /// <summary>The inner pass of that pair (<see cref="NovaArt"/>), drawn with the outer ring.</summary>
    internal const string NovaDonutArt = "Metadata/Effects/Spells/monsters_effects/act1/goatman/shock_nova_02.ao";

    /// <summary>How long the nova art plays, from the client's own rig: `Art/Models/Effects/shock_nova_accurate/shock_nova.amd`
    /// has one `animate` clip of 1000 ms, played once (the donut's `shock_donut.amd` is the same). It is very nearly the
    /// cast cadence, so removing the object on the cast interval took it away on the frame its clip ended, every second:
    /// the art is kept a margin past its clip instead, and two novae overlap by that margin.</summary>
    internal const int NovaArtMs = 1000;

    /// <summary>The margin a nova object outlives its clip by.</summary>
    internal const double NovaArtMargin = 0.25;

    /// <summary>BISECT: whether the shocked ground is placed at all. Off while the client desyncs on this shrine's
    /// stream, so the nova art and its hit can be played without any of the ground the shrine lays. A method rather than a
    /// constant, so the code it guards stays compiled while it is off.</summary>
    internal static bool GroundEnabled() => true;

    /// <summary>The Shocked Ground icon the client draws on the player for standing in a patch (BuffDefinitions 232). Off:
    /// the shrine desynced the client while it was sent, and that buff write is the only thing the ground does that the
    /// art and the monster shock do not. The ground itself, and what it does to the monsters in it, stays on.</summary>
    internal static bool PlayerGroundIcon() => false;

    /// <summary>GroundEffects' lightning art (GroundEffectTypes Lightning, its first variation).</summary>
    internal const string GroundArt = "Metadata/Effects/Spells/ground_effects/lightning/lightning_ground_01.ao";

    /// <summary>GroundEffectTypes Lightning's patch size in cells, as BurningGround reads Fire's 4.</summary>
    internal const int GroundRadius = 4;

    /// <summary>How far from the caster the patches land: a ring around the player, the way the nova is cast around them.</summary>
    internal const int GroundRing = 6;

    /// <summary>The generic area radius SkillArea scales when the skill exports none (SkillArea.BaseRadius).</summary>
    internal const int GenericRadius = 12;

    internal const double FallbackSeconds = 4;
    internal const double FallbackCastSeconds = .75;

    /// <summary>How long the ground's shock lasts each time it is refreshed: long enough to bridge the tick that
    /// refreshes it, so leaving the patch clears it shortly after.</summary>
    internal const double GroundShockSeconds = 1;

    /// <summary>One patch: where it lies, when it ends (connection clock) and its art object.</summary>
    sealed class Patch
    {
        public (int X, int Y) At;
        public double Until;
        public uint Object;
    }

    /// <summary>One cast's nova art: the object drawn and when it is removed (its clip is the skill's cast time).</summary>
    sealed class Visual
    {
        public uint Object;
        public double Until;
    }

    readonly List<Patch> _patches = [];
    readonly List<Visual> _novas = [];
    double _nextCast;
    ushort _buff;
    double _buffUntil;

    /// <summary>One tick: expire finished patches and nova arts, keep each monster's shocked ground in step with where it
    /// stands, show the player's Shocked Ground buff, and cast the nova on the daemon's cadence.</summary>
    public async Task Tick(ShrineContext ctx, ShrineInstance shrine)
    {
        var conn = ctx.Conn;
        if (conn.Closed) return;
        double now = ctx.Now;
        var targets = new List<(uint Id, SpawnedMonster M)>();
        uint nova = 0;
        double radius = 0;
        bool cast = false;
        lock (conn.Monsters)
        {
            // A connection that has left the area keeps none of it: the patches' and novae's objects are gone with the area.
            if (conn.LeavingArea)
            {
                _patches.Clear();
                _novas.Clear();
                _nextCast = 0;
                _buff = 0;
                _buffUntil = 0;
                return;
            }
            foreach (var ended in _patches.Where(p => p.Until <= now).ToArray())
            {
                _patches.Remove(ended);
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(ended.Object), null));
            }
            foreach (var ended in _novas.Where(v => v.Until <= now).ToArray())
            {
                _novas.Remove(ended);
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(ended.Object), null));
            }
            // Shocked ground: a monster standing in a patch is kept under a 15% shock (base_damage_taken_+%_from_shock),
            // refreshed only when it has run low, so its buff is not re-sent every tick.
            foreach (var (_, m) in conn.Monsters)
                if (m is { Hostile: true, Dead: false, Life: > 0 }
                    && _patches.Any(p => p.Until > now && Dist(p.At, m.Walk.Position(now)) <= GroundRadius)
                    && (m.Ailments.Magnitude(MonsterElementalAilments.Kind.Shock, now) < ShockPct
                        || m.Ailments.Remaining(MonsterElementalAilments.Kind.Shock, now) < .4))
                    m.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Shock, GroundShockSeconds, now,
                        n => 0, n => m.Effects[n], 1, ShockPct);
            MaintainPlayerBuff(conn, now);
            if (now < _nextCast) return;
            cast = true;
            _nextCast = now + CastSeconds(conn);
            // While the shrine is unclaimed the nova is cast around the shrine itself, which is what the standing
            // shrine does; once claimed the shrine's own instance is gone and this one follows the player.
            var at = shrine.State == ShrineState.Inactive ? shrine.At : conn.Walk.Position(now);
            double seconds = Seconds(conn);
            var own = Own(conn);
            radius = SkillArea.Radius(GenericRadius, n => Stat(own, n), spell: true);
            nova = Nova(conn, at, radius, now);
            // BISECT: the shocked ground (its art objects, the shock they keep on monsters and the player's ground buff)
            // is off while the client desyncs; the nova art and its hit still run. Remove this guard to bring it back.
            if (GroundEnabled()) Place(conn, at, now, seconds);
            foreach (var (id, m) in conn.Monsters)
                if (m is { Hostile: true, Dead: false, Life: > 0, Invulnerable: false }
                    && Dist(m.Walk.Position(now), at) <= radius) targets.Add((id, m));
            Console.WriteLine($"[gateway] {conn.Wire.Remote} shrine Shock Nova 0x{nova:x} at ({at.X}, {at.Y}): " +
                $"radius {radius}, {targets.Count} monster(s) in it");
        }
        if (!cast) return;
        var packet = NovaDamage(conn);
        // The standing shrine's nova is a hazard for the player, not for the monsters it guards: an unclaimed shrine
        // damages whoever walks into it, and only the claimed one turns its nova on the monsters.
        if (shrine.State == ShrineState.Inactive)
        {
            if (packet.MaxTotal <= 0) return;
            // The standing shrine casts on itself, so the reach is measured from the statue (`at` is the caster's cell
            // inside the tick's lock; recompute the radius here where that scope has closed).
            var statue = shrine.At;
            double reach = SkillArea.Radius(GenericRadius, n => Stat(Own(conn), n), spell: true);
            if (Dist(conn.Walk.Position(now), statue) > reach) return;
            int damage = (int)((packet.MinTotal + packet.MaxTotal) / 2);
            if (damage <= 0) return;
            long before = conn.PlayerLife;
            // DamagePlayerHitTyped only decides what reaches Life (it drains Ward, Energy Shield and Mana itself); the
            // caller applies the returned amount, exactly as a monster hit does. Discarding it is why a hit that emptied
            // the player's shield never touched his life.
            int lifeDamage = DamagePlayerHitTyped(conn, 0, damage, 0, now, CharacterPlayerStats(conn), packet);
            conn.PlayerLife = (uint)Math.Max(0, before - lifeDamage);
            Console.WriteLine($"[gateway] {conn.Wire.Remote} shrine Shock Nova at ({statue.X}, {statue.Y}) hits the player for " +
                $"{damage}: life {before} -> {conn.PlayerLife}");
            if (conn.PlayerLife != (uint)before)
                conn.Minions.OutputQueue.Add(new(InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife, conn.PlayerReservedLife),
                    "0x01b1 player hit by a shrine Shock Nova"));
            // The hit is not the player's own, so tell the client what it did to the shield pool as well: without this the
            // shield never moves on the panel and a hit that only drained energy shield looked like it did nothing.
            await SendPlayerEnergyShield(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn);
            return;
        }
        if (packet.MaxTotal > 0)
            foreach (var (id, m) in targets)
            {
                int damage;
                lock (conn.Monsters)
                {
                    if (m.Dead || m.Life == 0) continue;
                    var mitigated = DamageModel.Mitigate(packet.Roll(Random.Shared),
                        type => MonsterHitResistance(m, type, default));
                    damage = MonsterTakesMitigated(m, mitigated, spell: true);
                }
                if (damage <= 0) continue;
                if (!await HitMonster(conn.Wire.Remote, conn.Wire.Stream, conn.Wire.Ciphers, conn, id, m, damage,
                        "Shock Nova", log: false)) continue;
                // The shock lands after the hit that lands it, so the hit does not gain from its own shock.
                lock (conn.Monsters)
                    if (!m.Dead && m.Life > 0)
                        m.Ailments.WithoutDamage(MonsterElementalAilments.Kind.Shock, Seconds(conn), now,
                            n => 0, n => m.Effects[n], 1, ShockPct);
            }
    }

    /// <summary>The buff is expiring: the ground, its shocks and the nova arts go with it.</summary>
    /// <summary>The shrine was claimed while this instance watched it: the ground and novae of its unclaimed state go.</summary>
    public Task OnActivation(ShrineContext ctx, ShrineInstance shrine) => Clear(ctx.Conn);

    public Task OnExpired(ShrineContext ctx, ShrineInstance shrine) => Clear(ctx.Conn);

    Task Clear(ClientState conn)
    {
        if (conn.Closed || conn.LeavingArea) return Task.CompletedTask;
        lock (conn.Monsters)
        {
            foreach (var patch in _patches) conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(patch.Object), null));
            foreach (var nova in _novas) conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(nova.Object), null));
            _patches.Clear();
            _novas.Clear();
            if (_buff != 0)
            {
                conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, _buff),
                    $"0x01c2 player shocked ground buff {_buff} ends"));
                _buff = 0;
                _buffUntil = 0;
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>The nova's art object at the caster, scaled to the radius it hits in. Its clip is the skill's cast time,
    /// then it is removed. Called under the connection's monsters lock.</summary>
    uint Nova(ClientState conn, (int X, int Y) at, double radius, double now)
    {
        // The monster skill's two arts at the cast's radius. These rows carry no `__col4` radius (0), so they are not
        // scaled to the area the way the gem's pair is: `Art.ArtRadius` returns null and the art plays at its own size.
        uint first = 0;
        foreach (string piece in (string[])[NovaArt, NovaDonutArt])
        {
            uint id = conn.NextObjectId++;
            var art = new EffectObject(at.X, at.Y, piece)
            {
                Flags0 = EffectObject.FixedRotation,
                Scale = Art.ArtRadius(piece) is int native and > 0 ? (float)(radius * (250d / 23) / native) : 1,
            };
            conn.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                art.Blob()), null));
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.EnterWakeZone(id), null));
            _novas.Add(new() { Object = id, Until = now + NovaArtMs / 1000.0 + NovaArtMargin });
            if (first == 0) first = id;
        }
        return first;
    }

    /// <summary>The five patches around the caster (the skill's <c>shock_nova_places_X_ground_lightning_patches</c>); a
    /// cast on a cell that already has a live patch refreshes it instead of stacking a second one.</summary>
    void Place(ClientState conn, (int X, int Y) at, double now, double seconds)
    {
        int count = Math.Max(0, Stat(Own(conn), "shock_nova_places_X_ground_lightning_patches"));
        double until = now + seconds;
        // A ring that is still down and still around the caster is refreshed where it lies: its objects stay with the
        // client and only their lifetime moves. Laying a fresh five every cast meant five 0x02bb creates a second, up to
        // twenty live ground objects, and the client desynced on the stream while walking through them.
        if (count > 0 && _patches.Count == count && _patches.TrueForAll(p => p.Until > now)
            && Dist(_patches[0].At, at) <= GroundRing * 2)
        {
            foreach (var patch in _patches) patch.Until = Math.Max(patch.Until, until);
            Console.WriteLine($"[gateway] {conn.Wire.Remote} shrine Shock Nova at ({at.X}, {at.Y}): " +
                $"the {count} shocked ground patches are refreshed for {seconds:0.#} s");
            return;
        }
        // A ring that expired, or that the caster walked out of, is replaced outright: the ground follows the caster
        // rather than trailing behind him.
        foreach (var previous in _patches)
            conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(previous.Object), null));
        _patches.Clear();
        for (int i = 0; i < count; i++)
        {
            double angle = Math.PI * 2 * i / count;
            int x = at.X + (int)Math.Round(Math.Cos(angle) * GroundRing);
            int y = at.Y + (int)Math.Round(Math.Sin(angle) * GroundRing);
            uint id = conn.NextObjectId++;
            _patches.Add(new() { At = (x, y), Until = until, Object = id });
            var art = new EffectObject(x, y, GroundArt) { Flags0 = EffectObject.FixedRotation };
            conn.Minions.OutputQueue.Add(new(InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(EffectObject.TypePath),
                art.Blob()), null));
        }
        Console.WriteLine($"[gateway] {conn.Wire.Remote} shrine Shock Nova at ({at.X}, {at.Y}): " +
            $"{count} shocked ground patches laid for {seconds:0.#} s");
    }

    /// <summary>The player's Shocked Ground icon (BuffDefinitions 232) while they stand in a patch, with the time left of
    /// it; a refreshed patch is a new instance, as the shrine buffs replace theirs.</summary>
    void MaintainPlayerBuff(ClientState conn, double now)
    {
        if (!PlayerGroundIcon()) return;
        var here = conn.Walk.Position(now);
        var patch = _patches.Where(p => p.Until > now && Dist(p.At, here) <= GroundRadius).MaxBy(p => p.Until);
        if (patch is not null && (_buff == 0 || Math.Abs(patch.Until - _buffUntil) > .25))
        {
            if (_buff != 0)
                conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, _buff), null));
            _buff = (ushort)conn.NextBuff++;
            _buffUntil = patch.Until;
            conn.Minions.OutputQueue.Add(new(InstanceEntities.AddBuff(PlayerId, _buff, GroundBuffRow, PlayerId,
                u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, patch.Until - now))),
                $"0x01c1 player shocked ground buff {_buff} ({patch.Until - now:0.#} s)"));
        }
        else if (patch is null && _buff != 0)
        {
            conn.Minions.OutputQueue.Add(new(InstanceEntities.RemoveBuff(PlayerId, _buff),
                $"0x01c2 player shocked ground buff {_buff} ends"));
            _buff = 0;
            _buffUntil = 0;
        }
    }

    /// <summary>How long a patch lasts: the skill's <c>base_skill_effect_duration</c> (4000 ms).</summary>
    static double Seconds(ClientState conn)
    {
        int ms = Stat(Own(conn), "base_skill_effect_duration");
        return ms > 0 ? ms / 1000.0 : FallbackSeconds;
    }

    /// <summary>How often the daemon casts, and how long its art plays: its attack time (MonsterVarieties AttackSpeed
    /// 1000 → 1 s), or the effect's own cast time when that is longer.</summary>
    static double CastSeconds(ClientState conn)
    {
        double attack = Monsters.Find(Daemon) is { AttackMs: > 0 } daemon ? daemon.AttackMs / 1000.0 : 1;
        int cast = SkillsFor(conn).CastTimeMs(Skill);
        return Math.Max(attack, cast > 0 ? cast / 1000.0 : FallbackCastSeconds);
    }

    /// <summary>The nova's hit: the daemon's level hit as lightning damage (the shrine skill carries no damage stats).</summary>
    static DamagePacket NovaDamage(ClientState conn)
    {
        if (Monsters.Find(Daemon) is not { } daemon) return DamagePacket.Empty;
        if (Monsters.BaseDamage(daemon, ShrineManager.AreaLevel(conn)) is not { } range) return DamagePacket.Empty;
        return DamagePacket.Of(DamageType.Lightning, range.Item1, range.Item2);
    }

    static Dictionary<uint, int> Own(ClientState conn) =>
        SkillsFor(conn).EffectStats(Skill, 1).ToDictionary(s => s.Item1, s => s.Item2);

    static int Stat(IReadOnlyDictionary<uint, int> stats, string name) =>
        Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
}
