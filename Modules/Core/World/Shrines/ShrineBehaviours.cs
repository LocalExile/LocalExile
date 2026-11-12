using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using static Game;

namespace GameServer.Modules.Shrines;

/// <summary>
/// The pre-activation effect of a shrine whose Shrines row names guard packs (<c>MonsterPacks</c>): it brings those
/// monsters at the area's level through the monsters' own spawn path and puts the shrine's monster buff
/// (<c>Monster_ShrineBuffsKey</c>) on each of them. The standard map shrines name no packs, so it is not added for them;
/// the boss and influence shrines (the apparition scarab's, the Conquerors') get it from
/// <see cref="ShrineRegistry.Before"/> as soon as they can spawn.
/// <para>The guards are the shrine's own: they stay when it is claimed, while its pre-activation effects end.</para>
/// </summary>
sealed class GuardedPacksEffect : IShrineEffect
{
    public Task OnSpawn(ShrineContext ctx, ShrineInstance shrine) =>
        shrine.Definition.GuardPacks.Count == 0 ? Task.CompletedTask : SpawnGuards(ctx.Conn, shrine);

    static async Task SpawnGuards(ClientState conn, ShrineInstance shrine)
    {
        try
        {
            var (remote, stream, ciphers) = conn.Wire;
            byte level = ShrineManager.AreaLevel(conn);
            var created = new List<string>(shrine.Definition.GuardPacks.Count);
            int buffed = 0;
            foreach (var pack in shrine.Definition.GuardPacks)
            {
                int count = Random.Shared.Next(Math.Max(1, pack.MinCount), Math.Max(pack.MinCount, pack.MaxCount) + 1);
                int made = 0;
                for (int i = 0; i < count; i++)
                {
                    if (Roll(pack.Monsters) is not { Supported: true } variety) break;
                    var (x, y) = Around(shrine.At.X, shrine.At.Y, i, count);
                    uint guard = await SpawnMonster(remote, stream, ciphers, conn, variety, level,
                        MonsterRarity.Normal, x, y, alone: false, log: false);
                    if (guard == 0) break;
                    made++;
                    if (shrine.Definition.Monster is { } monsterBuff
                        && await GuardBuff(remote, stream, ciphers, conn, guard, monsterBuff))
                        buffed++;
                }
                created.Add($"{pack.Id} × {made}");
            }
            Console.WriteLine($"[gateway] {remote} shrine {shrine.Definition.Id} at {shrine.At} guards, level {level}: " +
                string.Join(", ", created) +
                (shrine.Definition.Monster is { } buff ? $"; shrine buff {buff.Template} (row {buff.BuffRow}) on {buffed}" : ""));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[gateway] shrine {shrine.Definition.Id} guards failed: {ex}");
        }
    }

    /// <summary>The shrine's monster buff on one of its guards: the BuffDefinitions row the Shrines row names, and its
    /// BuffTemplates values as the monster's effects. <see cref="SpawnedMonster.EncounterStats"/> is where a monster's buff
    /// stats are read, keyed by stat name: the client key is a Stats row + 1, so the name comes back from Stats.</summary>
    static async Task<bool> GuardBuff(string remote, System.Net.Sockets.NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, uint guard, ShrineCatalog.MonsterBuff buff)
    {
        var stats = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, value) in buff.Stats)
            if (key >= 1 && key <= Stats.Names.Count)
            {
                string name = Stats.Name(key);
                stats[name] = stats.GetValueOrDefault(name) + value;
            }
        ushort instance;
        lock (conn.Monsters)
        {
            if (!conn.Monsters.TryGetValue(guard, out var monster)) return false;
            instance = conn.NextBuff++;
            if (stats.Count > 0) monster.EncounterStats = stats;
        }
        await Program.Send(remote, stream, ciphers, InstanceEntities.AddBuff(guard, instance, buff.BuffRow, guard),
            $"0x01c1 guard 0x{guard:x} has shrine buff {buff.Template} (BuffDefinitions row {buff.BuffRow})");
        return true;
    }

    /// <summary>One of a pack's varieties: its MonsterPackEntries rows by their weight.</summary>
    static MonsterCatalog.Variety? Roll(IReadOnlyList<(ushort Variety, int Weight)> monsters)
    {
        int total = monsters.Sum(m => Math.Max(0, m.Weight));
        if (monsters.Count == 0 || total <= 0) return null;
        int roll = Random.Shared.Next(total);
        foreach (var (variety, weight) in monsters)
            if ((roll -= Math.Max(0, weight)) < 0) return Monsters.Find(variety.ToString());
        return Monsters.Find(monsters[^1].Variety.ToString());
    }

    /// <summary>Where guard <paramref name="index"/> of a pack stands: evenly around the shrine, on a ring just outside
    /// its object size, alternating radii so the guards do not stack.</summary>
    static (int X, int Y) Around(int x, int y, int index, int count)
    {
        double angle = Math.PI * 2 * index / Math.Max(1, count);
        int radius = ShrinePlacement.ObjectSize + 1 + index % 3;
        return (x + (int)Math.Round(Math.Cos(angle) * radius), y + (int)Math.Round(Math.Sin(angle) * radius));
    }
}
