using System.Text.Json;
using GameServer.Core;

/// <summary>Player summon identities, independent of the golem limit and golem revival policy.</summary>
public sealed class SummonCatalog
{
    readonly string folder;

    public SummonCatalog(string folder = "data")
    {
        this.folder = folder;
        all = Load(folder);
        byEffect = LoadByEffect(all);
    }

    public sealed record Definition(string Id, string Family, string Variety, uint LimitStat,
        uint CountStat, bool Persistent = false, bool Corpse = false, bool Follows = true, bool Targetable = true)
    {
        public int LimitWithoutStat { get; init; } = 1;
        /// <summary>The limit without its base_ prefix (the final stat the panel shows), 0 when none.</summary>
        public uint FinalLimitStat => LimitStat == 0 ? 0 : Stat.Of(Program.Stats.Name(LimitStat).Replace("base_", "", StringComparison.Ordinal));
    }
    readonly IReadOnlyDictionary<string, Definition> all;
    public IReadOnlyCollection<Definition> All => all.Values.ToArray();
    public Definition? Find(string? effect) => effect is null ? null : all.GetValueOrDefault(effect);

    /// <summary>The definition of the player skill a GrantedEffects row shares its ActiveSkills row with (a monster's
    /// ExileSummonSkeletons is ActiveSkill summon_skeletons, as the gem's SummonSkeletons is): the base gem's, not an
    /// AltX/AltY's, whose minion is a choice of the gem's. Null for a skill no player summon uses.</summary>
    public Definition? ForActiveSkill(int effectRow) => byEffect.GetValueOrDefault(effectRow);

    readonly Dictionary<int, Definition> byEffect;

    /// <summary>Every GrantedEffects row whose ActiveSkills row a base player summon has, to that summon (the first in
    /// row order).</summary>
    static Dictionary<int, Definition> LoadByEffect(IReadOnlyDictionary<string, Definition> all)
    {
        var result = new Dictionary<int, Definition>();
        if (ClientTables.TableOrNull("GrantedEffects") is not { } effects) return result;
        var bySkill = new Dictionary<int, Definition>();
        foreach (var e in effects)
            if (e.IntOrNull("ActiveSkill") is int skill && e.Text("Id") is { } id && all.TryGetValue(id, out var d)
                && !id.EndsWith("AltX", StringComparison.Ordinal) && !id.EndsWith("AltY", StringComparison.Ordinal))
                bySkill.TryAdd(skill, d);
        foreach (var e in effects)
            if (e.IntOrNull("ActiveSkill") is int skill && bySkill.TryGetValue(skill, out var d)) result[e.Index] = d;
        return result;
    }

