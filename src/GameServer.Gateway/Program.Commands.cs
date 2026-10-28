using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using Progression = GameServer.Core.World.Progression;
using DamageModel = GameServer.Core.World.DamageModel;
using DamageType = GameServer.Core.World.DamageType;

partial class Program
{
    // The chat commands of one connection (ChatCommands.cs).
    static ChatCommands Commands(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        return new ChatCommands
        {
            Send = (bytes, log) => Send(remote, stream, ciphers, bytes, log),
            // S→C 0x17 chat lines in the player's chat panel, one per line of the text (an empty line as a space).
            Reply = async text =>
            {
                foreach (string line in text.Split('\n').Select(l => l.Length == 0 ? " " : l))
                    await Send(remote, stream, ciphers, InstanceChat.ChatLine(line), $"0x0017 chat line \"{line}\"");
            },
        }
        .Add("raw", "/raw <hex> (send one S→C message)", async args =>
        {
            byte[] m = ChatCommands.ParseHex(args);
            await Send(remote, stream, ciphers, m, $"0x{m[0] << 8 | m[1]:x4} raw ({m.Length} bytes)");
        })
        .Add("set_speed", "/set_speed <percent> (100 = normal)", async args =>
        {
            int percent = int.Parse(args[0]);
            if (percent is < 10 or > 1000) throw new ArgumentException("10 to 1000");
            int totalPct = conn.Pools.MovementVelocityPct + percent - 100;
            conn.Walk.SetSpeed(WalkSpeed * (100 + totalPct) / 100, conn.Clock.Elapsed.TotalSeconds);
            lock (conn.Monsters) conn.PlayerStats[StatKeys.MovementVelocityPct] = totalPct;
            await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.MovementVelocityPct, totalPct)]),
                $"0x01b3 stats: movement_velocity_+% (key {StatKeys.MovementVelocityPct}) = {totalPct} ({conn.Pools.MovementVelocityPct} passive + {percent - 100} dev)");
        })
        .Add("set_stat", "/set_stat <stat name or client key> <value|off> (off removes the dev replacement)", async args =>
        {
            uint stat = Stats.Key(args[0]) ?? throw new ArgumentException($"unknown stat {args[0]}");
            bool off = args[1].Equals("off", StringComparison.OrdinalIgnoreCase);
            int value = off ? 0 : int.Parse(args[1]);
            bool rawAttribute = AttributeModifiers.IsInput(Stats.Name(stat));
            bool rebuildMana = stat == Stats.Key(StrengthMana.PerFourStrength)
                || stat == Stats.Key(PowerOfPurpose.RawFlag);
            lock (conn.Monsters)
            {
                if (off) conn.PlayerStats.Remove(stat);
                else conn.PlayerStats[stat] = value;
            }
            // Keep each supported raw consumer and source restoration when another mechanic changes this gate.
            if (AttackBaseCriticalStrike.IsInput(Stats.Name(stat) ?? args[0])
                || ResistanceOvercap.IsInput(Stats.Name(stat) ?? args[0])
                || rebuildMana || rawAttribute
                || Stats.Name(stat) == PlayerHitMitigation.ArmourAppliesToChaos)
            {
                while (true)
                {
                    CharacterStats.Pools before;
                    lock (conn.Monsters) before = conn.Pools;
                    var pools = PoolsFor(conn, before.Level);
                    lock (conn.Monsters)
                    {
                        // A concurrent level/aura rebuild wins; retry against its current inputs and level.
                        if (!ReferenceEquals(conn.Pools, before)) continue;
                        conn.Pools = pools;
                        if (rawAttribute)
                        {
                            conn.PlayerLife = Math.Min(conn.PlayerLife, conn.PlayerMaxLife);
                            conn.PlayerMana = Math.Min(conn.PlayerMana, (uint)UnreservedMana(conn));
                        }
                        else if (rebuildMana) conn.PlayerMana = Math.Min(conn.PlayerMana, (uint)UnreservedMana(conn));
                        break;
                    }
                }
            }
            // The client's requirement checks (gear and gems) read virtual_strength/dexterity/intelligence_for_requirements,
            // not the attributes themselves (13-items.md §2d), so an attribute set here sets its requirement value too.
            if (Stats.Key($"virtual_{args[0]}_for_requirements") is uint requirement && requirement != stat)
                lock (conn.Monsters)
                {
                    if (off) conn.PlayerStats.Remove(requirement);
                    else conn.PlayerStats[requirement] = value;
                }
            await SendPlayerStatSnapshot(remote, stream, ciphers, conn,
            () => PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)),
                $"0x01b3 stats: {Stats.Name(stat)} (key 0x{stat:x}) = {(off ? "the character's own" : value)}, and the attack's final stats");
            // Skills compute with their own stats (the attack and the character panel read those), so send them again.
            await SendSkills(remote, stream, ciphers, conn);
            if (rawAttribute)
            {
                // Skill refresh can restore an aura or update its reservation; use those completed caps too.
                lock (conn.Monsters)
                {
                    conn.PlayerLife = Math.Min(conn.PlayerLife, conn.PlayerMaxLife);
                    conn.PlayerMana = Math.Min(conn.PlayerMana, (uint)UnreservedMana(conn));
                }
                // A raw pool change can alter reservation while current mana stays idle. Sample resources only after
                // earlier queued sends, and never refill or resurrect a character when a maximum grows.
                await SendPlayerStatSnapshot(remote, stream, ciphers, conn,
                    () => InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerLife), "raw attribute current life");
                await SendPlayerMana(remote, stream, ciphers, conn, "raw attribute mana reservation");
            }
            else if (rebuildMana)
                await SendPlayerMana(remote, stream, ciphers, conn, "0x01b1 mana after raw source rebuild");
        })
        .Add("spawn_mob", "/spawn_mob <MonsterVarieties row, Id or name>[@level] [friendly] [normal|magic|rare|unique] [name]", async args =>
        {
            // "friendly": on the player's team (Positioned team 0); monsters are hostile (team 1) otherwise.
            bool friendly = args.Any(a => a.Equals("friendly", StringComparison.OrdinalIgnoreCase));
            args = [.. args.Where(a => !a.Equals("friendly", StringComparison.OrdinalIgnoreCase))];
            if (args.Length == 0) throw new ArgumentException("which monster");
            if (Monsters.Error is { } error) { await conn.Commands!.Reply(error); return; }
            // Words up to a rarity keyword name the monster; words after it are a custom name. Only the four names count:
            // Enum.TryParse would also accept "100" as a rarity number and send a byte the client cannot read (Buffer Underflow).
            int r = Array.FindIndex(args, a => a.ToLowerInvariant() is "normal" or "magic" or "rare" or "unique");
            var rarity = r < 0 ? MonsterRarity.Normal : Enum.Parse<MonsterRarity>(args[r], ignoreCase: true);
            string? name = r < 0 || r == args.Length - 1 ? null : string.Join(' ', args[(r + 1)..]);
            if (r == 0) throw new ArgumentException("which monster");
            string query = string.Join(' ', r < 0 ? args : args[..r]);
            byte level = 1;
            if (query.LastIndexOf('@') is int at and > 0)
            {
                level = byte.Parse(query[(at + 1)..]);
                if (level is < 1 or > 100) throw new ArgumentException("level 1 to 100");
                query = query[..at];
            }
            else if (r < 0 && args.Length > 1 && int.TryParse(args[^1], out int bare) && bare is >= 1 and <= 100)
            {
                // A bare number after the monster is the level most people mean (the syntax itself wants @).
                level = (byte)bare;
                query = query[..query.LastIndexOf(' ')];
            }
            if (Monsters.Find(query) is not { } variety) { await conn.Commands!.Reply($"No monster matches \"{query}\"."); return; }
            if (!variety.Supported)
            {
                await conn.Commands!.Reply($"{variety} is not supported yet: its type has components " +
                    $"{string.Join(", ", variety.ComponentsOrBase.Except(MonsterObject.BaseComponents))} that are not serialized.");
                return;
            }
            var (x, y) = conn.SpawnNear(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds));
            await SpawnMonster(remote, stream, ciphers, conn, variety, level, rarity, x, y, name, friendly);
        })
        .Add("item", "/item <BaseItemTypes row, Id, name or unique> [count] [magic|rare] (into the inventory, e.g. /item Small Life Flask 2, /item Iron Ring rare, /item Mageblood)", async args =>
        {
            if (args.Length == 0) throw new ArgumentException("which item?");
            ItemRarity rolled = ItemRarity.Normal;
            if (args.Length > 1 && args[^1].ToLowerInvariant() is "magic" or "rare") { rolled = Enum.Parse<ItemRarity>(args[^1], true); args = args[..^1]; }
            int count = 1, requested = 1;
            if (args.Length > 1 && int.TryParse(args[^1], out int n)) { requested = n; count = Math.Clamp(n, 1, 60); args = args[..^1]; }
            if (Items.Error is { } error) { await conn.Commands!.Reply(error); return; }
            string query = string.Join(' ', args);
            if (Uniques.Find(query) is { } unique)
            {
                var uniqueItems = ItemsOf(conn);
                var uniqueAdded = new List<InventoryItem>();
                for (int i = 0; i < count; i++)
                {
                    if (Uniques.Create(unique.Name, Random.Shared) is not { } createdUnique
                        || Items.ByHash(createdUnique.TypeHash) is not { } uniqueType
                        || uniqueItems.FreeCell(PlayerInventories.MainInventory, uniqueType.Width, uniqueType.Height, ShapeOf) is not { } uniqueCell) break;
                    uniqueAdded.Add(uniqueItems.Put(PlayerInventories.MainInventory,
                        new InventoryItem(uniqueItems.NewItemId(), uniqueCell.X, uniqueCell.Y, createdUnique)));
                }
                if (uniqueAdded.Count == 0) { await conn.Commands!.Reply("No room in the inventory"); return; }
                await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                    uniqueItems.NextRevision(PlayerInventories.MainInventory), [], uniqueAdded),
                    $"0x00bf inventory 1: /item {unique.Name} x {uniqueAdded.Count}");
                await conn.Commands!.Reply($"Added {unique.Name}");
                return;
            }
            if (Items.Find(query) is not { } type) { await conn.Commands!.Reply($"No item type \"{query}\""); return; }
            if (!type.Supported)
            {
                await conn.Commands!.Reply($"{type} has components the server cannot write yet: {string.Join(", ", type.Components.Where(c => !Item.Supported.Contains(c)))}");
                return;
            }
            var items = ItemsOf(conn);
            if (type.Stackable)
            {
                // A stackable type: [count] is how many, onto the stacks already there first, then into every free cell (at most
                // a full main inventory, 12 × 5 stacks); what doesn't fit is reported.
                int amount = Math.Clamp(requested, 1, type.MaxStack * 60);
                var stacked = items.AddStacked(PlayerInventories.MainInventory, ItemCatalog.Create(type) with { StackSize = (ushort)Math.Min(amount, ushort.MaxValue) }, ShapeOf);
                if (!stacked.Any) { await conn.Commands!.Reply("No room in the inventory"); return; }
                await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                    items.NextRevision(PlayerInventories.MainInventory), stacked.RemovedIds, stacked.AddedItems),
                    $"0x00bf inventory 1: /item {type} × {amount - stacked.Left} ({stacked.Changed.Count} stack(s) grown, {stacked.Added.Count} new)");
                if (stacked.Left > 0) await conn.Commands!.Reply($"No room for {stacked.Left} of them");
                return;
            }
            int itemLevel = Math.Clamp(conn.Pools.Level, 1, 100);
            var added = new List<InventoryItem>();
            for (int i = 0; i < count; i++)
            {
                if (items.FreeCell(PlayerInventories.MainInventory, type.Width, type.Height, ShapeOf) is not { } cell) break;
                var (sockets, links) = Items.DefaultSockets(type, itemLevel);
                var created = ItemCatalog.Create(type, sockets, links) with { ItemLevel = (byte)itemLevel };
                if (rolled != ItemRarity.Normal) created = Rolls.Roll(created, rolled, Random.Shared);
                added.Add(items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, created)));
            }
            if (added.Count == 0) { await conn.Commands!.Reply("No room in the inventory"); return; }
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), [], added), $"0x00bf inventory 1: /item {type} × {added.Count}");
        })
        .Add("gem", "/gem <name> [level] [quality] (a gem into the inventory at that level and quality, e.g. /gem Fireball 20 20, /gem Ice Nova of Frostbolts, /gem Vaal Fireball 5)", async args =>
        {
            // The trailing numbers: the level, then the quality.
            var numbers = new List<int>();
            while (args.Length > 1 && numbers.Count < 2 && int.TryParse(args[^1], out int n)) { numbers.Insert(0, n); args = args[..^1]; }
            if (args.Length == 0) throw new ArgumentException("which gem?");
            if (Items.Error is { } error) { await conn.Commands!.Reply(error); return; }
            string query = string.Join(' ', args);
            if (Items.FindGem(query) is not var (type, variant) || Items.Gem(type) is not { } gem) { await conn.Commands!.Reply($"No gem \"{query}\""); return; }
            if (!type.Supported)
            {
                await conn.Commands!.Reply($"{variant.Name} has components the server cannot write yet: {string.Join(", ", type.Components.Where(c => !Item.Supported.Contains(c)))}");
                return;
            }
            int level = numbers.Count > 0 ? numbers[0] : 1, quality = numbers.Count > 1 ? numbers[1] : 0;
            // Corruption (one level over the cap, quality up to 23%) is not modelled yet: the uncorrupted limits.
            if (level < 1 || level > gem.MaxLevel) { await conn.Commands!.Reply($"{variant.Name} has levels 1 to {gem.MaxLevel}"); return; }
            if (quality is < 0 or > 20) { await conn.Commands!.Reply("Quality is 0 to 20"); return; }
            var items = ItemsOf(conn);
            if (items.FreeCell(PlayerInventories.MainInventory, type.Width, type.Height, ShapeOf) is not { } cell) { await conn.Commands!.Reply("No room in the inventory"); return; }
            var created = ItemCatalog.Create(type) with { GemExperience = gem.ExperienceFor(level), GemVariant = variant.Index, Quality = (byte)quality };
            var added = items.Put(PlayerInventories.MainInventory, new InventoryItem(items.NewItemId(), cell.X, cell.Y, created));
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.MainInventory),
                items.NextRevision(PlayerInventories.MainInventory), [], [added]),
                $"0x00bf inventory 1: /gem {variant.Name} (variant {variant.Index}) level {level}, {quality}% quality, experience {created.GemExperience}");
        })
        .Add("gem_xp", "/gem_xp <amount|near> (dev: set the experience of every gem in the inventory and the sockets; near = one below the next level's threshold)", async args =>
        {
            if (args.Length != 1) throw new ArgumentException("an amount, or near");
            bool near = args[0].Equals("near", StringComparison.OrdinalIgnoreCase);
            int amount = near ? 0 : int.Parse(args[0]);
            var items = ItemsOf(conn);
            int Set(Item gem, ItemCatalog.GemType type)
            {
                int level = Items.GemLevel(gem);
                int max = Math.Min(Skills.MaxLevelAt(type.Variant(gem.GemVariant).Effect, conn.Pools.Level), type.MaxLevel);
                return near ? (level < max ? type.ExperienceFor(level + 1) - 1 : gem.GemExperience) : amount;
            }
            var changed = new List<(byte Row, InventoryItem Item)>();
            foreach (var held in items.Items(PlayerInventories.MainInventory))
                if (Items.ByHash(held.Item.TypeHash) is { } baseType && Items.Gem(baseType) is { } type)
                    if (items.Change(PlayerInventories.MainInventory, held.Id, it => it with { GemExperience = Set(it, type) }) is { } after)
                        changed.Add((PlayerInventories.MainInventory, after));
            foreach (var (row, item, _) in items.ChangeSocketedGems((_, gem) =>
                Items.ByHash(gem.TypeHash) is { } baseType && Items.Gem(baseType) is { } type ? gem with { GemExperience = Set(gem, type) } : null))
                changed.Add((row, item));
            if (changed.Count == 0) { await conn.Commands!.Reply("No gem to change"); return; }
            foreach (var row in changed.GroupBy(c => c.Row))
                await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row.Key), items.NextRevision(row.Key),
                    [.. row.Select(c => c.Item.Id)], [.. row.Select(c => c.Item)]),
                    $"0x00bf inventory {row.Key}: /gem_xp {(near ? "near" : amount.ToString())} ({string.Join(", ", row.Select(c => $"{c.Item.Id:x} exp {c.Item.Item.GemExperience}"))})");
        })
        .Add("map_portal", "/map_portal [type 1-4] [created 0|1] [open after ms, -1 never] [model 0-2] (dev: one MultiplexPortal next to you, listed in the map device's 0x8f)", async args =>
        {
            if (args.Length > 3) conn.MapPortalStyle = byte.Parse(args[3]);
            byte type = args.Length > 0 ? byte.Parse(args[0]) : MapPortalObject.MapDevicePortal;
            byte created = args.Length > 1 ? byte.Parse(args[1]) : (byte)1;
            int openMs = args.Length > 2 ? int.Parse(args[2]) : -1;
            var (x, y) = conn.SpawnNear(conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds));
            uint id = conn.NextObjectId++;
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(MapPortalObject.MetadataPath),
                new MapPortalObject(x, y, 0, type, created, Rotation: conn.PortalRotationRadians).Blob()),
                $"0x02b9 create test portal 0x{id:x} type {type}, transitionable {created}, at ({x}, {y})");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
            lock (conn.MapPortals) conn.MapPortals[id] = (x, y, -1);
            if (openMs >= 0) OpenMapPortalLater(remote, stream, ciphers, conn, id, openMs);
            uint deviceId;
            lock (conn.HideoutDoodads) deviceId = conn.HideoutDoodads.Keys.FirstOrDefault();
            if (deviceId != 0) SendMapPortalsLater(remote, stream, ciphers, conn, deviceId, MapDevice.OpenOf(CharacterName(conn))?.Area.Hash16 ?? conn.Layout.Hash, MapPortalListMs);
            await conn.Commands!.Reply($"Portal 0x{id:x}: type {type}, created {created}, " + (openMs >= 0 ? $"opens after {openMs} ms" : "not opened"));
        })
        .Add("cosmetic", "/cosmetic portal <name, number or part of a name> | /cosmetic list [filter] | /cosmetic off (the portal cosmetic: the shop effect replaces the base portal visual)", async args =>
        {
            if (args.Length == 0)
            {
                await conn.Commands!.Reply($"{Cosmetics.Portals.Count} portal effects. " +
                    "/cosmetic list <part of a name> lists the ones that have it, /cosmetic portal <number or name> applies one.");
                return;
            }
            string what = args[0].ToLowerInvariant();
            if (what is not ("list" or "portal" or "off")) throw new ArgumentException("list, portal or off");
            if (what == "off")
            {
                conn.CosmeticPortalVariation = 0;
                CharacterSaves.SetPortalCosmetic(CharacterName(conn), 0);
                bool back = await ResendMapPortals(remote, stream, ciphers, conn);
                await conn.Commands!.Reply(back
                    ? "Portal cosmetic off: the open portals are back to the base effect."
                    : "Portal cosmetic off: the next portal uses the base effect.");
                return;
            }
            string filter = string.Join(' ', args.Skip(1).Where(a => !a.Equals("portal", StringComparison.OrdinalIgnoreCase)));
            if (what == "list")
            {
                var shown = filter.Length == 0 ? Cosmetics.Portals : CosmeticCatalog.Matches(Cosmetics.Portals, filter);
                await conn.Commands!.Reply(shown.Count == 0
                    ? Cosmetics.Portals.Count == 0 ? "No portal effects: data/tables is missing." : $"No portal effects match \"{filter}\"."
                    : $"{shown.Count} portal effects: " + string.Join(", ", shown.Take(12).Select(c => c.ToString())) + (shown.Count > 12 ? ", ..." : ""));
                return;
            }
            if (filter.Length == 0)
            {
                // Nothing named: the first page of the list, so the numbers that pick one are right there.
                await conn.Commands!.Reply($"{Cosmetics.Portals.Count} portal effects: " + string.Join(", ", Cosmetics.Portals.Take(12)) +
                    (Cosmetics.Portals.Count > 12 ? ", ..." : "") + " — use a number, a name or a piece of one.");
                return;
            }
            var matches = CosmeticCatalog.Matches(Cosmetics.Portals, filter);
            if (matches.Count == 0) throw new ArgumentException($"no portal effect matches \"{filter}\" (try /cosmetic list {filter})");
            if (matches.Count > 1)
            {
                // Several contain what was typed: name them with the numbers that pick one, rather than guessing which was
                // meant. The next /cosmetic with one of the numbers (or a longer fragment) applies it.
                await conn.Commands!.Reply($"{matches.Count} portal effects match \"{filter}\": " +
                    string.Join(", ", matches.Take(12).Select(c => c.ToString())) + (matches.Count > 12 ? ", ..." : "") +
                    ". Add more of the name, or use one of the numbers.");
                return;
            }
            var cosmetic = matches[0];
            // A portal cosmetic is the portal's own variation (+0x26 of its 0x008f entry), not a second object: the client
            // swaps the base portal effect for the shop one.
            conn.CosmeticPortalVariation = (ushort)cosmetic.Index;
            CharacterSaves.SetPortalCosmetic(CharacterName(conn), conn.CosmeticPortalVariation);   // survives a relog
            bool applied = await ResendMapPortals(remote, stream, ciphers, conn);
            await conn.Commands!.Reply($"portal: {cosmetic.Name} (variation {cosmetic.Index})" +
                (applied ? ", applied to the open portals" : ", the next portal uses it"));
        })
        .Add("portal_rotation", "/portal_rotation <degrees> (dev: turn the map portals around the vertical axis; rebuilds the ones standing)", async args =>
        {
            if (args.Length == 0) throw new ArgumentException("degrees");
            conn.PortalRotationDegrees = float.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
            int rebuilt = await RebuildMapPortals(remote, stream, ciphers, conn);
            await conn.Commands!.Reply(rebuilt == 0
                ? $"Map portals will be created at {conn.PortalRotationDegrees:0.#}°."
                : $"{rebuilt} map portal(s) rebuilt at {conn.PortalRotationDegrees:0.#}°.");
        })
        .Add("mod", "/mod [implicit|explicit|enchant|scourge|hidden] <Mods row, Id or part of it> [values…] | /mod clear [section] (the held item; missing values are rolled)", async args =>
        {
            if (args.Length == 0) throw new ArgumentException("which mod?");
            ModSection? section = null;
            if (args.Length > 1 && ParseSection(args[0]) is { } s) { section = s; args = args[1..]; }
            if (args[0].Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                var only = args.Length > 1 ? ParseSection(args[1]) : section;
                await ChangeHeldItem(remote, stream, ciphers, conn, i => i with { Mods = [.. i.Mods.Where(m => only is { } o && m.Section != o)] },
                    $"mods cleared{(only is { } o ? $" ({o})" : "")}");
                return;
            }
            if (Items.Mods.Find(args[0]) is not { } mod) { await conn.Commands!.Reply($"No mod \"{args[0]}\" in Mods.dat"); return; }
            var values = args[1..].Select(int.Parse).ToList();
            var added = mod.With(section ?? mod.DefaultSection, values, Random.Shared);
            await ChangeHeldItem(remote, stream, ciphers, conn, i => i with { Mods = [.. i.Mods, added] }, $"+ {Items.Mods.Describe(added)}");
        })
        .Add("roll", "/roll <magic|rare> [item level] (new affixes and name for the held item)", async args =>
        {
            if (args.Length == 0 || !Enum.TryParse<ItemRarity>(args[0], true, out var rarity) || rarity is not (ItemRarity.Magic or ItemRarity.Rare))
                throw new ArgumentException("magic or rare");
            byte? level = args.Length > 1 ? byte.Parse(args[1]) : null;
            await ChangeHeldItem(remote, stream, ciphers, conn, i => Rolls.Roll(level is byte l ? i with { ItemLevel = l } : i, rarity, Random.Shared),
                $"rolled {rarity}");
        })
        .Add("mods", "/mods (the held item's rarity and mods)", async args =>
        {
            if (ItemsOf(conn).Items(PlayerInventories.Cursor) is not [var held, ..]) { await conn.Commands!.Reply("Hold an item first"); return; }
            await conn.Commands!.Reply($"{(held.Item.NameWords.Count > 0 ? Rolls.NameOf(held.Item.NameWords) + ", " : "")}" +
                $"{Items.ByHash(held.Item.TypeHash)?.Name} ilvl {held.Item.ItemLevel}, {held.Item.Rarity}" +
                $"{(held.Item.Identified ? "" : ", unidentified")}: " +
                (held.Item.Mods.Count == 0 ? "no mods" : string.Join("; ", held.Item.Mods.Select(Items.Mods.Describe))));
        })
        .Add("rarity", "/rarity <normal|magic|rare|unique> [unid] (the held item)", async args =>
        {
            if (args.Length == 0 || !Enum.TryParse<ItemRarity>(args[0], true, out var rarity)) throw new ArgumentException("normal, magic, rare or unique");
            bool identified = !args.Skip(1).Any(a => a.StartsWith("unid", StringComparison.OrdinalIgnoreCase));
            await ChangeHeldItem(remote, stream, ciphers, conn, i => i with { Rarity = rarity, Identified = identified },
                $"{rarity}{(identified ? "" : ", unidentified")}");
        })
        .Add("sockets", "/sockets [colours] (the held item's sockets: R G B W, linked within a word, e.g. /sockets RRGBBB a 6-link, /sockets RRG B a 3-link and a single; without colours, shows them; Game_SOCKET_CAPACITY=1..6 lets any socketable base take that many, e.g. a 6-link wand)", async args =>
        {
            if (ItemsOf(conn).Items(PlayerInventories.Cursor) is not [var held, ..]) { await conn.Commands!.Reply("Hold an item first"); return; }
            if (args.Length == 0) { await conn.Commands!.Reply($"sockets: {SocketLayout.Describe(held.Item)}"); return; }
            if (Items.ByHash(held.Item.TypeHash) is not { } type) { await conn.Commands!.Reply("Unknown item type"); return; }
            if (SocketLayout.Parse(args, out string? error) is not var (colours, links)) throw new ArgumentException(error);
            int capacity = SocketLayout.CapacityOverride;
            if (SocketLayout.Apply(held.Item, type, colours, links, out error, capacity) is not { } result) { await conn.Commands!.Reply(error!); return; }
            await ChangeHeldItem(remote, stream, ciphers, conn, i => SocketLayout.Apply(i, type, colours, links, out _, capacity) ?? i,
                $"sockets: {SocketLayout.Describe(result)}", "Sockets");
        })
        .Add("set_level", "/set_level <1 to 100> (saved to the account; the level's experience and stats are sent at once)", async args =>
        {
            if (args.Length == 0 || !int.TryParse(args[0], out int level) || level is < 1 or > 100) throw new ArgumentException("level 1 to 100");
            await SetLevel(remote, stream, ciphers, conn, level);
            var r = await SynchronizeAccountLevel(conn);
            if (!r.Ok) await conn.Commands!.Reply($"Level saved locally; account update failed: {r.Error}");
        })
        .Add("add_xp", "/add_xp <amount> (experience as from a kill: levels up, saved)", async args =>
        {
            if (args.Length == 0 || !uint.TryParse(args[0], out uint xp) || xp == 0) throw new ArgumentException("an amount of experience");
            await GainExperience(remote, stream, ciphers, conn, xp, "/add_xp");
        })
        .Add("passives", "/passives (passive skill points and the allocated passives)", async _ =>
        {
            var (points, refundPoints, nodes, _) = PassiveState(conn);
            int unspent = points + Passives.PointsGranted(nodes) + AscendancyOrdinaryPoints(conn) - nodes.Count;
            await conn.Commands!.Reply($"Level {conn.Pools.Level}, {conn.Experience} experience: {points} passive skill point(s), {unspent} unspent, {refundPoints} refund point(s).\n" +
                string.Join("\n", nodes.Select(n => $"{n} {Passives.Find(n)?.Name}").DefaultIfEmpty("No passives allocated.")));
        })
        .Add("ascendancy", "/ascendancy [normal|phrecia] [name] (list or choose; Phrecia play is gated pending client compatibility)", async args =>
        {
            byte baseClass = (byte)Skills.ClassFor(conn.ClassRow).IntegerId;
            var choices = Ascendancies.Choices(baseClass).ToArray();
            if (choices.Length == 0) throw new ArgumentException("ascendancy data is missing; run scripts/dev/extract-data.ps1");
            if (args.Length == 0)
            {
                var current = AscendancyRules.Validate(baseClass, conn.MainAscendancy);
                int spent = AscendancyRules.SpentPoints(baseClass, conn.MainAscendancy);
                await conn.Commands!.Reply($"Main ascendancy - {current?.Name ?? "none"}. Ascendancy points {spent}/{conn.MainAscendancy.EarnedPoints}.\n" +
                    string.Join("\n", choices.Select(c => $"/ascendancy {c.Identity.Family.ToString().ToLowerInvariant()} {c.Name}{(c.Identity.Family == AscendancyFamily.Phrecia ? " (play gated)" : "")}")));
                return;
            }
            bool hasFamily = Enum.TryParse<AscendancyFamily>(args[0], true, out var family) && Enum.IsDefined(family);
            string name = string.Join(' ', hasFamily ? args[1..] : args);
            var matching = choices.Where(c => (!hasFamily || c.Identity.Family == family) &&
                (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || c.Identity.Id.Equals(name, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matching.Length != 1) throw new ArgumentException("choose one name from /ascendancy, including its normal/phrecia family when needed");
            var chosen = matching[0];
            AscendancyClientCompatibility.RequirePlayable(chosen.Identity);
            if (conn.MainAscendancy.Selection == chosen.Identity)
            {
                await conn.Commands!.Reply($"{chosen.Name} is already your main ascendancy.");
                return;
            }
            var next = Saves.Atomically(() =>
            {
                var saved = CharacterSaves.Load(CharacterName(conn));
                return CharacterSaves.ChangeMainAscendancy(CharacterName(conn), latest =>
                {
                    var candidate = AscendancyRules.Select(baseClass, latest, chosen.Identity);
                    ValidateOrdinaryPassives(conn, saved?.Passives ?? [], candidate);
                    return candidate;
                });
            });
            lock (conn.Monsters)
            {
                conn.MainAscendancy = next;
                conn.SavedPassiveProgress = new(next, conn.Passives, conn.MasteryEffects, conn.RefundPoints);
            }
            await conn.Commands!.Reply($"Main ascendancy selected - {chosen.Name}. Reloading the area to show its tree.");
            await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(conn.Layout.Hash, 0, false), $"ascendancy {chosen.Name}");
        })
        .Add("ascendancy_points", "/ascendancy_points <normal|cruel|merciless|eternal|all> (dev: grant each Labyrinth's two ascendancy points once)", async args =>
        {
            if (args.Length != 1) throw new ArgumentException("a Labyrinth tier or all");
            string tier = args[0].ToLowerInvariant();
            byte baseClass = (byte)Skills.ClassFor(conn.ClassRow).IntegerId;
            var next = CharacterSaves.ChangeMainAscendancy(CharacterName(conn), latest =>
            {
                foreach (string award in tier == "all" ? global::AscendancyRules.AwardKeys : [tier])
                    latest = AscendancyRules.Award(baseClass, latest, award);
                return latest;
            });
            lock (conn.Monsters)
            {
                conn.MainAscendancy = next;
                conn.SavedPassiveProgress = new(next, conn.Passives, conn.MasteryEffects, conn.RefundPoints);
            }
            await SendPassives(remote, stream, ciphers, conn);
            await conn.Commands!.Reply($"Ascendancy points earned - {next.EarnedPoints}; each tier can be earned once.");
        })
        .Add("reset_ascendancy", "/reset_ascendancy (refund all ascendancy nodes; five refund points per node/effect, choice shells are free)", async _ =>
        {
            if (conn.MainAscendancy.Allocated.Count == 0) { await conn.Commands!.Reply("No ascendancy passives allocated."); return; }
            var error = await ChangePassives(remote, stream, ciphers, conn, conn.MainAscendancy.Allocated.ToArray(), false, "/reset_ascendancy", refundPoints: true);
            await conn.Commands!.Reply(error ?? "Ascendancy passives refunded; you can choose another main ascendancy.");
        })
        .Add("gold", "/gold <amount> (dev: add gold, or take it with a negative amount; the account's, saved)", async args =>
        {
            if (args.Length == 0 || !long.TryParse(args[0], out long delta)) throw new ArgumentException("an amount of gold (negative takes gold)");
            uint gold = PersonalStashOf(conn).AddGold(delta);
            await Send(remote, stream, ciphers, InstanceEntities.Gold(gold), $"0x02a9 gold: {gold} (/gold {delta})");
            await conn.Commands!.Reply($"Gold: {gold}");
        })
        .Add("add_passive_points", "/add_passive_points <n> (dev: add n extra passive skill points, saved)", async args =>
        {
            if (args.Length == 0 || !int.TryParse(args[0], out int add) || add <= 0) throw new ArgumentException("a positive number");
            ushort total;
            lock (conn.Monsters)
            {
                total = Saves.Atomically(() =>
                {
                    ushort current = CharacterSaves.Load(CharacterName(conn))?.ExtraPassivePoints ?? 0;
                    ushort next = (ushort)Math.Min(ushort.MaxValue, (long)current + add);
                    CharacterSaves.SetExtraPassivePoints(CharacterName(conn), next);
                    return next;
                });
                conn.ExtraPassivePoints = total;
            }
            await SendPassives(remote, stream, ciphers, conn);
            await conn.Commands!.Reply($"{add} extra passive point(s) added; {total} extra total.");
        })
        .Add("add_refund_points", "/add_refund_points <n> (dev: add n refund points, saved)", async args =>
        {
            if (args.Length == 0 || !int.TryParse(args[0], out int add) || add <= 0) throw new ArgumentException("a positive number");
            ushort total = Saves.Atomically(() =>
            {
                ushort next = (ushort)Math.Min(ushort.MaxValue, (CharacterSaves.Load(CharacterName(conn))?.RefundPoints ?? 0) + add);
                CharacterSaves.SetRefundPoints(CharacterName(conn), next);
                return next;
            });
            lock (conn.Monsters)
            {
                conn.RefundPoints = total;
                conn.SavedPassiveProgress = new(conn.MainAscendancy, conn.Passives, conn.MasteryEffects, total);
            }
            await SendPassives(remote, stream, ciphers, conn);
            await conn.Commands!.Reply($"{add} refund point(s) added; {total} total.");
        })
        .Add("add_passive", "/add_passive <PassiveSkillGraphId> [...] (dev: allocate a connected set of passive nodes)", async args =>
        {
            if (args.Length == 0 || args.Any(a => !ushort.TryParse(a, out _))) throw new ArgumentException("PassiveSkillGraphId numbers");
            ushort[] nodes = [.. args.Select(ushort.Parse)];
            string? error = await ChangePassives(remote, stream, ciphers, conn, nodes, allocate: true, "/add_passive");
            await conn.Commands!.Reply(error ?? $"Passives {string.Join(", ", nodes)} allocated.");
        })
        .Add("reset_passives", "/reset_passives (refunds every passive, saved)", async _ =>
        {
            var (_, _, nodes, _) = PassiveState(conn);
            await ChangePassives(remote, stream, ciphers, conn, nodes, allocate: false, "/reset_passives");
            await conn.Commands!.Reply($"{nodes.Count} passive(s) refunded.");
        })
        .Add("resistances", "/resistances (your uncapped and capped fire/cold/lightning/chaos resistance)", async _ =>
        {
            var p = conn.Pools;
            int penalty = ResistancePenalty(conn);
            int fire = p.FireResistance + penalty, cold = p.ColdResistance + penalty;
            int lightning = p.LightningResistance + penalty, chaos = p.ChaosResistance + penalty;
            await conn.Commands!.Reply(
                $"Kitava penalty {penalty}%. " +
                $"Fire {fire}% (passives {p.FireResistance}%, max {p.FireMaxResistance}%, capped {DamageModel.Capped(fire, p.FireMaxResistance)}%), " +
                $"Cold {cold}% (passives {p.ColdResistance}%, max {p.ColdMaxResistance}%, capped {DamageModel.Capped(cold, p.ColdMaxResistance)}%), " +
                $"Lightning {lightning}% (passives {p.LightningResistance}%, max {p.LightningMaxResistance}%, capped {DamageModel.Capped(lightning, p.LightningMaxResistance)}%), " +
                $"Chaos {chaos}% (passives {p.ChaosResistance}%, max {p.ChaosMaxResistance}%, capped {DamageModel.Capped(chaos, p.ChaosMaxResistance)}%).");
        })
        .Add("hit", "/hit <fire|cold|lightning|chaos|physical> <damage> (dev: incoming enemy test contact through resistances or armour)", async args =>
        {
            if (args.Length < 2) throw new ArgumentException("a damage type and an amount");
            if (ParseDamageType(args[0]) is not { } type) throw new ArgumentException("fire, cold, lightning, chaos or physical");
            if (!int.TryParse(args[1], out int amount) || amount <= 0) throw new ArgumentException("a positive amount");
            if (!TryDevEnemyHit(conn, type, amount, false, false, false, out var hit,
                ranged: args.Length > 2 && args[2] == "ranged"))
            {
                await conn.Commands!.Reply(conn.PlayerLife == 0 ? "You are already dead." : "Enemy test hit rejected while paused or leaving.");
                return;
            }
            int dealt = hit.Damage; string mitigation = hit.Mitigation; uint life = hit.Life;
            await SendPlayerEnergyShield(remote, stream, ciphers, conn);
            if (life == 0)
                await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.HideMiniLifeBar, 1)]),
                    "0x01b3 player hide_mini_life_bar = 1 (dev hit)");
            await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, life),
                $"0x01b1 player dev {type} hit for {dealt} ({amount} before {mitigation}), life {life}/{conn.PlayerMaxLife}");
            if (life == 0)
                await PlayerDied(remote, stream, ciphers, conn, $"0x0055 death screen ({conn.League ?? "?"} league, dev hit)");
            await conn.Commands!.Reply($"{type}: {amount} → {dealt} after {mitigation}; life {life}/{conn.PlayerMaxLife}.");
        })
        .Add("suppression", "/suppression (Spell Suppression chance, roll and damage reduction)", async _ =>
        {
            var (chance, effect, lucky, unlucky) = Suppression(conn);
            double effective = DamageModel.EffectiveSuppressionChance(chance, lucky, unlucky);
            await conn.Commands!.Reply($"Spell Suppression: {chance}% chance (cap {DamageModel.SpellSuppressionChanceCap}%, effective {effective:0.#}%" +
                $"{(lucky ? ", lucky" : "")}{(unlucky ? ", unlucky" : "")}), {effect}% less damage on a suppressed spell hit.");
        })
        .Add("suppression_luck", "/suppression_luck <lucky|unlucky|off> (dev: force a lucky/unlucky suppression roll)", async args =>
        {
            string mode = args.Length == 0 ? "" : args[0].ToLowerInvariant();
            bool lucky = mode == "lucky", unlucky = mode == "unlucky";
            if (mode is not ("lucky" or "unlucky" or "off" or "")) throw new ArgumentException("lucky, unlucky or off");
            lock (conn.Monsters)
            {
                conn.ForceSpellSuppressionLucky = lucky;
                conn.ForceSpellSuppressionUnlucky = unlucky;
                conn.Pools = PoolsFor(conn, conn.Pools.Level);
            }
            await SendCharacterStats(remote, stream, ciphers, conn, $"suppression luck {mode}");
            await conn.Commands!.Reply($"Spell Suppression roll: {(lucky ? "lucky" : unlucky ? "unlucky" : "normal")}.");
        })
        .Add("accuracy", "/accuracy [monster level] (accuracy rating and attack hit chance; default: your level)", async args =>
        {
            int level = conn.Pools.Level;
            if (args.Length > 0)
            {
                if (!int.TryParse(args[0], out int requested) || requested is < 1 or > Progression.MaxLevel)
                    throw new ArgumentException($"monster level 1 to {Progression.MaxLevel}");
                level = requested;
            }
            int evasion = Monsters.BaseEvasion(level) ?? 0;
            int evasiveEvasion = Monsters.BaseEvasiveEvasion(level) ?? 0;
            int chance = DamageModel.AccuracyHitChance(evasion, conn.Pools.Accuracy);
            int evasiveChance = DamageModel.AccuracyHitChance(evasiveEvasion, conn.Pools.Accuracy);
            var withMastery = conn.Pools.AccuracyMoreVsUniquePct != 0 || conn.Pools.AccuracyMoreInCloseRangePct != 0
                ? $" Targets gain {conn.Pools.AccuracyMoreVsUniquePct:0.#}% more accuracy against unique enemies and " +
                  $"{conn.Pools.AccuracyMoreInCloseRangePct:0.#}% more within the 20-unit close range, applied per attack roll."
                : "";
            await conn.Commands!.Reply($"Accuracy {conn.Pools.Accuracy} ({conn.Pools.AccuracyPct}% increased): " +
                $"{chance}% chance to hit a level {level} monster (evasion {evasion}), " +
                $"{evasiveChance}% against evasive monsters (evasion {evasiveEvasion}). Spells always hit." + withMastery);
        })
        .Add("defences", "/defences [monster level] (armour, evasion and avoidance; default: your level)", async args =>
        {
            int level = conn.Pools.Level;
            if (args.Length > 0)
            {
                if (!int.TryParse(args[0], out int requested) || requested is < 1 or > Progression.MaxLevel)
                    throw new ArgumentException($"monster level 1 to {Progression.MaxLevel}");
                level = requested;
            }
            int monsterAccuracy = Monsters.BaseAccuracy(level) ?? 0;
            int monsterDamage = Monsters.BaseDamage(level) ?? 0;
            int hitChance = DamageModel.AccuracyHitChance(conn.Pools.Evasion, monsterAccuracy);
            int reduction = DamageModel.ArmourReduction(conn.Pools.Armour, monsterDamage);
            await conn.Commands!.Reply($"Armour {conn.Pools.Armour} ({conn.Pools.ArmourPct}% increased): " +
                $"{reduction}% physical reduction against a level {level} monster hit ({monsterDamage}), " +
                $"evasion {conn.Pools.Evasion} ({conn.Pools.EvasionPct}% increased): {100 - hitChance}% chance to evade " +
                $"(monster accuracy {monsterAccuracy}, {hitChance}% chance to be hit).");
        })
        .Add("auras", "/auras (active auras, their reservation and the character's efficiency)", async _ =>
        {
            List<AuraCatalog.Aura> auras;
            int reserved;
            int maxMana;
            int efficiency;
            int auraEffect;
            lock (conn.Monsters)
            {
                auras = [.. conn.Auras.Values];
                reserved = AuraCatalog.ReservedMana(conn.Pools, auras);
                maxMana = (int)conn.Pools.Mana;
                efficiency = conn.Pools.ManaReservationEfficiencyPct;
                auraEffect = conn.Pools.AuraEffectPct;
            }
            string list = auras.Count == 0 ? "none" : string.Join(", ", auras.Select(a =>
                $"{a.Name} ({a.ManaFlat} flat, {a.ManaPercent / 100.0:0.##}%)"));
            await conn.Commands!.Reply($"Auras: {list}. Mana {conn.PlayerMana}/{maxMana}, {reserved} reserved " +
                $"({maxMana - reserved} unreserved); reservation efficiency {efficiency}%, aura effect {auraEffect}%.");
        })
        .Add("spellhit", "/spellhit <fire|cold|lightning|chaos|physical> <damage> (dev: spell hit through resistance and Spell Suppression)", async args =>
        {
            if (args.Length < 2) throw new ArgumentException("a damage type and an amount");
            if (ParseDamageType(args[0]) is not { } type) throw new ArgumentException("fire, cold, lightning, chaos or physical");
            if (!int.TryParse(args[1], out int amount) || amount <= 0) throw new ArgumentException("a positive amount");
            if (!TryDevEnemyHit(conn, type, amount, false, true, true, out var hit))
            {
                await conn.Commands!.Reply(conn.PlayerLife == 0 ? "You are already dead." : "Enemy test hit rejected while paused or leaving.");
                return;
            }
            int dealt = hit.Damage; string mitigation = hit.Mitigation; uint life = hit.Life;
            bool suppressed = hit.Suppressed;
            var (chance, effect, lucky, unlucky) = hit.Suppression;
            await SendPlayerEnergyShield(remote, stream, ciphers, conn);
            if (life == 0)
                await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.HideMiniLifeBar, 1)]),
                    "0x01b3 player hide_mini_life_bar = 1 (dev spell hit)");
            await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, life),
                $"0x01b1 player dev spell {type} hit for {dealt} ({amount} before {mitigation}, suppression {(suppressed ? "yes" : "no")}), life {life}/{conn.PlayerMaxLife}");
            if (life == 0)
                await PlayerDied(remote, stream, ciphers, conn, $"0x0055 death screen ({conn.League ?? "?"} league, dev spell hit)");
            double effective = DamageModel.EffectiveSuppressionChance(chance, lucky, unlucky);
            await conn.Commands!.Reply($"{type} spell hit: {amount} → {dealt} after {mitigation}; " +
                $"suppression {(suppressed ? "rolled" : "not rolled")} ({chance}% chance, effective {effective:0.#}%" +
                $"{(lucky ? ", lucky" : "")}{(unlucky ? ", unlucky" : "")}); life {life}/{conn.PlayerMaxLife}.");
        })
        .Add("charges", ChargesUsage, args => ChargesCommand(conn, args))
        .Add("crit", "/crit (attack and spell critical strike chance and multiplier)", async _ =>
        {
            var p = conn.Pools;
            await conn.Commands!.Reply(
                $"Attack crit {p.AttackCriticalStrikeChance / 100.0:0.##}% ({p.CriticalStrikeChancePct}% increased), " +
                $"multiplier {AttackCriticalMultiplier(conn, CharacterPlayerStats(conn))}%. Spell crit reference (5% base) {p.SpellCriticalStrikeChance / 100.0:0.##}% " +
                $"({p.SpellCriticalStrikeChancePct}% increased), multiplier {p.SpellCriticalStrikeMultiplier}%.");
        })
        .Add("block", "/block (attack and spell block chance and caps)", async _ =>
        {
            var p = conn.Pools;
            await conn.Commands!.Reply($"Block {p.BlockChance}% (max {p.BlockChanceMax}%, effective {DamageModel.EffectiveBlockChance(p.BlockChance, p.AttackBlockLucky):0.#}%), " +
                $"spell block {p.SpellBlockChance}% (max {p.SpellBlockChanceMax}%, effective {DamageModel.EffectiveBlockChance(p.SpellBlockChance, p.SpellBlockLucky):0.#}%)" +
                $"{(p.AttackBlockLucky ? "; attack lucky" : "")}{(p.SpellBlockLucky ? "; spell lucky" : "")}.");
        })
        .Add("block_luck", "/block_luck <attack|spell|both|off> (dev: force lucky block)", async args =>
        {
            string mode = args.Length == 0 ? "" : args[0].ToLowerInvariant();
            bool attack = mode is "attack" or "both", spell = mode is "spell" or "both";
            if (mode is not ("attack" or "spell" or "both" or "off" or "")) throw new ArgumentException("attack, spell, both or off");
            lock (conn.Monsters)
            {
                conn.ForceAttackBlockLucky = attack;
                conn.ForceSpellBlockLucky = spell;
                conn.Pools = PoolsFor(conn, conn.Pools.Level);
            }
            await SendCharacterStats(remote, stream, ciphers, conn, $"lucky block {mode}");
            await conn.Commands!.Reply($"Lucky block: attack {(attack ? "on" : "off")}, spell {(spell ? "on" : "off")}.");
        })
        .Add("blockhit", "/blockhit <attack|spell> <fire|cold|lightning|chaos|physical> <damage> (dev: hit through block and resistance)", async args =>
        {
            if (args.Length < 3) throw new ArgumentException("attack|spell, a damage type and an amount");
            bool spell = args[0].Equals("spell", StringComparison.OrdinalIgnoreCase);
            if (!spell && !args[0].Equals("attack", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("attack or spell");
            if (ParseDamageType(args[1]) is not { } type) throw new ArgumentException("fire, cold, lightning, chaos or physical");
            if (!int.TryParse(args[2], out int amount) || amount <= 0) throw new ArgumentException("a positive amount");
            if (!TryDevEnemyHit(conn, type, amount, true, spell, false, out var hit))
            {
                await conn.Commands!.Reply(conn.PlayerLife == 0 ? "You are already dead." : "Enemy test hit rejected while paused or leaving.");
                return;
            }
            int dealt = hit.Damage; string mitigation = hit.Mitigation; uint life = hit.Life;
            uint mana = hit.Mana, manaBefore = hit.ManaBefore;
            bool blocked = hit.Blocked; int chance = hit.BlockChance;
            await SendPlayerEnergyShield(remote, stream, ciphers, conn);
            if (life == 0)
                await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.HideMiniLifeBar, 1)]),
                    "0x01b3 player hide_mini_life_bar = 1 (dev block hit)");
            await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, life),
                $"0x01b1 player dev {(spell ? "spell" : "attack")} hit for {dealt} ({(blocked ? "blocked" : mitigation)}), life {life}/{conn.PlayerMaxLife}");
            if (mana != manaBefore)
                await SendPlayerMana(remote, stream, ciphers, conn,
                    $"0x01b1 player mana {mana} ({(blocked ? $"+{conn.Pools.ManaGainedOnBlock} on block" : "no block gain")})");
            if (life == 0)
                await PlayerDied(remote, stream, ciphers, conn, $"0x0055 death screen ({conn.League ?? "?"} league, dev block hit)");
            await conn.Commands!.Reply($"{(spell ? "Spell" : "Attack")} hit: {amount} → {dealt}; {(blocked ? "blocked" : "not blocked")} ({chance}% chance); life {life}/{conn.PlayerMaxLife}.");
        })
        .Add("god", "/god (toggles: 1000% increased attack and cast speed, 100% of life and 10 000 mana regenerated per second, 200% increased area of effect, +10 000 life, +1000 attributes)", async _ =>
        {
            bool on = !conn.God;
            await SetGod(remote, stream, ciphers, conn, on);
            // A burning Righteous Fire takes its radius when lit: light it again with the new area.
            await RelightRighteousFire(remote, stream, ciphers, conn, "/god");
            await conn.Commands!.Reply(on ? "God mode on." : "God mode off.");
        })
        .Add("set_aoe", "/set_aoe <percent> | off (dev: the character's increased area of effect, base_skill_area_of_effect_+%, in place of what passives, items and /god give)", async args =>
        {
            if (args.Length == 0) throw new ArgumentException("percent or off");
            if (Stats.Key("base_skill_area_of_effect_+%") is not uint stat) throw new ArgumentException("no area of effect stat in the data");
            bool off = args[0].Equals("off", StringComparison.OrdinalIgnoreCase);
            int value = off ? 0 : int.Parse(args[0]);
            lock (conn.Monsters) { if (off) conn.PlayerStats.Remove(stat); else conn.PlayerStats[stat] = value; }
            await SendPlayerStatSnapshot(remote, stream, ciphers, conn,
            () => PlayerStatsUpdateAtGate(conn, PlayerStatsWithCleared(conn)),
                $"0x01b3 stats: base_skill_area_of_effect_+% = {(off ? "the character's own" : value)}");
            await SendSkills(remote, stream, ciphers, conn);
            bool relit = await RelightRighteousFire(remote, stream, ciphers, conn, "/set_aoe");
            await conn.Commands!.Reply((off ? "Increased area of effect: the character's own" : $"Increased area of effect: {value}%")
                + (relit ? ", Righteous Fire relit" : ""));
        })
        .Add("heal", "/heal (full life; also revives: 0x1b1 from 0 runs the client's revive)", async _ =>
        {
            lock (conn.Monsters) { (conn.PlayerLife, conn.PlayerMana) = (conn.PlayerMaxLife, (uint)UnreservedMana(conn)); conn.EnergyShield.SetMaximum(conn.Pools.EnergyShield, true); }
            await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, conn.PlayerMaxLife),
                $"0x01b1 player healed to {conn.PlayerMaxLife}");
            await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana}");
            await Send(remote, stream, ciphers, InstanceEntities.StatsUpdate(PlayerId, [(StatKeys.HideMiniLifeBar, 0)]),
                "0x01b3 player hide_mini_life_bar = 0");
        })
        .Add("death_screen", "/death_screen [hex flags] (show the death screen; default: this league's; 2 hides it)", async args =>
        {
            byte flags = args.Length > 0 ? Convert.ToByte(args[0].Replace("0x", ""), 16) : DeathScreenFlags(conn);
            await Send(remote, stream, ciphers, InstanceProtocol.DeathScreen(flags), $"0x0055 death screen flags 0x{flags:x2}");
        })
        .Add("transition", "/transition [pos [here | x y | +dx +dy] | rot <degrees> | z <z | none> | model <path | none> | index <n>] " +
            "(dev: show or change the first area transition; it is re-created)", async args =>
        {
            var (px, py) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            if (conn.Layout.AreaTransitions.Length == 0) { await conn.Commands!.Reply("This area has no area transition."); return; }
            var (id, at) = conn.Layout.AreaTransitions[0];
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string? what = args.Length > 0 ? args[0] : null;
            string[] rest = args.Skip(1).ToArray();
            AreaTransitionObject? changed = what switch
            {
                "pos" when rest.Length > 0 => rest[0] == "here" ? at with { GridX = px, GridY = py }
                    : rest[0].StartsWith('+') || rest[0].StartsWith('-')
                        ? at with { GridX = at.GridX + int.Parse(rest[0]), GridY = at.GridY + int.Parse(rest[1]) }
                        : at with { GridX = int.Parse(rest[0]), GridY = int.Parse(rest[1]) },
                "rot" => at with { Rotation = float.Parse(rest[0], inv) * MathF.PI / 180f },
                "z" => at with { Height = rest[0] == "none" ? null : float.Parse(rest[0], inv) },
                "model" => at with { Model = rest[0] == "none" ? null : string.Join(' ', rest) },
                "index" => at with { Index = int.Parse(rest[0]) },
                _ => null,
            };
            if (changed is not null)
            {
                conn.ReplaceAreaTransition(id, changed);
                at = changed;
                await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove area transition 0x{id:x}");
                await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(at.Type), at.Blob()),
                    $"0x02b9 create area transition {at}");
                await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), $"0x01a9 area transition 0x{id:x} enters wake zone");
            }
            await conn.Commands!.Reply($"Area transition 0x{id:x} at ({at.GridX}, {at.GridY}), {at.Rotation * 180 / MathF.PI:0.#} degrees, " +
                $"height {at.Height?.ToString(inv) ?? "terrain"}, model {at.Model ?? "none"}; you at ({px}, {py}).");
        })
        .Add("quest_flag", "/quest_flag [<QuestFlags Id> [off]] (dev: list, set or clear quest flags, e.g. /quest_flag a1q1-KilledHillock)", async args =>
        {
            if (args.Length < 1) { await conn.Commands!.Reply(string.Join(", ", conn.QuestFlags.DefaultIfEmpty("no quest flags"))); return; }
            bool off = args.Length > 1 && args[1].Equals("off", StringComparison.OrdinalIgnoreCase);
            await SetQuestFlags(remote, stream, ciphers, conn, [args[0]], on: !off);
            await conn.Commands!.Reply($"{args[0]} {(off ? "cleared" : "set")}");
        })
        .Add("delve_chart", "/delve_chart <u32> <u16> [<u16> <u16> <u64> <u8> <u32> ...] (dev: what the next Delve chart (S→C 0x240) carries, to work out its fields)", async args =>
        {
            try
            {
                var n = args.Select(a => a.StartsWith("0x") ? Convert.ToUInt64(a[2..], 16) : ulong.Parse(a)).ToArray();
                var cells = new List<InstanceDelve.Cell>();
                for (int i = 2; i + 4 < n.Length; i += 5) cells.Add(new((ushort)n[i], (ushort)n[i + 1], n[i + 2], (byte)n[i + 3], (uint)n[i + 4]));
                conn.DelveChartProbe = (n.Length > 0 ? (uint)n[0] : 0, n.Length > 1 ? (ushort)n[1] : (ushort)0, cells);
                await conn.Commands!.Reply($"Next Delve chart: {conn.DelveChartProbe.A}, {conn.DelveChartProbe.B}, {cells.Count} cell(s). Open it from Niko.");
            }
            catch (FormatException) { await conn.Commands!.Reply("Numbers only (decimal or 0x…)."); }
        })
        .Add("goto", "/goto <area id or name> (dev: change area as if through an area transition, e.g. /goto 1_1_3)", async args =>
        {
            string target = string.Join(' ', args);
            if (AreaTransitionCatalog.HashOf(target) is not { } hash) { await conn.Commands!.Reply($"Unknown area \"{target}\" (needs data/area-transitions.json)."); return; }
            await ChangeArea(remote, stream, ciphers, conn, new PendingTransfer(hash, 0, false), $"/goto {target}");
        })
        .Add("league_flag", "/league_flag [<id> [off]] (dev: list, set or clear league quest flags by the client's id, e.g. /league_flag 350)", async args =>
        {
            if (args.Length < 1 || !int.TryParse(args[0], out int flag))
            {
                await Send(remote, stream, ciphers, InstanceChat.ChatLine($"League quest flags: {string.Join(", ", conn.LeagueQuestFlags.Order())}"), "0x0017 chat line: league quest flags");
                return;
            }
            bool off = args.Length > 1 && args[1].Equals("off", StringComparison.OrdinalIgnoreCase);
            if (off) conn.LeagueQuestFlags.Remove(flag); else conn.LeagueQuestFlags.Add(flag);
            await Send(remote, stream, ciphers, InstanceEntities.PlayerLeagueQuestFlagsUpdate(PlayerId, Quests.League.Bits(conn.LeagueQuestFlags)),
                $"0x02ba player league quest flags: {string.Join(", ", conn.LeagueQuestFlags.Order())}");
            await Send(remote, stream, ciphers, InstanceChat.ChatLine($"League quest flag {flag} {(off ? "cleared" : "set")}."), $"0x0017 chat line: league quest flag {flag}");
        })
        .Add("hideout_keys", "/hideout_keys <a> <b> (dev: the Player component's two Hideouts.dat keys, HASH16 hex or a hideout area id)", async args =>
        {
            ushort Key(string s) => HideoutCatalog.Hideouts.FirstOrDefault(h => h.AreaId.Equals(s, StringComparison.OrdinalIgnoreCase)) is { } h ? h.Hash16
                : ushort.TryParse(s.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out var k) ? k : (ushort)0;
            ushort a = args.Length > 0 ? Key(args[0]) : (ushort)0, b = args.Length > 1 ? Key(args[1]) : (ushort)0;
            await Send(remote, stream, ciphers, InstanceEntities.PlayerHideoutsUpdate(PlayerId, a, b), $"0x02ba player hideout keys 0x{a:x4} 0x{b:x4}");
            await Send(remote, stream, ciphers, InstanceChat.ChatLine($"Hideout keys 0x{a:x4} 0x{b:x4}."), "0x0017 chat line: hideout keys");
        })
        .Add("waypoints", "/waypoints [all | reset] (list the activated waypoints; dev: activate all and visit every pinned area, or keep only the towns')", async args =>
        {
            var entries = WaypointCatalog.Areas.Entries;
            if (entries.Count == 0) { await conn.Commands!.Reply("No waypoints (needs data/tables/WorldAreas.json)."); return; }
            string what = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (what is "all" or "reset")
            {
                string[] all;
                string[] visited;
                lock (conn.Waypoints)
                {
                    conn.Waypoints.Clear();
                    conn.Waypoints.UnionWith(entries.Where(e => what == "all" || e.IsTown).Select(e => e.Id));
                    all = [.. conn.Waypoints];
                    // "all" also visits every area with a World panel pin, so every pin and line shows.
                    if (what == "all") conn.Visited.UnionWith(WaypointCatalog.Visits.PinnedAreas);
                    else { conn.Visited.Clear(); conn.Visited.Add(conn.Layout.Id); }
                    visited = [.. conn.Visited];
                }
                CharacterSaves.SetWaypoints(CharacterName(conn), all);
                CharacterSaves.SetVisited(CharacterName(conn), visited);
                await SendWaypointList(remote, stream, ciphers, conn, $"/waypoints {what}");
            }
            string list;
            lock (conn.Waypoints) list = string.Join(", ", entries.Where(e => conn.Waypoints.Contains(e.Id)).Select(e => e.Name));
            await conn.Commands!.Reply(list.Length == 0 ? "No activated waypoints." : $"Activated waypoints: {list}.");
        })
        .Add("npc", "/npc <NPCs row, Id or name> [rotation°] (spawns an NPC next to you)", async args =>
        {
            if (args.Length == 0) throw new ArgumentException("which NPC");
            if (Npcs.Value.Error is { } error) { await conn.Commands!.Reply(error); return; }
            float? deg = args.Length > 1 && float.TryParse(args[^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float d) ? d : null;
            string query = string.Join(' ', deg is null ? args : args[..^1]);
            if (Npcs.Value.Find(query) is not { } npc) { await conn.Commands!.Reply($"No NPC {query}."); return; }
            if (!npc.Supported) { await conn.Commands!.Reply($"{npc} has components the server cannot write: {string.Join(", ", npc.Components ?? [])}."); return; }
            var (x, y) = NextToPlayer(conn);
            await SpawnNpc(remote, stream, ciphers, conn, npc, x, y, (deg ?? 0) * MathF.PI / 180);
        })
        // Turns an NPC: it is created again with the new facing (0x2bb, then 0x2b9).
        .Add("npc_face", "/npc_face <name> <degrees> (turn an NPC here)", async args =>
        {
            if (args.Length < 2) throw new ArgumentException("name and degrees");
            float deg = float.Parse(args[^1], System.Globalization.CultureInfo.InvariantCulture);
            string name = string.Join(' ', args[..^1]);
            KeyValuePair<uint, SpawnedNpc> found;
            lock (conn.Npcs) found = conn.Npcs.FirstOrDefault(n => n.Value.Npc.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (found.Value is not { } npc) { await conn.Commands!.Reply($"No NPC {name} here."); return; }
            lock (conn.Npcs) conn.Npcs.Remove(found.Key);
            conn.RemoveBlocker(found.Key);
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(found.Key), $"0x02bb remove npc 0x{found.Key:x} {npc.Npc.Name}");
            float rad = deg * MathF.PI / 180;
            await SpawnNpc(remote, stream, ciphers, conn, npc.Npc, npc.X, npc.Y, rad);
            await conn.Commands!.Reply($"{npc.Npc.Name} at ({npc.X}, {npc.Y}) faces {deg}° ({rad:0.###} rad).");
        })
        // Probe: S→C 0x7b (open a UI) with any kind and its four u32s (16-waypoints.md §3; kind 0 = World panel).
        .Add("ui", "/ui <kind hex> [a] [b] [c] [d] (S→C 0x7b open a UI)", async args =>
        {
            if (args.Length == 0) throw new ArgumentException("which kind");
            byte kind = Convert.ToByte(args[0].Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16);
            uint U(int i) => args.Length > i ? Convert.ToUInt32(args[i], args[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10) : 0;
            await Send(remote, stream, ciphers, InstanceWaypoints.OpenUi(kind, U(1), U(2), U(3), U(4)), $"0x007b open UI kind 0x{kind:x2} ({U(1)}, {U(2)}, {U(3)}, {U(4)})");
        })
        .Add("npcs", "/npcs (list NPCs here)", async _ =>
        {
            string list;
            lock (conn.Npcs) list = string.Join(", ", conn.Npcs.Select(n => $"0x{n.Key:x} {n.Value.Npc.Name} ({n.Value.X}, {n.Value.Y})"));
            await conn.Commands!.Reply(list.Length == 0 ? "No NPCs." : list);
        })
        .Add("pos", "/pos (your cell)", async _ =>
        {
            var (x, y) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            Console.WriteLine($"[gateway] {remote} /pos ({x}, {y}) in {conn.Layout.Id}");
            await conn.Commands!.Reply($"({x}, {y}) in {conn.Layout.Id}");
        })
        .Add("tp", "/tp <x> <y> | <+dx> <+dy> | <id> | <name> [n] | list [filter] (dev: teleport to a cell, a monster, NPC, transition, waypoint or captured object, e.g. /tp RhoaChest 2)",
            args => DevTeleport(remote, stream, ciphers, conn, args))
        .Add("mobs", "/mobs (list spawned monsters)", async _ =>
        {
            // Copied under the lock: the AI and the emerge watch change both lists.
            List<string> listed;
            lock (conn.Monsters)
            {
                var buried = MonsterEmerge.Buried(conn);
                var waiting = new HashSet<SpawnedMonster>(MonsterEmerge.Unrisen(conn), ReferenceEqualityComparer.Instance);
                double now = conn.Clock.Elapsed.TotalSeconds;
                listed = [.. conn.Monsters.Concat(buried).Select(m =>
                    $"0x{m.Key:x} {m.Value.Label} {m.Value.Walk.Position(now)}" +
                    (waiting.Contains(m.Value) ? buried.ContainsKey(m.Key) ? " (buried)" : " (lurking)" : m.Value.Aggro ? " (chasing)" : ""))];
            }
            await conn.Commands!.Reply(listed.Count == 0 ? "No monsters." : string.Join(", ", listed));
        })
        .Add("despawn", "/despawn [all | <id>] (default: the last one spawned)", async args =>
        {
            List<uint> ids;
            lock (conn.Monsters)
                ids = args.Length == 0 ? [.. conn.Monsters.Keys.Concat(MonsterEmerge.Buried(conn).Keys).OrderBy(k => k).TakeLast(1)]
                    : args[0].Equals("all", StringComparison.OrdinalIgnoreCase) ? [.. conn.Monsters.Keys, .. MonsterEmerge.Buried(conn).Keys]
                    : [Convert.ToUInt32(args[0], args[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10)];
            foreach (uint id in ids)
            {
                bool removed;
                lock (conn.Monsters) removed = conn.Monsters.Remove(id) || MonsterEmerge.Buried(conn).Remove(id);
                if (!removed) { await conn.Commands!.Reply($"No monster 0x{id:x}."); continue; }
                await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove monster 0x{id:x}");
            }
        })
        // Movement probes: walk (0x1ba, Positioned move with a speed) or teleport (0x1b7) a monster to a few cells from the player.
        .Add("mob_move", "/mob_move [id] [speed] (0x1ba toward you; default the last monster, speed 20)", async args =>
        {
            if (MonsterArg(conn, args, 0) is not { } id) { await conn.Commands!.Reply("No such monster."); return; }
            string? sp = args.FirstOrDefault(a => !a.StartsWith("0x", StringComparison.OrdinalIgnoreCase));
            float speed = sp is null ? 20f : float.Parse(sp, System.Globalization.CultureInfo.InvariantCulture);
            var (x, y) = NextToPlayer(conn);
            await Send(remote, stream, ciphers, InstanceProtocol.MoveTo(id, x, y, speed), $"0x01ba move monster 0x{id:x} to ({x}, {y}) speed {speed}");
            lock (conn.Monsters)
            {
                conn.Monsters[id].Walk = conn.NewMonsterWalk(id, x, y, conn.Monsters[id].Walk.Speed,
                    LivingCollision.Size(conn.Monsters[id]), conn.Monsters[id].Rarity == MonsterRarity.Unique);
                conn.Monsters[id].CollisionPosition = (x, y);
            }
        })
        // Action probe: start the monster's Move action (0x1ab) toward the player, like the echo of a player's click. The skill
        // id is the player's Move (0x2909) unless given (hex); sequence 0xffff (the monster is not client-predicted).
        .Add("mob_walk", "/mob_walk [id] [skill hex] (0x1ab Move toward you)", async args =>
        {
            if (MonsterArg(conn, args, 0) is not { } id) { await conn.Commands!.Reply("No such monster."); return; }
            string? sk = args.FirstOrDefault(a => !a.StartsWith("0x", StringComparison.OrdinalIgnoreCase));
            ushort skill = sk is null ? (ushort)0x2909 : Convert.ToUInt16(sk, 16);
            var (x, y) = NextToPlayer(conn);
            (int X, int Y) from;
            lock (conn.Monsters) from = conn.Monsters[id].Walk.Order(x, y, conn.Clock.Elapsed.TotalSeconds);
            await Send(remote, stream, ciphers, InstanceProtocol.ActionStarted(id, from.X, from.Y, x, y, skill, 0xffff, 0x0408),
                $"0x01ab monster 0x{id:x} action skill 0x{skill:x4} from {from} to ({x}, {y})");
        })
        // Animation probe: play animation index `id` on the nearest living monster (0x1b2 kind 0). The Animated handler
        // bounds-checks the index against the actor's animation count, so an out-of-range value is ignored.
        .Add("anim", "/anim <index> [speed] (0x1b2 kind 0: play an animation on the nearest monster)", async args =>
        {
            if (args.Length < 1 || !uint.TryParse(args[0], out uint index)) throw new ArgumentException("an animation index");
            float speed = args.Length > 1 ? float.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 1f;
            var here = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
            uint? pick;
            lock (conn.Monsters)
                pick = conn.Monsters.Where(kv => kv.Value.Life > 0)
                    .OrderBy(kv => Dist(here, kv.Value.Walk.Position(conn.Clock.Elapsed.TotalSeconds)))
                    .Select(kv => (uint?)kv.Key).FirstOrDefault();
            if (pick is not { } id) { await conn.Commands!.Reply("No living monster nearby."); return; }
            await Send(remote, stream, ciphers, InstanceProtocol.AnimationUpdate(id, 0, index, speed),
                $"0x01b2 monster 0x{id:x} play animation {index} at speed {speed}");
            await conn.Commands!.Reply($"0x1b2 anim {index} (speed {speed}) -> monster 0x{id:x}");
        })
        .Add("mob_mods", "/mob_mods [id] [mod …|pool] (a monster's mods; with mods: create it again with those affixes, e.g. /mob_mods Quick of_Flames)",
            args => MonsterModsCommand(remote, stream, ciphers, conn, args))
        .Add("mob_tp", "/mob_tp [id] (0x1b7 next to you; default the last monster)", async args =>
        {
            if (MonsterArg(conn, args, 0) is not { } id) { await conn.Commands!.Reply("No such monster."); return; }
            var (x, y) = NextToPlayer(conn);
            await Send(remote, stream, ciphers, InstanceProtocol.Teleport(id, x, y), $"0x01b7 teleport monster 0x{id:x} to ({x}, {y})");
            lock (conn.Monsters)
            {
                conn.Monsters[id].Walk = conn.NewMonsterWalk(id, x, y, conn.Monsters[id].Walk.Speed,
                    LivingCollision.Size(conn.Monsters[id]), conn.Monsters[id].Rarity == MonsterRarity.Unique);
                conn.Monsters[id].CollisionPosition = (x, y);
            }
        })
        .Add("ai", "/ai [on|off] (monsters chase you when you come near; emerge monsters rise either way)", async args =>
        {
            if (args.Length > 0) conn.AiEnabled = !args[0].Equals("off", StringComparison.OrdinalIgnoreCase);
            await conn.Commands!.Reply($"Monster AI is {(conn.AiEnabled ? "on" : "off")}.");
        });
    }
}
