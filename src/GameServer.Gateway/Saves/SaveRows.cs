using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServer.Core.Protocol.Instance;
using GameServer.Storage;
using Microsoft.Data.Sqlite;
using static CharacterSaves;

/// <summary>
/// Character saves and stashes as rows of the save tables (sql/updates/.../relational_saves.sql): character_saves and its
/// character_* tables, stash_saves with stash_tab_headers and stash_module_data, and items with item_mods for every saved
/// item. A save is written whole in one transaction (its items only when they changed since this process last wrote
/// them) and read back into the same records the Gateway used as JSON. <see cref="Migrate"/> moves the JSON saves from
/// before the tables.
/// </summary>
public static class SaveRows
{
    static readonly JsonSerializerOptions Json = CharacterSaves.Opts;

    // ---------------------------------------------------------------- characters

    /// <summary>A character's save (not a deleted one's), or null when it has none.</summary>
    public static Save? LoadCharacter(SaveDatabase db, string name) => db.Read(() =>
        CharacterId(db, name) is { } id ? ReadCharacter(db, id) : null);

    /// <summary>Writes a character's save, replacing what was there.</summary>
    public static void WriteCharacter(SaveDatabase db, string name, Save save) => db.Atomically(() =>
    {
        long id = CharacterId(db, name) ?? Insert(db, "INSERT INTO character_saves (name, has_inventories) VALUES ($name, 0) RETURNING id", ("$name", name));
        WriteCharacterRow(db, id, save);
    });

    static long? CharacterId(SaveDatabase db, string name)
    {
        using var c = db.Command("SELECT id FROM character_saves WHERE name = $name AND deleted_utc IS NULL", ("$name", name));
        return c.ExecuteScalar() is long id ? id : null;
    }

    static readonly string[] CharacterColumns =
    [
        "has_inventories", "experience", "passives", "waypoints", "visited", "quest_flags", "extra_passive_points", "gold",
        "refund_points", "portal_cosmetic", "active_auras", "petrified_blood", "selected_hideout", "life", "mana",
        "vitals_character", "vitals_session", "vitals_retired", "cluster_passives", "sulphite", "atlas_passive_presets",
        "atlas_passive_points", "selected_atlas_layout", "quest_refunds_credited", "pantheon_major", "pantheon_minor",
        "attack_in_place_slots", "skill_bar_mask52", "stance", "unknown",
    ];

    static readonly string[] CharacterChildren =
    [
        "character_inventories", "character_skill_bar", "character_masteries", "character_cosmetics", "character_skill_cosmetics",
        "character_hideout_placements", "character_monster_statues", "character_tattoos", "character_tattoo_keystones",
        "character_module_data", "character_tutorials", "character_ascendancies",
    ];

