using GameServer.Core.Protocol.Instance;
using GameServer.Core.Projectiles;
using GameServer.Core.World;
using static Game;

/// <summary>
/// The Shaper (MonsterVarieties <c>Metadata/Monsters/AtlasBosses/TheShaperBoss</c>, The Shaper's Realm, level 84; and
/// <c>TheShaperBossUberElder</c> in the Uber Elder arena). His own GrantedEffectsKeys are the kit played here: the melee
/// (481), the Celestial Beam (6093, a three-second degen), the accelerating projectiles (6106), the flicker slam (6091),
/// the retreat (6089) and the bullet hell (6111) that every life threshold opens behind his immunity shield.
/// <para>Damage comes from each effect's own stat set at the area's level (<see cref="AtlasBosses.Attack"/> and
/// <see cref="AtlasBosses.DamageOverTime"/>), so the numbers move with the fight's level instead of being written here.
/// The melee keeps the variety's own attack time and reach (1755 ms, 6-12 units); everything else uses the clip's cast
/// time, which is what keeps the fight paced like the original's.</para>
/// <para>Each skill is played as the client's own action, so the client animates it and draws its art; the server adds
/// what only it knows: where the hit lands and how much it takes. The rig's contact keyframes say when and where that
/// is (Agents/docs/game/shaper.md).</para>
/// </summary>
sealed class ShaperFight(uint id, SpawnedMonster boss, ClientState conn) : AtlasBossFight(id, boss, conn)
{
    protected override string Name => "[Shaper]";
    protected override ushort ImmunityRow => AtlasBosses.ShaperImmunityRow;
    protected override string ShieldArt => AtlasBosses.ShaperShieldArt;
    protected override string ShieldOffArt => AtlasBosses.ShaperShieldOffArt;
    // The arena's entry point is the middle of the platform the fight happens on: where the Shaper blinks back to for an
    // intermission, away from the player's corner.
    // The blink of an intermission goes to the arena's middle marker, not to the layout's spawn (the map entry, 1050
    // cells away from where he actually stands).
    (int X, int Y)? stage;
    protected override (int X, int Y) Stage(ClientState conn) => stage ??= ArenaCentre(conn, boss);

    double nextMelee, nextBeam, nextVolley, nextFlicker, nextBlink, nextChase;
    // The Celestial Beam: the line from the Shaper to the point he aimed at keeps burning for the skill's own three
    // seconds (base_skill_effect_duration 3000). The player is free to walk out of it, which is the whole mechanic.
    double beamUntil, beamNextTick;
    (int X, int Y) beamFrom, beamAt;
    MonsterAttack? beamTick;
    // A skill whose hit lands later than its cast: the flicker slam's impact, on the clip's contact.
    readonly List<(double At, (int X, int Y) Where, double Radius, MonsterAttack Attack)> impacts = [];
    // A threshold's intermission: the shield, the blink to the middle, then three bullet-hell novas.
    bool intermission;
    int waves;
    double waveAt;

    protected override async Task Ticks(ClientState conn, double now)
    {
        foreach (var impact in impacts.Where(i => i.At <= now).ToArray())
        {
            impacts.Remove(impact);
            Console.WriteLine($"[gateway] {conn.Wire.Remote} [Shaper] the slam lands at ({impact.Where.X}, {impact.Where.Y}), radius {impact.Radius}");
            await Effect(conn, impact.Where, AtlasBosses.FlickerSlamArt, 2.5);
            foreach (var player in Watchers())
                if (Distance(player.Walk.Position(now), impact.Where) <= impact.Radius)
                    await Hit(player, impact.Attack, impact.Where);
        }
        if (beamTick is null || now >= beamUntil || now < beamNextTick) return;
        beamNextTick = now + 0.5;
        Console.WriteLine($"[gateway] {conn.Wire.Remote} [Shaper] the beam burns from ({beamFrom.X}, {beamFrom.Y}) to ({beamAt.X}, {beamAt.Y})");
        foreach (var player in Watchers())
        {
            var here = player.Walk.Position(now);
            Console.WriteLine($"[gateway] {conn.Wire.Remote} [Shaper] tick: player at ({here.X}, {here.Y}), " +
                $"line ({beamFrom.X}, {beamFrom.Y}) -> ({beamAt.X}, {beamAt.Y}), " +
                $"{BeamOffset(here, beamFrom, beamAt):F1} cells from the line");
            if (OnBeam(here, beamFrom, beamAt)) await Hit(player, beamTick, beamAt);
        }
    }

