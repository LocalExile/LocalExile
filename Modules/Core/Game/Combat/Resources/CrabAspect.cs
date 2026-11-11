using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

static partial class Game
{
    const string CrabAspect = "AspectOfTheCrab";
    static AuraCatalog.Aura? CrabRoot(ClientState conn) => conn.PlayerLife > 0
        ? conn.Auras.Values.FirstOrDefault(a => a.EffectId == CrabAspect) : null;
    internal static bool CrabActive(ClientState conn) => CrabRoot(conn) is { } root
        && conn.Reservations.CrabOwner == new ReservationState.CatAspectOwner(root.SkillId, root.Buff);

    static void EndCrabAspectLocked(ClientState conn, AuraCatalog.Aura aura)
    {
        var state = conn.Reservations;
        if (state.CrabOwner != new ReservationState.CatAspectOwner(aura.SkillId, aura.Buff)) return;
        state.CrabOwner = null;
        state.CrabBarriers = state.CrabMaximum = 0;
        state.NextCrabBarrier = 0;
        state.CrabLastLoss = double.NegativeInfinity;
        state.CrabBuff = 0;
        state.AspectRevision++;
        conn.AuraStatsDirty = true;
    }

    internal static bool CrabReconcileLocked(ClientState conn, double now,
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
        int Get(string name) => AspectInput(conn, raw, name);
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
        int maximum = Stats.Key("max_crab_aspect_stacks") is uint key ? rows.FirstOrDefault(p => p.Key == key).Value : 0;
        maximum = (int)Math.Clamp((long)maximum + Get("crab_aspect_crab_barrier_max_+"), 0, int.MaxValue);
        changed |= state.CrabMaximum != maximum;
        state.CrabMaximum = maximum;
        int stacks = Math.Min(state.CrabBarriers, maximum);
        if (now >= state.NextCrabBarrier)
        {
            long ticks = (long)Math.Floor((now - state.NextCrabBarrier + 1e-9) / .5) + 1;
            state.NextCrabBarrier += ticks * .5;
            int chance = Get("chance_to_gain_max_crab_stacks_when_you_would_gain_a_crab_stack_%");
            long gains = Math.Min(ticks, (long)maximum - stacks);
            if (chance <= 0) stacks += (int)gains;
            else for (long i = 0; i < gains && stacks < maximum; i++)
                stacks = RandomRolls.Chance(chance, 100, random ?? Random.Shared) ? maximum : stacks + 1;
        }
        changed |= state.CrabBarriers != stacks;
        state.CrabBarriers = stacks;
        if (changed) { conn.AuraStatsDirty = true; state.AspectRevision++; }
        return changed;
    }

    // This runs after mitigation, Aegis and Ward, before allocating the hit to ES, Mana and Life.
    // Dodges, fully blocked/shifted/prevented hits and damage over time cannot reach this producer with physical > 0.
    internal static void CrabPhysicalHitLocked(ClientState conn, int physical, double now,
        IReadOnlyDictionary<uint, int> stats)
    {
        if (physical <= 0 || !CrabActive(conn)) return;
        var state = conn.Reservations;
        if (state.CrabBarriers == 0) return;
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        if (Get("cannot_lose_crab_charges_if_you_have_lost_crab_charges_recently") != 0
            && now < state.CrabLastLoss + 4) return;
        int loss = Get("number_of_crab_charges_lost_when_hit");
        state.CrabBarriers = loss > 0 ? Math.Max(0, state.CrabBarriers - loss) : 0;
        state.CrabLastLoss = now;
        state.AspectRevision++;
        state.CrabPoolsDirty = true;
        conn.AuraStatsDirty = true;
    }

    internal static IReadOnlyList<(uint Key, int Value)> CrabRawStatsLocked(ClientState conn,
        IReadOnlyDictionary<uint, int> raw)
    {
        if (!CrabActive(conn) || CrabRoot(conn) is not { } root) return [];
        var state = conn.Reservations;
        int Get(string name) => AspectInput(conn, raw, name);
        var rows = root.SkillStats ?? SkillsFor(conn).EffectStats(root.Effect, root.Level, root.Quality);
        int perStack = Stats.Key("physical_damage_reduction_%_per_crab_aspect_stack") is uint reduction
            ? rows.FirstOrDefault(p => p.Key == reduction).Value : 0;
        int effect = Get("aspect_of_the_crab_buff_effect_+%");
        var result = new List<(uint, int)>();
        void Add(string name, long value)
        {
            if (Stats.Key(name) is uint key) result.Add((key, ActorScale.Saturate(value)));
        }
        Add("current_crab_aspect_stacks", state.CrabBarriers);
        Add("max_crab_aspect_stacks", state.CrabMaximum);
        Add("base_additional_physical_damage_reduction_%", (long)Math.Round(
            state.CrabBarriers * (double)perStack * Math.Max(0, 1 + effect / 100d), MidpointRounding.AwayFromZero));
        Add("damage_+%", (long)state.CrabBarriers * Get("damage_+%_per_crab_charge"));
        if (state.CrabBarriers >= 5) Add("additional_block_%", Get("additional_block_%_while_you_have_at_least_5_crab_charges")
            + (state.CrabBarriers >= 10 ? Get("additional_block_%_while_you_have_at_least_10_crab_charges") : 0));
        if (state.CrabBarriers >= 10) Add("cannot_be_stunned", Get("cannot_be_stunned_if_you_have_10_or_more_crab_charges"));
        return result;
    }

    static void PresentCrabLocked(ClientState conn, List<byte[]> packets)
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
