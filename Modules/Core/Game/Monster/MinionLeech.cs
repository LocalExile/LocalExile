using GameServer.Core.World;

public static partial class Game
{
    sealed class MinionLifeLeech
    {
        public readonly LeechRecoveryState Timed = new();
        public double Carry;
    }

    static readonly RuleTable<GolemState.Golem, MinionLifeLeech> MinionLifeLeeches = new();
    static readonly RuleTable<GolemState.Golem, LeechRecoveryState> MinionShieldLeeches = new();

    static SpawnedMonster? VampiricMinionAt(Player player, LinkSkills.Link link, double now) =>
        link.EffectId == "VampiricLink" && now < link.Until && player.PlayerLife > 0 && !player.Closed && !player.LeavingArea
        && (link.AwaySince is not double away || now - away < link.BreakGraceSeconds)
        && player.Area.Monsters.GetValueOrDefault(link.Target) is { Dead: false, Life: > 0 } monster
        && (player.Minions.Owns(link.Target, monster)
            || (player.Companion is { } comp && comp.EntityId == link.Target && ReferenceEquals(comp.Monster, monster)))
            ? monster : null;

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
            if (player.Companion is { } comp && comp.EntityId == link.Target)
                player.Area.Life.Set(link.Target, AreaLife.LifePool, life);
            else
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
        double amount = MinionLeechAmount(coefficients, mitigated, damage);
        if (amount <= 0) return;
        var leech = MinionLifeLeeches.GetOrCreateValue(minion);
        leech.Timed.Configure(now, new(1, monster.MaxLife * LeechLimits.BaseLifeManaRatePercentPerMinute / 6000d, 1));
        leech.Timed.Grant(now, amount, monster.MaxLife, monster.MaxLife * LeechLimits.BaseInstancePercent / 100d);
    }

    static double MinionLeechAmount(DamagePacket coefficients, DamagePacket mitigated, int damage) =>
        mitigated.MinTotal > 0 ? mitigated.Components.Sum(part => part.Range.Min * coefficients[part.Type].Min)
            * damage / mitigated.MinTotal / 10_000d : 0;

    static double MinionShieldLeechDamageFactor(GolemState.Golem minion, GolemState.Ability ability)
    {
        if (!minion.ShieldPool.Full) return 1;
        const uint stat = Stat.SupportEnergyShieldLeechDamagePlusPctOnFullEnergyShieldFinal;
        int more = minion.Monster.Effects[stat] + GolemStat(ability.Stats, stat);
        return Math.Max(0, 1 + more / 100d);
    }

    static void GrantMinionShieldLeech(GolemState.Golem minion, SpawnedMonster target, DamagePacket coefficients,
        DamagePacket mitigated, int damage, double now)
    {
        if (ReferenceEquals(coefficients, DamagePacket.Empty) || minion.MaximumShield == 0 || minion.ShieldPool.Full
            || target.Effects[Stat.CannotHaveEnergyShieldLeechedFrom] != 0 || target.Effects[Stat.MapMonstersCannotBeLeechedFrom] != 0) return;
        double amount = MinionLeechAmount(coefficients, mitigated, damage);
        if (amount <= 0) return;
        var leech = MinionShieldLeeches.GetOrCreateValue(minion);
        uint maximum = minion.MaximumShield;
        leech.Configure(now, new(1, maximum * LeechLimits.BaseEnergyShieldRatePercentPerMinute / 6000d, 1));
        leech.Grant(now, amount, maximum, maximum * LeechLimits.BaseInstancePercent / 100d);
    }

    static void RecoverMinionShieldLeech(GolemState.Golem minion, double now)
    {
        if (!MinionShieldLeeches.TryGetValue(minion, out var leech)) return;
        var shield = minion.ShieldPool;
        var effects = minion.Monster.Effects;
        double recovery = leech.TakeRecovery(now);
        if (!shield.Full && effects[Stat.CannotRecoverEnergyShield] == 0 && effects[Stat.NoEnergyShieldRecovery] == 0)
            shield.Gain(recovery * Math.Max(0, 1 + effects[Stat.EnergyShieldRecoveryRatePlusPct] / 100d));
        if (shield.Full || shield.Maximum == 0 || leech.Count == 0) MinionShieldLeeches.Remove(minion);
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
