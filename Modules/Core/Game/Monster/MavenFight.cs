using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;

/// <summary>
/// The Maven (<c>TheMaven</c> / <c>TheMavenEnraged_</c> in <c>MavenBoss</c>): multi-phase Maven ↔ Nucleus cycling
/// with life gates at 75% / 50% / 25%, Cascade of Pain, Stand Still laser barrage, Take This cold/fire cannons,
/// Circle Slam, Volatile/Degen Orbs, interactive 3-sector Memory Game with lethal arena explosion on failure, and
/// rotating no-regen beams (<c>Maven's Punishment</c>) in the final phase!
/// </summary>
sealed class MavenFight(uint id, SpawnedMonster boss, Player player) : AtlasBossFight(id, boss, player)
{
    const string Art = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/maven/";
    const string StasisPodArt = Art + "stasis_pod/stasis_pod.ao";
    const string StartFightArt = Art + "start_fight_01/rig.ao";
    const string EyeLaserArt = Art + "carpet_bomb_01/eyelaser.ao";
    const string EyeLaserAoeArt = Art + "carpet_bomb_01/eyelaser_aoe.ao";
    const string ExplosionArt = Art + "explosion/explosion.ao";
    const string VoidZoneArt = Art + "brain/void_zone/rig.ao";
    const string MemoryRevealArt = Art + "memory_game/arena_platform/reveal_platform.ao";
    const string MemoryDmgCircleArt = Art + "memory_game/dmg_circle/dmg_circle.ao";
    const string ArenaExplosionArt = Art + "brain/arena_wide_explosion/wideexplosion.ao";
    const string RotatingOrbArt = Art + "rotating_fbb/orb.ao";
    const string RotatingBeamsArt = Art + "rotating_fbb/orb_beams.ao";
    const string RotatingRingArt = Art + "rotating_fbb/orb_outer_ring.ao";

    protected override string Name => "[Maven]";
    protected override ushort ImmunityRow => AtlasBosses.MavenStasisBuffRow;
    protected override string ShieldArt => StasisPodArt;
    protected override string ShieldOffArt => StartFightArt;

    (int X, int Y)? stage;
    protected override (int X, int Y) Stage(Player player) => stage ??= ArenaCentre(player, boss);

    double nextCascade, nextLaser, nextCannon, nextCircleSlam, nextOrb, nextChase;
    readonly List<(double At, (int X, int Y) Where, double Radius, MonsterAttack Attack, string ImpactArt)> pending = [];
    readonly List<((int X, int Y) Where, double Radius, double Until)> voidZones = [];
    double nextVoidTick;

    // Memory Game state (3 sectors: 0 = NE, 1 = NW, 2 = South).
    enum MemoryStage { None, Showing, Solving }
    MemoryStage memoryStage;
    int[] sequence = [];
    int showIndex, solveIndex;
    double nextFlashAt, solveDeadline, lastStepAt;
    int lastSector = -1;
    uint memoryRingFx;

    // Final-phase rotating no-regen beams (<25% life / after 3rd intermission).
    bool rotatingBeamsActive;
    double beamsStartAt, nextBeamTick;
    uint beamOrbFx, beamSpokesFx, beamRingFx;

    static int SectorOf((int X, int Y) pos, (int X, int Y) centre)
    {
        double angle = Math.Atan2(pos.Y - centre.Y, pos.X - centre.X);
        // Normalize [-pi, pi] -> [0, 2pi), split into 3 equal 120-degree slices.
        if (angle < 0) angle += Math.Tau;
        return Math.Clamp((int)(angle / (Math.Tau / 3.0)), 0, 2);
    }

    static (int X, int Y) SectorMarker((int X, int Y) centre, int sector, int radius = 18)
    {
        double midAngle = (sector + 0.5) * (Math.Tau / 3.0);
        return (centre.X + (int)Math.Round(Math.Cos(midAngle) * radius),
            centre.Y + (int)Math.Round(Math.Sin(midAngle) * radius));
    }

