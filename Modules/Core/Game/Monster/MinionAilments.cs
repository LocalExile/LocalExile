using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    sealed record MinionHitProfile(MonsterIgnites.Settings Ignite, MonsterIgnites.Settings Bleed,
        MonsterPoisons.Settings Poison, int PoisonChance, int AdditionalPoisons, Penetration.PerType Penetration,
        DamagePacket LifeLeech, DamagePacket ShieldLeech, GolemState.Ability? AgainstIgnited = null);
    static readonly RuleTable<GolemState.Ability, MinionHitProfile> MinionHitProfiles = new();

    static void MinionExposureOnHitLocked(IReadOnlyDictionary<uint, int> skillStats, SpawnedMonster target,
        DamagePacket mitigated, double now, Random random)
    {
        int chance = GolemStat(skillStats, Stat.Of("minions_inflict_exposure_on_hit_%_chance"));
        if (chance <= 0) return;
        var effects = target.Effects;
        if (effects[Stat.Of("immune_to_exposure")] != 0) return;
        int element = -1;
        double highest = 0;
        for (int i = 0; i < ExposureTypes.Length; i++)
        {
            var type = ExposureTypes[i];
            double amount = mitigated[type].Min * (100 + ElementalHitTakenPercent(effects, target, type, now));
            if (amount <= highest) continue;
            element = i;
            highest = amount;
        }
        if (element >= 0 && RandomRolls.Chance(chance, 100, random))
            Expose(target, element, DefaultExposure, now);
    }

    static int RecentMinionSkillIgnites(GolemState.Golem minion, int effect, double now)
    {
        if (minion.RecentSkillIgnites?.GetValueOrDefault(effect) is not { } ignites) return 0;
        while (ignites.TryPeek(out double at) && at <= now - RecentEvents.WindowSeconds) ignites.Dequeue();
        return ignites.Count;
    }

    static void MinionAilmentsLocked(Player player, GolemState.Golem minion, GolemState.Ability ability,
        IReadOnlyDictionary<uint, int> own, uint id, SpawnedMonster target, bool critical, double now, double factor,
        bool controlledBlaze, int recentIgnites)
    {
        if (!MinionHitProfiles.TryGetValue(ability, out var profile)) return;
        if (factor != 1)
        {
            MonsterIgnites.Settings Scale(MonsterIgnites.Settings s) => s with
            { Min = s.Min * factor, Max = s.Max * factor, CriticalMin = s.CriticalMin * factor, CriticalMax = s.CriticalMax * factor };
            var poison = profile.Poison;
            profile = profile with { Ignite = Scale(profile.Ignite), Bleed = Scale(profile.Bleed),
                Poison = poison with { Min = poison.Min * factor, Max = poison.Max * factor,
                    CriticalMin = poison.CriticalMin * factor, CriticalMax = poison.CriticalMax * factor } };
        }
        var packets = new List<byte[]>();
        double igniteMore = controlledBlaze ? ControlledBlazeIgniteFactor(
            GolemStat(own, Stat.SupportRecentIgnitesIgniteDamagePerRecentIgnitePlusPctFinal),
            GolemStat(own, Stat.SupportRecentIgnitesIgniteDamagePerRecentIgnitePlusPctFinalMaximum), recentIgnites) : 1;
        var ignite = profile.Ignite with
        {
            Chance = ActorScale.Saturate((long)profile.Ignite.Chance + target.Effects[Stat.ChanceToBeIgnitedPct]),
            Seconds = profile.Ignite.Seconds * MonsterIgnites.SelfDuration(n => target.Effects[n]),
            Min = profile.Ignite.Min * igniteMore, Max = profile.Ignite.Max * igniteMore,
            CriticalMin = profile.Ignite.CriticalMin * igniteMore, CriticalMax = profile.Ignite.CriticalMax * igniteMore,
        };
        if (target.Ignites.TryApply(ignite, critical, now, target.Effects[Stat.AvoidIgnitePct],
            target.Effects[Stat.ImmuneToIgnite] != 0 || target.Effects[Stat.CannotBeIgnited] != 0, AreaRng(player)))
        {
            if (controlledBlaze)
            {
                var skills = minion.RecentSkillIgnites ??= [];
                if (!skills.TryGetValue(ability.Effect, out var ignites)) skills.Add(ability.Effect, ignites = new());
                ignites.Enqueue(now);
            }
            player.Area.ActiveIgniteMonsters.Add(id);
            player.Area.RecentlyIgnitedMonsters.Add(id);
            ApplyIgniteFirePenaltyLocked(target, ignite, now);
            ShowDotBuffLocked(player, id, target.Ignites, IgnitedBuffRow, now);
        }
        if (BleedOnHitLocked(player, id, target, profile.Bleed, critical, now) is { } bleeding) packets.AddRange(bleeding);
        if (profile.Poison.Seconds > 0 && profile.PoisonChance > 0 && RandomRolls.Chance(profile.PoisonChance, 100, AreaRng(player))
            && target.Effects[Stat.CannotBePoisoned] == 0 && !RandomRolls.Chance(target.Effects[Stat.AvoidPoisonPct], 100, AreaRng(player)))
        {
            var p = profile.Poison;
            double min = critical ? p.CriticalMin : p.Min, max = critical ? p.CriticalMax : p.Max;
            double dps = min + (Math.Max(min, max) - min) * AreaRng(player).NextDouble();
            if (ApplyHitPoisonsLocked(target.Poisons, now, dps, p.Seconds * MonsterPoisons.SelfDuration(n => target.Effects[n]),
                profile.AdditionalPoisons))
                player.Area.ActivePoisonMonsters.Add(id);
        }
        foreach (var packet in packets) player.Minions.OutputQueue.Add(new(packet, null));
    }
}
