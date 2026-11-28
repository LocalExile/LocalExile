using GameServer.Core.Protocol.Instance;

/// <summary>
/// Resource indices matching <c>VillageResources.dat</c> rows 0..20.
/// </summary>
public enum VillageResourceKind : byte
{
    PendingIronOre = 0,
    PendingCopperOre = 1,
    PendingAmber = 2,
    PendingBismuthOre = 3,
    PendingVerisiumOre = 4,

    IronOre = 5,
    CopperOre = 6,
    Amber = 7,
    BismuthOre = 8,
    VerisiumOre = 9,

    Wheat = 10,
    Corn = 11,
    Pumpkin = 12,
    Globulora = 13,
    Orbisfera = 14,

    RefinedIron = 15,
    RefinedCopper = 16,
    RefinedAmber = 17,
    RefinedBismuth = 18,
    RefinedVerisium = 19,

    MagicDust = 20,
}

/// <summary>
/// Sink that accepts map-side Settlers grants (pending ore, smelted bars, crops, Thaumaturgic Dust)
/// and forwards them to the account/league Kingsmarch village state.
/// </summary>
public interface IVillageOreSink
{
    /// <summary>Adds <paramref name="amount"/> of <paramref name="resource"/> to <paramref name="player"/>'s town.</summary>
    void GrantResource(Player player, VillageResourceKind resource, long amount, string source);
}

/// <summary>
/// Cross-module hooks for Settlers map-side resource grants (ore, bars, crops, dust) and Refiner's Bargain
/// integrations (Delve Sulphite, Harvest Lifeforce, Torment possessed drops) without coupling modules directly.
/// </summary>
public static class VillageResourceHooks
{
    static volatile IVillageOreSink? _sink;
    static volatile Func<double>? _oreFoundMultiplier;
    static volatile Func<Player, int, MapMods?, bool>? _sulphiteInterceptor;
    static volatile Action<Player, int, MapMods?>? _harvestCropGrant;
    static volatile Func<Player, List<ItemDrops.Drop>, int, Random, long>? _possessedDustConverter;

    public static IVillageOreSink? Sink => _sink;

    /// <summary>Configured multiplier for map-found ores (defaults to 1.0 retail).</summary>
    public static double OreFoundMultiplier
    {
        get
        {
            double value = _oreFoundMultiplier?.Invoke() ?? 1.0;
            return double.IsFinite(value) && value > 0 ? value : 1.0;
        }
    }

    public static IDisposable RegisterSink(IVillageOreSink sink, Func<double>? oreFoundMultiplier = null)
    {
        _sink = sink;
        if (oreFoundMultiplier is not null) _oreFoundMultiplier = oreFoundMultiplier;
        return new Cleanup(() =>
        {
            if (ReferenceEquals(_sink, sink)) _sink = null;
            if (oreFoundMultiplier is not null && ReferenceEquals(_oreFoundMultiplier, oreFoundMultiplier))
                _oreFoundMultiplier = null;
        });
    }

    public static IDisposable RegisterOreFoundMultiplier(Func<double> multiplier)
    {
        _oreFoundMultiplier = multiplier;
        return new Cleanup(() =>
        {
            if (ReferenceEquals(_oreFoundMultiplier, multiplier)) _oreFoundMultiplier = null;
        });
    }

    public static IDisposable RegisterRefinersBargain(
        Func<Player, int, MapMods?, bool> sulphiteInterceptor,
        Action<Player, int, MapMods?> harvestCropGrant,
        Func<Player, List<ItemDrops.Drop>, int, Random, long> possessedDustConverter)
    {
        _sulphiteInterceptor = sulphiteInterceptor;
        _harvestCropGrant = harvestCropGrant;
        _possessedDustConverter = possessedDustConverter;
        return new Cleanup(() =>
        {
            if (ReferenceEquals(_sulphiteInterceptor, sulphiteInterceptor)) _sulphiteInterceptor = null;
            if (ReferenceEquals(_harvestCropGrant, harvestCropGrant)) _harvestCropGrant = null;
            if (ReferenceEquals(_possessedDustConverter, possessedDustConverter)) _possessedDustConverter = null;
        });
    }

    /// <summary>
    /// Grants a Settlers resource to <paramref name="player"/> through the registered <see cref="IVillageOreSink"/>,
    /// falling back to character module data storage if Kingsmarch town state is not yet attached.
    /// </summary>
    public static void GrantResource(Player player, VillageResourceKind resource, long amount, string source)
    {
        if (amount <= 0) return;
        if (_sink is { } sink)
        {
            sink.GrantResource(player, resource, amount, source);
            return;
        }

        // Fallback persistence in character module data so grants are never lost if map-ores runs standalone.
        string key = $"settlers.resource.{(int)resource}";
        CharacterSaves.ChangeModuleData(Program.CharacterName(player), key, old =>
        {
            long current = long.TryParse(old, out long v) ? v : 0;
            return checked(current + amount).ToString(System.Globalization.CultureInfo.InvariantCulture);
        });
    }

    /// <summary>
    /// Intercepts a Delve Sulphite deposit claim when Refiner's Bargain (<c>map_sulphite_grants_random_ore_instead</c>) is active.
    /// Returns true when the Sulphite grant was converted to random Settlers ore/bars.
    /// </summary>
    public static bool TryInterceptSulphite(Player player, int sulphiteAmount, MapMods? mods)
    {
        if (mods is null || mods.Get(Stat.MapSulphiteGrantsRandomOreInstead) <= 0) return false;
        return _sulphiteInterceptor?.Invoke(player, sulphiteAmount, mods) == true;
    }

    /// <summary>
    /// Grants random Kingsmarch crops when a Harvest monster dies under Refiner's Bargain
    /// (<c>map_harvest_monsters_grant_random_crops_instead</c>).
    /// </summary>
    public static void OnHarvestRefinersBargain(Player player, int lifeforceAmount, MapMods? mods)
    {
        if (lifeforceAmount <= 0 || mods is null || mods.Get(Stat.MapHarvestMonstersGrantRandomCropsInstead) <= 0) return;
        _harvestCropGrant?.Invoke(player, lifeforceAmount, mods);
    }

    /// <summary>
    /// Converts eligible equipment drops from a Possessed monster into Thaumaturgic Dust when Refiner's Bargain
    /// (<c>map_possessed_monster_equipment_drops_converted_to_dust_%_chance</c>) is active.
    /// Returns total dust granted.
    /// </summary>
    public static long ConvertPossessedDrops(Player player, List<ItemDrops.Drop> drops, int chancePct, Random rng)
    {
        if (chancePct <= 0 || drops.Count == 0) return 0;
        return _possessedDustConverter?.Invoke(player, drops, chancePct, rng) ?? 0;
    }

    sealed class Cleanup(Action clean) : IDisposable
    {
        public void Dispose() => clean();
    }
}
