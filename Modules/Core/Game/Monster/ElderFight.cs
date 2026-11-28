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
sealed class ElderFight(uint id, SpawnedMonster boss, Player player) : AtlasBossFight(id, boss, player)
{
    protected override string Name => "[Elder]";
    protected override ushort ImmunityRow => AtlasBosses.ElderImmunityRow;
    protected override string ShieldArt => AtlasBosses.ElderShieldArt;
    protected override string ShieldOffArt => AtlasBosses.ElderShieldOffArt;
    // The blink of an intermission goes to the arena's middle marker, not to the layout's spawn (the map entry).
    (int X, int Y)? stage;
    protected override (int X, int Y) Stage(Player player) => stage ??= ArenaCentre(player, boss);

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
    // Zana arena presence in MapWorldsElderArena / MapWorldsElderArenaUber: sealing channel & protective bubble.
    uint zanaId, zanaBubbleFx, zanaChannelFx;
    bool zanaSpawned, zanaBubbleActive;
    double zanaBubbleUntil;
    (int X, int Y) zanaAt;

    static bool HasZanaArena(Player player) => player.Layout.Id is "MapWorldsElderArena" or "MapWorldsElderArenaUber";

    async Task EnsureZana(Player player)
    {
        if (zanaSpawned || !HasZanaArena(player)) return;
        zanaSpawned = true;
        bool uber = player.Layout.Id == "MapWorldsElderArenaUber";
        string varietyId = uber ? AtlasBosses.ZanaUberElderVariety : AtlasBosses.ZanaElderVariety;
        foreach (var (existingId, existing) in player.Area.Monsters)
            if (existing.Variety?.Id is AtlasBosses.ZanaShaperVariety or AtlasBosses.ZanaElderVariety or AtlasBosses.ZanaUberElderVariety && !existing.Dead)
            {
                zanaId = existingId;
                zanaAt = existing.Walk.Position(AreaTime(player));
                return;
            }
        if (Monsters.Find(varietyId) is not { Supported: true } zanaV || zanaV.Id != varietyId) return;
        var centre = Stage(player);
        zanaAt = player.Layout.Near((centre.X + 14, centre.Y + 8));
        zanaId = await SpawnMonster(player, zanaV, boss.Level, MonsterRarity.Normal, zanaAt.X, zanaAt.Y,
            friendly: true, scripted: true, owned: (_, m) => { m.NoRewards = true; m.EntranceGrace = true; });
    }

    async Task ClearZanaBubble()
    {
        if (!zanaBubbleActive) return;
        zanaBubbleActive = false;
        await RemoveEffect(zanaBubbleFx);
        zanaBubbleFx = 0;
        foreach (var peer in Present())
        {
            var st = AtlasBosses.EffectsOf(peer);
            if (st.ZanaBubble)
            {
                st.ZanaBubble = false;
                peer.Area.Buffs.Clear(peer.Id, AtlasBosses.ZanaBubbleBuffRow);
                await RefreshPlayerStats(peer);
            }
        }
    }

