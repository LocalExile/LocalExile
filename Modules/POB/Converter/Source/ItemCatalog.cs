using System.Text.Json;
using GameServer.Core.Protocol.Instance;
sealed class ItemCatalog
{
    readonly Lazy<(List<BaseType> Rows, string? Error)> rows;
    readonly Lazy<Dictionary<int, GemType>> gems;
    readonly Lazy<Dictionary<int, BaseDefences>> defences;
    public ModCatalog Mods { get; }
    public ItemCatalog(string folder)
    {
        Mods = new(folder); rows = new(() => Load(folder, Mods));
        gems = new(() => LoadGems(folder, false)); defences = new(() => LoadDefences(folder));
    }
    public string? Error => rows.Value.Error;
    public IReadOnlyList<BaseType> All => rows.Value.Rows;
    public GemType? Gem(BaseType type) => gems.Value.GetValueOrDefault(type.Row);
    public BaseDefences? Defences(BaseType type) => defences.Value.TryGetValue(type.Row, out var value) ? value : null;
    public IEnumerable<int> GemEffects(Item gem)
    {
        var type = All.FirstOrDefault(t => t.Hash == gem.TypeHash);
        if (type is null || Gem(type) is not { IsSupport: false } g) yield break;
        var v = g.Variant(gem.GemVariant);
        if (NativeActiveEffects.Contains(v.Effect)) yield return v.Effect;
        if (NativeActiveEffects.Contains(v.Effect2)) yield return v.Effect2;
    }
    public static Item Create(BaseType type, IReadOnlyList<ItemSocket>? sockets = null, IReadOnlyList<byte>? links = null, byte? defenceBaseRoll = null) =>
        new(type.Hash, type.Components, type.VisualIdentity) { Charges = type.MaxCharges, Sockets = sockets ?? [], Links = links,
            DefenceBaseRoll = defenceBaseRoll ?? 0, Mods = [] };
    public sealed record BaseType(int Row, string Id, string Name, uint Hash, byte Width, byte Height, ushort VisualIdentity,
        IReadOnlyList<string> Components, int MaxCharges, string Class = "", byte Stance = 0xff, int DropLevel = 0, ushort MaxStack = 1,
        int FullStackRow = -1, IReadOnlyList<ModCatalog.Mod>? Implicits = null)
    {
        /// <summary>Tags.dat Ids for mod spawn weights: the .it chain's Base tags (item-types.json) and the row's TagsKeys.</summary>
        public IReadOnlySet<string> Tags { get; init; } = new HashSet<string>();

        /// <summary>BaseItemTypes ModDomain: the Mods.dat Domain its affixes come from (1 items, 2 flasks, …).</summary>
        public int ModDomain { get; init; } = 1;

        /// <summary>Its ItemClasses row (-1 unknown): the worn item's weapon type the client checks skills against is this
        /// row + 1 (11-skills.md "Weapon restrictions").</summary>
        public int ClassRow { get; init; } = -1;

        public bool Supported => Components.Count > 0 && Item.CanSerialize(Components);
        /// <summary>Several of the type share one cell (a Stack component and a CurrencyItems StackSize above 1).</summary>
        public bool Stackable => MaxStack > 1;
        /// <summary>A full stack becomes one of this BaseItemTypes row (CurrencyItems FullStack_BaseItemTypesKey: 5 Scroll
        /// Fragments → a Scroll of Wisdom, 20 shards → their orb), or -1.</summary>
        public bool BecomesWhenFull => FullStackRow >= 0;
        public override string ToString() => $"{Name} ({Id}, row {Row})";
    }

    public sealed record GemVariant(int Index, string Name, int Effect, int Effect2, bool Creatable)
    {
        /// <summary>Stable GemEffects.dat Id used by build export tools (it can differ from GrantedEffects Id).</summary>
        public string Id { get; init; } = "";
    }

