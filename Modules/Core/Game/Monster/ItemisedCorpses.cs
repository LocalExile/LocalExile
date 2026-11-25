using GameServer.Core;
using GameServer.Core.Protocol.Instance;

public static partial class Game
{
    static (Dictionary<int, int> ByItem, HashSet<int> Bodies) ItemisedCorpses = ([], []);

    static void LoadItemisedCorpseData()
    {
        var rows = ClientTables.TableOrEmpty("ItemisedCorpse")
            .ToDictionary(row => row.Int("BaseItem"), row => row.Int("MonsterVariety"));
        ItemisedCorpses = (rows, new(rows.Values));
    }

    static readonly MonsterModSet SpectralCorpseStats = MonsterModSet.None with
        { Effects = new(new Dictionary<uint, int> { [Stat.IsSpectreCorpse] = 1 }) };

    internal static bool IsSpectreCorpse(SpawnedMonster monster) => monster.Mods.Effects[Stat.IsSpectreCorpse] != 0;

    internal static bool CorpseTypeAllowsUse(SpawnedMonster monster, bool minionSkill = false, bool cannotUseSpectreCorpses = false) =>
        monster.Variety?.LifeData.IsUsableAsCorpse(monster.Life, isSpectreCorpse: IsSpectreCorpse(monster),
            skillIsMinionSkill: minionSkill, cannotUseSpectreCorpses: cannotUseSpectreCorpses) == true;

    static async Task<bool> TryUseItemisedCorpse(Player player, PlayerInventories items, byte row, InventoryItem item, ItemCatalog.BaseType type)
    {
        if (!ItemisedCorpses.ByItem.TryGetValue(type.Row, out int body)) return false;
        if (row is not (PlayerInventories.MainInventory or PlayerInventories.ExpandedInventory)
            || (uint)body >= (uint)Monsters.All.Count || Monsters.All[body] is not { Supported: true } variety) return true;

        var instance = player.Instance;
        int epoch = player.ProjectileEpoch;
        bool Floor(int x, int y) => player.Layout.WalkableFor(x, y, 0) && !player.Barrier.Contains((x, y));
        var at = NextToPlayer(player, bodySize: 0);
        bool Current() => player.PlayerLife > 0 && !player.Paused && !InTown(player)
            && ReferenceEquals(instance, player.Instance) && player.ProjectileEpoch == epoch
            && Floor(at.X, at.Y);

        PlayerInventories.Consumption? consumed = null;
        bool TakeItem()
        {
            if (!items.Items(row).Any(held => held.Id == item.Id && ReferenceEquals(held.Item, item.Item))) return false;
            consumed = items.ConsumeOne(row, item.Id);
            return consumed is not null;
        }

        byte level = (byte)Math.Clamp(Packs.For(player.Layout.Id)?.AreaLevel ?? Math.Max(1, (int)item.Item.ItemLevel), 1, 100);
        uint id = await PlaceItemisedCorpse(player, variety, at, level, SpectralCorpseStats, Current, TakeItem);
        if (id == 0 || consumed is not { } taken) return true;
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), [item.Id],
            taken.Left is { } left ? [left] : []), $"0x00bf {type.Name} used to place corpse 0x{id:x}");
        return true;
    }

    static Task<uint> PlaceItemisedCorpse(Player player, MonsterCatalog.Variety variety, (int X, int Y) at, byte level,
        MonsterModSet mods, Func<bool>? current = null, Func<bool>? admission = null)
    {
        // The item creates a corpse directly. Its Life flag must allow corpse selection even when the living
        // monster type (such as Shadow Berserker) normally produces no usable body.
        var corpseType = variety with { LifeData = variety.LifeData with { CorpseUsable = true } };
        return SpawnMonster(player, corpseType, level, MonsterRarity.Normal, at.X, at.Y, damagedLife: 1,
            mods: mods, unrisen: false, extraStats: [(Stat.IsSpectreCorpse, 1)], ownedInitialDeath: true,
            ownedCurrent: current, ownedAdmission: admission, placement: MonsterPlacement.Established, nonBlocking: true,
            owned: (_, corpse) =>
            {
                corpse.CreatedAsCorpse = corpse.Dead = corpse.NoRewards = true;
                corpse.Life = 0;
            });
    }

    static bool WasItemisedCorpse(SavedMonster saved) =>
        saved.Life == 0 && saved.Mods.Effects[Stat.IsSpectreCorpse] != 0 && ItemisedCorpses.Bodies.Contains(saved.Variety);

    static IEnumerable<SavedMonster> KeepItemisedCorpses(IEnumerable<SpawnedMonster> monsters, double now)
    {
        foreach (var corpse in monsters)
            if (corpse.CreatedAsCorpse && corpse.Dead && !corpse.CorpseConsumed && IsSpectreCorpse(corpse)
                && corpse.Variety is { } body && ItemisedCorpses.Bodies.Contains(body.Row))
            {
                var at = corpse.Walk.Position(now);
                yield return new(body.Row, corpse.Level, MonsterRarity.Normal, true, at.X, at.Y, 0) { Mods = corpse.Mods };
            }
    }
}
