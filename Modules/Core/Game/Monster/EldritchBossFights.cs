using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;

/// <summary>
/// The Infinite Hunger (<c>DoomBoss_</c> in <c>MapWorldsPrimordialBoss1</c>): melee, corrosive vomit channel/mortars,
/// drill charge, slam, and the Drowning Mire submerge phase where players navigate mire pools to reach the escape
/// vortex before corrosive drowning stacks overwhelm them.
/// </summary>
sealed class InfiniteHungerFight(uint id, SpawnedMonster boss, Player player) : AtlasBossFight(id, boss, player)
{
    const string Art = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/doompersonified/ao/";
    const string SlamArt = Art + "doom_slam.ao";
    const string VomitImpactArt = Art + "vomit_impact.ao";
    const string DrillLineArt = Art + "drill_line.ao";
    const string SubmergeArt = Art + "submerge_ground.ao";
    const string EmergeArt = Art + "submerge_emerge.ao";
    const string TarArt = Art + "tar_grd.ao";
    const string BubblesArt = Art + "bubbles_bursting.ao";
    const string EscapePortalArt = Art + "doom_transition.ao";

    protected override string Name => "[InfiniteHunger]";
    protected override ushort ImmunityRow => AtlasBosses.ElderImmunityRow;
    protected override string ShieldArt => Art + "doom_fadeout.ao";
    protected override string ShieldOffArt => Art + "doom_fadein.ao";

    (int X, int Y)? stage;
    protected override (int X, int Y) Stage(Player player) => stage ??= ArenaCentre(player, boss);

    double nextMelee, nextSlam, nextVomit, nextDrill, nextChase;
    readonly List<(double At, (int X, int Y) Where, double Radius, MonsterAttack Attack, string ImpactArt)> pending = [];
    readonly List<((int X, int Y) Where, double Radius, double Until)> mirePools = [];
    double nextPoolTick;

    // Drowning Mire submerge phase.
    bool submerged;
    double mireEnds, nextDrownStack;
    (int X, int Y) escapeAt;
    uint escapeFx;

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

