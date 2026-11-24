using System.Runtime.CompilerServices;
using GameServer.Core;
using GameServer.Core.Protocol.Instance;

static partial class QuestobjectsRules
{
    sealed record DivineFontOption(ushort Hash16, string Id, string Script, string Argument, int Tier, int MinDifficulty, bool HardMode);

    sealed class DivineFontSession
    {
        public byte RemainingUses;
        public List<DivineFontOption> Options = [];
        public List<Item> PendingResults = [];
    }

    static readonly ConditionalWeakTable<InstanceState, Dictionary<string, DivineFontSession>> DivineFontSessions = new();
    static IReadOnlyList<DivineFontOption>? DivineFontCatalog;

    static IReadOnlyList<DivineFontOption> LoadDivineFontCatalog()
    {
        if (DivineFontCatalog is { } cached) return cached;
        var list = new List<DivineFontOption>();
        if (ClientTables.Has("LabyrinthCraftOptions"))
        {
            foreach (var row in ClientTables.Table("LabyrinthCraftOptions"))
            {
                string id = row.Text("Id") ?? "";
                string script = row.Text("Script") ?? "";
                string arg = row.Text("ScriptArgument") ?? "";
                int tier = row.IntOrNull("Tier") ?? 0;
                ushort hash16 = (ushort)(row.IntOrNull("HASH16") ?? 0);
                if (hash16 == 0 || script.Length == 0) continue;
                bool hardMode = id.StartsWith("HardMode", StringComparison.OrdinalIgnoreCase);
                int minDiff = id.Contains("Uber", StringComparison.OrdinalIgnoreCase) ? 4
                    : id.Contains("Merciless", StringComparison.OrdinalIgnoreCase) ? 3
                    : id.Contains("Cruel", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
                list.Add(new(hash16, id, script, arg, tier, minDiff, hardMode));
            }
        }
        return DivineFontCatalog = list;
    }

    static DivineFontSession GetOrCreateDivineFontSession(Player player, int tier)
    {
        var byChar = DivineFontSessions.GetOrCreateValue(player.Instance);
        string character = CharacterName(player);
        lock (byChar)
        {
            if (byChar.TryGetValue(character, out var existing)) return existing;
            bool hardMode = LeagueFor(player).HardMode;
            var all = LoadDivineFontCatalog().Where(o => o.HardMode == hardMode && o.MinDifficulty <= tier).ToList();
            if (all.Count == 0) all = [.. LoadDivineFontCatalog()];
            var rng = AreaRng(player);
            var picked = new List<DivineFontOption>();
            // Always offer the signature transfigure-gem craft first when available, then tier-appropriate quality/XP crafts.
            if (all.FirstOrDefault(o => o.Script == "change_gem_to_random_alternate_gem_of_same_colour" && o.Argument.Length == 0) is { } transSame)
                picked.Add(transSame);
            if (tier >= 3 && all.FirstOrDefault(o => o.Script == "change_gem_to_alternate_gem") is { } transDirect)
                picked.Add(transDirect);
            var qualityPool = all.Where(o => o.Script == "use_x_gemcutter" && o.MinDifficulty == tier).ToList();
            if (qualityPool.Count == 0) qualityPool = all.Where(o => o.Script == "use_x_gemcutter").ToList();
            if (qualityPool.Count > 0) picked.Add(qualityPool[rng.Next(qualityPool.Count)]);
            var xpPool = all.Where(o => o.Script == "add_experience_to_gem" && o.MinDifficulty == tier).ToList();
            if (xpPool.Count == 0) xpPool = all.Where(o => o.Script == "add_experience_to_gem").ToList();
            if (xpPool.Count > 0) picked.Add(xpPool[rng.Next(xpPool.Count)]);
            var session = new DivineFontSession
            {
                RemainingUses = (byte)(tier >= 4 ? 2 : 1),
                Options = picked,
            };
            byChar[character] = session;
            return session;
        }
    }

    static DivineFontSession? TryGetDivineFontSession(Player player)
    {
        if (!DivineFontSessions.TryGetValue(player.Instance, out var byChar)) return null;
        lock (byChar) return byChar.GetValueOrDefault(CharacterName(player));
    }

    static Task SendFont(Player player, byte[] bytes, string log) =>
        player.Connection.Outbox is { } outbox ? outbox(bytes, log) : Send(player, bytes, log);

    static Task SendDivineFontOptions(Player player, DivineFontSession session)
    {
        ushort[] hashes = session.RemainingUses > 0 ? [.. session.Options.Select(o => o.Hash16)] : [];
        return SendFont(player, InstanceLabyrinth.CraftOptions(hashes, session.RemainingUses),
            $"0x0246 Divine Font: {hashes.Length} option(s), {session.RemainingUses} use(s) left");
    }

    // The Ascendancy Chamber's Divine Font (Metadata/Terrain/Labyrinth/Objects/LabyrinthBlessingBench):
    // once Izaro is defeated in this instance, send S→C 0x0246 (LabyrinthCraftOptions) to populate and open
    // LabyrinthCraftingWindow (0x140bc9a23, bound to PlayerInventories.CraftingBench row 16).
    internal static async Task UseDivineFont(Player player)
    {
        if (LabyrinthDay.DifficultyOf(player.Layout.Id) is not int tier) return;
        if (!player.Instance.HasState(IzaroFights.Beaten))
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} Divine Font: Izaro is not beaten in this instance");
            return;
        }
        var session = GetOrCreateDivineFontSession(player, tier);
        await SendDivineFontOptions(player, session);
        if (session.PendingResults.Count > 0)
            await SendFont(player, InstanceLabyrinth.CraftResultOptionsList(session.PendingResults),
                $"0x02a2 Divine Font pending results ({session.PendingResults.Count})");
    }

    internal static Task UseDivineFontOption(Player player, ushort optionHash16)
    {
        if (TryGetDivineFontSession(player) is not { } session) return Task.CompletedTask;
        int idx = session.Options.FindIndex(o => o.Hash16 == optionHash16);
        return idx >= 0 ? ApplyDivineFontCraft(player, session, session.Options[idx]) : Task.CompletedTask;
    }

    internal static Task UseDivineFontIndex(Player player, byte index)
    {
        if (TryGetDivineFontSession(player) is not { } session || index >= session.Options.Count) return Task.CompletedTask;
        return ApplyDivineFontCraft(player, session, session.Options[index]);
    }

    static async Task ApplyDivineFontCraft(Player player, DivineFontSession session, DivineFontOption option)
    {
        if (session.RemainingUses == 0)
        {
            await SendDivineFontOptions(player, session);
            return;
        }
        var items = ItemsOf(player);
        var slotItem = items.Items(PlayerInventories.CraftingBench).FirstOrDefault();
        var catalog = ItemsFor(player);
        if (slotItem is null || catalog.ByHash(slotItem.Item.TypeHash) is not { } baseType || catalog.Gem(baseType) is not { } gemType)
        {
            Console.WriteLine($"[gateway] {player.Connection.Remote} <= Divine Font {option.Id}: no skill gem in CraftingBench (row 16)");
            return;
        }

        if (option.Script == "use_x_gemcutter")
        {
            int addQuality = int.TryParse(option.Argument, out int q) ? q : 2;
            byte nextQuality = (byte)Math.Min(20, slotItem.Item.Quality + addQuality);
            if (nextQuality == slotItem.Item.Quality) return;
            var updated = slotItem.Item with { Quality = nextQuality };
            await CommitDivineFontGem(player, session, slotItem, updated, $"Divine Font +{addQuality}% quality");
            return;
        }

        if (option.Script == "add_experience_to_gem")
        {
            int addXp = int.TryParse(option.Argument, out int xp) ? xp : 3_000_000;
            if (CurrencyEffects.ApplyFacetorsLens(slotItem.Item, gemType, addXp) is not { } updated) return;
            await CommitDivineFontGem(player, session, slotItem, updated, $"Divine Font +{addXp} gem XP");
            return;
        }

        if (option.Script is "change_gem_to_alternate_gem" or "change_gem_to_random_alternate_gem_of_same_colour")
        {
            var candidates = BuildTransfiguredCandidates(catalog, slotItem.Item, baseType, gemType,
                sameGemOnly: option.Script == "change_gem_to_alternate_gem", AreaRng(player));
            if (candidates.Count == 0)
            {
                Console.WriteLine($"[gateway] {player.Connection.Remote} <= Divine Font {option.Id}: no transfigured variants for {baseType.Name}");
                return;
            }
            session.PendingResults = candidates;
            await SendFont(player, InstanceLabyrinth.CraftResultOptionsList(candidates),
                $"0x02a2 Divine Font craft results ({candidates.Count} gem(s) for {option.Id})");
            return;
        }

        if (option.Script == "exchange_gem_for_exceptional_gem")
        {
            var pool = catalog.ExceptionalShopGems();
            if (!gemType.IsSupport || pool.Count == 0) return;
            var chosen = pool[AreaRng(player).Next(pool.Count)];
            var created = ItemCatalog.Create(chosen.Type) with { GemVariant = chosen.Variant.Index, Quality = slotItem.Item.Quality };
            await CommitDivineFontGem(player, session, slotItem, created, $"Divine Font exceptional gem ({chosen.Variant.Name})");
            return;
        }

        if (option.Script == "exchange_gem_for_awakened_gem")
        {
            var pool = catalog.AwakenedShopGems();
            if (!gemType.IsSupport || pool.Count == 0) return;
            var chosen = pool[AreaRng(player).Next(pool.Count)];
            var created = ItemCatalog.Create(chosen.Type) with { GemVariant = chosen.Variant.Index, Quality = slotItem.Item.Quality };
            await CommitDivineFontGem(player, session, slotItem, created, $"Divine Font awakened gem ({chosen.Variant.Name})");
        }
    }

    static List<Item> BuildTransfiguredCandidates(ItemCatalog catalog, Item source, ItemCatalog.BaseType baseType,
        ItemCatalog.GemType gemType, bool sameGemOnly, Random rng)
    {
        var pool = new List<(ItemCatalog.BaseType Type, ItemCatalog.GemVariant Variant)>();
        if (sameGemOnly)
        {
            foreach (var v in gemType.Variants)
                if (v.Index > 0 && v.Index != source.GemVariant && !v.Name.StartsWith("Vaal ", StringComparison.Ordinal))
                    pool.Add((baseType, v));
        }
        if (pool.Count == 0 && gemType.Colour is >= 1 and <= 3)
        {
            foreach (var (t, v) in catalog.FullShopGems(gemType.Colour, gemType.IsSupport, ordinaryOnly: false))
                if (v.Index > 0 && !v.Name.StartsWith("Vaal ", StringComparison.Ordinal)
                    && (t.Hash != source.TypeHash || v.Index != source.GemVariant))
                    pool.Add((t, v));
        }
        if (pool.Count == 0)
        {
            for (int colour = 1; colour <= 3; colour++)
            {
                foreach (var (t, v) in catalog.FullShopGems(colour, gemType.IsSupport, ordinaryOnly: false))
                    if (v.Index > 0 && !v.Name.StartsWith("Vaal ", StringComparison.Ordinal)
                        && (t.Hash != source.TypeHash || v.Index != source.GemVariant))
                        pool.Add((t, v));
            }
        }
        var results = new List<Item>(3);
        int take = Math.Min(3, pool.Count);
        for (int i = 0; i < take; i++)
        {
            int pick = rng.Next(pool.Count);
            var (t, v) = pool[pick];
            pool.RemoveAt(pick);
            var created = ItemCatalog.Create(t) with
            {
                GemVariant = v.Index,
                Quality = source.Quality,
                GemExperience = source.GemExperience,
            };
            results.Add(created);
        }
        return results;
    }

    // C→S 0x02a3: player picked one of the gems offered via S→C 0x02a2.
    internal static async Task PickDivineFontResult(Player player, byte choiceIndex)
    {
        if (TryGetDivineFontSession(player) is not { } session || choiceIndex >= session.PendingResults.Count)
            return;
        var items = ItemsOf(player);
        var slotItem = items.Items(PlayerInventories.CraftingBench).FirstOrDefault();
        if (slotItem is null)
        {
            session.PendingResults.Clear();
            await SendFont(player, InstanceLabyrinth.CraftResultOptionsList([]), "0x02a2 Divine Font clear results (slot empty)");
            return;
        }
        var chosen = session.PendingResults[choiceIndex];
        session.PendingResults.Clear();
        await CommitDivineFontGem(player, session, slotItem, chosen, $"Divine Font result choice #{choiceIndex}");
        await SendFont(player, InstanceLabyrinth.CraftResultOptionsList([]), "0x02a2 Divine Font clear results");
    }

    static async Task CommitDivineFontGem(Player player, DivineFontSession session, InventoryItem before, Item after, string label)
    {
        var items = ItemsOf(player);
        var shape = ShapeOf(after);
        var replaced = Saves.Atomically(() =>
        {
            var next = shape is { } s ? PlayerInventories.Completed(before with { Item = after }, s).Item : after;
            return items.Change(PlayerInventories.CraftingBench, before.Id, _ => next);
        });
        if (replaced is null) return;
        if (session.RemainingUses > 0) session.RemainingUses--;
        await SendFont(player, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.CraftingBench),
            items.NextRevision(PlayerInventories.CraftingBench), [before.Id], [replaced]),
            $"0x00bf CraftingBench: {label}");
        await SendDivineFontOptions(player, session);
    }
}
