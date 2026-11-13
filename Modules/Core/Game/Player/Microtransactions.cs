using System.Text.Json;
using GameServer.Core.Protocol.Instance;
using GameServer.Core;


/// <summary>
/// Every microtransaction in the game, owned by every account: one 0x1f4 record per <c>MtxTypes</c> row the client can
/// resolve, which the client's Microtransactions panel (N) and the store's ALREADY OWNED marks read
/// (23-microtransactions.md). A record's <c>MtxTypeHash</c> is the row's HASH32 and its <c>Id</c> the row index + 1,
/// unique per record as the client's model needs. The panel looks each hash up in <c>MTXTypeGameSpecific.dat</c>
/// (0x1408e1550 → 0x142228b00), and a hash that is not there leaves a null the row build faults on (live 2026-10-23:
/// all 8411 MtxTypes rows crashed the client; 690 of them have no MtxTypeGameSpecific row), so only rows with one are
/// sent. Names stay empty: the client names a record from its catalogue.
/// Game data, loaded on first use; without the client tables the list is empty.
/// </summary>
public sealed class MtxCatalog
{
    private readonly Lazy<List<InstanceMicrotransactions.Record>> _owned = new(Load);
    private Dictionary<uint, InstanceMicrotransactions.Record>? _byId;

    public IReadOnlyList<InstanceMicrotransactions.Record> Owned => _owned.Value;

    /// <summary>The owned record with this id (what 0x1f9 picks), or null.</summary>
    public InstanceMicrotransactions.Record? Find(uint id) =>
        (_byId ??= Owned.ToDictionary(r => r.Id)).GetValueOrDefault(id);

    /// <summary>The owned record of a MtxTypes HASH32, or null.</summary>
    public InstanceMicrotransactions.Record? ByHash(uint hash) => Owned.FirstOrDefault(r => r.MtxTypeHash == hash);

    /// <summary>The owned record a saved pick names: by its hash, or for a pick a 3.29.3b build saved (no hash) by its
    /// record moved to the 3.29.3c MtxTypes rows (<see cref="CosmeticMigration"/>). Null when it no longer exists.</summary>
    public InstanceMicrotransactions.Record? Resolve(CharacterSaves.SavedCosmetic saved) =>
        saved.Hash is uint hash ? ByHash(hash)
        : CosmeticMigration.Record3293c(saved.Record) is uint record ? Find(record) : null;

    /// <summary>
    /// The worn table 0x1f3 carries for <paramref name="worn"/> (MicrotransactionSlot Id → saved pick): a pick whose
    /// record no longer exists (another game version's data) is left out, so the client never gets a hash it cannot
    /// resolve.
    /// </summary>
    /// The Portal slot's choice of random effects (record 0, option 1 or 2) is an entry with no cosmetic and its option.
    public List<InstanceMicrotransactions.Equipped> Equipped(IReadOnlyDictionary<byte, CharacterSaves.SavedCosmetic> worn) =>
        [.. worn.Where(w => w.Key < InstanceMicrotransactions.SlotCount)
            .Select(w => (w.Key, w.Value, Record: Resolve(w.Value)))
            .Where(w => w.Record is not null || (w.Key == InstanceMicrotransactions.PortalSlot && w.Value is { Record: 0, Option: 1 or 2 }))
            .OrderBy(w => w.Key)
            .Select(w => w.Record is null ? new InstanceMicrotransactions.Equipped(w.Key, 0, 0, 0, w.Value.Option)
                : new InstanceMicrotransactions.Equipped(w.Key, w.Record.Id, w.Record.MtxTypeHash, w.Record.Qword, w.Value.Option))];

    static List<InstanceMicrotransactions.Record> Load()
    {
        var resolvable = Hashes("MtxTypeGameSpecific");
        var types = Hashes("MtxTypes");
        if (resolvable is null || types is null) return [];
        var list = new List<InstanceMicrotransactions.Record>();
        for (int row = 0; row < types.Count; row++)
            if (types[row] is uint h && resolvable.Contains(h))
                list.Add(new InstanceMicrotransactions.Record(h, (uint)row + 1));
        return list;
    }

    /// <summary>Each row's HASH32 in table order (null for a row without one), or null without the table.</summary>
    public static List<uint?>? Hashes(string table) =>
        ClientTables.TableOrNull(table) is { } rows ? [.. rows.Select(r => r.LongOrNull("HASH32") is long h ? (uint)h : (uint?)null)] : null;
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
/// Game data, loaded on first use; without the client tables nothing applies.
/// </summary>
public sealed class CosmeticEffects(string folder = "data")
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

        /// <summary>The ItemClasses rows of the items this cosmetic dresses (a quiver skin: Quiver; a "Bow and Quiver"
        /// skin: both), empty for any. A skin or weapon effect is drawn only on an item of one of them.</summary>
        public IReadOnlyList<int> ItemClasses { get; init; } = [];

        /// <summary>Whether the cosmetic dresses an item of this ItemClasses row.</summary>
        public bool Fits(int classRow) => ItemClasses.Count == 0 || ItemClasses.Contains(classRow);

