using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;

/// <summary>
/// The Pantheon's campaign bosses fight with their own kits instead of one plain swing. Upstream precedent:
/// the Atlas pinnacle bosses (838bdcc6, ShaperFight/ElderFight/SirusFight over the shared
/// <see cref="AtlasBossFight"/> plumbing) and the campaign Brine King (BrineKing.cs). The same fix applied to
/// the ten Pantheon bosses that had none: each variety's whole GrantedEffectsKeys list goes onto the actor
/// (<see cref="SkillsFor"/>), so every 0x1ab the controller plays names a clip the client owns, and one
/// controller per monster rotates the kit instead of the ordinary AI's auto-attack (the AI skips them, like
/// the Atlas bosses: Monsters.cs line "DepravedTrinity.IsBoss || … || AtlasBosses.IsBoss").
/// <para>Skill ids: 0x4030 + the index in the merged kit, never a gem id (KingInMists.cs: the client's skill
/// manager resolves an id globally, so a gem id on a monster action starts that gem's action and crashes).
/// Every variety of one boss shares the merged kit and therefore the same ids, so a second arena boss plays
/// the same clips with its own controller.</para>
/// <para>Damage: each skill's own stat set at the area's level (HitDamage), falling back to the variety's
/// physical damage for attacks whose stat set names none — the way <see cref="AtlasBosses.Attack"/> reads the
/// same tables. State effects (hide, burrow, emerge, dodge, transform, trigger_spawners, empty_action) carry
/// no hit. Bosses that fight twice with the same row in a map arena (Tukohama's and Shakari's map spawners
/// keep the act row) each get their own controller; the arena damage share is not split, which only makes a
/// two-boss arena hit for its full damage.</para>
/// <para>Their art paths were checked against the client's metadata (data/game-files.zip, the
/// metadata/effects/spells/monsters_effects trees of act6/act7/act8/act9, tukohama_e, vaalspiderboss_e,
/// despair_bear_upheaval, yugul_silverfrog, insect_spawner_spit). Bosses whose kit rows name no distinctive
/// art of their own (Ryslatha's flame breath, Greust's blessing) play the effect row's own cast clip only.</para>
/// </summary>
static class PantheonBosses
{
    // ---- the varieties (Agent docs: Agents/docs/game/campaign/pantheon.md §2) ----
    internal const string BrineKingId = "Metadata/Monsters/BrineKing/BrineKing";           // owned by BrineKing.cs
    internal const string Tukohama = "Metadata/Monsters/Tukohama/Tukohama";                 // a6q3, The Karui Fortress
    internal const string Abberath = "Metadata/Monsters/Abberath/Abberath";                 // a6q7, Prisoner's Gate
    internal const string Abberath2 = "Metadata/Monsters/Abberath/Abberath2";               // his enraged form
    internal const string Ryslatha = "Metadata/Monsters/InsectSpawner/InsectSpawnerParasiteBoss"; // a6q6, The Wetlands
    internal const string Greust = "Metadata/Monsters/Ralakesh/Greust/Greust";              // a7q1, The Ashen Fields
    internal const string Gruthkul = "Metadata/Monsters/DespairBear/DespairBear";           // a7q9, The Dread Thicket
    internal const string Arakaali = "Metadata/Monsters/VaalSpiderGod/Arakaali";            // a7q4, Temple of Decay 2
    internal const string Yugul = "Metadata/Monsters/Frog/FrogGod/SilverFrog";              // a8q4, The High Gardens
    internal const string Solaris = "Metadata/Monsters/LunarisSolaris/Solaris";             // a8q2/3, The Harbour Bridge
    internal const string Lunaris = "Metadata/Monsters/LunarisSolaris/Lunaris";             // the same bridge
    internal const string Shakari = "Metadata/Monsters/Shakari/Shakari";                    // a9q5, The Oasis
    internal const string Shakari2 = "Metadata/Monsters/Shakari/Shakari2";                  // her second form
    internal const string Garukhan = "Metadata/Monsters/Garukhan/Garukhan";                 // a9q2, The Quarry

    internal const int MeleeEffect = 406;
    internal const int BearMeleeEffect = 420;   // Gruthkul's claw (DespairBear rows)
    internal const int CleaveEffect = 208;      // Arakaali's melee (no Default Attack in her row)