    static string SectorLabel(int sector) => sector switch
    {
        0 => "South-East",
        1 => "South-West",
        _ => "North",
    };

    protected override async Task Ticks(Player player, double now)
    {
        foreach (var hit in pending.Where(p => p.At <= now).ToArray())
        {
            pending.Remove(hit);
            await Effect(player, hit.Where, hit.ImpactArt, 1.5, 1.3f);
            foreach (var peer in Watchers())
                if (Distance(peer.Walk.Position(now), hit.Where) <= hit.Radius)
                    await Hit(peer, hit.Attack, hit.Where);
        }

        voidZones.RemoveAll(v => v.Until <= now);
        if (voidZones.Count > 0 && now >= nextVoidTick)
        {
            nextVoidTick = now + 0.6;
            var dot = Attack(player, AtlasBosses.MavenCircleSlamEffect);
            var tick = dot with { Damage = dot.Damage.Scale(0.25) };
            foreach (var peer in Watchers())
            {
                var pos = peer.Walk.Position(now);
                if (voidZones.Any(v => Distance(pos, v.Where) <= v.Radius))
                    await Hit(peer, tick, pos);
            }
        }

        // Refresh stats once Maven's Punishment (no-regen) expires so pools do not stay stuck at -100% recovery.
        foreach (var peer in Present())
        {
            var st = AtlasBosses.EffectsOf(peer);
            if (st.MavenNoRegenActive && now >= st.MavenNoRegenUntil)
            {
                st.MavenNoRegenActive = false;
                st.MavenNoRegenUntil = 0;
                peer.Area.Buffs.Clear(peer.Id, AtlasBosses.MavenPunishmentRow);
                await RefreshPlayerStats(peer);
            }
        }

        // Final-phase rotating no-regen beams (3 spokes rotating at 0.35 rad/s around arena centre).
        if (rotatingBeamsActive && now >= nextBeamTick)
        {
            nextBeamTick = now + 0.35;
            var centre = Stage(player);
            double baseAngle = (now - beamsStartAt) * 0.35;
            var beamHit = Attack(player, AtlasBosses.MavenCircleSlamEffect);
            var tick = beamHit with { Damage = beamHit.Damage.Scale(0.35) };
            foreach (var peer in Watchers())
            {
                var pos = peer.Walk.Position(now);
                bool touched = false;
                for (int s = 0; s < 3; s++)
                {
                    double a = baseAngle + s * (Math.Tau / 3.0);
                    var tip = (centre.X + (int)(Math.Cos(a) * 45), centre.Y + (int)(Math.Sin(a) * 45));
                    if (LineDistance(pos, centre, tip) <= 3.5) { touched = true; break; }
                }
                if (touched)
                {
                    var st = AtlasBosses.EffectsOf(peer);
                    st.MavenNoRegenActive = true;
                    st.MavenNoRegenUntil = now + 6.0;
                    peer.Area.Buffs.Set(peer.Id, new(AtlasBosses.MavenPunishmentRow, id, U68: Buff.Seconds(6)));
                    await RefreshPlayerStats(peer);
                    await Hit(peer, tick, centre);
                }
            }
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

        // Activate rotating no-regen beams once below 25% life (final phase).
        if (!rotatingBeamsActive && boss.Life * 4 <= boss.MaxLife)
            await StartRotatingBeams(player, now);

        if (now >= nextLaser && distance <= 60)
        {
            nextLaser = now + 13;
            Swing(player, AtlasBosses.MavenLaserBarrageEffect);
            await Effect(player, pos, EyeLaserArt, 2.0, 1.4f);
            await Effect(player, playerAt, EyeLaserAoeArt, 2.0, 1.4f);
            var blast = Attack(player, AtlasBosses.MavenCircleSlamEffect);
            pending.Add((now + 1.0, playerAt, 10, blast with { Damage = blast.Damage.Scale(1.3) }, EyeLaserAoeArt));
            return;
        }
        if (now >= nextCircleSlam && distance <= 40)
        {
            nextCircleSlam = now + 15;
            Swing(player, AtlasBosses.MavenCircleSlamCastEffect);
            var slam = Attack(player, AtlasBosses.MavenCircleSlamEffect);
            await Effect(player, playerAt, ExplosionArt, 2.0, 1.5f);
            pending.Add((now + 1.25, playerAt, 12, slam with { Damage = slam.Damage.Scale(1.5) }, ExplosionArt));
            return;
        }
        if (now >= nextCannon)
        {
            nextCannon = now + 9;
            bool fire = AreaRng(player).Next(2) == 0;
            Swing(player, fire ? AtlasBosses.MavenSuperFireEffect : AtlasBosses.MavenColdCannonEffect);
            return;
        }
        if (now >= nextOrb && voidZones.Count < 4)
        {
            nextOrb = now + 14;
            var at = player.Layout.Near((playerAt.X + AreaRng(player).Next(-8, 9), playerAt.Y + AreaRng(player).Next(-8, 9)));
            await Effect(player, at, VoidZoneArt, 14, 1.3f);
            voidZones.Add((at, 7, now + 14));
            if (Monsters.Find(MavenNucleus.Phase1Orbs[AreaRng(player).Next(MavenNucleus.Phase1Orbs.Length)]) is { Supported: true } orbV)
                await SpawnMonster(player, orbV, boss.Level, MonsterRarity.Normal, at.X, at.Y,
                    owned: (_, m) => { m.NoRewards = true; m.Aggro = true; });
            return;
        }
        if (now >= nextCascade)
        {
            nextCascade = now + 4.5;
            Swing(player, AtlasBosses.MavenProjectileEffect);
            return;
        }
        if (distance > 28 && now >= nextChase)
        {
            nextChase = now + 0.9;
            double fraction = Math.Clamp((distance - 22) / distance, 0, 1);
            var chase = ((int)Math.Round(pos.X + (playerAt.X - pos.X) * fraction),
                (int)Math.Round(pos.Y + (playerAt.Y - pos.Y) * fraction));
            await Move(player, player.Layout.Near(chase), now);
        }
    }

    async Task StartRotatingBeams(Player player, double now)
    {
        rotatingBeamsActive = true;
        beamsStartAt = now;
        var centre = Stage(player);
        beamOrbFx = await Effect(player, centre, RotatingOrbArt, 600, 1.4f);
        beamSpokesFx = await Effect(player, centre, RotatingBeamsArt, 600, 1.8f);
        beamRingFx = await Effect(player, centre, RotatingRingArt, 600, 1.8f);
        await Announce("The Maven's rotating beams activate — touching them disables all Life and Energy Shield recovery!");
    }

    protected override async Task Intermission(Player player, double now)
    {
        var centre = Stage(player);
        if (memoryStage == MemoryStage.None)
        {
            memoryStage = MemoryStage.Showing;
            int completed = Phases?.Completed ?? 0;
            int length = Math.Clamp(3 + completed, 3, 5);
            sequence = new int[length];
            int prev = -1;
            for (int i = 0; i < length; i++)
            {
                int pick = AreaRng(player).Next(3);
                if (pick == prev) pick = (pick + 1 + AreaRng(player).Next(2)) % 3;
                sequence[i] = prev = pick;
            }
            showIndex = 0;
            solveIndex = 0;
            lastSector = -1;
            await Shield(player, true);
            await Teleport(player, centre, now);
            Swing(player, AtlasBosses.MavenMemoryGameEffect);
            memoryRingFx = await Effect(player, centre, MemoryDmgCircleArt, 30, 1.8f);
            await Announce($"MEMORY GAME ({length} steps)! Watch the glowing floor sectors and repeat the order!");
            nextFlashAt = now + 1.0;
            busyUntil = now + 2.0;
            return;
        }

        if (memoryStage == MemoryStage.Showing)
        {
            if (now < nextFlashAt) return;
            if (showIndex < sequence.Length)
            {
                int sector = sequence[showIndex++];
                var spot = player.Layout.Near(SectorMarker(centre, sector));
                await Effect(player, spot, MemoryRevealArt, 1.15, 1.8f);
                await Announce($"Memory Game step {showIndex}/{sequence.Length}: {SectorLabel(sector)}");
                nextFlashAt = now + 1.35;
                return;
            }
            memoryStage = MemoryStage.Solving;
            solveDeadline = now + 12.0;
            lastStepAt = now;
            await Announce("Now step through the sectors in order before the explosion!");
            return;
        }

        // Solving phase: track player moving into each sector in sequence.
        foreach (var peer in Watchers())
        {
            var pos = peer.Walk.Position(now);
            if (Distance(pos, centre) < 5) continue; // Ignore dead-center ambiguity.
            int current = SectorOf(pos, centre);
            if (current != lastSector && now - lastStepAt >= 0.35)
            {
                lastSector = current;
                lastStepAt = now;
                if (current == sequence[solveIndex])
                {
                    solveIndex++;
                    var spot = player.Layout.Near(SectorMarker(centre, current));
                    await Effect(player, spot, MemoryRevealArt, 0.8, 1.4f);
                    if (solveIndex >= sequence.Length)
                    {
                        await FinishMemoryGame(player, now, succeeded: true);
                        return;
                    }
                }
                else
                {
                    solveIndex = 0;
                    peer.Area.Buffs.Set(peer.Id, new(AtlasBosses.MavenMemoryGameDegenRow, id, U68: Buff.Seconds(2)));
                }
            }
        }

        if (now >= solveDeadline)
            await FinishMemoryGame(player, now, succeeded: false);
    }

    async Task FinishMemoryGame(Player player, double now, bool succeeded)
    {
        memoryStage = MemoryStage.None;
        await RemoveEffect(memoryRingFx);
        memoryRingFx = 0;
        var centre = Stage(player);
        if (succeeded)
        {
            await Announce("Memory Game solved! The Maven's shield drops!");
        }
        else
        {
            await Announce("Memory Game failed! The Maven unleashes a lethal detonation!");
            await Effect(player, centre, ArenaExplosionArt, 2.8, 2.4f);
            var nuke = Attack(player, AtlasBosses.MavenCircleSlamEffect);
            var lethal = nuke with { Damage = nuke.Damage.Scale(3.5) };
            foreach (var peer in Watchers()) await Hit(peer, lethal, centre);
        }
        await Shield(player, false);
        EndIntermission();
        if (!rotatingBeamsActive && (Phases?.Completed ?? 0) >= 2)
            await StartRotatingBeams(player, now);
        nextCascade = now + 2.0;
        busyUntil = now + 1.5;
    }

    protected override async Task OnIntermissionAborted(Player player)
    {
        memoryStage = MemoryStage.None;
        showIndex = 0;
        solveIndex = 0;
        lastSector = -1;
        await RemoveEffect(memoryRingFx);
        memoryRingFx = 0;
    }

    protected override async Task OnCleanup(Player player)
    {
        await RemoveEffect(memoryRingFx);
        await RemoveEffect(beamOrbFx);
        await RemoveEffect(beamSpokesFx);
        await RemoveEffect(beamRingFx);
        foreach (var peer in Present())
        {
            var st = AtlasBosses.EffectsOf(peer);
            st.MavenNoRegenActive = false;
            st.MavenNoRegenUntil = 0;
            peer.Area.Buffs.Clear(peer.Id, AtlasBosses.MavenPunishmentRow);
            peer.Area.Buffs.Clear(peer.Id, AtlasBosses.MavenMemoryGameDegenRow);
            await RefreshPlayerStats(peer);
        }
    }
}

/// <summary>
/// Nucleus of the Maven (<c>MavenBrainBoss</c> in <c>MavenBoss</c>): drives the brain's full GrantedEffects kit
/// (Charge Beam, Ion Cannon, Beam Blast, Large/Small Projectile Barrages, Decelerating Projectiles) with boss life bar
/// and a 50% life stasis intermission spawning Volatile Cores.
/// </summary>
sealed class MavenBrainFight(uint id, SpawnedMonster boss, Player player) : AtlasBossFight(id, boss, player)
{
    const string Art = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/maven/brain/";
    const string ShieldBeamArt = Art + "beam_shield/brainbeamshield.ao";
    const string RectBeamArt = Art + "rectangle_beam/rectbeam.ao";
    const string CenterExplodeArt = Art + "center_explosion/arena_explosion.ao";
    const string VoidZoneArt = Art + "void_zone/rig.ao";

