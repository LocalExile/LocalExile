using System.Runtime.CompilerServices;
using GameServer.Core.Diagnostics;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;

/// <summary>
/// The Atlas pinnacle bosses: the Shaper and the Elder (WorldAreas MapWorldsShapersRealm at level 84 and
/// MapWorldsElderArena / MapWorldsElderArenaUber at 83/84), and Sirus (AtlasExilesBoss5, the Eye of the Storm, level
/// 84). Their MonsterVarieties rows list the whole kit in GrantedEffectsKeys, but the ordinary monster AI played one of
/// those ids as a plain swing, so the bosses only auto-attacked. <see cref="ShaperFight"/>, <see cref="ElderFight"/> and
/// <see cref="SirusFight"/> play the kit instead; this class holds what all three need: which varieties they own, the
/// skill ids, the per-effect <see cref="MonsterAttack"/> (damage straight from the effect's own stat set) and the actor
/// skill list the client needs before it can play a single clip.
/// <para>Skill ids: 0x4000+ per boss, never the player's gem range (0x0103..0x0142). The client's skill manager resolves
/// an id globally, so reusing a gem id for a monster action makes it start that gem's action instead (KingInMists.cs:
/// the client crashed at the action, 0xc0000005). The three bosses get disjoint ranges (0x4000 the Shaper, 0x4010 the
/// Elder, 0x4020 Sirus) because the Shaper and the Elder stand in MapWorldsElderArenaUber together.</para>
/// <para>Art: every .ao path below was checked against the client's metadata (data/game-files.zip, the whole
/// metadata/effects/spells/monsters_effects/atlasofworldsbosses tree).</para>
/// </summary>
static class AtlasBosses
{
    internal const string Shaper = "Metadata/Monsters/AtlasBosses/TheShaperBoss";
    // The Shaper as the Uber Elder encounter brings him (MapWorldsElderArenaUber): no GrantedEffectsKeys of his own and
    // no boss bar, standing beside TheElderUber.
    internal const string ShaperUberElder = "Metadata/Monsters/AtlasBosses/TheShaperBossUberElder";
    internal const string Elder = "Metadata/Monsters/AtlasBosses/TheElder/TheElder";
    internal const string ElderUber = "Metadata/Monsters/AtlasBosses/TheElder/TheElderUber";
    // Sirus, Awakener of Worlds (AtlasExilesBoss5). The Gauntlet and the standalone arenas are the same fight; the
    // throne is the row he sits on when the arena is opened (see the phase note below), and the apparition/wild rows are
    // other encounters with kits of their own.
    internal const string Sirus = "Metadata/Monsters/AtlasExiles/AtlasExile5";
    internal const string SirusThrone = "Metadata/Monsters/AtlasExiles/AtlasExile5Throne";
    internal const string SirusGauntlet = "Metadata/Monsters/AtlasExiles/AtlasExile5Gauntlet";
    internal const string SirusStandalone = "Metadata/Monsters/AtlasExiles/AtlasExile5Standalone";
    internal const string SirusStandaloneMirage = "Metadata/Monsters/AtlasExiles/AtlasExile5StandaloneMirage";

    // GrantedEffects rows (MonsterVarieties GrantedEffectsKeys; poedb names the same skills).
    internal const int ShaperMeleeEffect = 481;
    internal const int CelestialBeamEffect = 6093;
    internal const int AcceleratingProjectilesEffect = 6106;
    internal const int FlickerSlamEffect = 6091;
    internal const int RetreatEffect = 6089;
    internal const int BulletHellEffect = 6111;
    internal const int ElderMeleeEffect = 406;
    internal const int SiphonEffect = 9058;
    internal const int SlammingTentaclesEffect = 9062;
    internal const int ExpandingNovaEffect = 9066;
    internal const int DoomProjectileEffect = 9351;
    internal const int ProjectileSpellcastEffect = 9330;
    // Sirus' kit (the Orion effects): the beam blast he spams, the corridor "Die" beam, the five circle-maze blasts of
    // one sweep, the multi-beam barrage, the clone beam and the meteor arena with the ground it leaves burning. His two
    // teleports (TeleportOrion 6809, AtlasOrionTeleportToCentre 10991) are not in it: a teleport played as an action also
    // lands that action's contact, so the fight moves him and plays the clip as art instead. The standalone varieties do
    // not grant those two anyway.
    internal const int SirusBeamBlastEffect = 7008;
    internal const int SirusCorridorBlastEffect = 10948;
    internal const int SirusCircleMazeEffect = 10949;
    internal const int SirusCircleMaze2Effect = 10950;
    internal const int SirusCircleMaze3Effect = 10951;
    internal const int SirusCircleMaze4Effect = 10952;
    internal const int SirusCircleMaze5Effect = 10953;
    internal const int SirusMultiBeamEffect = 8098;
    internal const int SirusMultiBeam2Effect = 8099;
    internal const int SirusCloneBeamEffect = 7031;
    internal const int SirusMeteorEffect = 10141;
    internal const int SirusMeteorGroundEffect = 10945;
    internal const int SirusBeamBombEffect = 10973;

