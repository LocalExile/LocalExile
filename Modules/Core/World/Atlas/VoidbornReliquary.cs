using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Atlas;

/// <summary>
/// The Voidborn Reliquary (<c>ClassicTreasury_Cosmic</c>), which a Voidborn Reliquary Key opens through its
/// MapDeviceRecipes row: no monsters, one Relic Vault (<c>ForgottenReliquaryCosmic</c>, the area's own chest) holding a
/// foil unique of item level 75. The reliquary chests stay out of the ordinary chests (CampaignChests), so this places it
/// where the area's room does, else at the area's far end from the arrival (where the vault stands; the room file
/// reliquary.arm names no chest point). A click opens it and drops one outcome of world.db's
/// <c>voidborn_reliquary_outcomes</c> by weight (the unique's rarity times its supporter entries), in its foil colour
/// (RelicItemEffectVariations 1–6: Amethyst, Verdant, Ruby, Cobalt, Sunset, Aureate).
/// </summary>
public static class VoidbornReliquary
{
    public const string AreaId = "ClassicTreasury_Cosmic";
    const string ChestPath = "Metadata/Chests/Legacy/ForgottenReliquaryCosmic";
    const byte ItemLevel = 75;

    sealed record Outcome(string Reward, byte Foil, double Weight);
    static IReadOnlyList<Outcome> _outcomes = [];

    /// <summary>The chest's unique name, Words "Voidborn Chest" (row 915, Text2): over the Chests row's "Relic Vault"
    /// it shows the unique nameplate, and the client adds its own FlavourText line (ChestForgottenReliquaryCosmic).</summary>
    static uint? _nameWord;

    sealed class ChestState
    {
        public uint Id;
        public int X, Y;
        public bool Opened;
    }
    static readonly RuleTable<object, ChestState> Chests = new();

    public static void LoadData()
    {
        _nameWord = ClientTables.TableOrEmpty("Words").Where(r => r.Text("Text2") == "Voidborn Chest")
            .Select(r => (uint?)r.UInt("HASH32")).FirstOrDefault();
        if (GameData.Store is not { } store) return;
        _outcomes = [.. store.LoadRows("voidborn_reliquary_outcomes", "reward, foil").Select(r => new Outcome(
            (string)r["reward"]!, (byte)Convert.ToInt64(r["foil"]), Convert.ToDouble(r["weight"])))];
    }

    public static void Register(string owner)
    {
        GameEvents.AreaGenerated.Add(owner, PlaceChest);
        GameEvents.ObjectUse.Add(owner, OpenChest);
    }

    static async Task PlaceChest(Player player)
    {
        if (player.Layout.Id != AreaId || player.Instance is not { } instance || Chests.TryGetValue(instance, out _)) return;
        if (QuestObjectTypes.Find(ChestPath) is not { } info || !QuestObject.CanSerialize(info.Components))
        {
            Console.WriteLine($"[voidborn] {player.Connection.Remote} no {ChestPath} object type: the reliquary stays empty");
            return;
        }
        // The room's own chest placement when the level data has one; else, as the vault stands in the area, at its far
        // end: the open cell farthest (by walk) from the arrival and its portal.
        var placed = ServerLevels.For(player.Layout.Id, player.Seed)?.RoomObjects
            .FirstOrDefault(o => o.Type.StartsWith("Metadata/Chests/Legacy/ForgottenReliquary", StringComparison.Ordinal));
        var (x, y) = placed is { } o ? player.Layout.Near((o.X, o.Y))
            : FarthestOpenCell(player.Layout) ?? player.Layout.Near((player.Layout.SpawnX + 20, player.Layout.SpawnY));
        var chest = new ChestState { Id = player.TakeObjectId(), X = x, Y = y };
        Chests.AddOrUpdate(instance, chest);
        await SendChest(player, info, chest, "placed");
        Console.WriteLine($"[voidborn] {player.Connection.Remote} Voidborn Reliquary: Relic Vault 0x{chest.Id:x} at ({x}, {y})"
            + (placed is null ? " (the far end from the arrival)" : " (room placement)"));
    }