    static void WriteCharacterRow(SaveDatabase db, long id, Save s)
    {
        object?[] values =
        [
            s.Inventories is not null, s.Experience, JsonOf(s.Passives), JsonOf(s.Waypoints), JsonOf(s.Visited), JsonOf(s.QuestFlags),
            s.ExtraPassivePoints, s.Gold, s.RefundPoints, s.PortalCosmetic, JsonOf(s.ActiveAuras), s.PetrifiedBlood,
            s.SelectedHideout, s.Life, s.Mana, s.VitalsOwner?.CharacterId.ToString(), s.VitalsOwner?.SessionId.ToString(),
            s.VitalsOwner?.Retired, JsonOf(s.ClusterPassives), s.Sulphite, JsonOf(s.AtlasPassivePresets), s.AtlasPassivePoints,
            s.SelectedAtlasLayout, JsonOf(s.QuestRefundsCredited), s.PantheonMajor, s.PantheonMinor, s.AttackInPlaceSlots,
            s.SkillBarMask52, s.Stance, JsonOf(s.Unknown),
        ];
        using (var update = db.Command($"UPDATE character_saves SET {string.Join(", ", CharacterColumns.Select((c, i) => $"{c} = $p{i}"))} WHERE id = $id",
                   [("$id", id), .. values.Select((v, i) => ($"$p{i}", Value(v)))]))
            update.ExecuteNonQuery();
        foreach (string table in CharacterChildren) Run(db, $"DELETE FROM {table} WHERE save = $id", ("$id", id));

        foreach (byte inventory in s.Inventories?.Keys ?? Enumerable.Empty<byte>())
            Run(db, "INSERT INTO character_inventories (save, inventory) VALUES ($s, $i)", ("$s", id), ("$i", (long)inventory));
        foreach (var (slot, skill) in s.SkillBar ?? [])
            Run(db, "INSERT INTO character_skill_bar (save, slot, skill) VALUES ($s, $k, $v)", ("$s", id), ("$k", (long)slot), ("$v", skill));
        foreach (var (node, effect) in s.MasteryEffects ?? [])
            Run(db, "INSERT INTO character_masteries (save, node, effect) VALUES ($s, $k, $v)", ("$s", id), ("$k", (long)node), ("$v", (long)effect));
        foreach (var (slot, c) in s.Cosmetics ?? [])
            Run(db, "INSERT INTO character_cosmetics (save, slot, record, option, hash) VALUES ($s, $k, $r, $o, $h)",
                ("$s", id), ("$k", (long)slot), ("$r", (long)c.Record), ("$o", (long)c.Option), ("$h", Value(c.Hash)));
        foreach (var (skill, c) in s.SkillCosmetics ?? [])
            Run(db, "INSERT INTO character_skill_cosmetics (save, skill, record, option, hash) VALUES ($s, $k, $r, $o, $h)",
                ("$s", id), ("$k", skill), ("$r", (long)c.Record), ("$o", (long)c.Option), ("$h", Value(c.Hash)));
        foreach (var (area, placements) in s.HideoutPlacements ?? [])
            foreach (var (key, p) in placements)
                Run(db, "INSERT INTO character_hideout_placements (save, area, key, x, y, rotation) VALUES ($s, $a, $k, $x, $y, $r)",
                    ("$s", id), ("$a", area), ("$k", key), ("$x", (long)p.X), ("$y", (long)p.Y), ("$r", (double)p.Rotation));
        foreach (var (area, statues) in s.MonsterStatues ?? [])
            for (int i = 0; i < statues.Count; i++)
            {
                var m = statues[i];
                Run(db, "INSERT INTO character_monster_statues (save, area, ord, x, y, rotation, monster, scale, variation) " +
                        "VALUES ($s, $a, $o, $x, $y, $r, $m, $c, $v)", ("$s", id), ("$a", area), ("$o", (long)i), ("$x", (long)m.X),
                    ("$y", (long)m.Y), ("$r", (double)m.Rotation), ("$m", (long)m.MonsterHash16), ("$c", (long)m.Scale), ("$v", (long)m.Variation));
            }
        foreach (var (node, tattoo) in s.Tattoos ?? [])
            Run(db, "INSERT INTO character_tattoos (save, node, tattoo) VALUES ($s, $k, $v)", ("$s", id), ("$k", (long)node), ("$v", tattoo));
        foreach (var (node, keystone) in s.TattooKeystones ?? [])
            Run(db, "INSERT INTO character_tattoo_keystones (save, node, keystone) VALUES ($s, $k, $v)", ("$s", id), ("$k", (long)node), ("$v", (long)keystone));
        foreach (var (key, value) in s.ModuleData ?? [])
            Run(db, "INSERT INTO character_module_data (save, key, value) VALUES ($s, $k, $v)", ("$s", id), ("$k", key), ("$v", value));
        if (s.Tutorial is { } t)
            Run(db, "INSERT INTO character_tutorials (save, tutorial_id, talked, zombie_killed, chest_opened, skill_claimed, support_claimed, " +
                    "weapon_prompt_heard, wake_up_shown) VALUES ($s, $id, $a, $b, $c, $d, $e, $f, $g)", ("$s", id), ("$id", Value(t.Id)),
                ("$a", Value(t.Talked)), ("$b", Value(t.ZombieKilled)), ("$c", Value(t.ChestOpened)), ("$d", Value(t.SkillClaimed)),
                ("$e", Value(t.SupportClaimed)), ("$f", Value(t.WeaponPromptHeard)), ("$g", Value(t.WakeUpShown)));
        if (s.MainAscendancy is { } a)
            Run(db, "INSERT INTO character_ascendancies (save, family, ascendancy, revision, selection_unknown, nodes, awards, unknown) " +
                    "VALUES ($s, $f, $a, $r, $su, $n, $w, $u)", ("$s", id),
                ("$f", Value(a.Selection is { } f ? JsonSerializer.SerializeToElement(f.Family, Json).ToString() : null)),
                ("$a", Value(a.Selection?.Id)), ("$r", Value(a.Selection?.Revision)), ("$su", Value(JsonOf(a.Selection?.Unknown))),
                ("$n", Value(JsonOf(a.Nodes))), ("$w", Value(JsonOf(a.Awards))), ("$u", Value(JsonOf(a.Unknown))));

        // The items: a save written again with the same items (most writes change progress, not items) keeps its rows.
        var items = new List<(long? Inventory, string Role, int Ord, SavedPlacement? Placement, SavedItem Item)>();
        foreach (var (inventory, list) in s.Inventories ?? [])
            for (int i = 0; i < list.Count; i++) items.Add((inventory, "placed", i, list[i], list[i].Item));
        if (s.StartWeapon is { } weapon) items.Add((null, "start_weapon", 0, null, weapon));
        if (s.Tutorial is { } tutorial)
        {
            items.Add((null, "tutorial_skill", 0, null, tutorial.SkillGem));
            items.Add((null, "tutorial_support", 0, null, tutorial.SupportGem));
        }
        WriteItems(db, Owner.Character, id, null, items);
    }

