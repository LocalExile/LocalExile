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
    static bool TriggerGuardSpell(Player player, SupportedGem gem) => SkillsFor(player).EffectId(gem.Effect)
        is "QuickGuard" or "ImmortalCall" or "MoltenShell";

    static TriggeredGuard? CurrentTriggeredGuard(Player player, double now)
    {
        if (!TriggeredGuards.TryGetValue(player, out var guard)) return null;
        if (guard.Epoch != player.ProjectileEpoch || !ReferenceEquals(guard.Rules, CombatLifetime)
            || player.Closed || player.LeavingArea || player.PendingTransfer is not null || player.PlayerLife == 0)
        { TriggeredGuards.Remove(player); return null; }
        if (guard.Until > 0 && now >= guard.Until) EndTriggeredGuard(player, guard, guard.Until);
        return guard;
    }
    public static bool TriggerGuardReady(Player player, double now) => CurrentTriggeredGuard(player, now)
        is not { } guard || guard.Until == 0 && now >= guard.ReadyAt;

    static void EndTriggeredGuard(Player player, TriggeredGuard guard, double now)
    {
        guard.Until = 0;
        guard.ReadyAt = now + guard.RecoverySeconds;
        if (guard.Session is not { } session) return;
        ushort buff = guard.Buff; guard.Buff = 0;
        if (buff != 0) player.Area.Buffs.Clear(player.Id, GuardRow(guard.Effect), player.Id);
        if (guard.Effect == "MoltenShell" && guard.Absorbed > 0)
        {
            double reflected = guard.Absorbed * guard.Reflection / 100d;
            int radius = SkillRadius(player, guard.Gem!.Stats, true, guard.Gem.Effect);
            session.Enqueue(async () =>
            {
                if (!session.Active || player.PlayerLife == 0 || player.LeavingArea) return;
                var deaths = new List<(uint Id, SpawnedMonster M)>();
                foreach (var (id, monster) in player.Area.Monsters.ToArray())
                {
                    MonsterHit? hit; int damage;
                    if (!session.Active || player.Paused || player.PlayerLife == 0) return;
                    if (!monster.Hostile || monster.Dead || monster.Life == 0
                        || Dist(monster.Walk.Position(AreaTime(player)), player.Walk.Position(AreaTime(player))) > radius) continue;
                    // Reflected damage has no spell/attack scaling, crit, penetration, leech or on-hit procs.
                    var resisted = DamageModel.Mitigate(DamagePacket.Of(DamageType.Fire, reflected, reflected),
                        type => MonsterHitResistance(monster, type, default, AreaTime(player)));
                    damage = (int)Math.Round(resisted.MinTotal * MonsterDamageTaken(monster, spell: false,
                        AreaTime(player), elemental: true, hit: false));
                    hit = ApplyMonsterHitLocked(player, monster, damage);
                    if (hit is { } applied)
                        await NotifyMonsterHit(player, id, monster,
                            applied, damage, "Molten Shell reflected damage", deaths: deaths);
                }
                foreach (var (id, monster) in deaths)
                    await MonsterKilled(player, id, monster);
            });
        }
        guard.Armour = 0;
        player.Pools = PoolsFor(player, player.Pools.Level);
        session.Enqueue(() => SendCharacterStats(player, PlayerStatChange.Sources));
    }

    // "Guard Skills have X% increased Duration" is the same increased-duration sum as skill_effect_duration_+%.
    internal static double GuardDuration(int baseMilliseconds, Func<uint, int> player, Func<uint, int> own) =>
        SkillDurationModifiers.Seconds(baseMilliseconds,
            name => player(name) + (name == SkillDurationModifiers.IncreasedDuration ? player(Stat.GuardSkillEffectDurationPlusPct) : 0),
            own);

    // Called immediately after the trigger's successful payment, under the connection lock.
    public static void StartTriggeredGuard(Player player, ProjectileSession session, ushort skill, double now)
    {
        var gem = SupportedSkill(player, skill);
        if (!TriggerGuardReady(player, now)) return;
        var playerStats = CharacterPlayerStats(player);
        int Own(uint name) => GolemStat(gem.Stats, name);
        int Get(uint name) => Own(name) + GolemStat(playerStats, name);
        var guard = TriggeredGuards.GetValue(player, _ => new(player.ProjectileEpoch, CombatLifetime));
        guard.Gem = gem; guard.Skill = skill; guard.Session = session;
        guard.Effect = SkillsFor(player).EffectId(gem.Effect)!;
        guard.Physical = guard.Elemental = 1;
        guard.Percent = guard.Remaining = guard.Absorbed = guard.Armour = guard.Reflection = 0;
        double duration = GuardDuration(Own(Stat.BaseSkillEffectDuration), name => GolemStat(playerStats, name), Own);
        if (guard.Effect == "QuickGuard")
        {
            guard.Percent = Math.Clamp(Own(Stat.QuickGuardDamageAbsorbedPct), 0, 100);
            guard.Remaining = Math.Max(0, Own(Stat.QuickGuardDamageAbsorbLimit));
        }
        else if (guard.Effect == "ImmortalCall")
        {
            var charges = ChargesOf(player);
            int removable = charges.Converted(ChargeType.Endurance) ? 0 : Math.Min(5,
                Math.Max(0, charges.Effective(ChargeType.Endurance) - charges.Limits(ChargeType.Endurance).Minimum));
            if (!RandomRolls.Chance(Get(Stat.ImmortalCallPctChanceToNotConsumeEnduranceCharges), 100, AreaRng(player)))
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
        AfterSeconds(player, duration, () => { _ = CurrentTriggeredGuard(player, AreaTime(player)); });
        double recovery = Math.Max(0, 1 + Get(Stat.VirtualCooldownSpeedPlusPct) / 100d + Own(Stat.BaseCooldownSpeedPlusPct) / 100d);
        guard.RecoverySeconds = recovery > 0 ? SkillsFor(player).CooldownMs(gem.Effect, gem.Level) / 1000d / recovery : double.PositiveInfinity;
        player.Pools = PoolsFor(player, player.Pools.Level);
        if (guard.Effect == "MoltenShell")
            guard.Remaining = (int)Math.Min(Math.Max(0, Own(Stat.MoltenShellMaxDamageAbsorbed)),
                (long)player.Pools.Armour * Math.Max(0, Own(Stat.MoltenShellDamageAbsorbLimitPctOfArmour)) / 100);
        guard.Buff = player.NextBuff++;
        session.Enqueue(() => SendCharacterStats(player, PlayerStatChange.Sources));
        PresentTriggeredGuard(player, now);
        Note($"triggered guard {guard.Effect} ({duration:0.###} s)");
    }

    static ushort GuardRow(string effect) => effect switch { "ImmortalCall" => 110, "MoltenShell" => 114, _ => 1302 };

    // The guard's buff: its countdown and what it has left to absorb (list[0]); a new guard or a hit sets it again.
    public static void PresentTriggeredGuard(Player player, double now)
    {
        if (CurrentTriggeredGuard(player, now) is not { Until: > 0 } guard) return;
        // Non-recovery definitions have no inline stats. The guard's native backend reads additional-data[0]
        // through Buff+0x80 (0x141f84000); an empty list leaves a null pointer and crashes the HUD.
        player.Area.Buffs.Set(player.Id, new(GuardRow(guard.Effect), player.Id, guard.Skill, U68: Buff.Seconds(guard.Until - now),
            ActiveSkill: SkillsFor(player).ActionHash(guard.Gem!.Effect) is ushort action ? (guard.Skill, action) : null,
            List: [(uint)guard.Remaining], KeepInstance: true));
    }

    public static Dictionary<uint, int> WithTriggeredGuard(Player player, Dictionary<uint, int> stats)
    {
        if (CurrentTriggeredGuard(player, AreaTime(player)) is not { Until: > 0 } guard) return stats;
        void Add(uint name, int value)
        {
            if (name is var key) stats[key] = stats.GetValueOrDefault(key) + value;
        }
        Add(Stat.AffectedByGuardSkill, 1);
        Add(Stat.BasePhysicalDamageReductionRating, guard.Armour);
        if (guard.Effect == "QuickGuard") Add(Stat.AvoidBleedPct, 100);
        return stats;
    }

    public static (int Physical, int Other, int Chaos) TriggeredGuardHit(Player player,
        int physical, int other, int chaos, double at)
    {
        (physical, other, chaos) = MitigateTriggeredGuardHit(player, physical, other, chaos, at);
        return AbsorbTriggeredGuardHit(player, physical, other, chaos, at);
    }

    static (int Physical, int Other, int Chaos) MitigateTriggeredGuardHit(Player player,
        int physical, int other, int chaos, double at) =>
        CurrentTriggeredGuard(player, at) is { Until: > 0 } guard
            ? ((int)(physical * guard.Physical), (int)(other * guard.Elemental), chaos)
            : (physical, other, chaos);

    static (int Physical, int Other, int Chaos) AbsorbTriggeredGuardHit(Player player,
        int physical, int other, int chaos, double at)
    {
        if (CurrentTriggeredGuard(player, at) is not { Until: > 0 } guard) return (physical, other, chaos);
        int Absorb(int damage)
        {
            int absorbed = (int)Math.Min(guard.Remaining, (long)damage * guard.Percent / 100);
            guard.Remaining -= absorbed; guard.Absorbed += absorbed;
            return damage - absorbed;
        }
        physical = Absorb(physical); other = Absorb(other); chaos = Absorb(chaos);
        if (guard.Percent > 0)
        {
            if (guard.Remaining == 0) EndTriggeredGuard(player, guard, at);
            else PresentTriggeredGuard(player, at);
        }
        return (physical, other, chaos);
    }
}