    // The merged kit of each boss, in its MonsterVarieties row order (the client plays the clip the 0x1ab
    // names; the order is stable, so an id always means one clip).
    internal static readonly int[] TukohamaEffects = [406, 5373, 5302, 6664, 6665];
    internal static readonly int[] ArakaaliEffects = [6555, 6557, 6545, 4259, 6549, 5567, 5570, 6879, 6885,
        6887, 6550, 6551, 4260, 4261, 5221, 7802, 7805, 6886, 5222, 4438, 208];
    internal static readonly int[] AbberathEffects = [406, 6744, 6748, 6751, 6752, 6753, 2970, 808];
    internal static readonly int[] Abberath2Effects = [406, 6744, 6748, 6751, 808, 6140, 6141, 6142, 6143,
        6144, 2968];
    internal static readonly int[] RyslathaEffects = [2069, 6266, 3432, 8449, 3434];
    internal static readonly int[] GreustEffects = [406, 4364, 4781, 5586, 8450, 5623];
    internal static readonly int[] GruthkulEffects = [420, 6763, 6765, 6774, 6775, 6776, 6777];
    internal static readonly int[] YugulEffects = [406, 6792, 6794, 6796, 807, 7860, 8455];
    internal static readonly int[] SolarisEffects = [6468, 6469, 6544, 6456, 6739, 4399, 4400, 406, 4401, 6462];
    internal static readonly int[] LunarisEffects = [6455, 2542, 6480, 6481, 6482, 6483, 6484, 6740, 4399,
        4400, 6485, 6486, 6471, 8432];
    internal static readonly int[] ShakariEffects = [406, 8768, 8769, 8802, 6099, 8876, 6100, 5602];
    internal static readonly int[] Shakari2Effects = [406, 8768, 8769, 8802, 6099, 8877, 6100, 5602];
    internal static readonly int[] GarukhanEffects = [406, 7857, 8445, 8443, 8456, 7861, 6229, 7862, 7863,
        6754, 6755, 7864];

    // State effects: their rows carry no hit (hide, emerge, burrow, dodge, transform, spawners, cast markers).
    static readonly int[] Markers =
    [
        2970, 2968,                    // trigger_spawners (Abberath, Abberath2)
        6752, 6753,                    // abberath_emerge, abberath_hide
        6885, 6887, 6886,              // arakaali_hide
        7860, 8455,                    // Yugul's cast marker and hide
        6740,                          // solaris_lunaris_transform
        4399, 4400, 4401,              // monster_dodge
        8802, 6099, 6100,              // Shakari's burrow states
        7861, 7862, 7863, 7864,        // Garukhan's charged-dash cast markers
        8450,                          // ralakesh_blessing (Greust)
        8449,                          // ryslatha_slide
        5373,                          // Tukohama's totem summon (the clip; the mechanic is the phase shield)
        5586,                          // izaro_taunt (Greust)
        6774, 6775, 6776, 6777,        // Gruthkul's trigger_spawners (the geysers hit through the fight)
    ];

    // Attacks that hit harder than their stat set names (the slams and the big swings): a scale on the variety's
    // physical damage, the way the Brine King's slam reads its own (BrineKing.cs Attack: Scale(2)).
    static readonly Dictionary<int, double> DamageScale = new()
    {
        [6665] = 1.5,   // tukohama_slam
        [6664] = 1.2,   // tukohama_sunder
        [6748] = 1.5,   // abberath_meteor_upheaval
        [6144] = 1.5,   // hellfire_barrage (enraged form's finisher)
        [6763] = 1.5,   // despair_bear_upheaval
        [6765] = 1.2,   // monster_slam
        [8768] = 1.5,   // shakari_tail_strike
        [8769] = 1.2,   // shakari_upheaval
        [7857] = 1.5,   // garukhan_combo_attack
        [6468] = 1.3,   // solaris_energy_lance (the 3 s charged one)
        [6794] = 1.5,   // frog_god_chomp
        [808] = 1.3, [807] = 1.3,   // the Leap Slams
        [4364] = 1.2,   // bull_charge (Greust)
    };