    static Save ReadCharacter(SaveDatabase db, long id)
    {
        using var c = db.Command($"SELECT {string.Join(", ", CharacterColumns)} FROM character_saves WHERE id = $id", ("$id", id));
        using var r = c.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException($"character save {id} is gone");
        var row = new Row(r);
        var items = ReadItems(db, Owner.Character, id, null);
        var placed = items.Where(i => i.Role == "placed").ToLookup(i => (byte)i.Inventory!.Value);
        Dictionary<byte, List<SavedPlacement>>? inventories = row.Bool("has_inventories") == true
            ? Rows(db, "SELECT inventory FROM character_inventories WHERE save = $s ORDER BY inventory", id)
                .ToDictionary(x => (byte)x.Long("inventory")!.Value, x => placed[(byte)x.Long("inventory")!.Value]
                    .Select(i => new SavedPlacement(i.Placement, i.X, i.Y, i.Item)).ToList())
            : null;
        var tutorial = Rows(db, "SELECT * FROM character_tutorials WHERE save = $s", id).Select(t => new OpeningTutorial(
            Single(items, "tutorial_skill"), Single(items, "tutorial_support"), t.Bool("talked")!.Value, t.Bool("zombie_killed")!.Value,
            t.Bool("chest_opened")!.Value, t.Bool("skill_claimed")!.Value, t.Bool("support_claimed")!.Value, t.Text("tutorial_id"),
            t.Bool("weapon_prompt_heard")!.Value, t.Bool("wake_up_shown")!.Value)).FirstOrDefault();
        var ascendancy = Rows(db, "SELECT * FROM character_ascendancies WHERE save = $s", id).Select(a => new AscendancyProgress(
            a.Text("family") is { } family ? new AscendancyIdentity(JsonSerializer.Deserialize<AscendancyFamily>(JsonSerializer.Serialize(family), Json),
                a.Text("ascendancy")!, a.Text("revision")!) { Unknown = a.Json<Dictionary<string, JsonElement>>("selection_unknown") } : null,
            a.Json<List<ushort>>("nodes"), a.Json<List<string>>("awards")) { Unknown = a.Json<Dictionary<string, JsonElement>>("unknown") }).FirstOrDefault();
        var hideouts = Rows(db, "SELECT * FROM character_hideout_placements WHERE save = $s ORDER BY area, key", id)
            .GroupBy(p => p.Text("area")!).ToDictionary(g => g.Key, g => g.ToDictionary(p => p.Text("key")!,
                p => new HideoutPlacement((int)p.Long("x")!.Value, (int)p.Long("y")!.Value, (float)p.Real("rotation")!.Value)));
        var statues = Rows(db, "SELECT * FROM character_monster_statues WHERE save = $s ORDER BY area, ord", id)
            .GroupBy(m => m.Text("area")!).ToDictionary(g => g.Key, g => g.Select(m => new SavedMonsterStatue((int)m.Long("x")!.Value,
                (int)m.Long("y")!.Value, (float)m.Real("rotation")!.Value, (ushort)m.Long("monster")!.Value, (ushort)m.Long("scale")!.Value,
                (byte)m.Long("variation")!.Value)).ToList());
        return new Save(inventories!,
            NullIfEmpty(Rows(db, "SELECT slot, skill FROM character_skill_bar WHERE save = $s ORDER BY slot", id)
                .ToDictionary(x => (int)x.Long("slot")!.Value, x => x.Text("skill")!)),
            (uint?)row.Long("experience"), row.Json<List<ushort>>("passives"), row.Json<List<string>>("waypoints"),
            row.Json<List<string>>("visited"), row.Json<List<string>>("quest_flags"), (ushort?)row.Long("extra_passive_points"),
            NullIfEmpty(Rows(db, "SELECT node, effect FROM character_masteries WHERE save = $s ORDER BY node", id)
                .ToDictionary(x => (ushort)x.Long("node")!.Value, x => (ushort)x.Long("effect")!.Value)),
            (uint?)row.Long("gold"), (ushort?)row.Long("refund_points"), Single(items, "start_weapon", required: false),
            (ushort?)row.Long("portal_cosmetic"),
            NullIfEmpty(Rows(db, "SELECT * FROM character_cosmetics WHERE save = $s ORDER BY slot", id)
                .ToDictionary(x => (byte)x.Long("slot")!.Value, Cosmetic)),
            NullIfEmpty(Rows(db, "SELECT * FROM character_skill_cosmetics WHERE save = $s ORDER BY skill", id)
                .ToDictionary(x => x.Text("skill")!, Cosmetic)),
            row.Json<List<string>>("active_auras"), row.Bool("petrified_blood"), row.Text("selected_hideout"), tutorial, ascendancy,
            NullIfEmpty(hideouts), NullIfEmpty(statues), (uint?)row.Long("life"), (uint?)row.Long("mana"),
            row.Text("vitals_character") is { } owner ? new VitalsSession(Guid.Parse(owner), Guid.Parse(row.Text("vitals_session")!),
                row.Bool("vitals_retired") == true) : null,
            row.Json<List<ushort>>("cluster_passives"), (ushort?)row.Long("sulphite"),
            NullIfEmpty(Rows(db, "SELECT node, tattoo FROM character_tattoos WHERE save = $s ORDER BY node", id)
                .ToDictionary(x => (ushort)x.Long("node")!.Value, x => x.Text("tattoo")!)),
            NullIfEmpty(Rows(db, "SELECT node, keystone FROM character_tattoo_keystones WHERE save = $s ORDER BY node", id)
                .ToDictionary(x => (ushort)x.Long("node")!.Value, x => (ushort)x.Long("keystone")!.Value)),
            row.Json<List<List<ushort>>>("atlas_passive_presets"), (ushort?)row.Long("atlas_passive_points"), row.Text("selected_atlas_layout"),
            NullIfEmpty(Rows(db, "SELECT key, value FROM character_module_data WHERE save = $s ORDER BY key", id)
                .ToDictionary(x => x.Text("key")!, x => x.Text("value")!)),
            row.Json<List<string>>("quest_refunds_credited"), row.Text("pantheon_major"), row.Text("pantheon_minor"),
            (ushort?)row.Long("attack_in_place_slots"), (ushort?)row.Long("skill_bar_mask52"), row.Text("stance"))
        {
            Unknown = row.Json<Dictionary<string, JsonElement>>("unknown"),
        };
    }

