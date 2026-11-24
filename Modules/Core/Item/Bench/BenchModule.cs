using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Modding;
using PlayerWalk = GameServer.Core.World.PlayerWalk;
using static Program;

namespace GameServer.Modules.CraftingBench;

/// <summary>The crafting bench in the hideout.</summary>
public sealed class BenchModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        GameEvents.HideoutReady.Add(module.Manifest.Id, player =>
        {
            return BenchFeature.SpawnBench(player);
        });
        GameEvents.AreaGenerated.Add(module.Manifest.Id, player =>
        {
            return BenchFeature.SpawnTownBench(player);
        });
        GameEvents.PlayerEntered.Add(module.Manifest.Id, player =>
        {
            return BenchFeature.UnlockAreaRecipes(player);
        });
        GameEvents.ObjectUse.Add(module.Manifest.Id, async (player, use, at) =>
        {
            if (player.CraftingBenchObject == 0 || use.TargetId != player.CraftingBenchObject) return false;
            await BenchFeature.OpenCraftingBench(player, use.TargetId, at);
            return true;
        });
        GameEvents.Packet(InstanceBench.MoveId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await BenchFeature.MoveToBench(player, InstanceBench.ReadMove(bytes));
            return true;
        });
        GameEvents.Packet(InstanceBench.CraftId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            await BenchFeature.CraftBench(player, InstanceBench.ReadCraft(bytes));
            return true;
        });
        GameEvents.Packet(InstanceBench.CloseId).Add(module.Manifest.Id, async (player, bytes) =>
        {
            return await BenchFeature.CloseBench(player, InstanceBench.ReadClose(bytes));
        });
    }
}

static class BenchFeature
{
    // The bench in the hideout, where the league has one.
    internal static async Task SpawnBench(Player player)
    {
        var scene = player.CurrentHideoutScene;
        if (LeagueFor(player).Rules.CraftingBench && scene?.Bench is { } bench
            && HideoutCatalog.TypeOf(bench.Type!) is var (components, states)
            && HideoutObject.CanSerialize(components))
        {
            uint benchId = player.TakeObjectId();
            var obj = new HideoutObject(bench.X, bench.Y, bench.Rotation, components,
                new HideoutDoodad(bench.Hash, (byte)bench.Variation), [.. states.Select(_ => 0)]);
            await global::Game.SendWorldCreate(player, benchId, () => InstanceEntities.CreateObject(benchId,
                InstanceEntities.ObjectTypeHash(bench.Type!), obj.Blob()),
                    $"0x02bb create crafting bench 0x{benchId:x} left of guild stash at ({bench.X}, {bench.Y})", wake: true, awaitBirth: false);
            player.CraftingBenchObject = benchId;
            player.CraftingBenchAt = (bench.X, bench.Y);
            global::Game.SetAreaBench(player, benchId, (bench.X, bench.Y));
            await RegisterStationaryCollision(player, benchId, bench.Type!, bench.X, bench.Y, bench.Rotation, "bench", bench.Model);
        }
    }