        mirePools.RemoveAll(p => p.Until <= now);
        if (mirePools.Count > 0 && now >= nextPoolTick)
        {
            nextPoolTick = now + 0.6;
            var baseAttack = Attack(player, AtlasBosses.HungerGooEruptionEffect);
            var tickAttack = baseAttack with { Damage = baseAttack.Damage.Scale(0.28) };
            foreach (var peer in Watchers())
            {
                var pos = peer.Walk.Position(now);
                if (mirePools.Any(pool => Distance(pos, pool.Where) <= pool.Radius))
                    await Hit(peer, tickAttack, pos);
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

        if (distance <= Variety.AttackRange + 1 && now >= nextMelee)
        {
            nextMelee = now + Variety.AttackMs / 1000.0 + 0.35;
            Swing(player, AtlasBosses.UniqueMeleeEffect);
            return;
        }
        if (now >= nextSlam && distance <= 28)
        {
            nextSlam = now + 10;
            var slam = Attack(player, AtlasBosses.HungerSumoSlamEffect);
            slam = slam with { Damage = slam.Damage.Scale(1.4) };
            Swing(player, AtlasBosses.HungerSlamCastEffect);
            await Effect(player, playerAt, SlamArt, 2.2, 1.4f);
            pending.Add((now + 1.3, playerAt, 10, slam, SlamArt));
            return;
        }
        if (now >= nextVomit && distance <= 55)
        {
            nextVomit = now + 13;
            var vomit = Attack(player, AtlasBosses.HungerVomitMortarEffect);
            Swing(player, AtlasBosses.HungerVomitChannelEffect);
            for (int i = 0; i < 4; i++)
            {
                double angle = Math.Tau * i / 4 + AreaRng(player).NextDouble() * 0.5;
                var where = player.Layout.Near((playerAt.X + (int)(Math.Cos(angle) * 8), playerAt.Y + (int)(Math.Sin(angle) * 8)));
                await Effect(player, where, TarArt, 8.0, 1.3f);
                mirePools.Add((where, 7, now + 8.0));
                pending.Add((now + 0.8 + i * 0.25, where, 6, vomit, VomitImpactArt));
            }
            return;
        }
        if (now >= nextDrill && distance is > 16 and < 65)
        {
            nextDrill = now + 15;
            await Effect(player, pos, DrillLineArt, 1.5);
            Swing(player, AtlasBosses.HungerDrillChargeEffect);
            var chargeTo = player.Layout.Near(playerAt);
            await Teleport(player, chargeTo, now);
            var stomp = Attack(player, AtlasBosses.HungerStompEffect);
            pending.Add((now + 0.6, chargeTo, 9, stomp, BubblesArt));
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
        if (!submerged)
        {
            submerged = true;
            await Shield(player, true);
            await Effect(player, boss.Walk.Position(now), SubmergeArt, 2.5, 1.6f);
            await Teleport(player, Stage(player), now);
            var centre = Stage(player);
            // Place escape vortex at a walkable spot ~34 cells from centre; players wade through the mire to escape.
            double dir = AreaRng(player).NextDouble() * Math.Tau;
            escapeAt = player.Layout.Near((centre.X + (int)(Math.Cos(dir) * 34), centre.Y + (int)(Math.Sin(dir) * 34)));
            escapeFx = await Effect(player, escapeAt, EscapePortalArt, 16, 1.8f);
            for (int i = 0; i < 6; i++)
            {
                double a = Math.Tau * i / 6;
                var poolAt = player.Layout.Near((centre.X + (int)(Math.Cos(a) * 18), centre.Y + (int)(Math.Sin(a) * 18)));
                await Effect(player, poolAt, TarArt, 14, 1.6f);
                mirePools.Add((poolAt, 9, now + 14));
            }
            mireEnds = now + 14;
            nextDrownStack = now + 1.5;
            busyUntil = now + 1.5;
            await Announce("The Infinite Hunger drags you into the Drowning Mire! Reach the escape vortex!");
            return;
        }

        // Check if any player reached the escape vortex to return immediately.
        bool escaped = Watchers().Any(p => Distance(p.Walk.Position(now), escapeAt) <= 8);
        if (escaped || now >= mireEnds)
        {
            submerged = false;
            await RemoveEffect(escapeFx);
            escapeFx = 0;
            await ClearDrowningDebuffs();
            await Shield(player, false);
            await Effect(player, boss.Walk.Position(now), EmergeArt, 2.2, 1.6f);
            EndIntermission();
            nextSlam = now + 3;
            busyUntil = now + 1.5;
            if (escaped) await Announce("You escaped the Drowning Mire!");
            return;
        }

        if (now >= nextDrownStack)
        {
            nextDrownStack = now + 1.5;
            var mireDot = Attack(player, AtlasBosses.HungerGooEruptionEffect);
            var tick = mireDot with { Damage = mireDot.Damage.Scale(0.25) };
            foreach (var peer in Watchers())
            {
                var st = AtlasBosses.EffectsOf(peer);
                st.HungerDrowningStacks = Math.Min(20, st.HungerDrowningStacks + 1);
                peer.Area.Buffs.Set(peer.Id, new(AtlasBosses.HungerDrowningRow, id, Stacks: (ushort)st.HungerDrowningStacks, U68: Buff.Seconds(6)));
                await RefreshPlayerStats(peer);
                await Hit(peer, tick, peer.Walk.Position(now));
            }
        }
    }

    async Task ClearDrowningDebuffs()
    {
        foreach (var peer in Present())
        {
            var st = AtlasBosses.EffectsOf(peer);
            if (st.HungerDrowningStacks > 0)
            {
                st.HungerDrowningStacks = 0;
                peer.Area.Buffs.Clear(peer.Id, AtlasBosses.HungerDrowningRow);
                await RefreshPlayerStats(peer);
            }
        }
    }

    protected override async Task OnIntermissionAborted(Player player)
    {
        submerged = false;
        await RemoveEffect(escapeFx);
        escapeFx = 0;
        await ClearDrowningDebuffs();
    }

    protected override Task OnCleanup(Player player) => ClearDrowningDebuffs();
}

/// <summary>
/// The Black Star (<c>BlackStarBoss</c> in <c>MapWorldsPrimordialBoss2</c>): Solar Flare / Astral Avalanche half-plane
/// stack mechanics, 4 protective arena pillars, fire leap / cold cascade / comet barrage / laser spears, and Divergent &amp;
/// Warped Reflection clones (<c>BlackStarBossCasterClone</c> / <c>BlackStarBossMeleeClone</c>).
/// </summary>
sealed class BlackStarFight(uint id, SpawnedMonster boss, Player player) : AtlasBossFight(id, boss, player)
{
    const string Art = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/theblackstar/ao/";
    const string PillarArt = Art + "blackstarpillars.ao";
    const string FirePillarArt = Art + "fire_pillar_effect.ao";
    const string IcePillarArt = Art + "ice_pillar_effect.ao";
    const string FireSlamArt = Art + "fire_slam.ao";
    const string ColdCascadeArt = Art + "cold_cascade.ao";
    const string FireBeamArt = Art + "fire_beam.ao";
    const string IceBeamArt = Art + "ice_beam.ao";
    const string IceOrbSlamArt = Art + "ice_orb_slam.ao";

    protected override string Name => "[BlackStar]";
    protected override ushort ImmunityRow => AtlasBosses.ShaperImmunityRow;
    protected override string ShieldArt => Art + "pillar_shield_fire.ao";
    protected override string ShieldOffArt => Art + "pillar_shield_cold.ao";

    (int X, int Y)? stage;
    protected override (int X, int Y) Stage(Player player) => stage ??= ArenaCentre(player, boss);

    double nextMelee, nextLeap, nextCascade, nextComet, nextSpears, nextChase, nextZoneTick;
    readonly List<((int X, int Y) At, uint Fx, bool Intact)> pillars = [];
    bool pillarsPlaced;
    readonly List<(double At, (int X, int Y) Where, double Radius, MonsterAttack Attack, string ImpactArt, bool PillarBlocked)> pending = [];
    bool intermission;
    int waves;
    double waveAt;

    async Task EnsurePillars(Player player)
    {
        if (pillarsPlaced) return;
        pillarsPlaced = true;
        var centre = Stage(player);
        for (int i = 0; i < 4; i++)
        {
            double a = Math.PI / 4 + i * Math.PI / 2;
            var at = player.Layout.Near((centre.X + (int)(Math.Cos(a) * 20), centre.Y + (int)(Math.Sin(a) * 20)));
            uint fx = await Effect(player, at, PillarArt, 600, 1.2f);
            pillars.Add((at, fx, true));
        }
    }

    bool ShelteredByPillar((int X, int Y) playerAt, (int X, int Y) sourceAt)
    {
        foreach (var (at, _, intact) in pillars)
        {
            if (!intact) continue;
            if (Distance(playerAt, at) <= 7 && Distance(playerAt, sourceAt) > Distance(at, sourceAt) - 3)
                return true;
        }
        return false;
    }

    protected override async Task Ticks(Player player, double now)
    {
        await EnsurePillars(player);
        foreach (var strike in pending.Where(p => p.At <= now).ToArray())
        {
            pending.Remove(strike);
            await Effect(player, strike.Where, strike.ImpactArt, 1.6);
            foreach (var peer in Watchers())
            {
                var pos = peer.Walk.Position(now);
                if (strike.PillarBlocked && ShelteredByPillar(pos, strike.Where)) continue;
                if (Distance(pos, strike.Where) <= strike.Radius) await Hit(peer, strike.Attack, strike.Where);
            }
        }

        // Solar Flare (east of centre) vs Astral Avalanche (west of centre) stacking zone mechanic.
        if (now >= nextZoneTick)
        {
            nextZoneTick = now + 2.0;
            var centre = Stage(player);
            foreach (var peer in Watchers())
            {
                var pos = peer.Walk.Position(now);
                var st = AtlasBosses.EffectsOf(peer);
                bool solarSide = pos.X >= centre.X;
                if (solarSide)
                {
                    st.SolarFlareStacks = Math.Min(15, st.SolarFlareStacks + 1);
                    if (st.AstralAvalancheStacks > 0) st.AstralAvalancheStacks = Math.Max(0, st.AstralAvalancheStacks - 2);
                }
                else
                {
                    st.AstralAvalancheStacks = Math.Min(15, st.AstralAvalancheStacks + 1);
                    if (st.SolarFlareStacks > 0) st.SolarFlareStacks = Math.Max(0, st.SolarFlareStacks - 2);
                }
                if (st.SolarFlareStacks > 0)
                    peer.Area.Buffs.Set(peer.Id, new(AtlasBosses.BlackStarSunlightRow, id, Stacks: (ushort)st.SolarFlareStacks, U68: Buff.Seconds(8)));
                else
                    peer.Area.Buffs.Clear(peer.Id, AtlasBosses.BlackStarSunlightRow);
                if (st.AstralAvalancheStacks > 0)
                    peer.Area.Buffs.Set(peer.Id, new(AtlasBosses.BlackStarMoonlightRow, id, Stacks: (ushort)st.AstralAvalancheStacks, U68: Buff.Seconds(8)));
                else
                    peer.Area.Buffs.Clear(peer.Id, AtlasBosses.BlackStarMoonlightRow);
                await RefreshPlayerStats(peer);
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

        if (distance <= Variety.AttackRange + 1 && now >= nextMelee)
        {
            nextMelee = now + Variety.AttackMs / 1000.0 + 0.35;
            Swing(player, AtlasBosses.UniqueMeleeEffect);
            return;
        }
        if (now >= nextLeap && distance is > 14 and < 60)
        {
            nextLeap = now + 11;
            Swing(player, AtlasBosses.BlackStarFireLeapEffect);
            var to = player.Layout.Near(playerAt);
            await Teleport(player, to, now);
            var impact = Attack(player, AtlasBosses.BlackStarFireLeapImpactEffect);
            pending.Add((now + 0.7, to, 10, impact, FireSlamArt, false));
            return;
        }
        if (now >= nextCascade && distance <= 45)
        {
            nextCascade = now + 9;
            var cascade = Attack(player, AtlasBosses.BlackStarColdCascadeEffect);
            Swing(player, AtlasBosses.BlackStarColdCascadeEffect);
            await Effect(player, playerAt, ColdCascadeArt, 2.0, 1.3f);
            pending.Add((now + 0.9, playerAt, 9, cascade, IceOrbSlamArt, false));
            return;
        }
        if (now >= nextComet)
        {
            nextComet = now + 13;
            Swing(player, AtlasBosses.BlackStarCometBarrageEffect);
            var impact = Attack(player, AtlasBosses.BlackStarCometImpactEffect);
            for (int i = 0; i < 3; i++)
            {
                var at = player.Layout.Near((playerAt.X + AreaRng(player).Next(-12, 13), playerAt.Y + AreaRng(player).Next(-12, 13)));
                await Effect(player, at, IceBeamArt, 1.6);
                pending.Add((now + 1.1 + i * 0.3, at, 8, impact, IceOrbSlamArt, true));
            }
            return;
        }
        if (now >= nextSpears)
        {
            nextSpears = now + 15;
            Swing(player, AtlasBosses.BlackStarLaserSpearsEffect);
            var spear = Attack(player, AtlasBosses.BlackStarLaserSpearsEffect);
            await Effect(player, pos, FireBeamArt, 2.0, 1.5f);
            pending.Add((now + 1.0, playerAt, 8, spear, FireSlamArt, true));
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
            await Announce("The Black Star channels Solar Flare & Astral Avalanche! Take shelter behind a pillar!");
            await SpawnClone(player, AtlasBosses.BlackStarCasterClone, (centre.X - 14, centre.Y));
            await SpawnClone(player, AtlasBosses.BlackStarMeleeClone, (centre.X + 14, centre.Y));
            Swing(player, AtlasBosses.BlackStarSpawnCasterEffect);
            waveAt = now + 2.5;
            busyUntil = now + 2.5;
            return;
        }
        if (now < waveAt) return;
        if (waves >= 2)
        {
            intermission = false;
            await Shield(player, false);
            EndIntermission();
            nextComet = now + 3;
            busyUntil = now + 1.2;
            return;
        }
        waves++;
        var centre2 = Stage(player);
        bool solar = waves % 2 == 1;
        await Effect(player, centre2, solar ? FirePillarArt : IcePillarArt, 2.5, 2.2f);
        var blast = Attack(player, solar ? AtlasBosses.BlackStarFireLeapImpactEffect : AtlasBosses.BlackStarCometImpactEffect);
        pending.Add((now + 1.4, centre2, 55, blast with { Damage = blast.Damage.Scale(1.6) }, solar ? FireSlamArt : IceOrbSlamArt, true));
        waveAt = now + 3.2;
        busyUntil = now + 2.5;
    }

    async Task SpawnClone(Player player, string varietyId, (int X, int Y) near)
    {
        if (Monsters.Find(varietyId) is not { Supported: true } v || v.Id != varietyId) return;
        var at = player.Layout.Near(near);
        await SpawnMonster(player, v, boss.Level, MonsterRarity.Unique, at.X, at.Y,
            owned: (_, m) => { m.NoRewards = true; m.Aggro = true; });
    }

    protected override Task OnIntermissionAborted(Player player)
    {
        intermission = false;
        waves = 0;
        return Task.CompletedTask;
    }

    protected override async Task OnCleanup(Player player)
    {
        foreach (var peer in Present())
        {
            var st = AtlasBosses.EffectsOf(peer);
            st.SolarFlareStacks = 0;
            st.AstralAvalancheStacks = 0;
            peer.Area.Buffs.Clear(peer.Id, AtlasBosses.BlackStarSunlightRow);
            peer.Area.Buffs.Clear(peer.Id, AtlasBosses.BlackStarMoonlightRow);
            await RefreshPlayerStats(peer);
        }
    }
}

/// <summary>
/// The Eater of Worlds (<c>ConsumeBoss</c> in <c>MapWorldsPrimordialBoss4</c>): Hunger beam, tentacle slams, chasing
/// drown orbs, and the Inescapable Doom energized sphere phase (walk onto all spheres to disarm before detonation).
/// </summary>
sealed class EaterOfWorldsFight(uint id, SpawnedMonster boss, Player player) : AtlasBossFight(id, boss, player)
{
    const string Art = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/thetangle/ao/";
    const string BeamBlastArt = Art + "beam_blast.ao";
    const string FocusBeamArt = Art + "focus_beam.ao";
    const string FusionChargeArt = Art + "fusion_charge_beam.ao";
    const string FusionExplodeArt = Art + "fusion_beam_explode.ao";
    const string EnergizedOrbArt = Art + "mega_beam_orb.ao";
    const string ChasingOrbArt = Art + "chasing_orb.ao";
    const string DrownArt = Art + "drown.ao";
    const string SlamArt = Art + "slam.ao";
    const string TentacleHoleArt = Art + "tentacle_hole.ao";
    const string TeleportInArt = Art + "teleport_in.ao";
    const string TeleportOutArt = Art + "teleport_out.ao";

    protected override string Name => "[EaterOfWorlds]";
    protected override ushort ImmunityRow => AtlasBosses.ElderImmunityRow;
    protected override string ShieldArt => FusionChargeArt;
    protected override string ShieldOffArt => TeleportInArt;

    (int X, int Y)? stage;
    protected override (int X, int Y) Stage(Player player) => stage ??= ArenaCentre(player, boss);

    double nextMelee, nextBeam, nextTentacles, nextDashSlam, nextDrownOrb, nextChase;
    double nextDrownTick;
    readonly List<(double At, (int X, int Y) Where, double Radius, MonsterAttack Attack, string ImpactArt)> pending = [];
    // Chasing drown orbs.
    readonly List<DrownOrb> drownOrbs = [];
    sealed class DrownOrb(uint fx, (int X, int Y) at, double ends)
    {
        public uint Fx = fx;
        public (int X, int Y) At = at;
        public double Ends = ends;
        public double NextMove;
    }

    // Inescapable Doom energized sphere intermission.
    bool doomPhase;
    double doomDetonateAt;
    readonly List<(uint Fx, (int X, int Y) At)> spheres = [];

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

        // Advance chasing drown orbs and track Drowning debuff independently per player.
        foreach (var orb in drownOrbs.ToArray())
        {
            if (orb.Ends <= now)
            {
                drownOrbs.Remove(orb);
                await RemoveEffect(orb.Fx);
                continue;
            }
            var target = Watchers().MinBy(p => Distance(orb.At, p.Walk.Position(now)));
            if (target is null) continue;
            if (now >= orb.NextMove)
            {
                orb.NextMove = now + 0.6;
                var to = ArenaHazards.Toward(orb.At, target.Walk.Position(now), 4.5);
                orb.At = player.Layout.Near(to);
                await RemoveEffect(orb.Fx);
                orb.Fx = await Effect(player, orb.At, ChasingOrbArt, Math.Max(0.8, orb.Ends - now), 1.1f);
            }
        }
        MonsterAttack? drownTick = null;
        // The drown hit ticks like the other ground degens (0.6 s), not every frame.
        bool drownTickDue = now >= nextDrownTick;
        foreach (var peer in Present())
        {
            var st = AtlasBosses.EffectsOf(peer);
            var touching = peer.PlayerLife > 0
                ? drownOrbs.FirstOrDefault(orb => Distance(peer.Walk.Position(now), orb.At) <= 7)
                : null;
            if (touching is not null)
            {
                if (!st.EaterDrowning)
                {
                    st.EaterDrowning = true;
                    peer.Area.Buffs.Set(peer.Id, new(AtlasBosses.EaterDrowningRow, id, U68: Buff.Seconds(3)));
                    await RefreshPlayerStats(peer);
                }
                if (drownTickDue)
                {
                    drownTick ??= Attack(player, AtlasBosses.EaterSlamEffect) with { Damage = Attack(player, AtlasBosses.EaterSlamEffect).Damage.Scale(0.25) };
                    await Hit(peer, drownTick, touching.At);
                }
            }
            else if (st.EaterDrowning)
            {
                st.EaterDrowning = false;
                peer.Area.Buffs.Clear(peer.Id, AtlasBosses.EaterDrowningRow);
                await RefreshPlayerStats(peer);
            }
        }
        if (drownTick is not null) nextDrownTick = now + 0.6;
    }

    protected override async Task Act(Player player, double now)
    {
        var watchers = Watchers();
        if (watchers.Length == 0) return;
        var pos = boss.Walk.Position(now);
        var target = watchers.MinBy(p => Distance(pos, p.Walk.Position(now)))!;
        var playerAt = target.Walk.Position(now);
        double distance = Distance(pos, playerAt);

        if (distance <= Math.Min(18, Variety.AttackRange) && now >= nextMelee)
        {
            nextMelee = now + Variety.AttackMs / 1000.0 + 0.35;
            Swing(player, AtlasBosses.UniqueMeleeEffect);
            return;
        }
        if (now >= nextBeam && distance <= 65)
        {
            nextBeam = now + 11;
            var beam = Attack(player, AtlasBosses.EaterDisintegrateBeamEffect);
            Swing(player, AtlasBosses.EaterDisintegrateBeamEffect);
            await Effect(player, pos, FocusBeamArt, 2.2, 1.5f);
            await Effect(player, playerAt, BeamBlastArt, 2.2, 1.4f);
            pending.Add((now + 1.1, playerAt, 10, beam with { Damage = beam.Damage.Scale(1.35) }, BeamBlastArt));
            return;
        }
        if (now >= nextTentacles)
        {
            nextTentacles = now + 13;
            var slam = Attack(player, AtlasBosses.EaterTentacleSlamEffect);
            Swing(player, AtlasBosses.EaterOmegaTentaclesEffect);
            for (int i = 0; i < 5; i++)
            {
                double a = Math.Tau * i / 5 + AreaRng(player).NextDouble() * 0.4;
                var where = player.Layout.Near((playerAt.X + (int)(Math.Cos(a) * 10), playerAt.Y + (int)(Math.Sin(a) * 10)));
                await Effect(player, where, TentacleHoleArt, 2.0);
                pending.Add((now + 1.15, where, 6, slam, SlamArt));
            }
            return;
        }
        if (now >= nextDashSlam && distance is > 16 and < 65)
        {
            nextDashSlam = now + 15;
            await Effect(player, pos, TeleportOutArt, 1.0);
            Swing(player, AtlasBosses.EaterDashSlamCastEffect);
            var to = player.Layout.Near(playerAt);
            await Teleport(player, to, now);
            await Effect(player, to, TeleportInArt, 1.0);
            var dashSlam = Attack(player, AtlasBosses.EaterDashSlamEffect);
            pending.Add((now + 0.85, to, 11, dashSlam with { Damage = dashSlam.Damage.Scale(1.3) }, SlamArt));
            return;
        }
        if (now >= nextDrownOrb && drownOrbs.Count < 2)
        {
            nextDrownOrb = now + 16;
            var orbAt = player.Layout.Near((pos.X + (playerAt.X - pos.X) / 2, pos.Y + (playerAt.Y - pos.Y) / 2));
            uint fx = await Effect(player, orbAt, DrownArt, 10, 1.2f);
            drownOrbs.Add(new DrownOrb(fx, orbAt, now + 10));
            return;
        }
        if (distance > 18 && now >= nextChase)
        {
            nextChase = now + 0.75;
            double fraction = Math.Clamp((distance - 15) / distance, 0, 1);
            var chase = ((int)Math.Round(pos.X + (playerAt.X - pos.X) * fraction),
                (int)Math.Round(pos.Y + (playerAt.Y - pos.Y) * fraction));
            await Move(player, player.Layout.Near(chase), now);
        }
    }

    protected override async Task Intermission(Player player, double now)
    {
        if (!doomPhase)
        {
            doomPhase = true;
            spheres.Clear();
            await Shield(player, true);
            var centre = Stage(player);
            await Teleport(player, centre, now);
            await Announce("INESCAPABLE DOOM! Step into all 6 energized spheres to disarm them before detonation!");
            Swing(player, AtlasBosses.EaterDoomLaserEffect);
            for (int i = 0; i < 6; i++)
            {
                double angle = Math.Tau * i / 6;
                var at = player.Layout.Near((centre.X + (int)(Math.Cos(angle) * 24), centre.Y + (int)(Math.Sin(angle) * 24)));
                uint fx = await Effect(player, at, EnergizedOrbArt, 16, 1.4f);
                spheres.Add((fx, at));
            }
            doomDetonateAt = now + 14;
            busyUntil = now + 1.5;
            return;
        }

        // Check if any player steps onto an energized sphere to disarm it.
        foreach (var sphere in spheres.ToArray())
        {
            foreach (var peer in Watchers())
            {
                if (Distance(peer.Walk.Position(now), sphere.At) <= 7.5)
                {
                    spheres.Remove(sphere);
                    await RemoveEffect(sphere.Fx);
                    peer.Area.Buffs.Set(peer.Id, new(AtlasBosses.EaterLivewireRow, id, U68: Buff.Seconds(3)));
                    break;
                }
            }
        }

        if (spheres.Count == 0)
        {
            // All spheres disarmed — interrupt Inescapable Doom!
            doomPhase = false;
            await Shield(player, false);
            Swing(player, AtlasBosses.EaterBonkedEffect);
            EndIntermission();
            nextBeam = now + 4;
            busyUntil = now + 2.5;
            await Announce("All energized spheres disarmed! The Eater of Worlds is staggered!");
            return;
        }

        if (now >= doomDetonateAt)
        {
            int remaining = spheres.Count;
            foreach (var sphere in spheres.ToArray()) await RemoveEffect(sphere.Fx);
            spheres.Clear();
            doomPhase = false;
            await Shield(player, false);
            var centre = Stage(player);
            await Effect(player, centre, FusionExplodeArt, 3.0, 2.5f);
            Swing(player, AtlasBosses.EaterUltimateNukeEffect);
            var nuke = Attack(player, AtlasBosses.EaterUltimateNukeEffect);
            var scaled = nuke with { Damage = nuke.Damage.Scale(1.8 + remaining * 0.35) };
            foreach (var peer in Watchers()) await Hit(peer, scaled, centre);
            EndIntermission();
            nextBeam = now + 3;
            busyUntil = now + 2.0;
        }
    }

    protected override async Task OnIntermissionAborted(Player player)
    {
        doomPhase = false;
        foreach (var sphere in spheres.ToArray()) await RemoveEffect(sphere.Fx);
        spheres.Clear();
    }

    protected override async Task OnCleanup(Player player)
    {
        foreach (var sphere in spheres.ToArray()) await RemoveEffect(sphere.Fx);
        spheres.Clear();
        foreach (var orb in drownOrbs.ToArray()) await RemoveEffect(orb.Fx);
        drownOrbs.Clear();
        foreach (var peer in Present())
        {
            var st = AtlasBosses.EffectsOf(peer);
            st.EaterDrowning = false;
            peer.Area.Buffs.Clear(peer.Id, AtlasBosses.EaterDrowningRow);
            peer.Area.Buffs.Clear(peer.Id, AtlasBosses.EaterLivewireRow);
            await RefreshPlayerStats(peer);
        }
    }
}

/// <summary>
/// The Searing Exarch (<c>CleansingBoss</c> in <c>MapWorldsPrimordialBoss3</c>): Incineration flame wave, Annihilation
/// slam, Searing Runes ground hazards, and the Rolling Meteor (ball) intermission phase with guaranteed navigable safe lanes.
/// </summary>
sealed class SearingExarchFight(uint id, SpawnedMonster boss, Player player) : AtlasBossFight(id, boss, player)
{
    const string Art = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/cleansingfire/ao/";
    const string WaveArt = Art + "cleansing_wave_boss.ao";
    const string FlameWallArt = Art + "flame_wall.ao";
    const string LanternExplodeArt = Art + "lantern_explode.ao";
    const string SnakeRuneArt = Art + "snake_rune.ao";
    const string MeteorBallArt = Art + "bug_proj_01.ao";
    const string MeteorImpactArt = Art + "bug_impact.ao";
    const string ArenaGlowArt = Art + "arena_glow.ao";
    const string TeleportArt = Art + "teleport.ao";

    protected override string Name => "[SearingExarch]";
    protected override ushort ImmunityRow => AtlasBosses.ShaperImmunityRow;
    protected override string ShieldArt => ArenaGlowArt;
    protected override string ShieldOffArt => TeleportArt;

    (int X, int Y)? stage;
    protected override (int X, int Y) Stage(Player player) => stage ??= ArenaCentre(player, boss);

    double nextMelee, nextIncinerate, nextAnnihilation, nextRunes, nextDashSlash, nextChase;
    readonly List<(double At, (int X, int Y) Where, double Radius, MonsterAttack Attack, string ImpactArt)> pending = [];
    readonly List<((int X, int Y) Where, uint Fx, double Until)> runes = [];
    double nextRuneTick;

    // Rolling Meteor (ball) phase with navigable lanes.
    bool meteorPhase;
    int meteorWave;
    double nextWaveAt, nextBallStep;
    readonly List<RollingBall> balls = [];
    sealed class RollingBall(uint fx, double x, double y, double vx, double vy, double ends)
    {
        public uint Fx = fx;
        public double X = x, Y = y, Vx = vx, Vy = vy, Ends = ends;
    }

    protected override async Task Ticks(Player player, double now)
    {
        foreach (var hit in pending.Where(p => p.At <= now).ToArray())
        {
            pending.Remove(hit);
            await Effect(player, hit.Where, hit.ImpactArt, 1.8, 1.4f);
            foreach (var peer in Watchers())
                if (Distance(peer.Walk.Position(now), hit.Where) <= hit.Radius)
                    await Hit(peer, hit.Attack, hit.Where);
        }

        foreach (var rune in runes.Where(r => r.Until <= now).ToArray())
        {
            runes.Remove(rune);
            await RemoveEffect(rune.Fx);
        }
        // Expire Cauterised Flesh stacks cleanly once their 8 s duration elapses.
        foreach (var peer in Present())
        {
            var st = AtlasBosses.EffectsOf(peer);
            if (st.SearingRuneStacks > 0 && now >= st.SearingRuneUntil)
            {
                st.SearingRuneStacks = 0;
                st.SearingRuneUntil = 0;
                peer.Area.Buffs.Clear(peer.Id, AtlasBosses.ExarchSearingRuneRow);
                peer.Area.Buffs.Clear(peer.Id, AtlasBosses.ExarchCauterisedRow);
                await RefreshPlayerStats(peer);
            }
        }
        if (runes.Count > 0 && now >= nextRuneTick)
        {
            nextRuneTick = now + 0.7;
            var runeHit = Attack(player, AtlasBosses.ExarchFireWallEffect);
            var tick = runeHit with { Damage = runeHit.Damage.Scale(0.30) };
            foreach (var peer in Watchers())
            {
                var pos = peer.Walk.Position(now);
                if (runes.Any(r => Distance(pos, r.Where) <= 6))
                {
                    var st = AtlasBosses.EffectsOf(peer);
                    st.SearingRuneStacks = Math.Min(10, st.SearingRuneStacks + 1);
                    st.SearingRuneUntil = now + 8.0;
                    peer.Area.Buffs.Set(peer.Id, new(AtlasBosses.ExarchSearingRuneRow, id, U68: Buff.Seconds(4)));
                    peer.Area.Buffs.Set(peer.Id, new(AtlasBosses.ExarchCauterisedRow, id, Stacks: (ushort)st.SearingRuneStacks, U68: Buff.Seconds(8)));
                    await RefreshPlayerStats(peer);
                    await Hit(peer, tick, pos);
                }
            }
        }

        // Step active Rolling Meteors across the arena.
        if (balls.Count > 0 && now >= nextBallStep)
        {
            nextBallStep = now + 0.35;
            var meteorAttack = Attack(player, AtlasBosses.ExarchBugMortarEffect);
            var hitAttack = meteorAttack with { Damage = meteorAttack.Damage.Scale(1.45) };
            foreach (var ball in balls.ToArray())
            {
                if (now >= ball.Ends)
                {
                    balls.Remove(ball);
                    await RemoveEffect(ball.Fx);
                    continue;
                }
                ball.X += ball.Vx * 0.35;
                ball.Y += ball.Vy * 0.35;
                var cell = ((int)Math.Round(ball.X), (int)Math.Round(ball.Y));
                await RemoveEffect(ball.Fx);
                ball.Fx = await Effect(player, cell, MeteorBallArt, 0.7, 1.35f);
                foreach (var peer in Watchers())
                {
                    if (Distance(peer.Walk.Position(now), cell) <= 5.5)
                    {
                        balls.Remove(ball);
                        await RemoveEffect(ball.Fx);
                        await Effect(player, cell, MeteorImpactArt, 1.4, 1.4f);
                        await Hit(peer, hitAttack, cell);
                        break;
                    }
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

        if (distance <= Variety.AttackRange + 1 && now >= nextMelee)
        {
            nextMelee = now + Variety.AttackMs / 1000.0 + 0.35;
            Swing(player, AtlasBosses.FireUniqueMeleeEffect);
            return;
        }
        if (now >= nextIncinerate && distance <= 50)
        {
            nextIncinerate = now + 10;
            var wave = Attack(player, AtlasBosses.ExarchFireWallEffect);
            Swing(player, AtlasBosses.ExarchFireWallEffect);
            await Effect(player, pos, WaveArt, 2.0, 1.5f);
            pending.Add((now + 1.0, playerAt, 10, wave with { Damage = wave.Damage.Scale(1.3) }, FlameWallArt));
            return;
        }
        if (now >= nextAnnihilation && distance <= 40)
        {
            nextAnnihilation = now + 15;
            var slam = Attack(player, AtlasBosses.ExarchLanternExplosionEffect);
            Swing(player, AtlasBosses.ExarchMurderLanternCastEffect);
            await Effect(player, playerAt, ArenaGlowArt, 1.8, 1.6f);
            pending.Add((now + 1.6, playerAt, 14, slam with { Damage = slam.Damage.Scale(1.75) }, LanternExplodeArt));
            return;
        }
        if (now >= nextRunes && runes.Count < 8)
        {
            nextRunes = now + 12;
            Swing(player, AtlasBosses.ExarchSummonFlameWallEffect);
            for (int i = 0; i < 3; i++)
            {
                double a = AreaRng(player).NextDouble() * Math.Tau;
                var at = player.Layout.Near((playerAt.X + (int)(Math.Cos(a) * 12), playerAt.Y + (int)(Math.Sin(a) * 12)));
                uint fx = await Effect(player, at, SnakeRuneArt, 14, 1.2f);
                runes.Add((at, fx, now + 14));
            }
            return;
        }
        if (now >= nextDashSlash && distance is > 14 and < 55)
        {
            nextDashSlash = now + 13;
            Swing(player, AtlasBosses.ExarchDashSlashCastEffect);
            var to = player.Layout.Near(playerAt);
            await Teleport(player, to, now);
            var slash = Attack(player, AtlasBosses.ExarchDashSlashEffect);
            pending.Add((now + 0.65, to, 9, slash, FlameWallArt));
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
        if (!meteorPhase)
        {
            meteorPhase = true;
            meteorWave = 0;
            await Shield(player, true);
            await Teleport(player, Stage(player), now);
            await Announce("CLEANSING FIRE! Dodge through the safe lanes between the Rolling Meteors!");
            Swing(player, AtlasBosses.ExarchBugBarrageCastEffect);
            nextWaveAt = now + 1.2;
            busyUntil = now + 1.5;
            return;
        }

        if (now < nextWaveAt) return;
        if (meteorWave >= 4 && balls.Count == 0)
        {
            meteorPhase = false;
            await Shield(player, false);
            EndIntermission();
            nextIncinerate = now + 3;
            busyUntil = now + 1.2;
            return;
        }

        if (meteorWave < 4)
        {
            meteorWave++;
            await SpawnMeteorWave(player, now, horizontal: meteorWave % 2 == 1);
            nextWaveAt = now + 3.0;
            busyUntil = now + 2.5;
        }
    }

    async Task SpawnMeteorWave(Player player, double now, bool horizontal)
    {
        var centre = Stage(player);
        // 7 lanes spaced 9 cells apart; leave 2 guaranteed navigable safe lanes so the player always has clear paths!
        const int lanes = 7;
        int gap1 = AreaRng(player).Next(lanes);
        int gap2 = (gap1 + 2 + AreaRng(player).Next(lanes - 3)) % lanes;
        double speed = 15.0; // cells per second across the 64-cell span
        for (int i = 0; i < lanes; i++)
        {
            if (i == gap1 || i == gap2) continue;
            int offset = (i - lanes / 2) * 9;
            double sx = horizontal ? centre.X - 32 : centre.X + offset;
            double sy = horizontal ? centre.Y + offset : centre.Y - 32;
            double vx = horizontal ? speed : 0;
            double vy = horizontal ? 0 : speed;
            uint fx = await Effect(player, ((int)sx, (int)sy), MeteorBallArt, 1.0, 1.35f);
            balls.Add(new RollingBall(fx, sx, sy, vx, vy, now + 4.4));
        }
    }

    protected override async Task OnIntermissionAborted(Player player)
    {
        meteorPhase = false;
        meteorWave = 0;
        foreach (var ball in balls.ToArray()) await RemoveEffect(ball.Fx);
        balls.Clear();
    }

    protected override async Task OnCleanup(Player player)
    {
        foreach (var rune in runes.ToArray()) await RemoveEffect(rune.Fx);
        runes.Clear();
        foreach (var ball in balls.ToArray()) await RemoveEffect(ball.Fx);
        balls.Clear();
        foreach (var peer in Present())
        {
            var st = AtlasBosses.EffectsOf(peer);
            st.SearingRuneStacks = 0;
            st.SearingRuneUntil = 0;
            peer.Area.Buffs.Clear(peer.Id, AtlasBosses.ExarchSearingRuneRow);
            peer.Area.Buffs.Clear(peer.Id, AtlasBosses.ExarchCauterisedRow);
            await RefreshPlayerStats(peer);
        }
    }
}