    static SavedCosmetic Cosmetic(Row x) => new((uint)x.Long("record")!.Value, (byte)x.Long("option")!.Value, (uint?)x.Long("hash"));

    static SavedItem Single(List<ItemRow> items, string role) => Single(items, role, required: true)!;

    static SavedItem? Single(List<ItemRow> items, string role, bool required) =>
        items.FirstOrDefault(i => i.Role == role)?.Item
        ?? (required ? throw new InvalidDataException($"the save has no {role} item") : null);

    // ---------------------------------------------------------------- stashes

    /// <summary>A stash's header (its tabs without their items), or null when it has none.</summary>
    public static AccountStash.Save? LoadStash(SaveDatabase db, string account) => db.Read(() =>
    {
        using var c = db.Command("SELECT id, last_tab, next_item_id, gold, delve, league_quest_flags, unknown FROM stash_saves WHERE account = $a",
            ("$a", account));
        using var r = c.ExecuteReader();
        if (!r.Read()) return null;
        var row = new Row(r);
        long id = row.Long("id")!.Value;
        var tabs = Rows(db, "SELECT * FROM stash_tab_headers WHERE stash = $s ORDER BY tab", id).Select(t => new AccountStash.SavedTab(
            t.Text("name")!, (byte)t.Long("type")!.Value, (byte)t.Long("r")!.Value, (byte)t.Long("g")!.Value, (byte)t.Long("b")!.Value, [],
            (uint)t.Long("affinities")!.Value, (ushort?)t.Long("position"), (uint)t.Long("colour")!.Value,
            unchecked((ulong)t.Long("unique_category")!.Value), (ushort)t.Long("parent")!.Value)).ToList();
        var moduleData = Rows(db, "SELECT key, value FROM stash_module_data WHERE stash = $s ORDER BY key", id)
            .ToDictionary(x => x.Text("key")!, x => x.Text("value")!);
        return new AccountStash.Save(tabs, (int)row.Long("last_tab")!.Value, (uint)row.Long("next_item_id")!.Value, (uint)row.Long("gold")!.Value,
            row.Json<JsonElement?>("delve"), moduleData, row.Json<List<int>>("league_quest_flags"))
        {
            Unknown = row.Json<Dictionary<string, JsonElement>>("unknown"),
        };
    });

    /// <summary>Writes a stash's header (its tabs' items are <see cref="WriteStashTab"/>'s), creating the stash.</summary>
    public static void WriteStash(SaveDatabase db, string account, AccountStash.Save header) => db.Atomically(() =>
    {
        long id = StashId(db, account) ?? Insert(db, "INSERT INTO stash_saves (account, last_tab, next_item_id, gold) VALUES ($a, 0, 0, 0) RETURNING id",
            ("$a", account));
        Run(db, "UPDATE stash_saves SET last_tab = $l, next_item_id = $n, gold = $g, delve = $d, league_quest_flags = $q, unknown = $u WHERE id = $id",
            ("$id", id), ("$l", (long)header.LastTab), ("$n", (long)header.NextItemId), ("$g", (long)header.Gold),
            ("$d", Value(header.Delve is { } delve ? delve.GetRawText() : null)), ("$q", Value(JsonOf(header.LeagueQuestFlags))),
            ("$u", Value(JsonOf(header.Unknown))));
        Run(db, "DELETE FROM stash_tab_headers WHERE stash = $id", ("$id", id));
        Run(db, "DELETE FROM stash_module_data WHERE stash = $id", ("$id", id));
        for (int i = 0; i < header.Tabs.Count; i++)
        {
            var t = header.Tabs[i];
            Run(db, "INSERT INTO stash_tab_headers (stash, tab, name, type, r, g, b, affinities, position, colour, unique_category, parent) " +
                    "VALUES ($s, $t, $n, $ty, $r, $g, $b, $a, $p, $c, $u, $pa)", ("$s", id), ("$t", (long)i), ("$n", t.Name), ("$ty", (long)t.Type),
                ("$r", (long)t.R), ("$g", (long)t.G), ("$b", (long)t.B), ("$a", (long)t.Affinities), ("$p", Value(t.Position)),
                ("$c", (long)t.Colour), ("$u", unchecked((long)t.UniqueCategory)), ("$pa", (long)t.Parent));
        }
        foreach (var (key, value) in header.ModuleData ?? [])
            Run(db, "INSERT INTO stash_module_data (stash, key, value) VALUES ($s, $k, $v)", ("$s", id), ("$k", key), ("$v", value));
    });