    // A town's bench (TownBench): the room's own line places it, next to the stash. The hideout's comes from its decoration
    // (SpawnBench) and a hideout has no TownBench entry.
    internal static async Task SpawnTownBench(Player player)
    {
        if (!LeagueFor(player).Rules.CraftingBench || player.CurrentHideoutScene is not null) return;
        if (HideoutCatalog.ByArea(player.Layout.Hash) is not null) return;
        if (TownBench.In(player.Layout.Hash) is not { } bench) return;
        if (HideoutCatalog.TypeOf(bench.Type) is not var (components, states) || !HideoutObject.CanSerialize(components))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} crafting bench: {bench.Type} has no serialisable components (data/object-types.json)");
            return;
        }
        uint benchId = player.TakeObjectId();
        var obj = new HideoutObject(bench.X, bench.Y, bench.Rotation, components, States: [.. states.Select(_ => 0)], Model: bench.Model);
        await global::Game.SendWorldCreate(player, benchId, InstanceEntities.CreateObject(benchId,
            InstanceEntities.ObjectTypeHash(bench.Type), obj.Blob()),
            $"0x02bb create crafting bench 0x{benchId:x} at ({bench.X}, {bench.Y}) of {player.Layout.Id}");
        await global::Game.SendWorldRecord(player, benchId, InstanceProtocol.EnterWakeZone(benchId), $"0x01a9 crafting bench 0x{benchId:x} enters wake zone");
        player.CraftingBenchObject = benchId;
        player.CraftingBenchAt = (bench.X, bench.Y);
        global::Game.SetAreaBench(player, benchId, (bench.X, bench.Y));
        await RegisterStationaryCollision(player, benchId, bench.Type, bench.X, bench.Y, bench.Rotation, "bench", bench.Model);
    }

    // C→S 0x24: approach the bench before opening the ordinary crafting window (0x7b kind 0x33, bench type 0).
    internal static async Task OpenCraftingBench(Player player,
        uint use, double requestedAt)
    {
        if (!LeagueFor(player).Rules.CraftingBench) return;
        if (!TryBeginObjectWalk(player, requestedAt, player.CraftingBenchAt.X, player.CraftingBenchAt.Y, player.StationaryInteractionSize(player.CraftingBenchObject), out var objectWalk)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0024 crafting bench (0x{use:x}) from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        if (arrive > now) await AreaDelay(player, arrive - now);
        if (player.Closed || !PlayerActionCurrent(player, action)
            || !PlayerWalk.InWalkUpRange(stop, player.CraftingBenchAt, player.StationaryInteractionSize(player.CraftingBenchObject))) return;
        player.StashOpen = false;
        player.GuildStashOpen = false;
        player.BenchOpen = true;
        player.ModuleWindow = null;
        player.ModuleWindowAnchor = null;
        await RefreshBenchBalance(player);
        await Send(player, InstanceWaypoints.OpenUi(InstanceWaypoints.CraftingBenchUi),
            "0x007b open the crafting bench window (type 0)", still: () => PlayerActionCurrent(player, action));
    }

    internal static async Task<bool> CloseBench(Player player, (byte Flags, byte UiKind) request)
    {
        if (request.UiKind != InstanceWaypoints.CraftingBenchUi) return false;
        if (player.BenchOpen)
        {
            player.BenchOpen = false;
            await RefreshBenchBalance(player, clear: true);
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x02b1 crafting bench closed (flags 0x{request.Flags:x2})");
        }
        return true;
    }

    internal static async Task MoveToBench(Player player, (uint ItemId, bool IntoBench) request)
    {
        if (!LeagueFor(player).Rules.CraftingBench || !player.BenchOpen) return;
        var items = ItemsOf(player);
        if (ItemMoves.MoveToBench(items, request.ItemId, request.IntoBench, ShapeOf) is not { } move)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x00e9 Ctrl + click item {request.ItemId} {(request.IntoBench ? "onto" : "off")} the crafting bench: refused (not there, slot taken or no room)");
            return;
        }
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(move.From),
            items.NextRevision(move.From), [request.ItemId], []), $"0x00e9 Ctrl + click: item {request.ItemId} leaves row {move.From}");
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(move.To),
            items.NextRevision(move.To), [], [move.Placed]), $"0x00e9 Ctrl + click: item {request.ItemId} into row {move.To} at ({move.Placed.X}, {move.Placed.Y})");
    }

    internal static async Task CraftBench(Player player, (int Recipe, bool Bound) request)
    {
        if (!LeagueFor(player).Rules.CraftingBench || !player.BenchOpen || player.CraftingBenchObject == 0) return;
        var items = ItemsOf(player);
        var stash = PersonalStashOf(player);
        var result = ItemMoves.CraftAtBench(items, stash, Bench, request.Recipe, AreaRng(player));
        if (result is null)
        {
            var target = items.Items(PlayerInventories.CraftingBench).Select(i => i.Item).FirstOrDefault();
            string mods = target is null ? "" : string.Join(", ", target.Mods.Select(m => Items.Mods.ByHash(m.Hash)?.Id ?? $"0x{m.Hash:x4}"));
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x015d bench recipe {request.Recipe} ({Bench.Find(request.Recipe)?.Mod?.Id ?? Bench.Find(request.Recipe)?.Name}) refused on " +
                (target is null ? "no item" : $"{Items.ByHash(target.TypeHash)?.Id} {target.Rarity} ilvl {target.ItemLevel} [{mods}]"));
            await RefreshBenchBalance(player);
            await Send(player, InstanceChat.ChatLine("Craft refused: incompatible item or insufficient currency in inventory and stash."), "bench craft refused");
            return;
        }
        foreach (var payment in result.Payments)
        {
            if (payment.Inventory is { } paid)
                await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                    items.NextRevision(PlayerInventories.MainInventory), [.. paid.Removed, .. paid.Changed.Select(c => c.Id)], paid.Changed), "bench currency paid from inventory");
            await SendStashPayment(player, stash, payment.Stash);
        }
        await Send(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.CraftingBench),
            items.NextRevision(PlayerInventories.CraftingBench), [result.Changed.Id], [result.Changed]), $"bench recipe {request.Recipe}: crafted item {result.Changed.Id}");
        await RefreshBenchBalance(player);
        if (HideoutCatalog.ByArea(player.Layout.Hash) is not null)
        {
            if (!player.Character.LeagueQuestFlags.Contains(Quests.League.CraftedItemAtBench))
                await SetLeagueQuestFlags(player, [Quests.League.CraftedItemAtBench]);
        }
    }

    /// <summary>Records and broadcasts matching RecipeUnlockDisplay league quest flags when entering a recipe's
    /// unlock area. Setting a RecipeUnlockDisplay.RecipeId bit from 0→1 triggers the client's native recipe unlock
    /// notification (0x140d6d030 → 0x1402a7210).</summary>
    internal static Task<IReadOnlyList<int>> UnlockAreaRecipes(Player player)
    {
        if (!LeagueFor(player).Rules.CraftingBench || HardcoreProgressBlocked(player))
            return Task.FromResult<IReadOnlyList<int>>([]);
        var recipeIds = Bench.UnlocksForArea(player.Layout.Id);
        return recipeIds.Count == 0 ? Task.FromResult<IReadOnlyList<int>>([]) : UnlockRecipes(player, recipeIds);
    }

    internal static async Task<IReadOnlyList<int>> UnlockRecipes(Player player, IEnumerable<int> recipeIds)
    {
        if (HardcoreProgressBlocked(player)) return [];
        var stash = PersonalStashOf(player);
        var saved = stash.LeagueQuestFlags.ToHashSet();
        var fresh = recipeIds.Where(id => id > 0 && !saved.Contains(id)).Distinct().ToList();
        if (fresh.Count == 0) return [];
        // Game.SetLeagueQuestFlags updates both AccountStash.LeagueQuestFlags and Character.LeagueQuestFlags and
        // sends 0x02bc PlayerLeagueQuestFlagsUpdate. When every fresh id is already in Character.LeagueQuestFlags
        // (e.g. default league recipe flags published at area entry), only persist them to AccountStash.
        bool anyUnsetInPlayer = fresh.Any(id => !player.Character.LeagueQuestFlags.Contains(id));
        if (anyUnsetInPlayer)
            await global::Game.SetLeagueQuestFlags(player, fresh);
        else
            stash.ChangeLeagueQuestFlags(fresh, on: true);
        return fresh;
    }

}
