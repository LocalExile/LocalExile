using GameServer.Core.Protocol.Instance;

static partial class Game
{
    // The party bonus by the client's own rules, as its party panel shows them (0x14023bbc0 with the constants of
    // 0x1424840d0, read live 2026-11-04): each player beyond the first (Program.SetPartySize) adds to the monsters' life by
    // rarity (+50% normal, +60% magic, +80% rare, +100% unique) and 23% more currency items; other items' quantity and
    // rarity, the monsters' damage and experience have no party bonus.
    internal static int PartyMonsterLife(ClientState conn, MonsterRarity rarity) => Math.Max(0, conn.PartySize - 1) * rarity switch
    {
        MonsterRarity.Magic => 60,
        MonsterRarity.Rare => 80,
        MonsterRarity.Unique => 100,
        _ => 50,
    };

    const double PartyCurrencyPerPlayer = 0.23;

    // The extra currency items of a kill's drops: each currency item drops again with 23% per player beyond the first
    // (whole multiples drop that many more times).
    internal static IEnumerable<ItemDrops.Drop> PartyCurrency(ClientState conn, IReadOnlyList<ItemDrops.Drop> drops, Random rng)
    {
        double more = Math.Max(0, conn.PartySize - 1) * PartyCurrencyPerPlayer;
        if (more <= 0) yield break;
        foreach (var drop in drops.Where(d => d.Type.Class == "StackableCurrency").ToList())
        {
            int times = (int)Math.Floor(more) + (rng.NextDouble() < more - Math.Floor(more) ? 1 : 0);
            for (int i = 0; i < times; i++) yield return drop with { Item = drop.Item };
        }
    }
}