    /// <summary>One stash tab's items (none for a tab with no items), or null when the stash has no row.</summary>
    public static List<SavedPlacement>? LoadStashTab(SaveDatabase db, string account, int tab) => db.Read(() =>
        StashId(db, account) is { } id
            ? ReadItems(db, Owner.Stash, id, tab).Where(i => i.Role == "placed").Select(i => new SavedPlacement(i.Placement, i.X, i.Y, i.Item)).ToList()
            : null);

    /// <summary>Writes one stash tab's items, replacing that tab's; the stash's header is written first.</summary>
    public static void WriteStashTab(SaveDatabase db, string account, int tab, List<SavedPlacement> items) => db.Atomically(() =>
    {
        long id = StashId(db, account) ?? throw new InvalidOperationException($"stash of {account}: its header is not saved");
        WriteItems(db, Owner.Stash, id, tab, [.. items.Select((p, i) => ((long?)tab, "placed", i, (SavedPlacement?)p, p.Item))]);
    });

    static long? StashId(SaveDatabase db, string account)
    {
        using var c = db.Command("SELECT id FROM stash_saves WHERE account = $a", ("$a", account));
        return c.ExecuteScalar() is long id ? id : null;
    }

    // ---------------------------------------------------------------- items

    enum Owner { Character, Stash }

    sealed record ItemRow(long Id, string Role, long? Parent, int Ord, long? Inventory, uint Placement, byte X, byte Y, SavedItem Item);

    /// <summary>The items last written per database, owner and inventory (null: all of a character's), as JSON: a save
    /// written again with the same items keeps its item rows. Dropped when the transaction is undone.</summary>
    static readonly ConditionalWeakTable<SaveDatabase, System.Collections.Concurrent.ConcurrentDictionary<(Owner, long, long?), string>> Written = [];

    static void WriteItems(SaveDatabase db, Owner owner, long id, long? inventory,
        List<(long? Inventory, string Role, int Ord, SavedPlacement? Placement, SavedItem Item)> items)
    {
        var written = Written.GetOrCreateValue(db);
        var key = (owner, id, inventory);
        string json = JsonSerializer.Serialize(items.Select(i => new object?[] { i.Inventory, i.Role, i.Ord, i.Placement?.Id, i.Placement?.X, i.Placement?.Y, i.Item }), Json);
        if (written.TryGetValue(key, out string? last) && last == json) return;
        db.OnRollback(() => written.TryRemove(key, out _));

        string column = owner == Owner.Character ? "character" : "stash";
        string where = inventory is null ? $"{column} = $o" : $"{column} = $o AND inventory = $i";
        (string, object)[] filter = inventory is null ? [("$o", id)] : [("$o", id), ("$i", inventory.Value)];
        Run(db, $"DELETE FROM item_mods WHERE item IN (SELECT id FROM items WHERE {where})", filter);
        Run(db, $"DELETE FROM items WHERE {where}", filter);

        using var insert = db.Command($"INSERT INTO items ({column}, inventory, role, parent, ord, placement, x, y, {string.Join(", ", ItemColumns)}) " +
            $"VALUES ($o, $inv, $role, $parent, $ord, $placement, $x, $y, {string.Join(", ", ItemColumns.Select((_, i) => $"$c{i}"))}) RETURNING id");
        foreach (string name in new[] { "$o", "$inv", "$role", "$parent", "$ord", "$placement", "$x", "$y" }.Concat(ItemColumns.Select((_, i) => $"$c{i}")))
            insert.Parameters.Add(new SqliteParameter(name, DBNull.Value));
        using var mod = db.Command("INSERT INTO item_mods (item, ord, mod, section, stat_values) VALUES ($item, $ord, $mod, $section, $values)");
        foreach (string name in new[] { "$item", "$ord", "$mod", "$section", "$values" }) mod.Parameters.Add(new SqliteParameter(name, DBNull.Value));

        long Add(long? inv, string role, long? parent, int ord, SavedPlacement? placement, SavedItem item)
        {
            insert.Parameters["$o"].Value = id;
            insert.Parameters["$inv"].Value = Value(inv);
            insert.Parameters["$role"].Value = role;
            insert.Parameters["$parent"].Value = Value(parent);
            insert.Parameters["$ord"].Value = (long)ord;
            insert.Parameters["$placement"].Value = Value(placement?.Id);
            insert.Parameters["$x"].Value = Value(placement?.X);
            insert.Parameters["$y"].Value = Value(placement?.Y);
            object?[] values = ItemValues(item);
            for (int i = 0; i < values.Length; i++) insert.Parameters[$"$c{i}"].Value = Value(values[i]);
            long itemId = (long)insert.ExecuteScalar()!;
            for (int i = 0; i < (item.Mods?.Count ?? 0); i++)
            {
                var m = item.Mods![i];
                mod.Parameters["$item"].Value = itemId;
                mod.Parameters["$ord"].Value = (long)i;
                mod.Parameters["$mod"].Value = m.Id;
                mod.Parameters["$section"].Value = (long)m.Section;
                mod.Parameters["$values"].Value = JsonSerializer.Serialize(m.Values, Json);
                mod.ExecuteNonQuery();
            }
            for (int i = 0; i < (item.Sockets?.Count ?? 0); i++)
                if (item.Sockets![i].Gem is { } gem) Add(inv, "socketed", itemId, i, null, gem);
            if (item.OriginalItem is { } original) Add(inv, "original", itemId, 0, null, original);
            return itemId;
        }

        foreach (var i in items) Add(i.Inventory, i.Role, null, i.Ord, i.Placement, i.Item);
        written[key] = json;
    }