    // ActionTypes HASH16 & 0x3fff: the basic Move. A boss that has to close the distance walks it, like the King.
    internal const ushort MoveSkill = 0x2909;

    // BuffDefinitions: the bosses' own intermission shields (uber_shaper_immune_buff / uber_elder_immune_buff), i.e. the
    // immunity icon the client shows for that phase, not the generic cannot_be_damaged row.
    internal const ushort ShaperImmunityRow = 1028, ElderImmunityRow = 1027;
    // atlas_orion_immunity: the "Protective Shield" Sirus hides behind while his storms take the arena.
    internal const ushort SirusImmunityRow = 1464;

    const string ShaperArt = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/theshaper/";
    const string ElderArt = "Metadata/Effects/Spells/monsters_effects/atlasofworldsbosses/elder/";
    internal const string CelestialBeamArt = ShaperArt + "celestialbeam/rig.ao";
    internal const string ShaperShieldArt = ShaperArt + "immune_shield/immune_shield.ao";
    internal const string ShaperShieldOffArt = ShaperArt + "immune_shield/immune_shield_off.ao";
    internal const string FlickerSlamAppearArt = ShaperArt + "teleport_slam/teleport_appear.ao";
    internal const string FlickerSlamDisappearArt = ShaperArt + "teleport_slam/teleport_disappear.ao";
    internal const string FlickerSlamArt = ShaperArt + "teleport_slam/teleport_slam.ao";
    internal const string ShaperTeleportArt = ShaperArt + "instant_teleport/instant_teleport.ao";
    internal const string BulletHellCastArt = ShaperArt + "bullet_hell/cast/bullet_hell_cast.ao";
    internal const string ElderShieldArt = ElderArt + "immune_shield/immune_shield.ao";
    internal const string ElderShieldOffArt = ElderArt + "immune_shield/immune_shield_off.ao";
    internal const string ElderSiphonArt = ElderArt + "siphon/siphon_cone.ao";
    internal const string ElderNovaArt = ElderArt + "nova/nova.ao";
    internal const string ElderDoomArt = ElderArt + "doom/on_act/on_act.ao";
    internal const string ElderDoomHitArt = ElderArt + "doom/doom_hit/doomhit.ao";
    internal const string ElderTentaclePortalArt = ElderArt + "tentacle_portal/tentacle_portal.ao";
    internal const string ElderTentacleSlamArt = ElderArt + "tentacle_portal/slam_impact.ao";
    internal const string ElderTeleportPlayerArt = ElderArt + "teleport/player/warpslam.ao";

    const string SirusArt = "Metadata/Effects/Spells/monsters_effects/atlasexiles/orion/";
    internal const string SirusBeamBlastArt = SirusArt + "spam_beam.ao";
    internal const string SirusCorridorArt = SirusArt + "corridor_nuke.ao";
    internal const string SirusNukeBeamArt = SirusArt + "nuke_beam_blast.ao";
    internal const string SirusCarpetArt = SirusArt + "death_carpeting.ao";
    internal const string SirusTripleBeamArt = SirusArt + "triplebeam/triple_beam_01.ao";
    internal const string SirusTripleBeam2Art = SirusArt + "triplebeam/triple_beam_02.ao";
    internal const string SirusMeteorArt = SirusArt + "meteor/meteor_150.ao";
    internal const string SirusMeteorImpactArt = SirusArt + "destruction_assets/meteors/impact_medium.ao";
    internal const string SirusMeteorGroundArt = SirusArt + "destruction_assets/meteors/meteor_ground.ao";
    internal const string SirusShieldArt = SirusArt + "immunity_shield.ao";
    // No separate "shield off" .ao for Orion: the short warp-down flash plays when it drops.
    internal const string SirusShieldOffArt = SirusArt + "warp_down.ao";
    internal const string SirusTeleportArt = SirusArt + "teleporting/orion_teleport.ao";
    internal const string SirusDoomGeyserArt = SirusArt + "doom_geyser.ao";

