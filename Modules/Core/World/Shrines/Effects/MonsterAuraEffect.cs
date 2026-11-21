using System.Runtime.CompilerServices;
using static Game;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Shrines;

/// <summary>Each aura owns its contribution. Overlapping copies of one shrine buff do not stack.</summary>
sealed class MonsterAuraEffect : IShrineEffect
{
    static readonly ConditionalWeakTable<SpawnedMonster, Dictionary<ushort, HashSet<MonsterAuraEffect>>> Owners = new();
    readonly Dictionary<uint, SpawnedMonster> affected = [];
    static string Key(ushort row) => $"Shrine/{row}";

    public Task OnNearbyMonsterEnter(ShrineContext ctx, ShrineInstance shrine, uint monster)
    {
        if (shrine.Definition.Monster is not { } buff) return Task.CompletedTask;
        lock (ctx.Conn.Monsters)
        {
            if (affected.ContainsKey(monster) || !ctx.Conn.Monsters.TryGetValue(monster, out var m) || m.Dead)
                return Task.CompletedTask;
            affected[monster] = m;
            var owners = Owners.GetOrCreateValue(m);
            if (!owners.TryGetValue(buff.BuffRow, out var sources)) owners[buff.BuffRow] = sources = [];
            sources.Add(this);
            if (sources.Count > 1) return Task.CompletedTask;
            var stats = buff.Stats.Where(s => s.Key >= 1 && s.Key <= Stats.Names.Count)
                .GroupBy(s => Stats.Name(s.Key)).ToDictionary(g => g.Key, g => g.Sum(s => s.Value));
            Change(ctx.Conn, monster, m, () => m.TemporaryStats[Key(buff.BuffRow)] = stats);
        }
        return Task.CompletedTask;
    }

    public Task OnNearbyMonsterLeave(ShrineContext ctx, ShrineInstance shrine, uint monster)
    {
        lock (ctx.Conn.Monsters)
            if (affected.Remove(monster, out var m) && shrine.Definition.Monster is { } buff
                && Owners.TryGetValue(m, out var owners) && owners.TryGetValue(buff.BuffRow, out var sources))
            {
                sources.Remove(this);
                if (sources.Count == 0)
                {
                    owners.Remove(buff.BuffRow);
                    Change(ctx.Conn, monster, m, () => m.TemporaryStats.Remove(Key(buff.BuffRow)));
                }
            }
        return Task.CompletedTask;
    }

    static void Change(ClientState conn, uint id, SpawnedMonster monster, Action change)
    {
        var previous = monster.Effects;
        uint oldLife = monster.MaxLife;
        change();
        var effects = monster.Effects;
        monster.RefreshLife(previous, effects);
        uint maximum = monster.MaxLife;
        if (maximum == oldLife && effects.ActorScalePct == previous.ActorScalePct) return;
        var stats = MonsterLifeAndScale(monster);
        if (conn.Instance is { } instance && instance.World.MonsterOrigins.TryGetValue(id, out var origin))
        {
            instance.World.MonsterOrigins[id] = origin with { Object = origin.Object.WithStats(stats) };
            return; // Shared monster sync sends each awake viewer the change with the current life.
        }
        conn.Minions.OutputQueue.Add(new(InstanceEntities.StatsUpdate(id, stats), null));
        if (maximum != oldLife) conn.Minions.OutputQueue.Add(new(InstanceProtocol.LifeUpdate(id, monster.Life), null));
    }

    public async Task OnActivation(ShrineContext ctx, ShrineInstance shrine)
    {
        foreach (uint id in affected.Keys.ToArray()) await OnNearbyMonsterLeave(ctx, shrine, id);
    }
    public Task OnMapUnload(ShrineContext ctx, ShrineInstance shrine) => OnActivation(ctx, shrine);
}
