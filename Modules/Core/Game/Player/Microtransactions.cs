using System.Text.Json;
using GameServer.Core.Protocol.Instance;


/// <summary>
/// Every microtransaction in the game, owned by every account: one 0x1f4 record per <c>MtxTypes</c> row the client can
/// resolve, which the client's Microtransactions panel (N) and the store's ALREADY OWNED marks read
/// (23-microtransactions.md). A record's <c>MtxTypeHash</c> is the row's HASH32 and its <c>Id</c> the row index + 1,
/// unique per record as the client's model needs. The panel looks each hash up in <c>MTXTypeGameSpecific.dat</c>
/// (0x1408e4fb0 → 0x142222170), and a hash that is not there leaves a null the row build faults on (live 2026-10-23:
/// all 8411 MtxTypes rows crashed the client; 690 of them have no MtxTypeGameSpecific row), so only rows with one are
/// sent. Names stay empty: the client names a record from its catalogue.
/// Game data, loaded on first use; without <c>data/tables</c> the list is empty.
/// </summary>
sealed class MtxCatalog(string folder = "data")
{
    private readonly Lazy<List<InstanceMicrotransactions.Record>> _owned = new(() => Load(folder));
    private Dictionary<uint, InstanceMicrotransactions.Record>? _byId;

    public IReadOnlyList<InstanceMicrotransactions.Record> Owned => _owned.Value;

    /// <summary>The owned record with this id (what 0x1f9 picks), or null.</summary>
    public InstanceMicrotransactions.Record? Find(uint id) =>
        (_byId ??= Owned.ToDictionary(r => r.Id)).GetValueOrDefault(id);

    /// <summary>The owned record of a MtxTypes HASH32, or null.</summary>
    public InstanceMicrotransactions.Record? ByHash(uint hash) => Owned.FirstOrDefault(r => r.MtxTypeHash == hash);

    /// <summary>
    /// The worn table 0x1f3 carries for <paramref name="worn"/> (MicrotransactionSlot Id → saved pick): a pick whose
    /// record no longer exists (another game version's data) is left out, so the client never gets a hash it cannot
    /// resolve.
    /// </summary>
    public List<InstanceMicrotransactions.Equipped> Equipped(IReadOnlyDictionary<byte, CharacterSaves.SavedCosmetic> worn) =>
        [.. worn.Where(w => w.Key < InstanceMicrotransactions.SlotCount)
            .Select(w => (w.Key, w.Value, Record: Find(w.Value.Record)))
            .Where(w => w.Record is not null)
            .OrderBy(w => w.Key)
            .Select(w => new InstanceMicrotransactions.Equipped(w.Key, w.Record!.Id, w.Record.MtxTypeHash, w.Record.Qword, w.Value.Option))];

    static List<InstanceMicrotransactions.Record> Load(string folder)
    {
        string dir = Path.Combine(folder, "tables");
        var resolvable = Hashes(Path.Combine(dir, "MtxTypeGameSpecific.json"));
        var types = Hashes(Path.Combine(dir, "MtxTypes.json"));
        if (resolvable is null || types is null) return [];
        var list = new List<InstanceMicrotransactions.Record>();
        for (int row = 0; row < types.Count; row++)
            if (types[row] is uint h && resolvable.Contains(h))
                list.Add(new InstanceMicrotransactions.Record(h, (uint)row + 1));
        return list;
    }

    /// <summary>Each row's HASH32 in table order (null for a row without one), or null without the file.</summary>
    internal static List<uint?>? Hashes(string path)
    {
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return [.. doc.RootElement.EnumerateArray().Select(r =>
            r.TryGetProperty("HASH32", out var hash) && hash.TryGetUInt32(out uint h) ? h : (uint?)null)];
    }
}

