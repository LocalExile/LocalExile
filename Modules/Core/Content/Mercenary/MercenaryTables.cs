using GameServer.Core;

namespace GameServer.Modules.Mercenary;

/// <summary>
/// Client table data for Mercenaries of Trarthus: classes, builds, skills, supports, extra stats curve,
/// name words (Wordlist 13/14), flavour text, wieldable item classes, and disabled uniques. Built once when the module loads.
/// </summary>
public static class MercenaryTables
{
    /// <summary>One MercenaryClasses row. <paramref name="Attribute"/> is the Id of its MercenaryAttributes row (Str, Dex, Int,
    /// StrDex, StrInt, DexInt, StrDexInt), which decides what its house wears.</summary>
    public sealed record Class(int Row, string Id, string Path, string AlliedPath, string House, string Attribute, bool Supported)
    {
        public IReadOnlySet<string> AttributeTagIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);
        public int AttributeRow { get; init; } = -1;
    }

    /// <summary>One MercenaryBuilds row: the build a mercenary carries, its name, and the HASH16 the create names it by.</summary>
    public sealed record Build(int Row, string Id, int ClassRow, string Name, ushort Hash16, bool Infamous)
    {
        /// <summary><c>MercenaryBuilds.__col14</c>: 0 = no offhand (2H), 1/2 = any wieldable offhand, 3 = Shield/Quiver only.</summary>
        public int OffhandMode { get; init; }
        public int[] Tags { get; init; } = [];
        public int[] ExtraStats { get; init; } = [];
        public int[] WieldableItemClasses { get; init; } = [];
    }

    /// <summary>One MercenarySkills row: the effect the skill is, the one it is in the encounter where the row names one, and
    /// the level a mercenary must reach to use it.</summary>
    public sealed record SkillRow(int GrantedEffect, int EncounterGrantedEffect, int RequiredLevel)
    {
        public int Row { get; init; }
        public ushort Hash16 { get; init; }
        /// <summary><c>MercenarySupportCounts</c> row: 0 = Low, 1 = Medium, 2 = High, 3 = None.</summary>
        public int SupportCountMode { get; init; } = 3;
        public int[] PossibleSupports { get; init; } = [];
        public int? SkillFamily { get; init; }
        public string Name { get; init; } = "";
    }

    /// <summary>One MercenarySupports row: support HASH16 and stat bonuses applied to its linked skill.</summary>
    public sealed record SupportRow(int Row, string Id, ushort Hash16, uint[] StatKeys, int[] StatValues, int? SupportFamily, int Tier);

    /// <summary>One MercenaryBuildExtraStats row: 1-based stat key and piecewise-linear anchor values at levels 24 / 68 / 84.</summary>
    public sealed record ExtraStatRow(int Row, string Id, uint StatKey, int Level24, int Level68, int Level84, int Category);

    /// <summary>One MercenaryFlavourText row.</summary>
    public sealed record FlavourRow(int Row, string Id, ushort Hash16, int[] Tags, int[] Weights);

    /// <summary>One Words row from Wordlist 13 (mercenary first name) or 14 (mercenary epithet).</summary>
    public sealed record WordRow(int Row, int Wordlist, uint Hash32, string Text, int[] Tags, int[] Weights);

    /// <summary>One rolled skill + supports on a mercenary instance (serializable into save / instance state).</summary>
    public sealed record RolledSkill(int SkillRow, ushort SkillHash16, List<int> SupportRows, List<ushort> SupportHashes16);

    /// <summary>A rolled mercenary's wire-valid identity (all HASH16/HASH32 verified non-zero).</summary>
    public sealed record RolledIdentity(uint FirstNameHash32, uint SecondNameHash32, ushort FlavourHash16, List<RolledSkill> Skills);

    /// <summary>What a MercenaryBuilds row lists as its skills (MercenarySkills rows): all of Skills1, <c>Skills2Count</c> of
    /// Skills2 and <c>Skills3Count</c> of Skills3.</summary>
    sealed record BuildSkills(int[] Skills1, int[] Skills2, int Skills2Count, int[] Skills3, int Skills3Count);

    /// <summary>Cached WorldAreas metadata for town/hideout/act checks.</summary>
    public readonly record struct AreaMeta(string Id, int Act, int AreaLevel, bool IsTown, bool IsHideout);

    static IReadOnlyList<Class> ClassesData = [];
    static IReadOnlyList<Build> BuildsData = [];
    static IReadOnlyDictionary<int, SkillRow> SkillRowsData = new Dictionary<int, SkillRow>();
    static IReadOnlyDictionary<int, BuildSkills> BuildSkillsData = new Dictionary<int, BuildSkills>();
    static IReadOnlyDictionary<int, SupportRow> SupportRowsData = new Dictionary<int, SupportRow>();
    static IReadOnlyDictionary<int, ExtraStatRow> ExtraStatsData = new Dictionary<int, ExtraStatRow>();
    static List<FlavourRow> FlavoursData = [];
    static List<WordRow> FirstNamesData = [];
    static List<WordRow> SecondNamesData = [];
    static Dictionary<uint, string> WordsByHashData = [];
    static HashSet<string> DisabledUniquesData = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, AreaMeta> AreasById = new(StringComparer.OrdinalIgnoreCase);
    static IReadOnlyDictionary<int, IReadOnlyList<(int Dx, int Dy)>> PartyOffsetsData =
        new Dictionary<int, IReadOnlyList<(int Dx, int Dy)>>();

    public static AreaMeta? AreaOf(string areaId) => AreasById.TryGetValue(areaId, out var meta) ? meta : null;
    public static bool IsTownOrHideout(string areaId) => AreaOf(areaId) is { } m && (m.IsTown || m.IsHideout);

    /// <summary>The Rulesets rows of MercenaryEncounterRules (161) and MercenaryAreaRules (162), looked up by Id: a copy without
    /// the client rows then lists neither instead of a wrong row.</summary>
    public static byte? EncounterRulesRow { get; private set; }
    public static byte? AreaRulesRow { get; private set; }

    /// <summary>The GrantedEffects row of the plain melee hit (MeleeAtAnimationSpeed): the swing a duel opponent falls back
    /// to when its build yields no direct attack.</summary>
    public static int MeleeFallbackEffect { get; private set; } = -1;

    /// <summary>BaseItemTypes HASH32 of <c>Metadata/Items/MapFragments/MercenaryWarrant</c>.</summary>
    public static uint WarrantTypeHash { get; private set; }

    /// <summary>ItemClasses row indices for Shield (26) and Quiver (20), cached for offhand mode 3 validation.</summary>
    public static int ShieldClassRow { get; private set; } = 26;
    public static int QuiverClassRow { get; private set; } = 20;

    public static IReadOnlyList<Class> Classes => ClassesData;
    public static IReadOnlyList<Build> Builds => BuildsData;

    /// <summary>Builds this module's game data at startup (before the Gateway listens), not on first use.</summary>
    public static void LoadGameData()
    {
        var tagNames = LoadTagNames();
        ClassesData = LoadClasses(tagNames);
        BuildsData = LoadBuilds();
        (SkillRowsData, BuildSkillsData) = LoadSkills();
        SupportRowsData = LoadSupports();
        ExtraStatsData = LoadExtraStats();
        FlavoursData = LoadFlavours();
        (FirstNamesData, SecondNamesData, WordsByHashData) = LoadWords();
        DisabledUniquesData = LoadDisabledUniques();
        EncounterRulesRow = RulesetRow("MercenaryEncounterRules");
        AreaRulesRow = RulesetRow("MercenaryAreaRules");
        MeleeFallbackEffect = ClientTables.TableOrEmpty("GrantedEffects")
            .Where(r => r.TextOrNull("Id") == "MeleeAtAnimationSpeed").Select(r => r.Index).DefaultIfEmpty(-1).First();
        WarrantTypeHash = Program.Items.Find("Metadata/Items/MapFragments/MercenaryWarrant")?.Hash ?? 0;
        AreasById = LoadAreas();
        PartyOffsetsData = LoadPartyOffsets();
        var itemClasses = ClientTables.TableOrEmpty("ItemClasses");
        foreach (var r in itemClasses)
        {
            string? clsId = r.TextOrNull("Id");
            if (string.Equals(clsId, "Shield", StringComparison.OrdinalIgnoreCase)) ShieldClassRow = r.Index;
            else if (string.Equals(clsId, "Quiver", StringComparison.OrdinalIgnoreCase)) QuiverClassRow = r.Index;
        }
    }

    static byte? RulesetRow(string id)
    {
        foreach (var r in ClientTables.TableOrEmpty("Rulesets"))
            if (string.Equals(r.TextOrNull("Id"), id, StringComparison.Ordinal) && r.Index is >= 0 and < 256)
                return (byte)r.Index;
        return null;
    }

    /// <summary>The classes this server can place: a variety it can serialise (the Mercenary component is written) and at
    /// least one build of its own.</summary>
    public static IReadOnlyList<Class> Eligible(bool infamous = false) =>
        [.. Classes.Where(c => c.Path.Length > 0 && c.Supported && BuildsOf(c.Row, infamous).Count > 0)];

    /// <summary>The builds one class draws from.</summary>
    public static IReadOnlyList<Build> BuildsOf(int classRow, bool infamous = false) =>
        [.. Builds.Where(b => b.ClassRow == classRow && b.Infamous == infamous)];

    /// <summary>One eligible class and one of its builds, drawn uniformly from <paramref name="rng"/>; null when no class is
    /// eligible.</summary>
    public static (Class Kind, Build Build)? Pick(Random rng, bool infamous = false)
    {
        var pool = Eligible(infamous);
        if (pool.Count == 0) return null;
        var kind = pool[rng.Next(pool.Count)];
        var builds = BuildsOf(kind.Row, infamous);
        return (kind, builds[rng.Next(builds.Count)]);
    }

    /// <summary>The GrantedEffects rows a mercenary of this build fights with.</summary>
    public static IReadOnlyList<int> SkillsOf(Build build, int level, Random rng, bool encounter = true)
    {
        if (!BuildSkillsData.TryGetValue(build.Row, out var listed)) return [];
        List<int> effects = [];
        void Add(int row)
        {
            if (!SkillRowsData.TryGetValue(row, out var skill) || skill.RequiredLevel > level) return;
            int effect = encounter && skill.EncounterGrantedEffect >= 0 ? skill.EncounterGrantedEffect : skill.GrantedEffect;
            if (effect >= 0 && !effects.Contains(effect)) effects.Add(effect);
        }
        foreach (int row in listed.Skills1) Add(row);
        foreach (int row in DrawRows(listed.Skills2, listed.Skills2Count, level, rng)) Add(row);
        foreach (int row in DrawRows(listed.Skills3, listed.Skills3Count, level, rng)) Add(row);
        return effects;
    }

    /// <summary>Rolls wire-valid first/second Words HASH32, Flavour HASH16, and MercenarySkills + MercenarySupports for a build.</summary>
    public static RolledIdentity RollIdentity(Build build, byte level, Random rng, int additionalSupportChancePct = 0)
    {
        uint firstWord = PickWeightedWord(FirstNamesData, build.Tags, rng);
        uint secondWord = PickWeightedWord(SecondNamesData, build.Tags, rng);
        ushort flavour = PickWeightedFlavour(build.Tags, rng);
        var skills = RollSkills(build, level, rng, additionalSupportChancePct);
        return new(firstWord, secondWord, flavour, skills);
    }

    /// <summary>Rolls a mercenary's active skills and linked supports for a build at <paramref name="level"/>.</summary>
    public static List<RolledSkill> RollSkills(Build build, int level, Random rng, int additionalSupportChancePct = 0)
    {
        if (!BuildSkillsData.TryGetValue(build.Row, out var listed)) return [];
        List<RolledSkill> rolled = [];
        HashSet<int> usedRows = [];
        HashSet<int> usedFamilies = [];

        void TryAdd(int row)
        {
            if (usedRows.Contains(row) || !SkillRowsData.TryGetValue(row, out var skill)) return;
            if (skill.RequiredLevel > level || skill.Hash16 == 0) return;
            if (skill.SkillFamily is int fam && !usedFamilies.Add(fam)) return;
            usedRows.Add(row);
            var (suppRows, suppHashes) = RollSupportsFor(skill, level, rng, additionalSupportChancePct);
            rolled.Add(new(row, skill.Hash16, suppRows, suppHashes));
        }

        foreach (int row in listed.Skills1) TryAdd(row);
        foreach (int row in ShuffleEligible(listed.Skills2, level, rng))
        {
            if (rolled.Count >= listed.Skills1.Length + listed.Skills2Count) break;
            TryAdd(row);
        }
        int before3 = rolled.Count;
        foreach (int row in ShuffleEligible(listed.Skills3, level, rng))
        {
            if (rolled.Count - before3 >= listed.Skills3Count) break;
            TryAdd(row);
        }
        // If level was below every RequiredLevel in Skills1..3, pick at least one skill from Skills1 ignoring RequiredLevel so UI isn't empty.
        if (rolled.Count == 0)
        {
            foreach (int row in listed.Skills1.Concat(listed.Skills2).Concat(listed.Skills3))
            {
                if (!SkillRowsData.TryGetValue(row, out var skill) || skill.Hash16 == 0) continue;
                var (suppRows, suppHashes) = RollSupportsFor(skill, level, rng, additionalSupportChancePct);
                rolled.Add(new(row, skill.Hash16, suppRows, suppHashes));
                if (rolled.Count >= 2) break;
            }
        }
        return rolled;
    }

    static (List<int> Rows, List<ushort> Hashes) RollSupportsFor(SkillRow skill, int level, Random rng, int additionalSupportChancePct)
    {
        if (skill.SupportCountMode == 3 || skill.PossibleSupports.Length == 0) return ([], []);
        int maxForLevel = level < 24 ? 1 : level < 45 ? 2 : level < 68 ? 3 : 4;
        int baseCount = skill.SupportCountMode switch
        {
            0 => Math.Min(maxForLevel, rng.Next(1, 3)),
            1 => Math.Min(maxForLevel, rng.Next(1, 4)),
            2 => Math.Min(maxForLevel, rng.Next(2, 5)),
            _ => 0,
        };
        if (additionalSupportChancePct > 0 && rng.Next(100) < additionalSupportChancePct)
            baseCount = Math.Min(5, baseCount + 1);
        if (baseCount <= 0) return ([], []);

        int maxTier = level < 35 ? 1 : level < 65 ? 2 : 3;
        var candidates = skill.PossibleSupports
            .Select(r => SupportRowsData.TryGetValue(r, out var s) ? s : null)
            .Where(s => s is not null && s.Hash16 != 0 && s.Tier <= maxTier)
            .Cast<SupportRow>()
            .ToArray();
        if (candidates.Length == 0)
            candidates = skill.PossibleSupports
                .Select(r => SupportRowsData.TryGetValue(r, out var s) ? s : null)
                .Where(s => s is not null && s.Hash16 != 0)
                .Cast<SupportRow>()
                .ToArray();
        if (candidates.Length == 0) return ([], []);

        for (int i = candidates.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }

        List<int> rows = [];
        List<ushort> hashes = [];
        HashSet<int> families = [];
        foreach (var supp in candidates)
        {
            if (rows.Count >= baseCount) break;
            if (supp.SupportFamily is int fam && !families.Add(fam)) continue;
            rows.Add(supp.Row);
            hashes.Add(supp.Hash16);
        }
        return (rows, hashes);
    }

    static IEnumerable<int> DrawRows(int[] rows, int count, int level, Random rng)
    {
        if (count <= 0 || rows.Length == 0) return [];
        return ShuffleEligible(rows, level, rng).Take(count);
    }

    static int[] ShuffleEligible(int[] rows, int level, Random rng)
    {
        var pool = rows.Where(r => SkillRowsData.TryGetValue(r, out var s) && s.RequiredLevel <= level).ToArray();
        for (int i = pool.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        return pool;
    }

    /// <summary>Evaluates <c>MercenaryBuildExtraStats</c> for a build using the exact client level curve (<c>0x1405a9a20</c>):
    /// <c>effectiveLevel = max(recordLevel, min(68, areaLevel))</c> with piecewise-linear interpolation over (24, 68, 84).</summary>
    public static Dictionary<uint, int> EvaluateExtraStats(Build build, int recordLevel, int areaLevel)
    {
        int effectiveLevel = Math.Max(recordLevel, Math.Min(68, areaLevel));
        Dictionary<uint, int> result = [];
        foreach (int row in build.ExtraStats)
        {
            if (!ExtraStatsData.TryGetValue(row, out var extra)) continue;
            int value = InterpolateExtraStat(extra, effectiveLevel);
            if (value != 0)
                result[extra.StatKey] = result.GetValueOrDefault(extra.StatKey) + value;
        }
        return result;
    }

    /// <summary>Exact client piecewise-linear interpolation (<c>0x1405a9a20</c>) truncated toward zero.</summary>
    public static int InterpolateExtraStat(ExtraStatRow row, int effectiveLevel)
    {
        if (effectiveLevel <= 24) return row.Level24;
        if (effectiveLevel < 68) return row.Level24 + (int)((row.Level68 - row.Level24) * ((effectiveLevel - 24) / 44.0f));
        if (effectiveLevel < 84) return row.Level68 + (int)((row.Level84 - row.Level68) * ((effectiveLevel - 68) / 16.0f));
        return row.Level84;
    }

    /// <summary>Retail-calibrated wager gold cost for a mercenary duel (deducted on duel start, refunded on win).</summary>
    public static uint WagerGoldCost(byte level, bool infamous, int wagerAmountPlusPct = 0)
    {
        int lvl = Math.Clamp((int)level, 1, 100);
        double cost = lvl <= 24 ? 20 + lvl * 3.5
            : lvl <= 68 ? 100 + (lvl - 24) * 16.0
            : 800 + Math.Pow(lvl - 68, 1.55) * 45.0;
        if (infamous) cost *= 1.5;
        cost *= Math.Max(0.0, 1.0 + wagerAmountPlusPct / 100.0);
        return (uint)Math.Max(10, Math.Round(cost / 10.0) * 10);
    }

    /// <summary>Retail-calibrated revive gold cost for a dead hired mercenary.</summary>
    public static uint ReviveGoldCost(byte level, bool infamous)
    {
        int lvl = Math.Clamp((int)level, 1, 100);
        double cost = lvl <= 24 ? 50 + lvl * 8.5
            : lvl <= 68 ? 250 + (lvl - 24) * 28.5
            : 1500 + Math.Pow(lvl - 68, 1.65) * 95.0;
        if (infamous) cost *= 1.5;
        return (uint)Math.Max(25, Math.Round(cost / 25.0) * 25);
    }

    /// <summary>Maps a mercenary aura <c>GrantedEffects</c> row to its standard <see cref="AuraCatalog"/> family id
    /// (stripping the <c>Mercenary</c> suffix) for non-curse friendly auras; null if not a shared friendly aura.</summary>
    public static string? FriendlyAuraFamily(SkillCatalog skills, int grantedEffect)
    {
        if (grantedEffect < 0 || skills.EffectId(grantedEffect) is not { } id) return null;
        if (id.Contains("Blasphemy", StringComparison.OrdinalIgnoreCase)
            || id.Contains("Enfeeble", StringComparison.OrdinalIgnoreCase)
            || id.Contains("Flammability", StringComparison.OrdinalIgnoreCase)
            || id.Contains("Pride", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith("Vaal", StringComparison.OrdinalIgnoreCase))
            return null;
        string trimmed = id.EndsWith("Mercenary", StringComparison.Ordinal) ? id[..^"Mercenary".Length] : id;
        return AuraCatalog.Shared(trimmed) ? trimmed : null;
    }

    public static bool IsDisabledUnique(string? uniqueName) =>
        !string.IsNullOrEmpty(uniqueName) && DisabledUniquesData.Contains(uniqueName);

    public static string? WordTextOf(uint hash32) =>
        hash32 != 0 && WordsByHashData.TryGetValue(hash32, out var text) ? text : null;

    /// <summary>Formats a mercenary's full display name from its Wordlist 13 first name and Wordlist 14 epithet
    /// (<c>ClientStrings.MercenaryName = "{0}{1}"</c>, e.g. <c>"Secha, the Recluse"</c>).</summary>
    public static string? FormatName(uint firstHash32, uint secondHash32)
    {
        string? first = WordTextOf(firstHash32);
        string? second = WordTextOf(secondHash32);
        if (string.IsNullOrEmpty(first)) return string.IsNullOrEmpty(second) ? null : second.TrimStart(',', ' ');
        return string.IsNullOrEmpty(second) ? first : $"{first}{second}";
    }

    public static Class? ClassOf(int row) => Classes.FirstOrDefault(c => c.Row == row);
    public static Build? BuildOf(int row) => Builds.FirstOrDefault(b => b.Row == row);
    public static Build? BuildByHash(ushort hash16) => Builds.FirstOrDefault(b => b.Hash16 == hash16);
    public static SkillRow? SkillOf(int row) => SkillRowsData.GetValueOrDefault(row);

    /// <summary>Where a class's party members stand, in cells from the Mercenary: its camp room (<c>MercenaryClasses.TerrainFeature</c>
    /// → <c>ExtraTerrainFeatures.ArmFiles</c>, Witch1.arm …) puts a MercenarySpawnMarkerPartyMember on each side of its
    /// MercenarySpawnMarker, 15 to 26 cells away. Empty without the room.</summary>
    public static IReadOnlyList<(int Dx, int Dy)> PartyOffsets(int classRow) =>
        PartyOffsetsData.TryGetValue(classRow, out var offsets) ? offsets : [];

    static IReadOnlyDictionary<int, IReadOnlyList<(int Dx, int Dy)>> LoadPartyOffsets()
    {
        if (!File.Exists(GameServer.Data.Ggpk.GamePack.DefaultFile))
            return new Dictionary<int, IReadOnlyList<(int Dx, int Dy)>>();
        const string Marker = "Metadata/Terrain/Leagues/Mercenaries/Objects/MercenarySpawnMarker";
        var armByFeature = new Dictionary<int, string>();
        foreach (var room in ClientTables.TableOrEmpty("ExtraTerrainFeatures"))
            if (room.Texts("ArmFiles").FirstOrDefault() is { } arm)
                armByFeature[room.Index] = arm;
        var offsets = new Dictionary<int, IReadOnlyList<(int Dx, int Dy)>>();
        try
        {
            var pack = GameServer.Data.Ggpk.GamePack.Default;
            foreach (var kind in ClientTables.TableOrEmpty("MercenaryClasses"))
            {
                if (kind.IntOrNull("TerrainFeature") is not int feature
                    || !armByFeature.TryGetValue(feature, out var arm)
                    || pack.TryRead(arm) is not { } bytes) continue;
                var doodads = GameServer.Data.Terrain.RoomFile.Parse(GameServer.Data.Terrain.TerrainText.Decode(bytes)).Doodads;
                if (doodads.FirstOrDefault(d => d.Type == Marker) is not { } main) continue;
                var members = doodads.Where(d => d.Type == Marker + "PartyMember")
                    .Select(d => ((int)MathF.Round(d.X - main.X), (int)MathF.Round(d.Y - main.Y))).ToArray();
                if (members.Length > 0) offsets[kind.Index] = members;
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            return new Dictionary<int, IReadOnlyList<(int Dx, int Dy)>>();
        }
        return offsets;
    }
    public static SkillRow? SkillByHash(ushort hash16) => hash16 == 0 ? null : SkillRowsData.Values.FirstOrDefault(s => s.Hash16 == hash16);
    public static SupportRow? SupportOf(int row) => SupportRowsData.GetValueOrDefault(row);
    public static SupportRow? SupportByHash(ushort hash16) => hash16 == 0 ? null : SupportRowsData.Values.FirstOrDefault(s => s.Hash16 == hash16);
    public static FlavourRow? FlavourByHash(ushort hash16) => FlavoursData.FirstOrDefault(f => f.Hash16 == hash16);

    /// <summary>Reconstructs <see cref="RolledSkill"/> entries from wire <see cref="GameServer.Core.Protocol.Instance.InstanceMercenaries.MercenarySkillWire"/>
    /// records (e.g. from a persisted <see cref="GameServer.Core.Protocol.Instance.MercenaryContractData"/> warrant).</summary>
    public static List<RolledSkill> RolledSkillsFromWire(IReadOnlyList<GameServer.Core.Protocol.Instance.InstanceMercenaries.MercenarySkillWire> wire)
    {
        List<RolledSkill> result = [];
        foreach (var entry in wire)
        {
            if (SkillByHash(entry.SkillHash16) is not { } skill) continue;
            List<int> suppRows = [];
            List<ushort> suppHashes = [];
            foreach (ushort sh in entry.SupportHashes16)
            {
                if (SupportByHash(sh) is not { } supp) continue;
                suppRows.Add(supp.Row);
                suppHashes.Add(supp.Hash16);
            }
            result.Add(new(skill.Row, skill.Hash16, suppRows, suppHashes));
        }
        return result;
    }

    /// <summary>Collects the GrantedEffects rows from a mercenary's rolled skills (falling back to build skills if empty).</summary>
    public static IReadOnlyList<int> EffectsFromRolled(IReadOnlyList<RolledSkill> rolled, bool encounter)
    {
        List<int> effects = [];
        foreach (var entry in rolled)
        {
            if (!SkillRowsData.TryGetValue(entry.SkillRow, out var skill)) continue;
            int effect = encounter && skill.EncounterGrantedEffect >= 0 ? skill.EncounterGrantedEffect : skill.GrantedEffect;
            if (effect >= 0 && !effects.Contains(effect)) effects.Add(effect);
        }
        return effects;
    }

    /// <summary>Sums support stat bonuses from all rolled skills' <c>MercenarySupports</c> rows.</summary>
    public static Dictionary<uint, int> SupportStatsOf(IReadOnlyList<RolledSkill> rolled, int? forSkillRow = null)
    {
        Dictionary<uint, int> stats = [];
        foreach (var entry in rolled)
        {
            if (forSkillRow is int target && entry.SkillRow != target) continue;
            foreach (int suppRow in entry.SupportRows)
            {
                if (!SupportRowsData.TryGetValue(suppRow, out var supp)) continue;
                int len = Math.Min(supp.StatKeys.Length, supp.StatValues.Length);
                for (int i = 0; i < len; i++)
                    stats[supp.StatKeys[i]] = stats.GetValueOrDefault(supp.StatKeys[i]) + supp.StatValues[i];
            }
        }
        return stats;
    }

    static uint PickWeightedWord(IReadOnlyList<WordRow> words, int[] buildTags, Random rng)
    {
        if (words.Count == 0) return 0;
        var tagSet = buildTags.Length > 0 ? buildTags.ToHashSet() : null;
        long total = 0;
        var weighted = new List<(uint Hash, int Weight)>();
        foreach (var w in words)
        {
            if (w.Hash32 == 0) continue;
            int weight = MatchWeight(w.Tags, w.Weights, tagSet);
            if (weight > 0) { weighted.Add((w.Hash32, weight)); total += weight; }
        }
        if (total > 0)
        {
            long pick = rng.NextInt64(total);
            foreach (var (hash, weight) in weighted)
            {
                pick -= weight;
                if (pick < 0) return hash;
            }
        }
        return words[rng.Next(words.Count)].Hash32;
    }

    static ushort PickWeightedFlavour(int[] buildTags, Random rng)
    {
        if (FlavoursData.Count == 0) return 0;
        var tagSet = buildTags.Length > 0 ? buildTags.ToHashSet() : null;
        long total = 0;
        var weighted = new List<(ushort Hash, int Weight)>();
        foreach (var f in FlavoursData)
        {
            if (f.Hash16 == 0) continue;
            int weight = MatchWeight(f.Tags, f.Weights, tagSet);
            if (weight > 0) { weighted.Add((f.Hash16, weight)); total += weight; }
        }
        if (total > 0)
        {
            long pick = rng.NextInt64(total);
            foreach (var (hash, weight) in weighted)
            {
                pick -= weight;
                if (pick < 0) return hash;
            }
        }
        return FlavoursData[rng.Next(FlavoursData.Count)].Hash16;
    }

    static int MatchWeight(int[] tags, int[] weights, HashSet<int>? buildTags)
    {
        if (buildTags is null || tags.Length == 0) return 1;
        int len = Math.Min(tags.Length, weights.Length);
        for (int i = 0; i < len; i++)
            if (buildTags.Contains(tags[i])) return weights[i];
        return 0;
    }

    static Dictionary<int, string> LoadTagNames()
    {
        var map = new Dictionary<int, string>();
        foreach (var r in ClientTables.TableOrEmpty("Tags"))
            if (r.TextOrNull("Id") is { Length: > 0 } id) map[r.Index] = id;
        return map;
    }

    static List<Class> LoadClasses(Dictionary<int, string> tagNames)
    {
        var all = global::Game.Monsters.All;
        string PathOf(int row) => row >= 0 && row < all.Count ? all[row].Id : "";
        bool Supported(int row) => row >= 0 && row < all.Count && all[row].Supported;
        var attributes = ClientTables.TableOrEmpty("MercenaryAttributes");
        var rows = new List<Class>();
        foreach (var r in ClientTables.TableOrEmpty("MercenaryClasses"))
        {
            int hostile = r.IntOrNull("MonsterVariety") ?? -1;
            int allied = r.IntOrNull("MonsterVarietyAllied") ?? -1;
            int attrRow = r.IntOrNull("Attribute") ?? -1;
            string attribute = "";
            var attrTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (attrRow >= 0 && attributes.Row(attrRow) is { } aRow)
            {
                attribute = aRow.TextOrNull("Id") ?? "";
                foreach (int t in aRow.Ints("Tags"))
                    if (tagNames.TryGetValue(t, out var tId)) attrTags.Add(tId);
            }
            rows.Add(new Class(r.Index, r.TextOrNull("Id") ?? "", PathOf(hostile), PathOf(allied),
                r.TextOrNull("HouseName") ?? "", attribute, Supported(hostile))
            {
                AttributeRow = attrRow,
                AttributeTagIds = attrTags,
            });
        }
        return rows;
    }

    static (Dictionary<int, SkillRow>, Dictionary<int, BuildSkills>) LoadSkills()
    {
        Dictionary<int, SkillRow> skills = [];
        foreach (var r in ClientTables.TableOrEmpty("MercenarySkills"))
        {
            skills[r.Index] = new SkillRow(
                r.IntOrNull("GrantedEffect") ?? -1,
                r.IntOrNull("EncounterGrantedEffect") ?? -1,
                r.IntOrNull("RequiredLevel") ?? 0)
            {
                Row = r.Index,
                Hash16 = (ushort)(r.IntOrNull("HASH16") ?? 0),
                SupportCountMode = r.IntOrNull("SupportCount") ?? 3,
                PossibleSupports = r.Ints("PossibleSupports"),
                SkillFamily = r.IntOrNull("SkillFamily"),
                Name = r.TextOrNull("Name") ?? "",
            };
        }
        Dictionary<int, BuildSkills> builds = [];
        foreach (var r in ClientTables.TableOrEmpty("MercenaryBuilds"))
            builds[r.Index] = new BuildSkills(r.Ints("Skills1"), r.Ints("Skills2"), r.IntOrNull("Skills2Count") ?? 0,
                r.Ints("Skills3"), r.IntOrNull("Skills3Count") ?? 0);
        return (skills, builds);
    }

    static List<Build> LoadBuilds()
    {
        var wieldable = ClientTables.TableOrEmpty("MercenaryWieldableTypes");
        var rows = new List<Build>();
        foreach (var r in ClientTables.TableOrEmpty("MercenaryBuilds"))
        {
            List<int> classes = [];
            foreach (int wRow in r.Ints("WieldableTypes"))
                if (wieldable.Row(wRow) is { } w && w.IntOrNull("ItemClass") is int cls)
                    classes.Add(cls);
            rows.Add(new Build(r.Index, r.TextOrNull("Id") ?? "", r.IntOrNull("Class") ?? -1, r.TextOrNull("Name") ?? "",
                (ushort)(r.IntOrNull("HASH16") ?? 0), r.IntOrNull("IsInfamous") is int infamous && infamous != 0)
            {
                OffhandMode = r.IntOrNull("__col14") ?? 0,
                Tags = r.Ints("Tags"),
                ExtraStats = r.Ints("ExtraStats"),
                WieldableItemClasses = [.. classes],
            });
        }
        return rows;
    }

    static Dictionary<int, SupportRow> LoadSupports()
    {
        Dictionary<int, SupportRow> map = [];
        foreach (var r in ClientTables.TableOrEmpty("MercenarySupports"))
        {
            int[] rawStats = r.Ints("Stats");
            uint[] keys = new uint[rawStats.Length];
            for (int i = 0; i < rawStats.Length; i++) keys[i] = (uint)(rawStats[i] + 1);
            map[r.Index] = new SupportRow(
                r.Index,
                r.TextOrNull("Id") ?? "",
                (ushort)(r.IntOrNull("HASH16") ?? 0),
                keys,
                r.Ints("StatValues"),
                r.IntOrNull("SupportFamily"),
                r.IntOrNull("Tier") ?? 1);
        }
        return map;
    }

    static Dictionary<int, ExtraStatRow> LoadExtraStats()
    {
        Dictionary<int, ExtraStatRow> map = [];
        foreach (var r in ClientTables.TableOrEmpty("MercenaryBuildExtraStats"))
        {
            if (r.IntOrNull("Stat") is not int stat) continue;
            map[r.Index] = new ExtraStatRow(
                r.Index,
                r.TextOrNull("Id") ?? "",
                (uint)(stat + 1),
                r.IntOrNull("Level24") ?? 0,
                r.IntOrNull("Level68") ?? 0,
                r.IntOrNull("Level84") ?? 0,
                r.IntOrNull("Category") ?? 0);
        }
        return map;
    }

    static List<FlavourRow> LoadFlavours()
    {
        List<FlavourRow> list = [];
        foreach (var r in ClientTables.TableOrEmpty("MercenaryFlavourText"))
            list.Add(new FlavourRow(r.Index, r.TextOrNull("Id") ?? "", (ushort)(r.IntOrNull("HASH16") ?? 0),
                r.Ints("Tags"), r.Ints("TagWeight")));
        return list;
    }

    static (List<WordRow> First, List<WordRow> Second, Dictionary<uint, string> ByHash) LoadWords()
    {
        List<WordRow> first = [], second = [];
        Dictionary<uint, string> byHash = [];
        foreach (var r in ClientTables.TableOrEmpty("Words"))
        {
            uint hash = r.UInt("HASH32");
            if (hash == 0) continue;
            string text = r.TextOrNull("Text") ?? "";
            if (text.Length > 0) byHash.TryAdd(hash, text);
            int wl = r.IntOrNull("Wordlist") ?? -1;
            if (wl is not (13 or 14)) continue;
            var entry = new WordRow(r.Index, wl, hash, text,
                r.Ints("SpawnWeight_Tags"), r.Ints("SpawnWeight_Values"));
            if (wl == 13) first.Add(entry);
            else second.Add(entry);
        }
        return (first, second, byHash);
    }

    static HashSet<string> LoadDisabledUniques()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ClientTables.Has("MercenaryDisabledUniques"))
        {
            var words = ClientTables.TableOrEmpty("Words");
            foreach (var r in ClientTables.Table("MercenaryDisabledUniques"))
            {
                if (r.IntOrNull("UniqueName") is int wRow && words.Row(wRow)?.TextOrNull("Text") is { Length: > 0 } text)
                    set.Add(text);
                else if (r.TextOrNull("Id") is { Length: > 0 } id)
                    set.Add(id);
            }
        }
        return set;
    }

    static Dictionary<string, AreaMeta> LoadAreas()
    {
        var map = new Dictionary<string, AreaMeta>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in ClientTables.TableOrEmpty("WorldAreas"))
        {
            if (r.TextOrNull("Id") is not { Length: > 0 } id) continue;
            map[id] = new AreaMeta(id, r.IntOrNull("Act") ?? 0, r.IntOrNull("AreaLevel") ?? 1,
                r.IntOrNull("IsTown") is > 0, r.IntOrNull("IsHideout") is > 0);
        }
        return map;
    }
}
