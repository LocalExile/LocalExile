using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const string CrabAspect = "AspectOfTheCrab";
    static AuraCatalog.Aura? CrabRoot(Player conn) => conn.PlayerLife > 0
        ? conn.Auras.Values.FirstOrDefault(a => a.EffectId == CrabAspect) : null;
    public static bool CrabActive(Player conn) => CrabRoot(conn) is { } root
        && conn.Reservations.CrabOwner == new ReservationState.CatAspectOwner(root.SkillId, root.Buff);

    static void EndCrabAspectLocked(Player conn, AuraCatalog.Aura aura)
    {
        var state = conn.Reservations;
        if (state.CrabOwner != new ReservationState.CatAspectOwner(aura.SkillId, aura.Buff)) return;
        state.CrabOwner = null;
        state.CrabBarriers = state.CrabMaximum = 0;
        state.NextCrabBarrier = 0;
        state.CrabLastLoss = double.NegativeInfinity;
        state.CrabBuff = 0;
        state.AspectRevision++; MarkPlayerStats(conn, PlayerStatStep.AvianCrab);
        conn.AuraStatsDirty = true;
    }

    public static bool CrabReconcileLocked(Player conn, double now,
        IReadOnlyDictionary<uint, int>? raw = null, Random? random = null)
    {
        var state = conn.Reservations;
        var root = CrabRoot(conn);
        if (root is null)
        {
            if (state.CrabOwner is not { } old) return false;
            EndCrabAspectLocked(conn, new(old.Skill, 0, CrabAspect, "", old.RootBuff, 1046, 0, 0));
            return true;
        }
        raw ??= CharacterStatSources(conn).Stats;
        int Get(uint name) => AspectInput(conn, raw, name);
        var wanted = new ReservationState.CatAspectOwner(root.SkillId, root.Buff);
        bool changed = state.CrabOwner != wanted;
        if (changed)
        {
            state.CrabOwner = wanted;
            state.CrabBarriers = 0;
            state.CrabLastLoss = double.NegativeInfinity;
            state.NextCrabBarrier = now + .5;
            state.CrabBuff = conn.NextBuff++;
        }
        var rows = root.SkillStats ?? SkillsFor(conn).EffectStats(root.Effect, root.Level, root.Quality);
        int maximum = Stat.MaxCrabAspectStacks is var key ? rows.FirstOrDefault(p => p.Key == key).Value : 0;
        maximum = (int)Math.Clamp((long)maximum + Get(Stat.CrabAspectCrabBarrierMaxPlus), 0, int.MaxValue);
        changed |= state.CrabMaximum != maximum;
        state.CrabMaximum = maximum;
        int stacks = Math.Min(state.CrabBarriers, maximum);
        if (now >= state.NextCrabBarrier)
        {
            long ticks = (long)Math.Floor((now - state.NextCrabBarrier + 1e-9) / .5) + 1;
            state.NextCrabBarrier += ticks * .5;
            int chance = Get(Stat.ChanceToGainMaxCrabStacksWhenYouWouldGainACrabStackPct);
            long gains = Math.Min(ticks, (long)maximum - stacks);
            if (chance <= 0) stacks += (int)gains;
            else for (long i = 0; i < gains && stacks < maximum; i++)
                stacks = RandomRolls.Chance(chance, 100, random ?? AreaRng(conn)) ? maximum : stacks + 1;
        }
        changed |= state.CrabBarriers != stacks;
        state.CrabBarriers = stacks;
        if (changed) { conn.AuraStatsDirty = true; state.AspectRevision++; MarkPlayerStats(conn, PlayerStatStep.AvianCrab); }
        return changed;
    }

    // This runs after mitigation, Aegis and Ward, before allocating the hit to ES, Mana and Life.
    // Dodges, fully blocked/shifted/prevented hits and damage over time cannot reach this producer with physical > 0.
    public static void CrabPhysicalHitLocked(Player conn, int physical, double now,
        IReadOnlyDictionary<uint, int> stats)
    {
        if (physical <= 0 || !CrabActive(conn)) return;
        var state = conn.Reservations;
        if (state.CrabBarriers == 0) return;
        int Get(uint key) => stats.GetValueOrDefault(key);
        if (Get(Stat.CannotLoseCrabChargesIfYouHaveLostCrabChargesRecently) != 0
            && now < state.CrabLastLoss + 4) return;
        int loss = Get(Stat.NumberOfCrabChargesLostWhenHit);
        state.CrabBarriers = loss > 0 ? Math.Max(0, state.CrabBarriers - loss) : 0;
        state.CrabLastLoss = now;
        state.AspectRevision++; MarkPlayerStats(conn, PlayerStatStep.AvianCrab);
        state.CrabPoolsDirty = true;
        conn.AuraStatsDirty = true;
    }

    public static IReadOnlyList<(uint Key, int Value)> CrabRawStatsLocked(Player conn,
        IReadOnlyDictionary<uint, int> raw)
    {
        if (!CrabActive(conn) || CrabRoot(conn) is not { } root) return [];
        var state = conn.Reservations;
        int Get(uint name) => AspectInput(conn, raw, name);
        var rows = root.SkillStats ?? SkillsFor(conn).EffectStats(root.Effect, root.Level, root.Quality);
        int perStack = Stat.PhysicalDamageReductionPctPerCrabAspectStack is var reduction
            ? rows.FirstOrDefault(p => p.Key == reduction).Value : 0;
        int effect = Get(Stat.AspectOfTheCrabBuffEffectPlusPct);
        var result = new List<(uint, int)>();
        void Add(uint name, long value)
        {
            if (name is var key) result.Add((key, ActorScale.Saturate(value)));
        }
        Add(Stat.CurrentCrabAspectStacks, state.CrabBarriers);
        Add(Stat.MaxCrabAspectStacks, state.CrabMaximum);
        Add(Stat.BaseAdditionalPhysicalDamageReductionPct, (long)Math.Round(
            state.CrabBarriers * (double)perStack * Math.Max(0, 1 + effect / 100d), MidpointRounding.AwayFromZero));
        Add(Stat.DamagePlusPct, (long)state.CrabBarriers * Get(Stat.DamagePlusPctPerCrabCharge));
        if (state.CrabBarriers >= 5) Add(Stat.AdditionalBlockPct, Get(Stat.AdditionalBlockPctWhileYouHaveAtLeast5CrabCharges)
            + (state.CrabBarriers >= 10 ? Get(Stat.AdditionalBlockPctWhileYouHaveAtLeast10CrabCharges) : 0));
        if (state.CrabBarriers >= 10) Add(Stat.CannotBeStunned, Get(Stat.CannotBeStunnedIfYouHave10OrMoreCrabCharges));
        return result;
    }

    static void PresentCrabLocked(Player conn, List<byte[]> packets)
    {
        var state = conn.Reservations;
        ushort wanted = CrabActive(conn) ? state.CrabBuff : (ushort)0;
        if (state.PresentedCrabBuff != 0 && wanted != state.PresentedCrabBuff)
            packets.Add(InstanceEntities.RemoveBuff(PlayerId, state.PresentedCrabBuff));
        if (wanted != 0 && wanted != state.PresentedCrabBuff)
            packets.Add(InstanceEntities.AddBuff(PlayerId, wanted, 1047, PlayerId, state.CrabOwner!.Value.Skill));
        state.PresentedCrabBuff = wanted;
    }
}