    // Projectiles.dat rows for the shots no granted effect names (its monster_projectile_variation stat): the Shaper's
    // bullet-hell ring (375 units/s, collision radius 3) and the Elder's doom shot (600, radius 2), as Projectiles.dat
    // lists them. Speed is the row's own figure, the one ProjectileSpeed.CellsPerSecond reads.
    internal static SkillCatalog.MonsterProjectileInfo BulletHellShot { get; } =
        new("Metadata/Projectiles/ShaperBulletHellProjectile", 375, false, 3) { Count = 12, SpreadRadians = Math.Tau };
    internal static SkillCatalog.MonsterProjectileInfo DoomShot { get; } =
        new("Metadata/Projectiles/ElderProjectileDoom", 600, false, 2);

    internal static bool IsShaper(MonsterCatalog.Variety? variety) => variety?.Id is Shaper or ShaperUberElder;
    internal static bool IsElder(MonsterCatalog.Variety? variety) => variety?.Id is Elder or ElderUber;
    // AtlasExilesBoss5 is a phased arena for MapBossFights: WorldAreas lists [throne, walking], the walking row is the
    // held-back last boss, so a freshly opened arena contains only the throne. Both are Sirus and both fight: the throne
    // is the stage the player walks in on, and killing him is what forms the walking row and his four storms.
    internal static bool IsSirus(MonsterCatalog.Variety? variety) => variety?.Id is Sirus or SirusThrone or SirusGauntlet
        or SirusStandalone or SirusStandaloneMirage;

    /// <summary>Sirus seated on the throne: the arena's first stage, which casts without walking off the platform.</summary>
    internal static bool IsSirusSeated(MonsterCatalog.Variety? variety) => variety?.Id is SirusThrone;
    internal static bool IsBoss(MonsterCatalog.Variety? variety) => IsShaper(variety) || IsElder(variety) || IsSirus(variety);

    // The effect whose row the monster's own record uses as its swing: the melee, or Sirus' beam blast (he never swings a
    // weapon). Everything else is played by the controller through ids of its own, so the ordinary swing can only ever be
    // that one skill.
    internal static int BasicEffect(MonsterCatalog.Variety variety) =>
        IsSirus(variety) ? SirusBeamBlastEffect : IsElder(variety) ? ElderMeleeEffect : ShaperMeleeEffect;

    // A melee keeps the variety's own attack time and reach; Sirus' basic skill is a spell and uses its clip's.
    internal static bool MeleeLike(MonsterCatalog.Variety variety) => !IsSirus(variety);

    // 0x4000-0x4005 the Shaper, 0x4010-0x4015 the Elder, 0x4020-0x402c Sirus: one id per effect, fixed, so an id always
    // means one clip.
    internal static readonly int[] ShaperEffects = [ShaperMeleeEffect, CelestialBeamEffect, AcceleratingProjectilesEffect,
        FlickerSlamEffect, RetreatEffect, BulletHellEffect];
    internal static readonly int[] ElderEffects = [ElderMeleeEffect, SiphonEffect, SlammingTentaclesEffect,
        ExpandingNovaEffect, DoomProjectileEffect, ProjectileSpellcastEffect];
    internal static readonly int[] SirusEffects = [SirusBeamBlastEffect, SirusCorridorBlastEffect, SirusCircleMazeEffect,
        SirusCircleMaze2Effect, SirusCircleMaze3Effect, SirusCircleMaze4Effect, SirusCircleMaze5Effect, SirusMultiBeamEffect,
        SirusMultiBeam2Effect, SirusCloneBeamEffect, SirusMeteorEffect, SirusMeteorGroundEffect, SirusBeamBombEffect];

    internal static int[] EffectsFor(MonsterCatalog.Variety variety) =>
        IsSirus(variety) ? SirusEffects : IsElder(variety) ? ElderEffects : ShaperEffects;

    /// <summary>The skill id the client's actor uses for one of the boss's effects.</summary>
    internal static ushort Skill(MonsterCatalog.Variety variety, int effect)
    {
        var effects = EffectsFor(variety);
        int index = Array.IndexOf(effects, effect);
        // An effect outside the kit keeps an id of its own rather than colliding with the basic skill's.
        return (ushort)((IsSirus(variety) ? 0x4020 : IsElder(variety) ? 0x4010 : 0x4000)
            + (index >= 0 ? index : effects.Length + 1));
    }

