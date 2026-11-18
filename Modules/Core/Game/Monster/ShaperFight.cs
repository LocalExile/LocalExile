using GameServer.Core.Protocol.Instance;
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
    // The Celestial Beam: a point on the ground that keeps burning for the clip's three seconds (base_skill_effect_duration
    // 3000). The player is free to walk out of it, which is the whole mechanic.
    double beamUntil, beamNextTick;
    (int X, int Y) beamAt;
    MonsterAttack? beamTick;
    // The beam itself: a ServerBeamEffect object stretched from where he stood to the mark, alive for BeamSeconds.
    // CelestialBeamArt is only the source rig of the skill's mesh (celestialbeam.sm), so spawning it as an effect draws
    // that mesh un-stretched, a ball; the client's own beam object is the thing that draws a beam between two points.
    (int X, int Y) beamFrom;
    uint beamObject;
    double beamShowAt, beamHideAt;
    // A threshold's intermission: the shield, the blink to the middle, then three bullet-hell rings.
    bool intermission;
    int waves;
    double waveAt;

    protected override async Task Ticks(ClientState conn, double now)
    {
        if (beamShowAt > 0 && now >= beamShowAt) { beamShowAt = 0; beamObject = await ShowBeam(conn); }
        if (beamObject != 0 && now >= beamHideAt)
        {
            var gone = beamObject;
            beamObject = 0;
            await All(InstanceProtocol.RemoveObject(gone));
        }
        if (beamTick is null || now >= beamUntil || now < beamNextTick) return;
        beamNextTick = now + 0.5;
        if (now >= beamUntil) { beamTick = null; return; }
        foreach (var player in Watchers())
            if (Distance(player.Walk.Position(now), beamAt) <= 7) await Hit(player, beamTick, beamAt);
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
        if (now >= nextBeam)
        {
            nextBeam = now + 15;
            await Beam(conn, player, now);
            return;
        }
        // The slam needs no stand-off: the melee above already takes every distance it can reach, so whatever gets here
        // is out of his arms. The old "> 14" bound was never reachable (melee keeps him at 13) and kept the skill shut.
        if (now >= nextFlicker && distance < 70)
        {
            nextFlicker = now + 19;
            await FlickerSlam(conn, player, now);
            return;
        }
        if (now >= nextVolley)
        {
            nextVolley = now + 8;
            Swing(conn, AtlasBosses.AcceleratingProjectilesEffect);
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

    /// <summary>The Celestial Beam: the Shaper charges for <see cref="BeamChargeSeconds"/>, the server's beam stands where
    /// the player stood for <see cref="BeamSeconds"/>, and the ground then burns for three seconds on a 0.5 s tick, its
    /// damage the skill's own fire/cold/lightning degen over that half second.</summary>
    async Task Beam(ClientState conn, (int X, int Y) at, double now)
    {
        var dot = AtlasBosses.DamageOverTime(conn, AtlasBosses.CelestialBeamEffect, boss.Level);
        beamAt = conn.Layout.Near((at.X, at.Y));
        beamUntil = now + BeamChargeSeconds + BeamSeconds + BeamBurnSeconds;
        beamNextTick = now + BeamChargeSeconds;
        beamTick = dot.MaxTotal <= 0 ? null : new MonsterAttack(
            AtlasBosses.Skill(Variety, AtlasBosses.CelestialBeamEffect), 0.5, 0, dot.Scale(0.5))
        { Spell = true, Scripted = true };
        // The pose without the skill: 0x1b2 kind 0 plays the rig's own beam_start clip by index, which the client cannot
        // pair with the skill's art. Sending the skill's action (0x1ab) instead draws that art as well, and it is a whole
        // second beam, wherever and whenever ours is (live 2026-11-13). The clip spends nearly all of its 2.5 s charging,
        // so it plays faster to charge for the skill's own second, and the server's rig is the beam itself.
        lock (conn.Monsters) busyUntil = Math.Max(busyUntil, now) + BeamChargeSeconds + BeamSeconds;
        if (AtlasBosses.ClipFor(conn, Variety, AtlasBosses.CelestialBeamEffect) is { Index: < uint.MaxValue } clip)
            await All(InstanceProtocol.AnimationUpdate(id, 0, clip.Index,
                (float)Math.Clamp(clip.LengthMs / 1000.0 / BeamChargeSeconds, 0.1, 10)));
        beamFrom = boss.Walk.Position(now);
        beamShowAt = now + BeamChargeSeconds;
        beamHideAt = beamShowAt + BeamSeconds;
    }

    /// <summary>Draws the beam with the client's own beam object (<c>Metadata/Effects/ServerBeamEffect</c>): it stretches
    /// the MiscBeams row's art between the two ends, which is what the skill's class does for the mesh CelestialBeamArt
    /// points at. The end stays a world point (no object to follow), so the beam holds still where it landed.</summary>
    async Task<uint> ShowBeam(ClientState conn)
    {
        uint beam;
        lock (conn.Monsters) beam = conn.TakeObjectId();
        await All(InstanceEntities.CreateObject(beam, InstanceEntities.ObjectTypeHash(BeamEffectObject.TypePath),
            new BeamEffectObject(beamFrom.X, beamFrom.Y, id, beamAt.X * BeamEffectObject.WorldPerCell,
                beamAt.Y * BeamEffectObject.WorldPerCell, 0, BeamRow).Blob()));
        await All(InstanceProtocol.EnterWakeZone(beam));
        return beam;
    }

    /// <summary>The MiscBeams row the beam is drawn with: <c>ElderGuardianLightningBeam</c> (76,
    /// <c>.../ElderGuardian/LightningGuardian/arenabeam.ao</c>), the gold lightning column of the same family as the
    /// Celestial Beam. The skill's own mesh has no MiscBeams row of its own (the skill class stretches it itself).</summary>
    const ushort BeamRow = 76;

    /// <summary>How long the Shaper charges before the beam lands (the skill's own cast second).</summary>
    const double BeamChargeSeconds = 1.0;

    /// <summary>How long the beam stands where the cast lands.</summary>
    const double BeamSeconds = 2.5;

    /// <summary>How long the beam's ground keeps burning after the beam itself ends.</summary>
    const double BeamBurnSeconds = 3;

    /// <summary>The flicker slam: the Shaper vanishes, reappears beside the player and slams the ground (a 9 unit area,
    /// its physical damage 1.4x the melee's).</summary>
    async Task FlickerSlam(ClientState conn, (int X, int Y) player, double now)
    {
        var pos = boss.Walk.Position(now);
        await Effect(conn, pos, AtlasBosses.FlickerSlamDisappearArt, 1.2);
        var to = Landing(conn, player);
        await Teleport(conn, to, now);
        await Effect(conn, to, AtlasBosses.FlickerSlamAppearArt, 1.2);
        var slam = Attack(conn, AtlasBosses.FlickerSlamEffect);
        await Effect(conn, boss.Walk.Position(now), AtlasBosses.FlickerSlamArt, 2.5);
        SwingWith(conn, slam with { Damage = slam.Damage.Scale(1.4) }, AtlasBosses.FlickerSlamEffect, area: true, radius: 9);
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
        for (int radius = 4; radius <= 10; radius += 2)
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
            await Effect(conn, Stage(conn), AtlasBosses.BulletHellCastArt, 2.4, 1.8f);
            waveAt = now + 1;
            busyUntil = now + 1;
            return;
        }
        if (now < waveAt) return;
        if (waves == 3)
        {
            intermission = false;
            await Shield(conn, false);
            EndIntermission();
            nextVolley = now + 2;
            busyUntil = now + 1;
            return;
        }
        waves++;
        // A ring of twelve, from the Projectiles.dat row (ShaperBulletHellProjectile): no skill record makes a circle. The
        // clip's own five seconds would stretch the intermission into three of them, so a wave lasts the 1.6 s it takes
        // the next one to come (the ring leaves at the clip's contact, half of it).
        var ring = Attack(conn, AtlasBosses.BulletHellEffect) with { Seconds = 1.6 };
        SwingWith(conn, ring, AtlasBosses.BulletHellEffect);
        waveAt = now + 1.4;
        busyUntil = now + 1.2;
    }
}
