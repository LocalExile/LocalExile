using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Misery's chaos pulse (<c>RepeatedPulseDegen</c>) — the chaos element of the essence pulses (Hatred cold, Anger
/// fire, Wrath lightning, Misery chaos). The aura-nova around the rare applies a stack of the «Essence of Misery» debuff
/// (BuffDefinitions 744 <c>repeated_pulse_chaos</c>, a chaos damage-over-time that shows its stack count); the rare's attacks
/// apply further stacks. Each stack is its own degeneration with the pulse's duration, so they fall off one by one. Kept
/// apart from the other essence spells so a change here cannot disturb them.</summary>
public static partial class MapEssences
{
    const ushort MiseryBuffRow = 744; // repeated_pulse_chaos, "Essence of Misery"

    /// <summary>The chaos pulse applies one «Essence of Misery» stack to the players near the rare.</summary>
    static void ChaosDegen(ClientState source, (int X, int Y) at, double range, SpecialSpell spell)
        => ApplyMiseryNear(source, at, range, spell, 1);

    static void ApplyMiseryNear(ClientState source, (int X, int Y) at, double range, SpecialSpell spell, int stacks)
    {
        if (spell.Dps <= 0) return;
        foreach (var conn in PlayersNear(source, at, range).ToArray())
            ApplyMiseryStacks(conn, spell, stacks);
    }

    /// <summary>The rare's attack applies an extra «Essence of Misery» stack to the player it hits (the wiki). Called from
    /// the monster's melee contact; a monster with no Misery pulse is a cheap no-op.</summary>
    public static void MiseryOnAttack(ClientState conn, SpawnedMonster attacker, ClientState target)
    {
        if (target.Immune || InGracePeriod(target)) return;
        if (!Areas.TryGetValue(conn.Instance, out var area)) return;
        CastState? state;
        lock (area.Casting) area.Casting.TryGetValue(attacker.ObjectId, out state);
        if (state?.Spells.FirstOrDefault(s => s.Kind == "chaos") is { } spell)
            ApplyMiseryStacks(target, spell, 1);
    }

    static void ApplyMiseryStacks(ClientState conn, SpecialSpell spell, int stacks)
    {
        if (spell.Dps <= 0 || conn.Immune || InGracePeriod(conn)) return;
        double seconds = Math.Max(1, spell.Duration);
        double dps = spell.Dps * Math.Max(0, 1 - Math.Min(MaxResistance(conn, DamageType.Chaos), Resistance(conn, DamageType.Chaos)) / 100.0);
        var effects = Effects.GetOrCreateValue(conn);
        lock (effects)
            for (int i = 0; i < stacks; i++) effects.MiseryStacks.Add((dps, Now + seconds));
        RefreshMisery(conn, effects);
    }

    /// <summary>Called from the essence tick: drops the expired stacks and refreshes the degeneration / buff count.</summary>
    static void TickMisery(ClientState conn, EssenceEffects effects)
    {
        if (effects.MiseryStacks.Count == 0 && effects.MiseryBuff == 0) return;
        RefreshMisery(conn, effects);
    }

    /// <summary>Recomputes the stack count and its chaos degeneration share of <c>TrapDegenPerSecond</c>, and (re)sends the
    /// «Essence of Misery» buff with the live count only when the count changes.</summary>
    static void RefreshMisery(ClientState conn, EssenceEffects effects)
    {
        double now = Now; double dps; int count; ushort buff; double remaining; bool changed;
        lock (effects)
        {
            effects.MiseryStacks.RemoveAll(s => s.Until <= now);
            count = effects.MiseryStacks.Count;
            dps = effects.MiseryStacks.Sum(s => s.Dps);
            remaining = count > 0 ? effects.MiseryStacks.Max(s => s.Until) - now : 0;
            if (dps != effects.MiseryShare)
            {
                lock (conn.Monsters) conn.TrapDegenPerSecond = Math.Max(0, conn.TrapDegenPerSecond - effects.MiseryShare + dps);
                effects.MiseryShare = dps;
            }
            changed = count != effects.MiserySentCount;
            if (count == 0) { buff = effects.MiseryBuff; effects.MiseryBuff = 0; effects.MiserySentCount = 0; }
            else
            {
                if (effects.MiseryBuff == 0) effects.MiseryBuff = conn.NextBuff++;
                buff = effects.MiseryBuff;
                effects.MiserySentCount = count;
            }
        }
        if (conn.Closed || !changed) return;
        var (remote, stream, ciphers) = conn.Wire;
        if (count == 0)
        {
            if (buff != 0) _ = SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.RemoveBuff(PlayerId, buff), null);
        }
        else
        {
            _ = SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.AddBuff(PlayerId, buff, MiseryBuffRow,
                u68: BitConverter.SingleToUInt32Bits((float)remaining), stacks: (ushort)count), $"essence of misery x{count}");
        }
    }
}