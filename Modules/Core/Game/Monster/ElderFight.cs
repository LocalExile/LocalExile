using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;

/// <summary>
/// The Elder (MonsterVarieties <c>Metadata/Monsters/AtlasBosses/TheElder/TheElder</c> in Absence of Value and Meaning at
/// level 83, <c>TheElderUber</c> in the Uber Elder arena). His GrantedEffectsKeys are the kit played here: the melee
/// (406), the siphon (9058, a physical degen that also heals him), the slamming tentacles (9062, five portals that land
/// around the player), the expanding nova (9066), the doom projectile (9351) and the three-shot spellcast (9330).
/// <para>Damage comes from the effects' own stat sets at the area's level, the pacing from each clip's cast time
/// (the tentacles 5 s, the siphon 8.5 s, the nova 3.83 s), the same way <see cref="ShaperFight"/> does it.</para>
/// </summary>
sealed class ElderFight(uint id, SpawnedMonster boss, ClientState conn) : AtlasBossFight(id, boss, conn)
{
    protected override string Name => "[Elder]";
    protected override ushort ImmunityRow => AtlasBosses.ElderImmunityRow;
    protected override string ShieldArt => AtlasBosses.ElderShieldArt;
    protected override string ShieldOffArt => AtlasBosses.ElderShieldOffArt;
    // The blink of an intermission goes to the arena's middle marker, not to the layout's spawn (the map entry).
    (int X, int Y)? stage;
    protected override (int X, int Y) Stage(ClientState conn) => stage ??= ArenaCentre(conn, boss);

    double nextMelee, nextSiphon, nextTentacles, nextNova, nextVolley, nextDoom, nextChase;
    // The siphon's degen: the ground under the player burns for as long as the Elder channels, and a quarter of what it
    // takes the Elder heals (the original's drain; see Ticks).
    double siphonUntil, siphonNext;
    (int X, int Y) siphonAt;
    MonsterAttack? siphonTick;
    // The tentacles' portals: each one slams where it opened, after its telegraph.
    readonly List<(double At, (int X, int Y) Where, double Radius, MonsterAttack Attack)> pending = [];
    // A threshold's intermission: the shield, the nova, one wave of tentacles.
    bool intermission;
    double waveAt;
    int waves;

    protected override async Task Ticks(ClientState conn, double now)
    {
        foreach (var slam in pending.Where(p => p.At <= now).ToArray())
        {
            pending.Remove(slam);
            await Effect(conn, slam.Where, AtlasBosses.ElderTentacleSlamArt, 1.6);
            foreach (var player in Watchers())
                if (Distance(player.Walk.Position(now), slam.Where) <= slam.Radius) await Hit(player, slam.Attack, slam.Where);
        }
        if (siphonTick is null || now >= siphonUntil || now < siphonNext) return;
        siphonNext = now + 0.5;
        if (now >= siphonUntil) { siphonTick = null; return; }
        uint healed = 0;
        foreach (var player in Watchers())
        {
            if (Distance(player.Walk.Position(now), siphonAt) > 8) continue;
            uint before = player.PlayerLife;
            // Hit the player the ground is under, or the tally below reads one connection's life while another takes
            // the damage and healed stays 0.
            await Hit(player, siphonTick, siphonAt);
            healed += before > player.PlayerLife ? before - player.PlayerLife : 0;
        }
        // The drain feeds him: a quarter of the life it took, never past his maximum.
        if (healed > 0) lock (conn.Monsters) boss.Life = Math.Min(boss.MaxLife, boss.Life + healed / 4);
    }

