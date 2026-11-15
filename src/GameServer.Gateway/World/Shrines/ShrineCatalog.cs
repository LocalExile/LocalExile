using GameServer.Core;

/// <summary>
/// The shrine definitions of the client data: a <c>Shrines</c> row's <c>Player_ShrineBuffsKey</c> is a BuffTemplates row,
/// whose <c>BuffDefinitionsKey</c> is the BuffDefinitions row the client shows, and whose <c>Buff_StatValues</c> are that
/// definition's <c>GrantedStats</c> values, in order (a Stats row + 1 is the client key). Duration, the lesser flag, shared
/// charges, the PVP flag, the sound and the visual variations are the Shrines row's own. A template whose values do not line
/// up with its definition's stats is skipped rather than misapplied.
/// <para><c>Monster_ShrineBuffsKey</c> is read the same way: the shrine's buff on the monsters it guards, which is not the
/// player's buff (Acceleration is −60 action speed and 100 projectile speed against the player's −50 and 80; Massive 60% life
/// and 155% area against 40 and 40). <c>MonsterPacks</c> names the pack rows that guard the shrine: only the boss and
/// influence shrines have them in the data; the standard map shrines are guarded by the area's own packs.</para>
/// <para>This is data only. What a shrine does with it is the shrine module's behaviour and effect for its id
/// (<c>ShrineRegistry</c>), so a new shrine type is a row plus, at most, one behaviour or effect registration.</para>
/// </summary>
public static class ShrineCatalog
{
    /// <summary>A pack row that guards a shrine (Shrines.MonsterPacks → MonsterPacks): its row, Id, spawn count and the
    /// MonsterVarieties rows it rolls (MonsterPackEntries: MonsterPacksKey → MonsterVarietiesKey, with a weight).</summary>
    public sealed record GuardPack(int Row, string Id, int MinCount, int MaxCount,
        IReadOnlyList<(ushort Variety, int Weight)> Monsters);

    /// <summary>The buff a shrine puts on monsters: its BuffTemplates row and Id, the BuffDefinitions row, and values.</summary>
    public sealed record MonsterBuff(ushort TemplateRow, string Template, ushort BuffRow, string BuffId,
        IReadOnlyList<(uint Key, int Value)> Stats);

    /// <summary>One shrine type: its Shrines row, the buff it grants the player and monsters, its guards, and the visual
    /// variations and sound the row names.</summary>
    public sealed record ShrineDefinition(int Row, string Id, string Name, int Duration, bool Lesser, bool ChargesShared,
        bool PvPOnly, int Sound, ushort BuffRow, string BuffId, IReadOnlyList<(uint Key, int Value)> Stats,
        MonsterBuff? Monster, IReadOnlyList<GuardPack> GuardPacks, IReadOnlyList<int> Visuals);

    static readonly Lazy<(ShrineDefinition[] Rows, string[] Skipped)> Data = new(Load);

    /// <summary>Every shrine type, by Shrines row. Rows without a resolvable player buff template are left out.</summary>
    public static IReadOnlyList<ShrineDefinition> All => Data.Value.Rows;

    /// <summary>The Shrines rows left out because their template values did not match, for the log.</summary>
    public static IReadOnlyList<string> Skipped => Data.Value.Skipped;

