using System.Runtime.CompilerServices;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;

/// <summary>
/// The campaign's scripted bosses fight with their own kits instead of one plain swing. Upstream precedent:
/// the Atlas pinnacle bosses (838bdcc6, ShaperFight/ElderFight/SirusFight over the shared
/// <see cref="AtlasBossFight"/> plumbing) and the campaign Brine King (BrineKing.cs). The same fix applied to
/// the ten Pantheon bosses that had none, and now to the acts' bosses as they are taken one by one (the
/// <see cref="Acts"/> table names them; each entry's own mechanics are the cases in <see cref="Fight.Cast"/>).
/// Each variety's whole GrantedEffectsKeys list goes onto the actor
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
/// <para>A campaign boss's later form is not the controller's business: the quest engine creates it when the
/// first one falls (<see cref="GameServer.Core.World.Quest.QuestEngine"/> SecondForm — Merveil, Dominus,
/// Malachai), so the second form's variety is listed here only to give it a controller of its own.</para>
/// <para>Their art paths were checked against the client's metadata (data/game-files.zip, the
/// metadata/effects/spells/monsters_effects trees of act6/act7/act8/act9, tukohama_e, vaalspiderboss_e,
/// despair_bear_upheaval, yugul_silverfrog, insect_spawner_spit). Bosses whose kit rows name no distinctive
/// art of their own (Ryslatha's flame breath, Greust's blessing) play the effect row's own cast clip only.</para>
/// </summary>
static class CampaignBosses
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

    // The monsters the bosses raise, all real varieties the client can draw (checked with MonsterCatalog.Find:
    // every one is Supported). Tukohama's is the fight: while it stands he cannot be damaged.
    internal const string TotemVariety = "Metadata/Monsters/Tukohama/TukohamaShieldTotem";
    internal const string EggVariety = "Metadata/Monsters/SpiderlingEgg/SpiderlingEggArakaali";
    internal const string SpiderHatch = "Metadata/Monsters/SpiderPlague/SpiderArakaaliHatch";
    internal const string GarukhanMinion = "Metadata/Monsters/Garukhan/GarukhanMinionSmall";

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
        // Act 1. Brutus' hook and slam hit; his spiked ground is the patch the slam leaves (it hits on landing).
        8089, 9282,                    // empty_action_attack, geometry_attack
        4007,                          // merveil_teleport (she blinks; there is no hit in the row)
        4016, 4021, 4022, 4023,        // her summons: animate_weapon, statues, daughters, spawn
        2713,                          // the Weaver's hide (its burrow)
        6995,                          // geometry_spell (Merveil's second form)
        // Acts 3 and 4: the form changes, the warps, the dodge/geometry rows and the spawner triggers.
        2505, 2506,                    // piety_fire_form, piety_ice_form
        1278,                          // Lightning Warp (Piety warps; the case in Cast moves her)
        2941,                          // dominus_death_teleport
        5142, 5143,                    // kaom's archer and skull spawners
        4396, 4397, 4398,              // Daresso's monster_dodge rows
        5076, 5077, 5078, 5079, 5080,  // his empty_action_attack and geometry rows
        4652, 4614,                    // daresso_enrage, daresso_summon_clones (the clones are the AI's own adds)
        5243, 5244,                    // piety_activate_eye_spawner
        5236, 5189,                    // malachai_transform_piety, malachai_shield
        5017,                          // malachai_teleport (the case in Cast moves him)
        5245,                          // malachai_open_heart
        // Acts 5 to 10: the statue and templar spawners, the warps, the hide and stance changes.
        6326, 6320, 6327, 6654,        // avarius_raise_statues, avarius_summon_templars, avarius_kill_statues
        5066, 5067,                    // shavronne_teleport (the case in Cast moves her)
        6674, 6675,                    // summon_specific_monsters_on_self/_on_target
        6426,                          // maligaro_vanish
        9402, 6203,                    // vilenta's hide and her change_to_stance2
        9083, 9085, 7901, 7903, 7904,  // her geometry and empty_action rows
        6328, 6329, 6322,              // Innocence's own raise_statues and summon_templars rows
        // The Labyrinth: Izaro's taunt, his leaving, the shield he raises and the lieutenants the arena calls for.
        5583, 5558, 5582, 5580,
        5922,                          // effect_driven_attack: it releases its own effect, no hit of its own
        // Atziri: the demons she calls and the mirrors she raises are spawns, not hits.
        3386, 3401, 3402,
    ];

    // The summon rows whose monster the data names: what the boss raises and how many at a time. The rows'
    // stat sets carry no hit, so they stay markers as well — the clip plays and the monsters arrive with it.
    // Rows the data leaves unnamed (Dominus' lightning souls, Malachai's hearts and his flayed ones, Maligaro's
    // blades) stay plain markers. Every variety here is one the blob can serialize (data/object-types.json: the
    // base Monster list plus the extras the write knows) — a summon the catalog cannot create is a skill that
    // silently does nothing, so a row whose real monster carries a component the write cannot serialize
    // (AvariusCasticusStatue's TriggerableBlockage, DaressoStatueMaleSword, SkeletonMeleeLightningShavronne) raises
    // a kindred supported variety instead.
    internal const string AvariusStatue = "Metadata/Monsters/BloodElemental/BloodElemental";   // a Guardian of Eternity
    internal const string KaomArcher = "Metadata/Monsters/Daemon/KaomVolcanoMortar";           // his skull-and-arrow daemon
    internal const string BloodElemental = "Metadata/Monsters/BloodElemental/BloodElemental";
    internal const string PietyEye = "Metadata/Monsters/SummonedSkull/SummonedArcaneEye";      // an eye of the abomination
    internal const string ShavronneSkeleton = "Metadata/Monsters/Skeletons/SkeletonCasterLightning";
    internal const string DaressoStatue = "Metadata/Monsters/BloodElemental/BloodElemental";   // the arena statue stands in
    internal const string AtziriKnight = "Metadata/Monsters/Skeletons/SkeletonMeleeKnightElementalSwordAtziri";
    internal const string AtziriMirror = "Metadata/Monsters/Atziri/AtziriMirror";

    internal static readonly Dictionary<int, (string Variety, int Count)> Spawns = new()
    {
        [6326] = (AvariusStatue, 2), [6327] = (AvariusStatue, 2), [6328] = (AvariusStatue, 2),
        [6329] = (AvariusStatue, 2),                       // avarius_raise_statues (all four rows)
        [5142] = (KaomArcher, 3),                          // trigger_kaom_archer_spawners
        [2507] = (BloodElemental, 2),                      // piety_summon_blood_elementals
        [5243] = (PietyEye, 1), [5244] = (PietyEye, 1),    // piety_activate_eye_spawner
        [6674] = (ShavronneSkeleton, 2),                   // summon_specific_monsters_on_self
        [6675] = (ShavronneSkeleton, 2),                   // summon_specific_monsters_on_target
        [4614] = (DaressoStatue, 2),                       // daresso_summon_clones (the arena's statues fight)
        [3386] = (AtziriKnight, 3),                        // atziri_summon_demons
        [3401] = (AtziriMirror, 2), [3402] = (AtziriMirror, 2), // atziri_mirror_image (her own copies)
    };

    // How many monsters one boss may have alive at once; a summon row that would go past it raises nothing.
    const int AddCap = 6;

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
        // Act 1. Brutus hooks, then slams, then leaves the spikes; Merveil blinks often and throws a spear or a
        // firestorm between blinks; the Weaver hides to reposition and mortars in between.
        [2395] = 9, [4003] = 11, [3971] = 13,
        [3774] = 7, [4007] = 10, [4016] = 18, [1422] = 12, [4021] = 16,
        [3777] = 6, [3781] = 12, [3250] = 15, [3329] = 9, [4491] = 8, [4022] = 20, [4023] = 14,
        [2247] = 6, [4440] = 10, [2713] = 16,
        // Acts 3 and 4. One boss per act final: each of them keeps one signature skill on a long cooldown.
        [2267] = 6, [2315] = 7, [2486] = 9, [1479] = 12, [2507] = 20, [1278] = 12,
        [2673] = 8, [2679] = 14, [2744] = 11, [2677] = 16, [2682] = 18, [2941] = 20,
        [2717] = 6, [2722] = 7, [2928] = 14, [2743] = 11, [2858] = 16,
        [119] = 10, [5035] = 14, [5142] = 25, [5143] = 25,
        [4752] = 6, [5075] = 9, [5081] = 12, [4614] = 22, [4652] = 30,
        [5000] = 7, [5003] = 11, [5304] = 8, [5243] = 20,
        [5014] = 7, [5015] = 12, [5167] = 10, [2140] = 9, [5146] = 11, [5241] = 13, [5251] = 15,
        [5257] = 12, [5210] = 10, [5245] = 18, [5017] = 14,
        // Acts 5 to 10: the boss-bar finals pace on the same cast time, with one signature skill held back.
        [6284] = 7, [6314] = 8, [6318] = 11, [6291] = 12, [6566] = 10, [6326] = 20, [6320] = 22,
        [6316] = 8, [6288] = 9, [6285] = 7, [6327] = 20,
        [6590] = 6, [6598] = 10, [6603] = 12, [6636] = 14,
        [6592] = 6, [6587] = 12, [6589] = 16, [8484] = 14, [8508] = 18, [8678] = 12, [6625] = 20,
        [3804] = 6, [6671] = 7, [6673] = 9, [6691] = 14, [6715] = 11, [5066] = 12,
        [492] = 6, [493] = 6, [6709] = 11, [6367] = 10, [6369] = 13,
        [6415] = 6, [6422] = 8, [6434] = 9, [6418] = 10, [5596] = 11, [6412] = 12,
        [5474] = 10, [4740] = 14,
        [8904] = 9, [8896] = 12, [357] = 7, [5603] = 10, [6388] = 11, [5637] = 10,
        // The Labyrinth: Izaro's leap slam, Voll's slam, Voll's charge and his ornaments.
        [811] = 12, [4386] = 14, [4362] = 16, [5535] = 10,
        // Atziri's storm calls, flameblasts and spear throws, empowered slower, and her two spawns.
        [3387] = 8, [3389] = 10, [3392] = 9, [3393] = 11, [3397] = 6, [3398] = 7, [3386] = 20, [3401] = 25, [3402] = 25,
        // The invaders' pinnacles. Each keeps its named attacks on the long end and its filler rows short, so the kit
        // reads as a rotation instead of one row being picked again every time its six seconds are up.
        [12177] = 12, [12182] = 16, [12183] = 10, [4563] = 9, [7396] = 8, [7372] = 7, [12178] = 14,
        [7381] = 9, [7383] = 10, [7392] = 16, [12193] = 12, [7402] = 8, [7403] = 7,
        [12185] = 14, [12186] = 11, [7364] = 7, [7365] = 12, [7379] = 8, [7389] = 9,
        [7373] = 8, [1887] = 16, [4564] = 9, [4353] = 14, [7386] = 10, [7387] = 7, [7388] = 20,
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
    // ---- the acts' bosses: one entry per variety the controller drives ----
    // Act 1 has no campaign doc (there is no act1.md), so its mechanics are the data's: the kit is the variety's
    // own GrantedEffectsKeys in its order, and the cases in Fight.Cast are the ones the rows name (a hook, a slam,
    // a teleport, a hide). A later form is listed as well: QuestEngine SecondForm creates it when the first one
    // falls, and it needs its own controller to fight with its own kit.
    internal const string Brutus = "Metadata/Monsters/Brute/BossBrute";                  // a1q2, The Upper Prison
    internal const string Merveil = "Metadata/Monsters/seawitchillusion/BossMerveil";     // a1q3, Cavern of Anger
    internal const string MerveilTwisted = "Metadata/Monsters/Seawitch/BossMerveil2";     // her second form
    internal const string Weaver = "Metadata/Monsters/Spiders/SpiderBossWeaver";         // The Weaver's Chambers
    internal const string Piety = "Metadata/Monsters/Axis/PietyBoss";                    // a3q7, Lunaris Temple 2
    internal const string Dominus = "Metadata/Monsters/Pope/Pope";                       // a3q11, Upper Sceptre of God
    internal const string DominusAscendant = "Metadata/Monsters/Dominusdemon/Dominusdemon"; // 1688, his second form
    internal const string Kaom = "Metadata/Monsters/Kaom/KaomBoss";                      // a4q2, Kaom's Stronghold
    internal const string Daresso = "Metadata/Monsters/Daresso/DaressoBoss";             // a4q3, The Grand Arena
    internal const string PietyBeast = "Metadata/Monsters/Axis/PietyBeastBoss";          // a4q4, Belly of the Beast 2
    internal const string Malachai = "Metadata/Monsters/Malachai/MalachaiBoss";          // a4q5, The Harvest
    internal const string Malachai2 = "Metadata/Monsters/Malachai/MalachaiBoss2";        // his second stage
    internal const string Avarius = "Metadata/Monsters/AvariusCasticus/AvariusCasticus";  // a5q?, Chamber of Innocence
    internal const string AvariusReassembled = "Metadata/Monsters/AvariusCasticus/AvariusCasticusWickerman"; // a10q?
    internal const string AvariusDivine = "Metadata/Monsters/AvariusCasticus/AvariusCasticusDivine"; // Innocence, a5
    // Kitava's two fights are main's own controllers (KitavaActFive.cs, KitavaActTen.cs): they already script the swipe,
    // the slam, the debris and the final fight's rows, and they own the heart, Sin and Innocence too, so this table
    // leaves them alone rather than giving one body two kits.
    internal const string ShavronneTower = "Metadata/Monsters/Shavronne/ShavronneTower"; // a6q2, Shavronne's Tower
    internal const string BruteTower = "Metadata/Monsters/Brute/BruteTower";             // the same arena's second boss
    internal const string Maligaro = "Metadata/Monsters/Maligaro/Maligaro2";             // a7q2, Maligaro's Sanctum
    internal const string GeneralAdus = "Metadata/Monsters/GeneralAdus/GeneralAdus";     // a9q?, The Refinery
    internal const string Vilenta = "Metadata/Monsters/Vilenta/Vilenta";                 // a10q?, The Control Blocks

    // The Lord's Labyrinth: Izaro's three weapons. The weapon rows share the LabyrinthBoss type (their own Ids have no
    // .ot; BaseMonsterTypeIndex names it), so the day's weapon is only a different kit on the same body.
    internal const string IzaroMaul = "Metadata/Monsters/Labyrinth/LabyrinthBossMaul";
    internal const string IzaroOneHand = "Metadata/Monsters/Labyrinth/LabyrinthBossOneHandAndShield";
    internal const string IzaroDualWield = "Metadata/Monsters/Labyrinth/LabyrinthBossDualWield";

    // The Apex of Sacrifice's Atziri, both forms (she has no Default Attack: the spear throw is what she swings with).
    internal const string Atziri = "Metadata/Monsters/Atziri/Atziri";
    internal const string Atziri2 = "Metadata/Monsters/Atziri/Atziri2";

    // The four Atlas invaders' pinnacles. They are the first endgame fights with a hand-picked kit: each is a body whose
    // granted rows are mostly spawners and triggers (the damage lives in the objects and projectiles those raise), so the
    // rows with an area or a projectile of their own are the ones that land a hit, and the rest stay markers.
    internal const string SearingExarch = "Metadata/Monsters/AtlasInvaders/CleansingMonsters/CleansingBoss";   // Absence of Patience and Wisdom
    internal const string EaterOfWorlds = "Metadata/Monsters/AtlasInvaders/ConsumeMonsters/ConsumeBoss";       // Absence of Symmetry and Harmony
    internal const string BlackStar = "Metadata/Monsters/AtlasInvaders/BlackStarMonsters/BlackStarBoss";         // Polaric Void
    internal const string InfiniteHunger = "Metadata/Monsters/AtlasInvaders/DoomMonsters/DoomBoss_";            // Seething Chyme

    // The Searing Exarch (Absence of Patience and Wisdom): the flame wall that walks across the arena and applies ruin,
    // the moving wall it pushes, the mortar volley, and the two area rows behind them. The empty actions, the dash and
    // the summon stay markers, so the body never plays one of them as a hit.
    internal static readonly int[] SearingExarchEffects = [422, 12177, 12182, 12183, 4563, 7396, 7372, 12178];
    // The Eater of Worlds (Absence of Symmetry and Harmony): the dash that closes the gap, its geometry rows (the area
    // hits), the ground laser and two more area rows. The spawn, trigger and empty-action rows stay markers, so the
    // body never plays one of them as a physical slam.
    internal static readonly int[] EaterOfWorldsEffects = [420, 10554, 7381, 7383, 7392, 12193, 7402, 7403];
    // The Black Star (Polaric Void): the comet barrage and the laser spears are its two named attacks, then the area
    // rows either side of them. The two empty actions and the dash stay markers.
    internal static readonly int[] BlackStarEffects = [420, 12185, 12186, 7364, 7365, 7379, 7389];
    // The Infinite Hunger (Seething Chyme): it alternates between two stances (both markers, and the rows they gate are
    // the ones that hit), so the kit is the area rows, the two mortar spells and the shield charge.
    internal static readonly int[] InfiniteHungerEffects = [420, 7388, 1887, 4353, 7373, 4564, 7386, 7387];

    // Brutus: his plain swing is row 491 (his own Default Attack, not 406), then the hook that pulls the player in,
    // the ground slam and the spiked ground it leaves.
    internal static readonly int[] BrutusEffects = [491, 2395, 4003, 3971];
    // Merveil: ice spears, the teleport that repositions her, the statues she animates and the firestorm.
    internal static readonly int[] MerveilEffects = [406, 3774, 4007, 4016, 1422, 4021];
    // Her second form has no plain swing at all: the geyser is what stands in for one.
    internal static readonly int[] MerveilTwistedEffects = [3777, 4007, 4022, 4023, 3781, 3250, 3329, 4491, 6995];
    // The Weaver hides (its burrow), throws knives and lobs its mortar.
    internal static readonly int[] WeaverEffects = [420, 2247, 4440, 2713];
    // Piety switches between her ice and fire forms (2506/2505) and warps; her blood elementals are her summons.
    internal static readonly int[] PietyEffects = [406, 2267, 2315, 2486, 1479, 2507, 1278];
    // Dominus: the chilling bond, Touch of God (his real swing), the dash, the lightning souls and his mark.
    internal static readonly int[] DominusEffects = [2673, 2679, 2744, 2677, 2682, 2941];
    // His second form (Dominusdemon): the cleave, the projectile, the grab, the charge and the blood rain.
    internal static readonly int[] DominusAscendantEffects = [406, 2717, 2722, 2928, 2743, 2858];
    // Kaom: the leap slam and the fire chasm he opens in the floor; the archers and skulls are his spawners.
    internal static readonly int[] KaomEffects = [3757, 119, 5035, 5142, 5143];
    // Daresso: the combo, the reave, the charge, the clones he summons and his enrage.
    internal static readonly int[] DaressoEffects = [406, 4752, 5075, 5081, 4614, 4652];
    // Piety, the Abomination: the corrupted ball lightning and beam, her reverse cleave, the eye spawner.
    internal static readonly int[] PietyBeastEffects = [406, 5000, 5003, 5304, 5243];
    // Malachai: the cone, the teleport slam, Voll's slam, the fire traps, the mortar, the impale and the tri bomb.
    internal static readonly int[] MalachaiEffects = [494, 5014, 5015, 5167, 2140, 5146, 5241, 5251];
    // His second stage: the cone, the repeatable wave, the teleport, the mortars, the open heart and the tri bomb.
    internal static readonly int[] Malachai2Effects = [5014, 5257, 5015, 5210, 2140, 5251, 5017];
    // Avarius: the cleave, the rapid projectile, the divine slam, the charge, the statues and his templars.
    internal static readonly int[] AvariusEffects = [406, 6284, 6314, 6318, 6291, 6566, 6326, 6320];
    // Reassembled (the wickerman form): no slam of his own left, only the projectiles, the cleave and the statues.
    internal static readonly int[] AvariusReassembledEffects = [406, 6316, 6288, 6285, 6327];
    // Innocence (his second form in the same fight): the cleave, the super projectile, the delayed blast, the divine
    // beam, the charge and the firestorm; he raises the same statues.
    internal static readonly int[] AvariusDivineEffects = [406, 6287, 6293, 2728, 6563, 6568, 1466, 6326];
    // Shavronne: the pulse, the levitating projectile, the empower beam and the barrage; she teleports and summons.
    internal static readonly int[] ShavronneEffects = [3804, 6671, 6673, 6691, 6715, 5066];
    // Reassembled Brutus: his two Default Attacks, the wall hook and the levitation slam.
    internal static readonly int[] BruteTowerEffects = [492, 493, 6709, 6367, 6369];
    // Maligaro: the combo, the blades in front and behind, the flurry, the telekinetic blades and the teleport slam.
    internal static readonly int[] MaligaroEffects = [406, 6415, 6422, 6434, 6418, 5596, 6412];
    // General Adus: his own swing, the earthquake and the living bomb.
    internal static readonly int[] AdusEffects = [406, 5474, 4740];
    // Vilenta: the gas mortars, the reflection, Whirling Blades, her slam, the breach mortar and the sunder.
    internal static readonly int[] VilentaEffects = [406, 8904, 8896, 357, 5603, 6388, 5637];
    // Izaro, two-handed mace: the swing, the leap slam and Voll's slam (the pile-driver the arena shakes with), and
    // the ornaments he raises off the floor.
    internal static readonly int[] IzaroMaulEffects = [482, 811, 4386, 5535];
    // Izaro, sword and shield: the swing, Voll's charge and the effect-driven attack that follows his block.
    internal static readonly int[] IzaroOneHandEffects = [482, 4362, 5922, 5535];
    // Izaro, two swords: the swing, the leap slam and the effect-driven attack.
    internal static readonly int[] IzaroDualWieldEffects = [482, 811, 5922, 5535];
    // Atziri: the spear throw (her only attack-like row, so it is her swing), Storm Call and its empowered form, the
    // Flameblast and its empowered form, the demons she calls and the mirrors of herself.
    internal static readonly int[] AtziriEffects = [3397, 3387, 3392, 3398, 3389, 3393, 3386, 3401];
    // Her second form (Atziri2) is the same kit but for its own mirror row.
    internal static readonly int[] Atziri2Effects = [3397, 3387, 3392, 3398, 3389, 3393, 3386, 3402];

    // What stands in for a campaign boss's own swing where the kit's first row is not an attack (BasicEffect).
    static readonly Dictionary<string, int> ActBasic = new()
    {
        [Brutus] = 491,            // his hook leads the row; the swing is 491
        [MerveilTwisted] = 3777,   // no Default Attack in her row: the geyser is her basic skill
        [Dominus] = 2679,          // no Default Attack either: Touch of God is what he swings
        [Malachai2] = 5014,        // likewise: the elemental cone
        [IzaroMaul] = 482,         // his rows' "melee" is the swing, for all three weapons
        [IzaroOneHand] = 482,
        [IzaroDualWield] = 482,
        [Atziri] = 3397,           // no Default Attack in her row: the spear throw is her basic skill
        [Atziri2] = 3397,
    };

    internal static bool IsAct(MonsterCatalog.Variety? v) => v?.Id is Brutus or Merveil or MerveilTwisted or Weaver
        or Piety or Dominus or DominusAscendant or Kaom or Daresso or PietyBeast or Malachai or Malachai2
        or Avarius or AvariusReassembled or ShavronneTower or BruteTower or Maligaro
        or GeneralAdus or Vilenta or AvariusDivine;

    // The Lord's Labyrinth's Izaro: one body, the day's weapon.
    internal static bool IsLabyrinth(MonsterCatalog.Variety? v) => v?.Id is IzaroMaul or IzaroOneHand or IzaroDualWield;

    // The Apex of Sacrifice's Atziri, both forms.
    internal static bool IsApex(MonsterCatalog.Variety? v) => v?.Id is Atziri or Atziri2;

    // The four Atlas invaders' pinnacles: the first endgame fights whose kits are picked row by row.
    internal static bool IsPinacle(MonsterCatalog.Variety? v) =>
        v?.Id is SearingExarch or EaterOfWorlds or BlackStar or InfiniteHunger;

    /// <summary>Whether this controller scripts the boss: the acts', Izaro, Atziri, the invaders' pinnacles, the Pantheon's twelve.</summary>
    internal static bool IsOwned(MonsterCatalog.Variety? v) => v?.Id is not BrineKingId && (IsAct(v) || IsLabyrinth(v) || IsApex(v)
        || IsPinacle(v)
        || IsTukohama(v)
        || IsAbberath(v) || IsRyslatha(v) || IsGreust(v) || IsGruthkul(v) || IsArakaali(v) || IsYugul(v)
        || IsSolaris(v) || IsLunaris(v) || IsShakari(v) || IsGarukhan(v));

    /// <summary>A bar-boss no other controller claims: the map, league and pinnacle fights the atlas, the Maven, the
    /// Elder, Sirus, the Brine King, the Depraved Trinity, Doedre and the King in the Mists do not own. Its own granted
    /// rows are its whole kit (<see cref="EndgameKit"/>).</summary>
    internal static bool IsEndgame(MonsterCatalog.Variety? v) => v is { BossHealthBar: true, GrantedEffects.Count: > 0 } && !IsOwned(v)
        && !AtlasBosses.IsBoss(v) && !BrineKing.IsBoss(v) && !DepravedTrinity.IsBoss(v) && !DoedreSewers.IsBoss(v)
        && !KingInMists.IsBoss(v) && !VaalOversoul.IsBoss(v)
        // Kitava keeps his own fights (KitavaActFive.cs, KitavaActTen.cs), which own his body, heart, Sin and Innocence:
        // giving the body a second kit here would rotate rows over the encounter's own actions.
        && !KitavaActFive.IsActor(v) && !KitavaActTen.IsActor(v);

    internal static bool IsBoss(MonsterCatalog.Variety? v) => IsOwned(v) || IsEndgame(v);

    // ---- The endgame bosses' kits ----
    //
    // Nobody picked these rows one by one (there are over a hundred of them), so a boss's own granted rows are its kit:
    // the swing first and at most six of them, so the rotation stays readable instead of cycling a whole row list. A row
    // whose skill name reads as a spawn, a stance, a warp, a dodge or a taunt carries no hit of its own even though no
    // hand-picked list names it (Marker), which is what keeps a summon row from being played as a physical hit.

    const int EndgameKitSize = 6;

    /// <summary>The endgame boss's kit: its own rows, the swing first.</summary>
    internal static int[] EndgameKit(MonsterCatalog.Variety variety)
    {
        if (variety.GrantedEffects is not { Count: > 0 } rows) return [];
        int swing = rows.FirstOrDefault(IsOwnSwing);
        List<int> kit = swing == 0 ? [] : [swing];
        foreach (int effect in rows)
            if (effect != swing && kit.Count < EndgameKitSize) kit.Add(effect);
        return [.. kit];
    }

    /// <summary>The row a boss swings with when nobody named one: its Default Attack, else the plain melee (Izaro's
    /// row is "melee", Brutus' "cleave").</summary>
    static bool IsOwnSwing(int effect) => EffectName(effect) is string name
        && (name.Contains("default_attack", StringComparison.Ordinal) || name is "melee" or "cleave" or "double_strike");

    /// <summary>A row with no hit of its own: the hand-picked ones, and any row whose skill name says it spawns, hides,
    /// warps, dodges or taunts.</summary>
    internal static bool Marker(int effect) => Markers.Contains(effect)
        || (EffectName(effect) is string name && UnhitNameParts.Any(part => name.Contains(part, StringComparison.Ordinal)));

    static readonly string[] UnhitNameParts =
    [
        "summon", "spawn", "raise_", "taunt", "teleport", "warp", "blink", "dodge", "hide", "burrow", "emerge",
        "mirror", "portal", "transform", "change_to_stance", "_shield", "flee",
    ];

    /// <summary>An effect's skill id (GrantedEffects' ActiveSkill row's Id), read once.</summary>
    internal static string? EffectName(int effect) => EffectSkillNames.Value.GetValueOrDefault(effect);

    static readonly Lazy<Dictionary<int, string>> EffectSkillNames = new(LoadEffectSkillNames);

    static Dictionary<int, string> LoadEffectSkillNames()
    {
        var names = new Dictionary<int, string>();
        try
        {
            using var effects = GameTables.Open("data/tables/GrantedEffects.json");
            using var skills = GameTables.Open("data/tables/ActiveSkills.json");
            string[] ids = [.. skills.RootElement.EnumerateArray()
                .Select(r => r.TryGetProperty("Id", out var id) ? id.GetString() ?? "" : "")];
            int row = 0;
            foreach (var effect in effects.RootElement.EnumerateArray())
            {
                if (effect.TryGetProperty("ActiveSkill", out var skill) && skill.ValueKind == JsonValueKind.Number
                    && ids.ElementAtOrDefault(skill.GetInt32()) is { Length: > 0 } name) names[row] = name;
                row++;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            Console.WriteLine($"[game] the effects' names: {ex.Message}");
        }
        return names;
    }

    internal static int[] EffectsFor(MonsterCatalog.Variety variety) => variety.Id switch
    {
        Brutus => BrutusEffects,
        IzaroMaul => IzaroMaulEffects,
        IzaroOneHand => IzaroOneHandEffects,
        IzaroDualWield => IzaroDualWieldEffects,
        Atziri => AtziriEffects,
        Atziri2 => Atziri2Effects,
        SearingExarch => SearingExarchEffects,
        EaterOfWorlds => EaterOfWorldsEffects,
        BlackStar => BlackStarEffects,
        InfiniteHunger => InfiniteHungerEffects,
        Merveil => MerveilEffects,
        MerveilTwisted => MerveilTwistedEffects,
        Weaver => WeaverEffects,
        Piety => PietyEffects,
        Dominus => DominusEffects,
        DominusAscendant => DominusAscendantEffects,
        Kaom => KaomEffects,
        Daresso => DaressoEffects,
        PietyBeast => PietyBeastEffects,
        Malachai => MalachaiEffects,
        Malachai2 => Malachai2Effects,
        Avarius => AvariusEffects,
        AvariusReassembled => AvariusReassembledEffects,
        AvariusDivine => AvariusDivineEffects,
        ShavronneTower => ShavronneEffects,
        BruteTower => BruteTowerEffects,
        Maligaro => MaligaroEffects,
        GeneralAdus => AdusEffects,
        Vilenta => VilentaEffects,
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
        // The endgame bosses: nobody picked their rows one by one, so their own granted rows are their kit.
        _ => EndgameKit(variety),
    };

    /// <summary>The kit's own melee: the row's first effect when that is a plain attack (the melee, the claw or the
    /// cleave); otherwise the first effect is the basic skill, the way Sirus' beam blast stands in for his swing.</summary>
    internal static int BasicEffect(MonsterCatalog.Variety variety)
    {
        if (ActBasic.TryGetValue(variety.Id, out int basic)) return basic;
        var effects = EffectsFor(variety);
        return effects.Length > 0 ? effects[0] : MeleeEffect;
    }

    internal static bool MeleeLike(MonsterCatalog.Variety variety) => BasicEffect(variety) is MeleeEffect or BearMeleeEffect
        or CleaveEffect or 491 or 492 or 493;   // 491/492/493: the Brute rows' own Default Attack

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
        bool marker = Marker(effect);
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
                // The boss is gone: whatever he raised goes with him (his controller will not step again).
                if (monster.Dead || monster.Life == 0) await fight.RetireAdds(conn);
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
                foreach (var fight in pair.Value.Values) { fight.RetireAll(); fight.Stop(); }
        }
    }

    /// <summary>One instance of a Pantheon boss fight: the shared plumbing is the Atlas fights'
    /// <see cref="AtlasBossFight"/> (the players looking at it, the effect objects, the serialized step); the
    /// rotation below is this boss's own kit.</summary>
    internal sealed class Fight(uint id, SpawnedMonster boss, ClientState conn)
        : AtlasBossFight(id, boss, conn)
    {
        protected override string Name => $"[Pantheon {boss.Variety?.Id.Split('/')[^1]}]";
        protected override ushort ImmunityRow => CampaignBosses.ImmunityRow;
        protected override string ShieldArt => "";
        protected override string ShieldOffArt => "";
        // Arakaali never walks (MovementSpeed 0): she fights from her web at the arena's middle.
        protected override (int X, int Y) Stage(ClientState conn) => stage ??= ArenaCentre(conn, boss);
        (int X, int Y)? stage;

        readonly Dictionary<int, double> ready = [];
        // The monsters a boss raises: Tukohama's shield totem, Arakaali's eggs, Garukhan's sand warriors. All are
        // real varieties (TukohamaShieldTotem, SpiderlingEggArakaali, GarukhanMinionSmall) owned by this boss and
        // retired with it; the ordinary AI runs them once they exist.
        readonly List<uint> adds = [];
        // A cast whose monsters arrive with it rather than at once: a mortar's eggs land when its clip does, the
        // warriors rise out of the cascade a moment after it. (Due, variety, X, Y, is the shield totem.)
        readonly List<(double Due, string Variety, int X, int Y, bool Totem)> pending = [];
        readonly List<(uint Id, double HatchAt, int X, int Y)> eggs = [];
        // Tukohama's shield totem: while one of his stands he cannot be damaged, and killing it is what lets the
        // fight continue (BuffDefinitions row 350, the buff the intermission shield uses too).
        uint totem;
        ushort totemBuff;
        double totemUntil;
        int hatchlings;
        // Shakari's underground travel: she submerges, blinks to the player and emerges.
        bool burrowed;

        protected override async Task Ticks(ClientState conn, double now)
        {
            // Monsters a cast of this fight raises, once their own moment comes.
            foreach (var spawn in pending.Where(p => p.Due <= now).ToArray())
            {
                pending.Remove(spawn);
                uint raised = await Add(conn, spawn.Variety, (spawn.X, spawn.Y));
                if (!spawn.Totem || raised == 0) continue;
                totem = raised;
                totemUntil = now + 20;
                await TotemShield(conn, true);
                Console.WriteLine($"[{Name}] 0x{id:x}: the shielding totem rises (0x{raised:x})");
            }
            // The shield totem came down (killed, or left standing too long): his immunity goes with it.
            if (totem != 0 && (now >= totemUntil || !Alive(conn, totem)))
            {
                uint gone = totem;
                totem = 0;
                var at = Position(conn, gone) ?? boss.Walk.Position(now);
                await TotemShield(conn, false);
                await Effect(conn, at, TukohamaArt + "sunder_surge.ao", 1.2);
                await Retire(conn, gone);
                Console.WriteLine($"[{Name}] 0x{id:x}: the shielding totem falls, Tukohama is exposed");
            }
            // An egg hatches when its time comes or when the player breaks it open.
            foreach (var egg in eggs.Where(e => e.HatchAt <= now || !Alive(conn, e.Id)).ToArray())
            {
                eggs.Remove(egg);
                if (hatchlings < 12 && await Add(conn, SpiderHatch, (egg.X, egg.Y)) != 0) hatchlings++;
                await Retire(conn, egg.Id);
            }
            if (burrowed)
            {
                burrowed = false;
                var at = boss.Walk.Position(now);
                // Shakari breaks out of the sand with a tail sweep everyone close takes; the Weaver's hide is only a
                // reposition (2713), so it comes back without either.
                if (!IsShakari(boss.Variety)) return;
                await Effect(conn, at, BurrowEmerge, 1.2);
                var attack = Attack(conn, 8768);
                foreach (var player in Watchers())
                    if (Distance(player.Walk.Position(now), at) <= 14) await Hit(player, attack, at);
            }
        }

        protected override Task Intermission(ClientState conn, double now)
        {
            // Unreachable on purpose: a Pantheon boss has no life gate (see the gate in Monsters.cs). The campaign
            // bosses do not stop taking damage at a threshold — the two-variety phases the data describes (Abberath
            // 6008→6012, Shakari 6275→6278) are a second monster in a second room, both already placed by
            // WorldAreas' Bosses, and Tukohama's totem shield is his own mechanic (Cast 5373). Should a gate ever be
            // given to one of these kits again, this closes it instead of standing behind a shield nothing lifts.
            EndIntermission();
            return Task.CompletedTask;
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
            // A summon row raises the boss's own monsters, owned by it (RetireAll takes them with it). They arrive
            // with the clip rather than at once, the way Garukhan's warriors rise out of her cascade.
            if (Spawns.TryGetValue(effect, out var add) && AliveAdds(conn) < AddCap)
                for (int k = 0; k < add.Count; k++)
                    pending.Add((now + Math.Max(0.8, attack.Seconds), add.Variety,
                        pos.X + 3 - 3 * k, pos.Y + 2, false));
            if (ArtOf(effect) is { } art)
            {
                var at = distance <= attack.Range + 4 ? player : pos;
                await Effect(conn, at, art, Math.Max(1.5, attack.Seconds + 0.8));
            }
            switch (effect)
            {
                // Tukohama's shielding totem: a real monster of his own stands beside him for the fight to break.
                case 5373:
                    if (totem != 0) return;   // one already stands: he waits behind it instead of raising another
                    pending.Add((now + Math.Max(0.8, attack.Seconds), TotemVariety,
                        pos.X + (player.X >= pos.X ? 5 : -5), pos.Y, true));
                    return;
                // Arakaali's egg mortar: the eggs land where the mortar does and hatch into her broodlings.
                case 5221 or 5222:
                    SwingWith(conn, attack, effect, area: true, radius: attack.Range);
                    if (eggs.Count < 4) pending.Add((now + Math.Max(1, attack.Seconds), EggVariety, player.X, player.Y, false));
                    return;
                // Garukhan's cascade: the sand it throws up is what her warriors rise out of.
                case 8445:
                    SwingWith(conn, attack, effect, area: true, radius: attack.Range);
                    if (AliveAdds(conn) < 4)
                        for (int k = 0; k < 2; k++)
                            pending.Add((now + Math.Max(1, attack.Seconds), GarukhanMinion, player.X + 3 * k, player.Y, false));
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
                // Merveil blinks away from the player before her next spell (merveil_teleport), and Dominus leaves
                // his body the same way when the first form falls (dominus_death_teleport).
                case 4007 or 2941 or 5066:
                {
                    int dx = pos.X - player.X, dy = pos.Y - player.Y;
                    double len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
                    var to = ((int)(pos.X + dx / len * 16), (int)(pos.Y + dy / len * 16));
                    await Teleport(conn, to, now);
                    busyUntil = Math.Max(busyUntil, now) + 0.6;
                    return;
                }
                // Piety's Lightning Warp and Malachai's teleport land them on the player: a reposition, no hit of
                // their own (the slam that follows is the skill's own cast).
                case 1278 or 5017:
                    await Teleport(conn, conn.Layout.Near((player.X + 2, player.Y + 2)), now);
                    busyUntil = Math.Max(busyUntil, now) + 0.5;
                    return;
                // The Weaver hides and comes back somewhere else (its burrow: spider_boss_hide).
                case 2713:
                    await Teleport(conn, conn.Layout.Near((player.X - 10, player.Y - 10)), now);
                    burrowed = true;
                    busyUntil = Math.Max(busyUntil, now) + 1.2;
                    return;
                default:
                    // Everything that is not the plain swing is a slam, a hook or a projectile row: it lands in its
                    // skill's own reach rather than on the one player it faced.
                    SwingWith(conn, attack, effect, area: effect != BasicEffect(variety), radius: attack.Range);
                    return;
            }
        }

        /// <summary>Raises one of the boss's monsters where the cast put it. It is a summon of this fight: the
        /// instance's own monster (not a player's minion), owned through <see cref="SpawnedMonster.SummonerId"/> so a
        /// departed boss takes it with him, and it drops nothing.</summary>
        async Task<uint> Add(ClientState conn, string varietyId, (int X, int Y) at)
        {
            if (Monsters.Find(varietyId) is not { Supported: true } variety) return 0;
            at = conn.Layout.Near(at);
            var (remote, stream, ciphers) = conn.Wire;
            uint raised;
            try
            {
                raised = await SpawnMonster(remote, stream, ciphers, conn, variety, boss.Level, MonsterRarity.Normal,
                    at.X, at.Y, owned: (_, m) => { m.SummonerId = id; m.NoRewards = true; });
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
                return 0;
            }
            if (raised != 0) { lock (conn.Monsters) adds.Add(raised); }
            return raised;
        }

        bool Alive(ClientState conn, uint monster)
        {
            lock (conn.Monsters) return conn.Monsters.TryGetValue(monster, out var m) && !m.Dead && m.Life > 0;
        }

        (int X, int Y)? Position(ClientState conn, uint monster)
        {
            lock (conn.Monsters)
                return conn.Monsters.TryGetValue(monster, out var m)
                    ? m.Walk.Position(conn.Clock.Elapsed.TotalSeconds) : null;
        }

        int AliveAdds(ClientState conn)
        {
            lock (conn.Monsters) return adds.Count(a => Alive(conn, a));
        }

        /// <summary>Takes one of the boss's monsters out of the world the way the campaign's controllers do
        /// (DoedreSewers.ClearHazards): dead server-side, then removed for every player.</summary>
        async Task Retire(ClientState conn, uint monster)
        {
            lock (conn.Monsters)
            {
                adds.Remove(monster);
                if (conn.Monsters.TryGetValue(monster, out var m)) { m.Dead = true; m.Life = 0; m.ActionGeneration++; }
                conn.Monsters.Remove(monster);
                conn.Instance.World.MonsterOrigins.TryRemove(monster, out _);
            }
            await All(InstanceProtocol.RemoveObject(monster));
        }

        /// <summary>Every monster this fight raised goes with it: the instance is unloaded or the boss died.</summary>
        internal async Task RetireAdds(ClientState conn)
        {
            if (totem != 0) { totem = 0; await TotemShield(conn, false); }
            foreach (uint add in adds.ToArray()) await Retire(conn, add);
            pending.Clear();
            eggs.Clear();
        }

        // The instance is being unloaded: the same, on whoever is still connected.
        internal void RetireAll()
        {
            if (players.FirstOrDefault(p => !p.Closed) is { } conn) _ = RetireAdds(conn);
        }

        /// <summary>Tukohama's immunity while his shield totem stands: row 350's buff on him, its own instance id so
        /// it stays independent of an intermission shield that is up at the same time.</summary>
        async Task TotemShield(ClientState conn, bool on)
        {
            if (on)
            {
                lock (conn.Monsters) totemBuff = conn.NextBuff++;
                await All(InstanceEntities.AddBuff(id, totemBuff, ImmunityRow, id), monster: true);
            }
            else if (totemBuff != 0)
            {
                await All(InstanceEntities.RemoveBuff(id, totemBuff), monster: true);
                totemBuff = 0;
            }
        }
    }
}