    protected override async Task Ticks(Player player, double now)
    {
        await EnsureZana(player);
        if (zanaBubbleActive)
        {
            if (now >= zanaBubbleUntil) await ClearZanaBubble();
            else
            {
                foreach (var peer in Watchers())
                {
                    bool inside = Distance(peer.Walk.Position(now), zanaAt) <= 12;
                    var st = AtlasBosses.EffectsOf(peer);
                    if (inside != st.ZanaBubble)
                    {
                        st.ZanaBubble = inside;
                        if (inside) peer.Area.Buffs.Set(peer.Id, new(AtlasBosses.ZanaBubbleBuffRow, zanaId, U68: Buff.Seconds(3)));
                        else peer.Area.Buffs.Clear(peer.Id, AtlasBosses.ZanaBubbleBuffRow);
                        await RefreshPlayerStats(peer);
                    }
                }
            }
        }
        foreach (var slam in pending.Where(p => p.At <= now).ToArray())
        {
            pending.Remove(slam);
            await Effect(player, slam.Where, AtlasBosses.ElderTentacleSlamArt, 1.6);
            foreach (var peer in Watchers())
                if (Distance(peer.Walk.Position(now), slam.Where) <= slam.Radius) await Hit(peer, slam.Attack, slam.Where);
        }
        if (siphonTick is null || now >= siphonUntil || now < siphonNext) return;
        siphonNext = now + 0.5;
        if (now >= siphonUntil) { siphonTick = null; return; }
        uint healed = 0;
        foreach (var peer2 in Watchers())
        {
            if (Distance(peer2.Walk.Position(now), siphonAt) > 8) continue;
            uint before = peer2.PlayerLife;
            // Hit the player the ground is under, or the tally below reads one connection's life while another takes
            // the damage and healed stays 0.
            await Hit(peer2, siphonTick, siphonAt);
            healed += before > peer2.PlayerLife ? before - peer2.PlayerLife : 0;
        }
        // The drain feeds him: a quarter of the life it took, never past his maximum.
        if (healed > 0) boss.Life = Math.Min(boss.MaxLife, boss.Life + healed / 4);
    }