    // Cooldowns per effect, seconds between one cast and the next. The cast time itself keeps the boss busy
    // (AtlasBossFight.SwingWith); these pace the rotation on top of it.
    static readonly Dictionary<int, double> Cooldowns = new()
    {
        [6664] = 9, [6665] = 14, [5302] = 10, [5373] = 25,
        [6744] = 12, [6748] = 11, [6751] = 9, [808] = 10, [6140] = 12, [6144] = 14,
        [6763] = 10, [6765] = 8, [6777] = 16, [6776] = 18, [6775] = 20,
        [8768] = 8, [8769] = 10, [8802] = 18, [8876] = 12, [5602] = 9,
        [7857] = 10, [8445] = 14, [8443] = 12, [8456] = 20, [6754] = 15, [6229] = 16,
        [6792] = 10, [6794] = 12, [6796] = 15, [807] = 10,
        [6468] = 12, [6462] = 14, [6456] = 7, [6469] = 9,
        [6471] = 10, [8432] = 14, [2542] = 12, [6485] = 6,
        [4364] = 12, [4781] = 8, [5623] = 14,
        [6557] = 12, [6545] = 6, [4259] = 14, [6549] = 15, [4438] = 9, [5221] = 12, [5222] = 14, [6555] = 7,
        [6266] = 10, [3432] = 12, [2069] = 6,
    };

    // Ground art of the kit's skills (checked against the client's metadata; where a skill has none, its cast
    // clip alone plays). One path per effect, the marker the player has to leave.
    internal static string? ArtOf(int effect) => effect switch
    {
        6664 => TukohamaArt + "totemsunder/firequake_01.ao",               // the sunder's quake
        6665 => TukohamaArt + "totemslam/marker.ao",                       // the slam's marker
        5302 => TukohamaArt + "circleslam_surge.ao",                       // the circle burn
        6140 => TukohamaArt + "sunder_surge.ao",                           // the barrage's surge
        6751 => AbberathArt + "combofireballbig.ao",                       // the fireball combo's landing
        6144 => AbberathArt + "combofireballbig.ao",                       // the barrage's finisher
        6744 => AbberathArt + "abberathenragedhoof.ao",                    // the fury
        6748 => AbberathArt + "cliffbomb_01.ao",                           // the meteor upheaval
        6763 => GruthkulArt + "remote_slam/upheaval/crack1.ao",            // the upheaval's crack
        6765 => GruthkulArt + "ground_slam/groundslam.ao",                 // the slam
        6777 => DespairGeyser,                                             // a geyser line
        6776 => GruthkulArt + "ground_crack/groundcrack1.ao",
        6775 => GruthkulArt + "ground_projectile/projectile.ao",
        8768 => ShakariArt + "tail_strike_epic/onact.ao",                  // the tail strike
        8769 => ShakariArt + "tail_strike_quick/onact.ao",                 // the upheaval
        5602 => ShakariArt + "half_burrow_quicksand/shakari/half_burrow_cascade_01.ao", // the cascade
        7857 => GarukhanArt + "combo_attack/sword_slash.ao",               // the combo's slash
        8443 => GarukhanArt + "combo_attack/shieldnova.ao",                // the tornado's nova
        8445 => GarukhanArt + "combo_attack/claw_stab.ao",                 // the cascade's stab
        8456 => GarukhanArt + "ambush/ambush_slash.ao",                    // the ambush
        6754 => GarukhanArt + "charged_dash/charge_end/charge_end.ao",     // the charged dash
        6229 => GarukhanArt + "combo_attack/on_act.ao",                    // the maelstrom
        6794 => YugulArt + "silverfrogsuperlargebite.ao",                  // the chomp
        6796 => YugulArt + "silverpoolexplosion.ao",                       // the split's pool
        6792 => YugulArt + "silverfrogcharge.ao",                          // the charge
        6462 => YugulArt + "silverfrogmortar_impact.ao",                   // Solaris' sky beam lands
        6468 => YugulArt + "silverfrogmortar_impact.ao",                   // the energy lance lands
        6480 => YugulArt + "silverfrogmortar_proj.ao",                     // Lunaris' glaive
        6471 => YugulArt + "silverfrogmortar_impact.ao",                   // the spell barrage lands
        8432 => YugulArt + "silverpoolexplosion.ao",                       // the cascade's landing
        4438 => SpiderArt + "basicmortar.ao",                              // Arakaali's mortar
        5221 => SpiderArt + "eggmortar.ao",                                // the egg mortar
        5222 => SpiderArt + "eggmortarimpact.ao",
        4259 => SpiderArt + "deathacid.ao",                                // the lightning tendrils' acid
        4260 => SpiderArt + "deathacid_l.ao",
        4261 => SpiderArt + "deathacid_r.ao",
        6555 => SpiderArt + "phase_01_melee_01.ao",                        // the riptide's strike
        6557 => SpiderArt + "phase_02_melee_01.ao",
        6549 => ArakaaliBeam + "core_beam/core_beam.ao",                   // the laser barrage
        6550 => ArakaaliBeam + "beam/beam_head/beam_head.ao",
        6551 => ArakaaliBeam + "beam/beam_head/beam_head_nobuildup.ao",
        6879 => ArakaaliBeam + "beam_turret/turret_rig.ao",                // a laser turret activates
        7802 => ArakaaliBeam + "beam_turret/turret_rig.ao",
        7805 => ArakaaliBeam + "beam_turret/turret_rig.ao",
        2069 => RyslathaArt,                                               // the spit's rig
        3432 => RyslathaArt,
        _ => null,
    };