        /// <summary>A portrait's variation (MicrotransactionCharacterPortraitVariations <c>__col1</c>), 0 otherwise: the
        /// Player component's u16 at +0x1aa and the character list's U46.</summary>
        public ushort Portrait { get; init; }

        /// <summary>A portrait frame's variation (MicrotransactionSocialFrameVariations <c>Index</c>), 0 otherwise: the
        /// client draws <c>Art/2DArt/UIImages/Common/MTX/SupporterIconFrame{Frame}</c> around the portrait.</summary>
        public ushort Frame { get; init; }
    }

    public sealed record SkillRestriction(int SlotType, IReadOnlyList<int> Gems, IReadOnlyList<int> Classes);

    // The same eligibility fields the client checks in 0x1424b2cb0. In particular a record from a stale picker
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
        var effects = new Dictionary<uint, Effect>();
        var slots = new Dictionary<byte, Slot>();
        var gameSpecific = MtxCatalog.Hashes("MtxTypeGameSpecific");
        var identities = Hash16s("ItemVisualIdentity");
        var weaponEffects = Hash16s("ItemVisualEffect");
        if (gameSpecific is null || identities is null || weaponEffects is null) return (effects, slots);
        var skillEffects = Rows("MtxTypeGameSpecific")
            .Where(r => r.Ints("__col4").Contains(27)).Select(r => r.UInt("HASH32")).ToHashSet();

        foreach (var row in Rows("ItemisedVisualEffect"))
        {
            if (Int(row, "Mtx") is not int mtx || mtx < 0 || mtx >= gameSpecific.Count || gameSpecific[mtx] is not uint hash) continue;
            ushort visual = Int(row, "VisualIdentity") is int vi && vi >= 0 && vi < identities.Count ? identities[vi] : (ushort)0;
            ushort effect = Int(row, "VisualEffect") is int ve && ve >= 0 && ve < weaponEffects.Count ? weaponEffects[ve] : (ushort)0;
            var stats = new List<(uint, int)>();
            foreach (var (stat, v) in row.Ints("Stats").Zip(row.Ints("__col7"))) stats.Add(((uint)stat + 1, v));
            effects.TryAdd(hash, new Effect(visual, effect, stats)
            {
                ItemClasses = row.Ints("ItemClasses"),
                Skill = skillEffects.Contains(hash) && row.Ints("__col8").Contains(27)
                    && Int(row, "EffectSlotType") is int slotType
                    ? new(slotType, row.Ints("BaseItemTypes"), row.Ints("ItemClasses")) : null,
            });
        }
        // Pets have no ItemisedVisualEffect row; each Pet row names its MtxTypeGameSpecific row. The server spawns the
        // pet object (PetObject); the player's pet_slot1 / pet_slot2 stat (a Pet.dat row + 1) is what the client's
        // talking-pet audio reads (0x1401096e0 → 0x14010f890).
        var petTypes = PetTypes();
        int petRow = 0;
        foreach (var row in Rows("Pet"))
        {
            petRow++;
            if (Int(row, "MtxTypeGameSpecificKey") is int mtx && mtx >= 0 && mtx < gameSpecific.Count && gameSpecific[mtx] is uint hash)
            {
                string? type = row.TextOrNull("Id");
                var (components, states) = type is not null && petTypes.TryGetValue(type, out var t) ? t : (null, null);
                effects[hash] = (effects.GetValueOrDefault(hash) ?? new Effect(0, 0, [])) with
                {
                    Pet = petRow, PetType = type, PetComponents = components, PetStates = states,
                };
            }
        }
        foreach (var row in Rows("MicrotransactionCharacterPortraitVariations"))
            if (Int(row, "MtxTypeGameSpecific") is int mtx && mtx >= 0 && mtx < gameSpecific.Count && gameSpecific[mtx] is uint hash
                && Int(row, "__col1") is int variation and > 0 and <= ushort.MaxValue)
                effects[hash] = (effects.GetValueOrDefault(hash) ?? new Effect(0, 0, [])) with { Portrait = (ushort)variation };
        foreach (var row in Rows("MicrotransactionSocialFrameVariations"))
            if (Int(row, "MtxTypeGameSpecific") is int mtx && mtx >= 0 && mtx < gameSpecific.Count && gameSpecific[mtx] is uint hash
                && Int(row, "Index") is int frame and > 0 and <= ushort.MaxValue)
                effects[hash] = (effects.GetValueOrDefault(hash) ?? new Effect(0, 0, [])) with { Frame = (ushort)frame };
        foreach (var row in Rows("MicrotransactionSlot"))
            if (Int(row, "Id") is int id and > 0 and < InstanceMicrotransactions.SlotCount)
                slots[(byte)id] = new Slot(row.TextOrNull("Name") ?? "",
                    Int(row, "__col7") is int model and >= 0 and < 17 ? (byte)model : null);
        return (effects, slots);
    }

    /// <summary>The Metadata/Pet/ types' deserialized components and StateMachine defaults, as MonsterCatalog reads them.</summary>
    static Dictionary<string, (IReadOnlyList<string>?, StateMachineData?)> PetTypes()
    {
        var types = new Dictionary<string, (IReadOnlyList<string>?, StateMachineData?)>();
        var sharedStates = ObjectTypes.SharedStates();
        foreach (var (type, t) in ObjectTypes.All())
            if (type.StartsWith("Metadata/Pet/", StringComparison.Ordinal))
                types[type] = (ObjectTypes.Components(t), ObjectTypes.StateMachine(t, sharedStates));
        return types;
    }

    static int? Int(ClientRow row, string name) => row.LongOrNull(name) is long v and >= int.MinValue and <= int.MaxValue ? (int)v : null;

    static List<ushort>? Hash16s(string table) => ClientTables.TableOrNull(table) is { } rows
        ? [.. rows.Select(r => Int(r, "HASH16") is int h ? (ushort)h : (ushort)0)]
        : null;

    static IReadOnlyList<ClientRow> Rows(string table) => ClientTables.TableOrNull(table) ?? (IReadOnlyList<ClientRow>)[];
}