    static readonly string[] ItemColumns =
    [
        "type", "item_level", "quality", "charges", "stack_size", "sockets", "links", "gem_experience", "rarity", "identified",
        "has_mods", "name", "gem_variant", "corrupted", "fractured", "mirrored", "gem_flags", "visual", "defence_base_roll",
        "influences", "split", "synthesised", "foreseeing", "foresee_seed", "alternate_quality_type",
    ];

    static object?[] ItemValues(SavedItem i) =>
    [
        i.Type, i.ItemLevel, i.Quality, i.Charges, i.StackSize, JsonOf(i.Sockets?.Select(s => s.Colour).ToList()), JsonOf(i.Links),
        i.GemExperience, (long)i.Rarity, i.Identified, i.Mods is not null, JsonOf(i.Name), i.GemVariant, i.Corrupted, i.Fractured,
        i.Mirrored, i.GemFlags, i.Visual, i.DefenceBaseRoll, (long)i.Influences, i.Split, i.Synthesised, i.Foreseeing, i.ForeseeSeed,
        i.AlternateQualityType,
    ];

    static List<ItemRow> ReadItems(SaveDatabase db, Owner owner, long id, long? inventory)
    {
        string column = owner == Owner.Character ? "character" : "stash";
        string where = inventory is null ? $"{column} = $o" : $"{column} = $o AND inventory = $i";
        (string, object)[] filter = inventory is null ? [("$o", id)] : [("$o", id), ("$i", inventory.Value)];
        var mods = new Dictionary<long, List<SavedMod>>();
        using (var c = db.Command($"SELECT item, mod, section, stat_values FROM item_mods WHERE item IN (SELECT id FROM items WHERE {where}) ORDER BY item, ord", filter))
        using (var r = c.ExecuteReader())
            while (r.Read())
            {
                long item = r.GetInt64(0);
                if (!mods.TryGetValue(item, out var list)) mods[item] = list = [];
                list.Add(new SavedMod(r.GetString(1), (ModSection)r.GetInt64(2),
                    JsonSerializer.Deserialize<List<int>>(r.GetString(3), Json) ?? throw new InvalidDataException($"item {item}: mod values are null")));
            }
        var rows = new List<(long Id, string Role, long? Parent, int Ord, long? Inventory, uint Placement, byte X, byte Y, Row Values)>();
        using (var c = db.Command($"SELECT id, role, parent, ord, inventory, placement, x, y, {string.Join(", ", ItemColumns)} FROM items WHERE {where} ORDER BY id", filter))
        using (var r = c.ExecuteReader())
            while (r.Read())
            {
                var row = new Row(r);
                rows.Add((row.Long("id")!.Value, row.Text("role")!, row.Long("parent"), (int)row.Long("ord")!.Value, row.Long("inventory"),
                    (uint)(row.Long("placement") ?? 0), (byte)(row.Long("x") ?? 0), (byte)(row.Long("y") ?? 0), row.Detach()));
            }
        var children = rows.Where(r => r.Parent is not null).ToLookup(r => r.Parent!.Value);
        var built = new Dictionary<long, SavedItem>();
        SavedItem Build((long Id, string Role, long? Parent, int Ord, long? Inventory, uint Placement, byte X, byte Y, Row Values) r)
        {
            var v = r.Values;
            var own = children[r.Id].ToList();
            var gems = own.Where(c => c.Role == "socketed").ToDictionary(c => c.Ord, Build);
            var colours = v.Json<List<SocketColour>>("sockets");
            var original = own.Where(c => c.Role == "original").Select(Build).FirstOrDefault();
            return new SavedItem((uint)v.Long("type")!.Value, (byte)v.Long("item_level")!.Value, (byte)v.Long("quality")!.Value,
                (int)v.Long("charges")!.Value, (ushort)v.Long("stack_size")!.Value,
                colours?.Select((colour, i) => new SavedSocket(colour, gems.GetValueOrDefault(i))).ToList(), v.Json<List<byte>>("links"),
                (int)v.Long("gem_experience")!.Value, (ItemRarity)v.Long("rarity")!.Value, v.Bool("identified")!.Value,
                v.Bool("has_mods") == true ? mods.GetValueOrDefault(r.Id) ?? [] : null, v.Json<List<uint>>("name"),
                (int)v.Long("gem_variant")!.Value, v.Bool("corrupted")!.Value, (byte)v.Long("fractured")!.Value, v.Bool("mirrored")!.Value,
                (byte)v.Long("gem_flags")!.Value, (ushort?)v.Long("visual"), (byte)v.Long("defence_base_roll")!.Value,
                (ItemInfluence)v.Long("influences")!.Value, v.Bool("split")!.Value, v.Bool("synthesised")!.Value, v.Bool("foreseeing")!.Value,
                (uint)v.Long("foresee_seed")!.Value, (byte?)v.Long("alternate_quality_type"), original);
        }
        return [.. rows.Where(r => r.Parent is null).OrderBy(r => r.Inventory).ThenBy(r => r.Ord)
            .Select(r => new ItemRow(r.Id, r.Role, null, r.Ord, r.Inventory, r.Placement, r.X, r.Y, Build(r)))];
    }