    const string TukohamaArt = "Metadata/Effects/Spells/monsters_effects/tukohama_e/";
    const string AbberathArt = "Metadata/Effects/Spells/monsters_effects/act6/abberath/";
    const string GruthkulArt = "Metadata/Effects/Spells/monsters_effects/act7/gruthkul/";
    const string DespairGeyser = "Metadata/Effects/Spells/monsters_effects/despair_bear_upheaval/despair_geyser.ao";
    const string ShakariArt = "Metadata/Effects/Spells/monsters_effects/act9/shakari/";
    const string GarukhanArt = "Metadata/Effects/Spells/monsters_effects/act9/garukhan/";
    const string YugulArt = "Metadata/Effects/Spells/monsters_effects/act8/yugul_silverfrog/";
    const string SpiderArt = "Metadata/Effects/Spells/monsters_effects/vaalspiderboss_e/";
    const string ArakaaliBeam = "Metadata/Effects/Spells/monsters_effects/act7/arakaali/beam/";
    const string RyslathaArt = "Metadata/Effects/Spells/monsters_effects/insect_spawner_spit/rig.ao";

    // Burrow art for Shakari's underground travel and her emergence, and Greust's charge trail.
    internal const string BurrowTravelling = "Metadata/Effects/Spells/monsters_effects/act9/shakari/burrow/travelling.ao";
    internal const string BurrowEmerge = "Metadata/Effects/Spells/monsters_effects/act9/shakari/burrow/emerge.ao";
    internal const string BurrowSubmerge = "Metadata/Effects/Spells/monsters_effects/act9/shakari/burrow/burrow_submerge_01.ao";

    // The bosses' intermission shield: BuffDefinitions row 350, the generic cannot_be_damaged buff (BrineKing.cs
    // plays the same row: no Pantheon boss has a shield BuffDefinitions row of its own, unlike the Atlas ones).
    internal const ushort ImmunityRow = 350;

    internal static bool IsTukohama(MonsterCatalog.Variety? v) => v?.Id == Tukohama;
    internal static bool IsAbberath(MonsterCatalog.Variety? v) => v?.Id is Abberath or Abberath2;
    internal static bool IsRyslatha(MonsterCatalog.Variety? v) => v?.Id == Ryslatha;
    internal static bool IsGreust(MonsterCatalog.Variety? v) => v?.Id == Greust;
    internal static bool IsGruthkul(MonsterCatalog.Variety? v) => v?.Id == Gruthkul;
    internal static bool IsArakaali(MonsterCatalog.Variety? v) => v?.Id == Arakaali;
    internal static bool IsYugul(MonsterCatalog.Variety? v) => v?.Id == Yugul;
    internal static bool IsSolaris(MonsterCatalog.Variety? v) => v?.Id == Solaris;
    internal static bool IsLunaris(MonsterCatalog.Variety? v) => v?.Id == Lunaris;
    internal static bool IsShakari(MonsterCatalog.Variety? v) => v?.Id is Shakari or Shakari2;
    internal static bool IsGarukhan(MonsterCatalog.Variety? v) => v?.Id == Garukhan;
    internal static bool IsBoss(MonsterCatalog.Variety? v) => v?.Id is not BrineKingId && (IsTukohama(v)
        || IsAbberath(v) || IsRyslatha(v) || IsGreust(v) || IsGruthkul(v) || IsArakaali(v) || IsYugul(v)
        || IsSolaris(v) || IsLunaris(v) || IsShakari(v) || IsGarukhan(v));

