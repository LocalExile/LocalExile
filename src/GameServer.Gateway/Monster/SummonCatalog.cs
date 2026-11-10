using System.Text.Json;

/// <summary>Player summon identities, independent of the golem limit and golem revival policy.</summary>
public sealed class SummonCatalog(string folder = "data")
{
    public sealed record Definition(string Id, string Family, string Variety, string LimitStat,
        string CountStat, bool Persistent = false, bool Corpse = false, bool Follows = true, bool Targetable = true);
    readonly Lazy<IReadOnlyDictionary<string, Definition>> all = new(() => Load(folder));
    public IReadOnlyCollection<Definition> All => all.Value.Values.ToArray();
    public Definition? Find(string? effect) => effect is null ? null : all.Value.GetValueOrDefault(effect);

    static IReadOnlyDictionary<string, Definition> Load(string folder)
    {
        using var effects = GameTables.Open(Path.Combine(folder, "tables", "GrantedEffects.json"));
        using var skills = GameTables.Open(Path.Combine(folder, "tables", "ActiveSkills.json"));
        var result = new Dictionary<string, Definition>(StringComparer.Ordinal);
        var definitions = new Definition[] {
            new("RaiseZombie", "Zombie", "Metadata/Monsters/RaisedZombies/RaisedZombieStandard", "base_number_of_zombies_allowed", "active_zombie_count", true, true),
            new("RaiseSpectre", "Spectre", "", "base_number_of_spectres_allowed", "active_spectre_count", true, true),
            new("SummonSkeletons", "Skeleton", "Metadata/Monsters/RaisedSkeletons/RaisedSkeletonStandard", "base_number_of_skeletons_allowed", "active_skeleton_count", Follows: false),
            new("SummonRagingSpirit", "RagingSpirit", "Metadata/Monsters/SummonedSkull/SummonedSkull", "base_number_of_raging_spirits_allowed", "active_raging_spirit_count", Targetable: false),
            new("SummonReaper", "Reaper", "Metadata/Monsters/SummonedReaper/SummonedReaper", "base_number_of_reapers_allowed", "active_reaper_count", true),
            new("SummonHolyRelic", "HolyRelic", "Metadata/Monsters/AnimatedItem/HolyLivingRelic", "base_number_of_relics_allowed", "number_of_active_relics", true, Targetable: false),
            new("AnimateWeapon", "AnimatedWeapon", "Metadata/Monsters/AnimatedItem/AnimatedWeapon", "base_number_of_animated_weapons_allowed", "active_animated_weapon_count"),
            new("AnimateGuardian", "AnimatedGuardian", "Metadata/Monsters/AnimatedItem/AnimatedArmour", "", "active_animated_guardian_count", true),
            new("BlinkArrow", "Clone", "Metadata/Monsters/Clone/MarauderCloneImmobile", "base_number_of_mirror_arrow_clones_allowed", "", Follows: false),
            new("MirrorArrow", "Clone", "Metadata/Monsters/Clone/MarauderCloneImmobile", "base_number_of_mirror_arrow_clones_allowed", "", Follows: false),
        };
        foreach (var row in effects.RootElement.EnumerateArray())
        {
            string id = row.GetProperty("Id").GetString()!;
            var template = definitions.FirstOrDefault(d => id == d.Id || id == d.Id + "AltX" || id == d.Id + "AltY");
            if (template is null || row.GetProperty("ActiveSkill") is not { ValueKind: JsonValueKind.Number } key) continue;
            string name = skills.RootElement[key.GetInt32()].GetProperty("DisplayedName").GetString() ?? "";
            if (name.StartsWith("[", StringComparison.Ordinal)) continue;
            var definition = template with { Id = id };
            if (id == "SummonSkeletonsAltX") definition = definition with { Variety = "Metadata/Monsters/RaisedSkeletons/RaisedSkeletonRanged1Quality" };
            if (id == "SummonSkeletonsAltY") definition = definition with { Variety = "Metadata/Monsters/RaisedSkeletons/RaisedSkeletonSpellcaster1Jewel" };
            if (id == "RaiseSpectreAltX") definition = definition with { Persistent = false };
            if (id == "RaiseZombieAltY") definition = definition with { Persistent = false, Corpse = false };
            if (template.Family == "Clone" && id.EndsWith("AltX", StringComparison.Ordinal)) definition = definition with { LimitStat = "base_number_of_blink_mirror_arrow_rain_of_arrows_clones" };
            if (template.Family == "Clone" && id.EndsWith("AltY", StringComparison.Ordinal)) definition = definition with { LimitStat = "base_number_of_blink_mirror_arrow_elemental_hit_clones" };
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
    public double RefreshAt;
    public bool Restored;
    public double HolyRegenerationUntil;
    public int HolyRegenerationPerMinute;
    public ushort HolyBuff;
    public readonly Queue<uint> Corpses = [];
    public Dictionary<byte, GameServer.Core.Protocol.Instance.Item> GuardianEquipment = [];
    public sealed record Saved(string Effect, ushort Source, string Variety, double Life, Dictionary<byte, GameServer.Core.Protocol.Instance.Item>? Equipment = null, uint? Host = null, int? Socket = null);
}

public sealed partial class ClientState
{
    SummonedMinions? summons;
    public SummonedMinions Summons => System.Threading.LazyInitializer.EnsureInitialized(ref summons);
}
