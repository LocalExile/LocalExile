using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    sealed record BearSnare(double RootUntil, double Until, int Slow, int Taken);
    sealed class BearSnares
    {
        public readonly List<BearSnare> Effects = [];
        public ushort Buff;
        public bool RootShown;
        public double ShownUntil;
        public int SentSlow;
    }
    static readonly RuleTable<SpawnedMonster, BearSnares> BearTraps = new();

    public static double BearTrapRootSeconds(int damage, uint maximumLife) => maximumLife == 0 ? 0
        : 15 * Math.Min(1d / 3, Math.Max(0, damage) / (double)maximumLife);

    public static void BearTrapOnHitLocked(SpawnedMonster monster, int damage, double now,
        Func<string, int> player, Func<string, int> own)
    {
        if (monster.Dead || monster.Life == 0 || monster.Invulnerable || damage <= 0) return;
        double rate = Math.Max(.01, monster.RecipientDebuffRate);
        double root = BearTrapRootSeconds(damage, monster.MaxLife)
            * SkillDurationModifiers.Seconds(1000, player, own) / rate;
        double tail = SkillDurationModifiers.Seconds(own("base_skill_effect_duration"), player, own) / rate;
        var state = BearTraps.GetOrCreateValue(monster);
        state.Effects.RemoveAll(e => e.Until <= now);
        state.Effects.Add(new(now + root, now + root + tail,
            Math.Clamp(-own("bear_trap_movement_speed_+%_final"), 0, 100),
            Math.Max(0, own("bear_trap_damage_taken_+%_from_traps_and_mines"))));
        // This holds walking only. No flinch, stun, action-speed change or phase grace is introduced.
        if (monster.Effects["movement_speed_cannot_be_reduced_below_base"] <= 0)
        {
            monster.Walk.BlockMovementUntil(now + root, now);
            monster.LastTarget = null;
        }
    }

    public static double BearTrapWalkFactor(SpawnedMonster monster, double now)
    {
        if (monster.Effects["movement_speed_cannot_be_reduced_below_base"] > 0
            || !BearTraps.TryGetValue(monster, out var state)) return 1;
        double slow = state.Effects.Where(e => e.Until > now)
            .Select(e => now < e.RootUntil ? e.Slow : e.Slow * (e.Until - now) / Math.Max(.001, e.Until - e.RootUntil))
            .DefaultIfEmpty().Max();
        return Math.Max(.01, 1 - slow / 100);
    }

    public static int BearTrapDamageTaken(SpawnedMonster monster, double now) =>
        BearTraps.TryGetValue(monster, out var state)
            ? state.Effects.Where(e => e.Until > now).Select(e => e.Taken).DefaultIfEmpty().Max() : 0;

    static void BearTrapChangesLocked(ClientState conn, uint id, SpawnedMonster monster, double now,
        List<(byte[] Packet, string Log)> packets)
    {
        if (!BearTraps.TryGetValue(monster, out var state)) return;
        state.Effects.RemoveAll(e => e.Until <= now || monster.Dead || monster.Life == 0);
        bool root = monster.Effects["movement_speed_cannot_be_reduced_below_base"] <= 0
            && state.Effects.Any(e => e.RootUntil > now);
        double until = state.Effects.Select(e => root ? e.RootUntil : e.Until).DefaultIfEmpty().Max();
        if (state.Buff != 0 && (until != state.ShownUntil || root != state.RootShown))
        {
            packets.Add((InstanceEntities.RemoveBuff(id, state.Buff), "Bear Trap debuff changes"));
            state.Buff = 0;
        }
        if (until > now && state.Buff == 0)
        {
            state.RootShown = root; state.ShownUntil = until;
            packets.Add((InstanceEntities.AddBuff(id, state.Buff = conn.NextBuff++, root ? (ushort)162 : (ushort)163,
                PlayerId, u68: BitConverter.SingleToUInt32Bits((float)(until - now))), "Bear Trap debuff"));
        }
        int slow = (int)Math.Round(100 * (BearTrapWalkFactor(monster, now) - 1));
        if (slow != state.SentSlow && Stats.Key("bear_trap_movement_speed_+%_final") is uint key)
        {
            packets.Add((InstanceEntities.StatsUpdate(id, [(key, slow)]), "Bear Trap movement speed"));
            state.SentSlow = slow;
        }
        if (state.Effects.Count == 0) BearTraps.Remove(monster);
    }
}