    internal static int[] EffectsFor(MonsterCatalog.Variety variety) => variety.Id switch
    {
        Tukohama => TukohamaEffects,
        Abberath or Abberath2 => variety.Id == Abberath ? AbberathEffects : Abberath2Effects,
        Ryslatha => RyslathaEffects,
        Greust => GreustEffects,
        Gruthkul => GruthkulEffects,
        Yugul => YugulEffects,
        Solaris => SolarisEffects,
        Lunaris => LunarisEffects,
        Shakari or Shakari2 => variety.Id == Shakari ? ShakariEffects : Shakari2Effects,
        Garukhan => GarukhanEffects,
        Arakaali => ArakaaliEffects,
        _ => [],
    };

    /// <summary>The kit's own melee: the row's first effect when that is a plain attack (the melee, the claw or the
    /// cleave); otherwise the first effect is the basic skill, the way Sirus' beam blast stands in for his swing.</summary>
    internal static int BasicEffect(MonsterCatalog.Variety variety)
    {
        var effects = EffectsFor(variety);
        return effects.Length > 0 ? effects[0] : MeleeEffect;
    }

    internal static bool MeleeLike(MonsterCatalog.Variety variety) => BasicEffect(variety) is MeleeEffect or BearMeleeEffect or CleaveEffect;

    /// <summary>The skill id the client's actor uses for one of the boss's effects: 0x4030 + the index in the
    /// merged kit. An effect outside the kit keeps an id of its own rather than colliding with the basic skill's.</summary>
    internal static ushort Skill(MonsterCatalog.Variety variety, int effect)
    {
        var effects = EffectsFor(variety);
        int index = Array.IndexOf(effects, effect);
        return (ushort)(0x4030 + (index >= 0 ? index : effects.Length + 1));
    }

    /// <summary>The actor's skill list: every effect the controller can play, so the client owns the clip a 0x1ab
    /// names — the same list the Atlas bosses hand over (AtlasBosses.SkillsFor).</summary>
    internal static IReadOnlyList<GrantedSkill>? SkillsFor(ClientState conn, MonsterCatalog.Variety variety,
        int level, MonsterAttack attack)
    {
        if (!IsBoss(variety)) return null;
        var catalog = Game.SkillsFor(conn);
        var effects = EffectsFor(variety);
        return [.. effects.Select(effect =>
        {
            var use = Attack(conn, variety, level, effect, attack);
            return catalog.FindByRow(Skill(variety, effect), effect, level,
                Game.WithBehaviourVariation(conn, effect, use.SkillStats()));
        }).OfType<GrantedSkill>()];
    }

    /// <summary>One of the boss's skills as the server plays it: its own damage at the area's level (the variety's
    /// physical damage when the stat set names none, scaled for the slams), its clip's cast time and reach. The
    /// melee keeps the variety's own attack time and reach; a marker (hide, burrow, dodge, a spawner) carries no
    /// hit at all.</summary>
    internal static MonsterAttack Attack(ClientState conn, MonsterCatalog.Variety variety, int level, int effect,
        MonsterAttack? swing = null)
    {
        var catalog = Game.SkillsFor(conn);
        var (min, max) = Monsters.BaseDamage(variety, (byte)Math.Clamp(level, 1, 100)) ?? (1, 1);
        double scale = DamageScale.GetValueOrDefault(effect, 1.0);
        var damage = catalog.HitDamage(effect, level);
        bool marker = Markers.Contains(effect);
        bool melee = effect == BasicEffect(variety) && MeleeLike(variety);
        if (marker) damage = DamagePacket.Empty;
        else if (damage.MaxTotal <= 0) damage = DamagePacket.Of(DamageType.Physical,
            (int)Math.Floor(min * scale), (int)Math.Ceiling(max * scale));
        double seconds = melee ? Math.Max(0.3, variety.AttackMs / 1000.0)
            : Math.Max(0.3, catalog.CastTimeMs(effect) / 1000.0);
        double reach = melee ? variety.AttackRange : 60;
        if (melee && swing is not null) return swing with { SkillId = Skill(variety, effect), Scripted = true };
        var projectile = Game.SkillsFor(conn).MonsterProjectile(effect);
        return new MonsterAttack(Skill(variety, effect), seconds, reach, damage)
        {
            Spell = catalog.IsSpell(effect),
            Scripted = true,
            Ranged = projectile is not null,
            Projectile = projectile,
        };
    }

