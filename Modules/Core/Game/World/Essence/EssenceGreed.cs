using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Greed's Corrupted Blood: the blood daemons the rare summons fire a physical nova whose hits apply a stacking
/// Corrupted Blood degeneration (buff 238), like Malachai's Tribomb. Kept apart from the other essence spells so a change here
/// cannot disturb them.</summary>
public static partial class MapEssences
{
    /// <summary>A monster hit whose skill applies Corrupted Blood (Greed's blood nova) stacks the physical degeneration on the
    /// player. Called from the incoming-hit pipeline; a hit with no Corrupted Blood skill is a cheap no-op.</summary>
    public static void CorruptedBloodOnHit(ClientState conn, SpawnedMonster m, MonsterAttack a, DamagePacket baseHit,
        bool blocked, double now, IReadOnlyDictionary<uint, int> stats)
    {
        if (blocked || conn.Immune || conn.PlayerLife == 0 || InGracePeriod(conn)) return;
        int index = a.SkillId - 0x4000;
        if (index < 0 || index >= m.Kit.Count) return;
        int effect = m.Kit[index].Effect;
        var native = GolemData.Skill(SkillsFor(conn), effect, m.Level);
        int Native(string name) => Stats.Key(name) is uint key ? native.Stats.GetValueOrDefault(key) : 0;
        int Get(string name) => Stats.Key(name) is uint key ? stats.GetValueOrDefault(key) : 0;
        double duration = Native("corrupted_blood_on_hit_duration") / 1000.0;
        if (duration <= 0) return;
        if (Get("cannot_be_inflicted_by_corrupted_blood") != 0 || Get("immune_to_corrupted_blood") != 0) return;
        if (RandomRolls.Chance(Get("avoid_corrupted_blood_%_chance"), 100, conn.IncomingHitRandom)) return;
        int stacks = Math.Max(1, Native("corrupted_blood_on_hit_num_stacks"));
        var effects = Effects.GetOrCreateValue(conn);
        lock (effects)
        {
            if (effects.BloodUntil <= now) { effects.BloodStacks = 0; effects.BloodDps = 0; }
            effects.BloodStacks = Math.Min(10, effects.BloodStacks + stacks);
            effects.BloodUntil = now + duration;
            effects.BloodDps = Math.Max(effects.BloodDps, (baseHit.MinTotal + baseHit.MaxTotal) * .5
                * Native("corrupted_blood_on_hit_%_average_damage_to_deal_per_minute_per_stack") / 6000.0);
        }
        RefreshBlood(conn, effects, now);
    }

    /// <summary>Called from the essence tick: drops the expired blood and keeps its degeneration share current.</summary>
    static void TickBlood(ClientState conn, EssenceEffects effects)
    {
        if (effects.BloodStacks == 0 && effects.BloodBuff == 0) return;
        RefreshBlood(conn, effects, Now);
    }

    static void RefreshBlood(ClientState conn, EssenceEffects effects, double now)
    {
        double dps; int stacks; ushort buff; double remaining; bool changed;
        lock (effects)
        {
            if (effects.BloodUntil <= now) { effects.BloodStacks = 0; effects.BloodDps = 0; }
            stacks = effects.BloodStacks;
            dps = stacks * effects.BloodDps;
            remaining = Math.Max(0, effects.BloodUntil - now);
            if (dps != effects.BloodShare)
            {
                lock (conn.Monsters) conn.TrapDegenPerSecond = Math.Max(0, conn.TrapDegenPerSecond - effects.BloodShare + dps);
                effects.BloodShare = dps;
            }
            changed = stacks != effects.BloodSentStacks;
            if (stacks == 0) { buff = effects.BloodBuff; effects.BloodBuff = 0; effects.BloodSentStacks = 0; }
            else
            {
                if (effects.BloodBuff == 0) effects.BloodBuff = conn.NextBuff++;
                buff = effects.BloodBuff;
                effects.BloodSentStacks = stacks;
            }
        }
        if (conn.Closed || !changed) return;
        var (remote, stream, ciphers) = conn.Wire;
        if (stacks == 0)
        {
            if (buff != 0) _ = SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.RemoveBuff(PlayerId, buff), null);
        }
        else
        {
            _ = SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.AddBuff(PlayerId, buff, 238,
                values: [0], u68: BitConverter.SingleToUInt32Bits((float)remaining), stacks: (ushort)stacks), $"essence corrupted blood x{stacks}");
        }
    }
}