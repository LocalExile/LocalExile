using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    sealed record MinionHitProfile(MonsterIgnites.Settings Ignite, MonsterIgnites.Settings Bleed,
        MonsterPoisons.Settings Poison, int PoisonChance, Penetration.PerType Penetration, DamagePacket LifeLeech);
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

    static void MinionAilmentsLocked(Player player, GolemState.Golem fighter, GolemState.Ability ability,
        uint id, SpawnedMonster target, bool critical, double now)
    {
        if (!MinionHitProfiles.TryGetValue(ability, out var profile)) return;
        double factor = DoedreSewers.MinionDamageFactor(player) * MinionTacticsDamage(player, fighter, id, target, now);
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
        if (target.Ignites.TryApply(profile.Ignite with { Seconds = profile.Ignite.Seconds * MonsterIgnites.SelfDuration(n => target.Effects[n]) }, critical, now,
            target.Effects[Stat.AvoidIgnitePct],
            target.Effects[Stat.ImmuneToIgnite] != 0, AreaRng(player)))
        {
            player.Area.ActiveIgniteMonsters.Add(id);
            player.Area.RecentlyIgnitedMonsters.Add(id);
            ShowDotBuffLocked(player, id, target.Ignites, IgnitedBuffRow, now);
        }
        if (BleedOnHitLocked(player, id, target, profile.Bleed, critical, now) is { } bleeding) packets.AddRange(bleeding);
        if (profile.Poison.Seconds > 0 && profile.PoisonChance > 0 && RandomRolls.Chance(profile.PoisonChance, 100, AreaRng(player))
            && target.Effects[Stat.CannotBePoisoned] == 0 && !RandomRolls.Chance(target.Effects[Stat.AvoidPoisonPct], 100, AreaRng(player)))
        {
            var p = profile.Poison;
            double min = critical ? p.CriticalMin : p.Min, max = critical ? p.CriticalMax : p.Max;
            if (target.Poisons.Add(now, min + (max - min) * AreaRng(player).NextDouble(), p.Seconds * MonsterPoisons.SelfDuration(n => target.Effects[n])))
                player.Area.ActivePoisonMonsters.Add(id);
        }
        foreach (var packet in packets) player.Minions.OutputQueue.Add(new(packet, null));
    }
}