    /// <summary>The shrine type with this Shrines row id (Acceleration, GodMode, …), or null.</summary>
    public static ShrineDefinition? Find(string id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>The basic shrines, by Shrines row id: the fifteen of the wiki's list. Several data ids differ from the
    /// names the wiki uses (Diamond is Criticals, Echoing Multi, Divine GodMode, Freezing Cold, Impenetrable Defence,
    /// Replenishing Regen, Shrouded Smoke, Skeletal Skeletons). The legacy rows (Fire, Arc, Thorns, Curses, Greed, Vaal,
    /// UpgradeOnKill), the PVP, CTF, Royale, blood and Evolving ones and the apparition and boss shrines are not in it.</summary>
    static readonly string[] BasicIds =
    [
        "Acceleration", "Lightning", "Cold", "Criticals", "Brutal", "Regen", "Massive", "Defence", "Resistance",
        "GodMode", "Multi", "Skeletons", "Smoke", "Resonating", "Gloom",
    ];

    /// <summary>The apparition shrines, each guarded by one of the Elderslayers' or the Pantheon's apparitions: they come
    /// with the Domination Scarab of Apparitions, which is not implemented yet, so a map does not roll them.</summary>
    public static readonly string[] ApparitionIds =
        ["BrineKing", "Lunaris", "Solaris", "Arakaali", "Warlord", "Crusader", "Redeemer", "Hunter"];

    /// <summary>Every type a map may roll: the basic shrines. A lesser shrine is The Gull's Create Lesser Shrine skill
    /// and a greater one is a summoned shrine daemon (its Shrines row has Duration 0), so neither is a map shrine room's
    /// type; the apparition shrines wait for their scarab (<see cref="ApparitionIds"/>).</summary>
    public static IReadOnlyList<ShrineDefinition> Standard => [.. All.Where(s => BasicIds.Contains(s.Id))];

    static (ShrineDefinition[], string[]) Load()
    {
        var skipped = new List<string>();
        if (new[] { "Shrines", "BuffTemplates", "BuffDefinitions" }.Any(t => !ClientTables.Has(t)))
            return ([], [.. skipped]);

        var shrines = ClientTables.Table("Shrines");
        var templates = ClientTables.Table("BuffTemplates");
        var definitions = ClientTables.Table("BuffDefinitions");
        var packs = ClientTables.TableOrNull("MonsterPacks");
        var entries = ClientTables.TableOrNull("MonsterPackEntries");
        var strings = ClientTables.TableOrNull("ClientStrings");
        // The visual variations a shrine row names (ShrineVisualArtVariations.Shrine → Visual), in table order.
        var visuals = new Dictionary<int, List<int>>();
        if (ClientTables.TableOrNull("ShrineVisualArtVariations") is { } variations)
            foreach (var v in variations)
                if (v.IntOrNull("Shrine") is int shrineRow && v.IntOrNull("Visual") is int visual)
                {
                    if (!visuals.TryGetValue(shrineRow, out var list)) visuals[shrineRow] = list = [];
                    list.Add(visual);
                }
        var packMonsters = new Dictionary<int, List<(ushort Variety, int Weight)>>();
        if (entries is not null)
            foreach (var e in entries)
            {
                if (e.IntOrNull("MonsterPacksKey") is not int p || e.IntOrNull("MonsterVarietiesKey") is not int v) continue;
                if (!packMonsters.TryGetValue(p, out var list)) packMonsters[p] = list = [];
                list.Add(((ushort)v, e.IntOrNull("Weight") ?? 0));
            }

        // A template row → its definition row, Id and the definition's GrantedStats with the template's values.
        (ushort Row, string Id, List<(uint Key, int Value)> Stats)? Resolve(int templateRow, string shrineId, string which)
        {
            if (templateRow < 0 || templateRow >= templates.Count)
            {
                skipped.Add($"{shrineId}: {which} template row {templateRow} out of range");
                return null;
            }
            var t = templates[templateRow];
            if (t.IntOrNull("BuffDefinitionsKey") is not int definitionRow)
            {
                skipped.Add($"{shrineId}: {which} template {templateRow} has no definition");
                return null;
            }
            if (definitionRow < 0 || definitionRow >= definitions.Count)
            {
                skipped.Add($"{shrineId}: {which} definition row {definitionRow} out of range");
                return null;
            }
            var d = definitions[definitionRow];
            var granted = d.Ints("GrantedStats");
            var values = t.Ints("Buff_StatValues");
            if (granted.Length != values.Length)
            {
                skipped.Add($"{shrineId}: {which} {granted.Length} stat(s) against {values.Length} value(s)");
                return null;
            }
            return ((ushort)definitionRow, d.TextOrNull("Id") ?? "",
                [.. granted.Zip(values, (stat, value) => ((uint)(stat + 1), value))]);
        }

        var rows = new List<ShrineDefinition>();
        foreach (var r in shrines)
        {
            if (r.TextOrNull("Id") is not { Length: > 0 } id) continue;
            if (r.IntOrNull("Player_ShrineBuffsKey") is not int key)
            {
                skipped.Add($"{id}: no player buff template");
                continue;
            }
            if (Resolve(key, id, "player") is not { } player)
            {
                skipped.Add($"{id}: player buff does not resolve");
                continue;
            }

            MonsterBuff? monster = null;
            if (r.IntOrNull("Monster_ShrineBuffsKey") is int mr && mr is >= 0 and <= ushort.MaxValue
                && Resolve(mr, id, "monster") is { } resolved)
                monster = new MonsterBuff((ushort)mr, templates[mr].TextOrNull("Id") ?? "",
                    resolved.Row, resolved.Id, resolved.Stats);

            var guards = new List<GuardPack>();
            if (packs is not null)
                foreach (int gr in r.Ints("MonsterPacks"))
                {
                    if (gr < 0 || gr >= packs.Count) continue;
                    var p = packs[gr];
                    guards.Add(new GuardPack(gr, p.TextOrNull("Id") ?? "",
                        p.IntOrNull("MinCount") ?? 0, p.IntOrNull("MaxCount") ?? 0,
                        packMonsters.GetValueOrDefault(gr) ?? []));
                }

            rows.Add(new ShrineDefinition(r.Index, id, Name(id, r, strings), r.IntOrNull("Duration") ?? 0,
                r.BoolOrNull("IsLesserShrine") ?? false, r.BoolOrNull("ChargesShared") ?? false,
                r.BoolOrNull("IsPVPOnly") ?? false, r.IntOrNull("ShrineSoundsKey") ?? 0,
                player.Row, player.Id, player.Stats, monster, guards,
                visuals.GetValueOrDefault(r.Index) ?? []));
        }
        return ([.. rows], [.. skipped]);

        // The row's Name is a ClientStrings row; the id stands in when the string is missing.
        static string Name(string id, ClientRow r, ClientTable? strings) =>
            strings is not null && r.IntOrNull("Name") is int nameRow && strings.Row(nameRow)?.TextOrNull("Text") is { Length: > 0 } text
                ? text : id;
    }
}