    protected override string Name => "[MavenBrain]";
    protected override ushort ImmunityRow => AtlasBosses.MavenStasisBuffRow;
    protected override string ShieldArt => ShieldBeamArt;
    protected override string ShieldOffArt => CenterExplodeArt;

    (int X, int Y)? stage;
    protected override (int X, int Y) Stage(Player player) => stage ??= ArenaCentre(player, boss);

    double nextBlast, nextLargeBarrage, nextBeam, nextDecel;
    readonly List<(double At, (int X, int Y) Where, double Radius, MonsterAttack Attack, string ImpactArt)> pending = [];
    bool intermission;
    double intermissionEnds;

    protected override async Task Ticks(Player player, double now)
    {
        foreach (var hit in pending.Where(p => p.At <= now).ToArray())
        {
            pending.Remove(hit);
            await Effect(player, hit.Where, hit.ImpactArt, 1.8, 1.5f);
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

        if (now >= nextBeam)
        {
            nextBeam = now + 11;
            var cannon = Attack(player, AtlasBosses.BrainIonCannonEffect);
            Swing(player, AtlasBosses.BrainChargeBeamEffect);
            await Effect(player, playerAt, RectBeamArt, 2.2, 1.5f);
            pending.Add((now + 1.3, playerAt, 11, cannon with { Damage = cannon.Damage.Scale(1.4) }, CenterExplodeArt));
            return;
        }
        if (now >= nextLargeBarrage)
        {
            nextLargeBarrage = now + 9;
            Swing(player, AtlasBosses.BrainLargeBarrageEffect);
            return;
        }
        if (now >= nextDecel)
        {
            nextDecel = now + 13;
            Swing(player, AtlasBosses.BrainDecelProjEffect);
            await Effect(player, playerAt, VoidZoneArt, 4.0, 1.2f);
            return;
        }
        if (now >= nextBlast)
        {
            nextBlast = now + 4.0;
            Swing(player, AtlasBosses.BrainProjBlastEffect);
        }
    }

    protected override async Task Intermission(Player player, double now)
    {
        if (!intermission)
        {
            intermission = true;
            await Shield(player, true);
            var centre = Stage(player);
            await Announce("Nucleus of the Maven surges with Volatile Cores!");
            foreach (var (orbId, ox, oy) in MavenNucleus.Around(MavenNucleus.Phase2Cores, centre, MavenNucleus.Phase2Radius))
            {
                if (Monsters.Find(orbId) is { Supported: true } v)
                    await SpawnMonster(player, v, boss.Level, MonsterRarity.Normal, ox, oy,
                        owned: (_, m) => { m.NoRewards = true; m.Aggro = true; });
            }
            Swing(player, AtlasBosses.BrainSmallBarrageEffect);
            intermissionEnds = now + 5.0;
            busyUntil = now + 2.0;
            return;
        }
        if (now >= intermissionEnds)
        {
            intermission = false;
            await Shield(player, false);
            EndIntermission();
            nextBeam = now + 2.5;
            busyUntil = now + 1.0;
        }
    }

    protected override Task OnIntermissionAborted(Player player)
    {
        intermission = false;
        return Task.CompletedTask;
    }
}
