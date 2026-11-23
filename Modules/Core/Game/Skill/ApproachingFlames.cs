using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

internal sealed class FlameTouchedState
{
    public WornGrantedSkills.Grant? Source { get; private set; }
    public bool RemoveAdrenaline { get; private set; }
    double at, rate, pending;
    public double TakenCarry, BypassCarry;

    public void Start(WornGrantedSkills.Grant source, double now, int fireDamage, bool removeAdrenaline)
    {
        Settle(now);
        Source = source;
        rate = Math.Max(0, fireDamage);
        RemoveAdrenaline = removeAdrenaline;
    }

    public void End(double now)
    {
        Settle(now);
        Source = null;
        rate = 0;
    }

    public double TakeDamage(double now)
    {
        Settle(now);
        double damage = pending;
        pending = 0;
        return damage;
    }

    void Settle(double now)
    {
        if (now <= at) return;
        pending += (now - at) * rate;
        at = now;
    }
}

public static partial class Game
{
    const string ApproachingFlamesEffect = "ApproachingFlames";
    const ushort FlameTouchedBuffRow = 2088;

    static FlameTouchedState? FlameTouchedOf(Player player) =>
        (player.ResourceEffectsState as ResourceEffectsState)?.FlameTouched;

    static bool FlameSourceCurrent(Player player, WornGrantedSkills.Grant source) =>
        !IsInactive(player, source.Host) && AllWornGrantedSkillsOf(player).Contains(source);

    static bool FlameTouchedActive(Player player) =>
        player.PlayerLife > 0 && FlameTouchedOf(player)?.Source is { } source && FlameSourceCurrent(player, source);

    static void ReconcileFlameTouched(Player player, double now)
    {
        if (FlameTouchedOf(player)?.Source is { } source && !FlameSourceCurrent(player, source))
            EndFlameTouched(player, now);
    }

    static void EndFlameTouched(Player player, double now)
    {
        if (FlameTouchedOf(player) is not { Source: not null } flame) return;
        flame.End(now);
        player.Antiquarian.EndSustainedAdrenaline(flame.RemoveAdrenaline);
        player.Area.Buffs.Clear(player.Id, FlameTouchedBuffRow, player.Id);
        MarkPlayerStats(player, PlayerStatStep.Sources);
    }

    internal static void RetireFlameTouched(Player player, ResourceEffectsState owner)
    {
        if (owner.FlameTouched is { Source: not null } flame)
        {
            player.Antiquarian.EndSustainedAdrenaline(flame.RemoveAdrenaline);
            player.Area.Buffs.Clear(player.Id, FlameTouchedBuffRow, player.Id);
            MarkPlayerStats(player, PlayerStatStep.Sources);
        }
        owner.FlameTouched = null;
    }

    static async Task<bool> ToggleApproachingFlames(Player player, ushort skillId, Func<bool> current)
    {
        bool toggled = false;
        await SendWorldRecord(player, player.Id, [], "Approaching Flames toggled", atGate: () =>
        {
            if (!current() || InTown(player) || SupportedSkill(player, skillId).WornGrant is not { EffectId: ApproachingFlamesEffect } source
                || !FlameSourceCurrent(player, source)) return null;
            var resources = ResourceEffectsOf(player);
            if (!ResourceEffectsCurrent(player, resources)) return null;

            double now = AreaTime(player);
            ReconcileFlameTouched(player, now);
            var flame = resources.FlameTouched ??= new();
            if (flame.Source is not null) EndFlameTouched(player, now);
            else
            {
                var stats = CharacterPlayerStats(player);
                flame.Start(source, now, stats.GetValueOrDefault(Stat.FireDamageTakenPerSecondWhileFlameTouched),
                    stats.GetValueOrDefault(Stat.LoseAdrenalineOnLosingFlameTouched) != 0);
                if (stats.GetValueOrDefault(Stat.GainAdrenalineOnGainingFlameTouched) != 0)
                    player.Antiquarian.SustainAdrenaline(now);
                player.Area.Buffs.Set(player.Id, new(FlameTouchedBuffRow, player.Id));
                MarkPlayerStats(player, PlayerStatStep.Sources);
            }

            RecomputePoolsAndSpeed(player);
            toggled = true;
            return [];
        });
        if (toggled) await SendAntiquarian(player);
        return toggled;
    }

    static void AdvanceFlameTouchedDamage(Player player, double now, IReadOnlyDictionary<uint, int> stats)
    {
        if (FlameTouchedOf(player) is not { } flame) return;
        ReconcileFlameTouched(player, now);
        double raw = flame.TakeDamage(now);
        if (raw <= 0 || player.Closed || player.LeavingArea || player.Immune || InGracePeriod(player) || InTown(player)
            || HardcoreProgressBlocked(player)) return;
        int Get(uint stat) => stats.GetValueOrDefault(stat);
        if (DamageImmunities.From(Get).Contains(DamageType.Fire)) return;
        double resisted = DamageModel.Mitigate(raw, Resistance(player, DamageType.Fire), MaxResistance(player, DamageType.Fire));
        int damage = IncomingResourceDefences.TakenOverTime(resisted, Get, ref flame.TakenCarry,
            IncomingResourceDefences.LessTakenPct(player.Pools, Get),
            typedIncreasedPct: ActorScale.Saturate((long)Get(Stat.FireDamageTakenPlusPct) + Get(Stat.ElementalDamageTakenPlusPct)),
            finalTakenPct: Get(IncomingResourceDefences.UnhingedSaneDamageFinal));
        int life = IncomingResourceDefences.DamageOverTime(player.EnergyShield, damage,
            Get(IncomingResourceDefences.BypassStat), now, ref flame.BypassCarry);
        player.PlayerLife = VaalImmortalCallLife(player, (uint)Math.Max(0, (long)player.PlayerLife - life), now);
        if (player.PlayerLife == 0) { player.InvalidateIncomingHits(); RecordHardcoreDeath(player); }
    }
}
