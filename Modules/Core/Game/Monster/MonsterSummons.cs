using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    sealed record MonsterSummonParent(uint Id, SpawnedMonster Monster, bool DiesWithParent, bool DiesWhenDeleted);
    static readonly RuleTable<SpawnedMonster, MonsterSummonParent> MonsterSummonParents = new();

    // Identity matters: a replacement actor under the same id is not this summon skill's parent.
    public static bool MonsterSummonerCurrentLocked(Player player, uint id, SpawnedMonster parent) =>
        CurrentGameWork && !player.Closed && !player.LeavingArea && !parent.Dead && parent.Life > 0
        && player.Area.Monsters.TryGetValue(id, out var held) && ReferenceEquals(held, parent);

    public static void RegisterMonsterSummonLocked(SpawnedMonster child, uint id, SpawnedMonster parent,
        SkillCatalog.MonsterSummonInfo summon)
    {
        child.SummonerId = id;
        child.NoRewards = summon.NoRewards;
        if (summon.DiesWithParent || summon.DiesWhenParentDeleted)
            MonsterSummonParents.AddOrUpdate(child, new(id, parent, summon.DiesWithParent, summon.DiesWhenParentDeleted));
        else MonsterSummonParents.Remove(child);
    }

    // The runner owns this lifecycle, as it owns hostile AI. These are deaths without a killer or kill rewards.
    public static int TickMonsterSummonParentsLocked(Player player, double now)
    {
        if (player.Closed || player.LeavingArea || player.Paused || !IsMonsterRunner(player)) return 0;
        int ended = 0;
        foreach (var (id, child) in player.Area.Monsters)
        {
            if (!MonsterSummonParents.TryGetValue(child, out var parent)) continue;
            if (child.Dead || child.Life == 0) { MonsterSummonParents.Remove(child); continue; }
            bool present = player.Area.Monsters.TryGetValue(parent.Id, out var held) && ReferenceEquals(held, parent.Monster)
                || player.Area.DormantMonsters.TryGetValue(parent.Id, out held)
                    && ReferenceEquals(held, parent.Monster);
            if (!(present ? parent.DiesWithParent && (parent.Monster.Dead || parent.Monster.Life == 0) : parent.DiesWhenDeleted)) continue;
            EndMonsterSummonLocked(player, id, child, now);
            ended++;
        }
        return ended;
    }

    static void EndMonsterSummonLocked(Player? player, uint id, SpawnedMonster child, double now)
    {
        child.Life = 0;
        child.Dead = true;
        child.ActionGeneration++;
        var at = child.Walk.Position(now);
        child.Walk.Teleport(at.X, at.Y, now);
        player?.Minions.OutputQueue.Add(new(InstanceEntities.StatsUpdate(id, [(Stat.HideMiniLifeBar, 1)]),
            "summon's parent ended", id, child, WorldId: id));
        player?.Minions.OutputQueue.Add(new([], "summon dies with its parent", id, child, PlayerMinions.OutputKind.Life));
        MonsterSummonParents.Remove(child);
    }

    public static IDisposable CleanupMonsterSummons() => new MonsterSummonCleanup();
    sealed class MonsterSummonCleanup : IDisposable
    {
        public void Dispose()
        {
            foreach (var instance in Program.Instances.Snapshot())
            {
                var world = instance.World;
                var viewer = world.Runner;
                if (viewer is null || viewer.Closed || viewer.LeavingArea || !ReferenceEquals(viewer.Instance, instance))
                    viewer = null;
                foreach (var (id, child) in world.Area.Monsters.Concat(world.Area.DormantMonsters))
                    if (!child.Dead && (MonsterSummonParents.TryGetValue(child, out _)
                        || IsEssenceExplosiveInsect(child.Variety?.Id) && child.SummonerId is not null))
                        EndMonsterSummonLocked(viewer, id, child, world.Clock.Elapsed.TotalSeconds);
            }
        }
    }
}
