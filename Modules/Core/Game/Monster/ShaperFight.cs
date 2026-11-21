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
sealed class ShaperFight(uint id, SpawnedMonster boss, Player player) : AtlasBossFight(id, boss, player)
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
    protected override (int X, int Y) Stage(Player player) => stage ??= ArenaCentre(player, boss);

    double nextMelee, nextBeam, nextVolley, nextFlicker, nextBlink, nextChase;
    // The Celestial Beam: a point on the ground that keeps burning for the clip's three seconds (base_skill_effect_duration
    // 3000). The player is free to walk out of it, which is the whole mechanic.
    double beamUntil, beamNextTick;
    (int X, int Y) beamAt;
    MonsterAttack? beamTick;
    // A threshold's intermission: the shield, the blink to the middle, then three bullet-hell rings.
    bool intermission;
    int waves;
    double waveAt;

    protected override async Task Ticks(Player player, double now)
    {
        if (beamTick is null || now >= beamUntil || now < beamNextTick) return;
        beamNextTick = now + 0.5;
        if (now >= beamUntil) { beamTick = null; return; }
        foreach (var peer in Watchers())
            if (Distance(peer.Walk.Position(now), beamAt) <= 7) await Hit(peer, beamTick, beamAt);
    }

    protected override async Task Act(Player player, double now)
    {
        var watchers = Watchers();
        if (watchers.Length == 0) return;
        var pos = boss.Walk.Position(now);
        var target = watchers.MinBy(p => Distance(pos, p.Walk.Position(now)))!;
        var playerAt = target.Walk.Position(now);
        double distance = Distance(pos, playerAt);

        if (distance <= Variety.AttackRange + 1 && now >= nextMelee)
        {
            nextMelee = now + Variety.AttackMs / 1000.0 + 0.4;
            Swing(player, AtlasBosses.ShaperMeleeEffect);
            return;
        }
        if (now >= nextBeam)
        {
            nextBeam = now + 15;
            await Beam(player, playerAt, now);
            return;
        }
        if (now >= nextFlicker && distance is > 14 and < 70)
        {
            nextFlicker = now + 19;
            await FlickerSlam(player, playerAt, now);
            return;
        }
        if (now >= nextVolley)
        {
            nextVolley = now + 8;
            Swing(player, AtlasBosses.AcceleratingProjectilesEffect);
            return;
        }
        if (distance <= 10 && now >= nextBlink)
        {
            nextBlink = now + 12;
            await Retreat(player, playerAt, now);
            return;
        }
        if (distance > Variety.AttackRange && now >= nextChase)
        {
            nextChase = now + 0.75;
            double fraction = Math.Clamp((distance - Variety.AttackRange + 1) / distance, 0, 1);
            var chase = ((int)Math.Round(pos.X + (playerAt.X - pos.X) * fraction),
                (int)Math.Round(pos.Y + (playerAt.Y - pos.Y) * fraction));
            await Move(player, player.Layout.Near(chase), now);
        }
    }

    /// <summary>The Celestial Beam: the Shaper casts, and the ground where the player stood burns for three seconds on a
    /// 0.5 s tick, its damage the skill's own fire/cold/lightning degen over that half second.</summary>
    async Task Beam(Player player, (int X, int Y) at, double now)
    {
        var dot = AtlasBosses.DamageOverTime(player, AtlasBosses.CelestialBeamEffect, boss.Level);
        beamAt = player.Layout.Near((at.X, at.Y));
        beamUntil = now + 3;
        beamNextTick = now + 0.6;
        beamTick = dot.MaxTotal <= 0 ? null : new MonsterAttack(
            AtlasBosses.Skill(Variety, AtlasBosses.CelestialBeamEffect), 0.5, 0, dot.Scale(0.5))
        { Spell = true, Scripted = true };
        Swing(player, AtlasBosses.CelestialBeamEffect);
        await Effect(player, beamAt, AtlasBosses.CelestialBeamArt, 3.6, 1.6f);
    }

    /// <summary>The flicker slam: the Shaper vanishes, reappears beside the player and slams the ground (a 9 unit area,
    /// its physical damage 1.4x the melee's).</summary>
    async Task FlickerSlam(Player player, (int X, int Y) playerAt, double now)
    {
        var pos = boss.Walk.Position(now);
        await Effect(player, pos, AtlasBosses.FlickerSlamDisappearArt, 1.2);
        var to = Landing(player, playerAt);
        await Teleport(player, to, now);
        await Effect(player, to, AtlasBosses.FlickerSlamAppearArt, 1.2);
        var slam = Attack(player, AtlasBosses.FlickerSlamEffect);
        await Effect(player, boss.Walk.Position(now), AtlasBosses.FlickerSlamArt, 2.5);
        SwingWith(player, slam with { Damage = slam.Damage.Scale(1.4) }, AtlasBosses.FlickerSlamEffect, area: true, radius: 9);
    }

    /// <summary>The retreat (6089): a short blink away from a player who got too close, the clip played at both ends.</summary>
    async Task Retreat(Player player, (int X, int Y) playerAt, double now)
    {
        var pos = boss.Walk.Position(now);
        double angle = Math.Atan2(pos.Y - playerAt.Y, pos.X - playerAt.X);
        var to = player.Layout.Near((pos.X + (int)(Math.Cos(angle) * 18), pos.Y + (int)(Math.Sin(angle) * 18)));
        await Effect(player, pos, AtlasBosses.ShaperTeleportArt, 1.2);
        Swing(player, AtlasBosses.RetreatEffect);
        await Teleport(player, to, now);
        await Effect(player, to, AtlasBosses.ShaperTeleportArt, 1.2);
    }

    // A walkable cell next to the player, the distance the flicker slam's clip covers.
    (int X, int Y) Landing(Player player, (int X, int Y) playerAt)
    {
        for (int radius = 4; radius <= 10; radius += 2)
            for (int step = 0; step < 8; step++)
            {
                double angle = Math.Tau * step / 8;
                int x = playerAt.X + (int)Math.Round(Math.Cos(angle) * radius);
                int y = playerAt.Y + (int)Math.Round(Math.Sin(angle) * radius);
                if (player.Layout.WalkableFor(x, y, 0)) return (x, y);
            }
        return player.Layout.Near((playerAt.X, playerAt.Y));
    }

    protected override async Task Intermission(Player player, double now)
    {
        if (!intermission)
        {
            intermission = true;
            waves = 0;
            await Shield(player, true);
            await Teleport(player, Stage(player), now);
            await Effect(player, Stage(player), AtlasBosses.BulletHellCastArt, 2.4, 1.8f);
            waveAt = now + 1;
            busyUntil = now + 1;
            return;
        }
        if (now < waveAt) return;
        if (waves == 3)
        {
            intermission = false;
            await Shield(player, false);
            EndIntermission();
            nextVolley = now + 2;
            busyUntil = now + 1;
            return;
        }
        waves++;
        // A ring of twelve, from the Projectiles.dat row (ShaperBulletHellProjectile): no skill record makes a circle. The
        // clip's own five seconds would stretch the intermission into three of them, so a wave lasts the 1.6 s it takes
        // the next one to come (the ring leaves at the clip's contact, half of it).
        var ring = Attack(player, AtlasBosses.BulletHellEffect) with { Seconds = 1.6 };
        SwingWith(player, ring, AtlasBosses.BulletHellEffect);
        waveAt = now + 1.4;
        busyUntil = now + 1.2;
    }
}