    protected override async Task Act(ClientState conn, double now)
    {
        var watchers = Watchers();
        if (watchers.Length == 0) return;
        var pos = boss.Walk.Position(now);
        var target = watchers.MinBy(p => Distance(pos, p.Walk.Position(now)))!;
        var player = target.Walk.Position(now);
        double distance = Distance(pos, player);

        if (distance <= Variety.AttackRange + 1 && now >= nextMelee)
        {
            nextMelee = now + Variety.AttackMs / 1000.0 + 0.4;
            Swing(conn, AtlasBosses.ShaperMeleeEffect);
            return;
        }
        // Only within the beam's reach: the skill has no range stat, and a beam cast at a player who just walked in at
        // the area's entrance drew a line across the whole map, so everyone standing on it was hit (live 2026-11-14).
        if (now >= nextBeam && distance <= AtlasBosses.BeamRange)
        {
            nextBeam = now + 15;
            await Beam(conn, player, now);
            return;
        }
        if (now >= nextFlicker && distance is > 14 and < 70)
        {
            nextFlicker = now + 19;
            await FlickerSlam(conn, player, now);
            return;
        }
        if (now >= nextVolley)
        {
            nextVolley = now + 8;
            await Volley(conn, player, now);
            return;
        }
        if (distance <= 10 && now >= nextBlink)
        {
            nextBlink = now + 12;
            await Retreat(conn, player, now);
            return;
        }
        if (distance > Variety.AttackRange && now >= nextChase)
        {
            nextChase = now + 0.75;
            double fraction = Math.Clamp((distance - Variety.AttackRange + 1) / distance, 0, 1);
            var chase = ((int)Math.Round(pos.X + (player.X - pos.X) * fraction),
                (int)Math.Round(pos.Y + (player.Y - pos.Y) * fraction));
            await Move(conn, conn.Layout.Near(chase), now);
        }
    }

    /// <summary>The Celestial Beam: the Shaper casts his beam at the player's feet (his own action, so the client plays
    /// the cast and draws the beam), and the ground between him and that point burns for
    /// <see cref="BeamBurnSeconds"/>, its damage the skill's own fire/cold/lightning degen over each half second.</summary>
    async Task Beam(ClientState conn, (int X, int Y) at, double now)
    {
        var dot = AtlasBosses.DamageOverTime(conn, AtlasBosses.CelestialBeamEffect, boss.Level);
        if (dot.MaxTotal <= 0) return;
        beamFrom = boss.Walk.Position(now);
        beamAt = at;
        // The charge is the rig's beam_start (2500 ms, its contact the last frame): the client plays the clip and
        // creates the beam when the cast ends, and the beam stands for the skill's own three seconds
        // (base_skill_effect_duration). The burn is those same three seconds.
        // The ground burns with the effect: the client creates the skill's effect (its orb, then the ray, and the
        // zone_* ground under it) when the cast starts, and keeps it for the skill's own three seconds. The burn is
        // that zone, so it starts with the cast and ends with the effect (live 2026-11-14: the damage came while the
        // orb was up and stopped while the beam was still standing).
        beamUntil = now + BeamChargeSeconds + BeamBurnSeconds;
        beamNextTick = now + BeamChargeSeconds;
        beamTick = new MonsterAttack(AtlasBosses.Skill(Variety, AtlasBosses.CelestialBeamEffect), 0.5, 0, dot.Scale(0.5))
        { Spell = true, Scripted = true };
        // The beam is his for the whole of it: the charge and the three seconds the ground burns. He does not start
        // another skill while his own beam is still standing (live 2026-11-14: he cast over it at once).
        lock (conn.Monsters) boss.BusyUntil = Math.Max(boss.BusyUntil, now) + BeamChargeSeconds + BeamBurnSeconds;
        lock (conn.Monsters) busyUntil = Math.Max(busyUntil, now) + BeamChargeSeconds + BeamBurnSeconds;
        // The action, not a pose: the client plays the cast (the rig's beam_start) and draws the beam itself. A 0x1b2
        // pose on its own left the Shaper standing there with nothing showing at all (live 2026-11-14). The release
        // fraction puts the action's contact on the clip's last frame, where the beam lands.
        // The action is the skill's own cast time (1000 ms): the client stretches the rig's beam_start (2500 ms) to
        // it, so the charge is a second and the contact (the clip's last frame) lands at its end. The beam itself is
        // the skill's effect, which the client creates when the cast lands and keeps for the skill's own three
        // seconds (base_skill_effect_duration 3000): its rig is a 500 ms orb, then the looping beam_idle_01 that is the
        // ray. The ground burns for exactly those three seconds.
        var beam = Attack(conn, AtlasBosses.CelestialBeamEffect) with
        {
            Seconds = Game.SkillsFor(conn).CastTimeMs(AtlasBosses.CelestialBeamEffect) / 1000.0,
            Release = new MonsterRelease("beam_start", 1.0, null, false),
            NoContact = true,
        };
        SwingWith(conn, beam, AtlasBosses.CelestialBeamEffect);
        // The client draws the beam itself (the skill's own effect, placed and aimed by the client), so the server draws
        // no second one. The actor's own clip is the rig's beam_start, and the client does not pick it for this skill, so
        // the server names it: the Shaper raises his arms for the whole charge (live 2026-11-14: he stood still).
        if (AtlasBosses.ClipFor(conn, Variety, AtlasBosses.CelestialBeamEffect) is { Index: < uint.MaxValue } clip)
            await All(InstanceProtocol.AnimationUpdate(id, 0, clip.Index, 1f));
    }