    protected override async Task Act(Player player, double now)
    {
        var watchers = Watchers();
        if (watchers.Length == 0) return;
        var pos = boss.Walk.Position(now);
        var target = watchers.MinBy(p => Distance(pos, p.Walk.Position(now)))!;
        var playerAt = target.Walk.Position(now);
        double distance = Distance(pos, playerAt);

        // Due ranged skills go before the melee: checked the other way round, the melee returned first every time he
        // stood next to a player and the siphon (which needs distance) never fired at all.
        if (now >= nextSiphon && distance < 60)
        {
            nextSiphon = now + 18;
            await Siphon(player, playerAt, now);
            return;
        }
        if (now >= nextNova && distance <= 26)
        {
            nextNova = now + 14;
            await Nova(player, playerAt, now);
            return;
        }
        if (distance <= Variety.AttackRange + 1 && now >= nextMelee)
        {
            nextMelee = now + Variety.AttackMs / 1000.0 + 0.4;
            Swing(player, AtlasBosses.ElderMeleeEffect);
            return;
        }
        if (now >= nextTentacles)
        {
            nextTentacles = now + 12;
            await Tentacles(player, playerAt, now, 5);
            return;
        }
        if (now >= nextVolley)
        {
            nextVolley = now + 7;
            Swing(player, AtlasBosses.ProjectileSpellcastEffect);
            return;
        }
        if (now >= nextDoom)
        {
            nextDoom = now + 10;
            await Effect(player, pos, AtlasBosses.ElderDoomArt, 1.6);
            Swing(player, AtlasBosses.DoomProjectileEffect);
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

    /// <summary>The siphon (9058): the Elder channels at the player's ground for three seconds; whatever stands there
    /// takes the skill's physical degen on a 0.5 s tick and heals him for a quarter of it.</summary>
    async Task Siphon(Player player, (int X, int Y) at, double now)
    {
        var dot = AtlasBosses.DamageOverTime(player, AtlasBosses.SiphonEffect, boss.Level);
        if (dot.MaxTotal <= 0) return;
        siphonAt = player.Layout.Near((at.X, at.Y));
        siphonUntil = now + 3;
        siphonNext = now + 0.8;
        siphonTick = new MonsterAttack(AtlasBosses.Skill(Variety, AtlasBosses.SiphonEffect), 0.5, 0, dot.Scale(0.5))
        { Spell = true, Scripted = true };
        Swing(player, AtlasBosses.SiphonEffect);
        await Effect(player, siphonAt, AtlasBosses.ElderSiphonArt, 3.6, 1.6f);
    }

    /// <summary>The slamming tentacles (9062): five portals open around the player and slam where they opened, a 5 unit
    /// area each, their physical damage 1.2x the melee's, one second after the cast.</summary>
    async Task Tentacles(Player player, (int X, int Y) playerAt, double now, int count)
    {
        var slam = Attack(player, AtlasBosses.SlammingTentaclesEffect);
        slam = slam with { Damage = slam.Damage.Scale(1.2) };
        Swing(player, AtlasBosses.SlammingTentaclesEffect);
        for (int i = 0; i < count; i++)
        {
            double angle = Math.Tau * i / count + AreaRng(player).NextDouble() * 0.4;
            int radius = 6 + AreaRng(player).Next(6);
            var where = player.Layout.Near((playerAt.X + (int)Math.Round(Math.Cos(angle) * radius),
                playerAt.Y + (int)Math.Round(Math.Sin(angle) * radius)));
            await Effect(player, where, AtlasBosses.ElderTentaclePortalArt, 2.4);
            pending.Add((now + 1.2, where, 5, slam));
        }
    }

    /// <summary>The expanding nova (9066): a ring that leaves the Elder and damages everything within 22 units once it
    /// has passed: the player walks out of it, or shelters inside Zana's protective bubble.</summary>
    async Task Nova(Player player, (int X, int Y) playerAt, double now)
    {
        var pos = boss.Walk.Position(now);
        var attack = Attack(player, AtlasBosses.ExpandingNovaEffect);
        await Effect(player, pos, AtlasBosses.ElderNovaArt, 2.4, 2f);
        Swing(player, AtlasBosses.ExpandingNovaEffect);
        if (HasZanaArena(player) && zanaId != 0 && !zanaBubbleActive)
        {
            zanaBubbleActive = true;
            zanaBubbleUntil = now + 2.4;
            zanaBubbleFx = await Effect(player, zanaAt, AtlasBosses.ZanaBubbleArt, 2.6, 1.5f);
            await Voice(4712, 323); // "Exile, to me! That thing is about to explode!"
        }
        // The ring reaches 22 units 1.4 s after the cast; anything still inside takes the skill's damage.
        pending.Add((now + 1.4, pos, 22, attack));
        _ = playerAt;
    }

    protected override async Task Intermission(Player player, double now)
    {
        if (!intermission)
        {
            intermission = true;
            waves = 0;
            await Shield(player, true);
            await Teleport(player, Stage(player), now);
            if (HasZanaArena(player) && zanaId != 0)
            {
                zanaChannelFx = await Effect(player, zanaAt, AtlasBosses.ZanaElderChannelArt, 6.0, 1.3f);
                await Voice(4717, 323); // "Protect me while I seal the portal!"
            }
            var watchers = Watchers();
            if (watchers.Length > 0) await Nova(player, watchers[0].Walk.Position(now), now);
            waveAt = now + 1.6;
            busyUntil = now + 1.6;
            return;
        }
        if (now < waveAt) return;
        if (waves == 1)
        {
            intermission = false;
            await RemoveEffect(zanaChannelFx);
            zanaChannelFx = 0;
            await Shield(player, false);
            EndIntermission();
            nextTentacles = now + 2;
            busyUntil = now + 1;
            return;
        }
        waves++;
        var target = Watchers().MinBy(p => Distance(boss.Walk.Position(now), p.Walk.Position(now)));
        if (target is not null) await Tentacles(player, target.Walk.Position(now), now, 4);
        waveAt = now + 1.8;
        busyUntil = now + 1.6;
    }

    protected override async Task OnIntermissionAborted(Player player)
    {
        intermission = false;
        waves = 0;
        await ClearZanaBubble();
        await RemoveEffect(zanaChannelFx);
        zanaChannelFx = 0;
    }

    protected override async Task OnCleanup(Player player)
    {
        await ClearZanaBubble();
        await RemoveEffect(zanaChannelFx);
        zanaChannelFx = 0;
        if (HasZanaArena(player) && zanaId != 0)
            await Voice(4715, 323); // "I banish you to the void between voids! May you never be free!"
    }
}
