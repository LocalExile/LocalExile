using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    const double MapThornsDuration = 5, MapThornsCooldown = 10, MapThornsHitInterval = .1;

    sealed class MapThornsWindow
    {
        public double PhysicalAt = double.NegativeInfinity, ElementalAt = double.NegativeInfinity;
    }

    static readonly RuleTable<SpawnedMonster, MapThornsWindow> MapThornsActivated = new();
    static readonly RuleTable<SpawnedMonster, MapThornsWindow> MapThornsReceived = new();

    static void MinionMapThornsOnHitLocked(Player player, GolemState.Golem minion, GolemState.Ability action,
        uint enemy, SpawnedMonster monster, DamagePacket hit, bool critical, double now)
    {
        if (monster.Rarity != MonsterRarity.Rare) return;
        int physical = monster.Effects[Stat.PhysicalThornsBaseDamage];
        int elemental = monster.Effects[Stat.ElementalThornsBaseDamage];
        if (physical <= 0 && elemental <= 0) return;
        var activation = MapThornsActivated.GetOrCreateValue(monster);
        var received = MapThornsReceived.GetOrCreateValue(minion.Monster);
        if (physical > 0 && hit[DamageType.Physical].Min > 0)
            Reflect(DamageType.Physical, physical, 2759, ref activation.PhysicalAt, ref received.PhysicalAt);
        if (elemental > 0)
        {
            var strongest = hit.Components.Where(component => DamageTypes.IsElemental(component.Type))
                .MaxBy(component => component.Range.Min);
            if (DamageTypes.IsElemental(strongest.Type) && strongest.Range.Min > 0)
                Reflect(strongest.Type, elemental, 2760, ref activation.ElementalAt, ref received.ElementalAt);
        }

        void Reflect(DamageType type, int basis, ushort row, ref double at, ref double receivedAt)
        {
            var area = player.Area;
            if (now >= at + MapThornsCooldown && !monster.Dead)
            {
                at = now;
                area.Buffs.Set(enemy, new(row, enemy, U68: Buff.Seconds(MapThornsDuration)));
                long frames = (long)Math.Ceiling(MapThornsDuration / FrameSeconds);
                area.Timers.After(area.Frame, frames, _ =>
                {
                    if (ReferenceEquals(area.Monsters.GetValueOrDefault(enemy), monster)) area.Buffs.Clear(enemy, row, enemy);
                }, source: CombatLifetime);
            }
            if (now >= at + MapThornsDuration || now < receivedAt + MapThornsHitInterval
                || !GolemCurrent(player, minion) || minion.Monster.Dead || minion.Monster.Life == 0) return;
            var body = minion.Monster;
            var effects = body.Effects;
            int Skill(uint key) => GolemStat(action.Stats, key);
            int Owner(uint key) => GolemStat(minion.Player, key);
            if (GolemIgnoresDots(minion) || effects[Stat.BaseCannotBeDamaged] != 0
                || effects[Stat.DamageCannotBeReflected] != 0 || Skill(Stat.DamageCannotBeReflected) != 0
                || Owner(Stat.MinionsDamageCannotBeReflected) != 0
                || critical && (effects[Stat.CriticalStrikeDamageCannotBeReflected] != 0 || Skill(Stat.CriticalStrikeDamageCannotBeReflected) != 0)
                || DamageTypes.IsElemental(type) && (GolemElementalImmune(minion) || Skill(Stat.ElementalDamageCannotBeReflected) != 0))
                return;
            bool phys = type == DamageType.Physical;
            int typedReflect = effects[phys ? Stat.PhysicalReflectDamageTakenPlusPct : Stat.ElementalReflectDamageTakenPlusPct]
                + Owner(phys ? Stat.PhysicalReflectDamageTakenAndMinionPhysicalReflectDamageTakenPlusPct
                    : Stat.ElementalReflectDamageTakenAndMinionElementalReflectDamageTakenPlusPct);
            double taken = Math.Max(0, 1 + (typedReflect + (double)effects[Stat.ReflectDamageTakenPlusPct]
                + Owner(Stat.MinionsReflectedDamageTakenPlusPct) + Owner(Stat.ReflectDamageTakenAndMinionReflectDamageTakenPlusPct)) / 100d);
            double reflectable = Math.Max(0, 1 - (Owner(Stat.MinionsHaveMinusPctOfThierDamageCannotBeReflected)
                + (double)Skill(Stat.MinusPctOfYourDamageCannotBeReflected)) / 100d);
            double mitigated = basis;
            if (phys)
            {
                double armour = GolemArmour(minion);
                mitigated *= 1 - Math.Clamp(armour / (armour + 5 * basis)
                    + (effects.PhysicalReduction - CrushReduction(body, now)) / 100d, 0, .9);
            }
            else
            {
                var resistance = MonsterHitResistance(body, type, default, now);
                mitigated = DamageModel.Mitigate(basis, resistance.Percent, resistance.Maximum) * GolemElementalReductionFactor(effects);
            }
            int damage = (int)Math.Min(int.MaxValue, Math.Round(mitigated * reflectable * taken
                * effects.DamageTaken(false, body.Ailments.Magnitude(MonsterElementalAilments.Kind.Shock, now)
                    + body.Ailments.ChillDamageTakenPct(now) + HitDebuffTaken(body, false, now))
                * MinionFortificationAt(player, minion, now) * GolemMeatShieldDamageFactor(minion)
                * MinionLinkDamageTaken(player, minion, now) * CompanionshipDamageTaken(player, minion, now)));
            if (damage <= 0) return;
            receivedAt = now;
            int life = minion.ShieldPool.Damage(damage, bypass: false, now);
            PublishEnergyShieldLocked(player, minion);
            if (life <= 0) return;
            body.Life = (uint)Math.Max(0, (long)body.Life - life);
            player.Minions.OutputQueue.Add(new([], "map thorns", minion.Id, body, PlayerMinions.OutputKind.Life));
            if (body.Life == 0) GolemDieLocked(player, minion, now);
            else MinionInstabilityLocked(player, minion, now);
        }
    }
}