using GameServer.Core.World;

public static partial class Game
{
    // Reflected physical damage receives target physical reduction and generic damage taken once. It is neither
    // attack nor spell damage and cannot crit, penetrate, or separately grant on-hit effects/Fortification/leech.
    public static int MonsterImpaleDamage(SpawnedMonster target, double stored, double now = double.NaN) =>
        MonsterTakesMitigated(target, DamagePacket.Of(DamageType.Physical, stored, stored), spell: false, now);

    public static (int Damage, double Reflected, bool Applied) WithImpaleHitLocked(Player player, SpawnedMonster target,
        int directDamage, DamagePacket rolled, bool critical, int sourceCriticalMultiplier,
        ImpaleStats.Settings settings, double hitMore = 1)
    {
        if (target.Invulnerable) return (directDamage, 0, false);
        double physical = rolled[DamageType.Physical].Min
            * (critical ? Math.Max(0, sourceCriticalMultiplier) / 100.0 : 1) * Math.Max(0, hitMore);
        return ApplyImpaleContact(target, directDamage, physical, AreaTime(player), ProjectileWorldTime(player),
            settings, AreaRng(player));
    }

    static (int Damage, double Reflected, bool Applied) ApplyImpaleContact(SpawnedMonster target,
        int directDamage, double physical, double at, double mitigationAt, ImpaleStats.Settings settings, Random random)
    {
        var (stored, applied) = target.Impale.OnHit(physical, at, settings, random, target.Effects[Stat.AvoidImpalePct]);
        return (ImpaleStats.AddReflected(directDamage, MonsterImpaleDamage(target, stored, mitigationAt)), stored, applied);
    }

    static readonly RuleTable<Player, IncomingImpale> IncomingImpales = new();
    // The map description names nearby allies without exporting the distance in its mod row.
    const int MapImpaleBlastRadius = 30;
    sealed class IncomingImpale(int epoch)
    {
        public readonly int Epoch = epoch;
        public readonly ImpaleState Stacks = new();
    }

    static void IncomingImpaleContactLocked(Player player, MonsterModEffects source, double physical, double at,
        (int X, int Y) center, IReadOnlyDictionary<uint, int> stats)
    {
        int chance = source[Stat.MapMonstersChanceToImpalePct];
        IncomingImpales.TryGetValue(player, out var state);
        if (state is not null && state.Epoch != player.IncomingHitEpoch)
        {
            IncomingImpales.Remove(player);
            state = null;
        }
        if (state is null)
        {
            if (chance <= 0 || physical <= 0) return;
            state = new(player.IncomingHitEpoch);
            IncomingImpales.AddOrUpdate(player, state);
        }
        var (reflected, applied) = state.Stacks.OnHit(physical, at, new(chance), player.IncomingHitRandom,
            stats.GetValueOrDefault(Stat.AvoidImpalePct));
        int limit = source[Stat.MapImpalesOnPlayersDetonateWhenPlayersHaveXImpaleDebuffs];
        double burst = applied && limit > 0 && state.Stacks.Stacks >= limit ? state.Stacks.Consume(at).Reflected : 0;
        if (state.Stacks.Stacks == 0) IncomingImpales.Remove(player);

        if (reflected > 0)
            ApplySelfHitLocked(player, DamagePacket.Of(DamageType.Physical, reflected, reflected), at);
        if (burst <= 0) return;
        var damage = DamagePacket.Of(DamageType.Physical, burst, burst);
        if (player.PlayerLife > 0) ApplySelfHitLocked(player, damage, at);

        var instance = player.Instance;
        var area = player.Area;
        foreach (var ally in instance.Presence.PlayersExcept(player.Id))
        {
            if (ally.Closed || ally.LeavingArea || ally.PlayerLife == 0 || !ReferenceEquals(ally.Area, area)
                || Dist(center, ally.Walk.Position(at)) > MapImpaleBlastRadius) continue;
            int epoch = ally.IncomingHitEpoch;
            // Never acquire another player's mutation gate while committing the original hit.
            ProjectilesFor(player).Enqueue(async () =>
            {
                uint mana;
                using (ProjectileMutation(ally, advance: false))
                {
                    if (!CurrentGameWork || ally.Closed || ally.LeavingArea || ally.PlayerLife == 0
                        || !ReferenceEquals(ally.Area, area) || ally.IncomingHitEpoch != epoch) return;
                    mana = ally.PlayerMana;
                    ApplySelfHitLocked(ally, damage, at);
                }
                await SendPlayerEnergyShield(ally);
                if (ally.PlayerMana != mana) await SendPlayerMana(ally, "mana after reflected impale detonation");
                if (ally.PlayerLife == 0)
                {
                    await PlayerDied(ally, "death from reflected impale detonation");
                    await CampaignBossHelpers.OnPlayerDiedInBossArea(ally);
                }
                else ally.Area.Life.Resend(ally);
            });
        }
    }

    readonly record struct ImpaleContact(double Physical, double At, ImpaleStats.Settings Settings, Random Random);
}
