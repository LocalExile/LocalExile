using System.Security.Cryptography;
using System.Text.Json;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using static Program;

namespace GameServer.Modules.CosmeticsAndPets;

/// <summary>
/// Offline local microtransaction shop, mystery box, and salvage box handlers (<c>C→S 0x012c/0x012e/0x0130/0x0138/
/// 0x0206/0x026e</c>) backed exclusively by <c>data/client.db</c> tables and local save persistence. No outbound network
/// calls or real-money endpoints are ever contacted.
/// </summary>
public static class LocalShopFeature
{
    const string LocalGrantsKey = "cosmetics.local-grants.v1";
    const uint FirstLocalGrantId = 0x100000;
    const uint DefaultOfflinePoints = 10000;
    const int MaxLocalGrants = 500;
    const int MaxSalvageConsumed = 64;

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    sealed record LocalGrant(uint Id, uint MtxTypeHash);
    sealed record LocalGrantSave(uint NextId, List<LocalGrant> Grants);

    sealed class ShopCatalog
    {
        public readonly HashSet<uint> ResolvableHashes = [];
        public readonly HashSet<uint> MysteryBoxHashes = [];
        public readonly HashSet<uint> SalvageBoxHashes = [];
        public readonly Dictionary<string, uint> PackageToHash = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<uint> MysteryRewardPool = [];
        public readonly List<uint> SalvageOutcomePool = [];
    }

    static ShopCatalog _catalog = new();

    /// <summary>Eagerly loads MTX shop, mystery box, and salvage outcome tables from <c>client.db</c> at module load.</summary>
    public static void LoadData()
    {
        var catalog = new ShopCatalog();
        var gameSpecific = ClientTables.TableOrNull("MtxTypeGameSpecific");
        var mtxTypes = ClientTables.TableOrNull("MtxTypes");
        if (gameSpecific is null || mtxTypes is null)
        {
            _catalog = catalog;
            return;
        }

        // Map MtxTypeGameSpecific._row -> HASH32 and build the resolvable hash set so emitted records never fault.
        var gsHashes = new uint?[gameSpecific.Count];
        for (int i = 0; i < gameSpecific.Count; i++)
        {
            if (gameSpecific[i].LongOrNull("HASH32") is long h)
            {
                uint hash = (uint)h;
                gsHashes[i] = hash;
                catalog.ResolvableHashes.Add(hash);
            }
        }

        var mtxRowHashes = new uint?[mtxTypes.Count];
        for (int row = 0; row < mtxTypes.Count; row++)
        {
            var r = mtxTypes[row];
            if (r.LongOrNull("HASH32") is not long h) continue;
            uint hash = (uint)h;
            if (!catalog.ResolvableHashes.Contains(hash)) continue;
            mtxRowHashes[row] = hash;
            if (r.TextOrNull("Id") is { Length: > 0 } id)
            {
                catalog.PackageToHash.TryAdd(id, hash);
                int slash = id.LastIndexOf('/');
                if (slash >= 0 && slash + 1 < id.Length)
                    catalog.PackageToHash.TryAdd(id[(slash + 1)..], hash);
            }
            if (r.TextOrNull("Name") is { Length: > 0 } name)
                catalog.PackageToHash.TryAdd(name, hash);
        }

        if (ClientTables.TableOrNull("MysteryBoxes") is { } mysteryRows)
        {
            foreach (var r in mysteryRows)
            {
                if (r.LongOrNull("MtxTypesKey") is not long key || key < 0 || key >= mtxRowHashes.Length) continue;
                if (mtxRowHashes[(int)key] is not uint hash) continue;
                catalog.MysteryBoxHashes.Add(hash);
                if (r.TextOrNull("BoxId") is { Length: > 0 } boxId) catalog.PackageToHash[boxId] = hash;
                if (r.TextOrNull("BundleId") is { Length: > 0 } bundleId) catalog.PackageToHash[bundleId] = hash;
            }
        }

        if (ClientTables.TableOrNull("SalvageBoxes") is { } salvageRows)
        {
            foreach (var r in salvageRows)
            {
                if (r.LongOrNull("MtxType") is not long gsRow || gsRow < 0 || gsRow >= gsHashes.Length) continue;
                if (gsHashes[(int)gsRow] is not uint hash) continue;
                catalog.SalvageBoxHashes.Add(hash);
                if (r.TextOrNull("Id") is { Length: > 0 } salvageId) catalog.PackageToHash[salvageId] = hash;
            }
        }

        if (ClientTables.TableOrNull("MicrotransactionRecycleOutcomes") is { } outcomeRows)
        {
            var seen = new HashSet<uint>();
            foreach (var r in outcomeRows)
            {
                if (r.LongOrNull("Id") is not long gsRow || gsRow < 0 || gsRow >= gsHashes.Length) continue;
                if (gsHashes[(int)gsRow] is not uint hash || !catalog.ResolvableHashes.Contains(hash)) continue;
                if (seen.Add(hash)) catalog.SalvageOutcomePool.Add(hash);
            }
        }

        // Build mystery reward pool from all resolvable non-box MTX hashes so every mystery box open yields a valid item.
        foreach (var h in mtxRowHashes)
            if (h is uint hash && !catalog.MysteryBoxHashes.Contains(hash) && !catalog.SalvageBoxHashes.Contains(hash))
                catalog.MysteryRewardPool.Add(hash);

        _catalog = catalog;
    }