    protected override async Task Act(ClientState conn, double now)
    {
        var watchers = Watchers();
        if (watchers.Length == 0) return;
        var pos = boss.Walk.Position(now);
        var target = watchers.MinBy(p => Distance(pos, p.Walk.Position(now)))!;
        var player = target.Walk.Position(now);
        double distance = Distance(pos, player);

        // Due ranged skills go before the melee: checked the other way round, the melee returned first every time he
        // stood next to a player and the siphon (which needs distance) never fired at all.
        if (now >= nextSiphon && distance < 60)
        {
            nextSiphon = now + 18;
            await Siphon(conn, player, now);
            return;
        }
        if (now >= nextNova && distance <= 26)
        {
            nextNova = now + 14;
            await Nova(conn, player, now);
            return;
        }
        if (distance <= Variety.AttackRange + 1 && now >= nextMelee)
        {
            nextMelee = now + Variety.AttackMs / 1000.0 + 0.4;
            Swing(conn, AtlasBosses.ElderMeleeEffect);
            return;
        }
        if (now >= nextTentacles)
        {
            nextTentacles = now + 12;
            await Tentacles(conn, player, now, 5);
            return;
        }
        if (now >= nextVolley)
        {
            nextVolley = now + 7;
            Swing(conn, AtlasBosses.ProjectileSpellcastEffect);
            return;
        }
        if (now >= nextDoom)
        {
            nextDoom = now + 10;
            await Effect(conn, pos, AtlasBosses.ElderDoomArt, 1.6);
            Swing(conn, AtlasBosses.DoomProjectileEffect);
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

    /// <summary>The siphon (9058): the Elder channels at the player's ground for three seconds; whatever stands there
    /// takes the skill's physical degen on a 0.5 s tick and heals him for a quarter of it.</summary>
    async Task Siphon(ClientState conn, (int X, int Y) at, double now)
    {
        var dot = AtlasBosses.DamageOverTime(conn, AtlasBosses.SiphonEffect, boss.Level);
        if (dot.MaxTotal <= 0) return;
        siphonAt = conn.Layout.Near((at.X, at.Y));
        siphonUntil = now + 3;
        siphonNext = now + 0.8;
        siphonTick = new MonsterAttack(AtlasBosses.Skill(Variety, AtlasBosses.SiphonEffect), 0.5, 0, dot.Scale(0.5))
        { Spell = true, Scripted = true };
        Swing(conn, AtlasBosses.SiphonEffect);
        await Effect(conn, siphonAt, AtlasBosses.ElderSiphonArt, 3.6, 1.6f);
    }

    /// <summary>The slamming tentacles (9062): five portals open around the player and slam where they opened, a 5 unit
    /// area each, their physical damage 1.2x the melee's, one second after the cast.</summary>
    async Task Tentacles(ClientState conn, (int X, int Y) player, double now, int count)
    {
        var slam = Attack(conn, AtlasBosses.SlammingTentaclesEffect);
        slam = slam with { Damage = slam.Damage.Scale(1.2) };
        Swing(conn, AtlasBosses.SlammingTentaclesEffect);
        for (int i = 0; i < count; i++)
        {
            double angle = Math.Tau * i / count + Random.Shared.NextDouble() * 0.4;
            int radius = 6 + Random.Shared.Next(6);
            var where = conn.Layout.Near((player.X + (int)Math.Round(Math.Cos(angle) * radius),
                player.Y + (int)Math.Round(Math.Sin(angle) * radius)));
            await Effect(conn, where, AtlasBosses.ElderTentaclePortalArt, 2.4);
            pending.Add((now + 1.2, where, 5, slam));
        }
    }

    /// <summary>The expanding nova (9066): a ring that leaves the Elder and damages everything within 22 units once it
    /// has passed: the player walks out of it, the way the original's is dodged.</summary>
    async Task Nova(ClientState conn, (int X, int Y) player, double now)
    {
        var pos = boss.Walk.Position(now);
        var attack = Attack(conn, AtlasBosses.ExpandingNovaEffect);
        await Effect(conn, pos, AtlasBosses.ElderNovaArt, 2.4, 2f);
        Swing(conn, AtlasBosses.ExpandingNovaEffect);
        // The ring reaches 22 units 1.4 s after the cast; anything still inside takes the skill's damage.
        pending.Add((now + 1.4, pos, 22, attack));
        _ = player;
    }

    protected override async Task Intermission(ClientState conn, double now)
    {
        if (!intermission)
        {
            intermission = true;
            waves = 0;
            await Shield(conn, true);
            await Teleport(conn, Stage(conn), now);
            var watchers = Watchers();
            if (watchers.Length > 0) await Nova(conn, watchers[0].Walk.Position(now), now);
            waveAt = now + 1.6;
            busyUntil = now + 1.6;
            return;
        }
        if (now < waveAt) return;
        if (waves == 1)
        {
            intermission = false;
            await Shield(conn, false);
            EndIntermission();
            nextTentacles = now + 2;
            busyUntil = now + 1;
            return;
        }
        waves++;
        var target = Watchers().MinBy(p => Distance(boss.Walk.Position(now), p.Walk.Position(now)));
        if (target is not null) await Tentacles(conn, target.Walk.Position(now), now, 4);
        waveAt = now + 1.8;
        busyUntil = now + 1.6;
    }
}
