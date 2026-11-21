using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;
using static Game;
using static Program;

/// <summary>Misery's chaos pulse (<c>RepeatedPulseDegen</c>): the chaos element of the essence pulses (Hatred cold, Anger
/// fire, Wrath lightning, Misery chaos). Kept apart from the other essence spells so a change here cannot disturb them.</summary>
public static partial class MapEssences
{
    /// <summary>The chaos pulse: a chaos degeneration on the players near the rare for the pulse's duration, after their
    /// chaos resistance, dealt through <c>TrapDegenPerSecond</c> (the tick recomputes the share). Pulses stack.</summary>
    static void ChaosDegen(ClientState source, (int X, int Y) at, double range, SpecialSpell spell)
    {
        if (spell.Dps <= 0) return;
        double seconds = Math.Max(1, spell.Duration);
        foreach (var conn in PlayersNear(source, at, range).ToArray())
        {
            if (conn.Immune || InGracePeriod(conn)) continue;
            double dps = spell.Dps * Math.Max(0, 1 - Math.Min(MaxResistance(conn, DamageType.Chaos), Resistance(conn, DamageType.Chaos)) / 100.0);
            var effects = Effects.GetOrCreateValue(conn);
            ushort buff = conn.NextBuff++;
            lock (effects)
            {
                effects.Degens.RemoveAll(d => d.Until <= Now);
                effects.Degens.Add((dps, Now + seconds, buff));
            }
            var (remote, stream, ciphers) = conn.Wire;
            _ = SendWorldRecord(remote, stream, ciphers, conn, PlayerId, InstanceEntities.AddBuff(PlayerId, buff, 100,
                u68: BitConverter.SingleToUInt32Bits((float)seconds)), $"essence chaos degen pulse {dps:0.#}/s");
        }
    }
}