    // ---------------------------------------------------------------- the move from JSON

    /// <summary>
    /// Moves the JSON saves (characters, deleted_characters, stashes with stash_tabs) into the save tables, once: each save is
    /// written, read back and compared with the JSON it came from, all in one transaction. A save that can't be read or
    /// does not come back the same stops it (and the Gateway) with nothing changed. The JSON tables are left as they were,
    /// and a copy of the database is made first. Returns how many saves were moved.
    /// </summary>
    public static int Migrate(SaveDatabase db, Action<string>? log = null)
    {
        log ??= Console.WriteLine;
        if (db.Relational) return 0;
        long rows = db.Read(() =>
        {
            using var c = db.Command("SELECT (SELECT count(*) FROM characters) + (SELECT count(*) FROM deleted_characters) + (SELECT count(*) FROM stashes)");
            return (long)c.ExecuteScalar()!;
        });
        if (rows > 0)
        {
            string backup = $"{db.Path}.backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            db.Read(() => { using var c = db.Command("VACUUM INTO $path", ("$path", backup)); return c.ExecuteNonQuery(); });
            log($"[saves] copy of {db.Path} before the saves move into tables: {backup}");
        }
        int moved = db.Atomically(() =>
        {
            int count = 0;
            foreach (var (name, json) in Pairs(db, "SELECT name, save FROM characters ORDER BY name"))
            {
                var save = Parse<Save>(db, json, $"character {name}");
                long id = Insert(db, "INSERT INTO character_saves (name, has_inventories) VALUES ($name, 0) RETURNING id", ("$name", name));
                WriteCharacterRow(db, id, save);
                Same(json, ReadCharacter(db, id), $"character {name}");
                count++;
            }
            foreach (var (name, deleted, json) in Triples(db, "SELECT name, deleted_utc, save FROM deleted_characters ORDER BY rowid"))
            {
                var save = Parse<Save>(db, json, $"deleted character {name}");
                long id = Insert(db, "INSERT INTO character_saves (name, deleted_utc, has_inventories) VALUES ($name, $at, 0) RETURNING id",
                    ("$name", name), ("$at", deleted));
                WriteCharacterRow(db, id, save);
                Same(json, ReadCharacter(db, id), $"deleted character {name}");
                count++;
            }
            foreach (var (account, json) in Pairs(db, "SELECT account, save FROM stashes ORDER BY account"))
            {
                var header = Parse<AccountStash.Save>(db, json, $"stash {account}");
                WriteStash(db, account, header with { Tabs = [.. header.Tabs.Select(t => t with { Items = [] })] });
                var tabRows = Pairs(db, "SELECT tab, items FROM stash_tabs WHERE account = $a ORDER BY tab", ("$a", account))
                    .ToDictionary(t => int.Parse(t.Item1), t => t.Item2);
                // A stash imported from before tabs had rows of their own keeps a tab's items in its header.
                foreach (int tab in Enumerable.Range(0, header.Tabs.Count).Union(tabRows.Keys).Order())
                {
                    var items = tabRows.TryGetValue(tab, out string? tabJson)
                        ? Parse<List<SavedPlacement>>(db, tabJson, $"stash {account} tab {tab}") : header.Tabs[tab].Items;
                    WriteStashTab(db, account, tab, items);
                    Same(JsonSerializer.Serialize(items, Json), LoadStashTab(db, account, tab), $"stash {account} tab {tab}");
                }
                Same(JsonSerializer.Serialize(header with { Tabs = [.. header.Tabs.Select(t => t with { Items = [] })] }, Json),
                    LoadStash(db, account), $"stash {account}");
                count++;
            }
            db.SetMeta(SaveDatabase.RelationalKey, DateTime.UtcNow.ToString("O"));
            return count;
        });
        if (moved > 0) log($"[saves] {moved} save(s) moved into the save tables of {db.Path}; the JSON tables are kept as they were");
        return moved;
    }

