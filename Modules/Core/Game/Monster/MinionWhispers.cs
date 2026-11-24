using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    // BuffDefinitions.crazy carries chaos damage based on maximum Life, attack speed, and increased damage.
    const ushort MinionWhispersBuff = 1764;
    const int WhispersDamagePerMinutePct = 1200, WhispersAttackSpeed = 50, WhispersDamage = 50;

    sealed class MinionWhispers
    {
        public double Since, Until, Carry;
    }

    static readonly RuleTable<GolemState.Golem, MinionWhispers> WhisperingMinions = new();

    static bool HearsWhispers(GolemState.Golem minion, double now) =>
        WhisperingMinions.TryGetValue(minion, out var state) && state.Until > now;

    static void MinionWhispersOnCriticalLocked(Player player, GolemState.Golem minion, double now)
    {
        int milliseconds = GolemStat(minion.Player, Stat.MinionsGoCrazyOnCritMs)
            + minion.Monster.Effects[Stat.GoCrazyOnCritMs];
        if (milliseconds <= 0 || minion.Monster.Dead || minion.Monster.Life == 0) return;
        var state = WhisperingMinions.GetOrCreateValue(minion);
        if (state.Until <= now) (state.Since, state.Carry) = (now, 0);
        double seconds = milliseconds / 1000d;
        state.Until = now + seconds;
        player.Area.Buffs.Set(minion.Id, new(MinionWhispersBuff, minion.Id,
            Values: [WhispersDamagePerMinutePct, WhispersAttackSpeed, WhispersDamage], U68: Buff.Seconds(seconds)));
    }

    static int MinionWhispersDamageLocked(Player player, GolemState.Golem minion, double from, double to, double mitigation)
    {
        if (!WhisperingMinions.TryGetValue(minion, out var state)) return 0;
        double seconds = Math.Max(0, Math.Min(state.Until, to) - Math.Max(from, state.Since));
        double exact = state.Carry + seconds * minion.Monster.MaxLife * WhispersDamagePerMinutePct / 6000d * mitigation;
        int whole = (int)Math.Min(int.MaxValue, Math.Max(0, Math.Floor(exact)));
        state.Carry = exact - whole;
        if (state.Until <= to)
        {
            player.Area.Buffs.Clear(minion.Id, MinionWhispersBuff, minion.Id);
            WhisperingMinions.Remove(minion);
        }
        return whole;
    }
}