using GameServer.Core.World;

public static partial class Game
{
    sealed class MinionLifeLeech
    {
        public readonly LeechRecoveryState Timed = new();
        public double Carry;
    }

    static readonly RuleTable<GolemState.Golem, MinionLifeLeech> MinionLifeLeeches = new();

    static SpawnedMonster? VampiricMinionAt(Player player, LinkSkills.Link link, double now) =>
        link.EffectId == "VampiricLink" && now < link.Until && player.PlayerLife > 0 && !player.Closed && !player.LeavingArea
        && (link.AwaySince is not double away || now - away < LinkSkills.BreakSeconds)
        && player.Area.Monsters.GetValueOrDefault(link.Target) is { Dead: false, Life: > 0 } monster
        && player.Minions.Owns(link.Target, monster) ? monster : null;

    static bool HasVampiricMinion(Player player, double now)
    {
        foreach (var link in player.Links.All)
            if (VampiricMinionAt(player, link, now) is not null) return true;
        return false;
    }

    sealed class VampiricLeechCarry { public double Life; }
    static readonly RuleTable<SpawnedMonster, VampiricLeechCarry> VampiricLeechCarries = new();

    static double MinionLifeRecoveryMultiplier(SpawnedMonster monster) =>
        Math.Max(0, 1 + monster.Effects[Stat.LifeRecoveryRatePlusPct] / 100d);

    static bool RecoverVampiricMinions(Player player, double recovery, double now, bool overTime = false)
    {
        bool redirected = false;
        foreach (var link in player.Links.All)
        {
            if (VampiricMinionAt(player, link, now) is not { } monster) continue;
            redirected = true;
            if (monster.Effects[Stat.CannotRecoverLife] != 0) continue;
            var carry = VampiricLeechCarries.GetOrCreateValue(monster);
            uint life = RecoveryPool.Advance(monster.Life, monster.MaxLife,
                overTime ? recovery * MinionLifeRecoveryMultiplier(monster) : recovery, ref carry.Life);
            if (life == monster.Life) continue;
            monster.Life = life;
            if (life == monster.MaxLife && OwnedFighter(player, link.Target) is { } fighter)
                MinionLifeLeeches.Remove(fighter);
            player.Minions.OutputQueue.Add(new([], null, link.Target, monster, PlayerMinions.OutputKind.Life));
        }
        return redirected;
    }

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

    static int TakeMinionLifeLeech(GolemState.Golem minion, double now, double recoveryRate)
    {
        if (!MinionLifeLeeches.TryGetValue(minion, out var leech)) return 0;
        if (minion.Monster.Life >= minion.Monster.MaxLife)
        {
            MinionLifeLeeches.Remove(minion);
            return 0;
        }
        double total = leech.Carry + leech.Timed.TakeRecovery(now) * recoveryRate;
        int heal = (int)Math.Min(int.MaxValue, total);
        leech.Carry = total - heal;
        return heal;
    }
}
