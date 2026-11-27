using System.Numerics;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Settlers.Operations;

/// <summary>
/// Rog's Disenchanting service backed by <c>KalguurDisenchanting1</c> (inventory row 87, 12x10) and
/// <c>village_disenchant_queue</c>.
/// <para>
/// Implements the exact decompiled 3.29.3c dust formula (<c>0x1424c6af0</c>) and eligibility checks (<c>0x1424c6970</c>):
/// <c>VillageUniqueDisenchantValues</c> (* 500), explicit mod sum via <c>VillageBalancePerLevelShared.__col6</c>,
/// +2%/quality, +50%/influence, +50%/corrupted implicit, exact 3-segment ilvl multiplier (50..100..500), scaled by
/// <see cref="SettlersSettings.DisenchantingSpeedMultiplier"/> and <see cref="SettlersSettings.DustYieldMultiplier"/>.
/// </para>
/// </summary>
public sealed class VillageDisenchantingService(IOperationsHost host)
{
    const int CorruptedImplicitGenerationType = 5;

    /// <summary>Exact eligibility check (<c>0x1424c6970</c>): disenchantable class, identified, rarity 1..maxRarity, no socketed gems.</summary>
    public static string? CheckEligibility(Item item, ItemCatalog catalog)
    {
        OperationsData.EnsureLoaded();
        var bt = catalog.ByHash(item.TypeHash);
        if (bt is null || !OperationsData.IsDisenchantableClass(bt.Class))
            return "this item cannot be disenchanted";
        if (!item.Identified)
            return "unidentified items cannot be disenchanted";
        int rarity = (int)item.Rarity;
        if (rarity < (int)ItemRarity.Magic || rarity > OperationsData.DisenchantMaxRarity)
            return "only Magic, Rare, or Unique items can be disenchanted";
        foreach (var s in item.Sockets)
            if (s.Gem is not null) return "remove socketed gems before disenchanting";
        return null;
    }

    /// <summary>Exact retail dust formula (<c>0x1424c6af0</c>), optionally scaled by <paramref name="dustYieldMultiplier"/>.</summary>
    public static long ComputeDustValue(Item item, ItemCatalog catalog, bool hardMode = false, double dustYieldMultiplier = 1.0)
    {
        OperationsData.EnsureLoaded();
        long baseDust;
        if (item.Rarity == ItemRarity.Unique)
        {
            float uniqueVal = 1.0f;
            if (TryLookupUniqueValue(item, out var entry))
                uniqueVal = hardMode ? entry.RuthlessValue : entry.Value;
            baseDust = (long)Math.Round(uniqueVal * 500.0);
        }
        else
        {
            long modSum = 0;
            foreach (var mod in item.Mods)
            {
                if (mod.Section != ModSection.Explicit) continue;
                int reqLvl = catalog.Mods.ByHash(mod.Hash)?.Level ?? item.ItemLevel;
                modSum += OperationsData.BalanceForLevel(reqLvl).DisenchantModDustBase;
            }
            baseDust = modSum;
        }

        if (baseDust <= 0) return 0;

        int corruptedImplicits = 0;
        if (item.Corrupted)
        {
            foreach (var mod in item.Mods)
            {
                if (mod.Section != ModSection.Implicit) continue;
                if (catalog.Mods.ByHash(mod.Hash) is { GenerationType: CorruptedImplicitGenerationType })
                    corruptedImplicits++;
            }
        }

        int influenceCount = BitOperations.PopCount((uint)item.Influences);
        int bonusPct = item.Quality * OperationsData.DisenchantBonusPerQuality
            + influenceCount * OperationsData.DisenchantBonusPerInfluence
            + corruptedImplicits * OperationsData.DisenchantBonusPerCorruptedImplicit;

        int ilvl = item.ItemLevel;
        int ilvlMult = ilvl < 46 ? 50
            : ilvl < 68 ? ((ilvl * 50 - 2300) / 22) + 50
            : Math.Min(500, ((ilvl * 100 - 6800) / 4) + 100);

        long retailDust = (((baseDust * (100L + bonusPct)) / 100L) * ilvlMult) / 100L;
        if (retailDust <= 0) return 0;
        return Math.Max(1L, (long)Math.Round(retailDust * Math.Max(0.0, dustYieldMultiplier)));
    }

    public static InstanceVillage.TimerState BuildDisenchantTimer(VillageStore.TownSnapshot snap)
    {
        var active = snap.DisenchantQueue.FirstOrDefault();
        if (active is null || active.DurationSeconds <= 0)
            return new InstanceVillage.TimerState(0, 0);
        uint total = (uint)Math.Max(1, Math.Round(active.DurationSeconds * 100));
        uint elapsed = (uint)Math.Clamp(Math.Round(active.ElapsedSeconds * 100), 0, total);
        return new InstanceVillage.TimerState(elapsed, total);
    }

    public string? ValidatePlacement(Player player, Item item) => CheckEligibility(item, ItemsFor(player));

    /// <summary>Reconciles the active disenchanting queue item when the player adds/removes items in row 87.</summary>
    public async Task OnInventoryChangedAsync(Player player)
    {
        var (account, league) = VillageStore.ScopeOf(player);
        var inv = ItemsOf(player);
        var currentItems = inv.Items(PlayerInventories.KalguurDisenchanting);

        bool changed = host.WithTownLock(account, league, () =>
        {
            var snap = VillageStore.Load(account, league);
            if (snap is null || snap.DisenchantQueue.Count == 0) return false;
            var active = snap.DisenchantQueue[0];
            if (currentItems.Any(i => i.Id == active.ItemId)) return false;
            // Player removed the item currently being disenchanted: clear active slot cleanly.
            VillageStore.Save(snap with { DisenchantQueue = [] });
            return true;
        });

        if (changed && OperationsSubsystem.IsVillageArea(player))
            await host.SyncFullVillageStateAsync(player);
    }

