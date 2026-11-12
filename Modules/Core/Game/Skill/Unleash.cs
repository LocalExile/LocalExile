using System.Runtime.CompilerServices;

public static partial class Game
{
    sealed class SealProgress(SupportedGem source, double now)
    {
        public readonly SupportedGem Source = source;
        public double Since = now;
    }

    static readonly ConditionalWeakTable<ClientState, Dictionary<ushort, SealProgress>> Seals = new();

    static void RemoveMissingSealSources(ClientState conn, IEnumerable<ushort> ids)
    {
        if (!Seals.TryGetValue(conn, out var tracked)) return;
        lock (tracked)
            foreach (ushort missing in tracked.Keys.Except(ids).ToArray()) tracked.Remove(missing);
    }

    public static int UnleashSeals(ClientState conn, ushort id, SupportedGem source, double now, bool spend = false)
    {
        int cap = GolemStat(source.Stats, "support_anticipation_rapid_fire_count");
        int milliseconds = GolemStat(source.Stats, "support_anticipation_charge_gain_interval_ms");
        if (!source.RequirementsMet || cap <= 0 || milliseconds <= 0)
        {
            if (Seals.TryGetValue(conn, out var old))
                lock (old) old.Remove(id);
            return 0;
        }
        var tracked = Seals.GetOrCreateValue(conn);
        lock (tracked)
        {
            if (!tracked.TryGetValue(id, out var progress) || !SameTriggerSource(progress.Source, source))
                tracked[id] = progress = new(source, now);
            double interval = milliseconds / 1000d;
            int count = (int)Math.Min(cap, Math.Floor(Math.Max(0, now - progress.Since) / interval + 1e-9));
            if (spend && count > 0) progress.Since = now;
            return count;
        }
    }
}
