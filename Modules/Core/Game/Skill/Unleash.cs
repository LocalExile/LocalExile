public static partial class Game
{
    sealed class SealProgress(SupportedGem source, double now)
    {
        public readonly SupportedGem Source = source;
        public double Since = now;
    }

    static readonly Store<Player, Dictionary<ushort, SealProgress>> Seals = new();

    static void RemoveMissingSealSources(Player player, IEnumerable<ushort> ids)
    {
        if (!Seals.TryGetValue(player, out var tracked)) return;
        lock (tracked)
            foreach (ushort missing in tracked.Keys.Except(ids).ToArray()) tracked.Remove(missing);
    }

    public static int UnleashSeals(Player player, ushort id, SupportedGem source, double now, bool spend = false)
    {
        int cap = GolemStat(source.Stats, Stat.SupportAnticipationRapidFireCount);
        int milliseconds = GolemStat(source.Stats, Stat.SupportAnticipationChargeGainIntervalMs);
        if (!source.RequirementsMet || cap <= 0 || milliseconds <= 0)
        {
            if (Seals.TryGetValue(player, out var old))
                lock (old) old.Remove(id);
            return 0;
        }
        var playerStats = CharacterPlayerStats(player);
        cap = (int)Math.Clamp((long)cap + GolemStat(playerStats, Stat.SupportAnticipationRapidFireCount), 0, int.MaxValue);
        const uint frequency = Stat.SupportAnticipationChargeGainFrequencyPlusPct;
        double rate = Math.Max(0, 1 + ((long)GolemStat(playerStats, frequency) + GolemStat(source.Stats, frequency)) / 100d);
        var tracked = Seals.GetOrCreateValue(player);
        lock (tracked)
        {
            if (!tracked.TryGetValue(id, out var progress) || !SameTriggerSource(progress.Source, source))
                tracked[id] = progress = new(source, now);
            double interval = rate == 0 ? double.PositiveInfinity : milliseconds / (1000d * rate);
            int count = (int)Math.Min(cap, Math.Floor(Math.Max(0, now - progress.Since) / interval + 1e-9));
            if (spend && count > 0) progress.Since = now;
            return count;
        }
    }
}
