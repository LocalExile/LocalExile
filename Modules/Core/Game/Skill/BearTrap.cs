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
        // The monster AI has stopped the walk a new hold found (BearTrapHoldStarts).
        public bool HoldHandled;
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
            // The helmet enchantment's additional increase is the player's (Mods EnchantmentBearTrapDamageTaken1/2).
            Math.Max(0, own("bear_trap_damage_taken_+%_from_traps_and_mines")
                + player("bear_trap_additional_damage_taken_+%_from_traps_and_mines"))));
        // This holds walking only. No flinch, stun, action-speed change or phase grace is introduced. The walk under way
        // is stopped by the monster AI (BearTrapHoldStarts), as the client's is.
        if (monster.Effects["movement_speed_cannot_be_reduced_below_base"] <= 0)
        {
            monster.Walk.BlockMovementUntil(now + root, now);
            state.HoldHandled = false;
        }
    }

    /// <summary>True once per new hold, for the monster AI to stop the walk or chase under way as it does a freeze's.</summary>
    public static bool BearTrapHoldStarts(SpawnedMonster monster, double now)
    {
        if (!monster.Walk.MovementBlocked(now) || !BearTraps.TryGetValue(monster, out var state) || state.HoldHandled) return false;
        state.HoldHandled = true;
        return true;
    }

    public static double BearTrapWalkFactor(SpawnedMonster monster, double now)
    {
        if (monster.Effects["movement_speed_cannot_be_reduced_below_base"] > 0
            || !BearTraps.TryGetValue(monster, out var state)) return 1;
        double slow = state.Effects.Where(e => e.Until > now)
            .Select(e => now < e.RootUntil ? e.Slow : e.Slow * (e.Until - now) / Math.Max(.001, e.Until - e.RootUntil))
            .DefaultIfEmpty().Max();
        // In 20% steps: each step is a stats message that re-lays the client's walk from its cell (07, the lockstep tick),
        // and 5% steps (16 in 3 s) made a predictive client's monster visibly jitter (live 2026-11-19).
        return Math.Max(.01, 1 - Math.Ceiling(slow / 20 - 1e-9) * 20 / 100);
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
        if (state.Effects.Count == 0) BearTraps.Remove(monster);
    }
}
