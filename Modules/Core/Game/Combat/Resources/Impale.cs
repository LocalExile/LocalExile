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

    internal static ImpaleStats.Settings WeaponImpaleSettings(IReadOnlyDictionary<uint, int> stats, StatCatalog catalog,
        bool attack, IReadOnlyDictionary<uint, int>? own, SkillCatalog.Unarmed? weapon, bool critical = false)
    {
        string? itemClass = weapon?.ItemClass;
        var local = weapon?.LocalSources;
        bool twoHanded = WeaponTypeDamage.TwoHanded(itemClass) || itemClass == "FishingRod";
        bool axeOrSword = WeaponTypeDamage.FamilyOf(itemClass) is "axe" or "sword" || WeaponIdentity.AllOneHandMelee(local);
        long Get(uint id) => (long)stats.GetValueOrDefault(id) + (own?.GetValueOrDefault(id) ?? 0);
        long additionalChance = (twoHanded ? Get(Stat.AttacksWithTwoHandedWeaponsImpaleOnHitPctChance) : 0)
            + (axeOrSword ? Get(Stat.ImpaleOnHitPctChanceWithAxesSwords) : 0);
        return ImpaleStats.From(stats, catalog, attack, own, local, critical, additionalChance);
    }

    static readonly RuleTable<Player, IncomingImpale> IncomingImpales = new();
    // The map stat description gives 1.8 m, or 18 cells.
    const int MapImpaleBlastRadius = 18;
    sealed class IncomingImpale(int epoch)
    {
        public readonly int Epoch = epoch;
        public readonly ImpaleState Stacks = new();
    }

    static void IncomingImpaleContactLocked(Player player, uint attacker, SpawnedMonster monster, bool attack, double physical, double at,
        (int X, int Y) center, IReadOnlyDictionary<uint, int> stats)
    {
        var source = monster.Effects;
        int chance = attack ? source[Stat.MapMonstersChanceToImpalePct] : 0;
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
            ApplySelfHitLocked(player, DamagePacket.Of(DamageType.Physical, reflected, reflected), at, reflectedPhysical: true);
        if (burst <= 0) return;
        var damage = DamagePacket.Of(DamageType.Physical, burst, burst);
        if (player.PlayerLife > 0) ApplySelfHitLocked(player, damage, at, reflectedPhysical: true);

        var blast = new MonsterAttack(0, 0, MapImpaleBlastRadius, damage) { Secondary = true };
        DamageAlliedMinions(player);
        var instance = player.Instance;
        var area = player.Area;
        foreach (var ally in instance.Presence.PlayersExcept(player.Id))
        {
            if (ally.Closed || ally.LeavingArea || ally.PlayerLife == 0 || !ReferenceEquals(ally.Area, area)) continue;
            bool nearby = Dist(center, ally.Walk.Position(at)) <= MapImpaleBlastRadius;
            if (!nearby && ally.Golems.All.Count + ally.Reservations.Fighters.Count + ally.Summons.Fighters.Count == 0) continue;
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
                    if (nearby) ApplySelfHitLocked(ally, damage, at, reflectedPhysical: true);
                    DamageAlliedMinions(ally);
                }
                if (!nearby) return;
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

        void DamageAlliedMinions(Player owner)
        {
            var targets = owner.Golems.All.Values.Concat(owner.Reservations.Fighters.Values)
                .Concat(owner.Summons.Fighters.Values).Where(g => g.Monster.Life > 0 && !g.Monster.Dead
                    && Dist(center, g.Monster.Walk.Position(at)) <= MapImpaleBlastRadius).ToArray();
            foreach (var target in targets)
                GolemTakeHitLocked(owner, target, attacker, monster, blast, at, area: true, reflectedPhysical: true);
        }
    }

    readonly record struct ImpaleContact(double Physical, double At, ImpaleStats.Settings Settings, Random Random);
}
