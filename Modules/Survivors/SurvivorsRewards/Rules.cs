using System.Collections.Concurrent;
using System.Text.Json;
using System.Net.Sockets;
using GameServer.Core.Projectiles;
using ChatCommand = GameServer.Modding.ChatCommand;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;

using GameServer.Modules.Survivors;

using static GameServer.Modules.Survivors.SurvivorsSkills;

namespace GameServer.Modules.Survivors;

static partial class SurvivorsRewards
{
    /// <summary>The reward inventory the level-up window lists its skills in (the quest-reward window's own).</summary>
    internal const uint SurvivorsOfferInventory = 0x600;

    /// <summary>The blessings a level-up may offer: support gems whose names describe the run-local effect.</summary>
    internal static IReadOnlyList<SurvivorsBlessingItem> SurvivorsBlessingsFor(ClientState conn)
    {
        (string Path, string Name, SurvivorsBlessing Blessing)[] candidates =
        [
            ("Metadata/Items/Gems/SupportGemAddedFireDamage", "Added Fire Damage Support", SurvivorsBlessing.Might),
            ("Metadata/Items/Gems/SupportGemFasterCast", "Faster Casting Support", SurvivorsBlessing.Haste),
            ("Metadata/Items/Gems/SupportGemIncreasedAreaOfEffect", "Increased Area of Effect Support", SurvivorsBlessing.Area),
            ("Metadata/Items/Gems/SupportGemIncreasedCriticalStrikes", "Increased Critical Strikes Support", SurvivorsBlessing.Critical),
            ("Metadata/Items/Gems/SupportGemLifeLeech", "Life Leech Support", SurvivorsBlessing.Leech),
            ("Metadata/Items/Gems/SupportGemAdditionalXP", "Enlighten Support", SurvivorsBlessing.Wisdom),
            ("Metadata/Items/Gems/SupportGemFortify", "Fortify Support", SurvivorsBlessing.Fortitude),
            ("Metadata/Items/Gems/SupportGemFasterProjectiles", "Faster Projectiles Support", SurvivorsBlessing.Swiftness),
        ];
        return [.. candidates.Where(c => ItemsFor(conn).Find(c.Path) is not null)
            .Select(c => new SurvivorsBlessingItem(c.Path, c.Name, c.Blessing))];
    }

    internal static async Task<bool> SurvivorsEquip(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, Item item)
    {
        if (StateOf(conn).Ownership is not { Current: true } ownership) return false;
        try
        {
            return await SurvivorsEquipCore(remote, stream, ciphers, conn, item, ownership);
        }
        catch (Exception ex) when (ex is not IOException and not ObjectDisposedException)
        {
            // A broken equip must not leave the run paused with the choice already spent.
            Console.WriteLine($"[gateway] {remote} survivors equip failed for {item.TypeHash:x8}: {ex}");
            return false;
        }
    }