    /// <summary>The cooldown between two casts of one effect (the rotation's pacing on top of the cast time).</summary>
    internal static double Cooldown(int effect) => Cooldowns.GetValueOrDefault(effect, 6);

    /// <summary>Runs the fight of every Pantheon boss in this connection's instance, once per tick per connection,
    /// the way <see cref="AtlasBosses.Tick"/> does: the instance's runner plays the steps, a joining player binds.</summary>
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
                Fights.Add(conn.Instance, byMonster = []);
            if (!byMonster.TryGetValue(id, out var fight))
            {
                fight = new Fight(id, monster, conn);
                byMonster[id] = fight;
                Console.WriteLine($"[gateway] {conn.Wire.Remote} 0x{id:x} {monster.Variety!.Name ?? monster.Variety.Id} "
                    + $"({monster.Variety.Id}, level {monster.Level}, life {monster.MaxLife}): Pantheon fight started, "
                    + $"{(IsMonsterRunner(conn) ? "its steps run here" : "another connection runs its steps")}");
            }
            await fight.Gate.WaitAsync();
            try
            {
                await fight.Bind(conn);
                if (IsMonsterRunner(conn)) await fight.Step(conn, conn.Clock.Elapsed.TotalSeconds);
            }
            finally { fight.Gate.Release(); }
        }
    }

    static readonly ConditionalWeakTable<InstanceState, Dictionary<uint, Fight>> Fights = new();

    /// <summary>Stops every fight of an instance (it is unloaded): the phase gates unlock and the walk speed returns.</summary>
    internal static IDisposable CleanupOnUnload() => new Cleanup();

    sealed class Cleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var pair in Fights)
                foreach (var fight in pair.Value.Values) fight.Stop();
        }
    }

    /// <summary>One instance of a Pantheon boss fight: the shared plumbing is the Atlas fights'
    /// <see cref="AtlasBossFight"/> (the players looking at it, the effect objects, the serialized step); the
    /// rotation below is this boss's own kit.</summary>
    internal sealed class Fight(uint id, SpawnedMonster boss, ClientState conn)
        : AtlasBossFight(id, boss, conn)
    {
        protected override string Name => $"[Pantheon {boss.Variety?.Id.Split('/')[^1]}]";
        protected override ushort ImmunityRow => PantheonBosses.ImmunityRow;
        protected override string ShieldArt => "";
        protected override string ShieldOffArt => "";
        // Arakaali never walks (MovementSpeed 0): she fights from her web at the arena's middle.
        protected override (int X, int Y) Stage(ClientState conn) => stage ??= ArenaCentre(conn, boss);
        (int X, int Y)? stage;

        readonly Dictionary<int, double> ready = [];
        // Tukohama's shielding totem: while it stands he cannot be damaged; the totem's lifetime is its own.
        double totemUntil;
        // Shakari's underground travel: she submerges, blinks to the player and emerges.
        bool burrowed;
        // The per-boss signature skill each intermission plays once the shield is up.
        int intermissionCast;

        protected override async Task Ticks(ClientState conn, double now)
        {
            // Tukohama's totem expires: the shield comes down and the clip's surge plays.
            if (totemUntil > 0 && now >= totemUntil)
            {
                totemUntil = 0;
                lock (conn.Monsters) boss.LifePhases?.Finish();
                var at = boss.Walk.Position(now);
                await Effect(conn, at, TukohamaArt + "sunder_surge.ao", 1.2);
                Console.WriteLine($"[{Name}] 0x{id:x}: the shielding totem falls");
            }
            if (burrowed)
            {
                burrowed = false;
                var at = boss.Walk.Position(now);
                await Effect(conn, at, BurrowEmerge, 1.2);
                // Emerging tail sweep: everyone close takes the tail strike's hit.
                var attack = Attack(conn, 8768);
                foreach (var player in Watchers())
                    if (Distance(player.Walk.Position(now), at) <= 14) await Hit(player, attack, at);
            }
        }

        protected override async Task Intermission(ClientState conn, double now)
        {
            // A quarter of the life gone: the shield comes up and the boss plays its signature move (the shared
            // Shield plays row 350's buff and removes it on the way out, the way the Atlas fights do).
            await Shield(conn, true);
            var at = Stage(conn);
            await Teleport(conn, at, now);
            intermissionCast = boss.Variety!.Id switch
            {
                Tukohama => 6665,      // the big slam
                Shakari or Shakari2 => 8768,
                Garukhan => 7857,
                Abberath or Abberath2 => 6748,
                Gruthkul => 6763,
                Arakaali => 6549,
                Solaris => 6462,
                Lunaris => 6471,
                Yugul => 6794,
                Ryslatha => 6266,
                Greust => 5623,
                _ => BasicEffect(boss.Variety),
            };
            var attack = Attack(conn, intermissionCast);
            if (ArtOf(intermissionCast) is { } art) await Effect(conn, at, art, Math.Max(2, attack.Seconds));
            SwingWith(conn, attack, intermissionCast, area: true, radius: attack.Range);
            busyUntil = Math.Max(busyUntil, now) + Math.Max(3, attack.Seconds);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, attack.Seconds)));
            await Shield(conn, false);
            EndIntermission();
        }

        protected override async Task Act(ClientState conn, double now)
        {
            var watchers = Watchers();
            if (watchers.Length == 0) return;
            var pos = boss.Walk.Position(now);
            var target = watchers.MinBy(p => Distance(pos, p.Walk.Position(now)))!;
            var player = target.Walk.Position(now);
            double distance = Distance(pos, player);
            var variety = boss.Variety!;
            var effects = EffectsFor(variety);
            bool stationary = variety.MovementSpeed == 0;

            // The rotation, in the kit's order: the first effect whose cooldown has passed and whose reach fits.
            foreach (int effect in effects)
            {
                if (ready.GetValueOrDefault(effect) > now) continue;
                bool melee = effect == BasicEffect(variety) && MeleeLike(variety);
                bool closeEnough = melee || effect is 808 or 807 or 4364 or 6792   // leaps and charges close in
                    ? distance <= Math.Max(variety.AttackRange, 6) + 4
                    : distance <= 60;
                if (!closeEnough) continue;
                ready[effect] = now + Cooldown(effect) + Attack(conn, effect).Seconds;
                await Cast(conn, effect, player, now, distance);
                return;
            }
            // Nothing ready: close the distance (the stationary ones hold the web).
            if (!stationary && distance > variety.AttackRange + 4)
            {
                await Move(conn, player, now);
                busyUntil = Math.Max(busyUntil, now) + 0.8;
            }
            else busyUntil = Math.Max(busyUntil, now) + 0.4;
        }

        async Task Cast(ClientState conn, int effect, (int X, int Y) player, double now, double distance)
        {
            var variety = boss.Variety!;
            var attack = Attack(conn, effect);
            var pos = boss.Walk.Position(now);
            if (ArtOf(effect) is { } art)
            {
                var at = distance <= attack.Range + 4 ? player : pos;
                await Effect(conn, at, art, Math.Max(1.5, attack.Seconds + 0.8));
            }
            switch (effect)
            {
                // Tukohama's shielding totem: the shield comes up for 5 s while the totem's beam plays.
                case 5373:
                    totemUntil = now + 5;
                    await Effect(conn, pos, TukohamaArt + "shieldingtotem/shieldingbeam.ao", 5);
                    lock (conn.Monsters) boss.LifePhases?.Begin(boss.Life, boss.MaxLife);
                    await All(InstanceEntities.AddBuff(id, conn.NextBuff++, ImmunityRow, id), monster: true);
                    Console.WriteLine($"[{Name}] 0x{id:x}: the shielding totem rises");
                    return;
                // Shakari's burrow: submerge, blink to the player; the emerge's sweep lands in Ticks.
                case 8802 or 6099 or 6100:
                    await Effect(conn, pos, BurrowSubmerge, 1.2);
                    await Teleport(conn, player, now);
                    burrowed = true;
                    busyUntil = Math.Max(busyUntil, now) + 1.5;
                    return;
                // Ryslatha's slide: she retreats from the player before spitting again.
                case 8449:
                {
                    int dx = pos.X - player.X, dy = pos.Y - player.Y;
                    double len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                    var to = ((int)(pos.X + dx / len * 12), (int)(pos.Y + dy / len * 12));
                    await Teleport(conn, to, now);
                    return;
                }
                default:
                    SwingWith(conn, attack, effect, area: !MeleeLike(variety) && effect != BasicEffect(variety),
                        radius: attack.Range);
                    return;
            }
        }
    }
}
