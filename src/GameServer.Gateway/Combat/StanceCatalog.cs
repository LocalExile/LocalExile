/// <summary>
/// The character's one shared stance. Blood and Sand and Flesh and Stone are independent reservation sources, but both
/// publish the same Blood/Sand selection to the player. Blood is the implicit default when neither source is active.
/// </summary>
static class StanceCatalog
{
    public const string BloodAndSand = "BloodAndSand";
    public const string FleshAndStone = "FleshAndStone";
    public const string Blood = "BloodStance";
    public const string Sand = "SandStance";
    public const string BloodSelection = "Blood";
    public const string SandSelection = "Sand";

    /// <summary>The stable GrantedEffects family of either stance source.</summary>
    public static string SourceFamily(string? effectId) => effectId switch
    {
        BloodAndSand or Blood or Sand => BloodAndSand,
        FleshAndStone => FleshAndStone,
        _ => "",
    };

    public static bool IsSource(string? effectId) => SourceFamily(effectId).Length > 0;
    public static bool IsStance(string? effectId) => IsSource(effectId);

    /// <summary>The runtime EffectId of one owner. Blood and Sand keeps its historical alias; Flesh and Stone keeps its
    /// actual gem effect id and carries the selection in <see cref="AuraCatalog.Aura.Stance"/>.</summary>
    public static string RuntimeEffectId(string? sourceEffectId, string selected) => SourceFamily(sourceEffectId) switch
    {
        BloodAndSand => selected == SandSelection ? Sand : Blood,
        FleshAndStone => FleshAndStone,
        _ => sourceEffectId ?? "",
    };

    public static string Selection(string selected) => selected == SandSelection ? SandSelection : BloodSelection;

    public static string PersistedSelection(string? effectId, string? canonical) => effectId switch
    {
        Sand => SandSelection,
        Blood => BloodSelection,
        _ => Selection(canonical ?? BloodSelection),
    };

    public static string SelectionOf(AuraCatalog.Aura aura, string? fallback = null) =>
        SourceFamily(aura.EffectId) == BloodAndSand && aura.EffectId == Sand ? SandSelection
        : Selection(aura.Stance ?? fallback ?? BloodSelection);

    public static string Selected(IEnumerable<AuraCatalog.Aura> auras) =>
        auras.FirstOrDefault(a => IsStance(a.EffectId) && SelectionOf(a) == SandSelection) is not null
            ? SandSelection : BloodSelection;

    public static string RestoredSelection(IEnumerable<string> saved) =>
        saved.Any(id => SourceFamily(id) == BloodAndSand && id == Sand) ? SandSelection : BloodSelection;

    public static string RestoredSelection(string? canon, IEnumerable<string> saved) =>
        canon is BloodSelection or SandSelection ? canon : RestoredSelection(saved);

    public static string Toggle(string selected) => Selection(selected) == SandSelection ? BloodSelection : SandSelection;
}