    internal static async Task<bool> SurvivorsEquipCore(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, Item item, SurvivorsOwnership ownership)
    {
        static Dictionary<uint, int> StatsNow(ClientState c) => PlayerStatsWithCleared(c).ToDictionary(s => s.Item1, s => s.Item2);
        var before = StatsNow(conn);
        if (ItemsFor(conn).ByHash(item.TypeHash) is not { } type) return false;
        var items = ItemsOf(conn);
        byte? slot = null;
        foreach (byte row in new byte[] { 1, 2, 3, 4, 5, 6, 7, 102, 8, 9, 10 })
            if (PlayerInventories.Accepts(row, type.Class) && items.Items(row).Count == 0) { slot = row; break; }
        if (slot is not { } equipped)
        {
            if (items.FreeCell(PlayerInventories.MainInventory, type.Width, type.Height, ShapeOf) is not { } cell) return false;
            InventoryItem inBag = null!;
            if (!ownership.Commit(() => inBag = items.Put(PlayerInventories.MainInventory,
                new InventoryItem(items.NewItemId(), cell.X, cell.Y, item)))) return false;
            await Send(remote, stream, ciphers,
                InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                    items.NextRevision(PlayerInventories.MainInventory), [], [inBag]),
                $"0x00bf survivors unique {type.Name} into the inventory", still: () => ownership.Current);
            return true;
        }
        InventoryItem placed = null!;
        if (!ownership.Commit(() =>
        {
            placed = items.Add(equipped, 0, 0, item);
            StateOf(conn).SurvivorsItemIds.Add(placed.Id);
        })) return false;
        await Send(remote, stream, ciphers,
            InstanceItems.InventoryChange(PlayerInventories.InventoryId(equipped), items.NextRevision(equipped), [], [placed]),
            $"0x00bf survivors unique {type.Name} equipped in row {equipped}", still: () => ownership.Current);
        if (!ownership.Current) return false;
        if (PlayerInventories.ShowsOnModel(equipped)) await SendEquipment(remote, stream, ciphers, conn, items);
        if (!ownership.Current) return false;
        await SendCharacterStats(remote, stream, ciphers, conn, $"survivors unique {type.Name}");
        if (!ownership.Current) return false;
        // The item's mods must reach the character: the stat packet above is checked against the same build.
        var after = StatsNow(conn);
        var changed = after.Where(kv => before.GetValueOrDefault(kv.Key) != kv.Value)
            .Select(kv => $"{Stats.Name(kv.Key)} {before.GetValueOrDefault(kv.Key)}->{kv.Value}").Take(8).ToList();
        Console.WriteLine($"[gateway] {remote} survivors unique {type.Name}: {(changed.Count == 0 ? "no stat change" : string.Join(", ", changed))}");
        return true;
    }

    /// <summary>
    /// Dev: walk the whole unique catalogue and report what the server can actually make work: the base types it can
    /// create, the ones that fit an equipment row, the ones whose Mods.dat rows are all present, and the ones whose
    /// mods resolve to at least one global or local stat. A name in "No stat found" is a unique whose mechanic is not
    /// a plain stat (or a conditional one) and needs its own implementation.
    /// </summary>
    internal static async Task SurvivorsUniqueReport(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        var uniques = UniquesFor(conn).All;
        var catalog = ItemsFor(conn);
        int creatable = 0, equipment = 0, withStats = 0, missingRows = 0, unsupported = 0;
        var empty = new List<string>();
        var missing = new List<string>();
        foreach (var definition in uniques)
        {
            if (UniquesFor(conn).Create(definition, Random.Shared) is not { } item) { unsupported++; continue; }
            creatable++;
            if (catalog.Find(definition.BaseTypeId) is not { } type
                || !ItemModStats.EquipmentRows.Any(row => PlayerInventories.Accepts(row, type.Class))) continue;
            equipment++;
            int absent = definition.Mods.Count(id => catalog.Mods.ById(id) is null)
                + (definition.Implicits?.Count(id => catalog.Mods.ById(id) is null) ?? 0);
            if (absent > 0)
            {
                missingRows++;
                if (missing.Count < 12) missing.Add($"{definition.Name} ({absent})");
            }
            var global = ItemModStats.Global([item], catalog.Mods, Stats);
            var local = ItemModStats.Local(item, catalog.Mods);
            if (global.Count > 0 || local.Count > 0) withStats++;
            else if (empty.Count < 12) empty.Add(definition.Name);
        }
        await conn.Commands!.Reply($"--- Uniques ---\n{uniques.Count} definitions: {creatable} create, {unsupported} " +
            $"unsupported base type(s), {equipment} fit an equipment row, {withStats} with a recognized stat, " +
            $"{missingRows} with a missing Mods.dat row.");
        if (missing.Count > 0) await conn.Commands!.Reply($"Missing Mods.dat rows: {string.Join(", ", missing)}");
        if (empty.Count > 0) await conn.Commands!.Reply($"No stat found: {string.Join(", ", empty)}");
        Console.WriteLine($"[gateway] {remote} survivors uniques: {uniques.Count} definitions, {creatable} create, " +
            $"{equipment} equipment, {withStats} with stats, {missingRows} missing rows, {unsupported} unsupported");
    }

    /// <summary>
    /// A run starts with one life flask: the emptied character would otherwise have no recovery at all against the
    /// horde. It is created with its full charges and refills from kills like any flask; the next run's reset clears it.
    /// </summary>
    internal static async Task SurvivorsGiveFlask(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (StateOf(conn).Ownership is not { Current: true } ownership) return;
        if (ItemsFor(conn).Find("Metadata/Items/Flasks/FlaskLife1") is not { } baseType) return;
        var items = ItemsOf(conn);
        if (items.Items(PlayerInventories.Flask).Count > 0) return;
        InventoryItem placed = null!;
        if (!ownership.Commit(() => placed = items.Add(PlayerInventories.Flask, 0, 0, ItemCatalog.Create(baseType)))) return;
        await Send(remote, stream, ciphers,
            InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Flask),
                items.NextRevision(PlayerInventories.Flask), [], [placed]),
            "0x00bf survivors life flask", still: () => ownership.Current);
    }

    /// <summary>A new run starts from an empty character: every carried, worn and flask item is removed and the client
    /// is told row by row (0xbf), so nothing from an earlier run carries in.</summary>
    internal static async Task SurvivorsResetInventory(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (StateOf(conn).Ownership is not { Current: true } ownership) return;
        var items = ItemsOf(conn);
        var rows = Enumerable.Range(0, 16).Select(i => (byte)i).Append((byte)102).Append(PlayerInventories.Cursor).Distinct();
        foreach (byte row in rows)
        {
            if (!ownership.Current) return;
            var ids = items.Items(row).Select(i => i.Id).ToList();
            if (ids.Count == 0) continue;
            if (!ownership.Commit(() => { foreach (uint id in ids) items.Remove(row, id); })) return;
            await Send(remote, stream, ciphers,
                InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), ids, []),
                $"0x00bf survivors reset row {row}: {ids.Count} item(s) removed", still: () => ownership.Current);
        }
        ownership.Commit(() => StateOf(conn).SurvivorsItemIds.Clear());
    }

    /// <summary>Removes every item on the ground of the area for this run: the client forgets the object and the
    /// instance's store drops it, so loot neither shows nor comes back on the next connection.</summary>
    internal static async Task SurvivorsClearGround(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (StateOf(conn).Ownership is not { Current: true } ownership) return;
        foreach (var (id, _) in conn.Instance.Ground.All())
        {
            if (!ownership.Commit(() => conn.Instance.Ground.Take(id))) return;
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id),
                $"0x02bb survivors hide world item 0x{id:x}", still: () => ownership.Current);
        }
    }

    /// <summary>The unique items a level-up may still offer: the catalogue's definitions the run has not taken and the
    /// character is not already wearing (a worn unique is matched by its name word).</summary>
    internal static IReadOnlyList<int> SurvivorsItemPool(ClientState conn, SurvivorsRun run)
    {
        var all = UniquesFor(conn).All;
        var catalog = ItemsFor(conn);
        var taken = run.Items;
        var worn = new HashSet<uint>();
        foreach (byte row in ItemModStats.EquipmentRows)
            foreach (var item in ItemsOf(conn).Items(row))
                if (item.Item.NameWords is [var word, ..]) worn.Add(word);
        var pool = new List<int>();
        for (int i = 0; i < all.Count; i++)
        {
            if (taken.Contains(i)) continue;
            // Only an item that fits an equipment row can work in a run: a jewel or flask has nowhere to go in a
            // character whose inventory is emptied at the start, and offering it would waste the reward.
            if (catalog.Find(all[i].BaseTypeId) is not { } type
                || !ItemModStats.EquipmentRows.Any(row => PlayerInventories.Accepts(row, type.Class))) continue;
            if (UniquesFor(conn).NameWord(all[i]) is not { } own || worn.Contains(own)) continue;
            pool.Add(i);
        }
        return pool;
    }

    internal static async Task SurvivorsOfferIfDue(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, SurvivorsRun run, IReadOnlyList<SurvivorsSkill> catalog)
    {
        if (StateOf(conn).Ownership is not { Current: true } ownership || !ReferenceEquals(ownership.Run, run)) return;
        // A run that was stopped or replaced while this call was in flight must not deal an offer into the next run's
        // state (it would stop the clock for a window the next run cannot close).
        if (!ReferenceEquals(StateOf(conn).Survivors, run) || !run.NeedsOffer) return;
        var blessings = SurvivorsBlessingsFor(conn);
        var supports = SurvivorsSupportsFor(conn);
        var offer = run.DealOffer(Random.Shared, catalog.Count, blessings.Count, supports.Count,
            SurvivorsItemPool(conn, run), SurvivorsLinkRule(conn), StateOf(conn).Stage);
        if (offer.Count == 0) return;
        var items = ItemsOf(conn);
        var uniques = UniquesFor(conn).All;
        var shown = new Dictionary<uint, SurvivorsChoice>();
        var names = new Dictionary<uint, string>();
        var offeredItems = new Dictionary<uint, Item>();
        var listed = new List<InventoryItem>();
        var labels = new List<string>();
        byte cell = 0;
        foreach (var choice in offer)
        {
            // A unique item is created now, with its rolled mods, so the pick equips exactly what the window showed.
            Item? created = null;
            string? path = null;
            string name;
            if (choice.Kind == SurvivorsChoiceKind.Item)
            {
                if (choice.Index < 0 || choice.Index >= uniques.Count) continue;
                name = uniques[choice.Index].Name;
                if (UniquesFor(conn).Create(name, Random.Shared) is not { } unique) continue;
                // A run's unique is worn whatever the character's level: the level requirement is a byte we send.
                created = unique with { RequiredLevel = 0 };
            }
            else if (choice.Kind == SurvivorsChoiceKind.Heal)
            {
                // The reference's Healthstone: shown as a life flask, restores all life when picked.
                var flask = ItemsFor(conn).All.FirstOrDefault(t => t.Supported && t.Class == "LifeFlask");
                if (flask is null) continue;
                path = flask.Id;
                name = "Healthstone";
                created = ItemCatalog.Create(flask);
            }
            else
            {
                path = choice.Kind switch
                {
                    SurvivorsChoiceKind.Blessing => choice.Index >= 0 && choice.Index < blessings.Count ? blessings[choice.Index].Path : null,
                    SurvivorsChoiceKind.Support => choice.Index >= 0 && choice.Index < supports.Count ? supports[choice.Index].Path : null,
                    _ => choice.Index >= 0 && choice.Index < catalog.Count ? catalog[choice.Index].Path : null,
                };
                name = choice.Kind switch
                {
                    SurvivorsChoiceKind.Blessing => blessings[choice.Index].Name,
                    SurvivorsChoiceKind.Support => supports[choice.Index].Name,
                    _ => catalog[choice.Index].Name,
                };
                if (path is null || ItemsFor(conn).Find(path) is not { } baseType) continue;
                created = ItemCatalog.Create(baseType);
            }
            int width = ItemsFor(conn).ByHash(created.TypeHash)?.Width ?? 1;
            var item = new InventoryItem(items.NewItemId(), cell, 0, created);
            // A wide item (a two-cell unique) takes two cells: the next choice starts after it, or the icons overlap.
            cell = (byte)Math.Min(11, cell + Math.Max(1, width));
            listed.Add(item);
            shown[item.Id] = choice;
            names[item.Id] = name;
            labels.Add(SurvivorsChoiceLabel(run, choice, name, catalog));
            if (choice.Kind == SurvivorsChoiceKind.Item) offeredItems[item.Id] = created;
        }
        if (listed.Count == 0) return;
        if (!ownership.Commit(() =>
        {
            StateOf(conn).SurvivorsOffer = shown;
            StateOf(conn).SurvivorsOfferNames = names;
            StateOf(conn).SurvivorsOfferList = listed;
            StateOf(conn).SurvivorsOfferItems = offeredItems.Count > 0 ? offeredItems : null;
        // The choice pauses the run: the clock stops, so monsters, spawns, casts and recovery wait for the pick.
            if (conn.Clock.IsRunning) { conn.Clock.Stop(); StateOf(conn).SurvivorsPausedByWindow = true; }
        })) return;
        // 0xc0 registers the reward inventory with its contents: 0xbf for an id the client does not know is fatal
        // ("Got an inventory set change for an inventory that isn't in the set").
        await Send(remote, stream, ciphers,
            InstanceInventories.AddInventory(SurvivorsOfferInventory, NpcShops.Tab, NpcShops.Manager, listed),
            $"0x00c0 survivors level {run.Level}: {listed.Count} choice(s) offered", still: () => ownership.Current);
        if (!ownership.Current) return;
        await Send(remote, stream, ciphers, InstanceNpcs.OpenQuestReward(SurvivorsOfferInventory, 0),
            "0x0087 survivors level-up window", still: () => ownership.Current);
        if (!ownership.Current) return;
        // The window only shows gem icons; the chat line names the skill each upgrade is for and the supports a skill
        // is missing, so a pick is never a guess.
        if (conn.Commands is { } commands)
            await commands.Reply("--- Upgrades ---:\n" + string.Join('\n', labels.Select((label, index) => $"{index + 1}: {label}")));
    }

    /// <summary>A click in the level-up window (C→S 0x88/0x8a). False when the id is not one of its choices.</summary>
    internal static async Task<bool> ChooseSurvivorsSkill(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, uint itemId)
    {
        if (StateOf(conn).SurvivorsOffer is not { } shown) return false;
        if (!shown.TryGetValue(itemId, out var choice))
        {
            Console.WriteLine($"[gateway] {remote} survivors offer click 0x{itemId:x8} is not one of " +
                $"{string.Join(", ", shown.Keys.Select(k => $"0x{k:x8}"))}");
            return false;
        }
        return await ApplySurvivorsChoice(remote, stream, ciphers, conn, choice);
    }

    /// <summary>Applies one choice (the window's click or `/survivors pick`), closes the window and deals the next one.</summary>
    internal static async Task<bool> ApplySurvivorsChoice(string remote, NetworkStream stream, ConnectionCiphers ciphers,
        ClientState conn, SurvivorsChoice choice)
    {
        if (StateOf(conn).Ownership is not { Current: true } ownership) return false;
        var run = ownership.Run;
        int index = 0;
        for (int i = 0; i < run.Offer.Count; i++) if (run.Offer[i] == choice) { index = i + 1; break; }
        SurvivorsChoice chosen = null!;
        if (index == 0 || !ownership.Commit(() => run.TryChoose(index, out chosen)) || chosen is null) return false;
        string name = "a skill";
        Item? unique = null;
        if (StateOf(conn).SurvivorsOffer is { } offeredNow)
            foreach (var (id, c) in offeredNow)
                if (c == chosen)
                {
                    if (StateOf(conn).SurvivorsOfferNames?.GetValueOrDefault(id) is { } found) name = found;
                    if (StateOf(conn).SurvivorsOfferItems?.GetValueOrDefault(id) is { } shownUnique) unique = shownUnique;
                    break;
                }
        var offered = StateOf(conn).SurvivorsOffer?.Keys.ToList() ?? [];
        if (!ownership.Commit(() =>
        {
            StateOf(conn).SurvivorsOffer = null;
            StateOf(conn).SurvivorsOfferNames = null;
            StateOf(conn).SurvivorsOfferList = null;
            StateOf(conn).SurvivorsOfferItems = null;
        })) return false;
        var items = ItemsOf(conn);
        await Send(remote, stream, ciphers,
            InstanceItems.InventoryChange(SurvivorsOfferInventory, ++StateOf(conn).SurvivorsOfferRevision, offered, [], NpcShops.Manager),
            $"0x00bf survivors offer cleared after choosing {name}", still: () => ownership.Current);
        if (!ownership.Current) return false;
        var catalog = SurvivorsCatalogFor(conn);
        string target = chosen.Target >= 0 && chosen.Target < catalog.Count ? catalog[chosen.Target].Name : "the skill";
        // A unique item goes straight onto the character: the run's gear choice.
        bool equipped = unique is not null && await SurvivorsEquip(remote, stream, ciphers, conn, unique);
        if (!ownership.Current) return false;
        if (chosen.Kind == SurvivorsChoiceKind.Heal)
        {
            if (!ownership.Commit(() => conn.PlayerLife = conn.PlayerMaxLife)) return false;
            await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife),
                "survivors healthstone restores life", still: () => ownership.Current);
        }
        if (!ownership.Current) return false;
        await conn.Commands!.Reply(chosen.Kind switch
        {
            SurvivorsChoiceKind.Blessing => $"Took {name}, level {run.BlessingLevel((SurvivorsBlessing)chosen.Index)}.",
            SurvivorsChoiceKind.Support => $"Linked {name} to {target}.",
            SurvivorsChoiceKind.Item => equipped ? $"Equipped {name}." : $"Took {name}, but no equipment slot or inventory room was free.",
            SurvivorsChoiceKind.Heal => "The Healthstone restores all your life.",
            _ => $"Learned {name}, rank {run.RankOf(chosen.Index)}.",
        });
        if (!ownership.Current) return false;
        // A new skill or rank changes the Actor's skills and the bar: the client draws the new gem and its cooldown.
        await SendSkills(remote, stream, ciphers, conn);
        if (!ownership.Current) return false;
        await SurvivorsPublishHeralds(remote, stream, ciphers, conn, run, catalog);
        if (!ownership.Current) return false;
        await Send(remote, stream, ciphers, InstanceNpcs.Talk(0), "0x0084 survivors offer closed", still: () => ownership.Current);
        if (!ownership.Current) return false;
        await SurvivorsOfferIfDue(remote, stream, ciphers, conn, run, catalog);
        // Nothing left to choose: the run resumes where it paused.
        ownership.Commit(() =>
        {
            if (StateOf(conn).SurvivorsPausedByWindow && !run.NeedsOffer)
            {
                StateOf(conn).SurvivorsPausedByWindow = false;
                conn.Clock.Start();
            }
        });
        return true;
    }
}
