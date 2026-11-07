using System.Runtime.CompilerServices;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

static partial class Game
{
    sealed record MinionAilmentProfile(MonsterIgnites.Settings Ignite, MonsterIgnites.Settings Bleed,
        MonsterPoisons.Settings Poison, int PoisonChance);
    static readonly ConditionalWeakTable<GolemState.Ability, MinionAilmentProfile> MinionAilmentProfiles = new();

    static void MinionAilmentsLocked(ClientState conn, GolemState.Golem fighter, GolemState.Ability ability,
        uint id, SpawnedMonster target, bool critical, double now)
    {
        if (!MinionAilmentProfiles.TryGetValue(ability, out var profile)) return;
        var packets = new List<byte[]>();
        if (target.Ignites.TryApply(profile.Ignite with { Seconds = profile.Ignite.Seconds * MonsterIgnites.SelfDuration(n => target.Effects[n]) }, critical, now,
            target.Effects["avoid_ignite_%"],
            target.Effects["immune_to_ignite"] != 0, Random.Shared))
            packets.AddRange(ShowDotBuffLocked(conn, id, target.Ignites, IgnitedBuffRow, now));
        if (BleedOnHitLocked(conn, id, target, profile.Bleed, critical, now) is { } bleeding) packets.AddRange(bleeding);
        if (profile.Poison.Seconds > 0 && profile.PoisonChance > 0 && RandomRolls.Chance(profile.PoisonChance, 100, Random.Shared)
            && target.Effects["cannot_be_poisoned"] == 0 && !RandomRolls.Chance(target.Effects["avoid_poison_%"], 100, Random.Shared))
        {
            var p = profile.Poison;
            double min = critical ? p.CriticalMin : p.Min, max = critical ? p.CriticalMax : p.Max;
            target.Poisons.Add(now, min + (max - min) * Random.Shared.NextDouble(), p.Seconds * MonsterPoisons.SelfDuration(n => target.Effects[n]));
        }
        foreach (var packet in packets) conn.Minions.OutputQueue.Add(new(packet, null));
    }
}