    /// <summary>The actor's skill list: every effect the controller can play, so that the client owns the clip a 0x1ab
    /// action names. Each record carries its own numbers (main-hand damage, the spell's virtual cast time), which the
    /// client reads to pick and play the clip; <see cref="Attack"/> supplies the same numbers to the server's hits.</summary>
    internal static IReadOnlyList<GrantedSkill>? SkillsFor(ClientState conn, MonsterCatalog.Variety variety, int level,
        MonsterAttack attack)
    {
        if (!IsBoss(variety)) return null;
        var catalog = Game.SkillsFor(conn);
        var effects = EffectsFor(variety);
        return [.. effects.Select(effect =>
        {
            var use = Attack(conn, variety, level, effect, attack);
            return catalog.FindByRow(Skill(variety, effect), effect, level,
                WithBehaviourVariation(conn, effect, use.SkillStats()));
        }).OfType<GrantedSkill>()];
    }

    // Sirus' effects carry their spell_minimum/maximum_base_physical_damage as a fraction of the monster's own damage,
    // not as a flat hit (0.8-1.2 the beam blast, 1.6-2.4 the corridor "Die" beam, 1.2-1.8 the circle-maze blasts,
    // 1.6-2.4 the clone and multi beams, 0.8-1.2 the beam bomb; the meteor arena's row names no damage at all and its
    // meteors land like the corridor's). EffectStats rounds those fractions to whole numbers, so the row cannot be read
    // as the hit and the fraction is applied here to BaseDamage instead.
    static readonly Dictionary<int, (double Min, double Max)> SirusFractions = new()
    {
        [SirusBeamBlastEffect] = (0.8, 1.2),
        [SirusCorridorBlastEffect] = (1.6, 2.4),
        [SirusCircleMazeEffect] = (1.2, 1.8),
        [SirusCircleMaze2Effect] = (1.2, 1.8),
        [SirusCircleMaze3Effect] = (1.2, 1.8),
        [SirusCircleMaze4Effect] = (1.2, 1.8),
        [SirusCircleMaze5Effect] = (1.2, 1.8),
        [SirusCloneBeamEffect] = (1.6, 2.4),
        [SirusMultiBeamEffect] = (1.6, 2.4),
        [SirusMultiBeam2Effect] = (1.6, 2.4),
        [SirusMeteorEffect] = (1.6, 2.4),
        [SirusMeteorGroundEffect] = (0.4, 0.6),
        [SirusBeamBombEffect] = (0.8, 1.2),
    };

    /// <summary>One of Sirus' hits, as the fraction of the variety's physical damage its effect row carries.</summary>
    internal static DamagePacket SirusDamage(int effect, int min, int max)
    {
        var (low, high) = SirusFractions.GetValueOrDefault(effect, (1.0, 1.0));
        return DamagePacket.Of(DamageType.Physical,
            new DamageRange((int)Math.Floor(min * low), (int)Math.Ceiling(max * high)));
    }

    /// <summary>One of the boss's skills as the server plays it: its own damage at the area's level, its clip's cast time
    /// and reach, and — for the skills that name one — its projectile. <paramref name="swing"/> is the variety's plain
    /// attack, the source of a melee's physical damage.</summary>
    internal static MonsterAttack Attack(ClientState conn, MonsterCatalog.Variety variety, int level, int effect,
        MonsterAttack? swing = null)
    {
        var catalog = Game.SkillsFor(conn);
        var (min, max) = Monsters.BaseDamage(variety, (byte)Math.Clamp(level, 1, 100)) ?? (1, 1);
        bool melee = effect == BasicEffect(variety) && MeleeLike(variety);
        // The skill's own base hit: its spell_minimum_base_*_damage (HitDamage also reads the unprefixed names, so an
        // attack such as a slam keeps its numbers too). A slam or a melee whose stat set carries no base damage of its own
        // hits for the variety's physical damage, the way every monster's swing does. The two degen skills are the
        // exception: their whole damage is the per-minute degen DamageOverTime reads, and a physical fallback there would
        // put an invisible hit on top of the beam or the siphon.
        var damage = catalog.HitDamage(effect, level);
        if (IsSirus(variety)) damage = SirusDamage(effect, min, max);
        else if (damage.MaxTotal <= 0 && effect is not (CelestialBeamEffect or SiphonEffect))
            damage = DamagePacket.Of(DamageType.Physical, min, max);
        double seconds = melee ? Math.Max(0.3, variety.AttackMs / 1000.0) : Math.Max(0.3, catalog.CastTimeMs(effect) / 1000.0);
        double reach = melee ? variety.AttackRange : 45;
        if (melee && swing is not null) return swing with { SkillId = Skill(variety, effect), Scripted = true };
        var projectile = ProjectileFor(conn, effect);
        return new MonsterAttack(Skill(variety, effect), seconds, reach, damage)
        {
            Spell = catalog.IsSpell(effect),
            Scripted = true,
            Ranged = projectile is not null,
            Projectile = projectile,
        };
    }