    static IReadOnlyDictionary<string, Definition> Load(string folder)
    {
        var effects = ClientTables.Table("GrantedEffects");
        var skills = ClientTables.Table("ActiveSkills");
        var result = new Dictionary<string, Definition>(StringComparer.Ordinal);
        // Alternate minion values 0-11 select these twelve distinct player Harbinger bodies.
        string[] harbingers = ["TheArcane", "Time", "Focus", "Directions", "Storms", "Brutality"];
        var definitions = new Definition[] {
            new("RaiseZombie", "Zombie", "Metadata/Monsters/RaisedZombies/RaisedZombieStandard", Stat.BaseNumberOfZombiesAllowed, Stat.Of("active_zombie_count"), true, true),
            new("RaiseSpectre", "Spectre", "", Stat.BaseNumberOfSpectresAllowed, Stat.Of("active_spectre_count"), true, true),
            new("VaalSummonSkeletons", "VaalSkeleton", "Metadata/Monsters/RaisedSkeletons/RaisedSkeletonMelee1Army", Stat.BaseNumberOfSkeletonsAllowed, 0, Follows: false),
            new("SummonSkeletons", "Skeleton", "Metadata/Monsters/RaisedSkeletons/RaisedSkeletonStandard", Stat.BaseNumberOfSkeletonsAllowed, Stat.Of("active_skeleton_count"), Follows: false),
            new("SummonRagingSpirit", "RagingSpirit", "Metadata/Monsters/SummonedSkull/SummonedSkull", Stat.BaseNumberOfRagingSpiritsAllowed, Stat.Of("active_raging_spirit_count"), Follows: false, Targetable: false),
            new("TriggeredSupportHiveborn", "Hiveborn", "Metadata/Monsters/Breach/BreachFodderHandSpiderGraftSummoned", Stat.BaseGraftSkillTulSummonMaximumAllowedDemons, 0),
            new("SummonReaper", "Reaper", "Metadata/Monsters/SummonedReaper/SummonedReaper", Stat.BaseNumberOfReapersAllowed, Stat.Of("active_reaper_count"), true),
            new("SummonHolyRelic", "HolyRelic", "Metadata/Monsters/AnimatedItem/HolyLivingRelic", Stat.BaseNumberOfRelicsAllowed, Stat.NumberOfActiveRelics, true, Targetable: false),
            new("SummonDoedresEffigy", "DoedresEffigy", "Metadata/Monsters/Effigy/DoedresEffigySummoned", Stat.BaseNumberOfEffigiesAllowed, 0, Follows: false, Targetable: false),
            new("VaalAnimateWeapon", "VaalAnimatedWeapon", "Metadata/Monsters/AnimatedItem/AnimatedWeapon", Stat.BaseNumberOfAnimatedWeaponsAllowed, Stat.NumberOfActiveVaalAnimatedWeapons),
            new("AnimateWeapon", "AnimatedWeapon", "Metadata/Monsters/AnimatedItem/AnimatedWeapon", Stat.BaseNumberOfAnimatedWeaponsAllowed, Stat.Of("active_animated_weapon_count")),
            new("AnimateGuardiansWeapon", "AnimatedWeapon", "Metadata/Monsters/AnimatedItem/AnimatedWeapon", Stat.BaseNumberOfAnimatedWeaponsAllowed, Stat.NumberOfActiveAnimatedWeapons),
            new("AnimateGuardian", "AnimatedGuardian", "Metadata/Monsters/AnimatedItem/AnimatedArmour", 0, Stat.Of("active_animated_guardian_count"), true),
            // base_number_of_mirror_arrow_clones_allowed is not a Stats row; the real cap stat is
            // base_number_of_clones_allowed (Stats 8217).
            new("BlinkArrow", "Clone", "Metadata/Monsters/Clone/MarauderCloneImmobile", Stat.BaseNumberOfClonesAllowed, 0, Follows: false),
            new("MirrorArrow", "Clone", "Metadata/Monsters/Clone/MarauderCloneImmobile", Stat.BaseNumberOfClonesAllowed, 0, Follows: false),
        };
        foreach (var row in effects)
        {
            string id = row.Text("Id")!;
            var template = definitions.FirstOrDefault(d => id == d.Id || id == d.Id + "AltX" || id == d.Id + "AltY"
                || id == "BlinkArrowRoyale" && d.Id == "BlinkArrow");
            if (id.StartsWith("SummonHarbingerOf", StringComparison.Ordinal))
            {
                bool greater = id.EndsWith("Uber", StringComparison.Ordinal);
                string kind = id["SummonHarbingerOf".Length..(greater ? ^4 : ^0)];
                int index = Array.IndexOf(harbingers, kind);
                if (index >= 0)
                {
                    int variety = index + (greater ? harbingers.Length : 0) + 1;
                    string suffix = variety == 9 ? "_" : "";
                    template = new(id, "ItemHarbinger", $"Metadata/Monsters/Harbinger/HarbingerMinion{variety}{suffix}", 0, 0, Targetable: false);
                }
            }
            if (template is null || row.IntOrNull("ActiveSkill") is not int key) continue;
            string name = skills[key].Text("DisplayedName") ?? "";
            if (name.StartsWith("[", StringComparison.Ordinal)) continue;
            var definition = template with { Id = id };
            if (id == "SummonSkeletonsAltX") definition = definition with { Variety = "Metadata/Monsters/RaisedSkeletons/RaisedSkeletonRanged1Quality" };
            if (id == "SummonSkeletonsAltY") definition = definition with { Variety = "Metadata/Monsters/RaisedSkeletons/RaisedSkeletonSpellcaster1Jewel" };
            if (id == "RaiseSpectreAltX") definition = definition with { Persistent = false };
            if (id == "RaiseZombieAltY") definition = definition with { Persistent = false, Corpse = false };
            if (template.Family == "Clone" && id.EndsWith("AltX", StringComparison.Ordinal)) definition = definition with { LimitStat = Stat.BaseNumberOfBlinkMirrorArrowRainOfArrowsClones };
            if (template.Family == "Clone" && id.EndsWith("AltY", StringComparison.Ordinal)) definition = definition with { LimitStat = Stat.BaseNumberOfBlinkMirrorArrowElementalHitClones };
            result.Add(id, definition);
        }
        return result;
    }
}