    static T Parse<T>(SaveDatabase db, string json, string what) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, Json) ?? throw new JsonException("null"); }
        catch (JsonException e) { throw new GameServer.Core.Saves.UnreadableSaveException(db.Path, $"{what} in {db.Path} can't be read ({e.Message}); nothing was moved", e); }
    }

    /// <summary>Throws unless <paramref name="back"/> is the save of <paramref name="json"/>: the same JSON once object keys
    /// are in order and a property that is null or an empty object is left out (an absent map and an empty one are the
    /// same save).</summary>
    static void Same(string json, object? back, string what)
    {
        string want = Canonical(JsonNode.Parse(json)), got = Canonical(back is null ? null : JsonSerializer.SerializeToNode(back, back.GetType(), Json));
        if (want != got)
            throw new InvalidDataException($"{what} does not come back the same from the save tables; nothing was moved. " +
                $"Saved: {Shorten(want)} Read: {Shorten(got)}");
        static string Shorten(string s) => s.Length > 400 ? s[..400] + "..." : s;
    }

    static string Canonical(JsonNode? node) => Normalize(node)?.ToJsonString() ?? "null";

    static JsonNode? Normalize(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(p => (p.Key, Value: Normalize(p.Value)))
            .Where(p => p.Value is not null && p.Value is not JsonObject { Count: 0 })
            .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, p.Value))),
        JsonArray a => new JsonArray([.. a.Select(Normalize)]),
        JsonValue v when v.GetValueKind() == JsonValueKind.Null => null,
        _ => node?.DeepClone(),
    };

    // ---------------------------------------------------------------- helpers

    static string? JsonOf<T>(T? value) => value is null ? null : JsonSerializer.Serialize(value, Json);

    static object Value(object? value) => value switch
    {
        null => DBNull.Value,
        bool b => b ? 1L : 0L,
        byte or ushort or uint or int or long => Convert.ToInt64(value),
        float f => (double)f,
        _ => value,
    };

    static Dictionary<TKey, TValue>? NullIfEmpty<TKey, TValue>(Dictionary<TKey, TValue> map) where TKey : notnull => map.Count == 0 ? null : map;

    static long Insert(SaveDatabase db, string sql, params (string, object)[] parameters)
    {
        using var c = db.Command(sql, parameters);
        return (long)c.ExecuteScalar()!;
    }

    static void Run(SaveDatabase db, string sql, params (string, object)[] parameters)
    {
        using var c = db.Command(sql, parameters);
        c.ExecuteNonQuery();
    }

    static List<Row> Rows(SaveDatabase db, string sql, long save)
    {
        using var c = db.Command(sql, ("$s", save));
        using var r = c.ExecuteReader();
        var list = new List<Row>();
        while (r.Read()) list.Add(new Row(r).Detach());
        return list;
    }

    static List<(string, string)> Pairs(SaveDatabase db, string sql, params (string, object)[] parameters)
    {
        using var c = db.Command(sql, parameters);
        using var r = c.ExecuteReader();
        var list = new List<(string, string)>();
        while (r.Read()) list.Add((Convert.ToString(r.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!, r.GetString(1)));
        return list;
    }

    static List<(string, string, string)> Triples(SaveDatabase db, string sql)
    {
        using var c = db.Command(sql);
        using var r = c.ExecuteReader();
        var list = new List<(string, string, string)>();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    /// <summary>A row's columns by name; <see cref="Detach"/> copies them out of the reader.</summary>
    sealed class Row
    {
        readonly Dictionary<string, object?> _values;

        public Row(SqliteDataReader r)
        {
            _values = new Dictionary<string, object?>(r.FieldCount, StringComparer.Ordinal);
            for (int i = 0; i < r.FieldCount; i++) _values[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
        }

        public Row Detach() => this;

        public long? Long(string column) => _values[column] switch
        {
            null => null,
            long l => l,
            var v => throw new InvalidDataException($"{column} is {v.GetType().Name}, not an integer"),
        };

        public double? Real(string column) => _values[column] switch { null => null, double d => d, long l => l, var v => throw new InvalidDataException($"{column} is {v}") };

        public bool? Bool(string column) => Long(column) is { } v ? v != 0 : null;

        public string? Text(string column) => _values[column] switch { null => null, string s => s, var v => throw new InvalidDataException($"{column} is {v}") };

        public T? Json<T>(string column) => Text(column) is { } text ? JsonSerializer.Deserialize<T>(text, SaveRows.Json) : default;
    }
}