    /// <summary>Returns all locally granted MTX records saved on this account's stash.</summary>
    public static IReadOnlyList<InstanceMicrotransactions.Record> LocalRecordsOf(Player player)
    {
        if (!Saves.IsOpen) return [];
        var catalog = _catalog;
        var save = ReadSave(AccountStorage(player));
        return [.. save.Grants
            .Where(g => catalog.ResolvableHashes.Count == 0 || catalog.ResolvableHashes.Contains(g.MtxTypeHash))
            .Select(g => new InstanceMicrotransactions.Record(g.MtxTypeHash, g.Id))];
    }

    /// <summary>C→S 0x012c: local offline shop purchase. Grants a resolved package from <c>client.db</c> locally.</summary>
    public static async Task HandleBuy(Player player, InstanceShop.BuyRequest req)
    {
        if (player.Closed || player.LeavingArea) return;
        uint? hash = ResolvePackageHash(req.PackageId);
        if (hash is not uint resolved)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x012c shop buy \"{req.PackageId}\": unknown local package");
            await Send(player, InstanceShop.EncodeBuyResult(1, "Unknown local microtransaction package."),
                $"0x012d shop buy unknown package \"{req.PackageId}\"");
            await Send(player, InstanceShop.EncodeJsonErrorMessage($"Unknown local microtransaction package: {req.PackageId}"),
                "0x0282 shop buy unknown package error");
            return;
        }

        if (!TryConsumeAndGrant(player, [], resolved, out var granted, out string? error) || granted is null)
        {
            await Send(player, InstanceShop.EncodeBuyResult(1, error ?? "Unable to grant microtransaction."),
                $"0x012d local shop buy refused (\"{req.PackageId}\")");
            await Send(player, InstanceShop.EncodeJsonErrorMessage(error ?? "Unable to grant microtransaction."),
                "0x0282 local shop buy refused");
            return;
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x012c local shop buy \"{req.PackageId}\": granted record {granted.Id} (0x{granted.MtxTypeHash:x8})");
        await Send(player, InstanceMicrotransactions.RecordUpsert(granted, isStore: false, isGuild: false, justReceived: true),
            $"0x0204 local shop granted record {granted.Id} (0x{granted.MtxTypeHash:x8})");
        await Send(player, InstanceShop.EncodeBuyResult(0, ""),
            $"0x012d local shop buy succeeded (\"{req.PackageId}\")");
        await Send(player, InstanceShop.EncodePoints(DefaultOfflinePoints),
            $"0x0131 local offline shop points ({DefaultOfflinePoints})");
    }

    /// <summary>C→S 0x012e: local offline shop/inventory reload.</summary>
    public static async Task HandleReload(Player player)
    {
        if (player.Closed || player.LeavingArea) return;
        var owned = CombinedOwned(player);
        await Send(player, InstanceShop.EncodePoints(DefaultOfflinePoints),
            $"0x0131 local offline shop points ({DefaultOfflinePoints})");
        await Send(player, InstanceShop.EncodePaymentPackagesResult(0, "{\"packages\":[]}"),
            "0x0132 local offline shop payment packages (empty)");
        await Send(player, InstanceMicrotransactions.Added(owned, InstanceMicrotransactions.Records),
            $"0x01f4 shop reload: {owned.Count} record(s)");
        await Send(player, InstanceMicrotransactions.Added(owned, InstanceMicrotransactions.StoreOwned),
            $"0x01f4 shop reload store list: {owned.Count} record(s)");
    }

    /// <summary>C→S 0x0130: offline shipping quote — responds locally with zero cost and makes no network calls.</summary>
    public static async Task HandleShippingQuote(Player player, InstanceShop.FourStringsRequest req)
    {
        if (player.Closed || player.LeavingArea) return;
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0130 local offline shipping quote");
        await Send(player, InstanceShop.EncodeShippingQuote(0, "{\"cost\":0}"),
            "0x0133 local offline shipping quote");
    }

    /// <summary>C→S 0x0138: real-money checkout is disabled on local offline servers; replies cleanly without network I/O.</summary>
    public static async Task HandlePlaceOrder(Player player, InstanceShop.FourStringsRequest req)
    {
        if (player.Closed || player.LeavingArea) return;
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0138 offline checkout refused");
        await Send(player, InstanceShop.EncodeJsonErrorMessage("Real-money store checkout is unavailable on local servers."),
            "0x0282 local offline checkout refused");
        await Send(player, InstanceShop.EncodeOrderResult(1),
            "0x0139 local offline checkout order result (disabled)");
    }

    /// <summary>C→S 0x0206: open an owned Mystery Box locally using <c>client.db</c> tables.</summary>
    public static async Task HandleOpenMysteryBox(Player player, uint recordId)
    {
        if (player.Closed || player.LeavingArea) return;
        var catalog = _catalog;
        var found = FindOwnedBox(player, recordId);
        if (found is null || !catalog.MysteryBoxHashes.Contains(found.Value.Record.MtxTypeHash) || catalog.MysteryRewardPool.Count == 0)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0206 open mystery box {recordId}: invalid box record");
            await Send(player, InstanceShop.EncodeJsonErrorMessage("That item is not an openable mystery box."),
                $"0x0282 invalid mystery box record {recordId}");
            return;
        }

        uint rewardHash = catalog.MysteryRewardPool[RandomNumberGenerator.GetInt32(catalog.MysteryRewardPool.Count)];
        uint[] requiredLocal = found.Value.IsLocal ? [recordId] : [];
        if (!TryConsumeAndGrant(player, requiredLocal, rewardHash, out var reward, out string? error) || reward is null)
        {
            await Send(player, InstanceShop.EncodeJsonErrorMessage(error ?? "That mystery box is no longer available."),
                $"0x0282 mystery box open refused ({recordId})");
            return;
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x0206 opened mystery box {recordId}: reward record {reward.Id} (0x{reward.MtxTypeHash:x8})");
        if (found.Value.IsLocal)
            await Send(player, InstanceMicrotransactions.RecordRemove(recordId), $"0x0205 consumed local mystery box {recordId}");
        await Send(player, InstanceMicrotransactions.RecordUpsert(reward, isStore: false, isGuild: false, justReceived: true),
            $"0x0204 mystery box reward record {reward.Id} (0x{reward.MtxTypeHash:x8})");
    }

    /// <summary>C→S 0x026e: open a Salvage Box locally, rolling a resolvable outcome from <c>MicrotransactionRecycleOutcomes</c>.</summary>
    public static async Task HandleOpenSalvageBox(Player player, InstanceShop.OpenSalvageBoxRequest req)
    {
        if (player.Closed || player.LeavingArea) return;
        var catalog = _catalog;
        var box = FindOwnedBox(player, req.BoxRecordId);
        var pool = catalog.SalvageOutcomePool.Count > 0 ? catalog.SalvageOutcomePool : catalog.MysteryRewardPool;
        if (box is null || !catalog.SalvageBoxHashes.Contains(box.Value.Record.MtxTypeHash)
            || req.ConsumedRecordIds.Count is 0 or > MaxSalvageConsumed || pool.Count == 0)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x026e open salvage box {req.BoxRecordId} ({req.ConsumedRecordIds.Count} item(s)): invalid");
            await Send(player, InstanceShop.EncodeJsonErrorMessage("Unable to salvage the selected microtransactions."),
                $"0x0282 invalid salvage box request {req.BoxRecordId}");
            return;
        }

        // Validate every sacrificed record: distinct, not the salvage box itself, and actually owned by the player.
        var seenConsumed = new HashSet<uint>();
        var localIdsToRemove = new List<uint>();
        if (box.Value.IsLocal) localIdsToRemove.Add(req.BoxRecordId);
        foreach (uint consumedId in req.ConsumedRecordIds)
        {
            if (consumedId == req.BoxRecordId || !seenConsumed.Add(consumedId))
            {
                await Send(player, InstanceShop.EncodeJsonErrorMessage("Duplicate or invalid microtransaction selected for salvage."),
                    $"0x0282 invalid salvage consumed id {consumedId}");
                return;
            }
            var ownedItem = FindOwnedBox(player, consumedId);
            if (ownedItem is null)
            {
                await Send(player, InstanceShop.EncodeJsonErrorMessage("Selected microtransaction is not owned."),
                    $"0x0282 unowned salvage consumed id {consumedId}");
                return;
            }
            if (ownedItem.Value.IsLocal) localIdsToRemove.Add(consumedId);
        }

        uint rewardHash = pool[RandomNumberGenerator.GetInt32(pool.Count)];
        if (!TryConsumeAndGrant(player, localIdsToRemove, rewardHash, out var reward, out string? error) || reward is null)
        {
            await Send(player, InstanceShop.EncodeJsonErrorMessage(error ?? "Selected microtransaction is no longer available."),
                $"0x0282 salvage box atomic check failed ({req.BoxRecordId})");
            return;
        }
        Console.WriteLine($"[gateway] {player.Connection.Remote} <= 0x026e opened salvage box {req.BoxRecordId} ({req.ConsumedRecordIds.Count} sacrificed): reward record {reward.Id} (0x{reward.MtxTypeHash:x8})");
        foreach (uint removed in localIdsToRemove)
            await Send(player, InstanceMicrotransactions.RecordRemove(removed), $"0x0205 removed local salvaged record {removed}");
        await Send(player, InstanceMicrotransactions.RecordUpsert(reward, isStore: false, isGuild: false, justReceived: true),
            $"0x0204 salvage box reward record {reward.Id} (0x{reward.MtxTypeHash:x8})");
    }

    /// <summary>Combines the base catalog with any locally granted records for this account.</summary>
    public static IReadOnlyList<InstanceMicrotransactions.Record> CombinedOwned(Player player)
    {
        var local = LocalRecordsOf(player);
        return local.Count == 0 ? Mtx.Owned : [.. Mtx.Owned, .. local];
    }

    /// <summary>Resolves an owned record from either local grants or the base catalog.</summary>
    public static InstanceMicrotransactions.Record? FindRecord(Player player, uint recordId) =>
        recordId >= FirstLocalGrantId
            ? LocalRecordsOf(player).FirstOrDefault(r => r.Id == recordId)
            : Mtx.Find(recordId);

    /// <summary>Resolves a saved cosmetic pick against local grants first, then the base catalog.</summary>
    public static InstanceMicrotransactions.Record? ResolveSaved(Player player, CharacterSaves.SavedCosmetic saved)
    {
        if (saved.Record >= FirstLocalGrantId
            && FindRecord(player, saved.Record) is { } local
            && (saved.Hash is null || local.MtxTypeHash == saved.Hash))
            return local;
        return Mtx.Resolve(saved);
    }

    static uint? ResolvePackageHash(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId)) return null;
        string trimmed = packageId.Trim();
        var catalog = _catalog;
        if (catalog.PackageToHash.TryGetValue(trimmed, out uint h)) return h;
        if (uint.TryParse(trimmed, out uint numeric))
        {
            if (catalog.ResolvableHashes.Contains(numeric)) return numeric;
            if (Mtx.Find(numeric) is { } rec && (catalog.ResolvableHashes.Count == 0 || catalog.ResolvableHashes.Contains(rec.MtxTypeHash)))
                return rec.MtxTypeHash;
        }
        return null;
    }

    static (InstanceMicrotransactions.Record Record, bool IsLocal)? FindOwnedBox(Player player, uint recordId)
    {
        if (recordId >= FirstLocalGrantId)
        {
            var local = LocalRecordsOf(player).FirstOrDefault(r => r.Id == recordId);
            return local is not null ? (local, true) : null;
        }
        return Mtx.Find(recordId) is { } rec ? (rec, false) : null;
    }

    static bool TryConsumeAndGrant(Player player, IReadOnlyList<uint> requiredLocalIds, uint rewardHash,
        out InstanceMicrotransactions.Record? granted, out string? error)
    {
        granted = null;
        error = null;
        if (!Saves.IsOpen)
        {
            error = "Local microtransaction storage is unavailable.";
            return false;
        }
        uint newId = FirstLocalGrantId;
        string? failure = null;
        var stash = AccountStorage(player);
        bool changed = stash.ChangeModuleData(LocalGrantsKey, text =>
        {
            var save = Deserialize(text);
            if (requiredLocalIds.Count > 0)
            {
                var present = save.Grants.Select(g => g.Id).ToHashSet();
                foreach (uint reqId in requiredLocalIds)
                {
                    if (!present.Contains(reqId))
                    {
                        failure = "Selected microtransaction record is no longer available.";
                        return null;
                    }
                }
                var removeSet = requiredLocalIds.ToHashSet();
                save.Grants.RemoveAll(g => removeSet.Contains(g.Id));
            }
            if (save.Grants.Count >= MaxLocalGrants)
            {
                failure = $"Local microtransaction grant limit ({MaxLocalGrants}) reached.";
                return null;
            }
            newId = Math.Max(FirstLocalGrantId, save.NextId);
            var next = new LocalGrantSave(newId + 1, [.. save.Grants, new LocalGrant(newId, rewardHash)]);
            return JsonSerializer.Serialize(next, JsonOpts);
        });
        if (!changed)
        {
            error = failure ?? "Unable to update local microtransaction records.";
            return false;
        }
        granted = new InstanceMicrotransactions.Record(rewardHash, newId);
        return true;
    }

    static AccountStash AccountStorage(Player player) =>
        AccountStash.For(player.Character.Account ?? CharacterName(player), ItemsFor(player));

    static LocalGrantSave ReadSave(AccountStash stash) => Deserialize(stash.ModuleData(LocalGrantsKey));

    static LocalGrantSave Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(FirstLocalGrantId, []);
        try
        {
            var parsed = JsonSerializer.Deserialize<LocalGrantSave>(json, JsonOpts);
            return parsed is null ? new(FirstLocalGrantId, []) : new(Math.Max(FirstLocalGrantId, parsed.NextId), parsed.Grants ?? []);
        }
        catch (JsonException) { return new(FirstLocalGrantId, []); }
    }
}