    /// <summary>A gem type (a SkillGems row): its variants (the first is the normal gem) and its experience thresholds,
    /// ItemExperiencePerLevel's Experience for levels 1, 2, … of its experience type; the last level is the cap.</summary>
    public sealed record GemType(int Row, bool IsSupport, IReadOnlyList<GemVariant> Variants, IReadOnlyList<int> Thresholds)
    {
        /// <summary>SkillGems GemColour: 1 red (strength), 2 green (dexterity), 3 blue (intelligence), 4 white.</summary>
        public int Colour { get; init; }

        /// <summary>A Vaal gem's own row (IsVaalVariant) or an awakened one (RegularVariant set).</summary>
        public bool IsVaalOrAwakened { get; init; }

        /// <summary>Vaal gems are intrinsically corrupted, independently of their level and quality.</summary>
        public bool IsVaal { get; init; }

        /// <summary>SkillGems MtxSlotTypes: the MicrotransactionSkillGemEffectSlotTypes rows its Cosmetics tab skill slots
        /// take (0 the skill's effect, 1 a totem skin).</summary>
        public IReadOnlyList<int> MtxSlotTypes { get; init; } = [];

        /// <summary>SkillGems StrengthRequirementPercent: the strength share of the gem's attribute requirement
        /// (SkillCatalog.GemAttributeRequirement), 0 for a pure dexterity or intelligence gem.</summary>
        public int StrengthPercent { get; init; }

        /// <summary>SkillGems DexterityRequirementPercent (<see cref="StrengthPercent"/>).</summary>
        public int DexterityPercent { get; init; }

        /// <summary>SkillGems IntelligenceRequirementPercent (<see cref="StrengthPercent"/>).</summary>
        public int IntelligencePercent { get; init; }

        public int MaxLevel => Thresholds.Count;

        /// <summary>The level as the client derives it (0x14246f900): the levels whose threshold the experience reaches, up
        /// to the cap. Experience -1 is the cap + 1 (0x14246eb00; a guess: the corrupted gems over their cap).</summary>
        public int Level(int experience) => experience == -1 ? MaxLevel + 1 : Thresholds.Count(t => t <= experience);

        /// <summary>The experience a gem of this level starts with (the level clamped to 1 … the cap).</summary>
        public int ExperienceFor(int level) => Thresholds.Count == 0 ? 0 : Thresholds[Math.Clamp(level, 1, MaxLevel) - 1];

        /// <summary>The variant the client uses: an index outside the list is the normal gem (0x14246fa50 resets it).</summary>
        public GemVariant Variant(int index) => index > 0 && index < Variants.Count ? Variants[index] : Variants[0];
    }

    public readonly record struct BaseDefences(int ArmourMin, int ArmourMax, int EvasionMin, int EvasionMax,
        int EnergyShieldMin = 0, int EnergyShieldMax = 0, int WardMin = 0, int WardMax = 0)
    {
        /// <summary>Resolve the saved native roll before local modifiers; wire 32 selects the minimum base.</summary>
        public (int Armour, int Evasion, int EnergyShield) AtRoll(byte roll)
        {
            if (roll > Item.SpecialDefenceBaseRoll)
                throw new ArgumentOutOfRangeException(nameof(roll), roll, "Defence base roll must be 0..32.");
            int effective = roll == Item.SpecialDefenceBaseRoll ? 0 : roll;
            int At(int minimum, int maximum) =>
                (int)(minimum + (((long)maximum - minimum) * effective + 15) / Item.MaximumDefenceBaseRoll);
            return (At(ArmourMin, ArmourMax), At(EvasionMin, EvasionMax), At(EnergyShieldMin, EnergyShieldMax));
        }
    }

