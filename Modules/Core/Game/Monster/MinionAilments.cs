using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

public static partial class Game
{
    sealed record MinionAilmentProfile(MonsterIgnites.Settings Ignite, MonsterIgnites.Settings Bleed,
        MonsterPoisons.Settings Poison, int PoisonChance);
    static readonly RuleTable<GolemState.Ability, MinionAilmentProfile> MinionAilmentProfiles = new();

    static void MinionAilmentsLocked(Player conn, GolemState.Golem fighter, GolemState.Ability ability,
        uint id, SpawnedMonster target, bool critical, double now)
    {
        if (!MinionAilmentProfiles.TryGetValue(ability, out var profile)) return;
        double factor = DoedreSewers.MinionDamageFactor(conn) * MinionTacticsDamage(conn, fighter, id, target, now);
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
            target.Effects[Stat.ImmuneToIgnite] != 0, AreaRng(conn)))
            packets.AddRange(ShowDotBuffLocked(conn, id, target.Ignites, IgnitedBuffRow, now));
        if (BleedOnHitLocked(conn, id, target, profile.Bleed, critical, now) is { } bleeding) packets.AddRange(bleeding);
        if (profile.Poison.Seconds > 0 && profile.PoisonChance > 0 && RandomRolls.Chance(profile.PoisonChance, 100, AreaRng(conn))
            && target.Effects[Stat.CannotBePoisoned] == 0 && !RandomRolls.Chance(target.Effects[Stat.AvoidPoisonPct], 100, AreaRng(conn)))
        {
            var p = profile.Poison;
            double min = critical ? p.CriticalMin : p.Min, max = critical ? p.CriticalMax : p.Max;
            target.Poisons.Add(now, min + (max - min) * AreaRng(conn).NextDouble(), p.Seconds * MonsterPoisons.SelfDuration(n => target.Effects[n]));
        }
        foreach (var packet in packets) conn.Minions.OutputQueue.Add(new(packet, null));
    }
}
