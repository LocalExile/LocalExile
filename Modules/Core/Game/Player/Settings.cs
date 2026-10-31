using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using WalkEstimator = GameServer.Core.World.WalkEstimator;
using PlayerWalk = GameServer.Core.World.PlayerWalk;
using ClientPathfinder = GameServer.Core.World.ClientPathfinder;
using Progression = GameServer.Core.World.Progression;
using DamageModel = GameServer.Core.World.DamageModel;
using DamageType = GameServer.Core.World.DamageType;
using SaveFiles = GameServer.Core.Saves.SaveFiles;
using DamagePacket = GameServer.Core.World.DamagePacket;
using DamageRange = GameServer.Core.World.DamageRange;
using DamageOverTime = GameServer.Core.World.DamageOverTime;
using GameServer.Core.Diagnostics;

static partial class Game
{
    // Game_LEAGUE=hardcore treats every character as hardcore; otherwise the character's league decides (Auth's resolve).
    internal static readonly bool ForceHardcore = Environment.GetEnvironmentVariable("Game_LEAGUE")?.ToLowerInvariant() == "hardcore";
    // S→C 0x55 flags (11-skills.md §8): standard = bit2 (Resurrect at Checkpoint) + bit3 (Resurrect in Town); hardcore =
    // bit0 (a single Exit button, "Exit to the character selection screen", which sends C→S 0x153). Before the character
    // has a town (The Twilight Strand) only bit2, as in the real game.
    internal static byte DeathScreenFlags(ClientState conn) => ForceHardcore || conn.Hardcore ? (byte)0x01 : conn.LastTown == 0 ? (byte)0x04 : (byte)0x0c;
    // PoB's "recently": the last 4 seconds.
    internal const double RecentlySeconds = 4;
    // Shown once per connection when the client moves in lockstep (InstanceProtocol.IsLockstepMovement).
    internal const string LockstepWarning = "Your Network Mode is lockstep, which this server does not support yet: your character stays " +
        "hidden and cannot move. Set Options > Game > Network Mode to Predictive, then log out to the character screen and Play again.";
    // The Positioned size (object_size − 1) of area transitions and waypoints (AreaTransition.ot, Waypoint.ot
    // object_size 6): the player walks up to them as the client does (PlayerWalk.WalkUpTo).
    internal const int AreaTransitionObjectSize = 5, WaypointObjectSize = 5;
    // How close a waypoint activates on its own.
    internal const double WaypointActivationRange = 25;
    // The affinity (StashTabAffinityId row 0, "UNUSED0": no item has it) that marks the account's last tab in the tab
    // list, so that S->C 0x7b kind 1 with it opens the stash on that tab.
    internal const uint StashSelectAffinity = 0;
    // How long (s) after the first open's 0x7b the panel's requests belong to that open (the client sends one 0xc7 per
    // 200 ms, at most two then).
    internal const double StashForcedWindow = 1.0;
    // The height u32 of S→C 0x1ba (+0x28, passed with the move to Positioned vtable +0xd8) and 0x1b7 (→ +0x290): the
    // fixed height, 0x7fffffff = none, follow the terrain. 0 pinned a moving projectile under the ground (live 2026-10-18).
    internal const uint TerrainHeight = 0x7fffffff;
    // How close (cells, centre to centre) the player must be to pick an item up from the ground: WorldItem.ot's
    // InteractionAction interaction_distance_override = 20 grid cells, plus the radii of the item and the player
    // (object_size 2 each). Measured live 2026-10-18 (Positioned +0x294/+0x298): the client walked from (1301, 1250)
    // toward an item at (1278, 1304) and stopped at (1284, 1284), 20.9 cells away, on the first step inside the range.
    internal const double PickupReach = 20 + 2;
    // How close (cells, centre to centre) gold on the ground is picked up by itself (RunPlayerTick): our own number, a
    // little inside a click's PickupReach.
    // A monster's second skill, after its attack (SkillCatalog.FirstSkillId): a boss's stage skill (BossFights). 0x0103
    // is free in 3.29.3b (SkillCatalog.SkillIds).
    internal const ushort StageSkillId = 0x0103;
    // NPCs.dat with the NPC types' components; loaded with the first town.
    internal static readonly Lazy<NpcCatalog> Npcs = new(() => NpcCatalog.Load());
    // Shared by every connection; loaded on the first /spawn_mob.
    internal static readonly MonsterCatalog Monsters = new();
    // MonsterPacks per area (MonsterPacks.cs); loaded with the first area that has monsters.
    internal static readonly PackCatalog Packs = new();
    // The shop's portal effects the server can apply itself (/cosmetic): the visual of the player's own portals.
    internal static readonly CosmeticCatalog Cosmetics = new();
    // Every MtxTypes row as an owned microtransaction (23-microtransactions.md); loaded with the first 0x1f2.
    internal static readonly MtxCatalog Mtx = new();
    // What worn cosmetics do on the model and in the stats (ItemisedVisualEffect); loaded with the first equipment sent.
    internal static readonly CosmeticEffects MtxEffects = new();
    // What a worn skill effect's skill_art_variation draws (MiscAnimated and BuffVisuals art variations).
    internal static readonly SkillArt Art = new();
    // The Cosmetics tab's Portrait slot (MicrotransactionSlot 16).
    internal const byte PortraitSlot = 16;
    // The Cosmetics tab's Social Frame slot (MicrotransactionSlot 17): the portrait frames.
    internal const byte SocialFrameSlot = 17;
    // Per character: the ids of worn items whose requirements are not met (CharacterStatSources).
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HashSet<uint>> InactiveItems = new(StringComparer.OrdinalIgnoreCase);
    // The character panel's Spell Suppression chance stats (the client reads the final value).
    internal static readonly uint[] SuppressionChanceKeys = [.. new[] { "spell_suppression_chance_%",
        "base_spell_suppression_chance_%", "combined_spell_suppression_chance_%" }.Select(n => Stats.Key(n)).OfType<uint>()];
    // Per character: the skill ids of the socketed gems (GemSkillIds), and the ids gem skills may use (after the default
    // attack, the ids below FirstSkillId + 64 that no basic action uses: SkillCatalog.IsGemSkill; 55 in 3.29.3b).
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, GemSkillIds> GemSkillIdsOf = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Lazy<ushort[]> GemSkillIdPool = new(() => [.. Skills.SkillIds().Skip(1).Where(Skills.IsGemSkill)]);
    // Maps.dat: which area each map opens (MapDevice.cs); loaded with the first Activate.
    internal static readonly Lazy<MapDevice> MapDevices = new(() => MapDevice.Load("data", row => Items.Row(row)?.Hash, Items));
    // The map device's portals are MultiplexPortals of the player type (1): the map device type (3) is only drawn after an
    // open transition, which hung or crashed the client (21-hideouts.md §5). Game_MAP_PORTAL_TYPE overrides it.
    internal static readonly byte MapPortalType = byte.TryParse(Environment.GetEnvironmentVariable("Game_MAP_PORTAL_TYPE"), out byte t) ? t : MapPortalObject.PlayerPortal;
    // Map portal timing (ms): each portal's 0x8f after its create, the gap between portals, the first one after entering
    // the hideout (Game_MAP_PORTAL_LIST_MS, _GAP_MS, _ENTRY_MS).
    internal static readonly int MapPortalListMs = EnvMs("Game_MAP_PORTAL_LIST_MS", 100), MapPortalGapMs = EnvMs("Game_MAP_PORTAL_GAP_MS", 150),
        MapPortalEntryMs = EnvMs("Game_MAP_PORTAL_ENTRY_MS", 1500);
    internal static int EnvMs(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out int ms) && ms >= 0 ? ms : fallback;
    // Unique items the server can create (names/art from the client data, mods curated: UniqueCatalog).
    internal static readonly UniqueCatalog Uniques = new(Items);
    internal static readonly EssenceCrafting Essences = new(Items, Rolls);
    internal static readonly ItemDrops Drops = new(Items, Rolls, maps: MapDevices.Value.Atlas, uniques: Uniques);
    // The Atlas passive tree's atlas_* nodes (PassiveSkills.dat), used by the three S→C 0x3d presets.
    internal static readonly AtlasPassives AtlasPassiveNodes = new();
    internal static readonly TimelessJewelEngine TimelessJewels = new();
    internal static readonly OrdinaryPassiveRules OrdinaryPassives = new();
    internal static readonly AscendancyCatalog Ascendancies = new();
    internal static readonly AscendancyRules AscendancyRules = new(Ascendancies);
    // How close (cells) the player must be to a dying monster to gain its experience. Game_XP_RANGE overrides it.
    internal const int GodRegenPerSecond = 10_000;
    // /god regenerates 100% of maximum life per second (more than Righteous Fire's 90% burn).
    internal const int GodLifeRegenPctPerSecond = 100;
    // S→C 0x1b1's pool byte for mana (0 life, 1 mana, 2 energy shield).
    internal const byte ManaPool = 1;
    // When a spell's projectile leaves (the class's spell animation's contact keyframe).
    internal static readonly AnimationContacts Animations = new();
    // BuffDefinitions rows: righteous_fire (on the caster) and righteous_fire_aura (on the enemies it burns).
    internal const ushort RighteousFireBuffRow = 12, RighteousFireAuraBuffRow = 13;
    // The flask buffs' BuffDefinitions rows (flask_effect_life "Healing", flask_effect_mana "Regenerating Mana": invisible,
    // ended by death or a full pool). Game_FLASK_BUFFS=0 recovers without them.
    internal const ushort FlaskEffectLifeBuffRow = 5, FlaskEffectManaBuffRow = 9;
    /// <summary>The quest books that give a passive skill point (BaseItemTypes Name; several Ids, one per quest).</summary>
    internal const string SkillBook = "Book of Skill";
    /// <summary>The quest books that give two passive refund points (BaseItemTypes Name; Acts 1, 6, 9 and 10).</summary>
    internal const string RegretBook = "Book of Regrets";
    /// <summary>Continuous pool recovery's target cadence; amounts use elapsed game time, not this nominal delay.</summary>
    internal const int RecoveryStepMs = 16;
    internal static bool FlaskBuffs => Environment.GetEnvironmentVariable("Game_FLASK_BUFFS") != "0";
    // MiscAnimated RighteousFireRing: the big burning circle around a Righteous Fire caster.
    internal const string RighteousFireRingAo = "Metadata/Effects/Spells/ri_fire/rifire.ao";
    // The projectiles' explosion (Fireball's, MiscAnimatedArtVariations fireball_standard variant 0).
    internal const string DefaultProjectileImpactAo = "Metadata/Effects/Spells/fireball/explode_new2.ao";
    // Secondary KB explosion: kinetic_blast_small, distinct from the unscaled main-impact kinetic_blast_large art.
    internal const string KineticBlastExplosionAo = "Metadata/Effects/Spells/cluster_burst/mini.ao";
    // The player's own tick (RunPlayerTick): regeneration and damage over time.
    internal const int PlayerTickMs = 250;
    internal static readonly (string Stat, int Value)[] GodStats =
    [
        ("attack_speed_+%", 1000), ("base_cast_speed_+%", 1000), ("cast_speed_+%", 1000),
        ("life_regeneration_rate_per_minute_%", GodLifeRegenPctPerSecond * 60),
        ("base_mana_regeneration_rate_per_minute", GodRegenPerSecond * 60), ("mana_regeneration_rate_per_minute", GodRegenPerSecond * 60),
        ("base_skill_area_of_effect_+%", 200),
    ];

    internal static readonly double ExperienceRange =
        double.TryParse(Environment.GetEnvironmentVariable("Game_XP_RANGE"), System.Globalization.CultureInfo.InvariantCulture, out var xr) ? xr : 150;
}
