using GameServer.Core.World;

public static partial class Game
{
    sealed class MinionLifeLeech
    {
        public readonly LeechRecoveryState Timed = new();
        public double Carry;
    }

    static readonly RuleTable<GolemState.Golem, MinionLifeLeech> MinionLifeLeeches = new();

    static void GrantMinionLifeLeech(GolemState.Golem minion, SpawnedMonster target, DamagePacket coefficients,
        DamagePacket mitigated, int damage, double now)
    {
        var monster = minion.Monster;
        if (ReferenceEquals(coefficients, DamagePacket.Empty) || monster.Life >= monster.MaxLife
            || target.Effects[Stat.CannotHaveLifeLeechedFrom] != 0 || target.Effects[Stat.MapMonstersCannotBeLeechedFrom] != 0) return;
        double total = mitigated.MinTotal;
        if (total <= 0) return;
        double amount = mitigated.Components.Sum(part => part.Range.Min * coefficients[part.Type].Min)
            * damage / total / 10_000d;
        if (amount <= 0) return;
        var leech = MinionLifeLeeches.GetOrCreateValue(minion);
        leech.Timed.Configure(now, new(1, monster.MaxLife * LeechLimits.BaseLifeManaRatePercentPerMinute / 6000d, 1));
        leech.Timed.Grant(now, amount, monster.MaxLife, monster.MaxLife * LeechLimits.BaseInstancePercent / 100d);
    }

    static int TakeMinionLifeLeech(GolemState.Golem minion, double now)
    {
        if (!MinionLifeLeeches.TryGetValue(minion, out var leech)) return 0;
        if (minion.Monster.Life >= minion.Monster.MaxLife)
        {
            MinionLifeLeeches.Remove(minion);
            return 0;
        }
        double total = leech.Carry + leech.Timed.TakeRecovery(now);
        int heal = (int)Math.Min(int.MaxValue, total);
        leech.Carry = total - heal;
        return heal;
    }
}