    /// <summary>The walkable cell with room around it (clearance 3) the longest walk from the arrival: a flood fill over
    /// the area's walkable cells. Null when the layout has no walk data.</summary>
    static (int X, int Y)? FarthestOpenCell(AreaLayout layout)
    {
        int w = layout.GridW, h = layout.GridH;
        if (w <= 0 || h <= 0 || !layout.WalkableFor(layout.SpawnX, layout.SpawnY, 0)) return null;
        var distance = new int[w * h];
        Array.Fill(distance, -1);
        var queue = new Queue<(int X, int Y)>();
        distance[layout.SpawnY * w + layout.SpawnX] = 0;
        queue.Enqueue((layout.SpawnX, layout.SpawnY));
        (int X, int Y)? best = null;
        int bestDistance = -1;
        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();
            int d = distance[cy * w + cx];
            if (d > bestDistance && layout.WalkableFor(cx, cy, 3)) (best, bestDistance) = ((cx, cy), d);
            foreach (var (nx, ny) in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
                if (nx >= 0 && ny >= 0 && nx < w && ny < h && distance[ny * w + nx] < 0 && layout.WalkableFor(nx, ny, 0))
                {
                    distance[ny * w + nx] = d + 1;
                    queue.Enqueue((nx, ny));
                }
        }
        return best;
    }

    static Task<bool> SendChest(Player player, QuestObjectTypes.Info info, ChestState chest, string what)
    {
        var obj = new QuestObject(chest.X, chest.Y, info.Components, Opened: chest.Opened)
        {
            Model = info.AnimatedModel,
            Rarity = (byte)ItemRarity.Unique,
            NameWords = _nameWord is uint word ? [word] : [],
        };
        return SendWorldCreate(player, chest.Id,
            () => InstanceEntities.CreateObject(chest.Id, InstanceEntities.ObjectTypeHash(ChestPath), obj.Blob()),
            $"0x02bb Voidborn Relic Vault 0x{chest.Id:x} {what} at ({chest.X}, {chest.Y})", wake: true);
    }

    static async Task<bool> OpenChest(Player player, InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        if (player.Instance is not { } instance || !Chests.TryGetValue(instance, out var chest) || use.TargetId != chest.Id) return false;
        if (chest.Opened) return true;
        if (!TryBeginObjectWalk(player, requestedAt, chest.X, chest.Y, 4, out var walk)) return true;
        var (action, now, _, _, arrive) = walk;
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (!PlayerActionCurrent(player, action) || chest.Opened) return true;
        chest.Opened = true;
        await SendWorldRecord(player, chest.Id, InstanceProtocol.AnimationUpdate(chest.Id, 0, 0, 1),
            $"0x02bc Voidborn Relic Vault 0x{chest.Id:x} opens");
        if (QuestObjectTypes.Find(ChestPath) is { } info)
        {
            await SendWorldRemoval(player, chest.Id, null);
            await SendChest(player, info, chest, "opened");
        }
        var random = AreaRng(player);
        if (Roll(random) is not { } outcome || Create(player, outcome, random) is not { } item)
        {
            Console.WriteLine($"[voidborn] {player.Connection.Remote} Relic Vault opened: no reward ({_outcomes.Count} outcomes in world.db)");
            await PlayerOf(player).Tell("The Relic Vault is empty: the Voidborn outcomes are missing from world.db.");
            return true;
        }
        var at = player.Layout.Near((chest.X + 1, chest.Y + 1));
        var ground = new WorldItemObject(at.X, at.Y, item);
        uint id = player.Instance.Ground.Add(ground, player);
        await SendGroundItem(player, id, ground, $"Voidborn Reliquary reward {outcome.Reward} (foil {outcome.Foil})", justDropped: true);
        Console.WriteLine($"[voidborn] {player.Connection.Remote} Relic Vault opened: {outcome.Reward} (foil {outcome.Foil}) at ({at.X}, {at.Y})");
        return true;
    }

    static Outcome? Roll(Random random)
    {
        double total = _outcomes.Sum(o => o.Weight);
        if (total <= 0) return null;
        double pick = random.NextDouble() * total;
        foreach (var outcome in _outcomes)
            if ((pick -= outcome.Weight) < 0) return outcome;
        return _outcomes[^1];
    }

    /// <summary>The outcome's unique (a unique map by its name when it is one), foil, item level 75.</summary>
    static Item? Create(Player player, Outcome outcome, Random random)
    {
        var uniques = UniquesFor(player);
        var item = uniques.Create(outcome.Reward, random) ?? uniques.NamedMap(outcome.Reward);
        return item is null ? null : item with { RelicStyle = outcome.Foil, ItemLevel = ItemLevel };
    }
}
