/// <summary>Unconditional impale inputs, kept separate from final panel aliases and target mitigation.</summary>
public static class ImpaleStats
{
    public static bool IsDisplay(uint stat) => stat is Stat.VirtualTotalMainHandImpaleOnHitPctChance
        or Stat.VirtualTotalOffHandImpaleOnHitPctChance or Stat.VirtualTotalSpellImpaleOnHitPctChance;

    public readonly record struct Settings(int Chance, int EffectPercent = 0,
        int Hits = ImpaleState.HitsPerStack, double Duration = ImpaleState.BaseDuration)
    {
        public bool TheImpaler { get; init; }
    }

    public static Settings From(IReadOnlyDictionary<uint, int> character, StatCatalog catalog, bool attack,
        IReadOnlyDictionary<uint, int>? own = null, IReadOnlyDictionary<uint, int>? weaponLocal = null,
        bool critical = false)
    {
        long Get(uint key) => (long)character.GetValueOrDefault(key) + (own?.GetValueOrDefault(key) ?? 0);
        long chance = Get(Stat.ImpaleOnHitPctChance) + (attack
            ? Get(Stat.AttacksImpaleOnHitPctChance) + (weaponLocal?.GetValueOrDefault(Stat.LocalAttacksImpaleOnHitPctChance) ?? 0)
            : Get(Stat.SpellsImpaleOnHitPctChance) + (critical ? Get(Stat.SpellImpaleOnCritPctChance) : 0));
        long effect = Get(Stat.ImpaleDebuffEffectPlusPct) + (attack ? 0 : Get(Stat.ImpaleEffectPlusPctForImpalesInfictedBySpells));
        return new((int)Math.Clamp(chance, 0, 100), (int)Math.Clamp(effect, int.MinValue, int.MaxValue),
            (int)Math.Clamp(ImpaleState.HitsPerStack + Get(Stat.ImpaledDebuffNumberOfReflectedHits), 0, int.MaxValue),
            ImpaleState.BaseDuration * Math.Max(0, 1 + Get(Stat.ImpaledDebuffDurationPlusPct) / 100.0))
        {
            TheImpaler = character.GetValueOrDefault(Stat.KeystoneImpale) != 0
        };
    }

    public static int AddReflected(int direct, int reflected) => (int)Math.Clamp((long)direct + reflected, 0, int.MaxValue);
}
