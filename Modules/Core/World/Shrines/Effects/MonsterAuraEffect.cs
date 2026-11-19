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
                .GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.Sum(s => s.Value));
            Change(ctx.Conn, monster, m, () => m.SetTemporaryStats(Key(buff.BuffRow), stats));
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
                    Change(ctx.Conn, monster, m, () => m.RemoveTemporaryStats(Key(buff.BuffRow)));
                }
            }
        return Task.CompletedTask;
    }

    // Its maximum life follows the aura's stats from the frame they are built in (SpawnedMonster.AfterStatsRebuild).
    static void Change(Player conn, uint id, SpawnedMonster monster, Action change)
    {
        change();
        monster.AfterStatsRebuild((was, effects) =>
        {
            int before = was.MaxLife(100000);
            uint oldLife = monster.MaxLife;
            uint maximum = (uint)Math.Clamp((long)oldLife * effects.MaxLife(100000) / Math.Max(1, before), 1, uint.MaxValue);
            monster.MaxLife = maximum;
            monster.Life = (uint)Math.Clamp(Math.Ceiling(monster.Life * (double)maximum / Math.Max(1, oldLife)), 0, maximum);
            var stats = new List<(uint, int)> { (Stat.MaximumLife, (int)Math.Min(int.MaxValue, maximum)) };
            if (Stat.ActorScalePlusPct is var scale) stats.Add((scale, effects.ActorScalePct));
            conn.Minions.OutputQueue.Add(new(InstanceEntities.StatsUpdate(id, stats), null));
            if (maximum != oldLife) conn.Minions.OutputQueue.Add(new(InstanceProtocol.LifeUpdate(id, monster.Life), null));
        });
    }

    public async Task OnActivation(ShrineContext ctx, ShrineInstance shrine)
    {
        foreach (uint id in affected.Keys.ToArray()) await OnNearbyMonsterLeave(ctx, shrine, id);
    }
    public Task OnMapUnload(ShrineContext ctx, ShrineInstance shrine) => OnActivation(ctx, shrine);
}