public sealed class SummonedMinions
{
    public readonly Dictionary<uint, GolemState.Golem> Fighters = [];
    public readonly Dictionary<uint, double> Until = [];
    public readonly Dictionary<uint, SummonCatalog.Definition> Definitions = [];
    public readonly Dictionary<uint, GameServer.Core.Protocol.Instance.Item> Weapons = [];
    public sealed record Offering(string Effect, double Until, ushort Buff, ushort Source,
        Dictionary<uint, int> Stats, Dictionary<uint, int> BaseStats, int EffectIncrease);
    public readonly List<Offering> Offerings = [];
    public Dictionary<uint, int> PlayerOfferingStats = [];
    public readonly Dictionary<ushort, (Offering Offering, ushort Instance)> PlayerOfferingBuffs = [];
    public long OfferingRevision;
    public double RefreshAt;
    public bool Restored;
    public bool LostPersistentOnDeath;
    public bool SpectreBankLoaded;
    public readonly List<string> SpectreBank = [];
    public double HolyRegenerationUntil;
    public int HolyRegenerationPerMinute;
    public ushort HolyBuff;
    public double FeedingFrenzyUntil;
    public sealed record Prey(uint Id, SpawnedMonster Monster, ushort Source, double Until, ushort Buff);
    public Prey? SignalledPrey;
    public readonly Queue<uint> Corpses = [];
    public sealed record HitMark(SpawnedMonster Monster, ushort Source, int Effect,
        GameServer.Core.Protocol.Instance.Item? Gem, double Until)
    {
        public bool PlayerConsumesCorpse { get; init; } = true;
    }
    public readonly Dictionary<(uint Id, ushort Source), HitMark> HitMarks = [];
    public sealed record HitSummon(uint Target, SpawnedMonster Monster, ushort Source, int Effect,
        GameServer.Core.Protocol.Instance.Item? Gem, bool ConsumeCorpse, string Kind = "", string Supports = "")
    {
        public GameServer.Core.Protocol.Instance.Item? Weapon { get; init; }
        public WornGrantedSkills.Grant? WornGrant { get; init; }
        public bool PlayerConsumesCorpse { get; init; } = true;
    }
    public readonly List<HitSummon> HitSummons = [];
    public readonly Dictionary<uint, GameServer.Core.Protocol.Instance.MinionOwnershipChange> PresentedOwnership = [];
    public Dictionary<byte, GameServer.Core.Protocol.Instance.Item> GuardianEquipment = [];
    public sealed record Saved(string Effect, ushort Source, string Variety, double Life,
        Dictionary<byte, GameServer.Core.Protocol.Instance.Item>? Equipment = null,
        GolemState.MinionResourceSnapshot? Resources = null, uint? Host = null, int? Socket = null);
}

public sealed partial class Player
{
    SummonedMinions? summons;
    public SummonedMinions Summons => System.Threading.LazyInitializer.EnsureInitialized(ref summons);
    MonsterPlacedObjects? monsterPlaced;
    public MonsterPlacedObjects MonsterPlaced => System.Threading.LazyInitializer.EnsureInitialized(ref monsterPlaced);
}
