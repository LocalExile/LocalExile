public static partial class Game
{
    static void ApplyAbyssGaze(Dictionary<uint, int> sources)
    {
        Add(Stat.ArcaneSurgeEffectOnSelfPlusPctPerCasterAbyssJewelUpToPlus40Pct,
            Stat.NumberOfCasterAbyssJewels, Stat.ArcaneSurgeEffectPlusPct, 40);
        Add(Stat.ProjecitleChanceToBeAbleToChainFromTerrainPctPerRangedAbyssJewelUpTo20Pct,
            Stat.NumberOfRangedAbyssJewels, Stat.ProjectileChainFromTerrainChancePct, 20);

        void Add(uint input, uint count, uint target, int cap)
        {
            int coefficient = sources.GetValueOrDefault(input);
            if (coefficient <= 0) return;
            int jewels = Math.Max(0, sources.GetValueOrDefault(count));
            long bonus = Math.Min(cap, (long)jewels * coefficient);
            if (bonus > 0) sources[target] = ActorScale.Saturate(sources.GetValueOrDefault(target) + bonus);
        }
    }
}