    /// <summary>Advances Rog's disenchanting queue while disenchanters are assigned and treasury has gold.</summary>
    public async Task<bool> TickAsync(Player player, double elapsedSeconds)
    {
        OperationsData.EnsureLoaded();
        if (elapsedSeconds <= 0) return false;

        var (account, league) = VillageStore.ScopeOf(player);
        var settings = host.Settings;
        if (settings.GoldBurnRateMultiplier > 0 && host.GetTreasuryGold(account, league) == 0)
            return false;

        var disenchanters = host.GetWorkersForJob(account, league, OperationsData.JobDisenchanting);
        if (disenchanters.Count == 0) return false;

        int teamPower = disenchanters.Sum(w => OperationsData.WorkerPowerForRank(w.DisenchantRank));
        double speedFactor = Math.Max(0.1, (teamPower / 63.0) * Math.Max(0.01, settings.DisenchantingSpeedMultiplier));

        var inv = ItemsOf(player);
        var catalog = ItemsFor(player);
        bool hardMode = LeagueFor(player).HardMode;
        bool anyChanged = false;
        double remaining = elapsedSeconds;
        int safety = 0;

        while (remaining > 0 && safety++ < 24)
        {
            var queuedItems = inv.Items(PlayerInventories.KalguurDisenchanting)
                .OrderBy(i => i.Y).ThenBy(i => i.X)
                .ToList();
            if (queuedItems.Count == 0)
            {
                ClearQueueIfNonEmpty(account, league);
                break;
            }

            // Find or start the active disenchanting item.
            var active = host.WithTownLock(account, league, () =>
            {
                var snap = StandaloneOperationsHost.LoadOrCreate(account, league);
                var existing = snap.DisenchantQueue.FirstOrDefault();
                if (existing is not null && queuedItems.Any(i => i.Id == existing.ItemId))
                    return existing;

                var firstEligible = queuedItems.FirstOrDefault(i => CheckEligibility(i.Item, catalog) is null);
                if (firstEligible is null) return null;

                long dust = ComputeDustValue(firstEligible.Item, catalog, hardMode, settings.DustYieldMultiplier);
                if (dust <= 0) dust = 1;
                // Retail duration scales with dust value (roughly 2 to 25 minutes at 1x rank-5 disenchanter).
                double baseDurationSec = Math.Clamp(90.0 + Math.Sqrt(dust) * 3.5, 60.0, 1500.0);
                var created = new VillageStore.DisenchantQueueRow(0, firstEligible.Id, dust, 0, baseDurationSec, 0.0);
                VillageStore.Save(snap with { DisenchantQueue = [created] });
                return created;
            });

            if (active is null) break;

            double effectiveDt = remaining * speedFactor;
            double nextElapsed = active.ElapsedSeconds + effectiveDt;
            anyChanged = true;

            if (nextElapsed < active.DurationSeconds)
            {
                host.WithTownLock(account, league, () =>
                {
                    var snap = StandaloneOperationsHost.LoadOrCreate(account, league);
                    VillageStore.Save(snap with { DisenchantQueue = [active with { ElapsedSeconds = nextElapsed }] });
                    return true;
                });
                break;
            }

            // Item finished disenchanting! Consume it and grant Thaumaturgic Dust (VillageResources row 20).
            double neededWallSec = Math.Max(0.0, (active.DurationSeconds - active.ElapsedSeconds) / speedFactor);
            remaining = Math.Max(0.0, remaining - neededWallSec);

            if (inv.Remove(PlayerInventories.KalguurDisenchanting, (uint)active.ItemId) is { } consumed)
            {
                host.AddResource(account, league, OperationsData.MagicDustResourceRow, active.DustTotal);
                await Send(player, InstanceItems.InventoryChange(
                    PlayerInventories.InventoryId(PlayerInventories.KalguurDisenchanting),
                    inv.NextRevision(PlayerInventories.KalguurDisenchanting),
                    [consumed.Id], []),
                    $"0x00bf village disenchanted item {consumed.Id} (+{active.DustTotal} Thaumaturgic Dust)");
            }

            ClearQueueIfNonEmpty(account, league);
        }

        return anyChanged;
    }

    void ClearQueueIfNonEmpty(string account, string league)
    {
        host.WithTownLock(account, league, () =>
        {
            var snap = VillageStore.Load(account, league);
            if (snap is null || snap.DisenchantQueue.Count == 0) return false;
            VillageStore.Save(snap with { DisenchantQueue = [] });
            return true;
        });
    }

    static bool TryLookupUniqueValue(Item item, out OperationsData.UniqueDisenchant entry)
    {
        foreach (uint wordHash in item.NameWords)
            if (OperationsData.UniqueDisenchantByWordHash.TryGetValue(wordHash, out entry!)) return true;
        if (!string.IsNullOrEmpty(item.CustomName) && OperationsData.UniqueDisenchantByName.TryGetValue(item.CustomName, out entry!))
            return true;
        entry = default!;
        return false;
    }
}