/// <summary>
/// What a worn cosmetic does in the world, beyond the client's own worn table (which already draws cursors, portals
/// and flask skins). <c>ItemisedVisualEffect</c> has one row per cosmetic (<c>Mtx</c>: its MtxTypeGameSpecific row):
/// <c>VisualIdentity</c> is a skin, attachment or character effect the model draws, <c>VisualEffect</c> a weapon
/// effect, and <c>Stats</c> with the values in <c>__col7</c> the stats it gives the character (footprints:
/// footstep_effect_variation, apparitions: imp_variation, …). <c>MicrotransactionSlot.__col7</c> is the model slot a
/// Cosmetics tab slot dresses: the Inventories component's slot numbers (Weapon1 0 … Amulet1 11) and the slots only
/// cosmetics use (armour attachment 3, character effect 12, footprints 13, helmet attachments 14 and 15, hip 16);
/// 17 is none.
/// Game data, loaded on first use; without <c>data/tables</c> nothing applies.
/// </summary>
sealed class CosmeticEffects(string folder = "data")
{
    /// <summary>One cosmetic's effect: ItemVisualIdentity / ItemVisualEffect HASH16s (0 for none), its stats (Stats
    /// row + 1, value) and, for a pet, its <c>Pet.dat</c> row + 1 (0 otherwise).</summary>
    public sealed record Effect(ushort Visual, ushort WeaponEffect, IReadOnlyList<(uint Stat, int Value)> Stats, int Pet = 0)
    {
        public SkillRestriction? Skill { get; init; }
        /// <summary>The pet's object type (its Pet.dat Id, e.g. Metadata/Pet/Kiwi/Basic), the object the server spawns.</summary>
        public string? PetType { get; init; }

        /// <summary>The pet type's deserialized components and StateMachine defaults (object-types.json), for its blob.</summary>
        public IReadOnlyList<string>? PetComponents { get; init; }
        public StateMachineData? PetStates { get; init; }

        /// <summary>A portrait's variation (MicrotransactionCharacterPortraitVariations <c>__col1</c>), 0 otherwise: the
        /// Player component's u16 at +0x1aa and the character list's U46.</summary>
        public ushort Portrait { get; init; }

        /// <summary>A portrait frame's variation (MicrotransactionSocialFrameVariations <c>Index</c>), 0 otherwise: the
        /// client draws <c>Art/2DArt/UIImages/Common/MTX/SupporterIconFrame{Frame}</c> around the portrait.</summary>
        public ushort Frame { get; init; }
    }

    public sealed record SkillRestriction(int SlotType, IReadOnlyList<int> Gems, IReadOnlyList<int> Classes);

    // The same eligibility fields the client checks in 0x1424ac1e0. In particular a record from a stale picker
    // must not attach A's art to B merely because it is an owned record.
    public bool FitsSkill(uint hash, ItemCatalog.BaseType gem, byte slotType) =>
        Of(hash)?.Skill is { } skill && skill.SlotType == slotType
        && (skill.Gems.Count == 0 || skill.Gems.Contains(gem.Row))
        && (skill.Classes.Count == 0 || skill.Classes.Contains(gem.ClassRow));

    /// <summary>A Cosmetics tab slot: its name and the model slot it dresses (null: none).</summary>
    public sealed record Slot(string Name, byte? ModelSlot)
    {
        /// <summary>Weapon effects go in the second reference of the weapon's entry, everything else in the first.</summary>
        public bool IsWeaponEffect => Name is "Weapon Effect" or "Weapon Added Effect";
    }

    /// <summary>Model slots no item fills: they only ever show a cosmetic, and are sent empty when none is worn.</summary>
    public static readonly byte[] CosmeticOnlySlots = [3, 12, 13, 14, 15, 16];

    readonly Lazy<(Dictionary<uint, Effect> Effects, Dictionary<byte, Slot> Slots)> _data = new(() => Load(folder));

    /// <summary>The effect of the cosmetic with this MtxTypeGameSpecific HASH32, or null.</summary>
    public Effect? Of(uint hash) => _data.Value.Effects.GetValueOrDefault(hash);

    /// <summary>The Cosmetics tab slot with this MicrotransactionSlot Id, or null.</summary>
    public Slot? SlotOf(byte id) => _data.Value.Slots.GetValueOrDefault(id);