    /// <summary>The shot a skill launches: its own MonsterProjectileVariation row when the skill has one, and the
    /// Projectiles.dat row for the two that name none (the bullet-hell ring is twelve in a circle, which no skill record
    /// makes, and the doom shot is a single slow one).</summary>
    internal static SkillCatalog.MonsterProjectileInfo? ProjectileFor(ClientState conn, int effect) => effect switch
    {
        BulletHellEffect => BulletHellShot,
        DoomProjectileEffect => DoomShot,
        _ => Game.SkillsFor(conn).MonsterProjectile(effect),
    };

    /// <summary>A degen skill's damage from its own stat set: the base fire/cold/lightning/physical/chaos
    /// <c>..._damage_to_deal_per_minute</c> over one second. The Shaper's celestial beam carries fire 5384, cold 4415 and
    /// lightning 6084 a second (323067, 264915 and 365066 a minute), the Elder's siphon 614 physical (36825 a minute);
    /// the rows are constant at every level, not per-level.
    /// </summary>
    internal static DamagePacket DamageOverTime(ClientState conn, int effect, int level)
    {
        var stats = Game.SkillsFor(conn).EffectStats(effect, level);
        int Get(string name)
        {
            if (Stats.Key(name) is not uint key) return 0;
            foreach (var (id, value) in stats) if (id == key) return value;
            return 0;
        }
        var packet = DamagePacket.Empty;
        foreach (var (type, name) in new[]
        {
            (DamageType.Physical, "base_physical_damage_to_deal_per_minute"),
            (DamageType.Fire, "base_fire_damage_to_deal_per_minute"),
            (DamageType.Cold, "base_cold_damage_to_deal_per_minute"),
            (DamageType.Lightning, "base_lightning_damage_to_deal_per_minute"),
            (DamageType.Chaos, "base_chaos_damage_to_deal_per_minute"),
        })
        {
            double perSecond = Get(name) / 60.0;
            if (perSecond < 1) continue;
            packet = packet.Add(type, new DamageRange(Math.Floor(perSecond), Math.Ceiling(perSecond)));
        }
        return packet;
    }

    /// <summary>Runs the fight of every Shaper, Elder and Sirus in this connection's instance. Called once per tick per
    /// connection, like <see cref="BrineKing.Tick"/>: the instance's runner plays the steps, a join only binds.</summary>
    internal static async Task Tick(ClientState conn)
    {
        if (!conn.PlayerSent || conn.Closed || conn.LeavingArea) return;
        List<(uint Id, SpawnedMonster Monster)> found = [];
        lock (conn.Monsters)
            foreach (var (id, monster) in conn.Monsters)
                if (IsBoss(monster.Variety)) found.Add((id, monster));
        if (found.Count == 0 && !Fights.TryGetValue(conn.Instance, out _)) return;
        foreach (var (id, monster) in found)
        {
            if (!Fights.TryGetValue(conn.Instance, out var byMonster))
                Fights.Add(conn.Instance, byMonster = new Dictionary<uint, AtlasBossFight>());
            if (!byMonster.TryGetValue(id, out var fight))
            {
                var variety = monster.Variety!;
                fight = IsSirus(variety) ? new SirusFight(id, monster, conn)
                    : IsShaper(variety) ? new ShaperFight(id, monster, conn)
                    : new ElderFight(id, monster, conn);
                byMonster[id] = fight;
                Console.WriteLine($"[gateway] {conn.Wire.Remote} 0x{id:x} {variety.Name ?? variety.Id} "
                    + $"({variety.Id}, level {monster.Level}, life {monster.MaxLife}): fight started, "
                    + $"{(IsMonsterRunner(conn) ? "its steps run here" : "another connection runs its steps")}");
            }
            await fight.Gate.WaitAsync();
            try
            {
                await fight.Bind(conn);
                if (IsMonsterRunner(conn)) await fight.Step(conn, conn.Clock.Elapsed.TotalSeconds);
            }
            catch (Exception ex)
            {
                // Guard logs an exception that escapes a tick and drops the hook; a fight that cannot step must say so.
                Console.WriteLine($"[gateway] {conn.Wire.Remote} {monster.Variety!.Id} 0x{id:x} step failed: {ServerLog.Describe(ex)}");
            }
            finally { fight.Gate.Release(); }
        }
    }

    static readonly RuleTable<InstanceState, Dictionary<uint, AtlasBossFight>> Fights = new();

    /// <summary>Stops every fight of an instance (it is unloaded): the phase gates unlock and the boss's own walk speed
    /// returns.</summary>
    internal static IDisposable CleanupOnUnload() => new Cleanup();

    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights)
                foreach (var fight in pair.Value.Values) fight.Stop();
        }
    }
}