/// <summary>
/// A skill's art variations: <c>skill_art_variation</c>, which a worn skill effect sets (its ItemisedVisualEffect
/// stats; SkillArtVariations' Variants names them), picks the MiscAnimated object (MiscAnimatedArtVariations) and the
/// BuffVisuals row (BuffVisualsArtVariations) the skill shows in place of its default, by the variation tables' Id and
/// Variant (Righteous Fire: <c>righteous_fire_ring</c>, <c>righteous_fire_buff_visual</c>, <c>righteous_fire_aura_visual</c>),
/// and the projectile (ProjectilesArtVariations: Fireball's <c>fireball</c> → Metadata/Projectiles/StygianFireball …).
/// </summary>
public sealed class SkillArt
{
    readonly Lazy<(Dictionary<(string, int), string> Animated, Dictionary<(string, int), ushort> BuffVisuals,
        Dictionary<(string, int), string> Projectiles)> _data = new(Load);
    readonly Lazy<Dictionary<string, int>> _radii = new(Radii);
    readonly Lazy<Dictionary<string, int>> _miscRows = new(() => Rows("MiscAnimated")
        .Select(row => (Id: row.TextOrNull("Id"), Row: row.Index))
        .Where(r => r.Id is { Length: > 0 }).GroupBy(r => r.Id!).ToDictionary(g => g.Key, g => g.First().Row));

    /// <summary>The MiscAnimated row of this Id, or null: what an Animated component sends in place of an .ao path.</summary>
    public int? MiscAnimatedRow(string id) => _miscRows.Value.TryGetValue(id, out int row) ? row : null;

    /// <summary>
    /// The radius an area art is drawn at, at scale 1, in world units: MiscAnimated <c>__col4</c> of the rows drawing
    /// that .ao (RighteousFireRing 400, RighteousFireRingWhite 200, the league avatars' blue ring 380, the Vaal rings
    /// 500; 0 for art that is not scaled to an area), or null. It matches the meshes: rifire.sm's BoundingBox is
    /// ±463 with its flames at 380, rifire_white.sm's half that.
    /// </summary>
    public int? ArtRadius(string ao) => _radii.Value.TryGetValue(ao, out int r) ? r : null;

    static Dictionary<string, int> Radii()
    {
        var radii = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows("MiscAnimated"))
            if (Int(row, "__col4") is int r and > 0 && row.Text("AOFile") is { Length: > 0 } ao)
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

    static (Dictionary<(string, int), string>, Dictionary<(string, int), ushort>, Dictionary<(string, int), string>) Load()
    {
        var animated = new Dictionary<(string, int), string>();
        var buffs = new Dictionary<(string, int), ushort>();
        var projectiles = new Dictionary<(string, int), string>();
        var projectileRows = Rows("Projectiles");
        foreach (var row in Rows("ProjectilesArtVariations"))
            if (Int(row, "Variant") is int v && row.Ints("Projectiles") is [int pr, ..] && pr >= 0 && pr < projectileRows.Count
                && projectileRows[pr].Text("Id") is { Length: > 0 } pid)
                projectiles.TryAdd((row.Text("Id") ?? "", v), pid);
        var misc = Rows("MiscAnimated");
        foreach (var row in Rows("MiscAnimatedArtVariations"))
            if (Int(row, "Variant") is int v && row.Ints("Animation") is [int m, ..] && m >= 0 && m < misc.Count
                && misc[m].Text("AOFile") is { Length: > 0 } ao)
                animated.TryAdd((row.Text("Id") ?? "", v), ao);
        foreach (var row in Rows("BuffVisualsArtVariations"))
            if (Int(row, "__col2") is int v && row.Ints("BuffVisual") is [int b, ..])
                buffs.TryAdd((row.Text("Buff") ?? "", v), (ushort)b);
        return (animated, buffs, projectiles);
    }

    static int? Int(ClientRow row, string name) => row.LongOrNull(name) is long v and >= int.MinValue and <= int.MaxValue ? (int)v : null;

    static IReadOnlyList<ClientRow> Rows(string table) => ClientTables.TableOrNull(table) ?? (IReadOnlyList<ClientRow>)[];
}