    /// <summary>How long the Shaper charges before the beam lands (the skill's own cast time).</summary>
    const double BeamChargeSeconds = 1.0;

    /// <summary>How long the beam's ground keeps burning after the beam itself ends (the skill's own
    /// base_skill_effect_duration).</summary>
    const double BeamBurnSeconds = 3;

    /// <summary>How far a cell lies from the beam's line (cells), the same measure the hit test uses.</summary>
    static double BeamOffset((int X, int Y) at, (int X, int Y) from, (int X, int Y) to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.5) return Distance(at, from);
        double t = Math.Clamp(((at.X - from.X) * dx + (at.Y - from.Y) * dy) / (length * length), 0, 1);
        var on = ((int)Math.Round(from.X + dx * t), (int)Math.Round(from.Y + dy * t));
        return Distance(at, on);
    }

    /// <summary>Whether a cell lies on the beam: the line from the Shaper to the point he aimed at, as wide as the beam
    /// is. The data gives the beam no width, so the width is the development figure.</summary>
    static bool OnBeam((int X, int Y) at, (int X, int Y) from, (int X, int Y) to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.5) return Distance(at, from) <= AtlasBosses.BeamWidthCells;
        double t = Math.Clamp(((at.X - from.X) * dx + (at.Y - from.Y) * dy) / (length * length), 0, 1);
        var on = ((int)Math.Round(from.X + dx * t), (int)Math.Round(from.Y + dy * t));
        // The beam is a wide straight band, not a line and not a flare (the player checked it against the original): the
        // whole width of it burns, and walking out of that width is how you live. The data gives the beam no width, so
        // the width is our figure.
        return Distance(at, on) <= AtlasBosses.BeamWidthCells;
    }

    /// <summary>The accelerating projectiles: the Shaper throws three orbs, one per contact of his cast (the rig's
    /// spellcast2 has three), each from the hand that throws it.</summary>
    async Task Volley(ClientState conn, (int X, int Y) player, double now)
    {
        // The action is the rig's spellcast2 (2133 ms, the clip the client plays whole): the three throws land at the
        // clip's own contacts, 433, 966 and 1533 ms in.
        var attack = Attack(conn, AtlasBosses.AcceleratingProjectilesEffect);
        // The action and its first orb: the pool's own contact (433 ms of the 2133 ms cast) and the point it carries.
        SwingWith(conn, attack, AtlasBosses.AcceleratingProjectilesEffect);
        // The other two: the rig's second and third contact (966 and 1533 ms), each from its own hand. The native throws
        // one orb per contact; the server's single release showed one orb and two empty throws (live 2026-11-14).
        var (remote, stream, ciphers) = conn.Wire;
        var session = ProjectilesFor(remote, stream, ciphers, conn);
        session.Advance();
        int action;
        lock (conn.Monsters) action = boss.ActionGeneration;
        foreach (var (ms, point) in AtlasBosses.ShaperProjectileContacts.Skip(1))
        {
            // The contact is a moment of the cast, measured from its start, and the cast starts now.
            var shot = attack with
            {
                Release = new MonsterRelease("spellcast2", ms / (double)AtlasBosses.ShaperProjectileCastMs, point, false),
            };
            session.Schedule(session.Runtime.Now + ProjectileRuntime.Time(shot.Release!.Fraction * attack.Seconds),
                () => MonsterActionCurrent(conn, id, boss, action),
                () => MonsterProjectileUse(session, id, boss, shot, AtlasBosses.AcceleratingShot, action,
                    orderedAim: player, sequence: conn.NextActionSequence()));
        }
    }

    /// <summary>The flicker slam: the Shaper vanishes, reappears beside the player and slams the ground (a 9 unit area,
    /// its damage the monster's own hit with the skill's +200 % final skill damage).</summary>
    async Task FlickerSlam(ClientState conn, (int X, int Y) player, double now)
    {
        var pos = boss.Walk.Position(now);
        var to = Landing(conn, player);
        await Teleport(conn, to, now);
        var slam = Attack(conn, AtlasBosses.FlickerSlamEffect);
        // The action, so the client plays the slam itself: its vanish, its blink and its landing (the circles on the
        // ground are the skill's own effect). The server draws none of that beside it, the same lesson as the beam.
        // The hit lands on the clip's contact, 800 ms in, where the Shaper stands (the rig's teleportslam_appear has
        // its contact at his own origin). The action's own contact carries no damage: the slam's hit is the area one
        // below.
        SwingWith(conn, slam with { Damage = DamagePacket.Empty, NoContact = true }, AtlasBosses.FlickerSlamEffect);
        // The impact lands where the Shaper lands (the cell he blinked to), not where he stood when he cast: the blink
        // has not landed yet at that moment, so the hit was sometimes a whole screen away from the player (live
        // 2026-11-14: the slam hit sometimes and sometimes did not).
        // The impact is the clip's contact (800 ms of the rig's 1500 ms teleportslam_appear), and the client plays
        // that clip stretched to the action's own time, so the contact is a fraction of the action, not a fixed moment.
        double contact = AtlasBosses.FlickerSlamContactSeconds / AtlasBosses.FlickerSlamClipSeconds * slam.Seconds;
        impacts.Add((now + contact, to, AtlasBosses.FlickerSlamRadius,
            slam with { Damage = slam.Damage.Scale(AtlasBosses.FlickerSlamDamageFactor) }));
    }

    /// <summary>The retreat (6089): a short blink away from a player who got too close, the clip played at both ends.</summary>
    async Task Retreat(ClientState conn, (int X, int Y) player, double now)
    {
        var pos = boss.Walk.Position(now);
        double angle = Math.Atan2(pos.Y - player.Y, pos.X - player.X);
        var to = conn.Layout.Near((pos.X + (int)(Math.Cos(angle) * 18), pos.Y + (int)(Math.Sin(angle) * 18)));
        await Effect(conn, pos, AtlasBosses.ShaperTeleportArt, 1.2);
        Swing(conn, AtlasBosses.RetreatEffect);
        await Teleport(conn, to, now);
        await Effect(conn, to, AtlasBosses.ShaperTeleportArt, 1.2);
    }

    // A walkable cell next to the player, the distance the flicker slam's clip covers.
    (int X, int Y) Landing(ClientState conn, (int X, int Y) player)
    {
        for (int radius = 2; radius <= 5; radius += 1)
            for (int step = 0; step < 8; step++)
            {
                double angle = Math.Tau * step / 8;
                int x = player.X + (int)Math.Round(Math.Cos(angle) * radius);
                int y = player.Y + (int)Math.Round(Math.Sin(angle) * radius);
                if (conn.Layout.WalkableFor(x, y, 0)) return (x, y);
            }
        return conn.Layout.Near((player.X, player.Y));
    }

    protected override async Task Intermission(ClientState conn, double now)
    {
        if (!intermission)
        {
            intermission = true;
            waves = 0;
            await Shield(conn, true);
            await Teleport(conn, Stage(conn), now);
            // The chant: the skill's own cast (5000 ms). The client plays the rig's bullethell_start (3000 ms) stretched
            // to it, with the cast art, and its contact (2866 ms, 0.955 of the clip) releases the nova. The server draws
            // no second cast art beside it.
            var chant = Attack(conn, AtlasBosses.BulletHellEffect) with
            {
                Seconds = Game.SkillsFor(conn).CastTimeMs(AtlasBosses.BulletHellEffect) / 1000.0,
                Release = new MonsterRelease("bullethell_start", 2866 / 3000.0, new ContactPoint(0, 0, -130), false),
            };
            SwingWith(conn, chant, AtlasBosses.BulletHellEffect);
            waveAt = now + chant.Seconds * chant.Release!.Fraction;
            busyUntil = waveAt + 1;
            return;
        }
        if (now < waveAt) return;
        if (waves == AtlasBosses.BulletHellWaves)
        {
            intermission = false;
            await Shield(conn, false);
            EndIntermission();
            nextVolley = now + 2;
            busyUntil = now + 1;
            return;
        }
        waves++;
        // The barrage: the rig's fast bullethell clip (366 ms) releases its nova at its contact (332 ms) from his own
        // centre, and the skill's own row makes it a nova (projectiles_nova) of eleven more projectiles, each for the
        // skill's own cold damage. The waves follow one another as fast as the clip can be cast again, which is what
        // makes it a bullet hell (live 2026-11-14: three waves a second and a half apart were not one).
        var ring = Attack(conn, AtlasBosses.BulletHellEffect) with
        {
            Seconds = AtlasBosses.BulletHellCastSeconds,
            Release = AtlasBosses.BulletHellRelease,
        };
        SwingWith(conn, ring, AtlasBosses.BulletHellEffect);
        waveAt = now + AtlasBosses.BulletHellCastSeconds;
        busyUntil = now + AtlasBosses.BulletHellCastSeconds;
    }
}
