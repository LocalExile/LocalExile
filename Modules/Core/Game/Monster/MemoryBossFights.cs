using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;

/// <summary>
/// Incarnation of Neglect (<c>IgnoranceBoss</c> / <c>IgnoranceBossUBER</c> in <c>MapWorldsMemoryBoss2*</c>):
/// accelerating cold/lightning projectiles, Ice Nova Slam, Big Blast, Skyfall meteor strikes, and phase shields with
/// sweeping cold &amp; lightning beams.
/// </summary>
sealed class NeglectFight(uint id, SpawnedMonster boss, Player player) : AtlasBossFight(id, boss, player)
{
    const string Art = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/memories_zana/neglect/";
    const string ShieldFx = Art + "statue_shield.ao";
    const string IceWaveArt = Art + "ice_wave_primary.ao";
    const string LargeMarkerArt = Art + "large_marker.ao";
    const string LargeAoeArt = Art + "large_aoe.ao";
    const string ColdBeamArt = Art + "cold_beam.ao";
    const string LightningBeamArt = Art + "lightning_beam.ao";
    const string MeteorDropArt = Art + "meteor_drop.ao";
    const string EyeLaserArt = Art + "eyelaser.ao";

    protected override string Name => "[IncarnationOfNeglect]";
    protected override ushort ImmunityRow => AtlasBosses.ShaperImmunityRow;
    protected override string ShieldArt => ShieldFx;
    protected override string ShieldOffArt => IceWaveArt;

    (int X, int Y)? stage;
    protected override (int X, int Y) Stage(Player player) => stage ??= ArenaCentre(player, boss);

    double nextMelee, nextProj, nextNova, nextBigBlast, nextBeam, nextChase;
    readonly List<(double At, (int X, int Y) Where, double Radius, MonsterAttack Attack, string ImpactArt)> pending = [];
    bool intermission;
    int waves;
    double waveAt;