    static (Dictionary<uint, Effect>, Dictionary<byte, Slot>) Load(string folder)
    {
        string dir = Path.Combine(folder, "tables");
        var effects = new Dictionary<uint, Effect>();
        var slots = new Dictionary<byte, Slot>();
        var gameSpecific = MtxCatalog.Hashes(Path.Combine(dir, "MtxTypeGameSpecific.json"));
        var identities = Hash16s(Path.Combine(dir, "ItemVisualIdentity.json"));
        var weaponEffects = Hash16s(Path.Combine(dir, "ItemVisualEffect.json"));
        if (gameSpecific is null || identities is null || weaponEffects is null) return (effects, slots);
        var skillEffects = Rows(Path.Combine(dir, "MtxTypeGameSpecific.json"))
            .Where(r => Ints(r, "__col4").Contains(27)).Select(r => r.GetProperty("HASH32").GetUInt32()).ToHashSet();

        foreach (var row in Rows(Path.Combine(dir, "ItemisedVisualEffect.json")))
        {
            if (Int(row, "Mtx") is not int mtx || mtx < 0 || mtx >= gameSpecific.Count || gameSpecific[mtx] is not uint hash) continue;
            ushort visual = Int(row, "VisualIdentity") is int vi && vi >= 0 && vi < identities.Count ? identities[vi] : (ushort)0;
            ushort effect = Int(row, "VisualEffect") is int ve && ve >= 0 && ve < weaponEffects.Count ? weaponEffects[ve] : (ushort)0;
            var stats = new List<(uint, int)>();
            if (row.TryGetProperty("Stats", out var keys) && keys.ValueKind == JsonValueKind.Array
                && row.TryGetProperty("__col7", out var values) && values.ValueKind == JsonValueKind.Array)
                foreach (var (key, value) in keys.EnumerateArray().Zip(values.EnumerateArray()))
                    if (key.TryGetInt32(out int stat) && value.TryGetInt32(out int v)) stats.Add(((uint)stat + 1, v));
            effects.TryAdd(hash, new Effect(visual, effect, stats)
            {
                Skill = skillEffects.Contains(hash) && Ints(row, "__col8").Contains(27)
                    && Int(row, "EffectSlotType") is int slotType
                    ? new(slotType, Ints(row, "BaseItemTypes"), Ints(row, "ItemClasses")) : null,
            });
        }
        // Pets have no ItemisedVisualEffect row; each Pet row names its MtxTypeGameSpecific row. The server spawns the
        // pet object (PetObject); the player's pet_slot1 / pet_slot2 stat (a Pet.dat row + 1) is what the client's
        // talking-pet audio reads (0x1401093a0 → 0x14010f530).
        var petTypes = PetTypes(folder);
        int petRow = 0;
        foreach (var row in Rows(Path.Combine(dir, "Pet.json")))
        {
            petRow++;
            if (Int(row, "MtxTypeGameSpecificKey") is int mtx && mtx >= 0 && mtx < gameSpecific.Count && gameSpecific[mtx] is uint hash)
            {
                string? type = row.TryGetProperty("Id", out var id) ? id.GetString() : null;
                var (components, states) = type is not null && petTypes.TryGetValue(type, out var t) ? t : (null, null);
                effects[hash] = (effects.GetValueOrDefault(hash) ?? new Effect(0, 0, [])) with
                {
                    Pet = petRow, PetType = type, PetComponents = components, PetStates = states,
                };
            }
        }
        foreach (var row in Rows(Path.Combine(dir, "MicrotransactionCharacterPortraitVariations.json")))
            if (Int(row, "MtxTypeGameSpecific") is int mtx && mtx >= 0 && mtx < gameSpecific.Count && gameSpecific[mtx] is uint hash
                && Int(row, "__col1") is int variation and > 0 and <= ushort.MaxValue)
                effects[hash] = (effects.GetValueOrDefault(hash) ?? new Effect(0, 0, [])) with { Portrait = (ushort)variation };
        foreach (var row in Rows(Path.Combine(dir, "MicrotransactionSocialFrameVariations.json")))
            if (Int(row, "MtxTypeGameSpecific") is int mtx && mtx >= 0 && mtx < gameSpecific.Count && gameSpecific[mtx] is uint hash
                && Int(row, "Index") is int frame and > 0 and <= ushort.MaxValue)
                effects[hash] = (effects.GetValueOrDefault(hash) ?? new Effect(0, 0, [])) with { Frame = (ushort)frame };
        foreach (var row in Rows(Path.Combine(dir, "MicrotransactionSlot.json")))
            if (Int(row, "Id") is int id and > 0 and < InstanceMicrotransactions.SlotCount)
                slots[(byte)id] = new Slot(row.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "",
                    Int(row, "__col7") is int model and >= 0 and < 17 ? (byte)model : null);
        return (effects, slots);
    }