    private static Dictionary<int, BaseDefences> LoadDefences(string folder)
    {
        string path = Path.Combine(folder, "tables", "ArmourTypes.json");
        var result = new Dictionary<int, BaseDefences>();
        if (!File.Exists(path)) return result;
        using var d = JsonDocument.Parse(File.ReadAllBytes(path));
        foreach (var r in d.RootElement.EnumerateArray())
        {
            if (r.GetProperty("BaseItemTypesKey") is not { ValueKind: JsonValueKind.Number } key) continue;
            result[(int)Math.Max(0, key.GetInt32())] = new BaseDefences(
                r.GetProperty("ArmourMin").GetInt32(), r.GetProperty("ArmourMax").GetInt32(),
                r.GetProperty("EvasionMin").GetInt32(), r.GetProperty("EvasionMax").GetInt32(),
                r.TryGetProperty("EnergyShieldMin", out var esMin) ? esMin.GetInt32() : 0,
                r.TryGetProperty("EnergyShieldMax", out var esMax) ? esMax.GetInt32() : 0,
                r.TryGetProperty("WardMin", out var wardMin) ? wardMin.GetInt32() : 0,
                r.TryGetProperty("WardMax", out var wardMax) ? wardMax.GetInt32() : 0);
        }
        return result;
    }
    private static Dictionary<int, GemType> LoadGems(string folder, bool ruthless)
    {
        string Table(string n) => Path.Combine(folder, "tables", n + ".json");
        var result = new Dictionary<int, GemType>();
        string[] needed = ["SkillGems", "GemEffects", "BaseItemTypes", "ItemExperiencePerLevel", "ItemExperienceTypes"];
        if (needed.Any(n => !File.Exists(Table(n)))) return result;
        using var g = JsonDocument.Parse(File.ReadAllBytes(Table("SkillGems")));
        using var e = JsonDocument.Parse(File.ReadAllBytes(Table("GemEffects")));
        using var b = JsonDocument.Parse(File.ReadAllBytes(Table("BaseItemTypes")));
        using var x = JsonDocument.Parse(File.ReadAllBytes(Table("ItemExperiencePerLevel")));
        using var xt = JsonDocument.Parse(File.ReadAllBytes(Table("ItemExperienceTypes")));
        static int Row(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;
        // Null or absent means no alternate; row zero remains a real effect, and must not be treated as missing.
        int Effect(JsonElement r, string name) => ruthless && Row(r, name + "Hardmode") is int alternate and >= 0
            ? alternate : Row(r, name);
        var thresholds = x.RootElement.EnumerateArray().GroupBy(r => Row(r, "ItemExperienceType")).ToDictionary(t => t.Key,
            t => (IReadOnlyList<int>)[.. t.OrderBy(r => Row(r, "ItemCurrentLevel")).Select(r => Row(r, "Experience"))]);
        var effects = e.RootElement;
        string Name(int row) => effects[row].GetProperty("Name").GetString() ?? "";
        static IEnumerable<(int Row, int Index)> Variants(JsonElement r) =>
            r.GetProperty("GemVariants").EnumerateArray().Select((v, i) => (v.GetInt32(), i));
        // A regular skill's GrantedEffects row → its variant's name, for the Vaal variants that grant it as their base.
        var regular = new Dictionary<int, string>();
        var rows = new List<(int BaseRow, JsonElement Row, string BaseName)>();
        foreach (var r in g.RootElement.EnumerateArray())
        {
            int baseRow = Row(r, "BaseItemTypesKey");
            if (baseRow < 0) continue;
            string baseName = b.RootElement[baseRow].GetProperty("Name").GetString() ?? "";
            rows.Add((baseRow, r, baseName));
            if (r.GetProperty("IsVaalVariant").GetBoolean() || baseName.StartsWith('[')) continue;
            foreach (var (v, i) in Variants(r))
                if (Effect(effects[v], "GrantedEffect") is int ge and >= 0 && !Name(v).StartsWith("[DNT]"))
                    regular.TryAdd(ge, i == 0 ? baseName : Name(v));
        }
        foreach (var (baseRow, r, baseName) in rows)
        {
            bool vaal = r.GetProperty("IsVaalVariant").GetBoolean();
            int expType = Row(r, "ItemExperienceType");
            bool awakened = Row(r, "RegularVariant") >= 0
                && (expType < 0 || xt.RootElement[expType].GetProperty("Id").GetString() != "GemProgressionExceptionalPlus");
            bool rowCreatable = !baseName.StartsWith('[') && !baseName.StartsWith("WIP");
            var variants = new List<GemVariant>();
            foreach (var (v, i) in Variants(r))
            {
                int effect = Effect(effects[v], "GrantedEffect"), effect2 = Effect(effects[v], "GrantedEffect2");
                string name = i == 0 ? baseName : Name(v);
                bool creatable = rowCreatable && !name.StartsWith("[DNT]");
                if (vaal && effect2 >= 0)
                {
                    creatable &= regular.TryGetValue(effect2, out var baseSkill);
                    if (i > 0 && baseSkill is not null) name = "Vaal " + baseSkill;
                }
                variants.Add(new GemVariant(i, name, effect, effect2, creatable)
                { Id = effects[v].TryGetProperty("Id", out var variantId) ? variantId.GetString() ?? "" : "" });
            }
            result.TryAdd(baseRow, new GemType(baseRow, r.GetProperty("IsSupport").GetBoolean(), variants,
                thresholds.GetValueOrDefault(expType) ?? [])
            {
                Colour = Row(r, "GemColour"), IsVaal = vaal, IsVaalOrAwakened = vaal || Row(r, "RegularVariant") >= 0,
                MtxSlotTypes = r.TryGetProperty("MtxSlotTypes", out var st) ? [.. st.EnumerateArray().Select(t => t.GetInt32())] : [],
                StrengthPercent = Row(r, "StrengthRequirementPercent"), DexterityPercent = Row(r, "DexterityRequirementPercent"),
                IntelligencePercent = Row(r, "IntelligenceRequirementPercent"),
            });
        }
        return result;
    }
    private static (List<BaseType>, string?) Load(string folder, ModCatalog mods)
    {
        string bases = Path.Combine(folder, "tables", "BaseItemTypes.json"), visuals = Path.Combine(folder, "tables", "ItemVisualIdentity.json");
        if (!File.Exists(bases) || !File.Exists(visuals)) return ([], $"{bases} is missing: extract the game tables (Tools/PoEDataExtractor)");
        var components = new Dictionary<string, IReadOnlyList<string>>();
        var itemTags = new Dictionary<string, IReadOnlyList<string>>();
        string types = Path.Combine(folder, "item-types.json");
        if (!File.Exists(types)) return ([], $"{types} is missing: run scripts/dev/extract-data.ps1");
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(types)))
            foreach (var t in doc.RootElement.GetProperty("types").EnumerateArray())
            {
                components[t.GetProperty("type").GetString()!] =
                    [.. t.GetProperty("components").EnumerateArray().Take(t.GetProperty("read").GetInt32()).Select(c => c.GetString()!)];
                // "tags" since 2026-10-20; older files have none (regenerate with ot_components.py --items).
                if (t.TryGetProperty("tags", out var tg)) itemTags[t.GetProperty("type").GetString()!] = [.. tg.EnumerateArray().Select(x => x.GetString()!)];
            }
        List<ushort> visualHashes;
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(visuals)))
            visualHashes = [.. doc.RootElement.EnumerateArray().Select(r => (ushort)r.GetProperty("HASH16").GetInt32())];
        var charges = new Dictionary<string, int>();
        string chargesPath = Path.Combine(folder, "tables", "ComponentCharges.json");
        if (File.Exists(chargesPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(chargesPath));
            foreach (var r in doc.RootElement.EnumerateArray())
                charges[r.GetProperty("BaseItemTypesKey").GetString() ?? ""] = r.GetProperty("MaxCharges").GetInt32();
        }
        // CurrencyItems StackSize by BaseItemTypes row (Orb of Transmutation 40, Scroll of Wisdom 40, Portal Scroll 40 …);
        // only types with a Stack component stack.
        var stackSizes = new Dictionary<int, ushort>();
        var fullStacks = new Dictionary<int, int>();
        string currencyPath = Path.Combine(folder, "tables", "CurrencyItems.json");
        if (File.Exists(currencyPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(currencyPath));
            foreach (var r in doc.RootElement.EnumerateArray())
                if (r.GetProperty("BaseItemTypesKey") is { ValueKind: JsonValueKind.Number } b && r.GetProperty("StackSize").GetInt32() is > 1 and var size)
                {
                    stackSizes.TryAdd(b.GetInt32(), (ushort)Math.Min(size, ushort.MaxValue));
                    if (r.GetProperty("FullStack_BaseItemTypesKey") is { ValueKind: JsonValueKind.Number } full) fullStacks.TryAdd(b.GetInt32(), full.GetInt32());
                }
        }
        var classes = new List<string>();
        var stances = new List<byte>();
        string classesPath = Path.Combine(folder, "tables", "ItemClasses.json");
        if (File.Exists(classesPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(classesPath));
            classes = [.. doc.RootElement.EnumerateArray().Select(r => r.GetProperty("Id").GetString() ?? "")];
            stances = [.. doc.RootElement.EnumerateArray().Select(r => r.GetProperty("ItemStance") is { ValueKind: JsonValueKind.Number } s ? (byte)s.GetInt32() : (byte)0xff)];
        }
        List<string> tagIds = [];
        string tagsPath = Path.Combine(folder, "tables", "Tags.json");
        if (File.Exists(tagsPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(tagsPath));
            tagIds = [.. doc.RootElement.EnumerateArray().Select(r => r.GetProperty("Id").GetString() ?? "")];
        }
        var rows = new List<BaseType>();
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(bases)))
        {
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                string id = r.GetProperty("Id").GetString() ?? "";
                var itemComponents = components.GetValueOrDefault(id) ?? [];
                int visual = r.GetProperty("ItemVisualIdentity").GetInt32();
                rows.Add(new BaseType(rows.Count, id, r.GetProperty("Name").GetString() ?? "", r.GetProperty("HASH32").GetUInt32(),
                    (byte)r.GetProperty("Width").GetInt32(), (byte)r.GetProperty("Height").GetInt32(),
                    visual >= 0 && visual < visualHashes.Count ? visualHashes[visual] : (ushort)0,
                    itemComponents, charges.GetValueOrDefault(id),
                    r.GetProperty("ItemClassesKey") is { ValueKind: JsonValueKind.Number } c && c.GetInt32() < classes.Count ? classes[c.GetInt32()] : "",
                    // the row's own ItemStance, else its class's (ItemStances: Wand = 2)
                    r.GetProperty("ItemStance") is { ValueKind: JsonValueKind.Number } st ? (byte)st.GetInt32()
                        : r.GetProperty("ItemClassesKey") is { ValueKind: JsonValueKind.Number } c2 && c2.GetInt32() < stances.Count ? stances[c2.GetInt32()] : (byte)0xff,
                    r.GetProperty("DropLevel").GetInt32(),
                    itemComponents.Contains("Stack") ? stackSizes.GetValueOrDefault(rows.Count, (ushort)1) : (ushort)1,
                    fullStacks.GetValueOrDefault(rows.Count, -1),
                    // Implicit_ModsKeys: Mods.dat rows every item of the type has (Driftwood Wand: 8-12% increased Spell Damage)
                    [.. r.GetProperty("Implicit_ModsKeys").EnumerateArray().Select(k => mods.Row(k.GetInt32())).OfType<ModCatalog.Mod>()])
                {
                    Tags = new HashSet<string>([.. itemTags.GetValueOrDefault(id) ?? [],
                        .. r.GetProperty("TagsKeys").EnumerateArray().Select(k => k.GetInt32()).Where(k => k < tagIds.Count).Select(k => tagIds[k])]),
                    ModDomain = r.GetProperty("ModDomain").GetInt32(),
                    ClassRow = r.GetProperty("ItemClassesKey") is { ValueKind: JsonValueKind.Number } cr ? cr.GetInt32() : -1,
                });
            }
        }
        return (rows, null);
    }
}
