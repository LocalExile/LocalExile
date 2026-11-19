using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    sealed class TriggeredGuard(int epoch, object? rules)
    {
        public readonly int Epoch = epoch;
        public readonly object? Rules = rules;
        public double Until, ReadyAt, RecoverySeconds;
        public int Percent, Remaining, Absorbed, Armour, Reflection;
        public double Physical = 1, Elemental = 1;
        public ushort Buff, Skill;
        public string Effect = "";
        public SupportedGem? Gem;
        public ProjectileSession? Session;
    }
    static readonly Store<Player, TriggeredGuard> TriggeredGuards = new();
    static bool TriggerGuardSpell(Player conn, SupportedGem gem) => SkillsFor(conn).EffectId(gem.Effect)
        is "QuickGuard" or "ImmortalCall" or "MoltenShell";

    static TriggeredGuard? CurrentTriggeredGuard(Player conn, double now)
    {
        if (!TriggeredGuards.TryGetValue(conn, out var guard)) return null;
        if (guard.Epoch != conn.ProjectileEpoch || !ReferenceEquals(guard.Rules, CombatLifetime)
            || conn.Closed || conn.LeavingArea || conn.PendingTransfer is not null || conn.PlayerLife == 0)
        { TriggeredGuards.Remove(conn); return null; }
        if (guard.Until > 0 && now >= guard.Until) EndTriggeredGuard(conn, guard, guard.Until);
        return guard;
    }
    public static bool TriggerGuardReady(Player conn, double now) => CurrentTriggeredGuard(conn, now)
        is not { } guard || guard.Until == 0 && now >= guard.ReadyAt;

    static void EndTriggeredGuard(Player conn, TriggeredGuard guard, double now)
    {
        guard.Until = 0;
        guard.ReadyAt = now + guard.RecoverySeconds;
        if (guard.Session is not { } session) return;
        ushort buff = guard.Buff; guard.Buff = 0;
        if (buff != 0) session.Packet(InstanceEntities.RemoveBuff(PlayerId, buff));
        if (guard.Effect == "MoltenShell" && guard.Absorbed > 0)
        {
            double reflected = guard.Absorbed * guard.Reflection / 100d;
            int radius = SkillRadius(conn, guard.Gem!.Stats, true, guard.Gem.Effect);
            session.Enqueue(async () =>
            {
                if (!session.Active || conn.PlayerLife == 0 || conn.LeavingArea) return;
                var deaths = new List<(uint Id, SpawnedMonster M)>();
                foreach (var (id, monster) in conn.Monsters.ToArray())
                {
                    MonsterHit? hit; int damage;
                    lock (conn.Monsters)
                    {
                        if (!session.Active || conn.Paused || conn.PlayerLife == 0) return;
                        if (!monster.Hostile || monster.Dead || monster.Life == 0
                            || Dist(monster.Walk.Position(AreaTime(conn)), conn.Walk.Position(AreaTime(conn))) > radius) continue;
                        // Reflected damage has no spell/attack scaling, crit, penetration, leech or on-hit procs.
                        var resisted = DamageModel.Mitigate(DamagePacket.Of(DamageType.Fire, reflected, reflected),
                            type => MonsterHitResistance(monster, type, default, AreaTime(conn)));
                        damage = (int)Math.Round(resisted.MinTotal * MonsterDamageTaken(monster, spell: false,
                            AreaTime(conn), elemental: true, hit: false));
                        hit = ApplyMonsterHitLocked(conn, monster, damage);
                    }
                    if (hit is { } applied)
                        await NotifyMonsterHit(conn, id, monster,
                            applied, damage, "Molten Shell reflected damage", deaths: deaths);
                }
                foreach (var (id, monster) in deaths)
                    await MonsterKilled(conn, id, monster);
            });
        }
        guard.Armour = 0;
        conn.Pools = PoolsFor(conn, conn.Pools.Level);
        session.Enqueue(() => SendCharacterStats(conn, PlayerStatChange.Sources));
    }

    // "Guard Skills have X% increased Duration" is the same increased-duration sum as skill_effect_duration_+%.
    internal static double GuardDuration(int baseMilliseconds, Func<uint, int> player, Func<uint, int> own) =>
        SkillDurationModifiers.Seconds(baseMilliseconds,
            name => player(name) + (name == SkillDurationModifiers.IncreasedDuration ? player(Stat.GuardSkillEffectDurationPlusPct) : 0),
            own);

    // Called immediately after the trigger's successful payment, under the connection lock.
    public static void StartTriggeredGuard(Player conn, ProjectileSession session, ushort skill, double now)
    {
        var gem = SupportedSkill(conn, skill);
        if (!TriggerGuardReady(conn, now)) return;
        var player = CharacterPlayerStats(conn);
        int Own(uint name) => GolemStat(gem.Stats, name);
        int Get(uint name) => Own(name) + GolemStat(player, name);
        var guard = TriggeredGuards.GetValue(conn, _ => new(conn.ProjectileEpoch, CombatLifetime));
        guard.Gem = gem; guard.Skill = skill; guard.Session = session;
        guard.Effect = SkillsFor(conn).EffectId(gem.Effect)!;
        guard.Physical = guard.Elemental = 1;
        guard.Percent = guard.Remaining = guard.Absorbed = guard.Armour = guard.Reflection = 0;
        double duration = GuardDuration(Own(Stat.BaseSkillEffectDuration), name => GolemStat(player, name), Own);
        if (guard.Effect == "QuickGuard")
        {
            guard.Percent = Math.Clamp(Own(Stat.QuickGuardDamageAbsorbedPct), 0, 100);
            guard.Remaining = Math.Max(0, Own(Stat.QuickGuardDamageAbsorbLimit));
        }
        else if (guard.Effect == "ImmortalCall")
        {
            var charges = ChargesOf(conn);
            int removable = charges.Converted(ChargeType.Endurance) ? 0 : Math.Min(5,
                Math.Max(0, charges.Effective(ChargeType.Endurance) - charges.Limits(ChargeType.Endurance).Minimum));
            if (!RandomRolls.Chance(Get(Stat.ImmortalCallPctChanceToNotConsumeEnduranceCharges), 100, Random.Shared))
                charges.State.Lose(ChargeType.Endurance, removable, charges.Limits(ChargeType.Endurance));
            guard.Remaining = removable;
            duration *= Math.Max(0, 1 + removable * Get(Stat.BuffEffectDurationPlusPctPerRemovableEnduranceChargeLimitedTo5) / 100d);
            guard.Physical = Math.Max(0, 1 + Own(Stat.MortalCallPhysicalDamageTakenPlusPctFinal) / 100d)
                * Math.Max(0, 1 + removable * Own(Stat.MortalCallPhysicalDamageTakenPerEnduranceChargeConsumedFinalPermyriad) / 10000d);
            guard.Elemental = Math.Max(0, 1 + Own(Stat.MortalCallElementalDamageTakenPlusPctFinal) / 100d)
                * Math.Max(0, 1 + removable * Own(Stat.ImmortalCallElementalDamageTakenPlusPctFinalPerEnduranceChargeConsumedPermyriad) / 10000d);
            session.PresentCharges();
        }
        else if (guard.Effect == "MoltenShell")
        {
            guard.Armour = Own(Stat.BasePhysicalDamageReductionRating);
            guard.Percent = Math.Clamp(Own(Stat.MoltenShellDamageAbsorbedPct), 0, 100);
            guard.Reflection = Own(Stat.MoltenShellPctOfAbsorbedDamageDealtAsReflectedFire);
        }
        guard.Until = now + duration;
        // It ends at Until (Molten Shell reflects then), whoever reads it first.
        AfterSeconds(conn, duration, () => { lock (conn.Monsters) _ = CurrentTriggeredGuard(conn, conn.Clock.Elapsed.TotalSeconds); });
        double recovery = Math.Max(0, 1 + Get(Stat.VirtualCooldownSpeedPlusPct) / 100d + Own(Stat.BaseCooldownSpeedPlusPct) / 100d);
        guard.RecoverySeconds = recovery > 0 ? SkillsFor(conn).CooldownMs(gem.Effect, gem.Level) / 1000d / recovery : double.PositiveInfinity;
        conn.Pools = PoolsFor(conn, conn.Pools.Level);
        if (guard.Effect == "MoltenShell")
            guard.Remaining = (int)Math.Min(Math.Max(0, Own(Stat.MoltenShellMaxDamageAbsorbed)),
                (long)conn.Pools.Armour * Math.Max(0, Own(Stat.MoltenShellDamageAbsorbLimitPctOfArmour)) / 100);
        guard.Buff = conn.NextBuff++;
        session.Enqueue(() => SendCharacterStats(conn, PlayerStatChange.Sources));
        session.Packet(TriggeredGuardPresentation(conn, now), $"triggered guard {guard.Effect} ({duration:0.###} s)");
    }

    public static byte[] TriggeredGuardPresentation(Player conn, double now)
    {
        if (CurrentTriggeredGuard(conn, now) is not { Until: > 0 } guard) return [];
        ushort row = guard.Effect switch { "ImmortalCall" => 110, "MoltenShell" => 114, _ => 1302 };
        // Non-recovery definitions have no inline stats. The guard's native backend reads additional-data[0]
        // through Buff+0x80 (0x141f84000); an empty list leaves a null pointer and crashes the HUD.
        return InstanceEntities.AddBuff(PlayerId, guard.Buff, row, PlayerId, guard.Skill,
            u68: BitConverter.SingleToUInt32Bits((float)Math.Max(0, guard.Until - now)),
            activeSkill: SkillsFor(conn).ActionHash(guard.Gem!.Effect) is ushort action ? (guard.Skill, action) : null,
            list: [(uint)guard.Remaining]);
    }

    public static Dictionary<uint, int> WithTriggeredGuard(Player conn, Dictionary<uint, int> stats)
    {
        if (CurrentTriggeredGuard(conn, conn.Clock.Elapsed.TotalSeconds) is not { Until: > 0 } guard) return stats;
        void Add(uint name, int value)
        {
            if (name is var key) stats[key] = stats.GetValueOrDefault(key) + value;
        }
        Add(Stat.AffectedByGuardSkill, 1);
        Add(Stat.BasePhysicalDamageReductionRating, guard.Armour);
        if (guard.Effect == "QuickGuard") Add(Stat.AvoidBleedPct, 100);
        return stats;
    }

    public static (int Physical, int Other, int Chaos) TriggeredGuardHit(Player conn,
        int physical, int other, int chaos, double at)
    {
        (physical, other, chaos) = MitigateTriggeredGuardHit(conn, physical, other, chaos, at);
        return AbsorbTriggeredGuardHit(conn, physical, other, chaos, at);
    }

    static (int Physical, int Other, int Chaos) MitigateTriggeredGuardHit(Player conn,
        int physical, int other, int chaos, double at) =>
        CurrentTriggeredGuard(conn, at) is { Until: > 0 } guard
            ? ((int)(physical * guard.Physical), (int)(other * guard.Elemental), chaos)
            : (physical, other, chaos);

    static (int Physical, int Other, int Chaos) AbsorbTriggeredGuardHit(Player conn,
        int physical, int other, int chaos, double at)
    {
        if (CurrentTriggeredGuard(conn, at) is not { Until: > 0 } guard) return (physical, other, chaos);
        int Absorb(int damage)
        {
            int absorbed = (int)Math.Min(guard.Remaining, (long)damage * guard.Percent / 100);
            guard.Remaining -= absorbed; guard.Absorbed += absorbed;
            return damage - absorbed;
        }
        physical = Absorb(physical); other = Absorb(other); chaos = Absorb(chaos);
        if (guard.Percent > 0)
        {
            if (guard.Remaining == 0) EndTriggeredGuard(conn, guard, at);
            else guard.Session?.Packet(TriggeredGuardPresentation(conn, at));
        }
        return (physical, other, chaos);
    }
}
