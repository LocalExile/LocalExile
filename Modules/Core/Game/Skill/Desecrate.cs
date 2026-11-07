using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

static partial class Game
{
    internal static async Task<bool> Desecrate(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        ushort source, (int X, int Y) aim, Func<bool> current)
    {
        var gem = SupportedSkill(conn, source);
        double release = AreaTime(conn) + (CastMs(conn, gem.Effect, source) ?? 1000) / 1000d;
        var lifetime = CombatLifetime;
        while (AreaTime(conn) < release || conn.Paused)
        {
            if (!CurrentGameWork || !ReferenceEquals(lifetime, CombatLifetime) || !current() || conn.Closed || conn.LeavingArea || conn.PlayerLife == 0) return false;
            await AreaWait(PlayerUpdateRole.Minions, 25);
        }
        MonsterCatalog.Variety[] pool;
        lock (conn.Monsters)
            pool = conn.Monsters.Values.Where(m => m.Variety is { Supported: true } && m.Variety.LifeData.IsUsableAsCorpse(0)
                && m.Rarity != MonsterRarity.Unique && (m.Hostile || conn.Summons.Fighters.Values.Any(g => g.Monster == m && g.Definition.Kind == "Spectre")))
                .Select(m => m.Variety!).DistinctBy(v => v.Row).ToArray();
        if (pool.Length == 0 && Monsters.Find("Metadata/Monsters/Zombies/Zombie") is { Supported: true } fallback) pool = [fallback];
        if (pool.Length == 0) return false;
        int count = Math.Max(1, GolemStat(gem.Stats, "desecrate_number_of_corpses_to_create"));
        int maximum = Math.Max(count, GolemStat(gem.Stats, "desecrate_maximum_number_of_corpses"));
        byte level = (byte)Math.Clamp(Math.Min(GolemStat(gem.Stats, "desecrate_corpse_level"), Packs.For(conn.Layout.Id)?.AreaLevel ?? 1), 1, 100);
        for (int i = 0; i < count; i++)
        {
            double angle = Math.Tau * i / count;
            var at = (aim.X + (int)(Math.Cos(angle) * 6), aim.Y + (int)(Math.Sin(angle) * 6));
            if (!conn.Layout.WalkableFor(at.Item1, at.Item2, 0)) at = aim;
            await SpawnMonster(remote, stream, ciphers, conn, pool[Random.Shared.Next(pool.Length)], level, MonsterRarity.Normal,
                at.Item1, at.Item2, damagedLife: 0, mods: MonsterModSet.None, unrisen: false,
                ownedCurrent: () => CurrentGameWork && ReferenceEquals(lifetime, CombatLifetime) && current() && conn.PlayerLife > 0,
                owned: (id, corpse) =>
                {
                    corpse.Dead = true; corpse.Life = 0; corpse.NoRewards = true;
                    conn.Summons.Corpses.Enqueue(id);
                    while (conn.Summons.Corpses.Count > maximum)
                    {
                        uint expired = conn.Summons.Corpses.Dequeue();
                        if (conn.Monsters.Remove(expired)) conn.Minions.OutputQueue.Add(new(InstanceProtocol.RemoveObject(expired), "old Desecrate corpse removed"));
                        conn.Instance.World.MonsterOrigins.TryRemove(expired, out _);
                    }
                });
        }
        return true;
    }
}