    protected override async Task Ticks(Player player, double now)
    {
        foreach (var hit in pending.Where(p => p.At <= now).ToArray())
        {
            pending.Remove(hit);
            await Effect(player, hit.Where, hit.ImpactArt, 1.6, 1.4f);
            foreach (var peer in Watchers())
                if (Distance(peer.Walk.Position(now), hit.Where) <= hit.Radius)
                    await Hit(peer, hit.Attack, hit.Where);
        }
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
            nextMelee = now + Variety.AttackMs / 1000.0 + 0.35;
            Swing(player, AtlasBosses.BossMeleeEffect);
            return;
        }
        if (now >= nextBeam && distance <= 55)
        {
            nextBeam = now + 11;
            bool cold = AreaRng(player).Next(2) == 0;
            var beam = Attack(player, cold ? AtlasBosses.NeglectLaserBeamEffect : AtlasBosses.NeglectLaserBeam2Effect);
            Swing(player, cold ? AtlasBosses.NeglectLaserBeamEffect : AtlasBosses.NeglectLaserBeam2Effect);
            await Effect(player, playerAt, cold ? ColdBeamArt : LightningBeamArt, 2.0, 1.5f);
            pending.Add((now + 1.0, playerAt, 10, beam with { Damage = beam.Damage.Scale(1.3) }, EyeLaserArt));
            return;
        }
        if (now >= nextNova && distance <= 26)
        {
            nextNova = now + 12;
            var nova = Attack(player, AtlasBosses.NeglectIceNovaSlamEffect);
            Swing(player, AtlasBosses.NeglectIceNovaSlamEffect);
            await Effect(player, pos, IceWaveArt, 2.0, 1.6f);
            pending.Add((now + 1.1, pos, 18, nova with { Damage = nova.Damage.Scale(1.35) }, IceWaveArt));
            return;
        }
        if (now >= nextBigBlast)
        {
            nextBigBlast = now + 15;
            var blast = Attack(player, AtlasBosses.NeglectBigBlastEffect);
            Swing(player, AtlasBosses.NeglectBigBlastEffect);
            await Effect(player, playerAt, LargeMarkerArt, 1.6, 1.5f);
            pending.Add((now + 1.5, playerAt, 14, blast with { Damage = blast.Damage.Scale(1.6) }, LargeAoeArt));
            return;
        }
        if (now >= nextProj)
        {
            nextProj = now + 6.5;
            Swing(player, AtlasBosses.NeglectAccelProjEffect);
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

    protected override async Task Intermission(Player player, double now)
    {
        if (!intermission)
        {
            intermission = true;
            waves = 0;
            await Shield(player, true);
            await Teleport(player, Stage(player), now);
            Swing(player, AtlasBosses.NeglectSpawnLasersEffect);
            waveAt = now + 1.4;
            busyUntil = now + 1.4;
            return;
        }
        if (now < waveAt) return;
        if (waves >= 3)
        {
            intermission = false;
            await Shield(player, false);
            EndIntermission();
            nextProj = now + 2;
            busyUntil = now + 1;
            return;
        }
        waves++;
        var beam = Attack(player, AtlasBosses.NeglectBeamDamageEffect);
        foreach (var peer in Watchers())
        {
            var at = peer.Walk.Position(now);
            await Effect(player, at, LargeMarkerArt, 1.3, 1.2f);
            pending.Add((now + 1.1, at, 9, beam with { Damage = beam.Damage.Scale(1.3) }, MeteorDropArt));
        }
        waveAt = now + 2.0;
        busyUntil = now + 1.6;
    }

    protected override Task OnIntermissionAborted(Player player)
    {
        intermission = false;
        waves = 0;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Incarnation of Fear (<c>AngerBoss</c> / <c>AngerBossUBER</c> in <c>MapWorldsMemoryBoss1*</c>):
/// burning fissure slams, leap attack, Solar Orb Nova / Shatter, Spear Orb throws, Giga Beam (<c>Anger's Intensity</c>),
/// and phase shields with converging beam barrages.
/// </summary>
sealed class FearFight(uint id, SpawnedMonster boss, Player player) : AtlasBossFight(id, boss, player)
{
    const string Art = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/memories_zana/anger/";
    const string AimBeamArt = Art + "aim_beam.ao";
    const string CoreBeamArt = Art + "corebeam.ao";
    const string BurningGroundArt = Art + "burning_ground.ao";
    const string FissureArt = Art + "fissure_crater.ao";
    const string LeapSlamArt = Art + "leap_slam.ao";
    const string NovaArt = Art + "nova.ao";
    const string SolarOrbArt = Art + "solar_orb.ao";
    const string SolarPulseArt = Art + "solar_pulse.ao";
    const string StompArt = Art + "stomp.ao";

    protected override string Name => "[IncarnationOfFear]";
    protected override ushort ImmunityRow => AtlasBosses.ShaperImmunityRow;
    protected override string ShieldArt => SolarOrbArt;
    protected override string ShieldOffArt => SolarPulseArt;

    (int X, int Y)? stage;
    protected override (int X, int Y) Stage(Player player) => stage ??= ArenaCentre(player, boss);

    double nextMelee, nextFissure, nextLeap, nextBeam, nextNova, nextChase;
    readonly List<(double At, (int X, int Y) Where, double Radius, MonsterAttack Attack, string ImpactArt)> pending = [];
    bool intermission;
    int waves;
    double waveAt;

    protected override async Task Ticks(Player player, double now)
    {
        foreach (var hit in pending.Where(p => p.At <= now).ToArray())
        {
            pending.Remove(hit);
            await Effect(player, hit.Where, hit.ImpactArt, 1.6, 1.4f);
            foreach (var peer in Watchers())
                if (Distance(peer.Walk.Position(now), hit.Where) <= hit.Radius)
                    await Hit(peer, hit.Attack, hit.Where);
        }
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
            nextMelee = now + Variety.AttackMs / 1000.0 + 0.35;
            Swing(player, AtlasBosses.UniqueMeleeEffect);
            return;
        }
        if (now >= nextBeam && distance <= 60)
        {
            nextBeam = now + 13;
            var dot = AtlasBosses.DamageOverTime(player, AtlasBosses.FearGigaBeamEffect, boss.Level);
            var beam = dot.MaxTotal > 0
                ? new MonsterAttack(AtlasBosses.Skill(Variety, AtlasBosses.FearGigaBeamEffect), 1.5, 60, dot.Scale(0.8)) { Spell = true, Scripted = true }
                : Attack(player, AtlasBosses.FearBeamBarrageEffect);
            Swing(player, AtlasBosses.FearGigaBeamEffect);
            await Effect(player, pos, AimBeamArt, 1.2, 1.3f);
            await Effect(player, playerAt, CoreBeamArt, 2.2, 1.5f);
            pending.Add((now + 1.1, playerAt, 10, beam, BurningGroundArt));
            return;
        }
        if (now >= nextLeap && distance is > 16 and < 60)
        {
            nextLeap = now + 11;
            Swing(player, AtlasBosses.FearLeapEffect);
            var to = player.Layout.Near(playerAt);
            await Teleport(player, to, now);
            var slam = Attack(player, AtlasBosses.FearLeapAttackEffect);
            pending.Add((now + 0.7, to, 11, slam with { Damage = slam.Damage.Scale(1.4) }, LeapSlamArt));
            return;
        }
        if (now >= nextFissure)
        {
            nextFissure = now + 8.5;
            Swing(player, AtlasBosses.FearFissureProjEffect);
            var slam = Attack(player, AtlasBosses.FearFissureSlamEffect);
            await Effect(player, playerAt, FissureArt, 2.2, 1.3f);
            pending.Add((now + 0.9, playerAt, 9, slam, BurningGroundArt));
            return;
        }
        if (now >= nextNova && distance <= 28)
        {
            nextNova = now + 14;
            Swing(player, AtlasBosses.FearSolarNovaEffect);
            var stomp = Attack(player, AtlasBosses.FearBigStompEffect);
            await Effect(player, pos, NovaArt, 2.0, 1.6f);
            pending.Add((now + 1.2, pos, 18, stomp with { Damage = stomp.Damage.Scale(1.45) }, StompArt));
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

    protected override async Task Intermission(Player player, double now)
    {
        if (!intermission)
        {
            intermission = true;
            waves = 0;
            await Shield(player, true);
            await Teleport(player, Stage(player), now);
            waveAt = now + 1.4;
            busyUntil = now + 1.4;
            return;
        }
        if (now < waveAt) return;
        if (waves >= 3)
        {
            intermission = false;
            await Shield(player, false);
            EndIntermission();
            nextFissure = now + 2;
            busyUntil = now + 1;
            return;
        }
        waves++;
        Swing(player, AtlasBosses.FearBeamBarrageEffect);
        var shatter = Attack(player, AtlasBosses.FearOrbShatterEffect);
        var centre = Stage(player);
        for (int i = 0; i < 4; i++)
        {
            double a = Math.Tau * i / 4 + waves * 0.4;
            var at = player.Layout.Near((centre.X + (int)(Math.Cos(a) * 18), centre.Y + (int)(Math.Sin(a) * 18)));
            await Effect(player, at, SolarOrbArt, 1.6, 1.3f);
            pending.Add((now + 1.2, at, 9, shatter with { Damage = shatter.Damage.Scale(1.3) }, SolarPulseArt));
        }
        waveAt = now + 2.0;
        busyUntil = now + 1.6;
    }

    protected override Task OnIntermissionAborted(Player player)
    {
        intermission = false;
        waves = 0;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Incarnation of Dread (<c>BenevolenceBoss</c> / <c>BenevolenceBossUBER</c> in <c>MapWorldsMemoryBoss3*</c>):
/// Cascade Slam, Double Front Slam, Elemental Portal Volleys, Celestial Eyebeam, Shrinking Fire Nova, and Bullet Hell
/// elemental barrage intermissions.
/// </summary>
sealed class DreadFight(uint id, SpawnedMonster boss, Player player) : AtlasBossFight(id, boss, player)
{
    const string Art = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/memories_zana/benevolence/";
    const string ChaosBlastTelegraphArt = Art + "chaos_blast_telegraph.ao";
    const string ChaosBlastArt = Art + "chaos_blast.ao";
    const string EyebeamArt = Art + "eyebeam_laser.ao";
    const string ColdBeamBlastArt = Art + "cold_beam_blast.ao";
    const string ShrinkArt = Art + "shrink.ao";
    const string RoseExplodeArt = Art + "rose_comb_explode.ao";
    const string PortalChaosArt = Art + "portal_chaos.ao";
    const string PortalFireArt = Art + "portal_fire.ao";
    const string PortalIceArt = Art + "portal_ice.ao";
    const string PortalLightningArt = Art + "portal_lightning.ao";

    protected override string Name => "[IncarnationOfDread]";
    protected override ushort ImmunityRow => AtlasBosses.ElderImmunityRow;
    protected override string ShieldArt => PortalChaosArt;
    protected override string ShieldOffArt => RoseExplodeArt;

    (int X, int Y)? stage;
    protected override (int X, int Y) Stage(Player player) => stage ??= ArenaCentre(player, boss);

    double nextMelee, nextCascade, nextEyebeam, nextVolley, nextShrink, nextTeleSlam, nextChase;
    readonly List<(double At, (int X, int Y) Where, double Radius, MonsterAttack Attack, string ImpactArt)> pending = [];
    bool intermission;
    int waves;
    double waveAt;

    protected override async Task Ticks(Player player, double now)
    {
        foreach (var hit in pending.Where(p => p.At <= now).ToArray())
        {
            pending.Remove(hit);
            await Effect(player, hit.Where, hit.ImpactArt, 1.6, 1.4f);
            foreach (var peer in Watchers())
                if (Distance(peer.Walk.Position(now), hit.Where) <= hit.Radius)
                    await Hit(peer, hit.Attack, hit.Where);
        }
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
            nextMelee = now + Variety.AttackMs / 1000.0 + 0.35;
            Swing(player, AtlasBosses.UniqueMeleeEffect);
            return;
        }
        if (now >= nextEyebeam && distance <= 60)
        {
            nextEyebeam = now + 11;
            var beam = Attack(player, AtlasBosses.DreadCelestialBeamEffect);
            Swing(player, AtlasBosses.DreadEyebeamEffect);
            await Effect(player, pos, EyebeamArt, 2.0, 1.5f);
            pending.Add((now + 1.0, playerAt, 10, beam with { Damage = beam.Damage.Scale(1.35) }, ColdBeamBlastArt));
            return;
        }
        if (now >= nextTeleSlam && distance is > 16 and < 60)
        {
            nextTeleSlam = now + 15;
            var to = player.Layout.Near(playerAt);
            await Teleport(player, to, now);
            var slam = Attack(player, AtlasBosses.DreadTeleSlamEffect);
            Swing(player, AtlasBosses.DreadDoubleFrontSlamEffect);
            await Effect(player, to, ChaosBlastTelegraphArt, 1.3, 1.4f);
            pending.Add((now + 1.1, to, 12, slam with { Damage = slam.Damage.Scale(1.5) }, ChaosBlastArt));
            return;
        }
        if (now >= nextShrink && distance <= 32)
        {
            nextShrink = now + 14;
            var nova = Attack(player, AtlasBosses.DreadShrinkingNovaEffect);
            Swing(player, AtlasBosses.DreadShrinkingNovaEffect);
            await Effect(player, pos, ShrinkArt, 2.2, 1.8f);
            pending.Add((now + 1.4, pos, 20, nova with { Damage = nova.Damage.Scale(1.4) }, RoseExplodeArt));
            return;
        }
        if (now >= nextCascade && distance <= 36)
        {
            nextCascade = now + 9;
            var cascade = Attack(player, AtlasBosses.DreadCascadeSlamEffect);
            Swing(player, AtlasBosses.DreadCascadeSlamEffect);
            await Effect(player, playerAt, ChaosBlastTelegraphArt, 1.2);
            pending.Add((now + 0.9, playerAt, 9, cascade, ChaosBlastArt));
            return;
        }
        if (now >= nextVolley)
        {
            nextVolley = now + 6.5;
            int[] volleys = [AtlasBosses.DreadCentipedeEffect, AtlasBosses.DreadLightningProjEffect, AtlasBosses.DreadColdProjEffect];
            Swing(player, volleys[AreaRng(player).Next(volleys.Length)]);
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

    protected override async Task Intermission(Player player, double now)
    {
        if (!intermission)
        {
            intermission = true;
            waves = 0;
            await Shield(player, true);
            var centre = Stage(player);
            await Teleport(player, centre, now);
            Swing(player, AtlasBosses.DreadBulletHellStartEffect);
            string[] portals = [PortalFireArt, PortalIceArt, PortalLightningArt, PortalChaosArt];
            for (int i = 0; i < 4; i++)
            {
                double a = Math.PI / 4 + i * Math.PI / 2;
                var at = player.Layout.Near((centre.X + (int)(Math.Cos(a) * 18), centre.Y + (int)(Math.Sin(a) * 18)));
                await Effect(player, at, portals[i], 6.5, 1.3f);
            }
            waveAt = now + 1.4;
            busyUntil = now + 1.4;
            return;
        }
        if (now < waveAt) return;
        if (waves >= 3)
        {
            intermission = false;
            await Shield(player, false);
            EndIntermission();
            nextVolley = now + 2;
            busyUntil = now + 1;
            return;
        }
        waves++;
        int effect = waves % 2 == 1 ? AtlasBosses.DreadFireProjEffect : AtlasBosses.DreadChaosProjEffect;
        Swing(player, effect);
        waveAt = now + 1.8;
        busyUntil = now + 1.4;
    }

    protected override Task OnIntermissionAborted(Player player)
    {
        intermission = false;
        waves = 0;
        return Task.CompletedTask;
    }
}

/// <summary>
/// The Neglected Flame (<c>FragmentOfIgnorance__</c> in <c>MapWorldsCourtyardOfWasting</c>): Memory Thread miniboss
/// casting beam blasts, corridor blasts, circle mazes, beam bombs, and Rain of Stars.
/// </summary>
sealed class FragmentOfNeglectFight(uint id, SpawnedMonster boss, Player player) : AtlasBossFight(id, boss, player)
{
    protected override string Name => "[NeglectedFlame]";
    protected override ushort ImmunityRow => AtlasBosses.SirusImmunityRow;
    protected override string ShieldArt => AtlasBosses.SirusShieldArt;
    protected override string ShieldOffArt => AtlasBosses.SirusShieldOffArt;

    (int X, int Y)? stage;
    protected override (int X, int Y) Stage(Player player) => stage ??= ArenaCentre(player, boss);

    double nextMelee, nextBeam, nextCorridor, nextRain, nextChase;
    readonly List<(double At, (int X, int Y) Where, double Radius, MonsterAttack Attack, string ImpactArt)> pending = [];
    bool intermission;
    double intermissionEnds;

    protected override async Task Ticks(Player player, double now)
    {
        foreach (var hit in pending.Where(p => p.At <= now).ToArray())
        {
            pending.Remove(hit);
            await Effect(player, hit.Where, hit.ImpactArt, 1.5);
            foreach (var peer in Watchers())
                if (Distance(peer.Walk.Position(now), hit.Where) <= hit.Radius)
                    await Hit(peer, hit.Attack, hit.Where);
        }
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
            nextMelee = now + Variety.AttackMs / 1000.0 + 0.35;
            Swing(player, AtlasBosses.BossMeleeEffect);
            return;
        }
        if (now >= nextCorridor && distance <= 55)
        {
            nextCorridor = now + 12;
            var blast = Attack(player, AtlasBosses.FragNeglectCorridorEffect);
            Swing(player, AtlasBosses.FragNeglectCorridorEffect);
            await Effect(player, playerAt, AtlasBosses.SirusCorridorArt, 2.0, 1.2f);
            pending.Add((now + 1.1, playerAt, 10, blast with { Damage = blast.Damage.Scale(1.3) }, AtlasBosses.SirusNukeBeamArt));
            return;
        }
        if (now >= nextRain)
        {
            nextRain = now + 10;
            var bomb = Attack(player, AtlasBosses.FragNeglectBeamBombEffect);
            Swing(player, AtlasBosses.FragNeglectRainOfStarsEffect);
            for (int i = 0; i < 3; i++)
            {
                var at = player.Layout.Near((playerAt.X + AreaRng(player).Next(-10, 11), playerAt.Y + AreaRng(player).Next(-10, 11)));
                await Effect(player, at, AtlasBosses.SirusMeteorArt, 1.4);
                pending.Add((now + 1.1 + i * 0.25, at, 8, bomb, AtlasBosses.SirusMeteorImpactArt));
            }
            return;
        }
        if (now >= nextBeam)
        {
            nextBeam = now + 6;
            Swing(player, AtlasBosses.FragNeglectDeathProjEffect);
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

    protected override async Task Intermission(Player player, double now)
    {
        if (!intermission)
        {
            intermission = true;
            await Shield(player, true);
            await Teleport(player, Stage(player), now);
            Swing(player, AtlasBosses.FragNeglectCircleMazeEffect);
            intermissionEnds = now + 3.5;
            busyUntil = now + 2.0;
            return;
        }
        if (now >= intermissionEnds)
        {
            intermission = false;
            await Shield(player, false);
            EndIntermission();
        }
    }

    protected override Task OnIntermissionAborted(Player player)
    {
        intermission = false;
        return Task.CompletedTask;
    }
}

/// <summary>
/// The Cardinal of Fear (<c>FragmentOfAnger</c> in <c>MapWorldsChambersOfImpurity</c>): Memory Thread miniboss
/// casting Shield Shards, Holy Blast Beam, Dash Slam, Net Mortar, Explosive Arrow, and Flameblast Ultimate.
/// </summary>
sealed class FragmentOfFearFight(uint id, SpawnedMonster boss, Player player) : AtlasBossFight(id, boss, player)
{
    const string Art = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/memories_zana/cardinal_sanctus_vox/";
    const string CoreBeamArt = Art + "corebeam.ao";
    const string FlameblastChargeArt = Art + "flameblast_charge.ao";
    const string FlameblastExplodeArt = Art + "flameblast_explode.ao";
    const string GroundSlamArt = Art + "ground_slam.ao";
    const string ExplodeImpactArt = Art + "exploding_impact.ao";

    protected override string Name => "[CardinalOfFear]";
    protected override ushort ImmunityRow => AtlasBosses.ShaperImmunityRow;
    protected override string ShieldArt => FlameblastChargeArt;
    protected override string ShieldOffArt => FlameblastExplodeArt;

    (int X, int Y)? stage;
    protected override (int X, int Y) Stage(Player player) => stage ??= ArenaCentre(player, boss);

    double nextMelee, nextBeam, nextSlam, nextShards, nextArrow, nextChase;
    readonly List<(double At, (int X, int Y) Where, double Radius, MonsterAttack Attack, string ImpactArt)> pending = [];
    bool intermission;
    double intermissionEnds;

    protected override async Task Ticks(Player player, double now)
    {
        foreach (var hit in pending.Where(p => p.At <= now).ToArray())
        {
            pending.Remove(hit);
            await Effect(player, hit.Where, hit.ImpactArt, 1.5);
            foreach (var peer in Watchers())
                if (Distance(peer.Walk.Position(now), hit.Where) <= hit.Radius)
                    await Hit(peer, hit.Attack, hit.Where);
        }
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
            nextMelee = now + Variety.AttackMs / 1000.0 + 0.35;
            Swing(player, AtlasBosses.FireUniqueMeleeEffect);
            return;
        }
        if (now >= nextBeam && distance <= 55)
        {
            nextBeam = now + 11;
            var beam = Attack(player, AtlasBosses.FragFearHolyBeamEffect);
            Swing(player, AtlasBosses.FragFearHolyBeamEffect);
            await Effect(player, playerAt, CoreBeamArt, 1.8, 1.3f);
            pending.Add((now + 1.0, playerAt, 9, beam with { Damage = beam.Damage.Scale(1.3) }, ExplodeImpactArt));
            return;
        }
        if (now >= nextSlam && distance <= 32)
        {
            nextSlam = now + 10;
            var slam = Attack(player, AtlasBosses.FragFearSlamEffect);
            Swing(player, AtlasBosses.FragFearSlamEffect);
            await Effect(player, playerAt, GroundSlamArt, 1.6);
            pending.Add((now + 0.85, playerAt, 9, slam, GroundSlamArt));
            return;
        }
        if (now >= nextArrow)
        {
            nextArrow = now + 8;
            Swing(player, AtlasBosses.FragFearExplosiveArrowEffect);
            return;
        }
        if (now >= nextShards)
        {
            nextShards = now + 6;
            Swing(player, AtlasBosses.FragFearShieldShardsEffect);
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

    protected override async Task Intermission(Player player, double now)
    {
        if (!intermission)
        {
            intermission = true;
            await Shield(player, true);
            var centre = Stage(player);
            await Teleport(player, centre, now);
            var ult = Attack(player, AtlasBosses.FragFearUltimateEffect);
            Swing(player, AtlasBosses.FragFearUltimateEffect);
            pending.Add((now + 2.5, centre, 22, ult with { Damage = ult.Damage.Scale(1.6) }, FlameblastExplodeArt));
            intermissionEnds = now + 3.2;
            busyUntil = now + 2.0;
            return;
        }
        if (now >= intermissionEnds)
        {
            intermission = false;
            await Shield(player, false);
            EndIntermission();
        }
    }

    protected override Task OnIntermissionAborted(Player player)
    {
        intermission = false;
        return Task.CompletedTask;
    }
}

/// <summary>
/// The Deceitful God (<c>FragmentOfBenevolence</c> in <c>MapWorldsTheatreOfLies</c>): Memory Thread miniboss
/// casting Super Projectiles, Delayed Blasts, Divine Charge, Corrupted Ball Lightning, and Divine Beam.
/// </summary>
sealed class FragmentOfDreadFight(uint id, SpawnedMonster boss, Player player) : AtlasBossFight(id, boss, player)
{
    const string Art = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/memories_zana/innocence/";
    const string CoreBeamArt = Art + "core_beam.ao";
    const string DelayedBlastArt = Art + "delayed_blast.ao";
    const string BeamHitArt = Art + "beam_hit.ao";
    const string WaveArt = Art + "wave.ao";

    protected override string Name => "[DeceitfulGod]";
    protected override ushort ImmunityRow => AtlasBosses.ShaperImmunityRow;
    protected override string ShieldArt => WaveArt;
    protected override string ShieldOffArt => BeamHitArt;

    (int X, int Y)? stage;
    protected override (int X, int Y) Stage(Player player) => stage ??= ArenaCentre(player, boss);

    double nextMelee, nextBeam, nextBlast, nextProj, nextChase;
    readonly List<(double At, (int X, int Y) Where, double Radius, MonsterAttack Attack, string ImpactArt)> pending = [];
    bool intermission;
    double intermissionEnds;

    protected override async Task Ticks(Player player, double now)
    {
        foreach (var hit in pending.Where(p => p.At <= now).ToArray())
        {
            pending.Remove(hit);
            await Effect(player, hit.Where, hit.ImpactArt, 1.5);
            foreach (var peer in Watchers())
                if (Distance(peer.Walk.Position(now), hit.Where) <= hit.Radius)
                    await Hit(peer, hit.Attack, hit.Where);
        }
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
            nextMelee = now + Variety.AttackMs / 1000.0 + 0.35;
            Swing(player, AtlasBosses.UniqueMeleeEffect);
            return;
        }
        if (now >= nextBeam && distance <= 55)
        {
            nextBeam = now + 11;
            var beam = Attack(player, AtlasBosses.FragDreadDivineBeamEffect);
            Swing(player, AtlasBosses.FragDreadDivineBeamEffect);
            await Effect(player, playerAt, CoreBeamArt, 1.8, 1.3f);
            pending.Add((now + 1.0, playerAt, 10, beam with { Damage = beam.Damage.Scale(1.3) }, BeamHitArt));
            return;
        }
        if (now >= nextBlast)
        {
            nextBlast = now + 9;
            var blast = Attack(player, AtlasBosses.FragDreadGreaterBlastEffect);
            Swing(player, AtlasBosses.FragDreadGreaterBlastEffect);
            await Effect(player, playerAt, DelayedBlastArt, 1.6, 1.4f);
            pending.Add((now + 1.2, playerAt, 11, blast with { Damage = blast.Damage.Scale(1.35) }, BeamHitArt));
            return;
        }
        if (now >= nextProj)
        {
            nextProj = now + 6;
            Swing(player, AtlasBosses.FragDreadOffsetProjEffect);
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

    protected override async Task Intermission(Player player, double now)
    {
        if (!intermission)
        {
            intermission = true;
            await Shield(player, true);
            await Teleport(player, Stage(player), now);
            Swing(player, AtlasBosses.FragDreadSuperProjEffect);
            intermissionEnds = now + 3.5;
            busyUntil = now + 2.0;
            return;
        }
        if (now >= intermissionEnds)
        {
            intermission = false;
            await Shield(player, false);
            EndIntermission();
        }
    }

    protected override Task OnIntermissionAborted(Player player)
    {
        intermission = false;
        return Task.CompletedTask;
    }
}