    /// <summary>The Metadata/Pet/ types' deserialized components and StateMachine defaults, as MonsterCatalog reads them.</summary>
    static Dictionary<string, (IReadOnlyList<string>?, StateMachineData?)> PetTypes(string folder)
    {
        var types = new Dictionary<string, (IReadOnlyList<string>?, StateMachineData?)>();
        string path = Path.Combine(folder, "object-types.json");
        if (!File.Exists(path)) return types;
        using var doc = GameTables.Open(path);
        foreach (var t in doc.RootElement.GetProperty("types").EnumerateArray())
        {
            string type = t.GetProperty("type").GetString() ?? "";
            if (!type.StartsWith("Metadata/Pet/", StringComparison.Ordinal)) continue;
            int read = t.GetProperty("read").GetInt32();
            StateMachineData? states = t.TryGetProperty("shared_states", out var ss) && t.TryGetProperty("shared_timers", out var st)
                ? new([.. ss.EnumerateArray().Select(e => e[1].GetInt32())], st.GetArrayLength(), [.. ss.EnumerateArray().Select(e => e[0].GetString() ?? "")])
                : null;
            types[type] = ([.. t.GetProperty("components").EnumerateArray().Take(read).Select(c => c.GetString()!)], states);
        }
        return types;
    }

    static int? Int(JsonElement row, string name) =>
        row.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int v) ? v : null;

    static List<int> Ints(JsonElement row, string name) =>
        row.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array
            ? [.. p.EnumerateArray().Select(v => v.GetInt32())] : [];

    static List<ushort>? Hash16s(string path) => File.Exists(path)
        ? [.. Rows(path).Select(r => Int(r, "HASH16") is int h ? (ushort)h : (ushort)0)]
        : null;

    static List<JsonElement> Rows(string path)
    {
        if (!File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}

/// <summary>
/// A skill's art variations: <c>skill_art_variation</c>, which a worn skill effect sets (its ItemisedVisualEffect
/// stats; SkillArtVariations' Variants names them), picks the MiscAnimated object (MiscAnimatedArtVariations) and the
/// BuffVisuals row (BuffVisualsArtVariations) the skill shows in place of its default, by the variation tables' Id and
/// Variant (Righteous Fire: <c>righteous_fire_ring</c>, <c>righteous_fire_buff_visual</c>, <c>righteous_fire_aura_visual</c>),
/// and the projectile (ProjectilesArtVariations: Fireball's <c>fireball</c> → Metadata/Projectiles/StygianFireball …).
/// </summary>
sealed class SkillArt(string folder = "data")
{
    readonly Lazy<(Dictionary<(string, int), string> Animated, Dictionary<(string, int), ushort> BuffVisuals,
        Dictionary<(string, int), string> Projectiles)> _data = new(() => Load(folder));
    readonly Lazy<Dictionary<string, int>> _radii = new(() => Radii(folder));

    /// <summary>
    /// The radius an area art is drawn at, at scale 1, in world units: MiscAnimated <c>__col4</c> of the rows drawing
    /// that .ao (RighteousFireRing 400, RighteousFireRingWhite 200, the league avatars' blue ring 380, the Vaal rings
    /// 500; 0 for art that is not scaled to an area), or null. It matches the meshes: rifire.sm's BoundingBox is
    /// ±463 with its flames at 380, rifire_white.sm's half that.
    /// </summary>
    public int? ArtRadius(string ao) => _radii.Value.TryGetValue(ao, out int r) ? r : null;

    static Dictionary<string, int> Radii(string folder)
    {
        var radii = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows(Path.Combine(folder, "tables", "MiscAnimated.json")))
            if (Int(row, "__col4") is int r and > 0 && row.GetProperty("AOFile").GetString() is { Length: > 0 } ao)
                radii[ao] = Math.Max(r, radii.GetValueOrDefault(ao));
        return radii;
    }

    /// <summary>The .ao this MiscAnimatedArtVariations Id draws for the variation, or null when it has no row (the
    /// variation draws nothing there, e.g. Righteous Fire's NoRing).</summary>
    public string? Animated(string id, int variation) => _data.Value.Animated.GetValueOrDefault((id, variation));

    /// <summary>The BuffVisuals row this BuffVisualsArtVariations Id shows for the variation, or null for the buff's own.</summary>
    public ushort? BuffVisual(string id, int variation) =>
        _data.Value.BuffVisuals.TryGetValue((id, variation), out ushort row) ? row : null;

    /// <summary>The projectile object a variation flies in place of <paramref name="projectile"/> (a Projectiles.dat Id):
    /// the ProjectilesArtVariations Id whose variant 0 is that projectile, at the variation; the projectile itself when
    /// the variation has no row there.</summary>
    public string Projectile(string projectile, int variation)
    {
        if (variation == 0) return projectile;
        var all = _data.Value.Projectiles;
        foreach (var ((id, v), p) in all)
            if (v == 0 && p == projectile && all.TryGetValue((id, variation), out var swapped)) return swapped;
        return projectile;
    }

    /// <summary>The .ao a variation draws in place of <paramref name="ao"/>, an art's default (variant 0) .ao, e.g.
    /// Fireball's explosion (<c>fireball_standard</c>); the default itself when the variation has no row there.</summary>
    public string AnimatedInPlaceOf(string ao, int variation)
    {
        if (variation == 0) return ao;
        var all = _data.Value.Animated;
        foreach (var ((id, v), a) in all)
            if (v == 0 && string.Equals(a, ao, StringComparison.OrdinalIgnoreCase) && all.TryGetValue((id, variation), out var swapped)) return swapped;
        return ao;
    }

    static (Dictionary<(string, int), string>, Dictionary<(string, int), ushort>, Dictionary<(string, int), string>) Load(string folder)
    {
        var animated = new Dictionary<(string, int), string>();
        var buffs = new Dictionary<(string, int), ushort>();
        var projectiles = new Dictionary<(string, int), string>();
        var projectileRows = Rows(Path.Combine(folder, "tables", "Projectiles.json"));
        foreach (var row in Rows(Path.Combine(folder, "tables", "ProjectilesArtVariations.json")))
            if (Int(row, "Variant") is int v && row.GetProperty("Projectiles") is { ValueKind: JsonValueKind.Array } pa && pa.GetArrayLength() > 0
                && pa[0].GetInt32() is int pr && pr < projectileRows.Count && projectileRows[pr].GetProperty("Id").GetString() is { Length: > 0 } pid)
                projectiles.TryAdd((row.GetProperty("Id").GetString() ?? "", v), pid);
        string dir = Path.Combine(folder, "tables"), miscPath = Path.Combine(dir, "MiscAnimated.json");
        var misc = File.Exists(miscPath) ? Rows(miscPath) : [];
        foreach (var row in Rows(Path.Combine(dir, "MiscAnimatedArtVariations.json")))
            if (Int(row, "Variant") is int v && row.GetProperty("Animation") is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() > 0
                && a[0].GetInt32() is int m && m < misc.Count && misc[m].GetProperty("AOFile").GetString() is { Length: > 0 } ao)
                animated.TryAdd((row.GetProperty("Id").GetString() ?? "", v), ao);
        foreach (var row in Rows(Path.Combine(dir, "BuffVisualsArtVariations.json")))
            if (Int(row, "__col2") is int v && row.GetProperty("BuffVisual") is { ValueKind: JsonValueKind.Array } b && b.GetArrayLength() > 0)
                buffs.TryAdd((row.GetProperty("Buff").GetString() ?? "", v), (ushort)b[0].GetInt32());
        return (animated, buffs, projectiles);
    }

    static int? Int(JsonElement row, string name) =>
        row.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out int v) ? v : null;

    static List<JsonElement> Rows(string path)
    {
        if (!File.Exists(path)) return [];
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())];
    }
}
