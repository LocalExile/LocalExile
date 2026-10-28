using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;

partial class Program
{
    // C→S 0x28: a click on an item. It moves to the cursor inventory (row 12) in one save (PlayerInventories.PickUp): one 0xbf
    // removes it from its inventory, another adds it to the cursor at (0, 0). Refused (logged) when the cursor already holds an
    // item or the item is not there.
    static async Task PickUpItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceItems.PickUpItem pick)
    {
        if (conn.Sale is { } sale && pick.InventoryId == sale.Mine) { await TakeFromSale(remote, stream, ciphers, conn, sale, pick.ItemId); return; }
        if (StashOf(conn).TabOf(pick.InventoryId) is int tab) { await PickUpFromStash(remote, stream, ciphers, conn, tab, pick); return; }
        var items = ItemsOf(conn);
        if (PlayerInventories.Row(pick.InventoryId) is not byte row || items.PickUp(row, pick.ItemId) is not { } held)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0028 pick up {pick}: refused");
            return;
        }
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(pick.InventoryId, items.NextRevision(row), [held.Id], []),
            $"0x00bf inventory {pick.InventoryId}: remove item {held.Id} (picked up {pick})");
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [], [held]), $"0x00bf cursor: add item {held.Id}");
        // Dual wielding: taking the main hand off moves the off hand's one-handed weapon into the main hand.
        if (items.PromoteOffhand(row, ShapeOf) is { } promoted)
        {
            byte off = (byte)(row + 1);
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(off), items.NextRevision(off), [promoted.Id], []),
                $"0x00bf inventory {PlayerInventories.InventoryId(off)}: remove item {promoted.Id} (to the main hand)");
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), [], [promoted]),
                $"0x00bf inventory {PlayerInventories.InventoryId(row)}: add item {promoted.Id} (from the off hand)");
        }
        if (PlayerInventories.ShowsOnModel(row)) await SendEquipment(remote, stream, ciphers, conn, items);
        if (row == PlayerInventories.Flask) await RefreshMagebloodFlasks(remote, stream, ciphers, conn);
    }

    // C→S 0x30: the held gem goes into a socket. The cursor loses it (and holds the gem that was in the socket, if any); the
    // item is replaced in its inventory (0xbf removing and adding the same id). Gems in equipment grant skills.
    static async Task SocketGem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceItems.SocketGem s)
    {
        var items = ItemsOf(conn);
        if (items.Items(PlayerInventories.Cursor) is [var held, ..] && GemRequirementsUnmet(conn, held.Item) is { } missing)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0030 socket {s}: refused ({missing})");
            return;
        }
        string why = "unknown inventory";
        var done = PlayerInventories.Row(s.InventoryId) is byte row ? items.Socket(row, s.ItemId, (int)s.Socket, ShapeOf, items.NewItemId, out why) : null;
        if (done is not var (item, before, now) || PlayerInventories.Row(s.InventoryId) is not byte r)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0030 socket {s}: refused ({why})");
            return;
        }
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [before.Id], now is null ? [] : [now]),
            $"0x00bf cursor: remove gem {before.Id}" + (now is null ? "" : $", hold gem {now.Id} from the socket"));
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(s.InventoryId, items.NextRevision(r), [item.Id], [item]),
            $"0x00bf inventory {s.InventoryId}: item {item.Id} with a gem in socket {s.Socket}");
        if (PlayerInventories.ShowsOnModel(r)) await SendEquipment(remote, stream, ciphers, conn, items);
    }

    // C→S 0x31: the "+" on a socketed gem. The client derives the level from the gem's experience on read (0x14246f900), so
    // the server writes the next level's threshold and replaces the item; the client then derives the level and its skill.
    static async Task LevelUpGem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceItems.SocketGem s)
    {
        var items = ItemsOf(conn);
        if (PlayerInventories.Row(s.InventoryId) is not byte row) return;
        if (items.Items(row).FirstOrDefault(i => i.Id == s.ItemId) is not { Item: { } worn } ||
            s.Socket >= worn.Sockets.Count || worn.Sockets[(int)s.Socket].Gem is not { } gem) return;
        if (ItemsFor(conn).ByHash(gem.TypeHash) is not { } baseType || ItemsFor(conn).Gem(baseType) is not { } type) return;
        if ((gem.GemFlags & 4) != 0) { await conn.Commands!.Reply($"{ItemsFor(conn).GemName(gem)}'s level is locked"); return; }
        int level = ItemsFor(conn).GemLevel(gem);
        // The character's level can fall below the next level's requirement after the experience was gained (/set_level, or a
        // save from before the stall): the "+" is still there, so say what is missing rather than that the gem is at its last
        // level. The next level is only computed while the gem is under its type's cap (ExperienceFor clamps).
        if (level < type.MaxLevel && GemRequirementsUnmet(conn, gem with { GemExperience = type.ExperienceFor(level + 1) }) is { } missing)
        {
            await conn.Commands!.Reply(missing);
            return;
        }
        int max = Math.Min(SkillsFor(conn).MaxLevelAt(type.Variant(gem.GemVariant).Effect, conn.Pools.Level), type.MaxLevel);
        if (level >= max) { await conn.Commands!.Reply($"{ItemsFor(conn).GemName(gem)} is at its highest level ({max})"); return; }
        var next = gem with { GemExperience = type.ExperienceFor(level + 1) };
        var after = items.Change(row, s.ItemId, it =>
        {
            var sockets = it.Sockets.ToList();
            sockets[(int)s.Socket] = sockets[(int)s.Socket] with { Gem = next };
            return it with { Sockets = sockets };
        });
        if (after is null) return;
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(s.InventoryId, items.NextRevision(row), [after.Id], [after]),
            $"0x00bf inventory {s.InventoryId}: item {after.Id} socket {s.Socket} gem to level {level + 1}");
        await SendSkills(remote, stream, ciphers, conn);
        Console.WriteLine($"[gateway] {remote} {CharacterName(conn)}'s {ItemsFor(conn).GemName(next)} levels to {level + 1}");
    }

    // C→S 0xd7: a right-click on a socketed gem toggles its level lock (SkillGem +0x10 bit 2); the item is replaced with the
    // new flag so the client shows the lock again after a login.
    static async Task LockGem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceItems.LockGem s)
    {
        var items = ItemsOf(conn);
        if (PlayerInventories.Row(s.InventoryId) is not byte row) return;
        if (items.Items(row).FirstOrDefault(i => i.Id == s.ItemId) is not { Item: { } worn } ||
            s.Socket >= worn.Sockets.Count || worn.Sockets[(int)s.Socket].Gem is not { } gem) return;
        byte flags = (byte)((gem.GemFlags & ~4) | (s.Locked ? 4 : 0));
        var after = items.Change(row, s.ItemId, it =>
        {
            var sockets = it.Sockets.ToList();
            sockets[(int)s.Socket] = sockets[(int)s.Socket] with { Gem = gem with { GemFlags = flags } };
            return it with { Sockets = sockets };
        });
        if (after is null) return;
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(s.InventoryId, items.NextRevision(row), [after.Id], [after]),
            $"0x00bf inventory {s.InventoryId}: item {after.Id} socket {s.Socket} gem {(s.Locked ? "locked" : "unlocked")}");
        Console.WriteLine($"[gateway] {remote} {CharacterName(conn)}'s {ItemsFor(conn).GemName(gem)} in item {after.Id} socket {s.Socket}: {(s.Locked ? "level locked" : "level unlocked")}");
    }

    // C→S 0x2f: a socketed gem goes onto the cursor; the item is replaced in its inventory.
    static async Task UnsocketGem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceItems.SocketGem s)
    {
        var items = ItemsOf(conn);
        string why = "unknown inventory";
        var done = PlayerInventories.Row(s.InventoryId) is byte row ? items.Unsocket(row, s.ItemId, (int)s.Socket, items.NewItemId, out why) : null;
        if (done is not var (item, held) || PlayerInventories.Row(s.InventoryId) is not byte r)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x002f unsocket {s}: refused ({why})");
            return;
        }
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(s.InventoryId, items.NextRevision(r), [item.Id], [item]),
            $"0x00bf inventory {s.InventoryId}: item {item.Id} without the gem in socket {s.Socket}");
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [], [held]), $"0x00bf cursor: hold gem {held.Id}");
        if (PlayerInventories.ShowsOnModel(r)) await SendEquipment(remote, stream, ciphers, conn, items);
    }

    // C→S 0x16 /destroy (the town popup's Destroy): the held item is gone.
    static async Task DestroyHeldItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (await TakeHeld(remote, stream, ciphers, ItemsOf(conn), "destroyed") is null)
            Console.WriteLine($"[gateway] {remote} <= 0x0016 /destroy: nothing held");
    }

    // The instance's items on the ground, where they were left (InstanceStore): dropped or looted on an earlier connection.
    static async Task SendGroundItems(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        foreach (var (id, ground) in conn.Instance.Ground.All())
        {
            await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob()),
                $"0x02b9 create world item 0x{id:x} left at ({ground.GridX}, {ground.GridY})");
            await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
        }
    }

    // C→S 0x70: a flask key. The flask in that cell of the flask bar spends its charges (S→C 0xbf with the changed item) and
    // its life or mana comes back, instantly or over its recovery time, under the flask_effect_life / _mana buff
    // (13-items.md §8): its quality, its mods and the character's flask stats count (FlaskCharges, FlaskRecovery). Without
    // enough charges, while dead, or when its recovery pools are full, nothing happens. A utility flask goes to UseUtilityFlask.
    static async Task UseFlask(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint slot)
    {
        var items = ItemsOf(conn);
        var flask = items.Items(PlayerInventories.Flask).FirstOrDefault(i => i.X == slot);
        if (flask is not null && ItemsFor(conn).ByHash(flask.Item.TypeHash) is { } utilityType && ItemsFor(conn).UtilityFlaskOf(utilityType) is { } utility)
        {
            await UseUtilityFlask(remote, stream, ciphers, conn, slot, flask, utilityType, utility);
            return;
        }
        if (flask is null || ItemsFor(conn).ByHash(flask.Item.TypeHash) is not { } type || ItemsFor(conn).FlaskOf(type) is not { } use)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0070 use flask {slot}: {(flask is null ? "no flask there" : "not a flask (not handled)")}");
            return;
        }
        var character = CharacterPlayerStats(conn);
        int Get(string name) => Stats.Key(name) is uint key ? character.GetValueOrDefault(key) : 0;
        var local = ItemModStats.Local(flask.Item, ItemsFor(conn).Mods);
        var charges = FlaskCharges.Of(use, local, Get);
        if (conn.PlayerLife == 0 || Flasks.Spend(flask.Item, charges) is null)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0070 use flask {slot} ({type.Name}): {(conn.PlayerLife == 0 ? "dead" : $"{flask.Item.Charges}/{charges.MaxCharges} charges, a use needs {charges.ChargesPerUse}")}");
            return;
        }
        bool canRecover;
        lock (conn.Monsters)
            canRecover = Flasks.CanRecover(use, conn.PlayerLife, conn.PlayerMaxLife, conn.PlayerMana, (uint)UnreservedMana(conn));
        if (!canRecover)
        {
            await Send(remote, stream, ciphers, InstanceItems.ItemUsed(flask.Item.TypeHash,
                PlayerInventories.InventoryId(PlayerInventories.Flask), flask.Id, result: 0x4e2),
                $"0x0263 flask {slot} ({type.Name}) refused: recovery pools full (0x4e2), charges unchanged");
            // The client can hold a lower pool than the server (a use it started but the server did not pay for): the
            // refusal carries the authoritative value, so the orb and the next press agree.
            if (use.LifePerUse > 0)
            {
                uint life;
                lock (conn.Monsters) life = conn.PlayerLife;
                await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, life),
                    $"0x01b1 player life {life} (full pool, flask refused)");
            }
            if (use.ManaPerUse > 0)
                await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {conn.PlayerMana} (full pool, flask refused)");
            return;
        }
        if (await SpendFlask(remote, stream, ciphers, conn, flask, charges, slot) is not { } spent) return;
        await Send(remote, stream, ciphers, InstanceItems.ItemUsed(spent.Item.TypeHash,
            PlayerInventories.InventoryId(PlayerInventories.Flask), spent.Id), $"0x0263 flask {slot} used (native use sound)");
        var r = FlaskRecovery.For(use, flask.Item, local, Get, conn.PlayerLife, conn.PlayerMaxLife);
        Console.WriteLine($"[gateway] {remote} <= 0x0070 use flask {slot} ({type.Name}): {r.Life:0.#} life, {r.Mana:0.#} mana over {r.Seconds:0.##} s, " +
            $"instantly {r.InstantLife:0.#} life, {r.InstantMana:0.#} mana{(r.ManaAtEnd > 0 ? $", {r.ManaAtEnd:0.#} mana at the end" : "")}" +
            $"{(r.RemovesLife + r.RemovesMana > 0 ? $", removes {r.RemovesLife:0.#} life, {r.RemovesMana:0.#} mana" : "")}" +
            $"{(r.ExtraLife > 0 ? $", {r.ExtraLife:0.#} more life over {FlaskRecovery.ExtraLifeSeconds} s" : "")}");
        // The instant part and what the flask takes from the other pool (never below 1 life), at once. A monster may have
        // killed the player while the charges went out: then nothing (FlaskRecovery.Instant), or the death screen would stay
        // with 1 life.
        uint lifeBefore, manaBefore, lifeAfter, manaAfter;
        lock (conn.Monsters)
        {
            (lifeBefore, manaBefore) = (conn.PlayerLife, conn.PlayerMana);
            if (FlaskRecovery.Instant(r, lifeBefore, manaBefore, conn.PlayerMaxLife, UnreservedMana(conn)) is not var (l, m))
            {
                Console.WriteLine($"[gateway] {remote} flask {slot} ({type.Name}): the player died meanwhile, no recovery");
                return;
            }
            (conn.PlayerLife, conn.PlayerMana) = (lifeAfter, manaAfter) = (l, m);
        }
        if (lifeAfter != lifeBefore) await Send(remote, stream, ciphers, InstanceProtocol.LifeUpdate(PlayerId, lifeAfter), $"0x01b1 player life {lifeAfter} (flask)");
        if (manaAfter != manaBefore) await SendPlayerMana(remote, stream, ciphers, conn, $"0x01b1 player mana {manaAfter} (flask)");
        double now = conn.Clock.Elapsed.TotalSeconds;
        if (r.ExtraLife > 0)
            conn.FlaskEffects.Start(new Flasks.Effect((byte)slot, 0, 0, r.ExtraLife / FlaskRecovery.ExtraLifeSeconds, 0, now, now + FlaskRecovery.ExtraLifeSeconds));
        if (r.Seconds <= 0 || r.Life + r.Mana + r.ManaAtEnd <= 0) return;
        ushort buff;
        lock (conn.Monsters) buff = conn.NextBuff++;
        ushort row = use.LifePerUse > 0 ? FlaskEffectLifeBuffRow : FlaskEffectManaBuffRow;
        var effect = new Flasks.Effect((byte)slot, buff, row, r.Life / r.Seconds, r.Mana / r.Seconds, now, now + r.Seconds,
            r.ManaAtEnd, r.LifeStaysAtFull, r.ManaStaysAtFull, r.NoQueue);
        conn.FlaskEffects.Start(effect);   // recovers at once or waits in its queue (Flasks.Effects)
        // Both rows are IsRecovery: 0x1c1 carries one u32 per GrantedStats entry, here base_life (or mana)
        // _recovery_per_minute_from_flask. Without it the client misread the message and showed no life change after it
        // (live 2026-10-20).
        uint perMinute = (uint)Math.Round((use.LifePerUse > 0 ? r.Life : r.Mana) * 60 / r.Seconds);
        // The duration (+0x68, f32 seconds) and the flask-bar cell (source skill): the timer bar under the flask (Flasks.Buff).
        // Game_FLASK_BUFF_DURATION=0 sends no duration (before 2026-10-25 a u32 in milliseconds went there, which the client
        // read as a float of almost 0 s).
        bool withDuration = Environment.GetEnvironmentVariable("Game_FLASK_BUFF_DURATION") != "0";
        if (FlaskBuffs)
            await Send(remote, stream, ciphers, Flasks.Buff(PlayerId, buff, row, (byte)slot, withDuration ? r.Seconds : null, [perMinute]),
                $"0x01c1 player buff {buff}: {(row == FlaskEffectLifeBuffRow ? "flask_effect_life" : "flask_effect_mana")} (BuffDefinitions row {row}), {perMinute} per minute, {(withDuration ? $"{r.Seconds:0.##} s" : "no duration")}");
    }

    // Gives each life, mana and utility flask in the flask bar up to `charges` charges (int.MaxValue: full) and saves them (or, for
    // the refills of kills and the like, lets them wait for the next save: PlayerInventories.ChangeUnsaved): the
    // flasks that changed.
    static List<InventoryItem> FillFlasks(PlayerInventories items, int charges, bool saveNow = true, ItemCatalog? catalog = null) =>
        Flasks.Fill(items, catalog ?? Items, charges, saveNow);

    // FillFlasks, and the changed flasks to the client (one S→C 0xbf).
    static Task GainFlaskCharges(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, int charges, string why) =>
        ChangeFlaskBar(remote, stream, ciphers, conn, items => Flasks.Fill(items, ItemsFor(conn), charges, saveNow: false,
                canGain: flask => CanGainFlaskCharges(conn, flask)) is { Count: > 0 } changed
                ? ([.. changed.Select(c => c.Id)], changed) : null,
            changed => $"0x00bf flasks: {string.Join(", ", changed.Select(c => $"{c.X}: {c.Item.Charges}"))} charges ({why})");

    // A new character's class weapon lies on the ground a few cells from where it appears, as on the real game's Twilight
    // Strand, until it is picked up (PlayerInventories.StartWeapon): put once into each instance it enters outside towns.
    static async Task PlaceStartWeapon(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (ItemsOf(conn).StartWeapon is not { } weapon || WaypointCatalog.Areas.Find(conn.Layout.Hash) is { IsTown: true }) return;
        var (x, y) = conn.Layout.Near((conn.Layout.SpawnX, conn.Layout.SpawnY));
        var ground = new WorldItemObject(x, y, weapon);
        // Put here on an earlier connection: SendGroundItems has sent it already, if it still lies here.
        if (conn.Instance.PlaceStartWeapon(CharacterName(conn), ground) is not { } id) return;
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob()),
            $"0x02b9 create world item 0x{id:x} start weapon {ItemsFor(conn).ByHash(weapon.TypeHash)?.Name} at ({x}, {y}), spawn ({conn.Layout.SpawnX}, {conn.Layout.SpawnY})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), null);
    }

    // C→S 0x29: drop the held item where the player stands, as a WorldItem object (0x2b9 + wake zone 0x1a9).
    static async Task DropItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn)
    {
        if (await TakeHeld(remote, stream, ciphers, ItemsOf(conn), "dropped") is not { } held)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0029 drop: nothing held");
            return;
        }
        var (x, y) = conn.Walk.Position(conn.Clock.Elapsed.TotalSeconds);
        var ground = new WorldItemObject(x, y, held.Item);
        uint id = conn.Instance.Ground.Add(ground);
        await Send(remote, stream, ciphers, InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), ground.Blob()),
            $"0x02b9 create world item 0x{id:x} at ({x}, {y})");
        await Send(remote, stream, ciphers, InstanceProtocol.EnterWakeZone(id), $"0x01a9 world item 0x{id:x} enters wake zone");
    }

    // C→S 0x24 on a world item: walk there, then put it in the main inventory (first free cell) and remove the object (0x2bb).
    static async Task PickUpGroundItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, WorldItemObject ground,
        InstanceTransfer.UseSkillOnObject use, double requestedAt)
    {
        // a1q4: a glyph is not needed once the glyphs are placed (a copy left in another instance): it goes, unpicked.
        if (GlyphIndex(ground.Item) is not null && ClientHasQuestFlag(conn, GlyphQuest.PlacedGlyphs))
        {
            bool removed;
            lock (conn.Monsters)
            {
                if (HardcoreProgressBlocked(conn)) return;
                removed = conn.Instance.Ground.Take(id) is not null;
            }
            if (removed)
                await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove glyph 0x{id:x}: the glyphs are placed");
            return;
        }
        if (!TryBeginObjectWalk(conn, requestedAt, ground.GridX, ground.GridY, PickupReach, out var objectWalk, centreRange: true)) return;
        var (action, now, here, stop, arrive) = objectWalk;
        Console.WriteLine($"[gateway] {remote} <= 0x0024 pick up world item 0x{id:x} at ({ground.GridX}, {ground.GridY}) {use} from ({here.X}, {here.Y}): reached at ({stop.X}, {stop.Y}) in {arrive - now:0.00} s");
        // Echo the interaction at once, from where the player is now (as a move's echo does): the client then walks to its
        // own pickup range itself. An echo sent on arrival carried the server's estimated stop, and the client snapped to it.
        await Send(remote, stream, ciphers,
            InstanceProtocol.ActionStarted(PlayerId, here.X, here.Y, ground.GridX, ground.GridY, use.SkillId, use.Sequence, use.Flags, targetObjectId: id),
            $"0x01ab player picks up world item 0x{id:x}", still: () => PlayerActionCurrent(conn, action));
        if (arrive > now) await Task.Delay(TimeSpan.FromSeconds(arrive - now));
        if (!PlayerActionCurrent(conn, action)) return;
        if (IsGold(ground))
        {
            await PickUpGold(remote, stream, ciphers, conn, id, ground, "picked up");
            return;
        }
        var items = ItemsOf(conn);
        var shape = ShapeOf(ground.Item) ?? new(1, 1, "");
        // With the inventory panel open the item goes to the cursor (when it is free), otherwise to the first free cell.
        bool toCursor = (use.Flags & InstanceTransfer.InventoryOpenFlag) != 0 && items.Items(PlayerInventories.Cursor).Count == 0;
        byte row = toCursor ? PlayerInventories.Cursor : PlayerInventories.MainInventory;
        if (!toCursor && shape.MaxStack > 1)
        {
            // A stack goes onto the inventory's stacks of its type first, then into free cells; all of it or nothing. Nothing
            // awaits between the check, the take and the add, and only this connection changes the character's items.
            PlayerInventories.Stacking stacked;
            WorldItemObject? rest = null;
            uint restId = 0;
            lock (conn.Monsters)
            {
                if (HardcoreProgressBlocked(conn)) return;
                if (!items.Fits(PlayerInventories.MainInventory, ground.Item, ShapeOf))
                {
                    Console.WriteLine($"[gateway] {remote} world item 0x{id:x}: no room in the inventory for {ground.Item.StackSize}");
                    return;
                }
                if (conn.Instance.Ground.Take(id) is null) return;   // already picked up
                stacked = KeepOnGroundIfNotSaved(conn, ground, () => items.AddStacked(PlayerInventories.MainInventory, ground.Item, ShapeOf));
                if (stacked.Left > 0)
                {
                    // Fits said yes, so this is not expected; the rest goes back on the ground rather than vanish.
                    rest = new WorldItemObject(ground.GridX, ground.GridY, ground.Item with { StackSize = (ushort)stacked.Left });
                    restId = conn.Instance.Ground.Add(rest);
                }
            }
            if (rest is not null)
            {
                Console.WriteLine($"[gateway] {remote} world item 0x{id:x}: {stacked.Left} didn't fit, back on the ground as 0x{restId:x}");
                await Send(remote, stream, ciphers, InstanceEntities.CreateObject(restId, InstanceEntities.ObjectTypeHash(WorldItemObject.MetadataPath), rest.Blob()),
                    $"0x02b9 create world item 0x{restId:x} (the rest of 0x{id:x})");
            }
            await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove world item 0x{id:x} (picked up from ({stop.X}, {stop.Y}))");
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), stacked.RemovedIds, stacked.AddedItems),
                $"0x00bf inventory 1: {ground.Item.StackSize} picked up ({stacked.Changed.Count} stack(s) grown, {stacked.Added.Count} new)");
            return;
        }
        InventoryItem? placed;
        int? glyph;
        (byte X, byte Y) cell = (0, 0);
        lock (conn.Monsters)
        {
            if (HardcoreProgressBlocked(conn)) return;
            if (!toCursor)
            {
                if (items.FreeCell(PlayerInventories.MainInventory, shape.Width, shape.Height, ShapeOf) is not { } free)
                {
                    Console.WriteLine($"[gateway] {remote} world item 0x{id:x}: no room in the inventory");
                    return;
                }
                cell = free;
            }
            if (conn.Instance.Ground.Take(id) is null) return;   // already picked up
            // The start weapon stops waiting; the Put below saves both changes at once.
            if (conn.Instance.IsStartWeapon(CharacterName(conn), id) && items.ClaimStartWeapon())
                Console.WriteLine($"[gateway] {remote} start weapon picked up");

            // A glyph (a1q4) sets its quest flags with the pickup, in the same save.
            glyph = GlyphIndex(ground.Item);
            placed = KeepOnGroundIfNotSaved(conn, ground, () =>
            {
                InventoryItem Put() => items.Put(row, new InventoryItem(items.NewItemId(), cell.X, cell.Y, ground.Item));
                if (CharacterSaves.Load(CharacterName(conn))?.Tutorial is { } tutorial)
                    foreach (bool support in new[] { false, true })
                        if (conn.Instance.IsStartWeapon(TutorialObjects.DropKey(CharacterName(conn), tutorial, support), id))
                            return TutorialObjects.Claim(CharacterName(conn), support, ground.Item, Put);
                return glyph is int g ? GlyphQuest.Claim(CharacterName(conn), g, Put) : Put();
            }, id);
        }
        await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove world item 0x{id:x} (picked up from ({stop.X}, {stop.Y}))");
        if (placed is null) return;
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), [], [placed]),
            toCursor ? $"0x00bf cursor: hold item {placed.Id}" : $"0x00bf inventory 1: add item {placed.Id} at ({cell.X}, {cell.Y})");
        if (glyph is int picked) await SetQuestFlags(remote, stream, ciphers, conn, GlyphQuest.PickupFlags(picked));
    }

    static bool IsGold(WorldItemObject ground) => Items.ByHash(ground.Item.TypeHash)?.Class == ItemDrops.GoldClass;

    // Gold goes to the stash's gold of the character's league (S→C 0x2a9), not into the inventory.
    static async Task PickUpGold(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, uint id, WorldItemObject ground, string how)
    {
        uint total;
        lock (conn.Monsters)
        {
            // A tick can resume from an earlier notification after the lethal contact committed migration.
            if (HardcoreProgressBlocked(conn)) return;
            if (conn.Instance.Ground.Take(id) is null) return;   // already picked up
            total = PersonalStashOf(conn).AddGold(ground.Item.StackSize);
        }
        await Send(remote, stream, ciphers, InstanceProtocol.RemoveObject(id), $"0x02bb remove world item 0x{id:x} (gold {how})");
        await Send(remote, stream, ciphers, InstanceEntities.Gold(total), $"0x02a9 gold: {total} (+{ground.Item.StackSize} {how})");
    }

    // A pickup whose save fails puts the item back on the ground before the error closes the connection: it left the ground
    // first, and the items are loaded again as saved (Saves).
    static T KeepOnGroundIfNotSaved<T>(ClientState conn, WorldItemObject ground, Func<T> pickUp, uint? originalId = null)
    {
        try { return pickUp(); }
        catch
        {
            if (originalId is uint id) conn.Instance.Ground.Restore(id, ground);
            else conn.Instance.Ground.Add(ground);
            throw;
        }
    }

    // C→S 0x7c: Shift + click on a stack, then the split window's amount. The stack shrinks in place (0xbf removing and adding
    // its id), the split-off part is held as a new item (0xbf cursor).
    static async Task SplitStack(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceItems.SplitStack split, string hex)
    {
        var items = ItemsOf(conn);
        int amount = (int)Math.Min(split.Amount, ushort.MaxValue);
        if (StashOf(conn).TabOf(split.InventoryId) is int tab)
        {
            // From a stash tab: the tab's stack shrinks and the split-off part is held with a character id, in one transaction.
            var stash = StashOf(conn);
            if (ItemMoves.SplitFromStash(stash, items, tab, split.ItemId, amount, ShapeOf) is not var (left, part))
            {
                Console.WriteLine($"[gateway] {remote} <= 0x007c split {split} in stash tab {tab}: refused: {hex}");
                return;
            }
            Console.WriteLine($"[gateway] {remote} <= 0x007c split {split} in stash tab {tab}" + (left is null ? " (the whole stack: picked up)" : ""));
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(split.InventoryId, stash.NextRevision(tab), [left?.Id ?? part.Id], left is null ? [] : [left], stash.IsGuild ? InstanceStash.GuildManager : (byte)0),
                left is null ? $"0x00bf stash tab {tab}: remove item {part.Id}" : $"0x00bf stash tab {tab}: stack {left.Id} now {left.Item.StackSize}");
            await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
                items.NextRevision(PlayerInventories.Cursor), [], [part]), $"0x00bf cursor: hold {part.Item.StackSize} as item {part.Id}");
            return;
        }
        string why = "unknown inventory";
        var done = PlayerInventories.Row(split.InventoryId) is byte row ? items.Split(row, split.ItemId, amount, out why) : null;
        if (done is not var (stack, held) || PlayerInventories.Row(split.InventoryId) is not byte r)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x007c split {split}: refused ({why}): {hex}");
            return;
        }
        Console.WriteLine($"[gateway] {remote} <= 0x007c split {split}");
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(split.InventoryId, items.NextRevision(r), [stack.Id], [stack]),
            $"0x00bf inventory {split.InventoryId}: stack {stack.Id} now {stack.Item.StackSize}");
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [], [held]), $"0x00bf cursor: hold {held.Item.StackSize} as item {held.Id}");
    }

    // C→S 0x58: use an item that needs no target (a right-click), such as an Orb of Regret: one item becomes one refund point.
    static async Task UseItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn,
        InstanceItems.UseItemRequest use, string hex)
    {
        const string Regret = "Metadata/Items/Currency/CurrencyPassiveRefund";
        var items = ItemsOf(conn);
        byte? row = null;
        foreach (uint inventory in new[] { use.InventoryId, use.InventoryId + 1 })
            if (PlayerInventories.Row(inventory) is byte candidate && items.Items(candidate).Any(i => i.Id == use.ItemId))
            {
                row = candidate;
                break;
            }
        if (row is not byte r || items.Items(r).FirstOrDefault(i => i.Id == use.ItemId) is not { } item)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0058 use item {use}: no such item: {hex}");
            return;
        }
        if (ItemsFor(conn).ByHash(item.Item.TypeHash) is not { } type)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0058 use item {use}: unknown type 0x{item.Item.TypeHash:x8}: {hex}");
            return;
        }
        if (type.Id == PortalScroll)
        {
            await UsePortalScroll(remote, stream, ciphers, conn, r, item, type);
            return;
        }
        if (type.Name == SkillBook)
        {
            await UseSkillBook(remote, stream, ciphers, conn, items, r, item, type);
            return;
        }
        if (type.Id != Regret && type.Name != RegretBook)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0058 use item {use}: {type.Name} is not handled: {hex}");
            return;
        }
        // Read the current balance inside the same transaction as consumption. An older area must not restore
        // refund points another connection has already spent.
        var result = Saves.Atomically(() =>
        {
            ushort held = CharacterSaves.Load(CharacterName(conn))?.RefundPoints ?? 0;
            if (held == ushort.MaxValue) return (Points: held, Taken: (PlayerInventories.Consumption?)null);
            // An Orb of Regret gives one refund point; a Book of Regrets two (ClientStrings ItemFunction2RespecPoints).
            ushort points = (ushort)Math.Min(ushort.MaxValue, held + (type.Name == RegretBook ? 2 : 1));
            return (Points: points, Taken: ItemMoves.UseForRefundPoint(items, CharacterName(conn), r, use.ItemId, points));
        });
        if (result.Taken is not { } taken)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0058 use {type.Name}: refund cap reached or item {use.ItemId} disappeared: {hex}");
            return;
        }
        ushort refunds = result.Points;
        lock (conn.Monsters)
        {
            conn.RefundPoints = refunds;
            conn.SavedPassiveProgress = new(conn.MainAscendancy, conn.Passives, conn.MasteryEffects, refunds);
        }
        uint inventoryId = PlayerInventories.InventoryId(r);
        // Every change removes the id first: the client keeps an item by id, so a stack that only shrinks needs the remove
        // and the add of the same id with its new Stack component (as SendMerged does).
        await Send(remote, stream, ciphers,
            InstanceItems.InventoryChange(inventoryId, items.NextRevision(r), [item.Id],
                taken.Left is { } left ? [left] : []),
            $"0x00bf inventory {inventoryId}: {type.Name} used" + (taken.Left is { } now ? $", {now.Item.StackSize} left" : ", stack empty"));
        await SendPassives(remote, stream, ciphers, conn);
        Console.WriteLine($"[gateway] {remote} <= 0x0058 use {type.Name} ({use.ItemId}): {refunds} refund point(s)");
    }

    // A Book of Skill used (C→S 0x58, right-click; any of the quest books named so, Metadata/Items/QuestItems/SkillBooks/…):
    // the book is used up and the character gets one more passive skill point, as /add_passive_points 1 (S→C 0x33).
    static async Task UseSkillBook(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, PlayerInventories items,
        byte row, InventoryItem item, ItemCatalog.BaseType type)
    {
        ushort extra;
        lock (conn.Monsters)
        {
            if (conn.ExtraPassivePoints == ushort.MaxValue)
            {
                Console.WriteLine($"[gateway] {remote} <= 0x0058 use {type.Name}: extra passive points are already at the maximum");
                return;
            }
            extra = (ushort)(conn.ExtraPassivePoints + 1);
        }
        // The book and the point are saved together.
        if (ItemMoves.UseForPassivePoint(items, CharacterName(conn), row, item.Id, extra) is not { } taken) return;
        lock (conn.Monsters) conn.ExtraPassivePoints = extra;
        uint inventoryId = PlayerInventories.InventoryId(row);
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(inventoryId, items.NextRevision(row), [item.Id], taken.Left is { } left ? [left] : []),
            $"0x00bf inventory {inventoryId}: {type.Name} used");
        await SendPassives(remote, stream, ciphers, conn);
        Console.WriteLine($"[gateway] {remote} <= 0x0058 use {type.Name} ({item.Id}, {type.Id}): +1 passive point, {extra} extra in all");
    }

    // C→S 0x71: an item used on another one. A Scroll of Wisdom identifies an unidentified item (its explicit mods and rare
    // name are sent from now on) and loses one from its stack. Socket currency uses the same update path, including Shift.
    static async Task UseItemOn(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceItems.UseItemOn use, string hex)
    {
        const string Wisdom = "Metadata/Items/Currency/CurrencyIdentification";
        var items = ItemsOf(conn);
        var stash = StashOf(conn);
        // The currency can come from a stash tab (right-click it in the open stash, then click an item in the inventory): the
        // message names the tab's inventory. Decided by the inventory, not the item id: stash and character ids can be equal.
        int? stashTab = stash.TabOf(use.InventoryId);
        byte usedRow = stashTab is null && items.RowOf(use.ItemId) is byte row ? row : (byte)0;
        var used = stashTab is int fromTab ? stash.Inventory(fromTab).Items.FirstOrDefault(i => i.Id == use.ItemId)
            : items.RowOf(use.ItemId) is not null ? items.Items(usedRow).FirstOrDefault(i => i.Id == use.ItemId) : null;
        // The target can be in a stash tab too (the currency tab's crafting slot, live 2026-10-23), decided the same way.
        int? targetTab = stash.TabOf(use.TargetInventoryId);
        byte targetRow = targetTab is null && items.RowOf(use.TargetId) is byte tRow ? tRow : (byte)0;
        var target = targetTab is int toTab ? stash.Inventory(toTab).Items.FirstOrDefault(i => i.Id == use.TargetId)
            : items.RowOf(use.TargetId) is not null ? items.Items(targetRow).FirstOrDefault(i => i.Id == use.TargetId) : null;
        if (used is null || target is null)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0071 use item on item {use}: no such item" + (stashTab is int t ? $" (currency in stash tab {t})" : "")
                + (targetTab is int tt ? $" (target in stash tab {tt})" : "") + $": {hex}");
            return;
        }
        var type = ItemsFor(conn).ByHash(used.Item.TypeHash);
        bool isEssence = type is not null && EssencesFor(conn).Contains(type.Hash);
        if (type?.Id == CurrencyEffects.MirrorOfKalandra)
        {
            if (stashTab is not null || targetTab is not null) { Console.WriteLine($"[gateway] {remote} <= 0x0071 Mirror of Kalandra with the stash: not handled, use it in the inventory"); return; }
            await MirrorItem(remote, stream, ciphers, conn, items, usedRow, used, target);
            return;
        }
        // What the currency makes of the target, or null (nothing happens, nothing is spent).
        // A corrupted or mirrored item takes no currency but a Scroll of Wisdom (no corrupted currency yet).
        Item? result = (target.Item.Corrupted || target.Item.Mirrored) && type?.Id != Wisdom ? null : type?.Id switch
        {
            Wisdom => target.Item.Identified || !target.Item.Components.Contains("Mods") ? null : target.Item with { Identified = true },
            CurrencyEffects.ChromaticOrb => ItemsFor(conn).ByHash(target.Item.TypeHash) is { } targetType
                ? CurrencyEffects.Chromatic(target.Item, ItemsFor(conn).Requirements(targetType), Random.Shared) : null,
            CurrencyEffects.JewellersOrb => ItemsFor(conn).ByHash(target.Item.TypeHash) is { } socketType
                ? CurrencyEffects.Jewellers(target.Item, socketType, Random.Shared) : null,
            CurrencyEffects.OrbOfFusing => ItemsFor(conn).ByHash(target.Item.TypeHash) is { } linkType
                ? CurrencyEffects.Fusing(target.Item, linkType, Random.Shared) : null,
            CurrencyEffects.OrbOfAlchemy => CurrencyEffects.Alchemy(target.Item, RollsFor(conn), Random.Shared),
            CurrencyEffects.ChaosOrb => CurrencyEffects.Chaos(target.Item, RollsFor(conn), Random.Shared),
            CurrencyEffects.DivineOrb => RollsFor(conn).Reforge(target.Item, Random.Shared),
            CurrencyEffects.BlessedOrb => RollsFor(conn).Bless(target.Item, Random.Shared),
            CurrencyEffects.OrbOfScouring => CurrencyEffects.Scouring(target.Item, RollsFor(conn)),
            CurrencyEffects.OrbOfTransmutation => CurrencyEffects.Transmutation(target.Item, RollsFor(conn), Random.Shared),
            CurrencyEffects.OrbOfAlteration => CurrencyEffects.Alteration(target.Item, RollsFor(conn), Random.Shared),
            CurrencyEffects.OrbOfAugmentation => CurrencyEffects.Augmentation(target.Item, RollsFor(conn), Random.Shared),
            CurrencyEffects.RegalOrb => CurrencyEffects.Regal(target.Item, RollsFor(conn), Random.Shared),
            CurrencyEffects.ExaltedOrb => CurrencyEffects.Exalted(target.Item, RollsFor(conn), Random.Shared),
            CurrencyEffects.OrbOfAnnulment => CurrencyEffects.Annulment(target.Item, RollsFor(conn), Random.Shared),
            CurrencyEffects.ArmourersScrap => CurrencyEffects.ArmourerScrap(target.Item),
            CurrencyEffects.BlacksmithsWhetstone => CurrencyEffects.Whetstone(target.Item),
            CurrencyEffects.OrbOfChance => CurrencyEffects.Chance(target.Item, RollsFor(conn), Random.Shared),
            CurrencyEffects.GlassblowersBauble => CurrencyEffects.Glassblower(target.Item),
            CurrencyEffects.GemcuttersPrism => CurrencyEffects.Gemcutter(target.Item),
            CurrencyEffects.FracturingOrb => CurrencyEffects.Fracture(target.Item, Random.Shared),
            CurrencyEffects.VaalOrb => ItemsFor(conn).ByHash(target.Item.TypeHash) is { } vaalType
                ? CurrencyEffects.Vaal(target.Item, vaalType, ItemsFor(conn).Requirements(vaalType), RollsFor(conn), Random.Shared) : null,
            _ when isEssence => EssencesFor(conn).Apply(type!.Hash, target.Item, Random.Shared),
            _ => null,
        };
        if (result is null)
        {
            if (type?.Id == CurrencyEffects.JewellersOrb && ItemsFor(conn).ByHash(target.Item.TypeHash) is { } refusedType)
                Console.WriteLine($"[gateway] Jeweller target {target.Id}: {refusedType.Id}, class {refusedType.Class}, ilvl {target.Item.ItemLevel}, " +
                    $"sockets {target.Item.Sockets.Count}, roll cap {CurrencyEffects.MaximumSockets(target.Item, refusedType)}, " +
                    $"base cap {CurrencyEffects.SocketCapacity(target.Item, refusedType)}, gems {target.Item.Sockets.Count(s => s.Gem is not null)}");
            Console.WriteLine($"[gateway] {remote} <= 0x0071 use {type?.Name ?? $"0x{used.Item.TypeHash:x8}"} on item {use.TargetId}: " +
                (isEssence || type?.Id is Wisdom or CurrencyEffects.ChromaticOrb or CurrencyEffects.JewellersOrb or CurrencyEffects.OrbOfFusing or CurrencyEffects.OrbOfAlchemy or CurrencyEffects.ChaosOrb or CurrencyEffects.DivineOrb or CurrencyEffects.BlessedOrb or CurrencyEffects.OrbOfScouring or CurrencyEffects.OrbOfTransmutation or CurrencyEffects.OrbOfAlteration or CurrencyEffects.OrbOfAugmentation or CurrencyEffects.RegalOrb or CurrencyEffects.ExaltedOrb or CurrencyEffects.OrbOfAnnulment or CurrencyEffects.ArmourersScrap or CurrencyEffects.BlacksmithsWhetstone or CurrencyEffects.OrbOfChance or CurrencyEffects.VaalOrb or CurrencyEffects.GlassblowersBauble or CurrencyEffects.GemcuttersPrism or CurrencyEffects.FracturingOrb
                    ? "target refused (corrupted / mirrored / identified / wrong rarity / no explicit mods / unsupported sockets / maximum sockets / socketed gems / protected links)" : $"not handled: {hex}"));
            return;
        }
        // The currency and the changed item are saved together.
        InventoryItem identified;
        uint targetInventory = PlayerInventories.InventoryId(targetRow);
        if (stashTab is not null || targetTab is not null)
        {
            // The stash is in it (currency from a tab, or the target in one): one transaction, then one 0xbf per place changed
            // (one for both when they share it), removing and adding the same ids as the client needs.
            if (ItemMoves.UseAcross(items, stash, new(stashTab, usedRow, used.Id), new(targetTab, targetRow, target.Id), _ => result)
                is not var (left, changedItem)) return;
            identified = changedItem;
            byte manager = stash.IsGuild ? InstanceStash.GuildManager : (byte)0;
            async Task SendPlace(int? tab, byte row, List<uint> gone, List<InventoryItem> now, string what) =>
                await Send(remote, stream, ciphers, tab is int t
                    ? InstanceItems.InventoryChange(stash.InventoryOf(t), stash.NextRevision(t), gone, now, manager)
                    : InstanceItems.InventoryChange(PlayerInventories.InventoryId(row), items.NextRevision(row), gone, now),
                    $"0x00bf {(tab is int st ? $"stash tab {st}" : $"inventory {PlayerInventories.InventoryId(row)}")}: {what}");
            List<InventoryItem> usedNow = left is { } l ? [l] : [];
            if (stashTab == targetTab && (stashTab is not null || usedRow == targetRow))
                await SendPlace(targetTab, targetRow, [used.Id, target.Id], [.. usedNow, identified], $"item {target.Id} changed, {type!.Name} used");
            else
            {
                await SendPlace(stashTab, usedRow, [used.Id], usedNow, $"{type!.Name} used" + (left is null ? ", stack gone" : $", {left.Item.StackSize} left"));
                await SendPlace(targetTab, targetRow, [target.Id], [identified], $"item {target.Id} changed");
                if (targetTab is null && PlayerInventories.ShowsOnModel(targetRow)) await SendEquipment(remote, stream, ciphers, conn, items);
            }
        }
        else
        {
            if (ItemMoves.UseOn(items, usedRow, used.Id, targetRow, target.Id, _ => result) is not var (taken, changed)) return;
            identified = changed;
            // Remove and add the same ids: the client replaces an item only that way (see UseItem).
            uint usedInventory = PlayerInventories.InventoryId(usedRow);
            if (usedRow == targetRow)
                await Send(remote, stream, ciphers, InstanceItems.InventoryChange(targetInventory, items.NextRevision(targetRow), [used.Id, target.Id],
                    taken.Left is { } left ? [left, identified] : [identified]), $"0x00bf inventory {targetInventory}: item {target.Id} changed, {type!.Name} used");
            else
            {
                await Send(remote, stream, ciphers, InstanceItems.InventoryChange(usedInventory, items.NextRevision(usedRow), [used.Id],
                    taken.Left is { } left ? [left] : []), $"0x00bf inventory {usedInventory}: {type!.Name} used");
                await Send(remote, stream, ciphers, InstanceItems.InventoryChange(targetInventory, items.NextRevision(targetRow), [target.Id], [identified]),
                    $"0x00bf inventory {targetInventory}: item {target.Id} changed");
                if (PlayerInventories.ShowsOnModel(targetRow)) await SendEquipment(remote, stream, ciphers, conn, items);
            }
        }
        Console.WriteLine($"[gateway] {remote} <= 0x0071 {type!.Name} ({used.Id}) on item {target.Id}: " + (isEssence || type.Id is Wisdom or CurrencyEffects.OrbOfAlchemy or CurrencyEffects.ChaosOrb or CurrencyEffects.DivineOrb or CurrencyEffects.BlessedOrb or CurrencyEffects.OrbOfScouring or CurrencyEffects.OrbOfTransmutation or CurrencyEffects.OrbOfAlteration or CurrencyEffects.OrbOfAugmentation or CurrencyEffects.RegalOrb or CurrencyEffects.ExaltedOrb or CurrencyEffects.OrbOfAnnulment or CurrencyEffects.ArmourersScrap or CurrencyEffects.BlacksmithsWhetstone or CurrencyEffects.OrbOfChance or CurrencyEffects.VaalOrb or CurrencyEffects.GlassblowersBauble or CurrencyEffects.GemcuttersPrism or CurrencyEffects.FracturingOrb
            ? string.Join("; ", identified.Item.Mods.Select(ItemsFor(conn).Mods.Describe))
            : $"sockets {string.Join(" ", identified.Item.Sockets.Select(k => k.Colour))}; link groups {string.Join(",", identified.Item.Links ?? [])}"));
    }

    // Mirror of Kalandra (C→S 0x71 / Shift 0x72): a mirrored copy of the target goes onto the (empty) cursor, as if picked
    // up, for the player to place; the target stays as it was. One mirror and the copy are saved together.
    static async Task MirrorItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, PlayerInventories items,
        byte usedRow, InventoryItem used, InventoryItem target)
    {
        if (CurrencyEffects.Mirror(target.Item) is not { } copy || items.Items(PlayerInventories.Cursor).Count > 0)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x0071 Mirror of Kalandra on item {target.Id}: refused (corrupted / mirrored / no mods / cursor not empty)");
            return;
        }
        var done = Saves.Atomically<(PlayerInventories.Consumption, InventoryItem)?>(() =>
            items.ConsumeOne(usedRow, used.Id) is { } taken ? (taken, items.Add(PlayerInventories.Cursor, 0, 0, copy)) : null);
        if (done is not var (taken, held)) return;
        uint usedInventory = PlayerInventories.InventoryId(usedRow), cursor = PlayerInventories.InventoryId(PlayerInventories.Cursor);
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(usedInventory, items.NextRevision(usedRow), [used.Id],
            taken.Left is { } left ? [left] : []), $"0x00bf inventory {usedInventory}: Mirror of Kalandra used");
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(cursor, items.NextRevision(PlayerInventories.Cursor), [], [held]),
            $"0x00bf cursor: mirrored copy {held.Id}");
        Console.WriteLine($"[gateway] {remote} <= 0x0071 Mirror of Kalandra ({used.Id}) on item {target.Id}: copy {held.Id} on the cursor");
    }

    // C→S 0x4a: the held item clicked on an item. A stack of the same type with room takes what fits (PlayerInventories.MergeOnto,
    // AccountStash.Merge); anything else (currency on an item …) is not handled yet.
    static async Task HeldOntoItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceItems.ItemAction a, string hex)
    {
        var items = ItemsOf(conn);
        if (StashOf(conn).TabOf(a.InventoryId) is int tab)
        {
            // On a stash tab: as 0x2a on the clicked item's cell (merge, one with Shift, or a swap).
            if (StashOf(conn).Inventory(tab).Items.FirstOrDefault(i => i.Id == a.ItemId) is not { } onto)
            {
                Console.WriteLine($"[gateway] {remote} <= 0x004a held item onto {a} in stash tab {tab}: no such item: {hex}");
                return;
            }
            Console.WriteLine($"[gateway] {remote} <= 0x004a held item onto {a} in stash tab {tab}: {hex}");
            await PlaceInStash(remote, stream, ciphers, conn, tab, new InstanceItems.PlaceItem(a.InventoryId, onto.X, onto.Y, (byte)(OneOf(a) ? 1 : 0), a.B, 0, 0));
            return;
        }
        string why = "unknown inventory";
        var done = PlayerInventories.Row(a.InventoryId) is byte row
            ? LevelTooLow(conn, row, items.Items(PlayerInventories.Cursor).FirstOrDefault()?.Item) is { } low ? null : items.MergeOnto(row, a.ItemId, ShapeOf, out why, OneOf(a))
            : null;
        if (done is null && PlayerInventories.Row(a.InventoryId) is byte lr && LevelTooLow(conn, lr, items.Items(PlayerInventories.Cursor).FirstOrDefault()?.Item) is { } tooLow) why = tooLow;
        if (done is null)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x004a held item onto {a}: refused ({why}): {hex}");
            return;
        }
        Console.WriteLine($"[gateway] {remote} <= 0x004a held item onto {a}: {hex}");
        if (done.Merged || done.Rest is not null) { await SendMerged(remote, stream, ciphers, items, done); return; }
        // Not a stack with room: swapped, as 0x2a on that cell.
        var d = done.Displaced;
        uint target = PlayerInventories.InventoryId(done.Row);
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(target, items.NextRevision(done.Row), d is null ? [] : [d.Id], [done.Placed]),
            $"0x00bf inventory {target}: add item {done.Placed.Id} at ({done.Placed.X}, {done.Placed.Y})" + (d is null ? "" : $", remove item {d.Id}"));
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [done.Held.Id], d is null ? [] : [d]),
            $"0x00bf cursor: remove item {done.Held.Id}" + (d is null ? "" : $", hold item {d.Id}"));
        if (PlayerInventories.ShowsOnModel(done.Row)) await SendEquipment(remote, stream, ciphers, conn, items);
        if (done.Row == PlayerInventories.Flask) await RefreshMagebloodFlasks(remote, stream, ciphers, conn);
    }

    // Shift held on a 0x4a: flag bit 2 (the sender's param 7; live 2026-10-19, Flags = 4 with Shift, 0 without).
    static bool OneOf(InstanceItems.ItemAction a) => (a.Flags & 0x04) != 0;

    // A held stack went onto a stack of its type: that one grows in place (same id), the held one shrinks (same id) or is gone.
    static async Task SendMerged(string remote, NetworkStream stream, ConnectionCiphers ciphers, PlayerInventories items, PlayerInventories.Placement done)
    {
        uint target = PlayerInventories.InventoryId(done.Row);
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(target, items.NextRevision(done.Row), done.Merged ? [done.Placed.Id] : [], [done.Placed]),
            $"0x00bf inventory {target}: " + (done.Merged ? $"stack {done.Placed.Id} now {done.Placed.Item.StackSize}" : $"add one as item {done.Placed.Id} at ({done.Placed.X}, {done.Placed.Y})")
            + (Items.ByHash(done.Placed.Item.TypeHash) is { } now && now.Hash != done.Held.Item.TypeHash ? $", became {now.Name}" : ""));
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [done.Held.Id], done.Rest is { } rest ? [rest] : []),
            $"0x00bf cursor: " + (done.Rest is { } r ? $"stack {r.Id} now {r.Item.StackSize}" : $"remove item {done.Held.Id} (merged)"));
    }

    // C→S 0x2a: put the held item into an inventory cell (PlayerInventories.Place). The target inventory gets one 0xbf (the
    // item it displaced removed, the held item added), the cursor another (the held item removed, the displaced one added).
    static async Task PlaceItem(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, InstanceItems.PlaceItem place)
    {
        if (conn.Sale is { } sale && place.InventoryId == sale.Mine)
        {
            uint cursor = PlayerInventories.InventoryId(PlayerInventories.Cursor);
            var held = ItemsOf(conn).Items(PlayerInventories.Cursor).FirstOrDefault();
            if (held is not null) await OfferForSale(remote, stream, ciphers, conn, sale, cursor, held.Id, (byte)place.X, (byte)place.Y);
            return;
        }
        if (StashOf(conn).TabOf(place.InventoryId) is int tab) { await PlaceInStash(remote, stream, ciphers, conn, tab, place); return; }
        var items = ItemsOf(conn);
        if (place.A != 0) Console.WriteLine($"[gateway] {remote} <= 0x002a place {place}: one of the held stack (Shift)");
        PlayerInventories.Placement? done = null;
        string why = "unknown inventory";
        if (PlayerInventories.Row(place.InventoryId) is byte row && LevelTooLow(conn, row, items.Items(PlayerInventories.Cursor).FirstOrDefault()?.Item) is { } low)
            why = low;
        else if (PlayerInventories.Row(place.InventoryId) is byte r)
            done = items.Place(r, (int)place.X, (int)place.Y, ShapeOf, out why, one: place.A != 0);
        if (done is null)
        {
            Console.WriteLine($"[gateway] {remote} <= 0x002a place {place}: refused ({why})");
            return;
        }
        if (done.Row == PlayerInventories.Flask) done = done with { Placed = EmptyIfEquippedInHostileArea(conn, items, done.Placed) };
        var d = done.Displaced;
        uint target = PlayerInventories.InventoryId(done.Row);   // may differ from the clicked one (off hand → main hand)
        if (done.Merged || done.Rest is not null) { await SendMerged(remote, stream, ciphers, items, done); return; }
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(target, items.NextRevision(done.Row), d is null ? [] : [d.Id], [done.Placed]),
            $"0x00bf inventory {target}: add item {done.Placed.Id} at ({done.Placed.X}, {done.Placed.Y})" + (d is null ? "" : $", remove item {d.Id}"));
        await Send(remote, stream, ciphers, InstanceItems.InventoryChange(PlayerInventories.InventoryId(PlayerInventories.Cursor),
            items.NextRevision(PlayerInventories.Cursor), [done.Held.Id], d is null ? [] : [d]),
            $"0x00bf cursor: remove item {done.Held.Id}" + (d is null ? "" : $", hold item {d.Id}"));
        if (PlayerInventories.ShowsOnModel(done.Row)) await SendEquipment(remote, stream, ciphers, conn, items);
        if (done.Row == PlayerInventories.Flask) await RefreshMagebloodFlasks(remote, stream, ciphers, conn);
    }
}
