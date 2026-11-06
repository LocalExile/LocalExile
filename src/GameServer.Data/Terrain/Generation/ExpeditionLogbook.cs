namespace GameServer.Data.Terrain.Generation;

/// <summary>An Expedition LOGBOOK area's terrain features (<c>0x141f362c0</c>, called by the Expedition plugin's feature
/// registration <c>0x141f36940</c> when the plugin's flag byte +0x18 has bit 1; a plain map or act area registers the
/// single ExpeditionEncounter row instead). In word order: the ExpeditionBalancePerLevel row (byte +4 = 1, the greatest
/// level at or below the area level: +0x1d the boss chance in 10 000ths, +0x21 / +0x25 the min / max feature count);
/// unless stat 14744, <c>Next(10000)</c> below the chance = a boss (an "exclusive" row); count = stat 14803 + min + one
/// direct <c>Uniform(max − min + 1)</c> (none when max = min); the ExpeditionTerrainFeatures rows in table order whose
/// faction (+0x18, none = any) is the logbook's, whose level range (+0x28..+0x2c) holds the level and whose +0x44 area
/// list is empty or names this area's ExpeditionAreas row; a +0x58 row is a boss row (weight +0x54 into the boss list
/// when the boss rolled), else a normal row (when the count is above 0 and its +0x30 max above 0); the boss:
/// <c>Next(sum)</c> and a cumulative pick, then count × <c>Next(sum)</c> over the normal rows, a row that reached its
/// max leaving the list. Every pick's ExtraTerrainFeatures row (+8) is registered in that order, boss first, and a
/// row's side-area world area (+0x34) is queued as a sub-area (the Bluffs' UndergroundSand beside the site). The
/// faction is the logbook item's (the server corpus fits it from the logged bounds). Verified on the corpus's
/// Utzaal Outskirts and Dried Riverbed logbooks.</summary>
public static class ExpeditionLogbook
{
    /// <summary>A pseudo feature naming a world area row to queue as a sub-area (the pool ignores "(" names).</summary>
    public const string SubArea = "(subarea)";

    public static void Roll(AreaInfo area, LevelRandom rng, int faction, List<ExtraTerrainFeatures.Feature> features)
    {
        if (area.Table is not { } table) return;
        var balance = table("ExpeditionBalancePerLevel");
        int chance = 0, lo = 0, hi = 0, bestLevel = int.MinValue;
        for (int r = 0; r < balance.Count; r++)
        {
            if (balance.U8(r, 4) != 1) continue;
            int level = balance.I32(r, 0);
            if (level <= area.Level && level > bestLevel)
            {
                bestLevel = level;
                chance = balance.I32(r, 0x1d);
                lo = balance.I32(r, 0x21);
                hi = balance.I32(r, 0x25);
                if (level == area.Level) break;
            }
        }
        bool boss = true;
        if (area.Stat(14744) == 0 && (chance <= 0 || (chance < 10000 && rng.Next(10000u) >= (uint)chance))) boss = false;
        if (hi < lo) hi = lo;
        int count = area.Stat(14803) + lo + (hi > lo ? (int)rng.Uniform((uint)(hi - lo + 1)) : 0);
        if (!boss && count == 0) return;

        var areas = table("ExpeditionAreas");
        var worldAreas = table("WorldAreas");
        int areaIndex = -1;
        for (int r = 0; r < areas.Count; r++)
            if (areas.Ref8(r, 0) is var w and >= 0 && w < worldAreas.Count && worldAreas.String(w, 0) == area.Id) { areaIndex = r; break; }

        var rows = table("ExpeditionTerrainFeatures");
        var etf = table("ExtraTerrainFeatures");
        var normal = new List<int>();
        var exclusive = new List<int>();
        for (int r = 0; r < rows.Count; r++)
        {
            int rowFaction = rows.Ref8(r, 0x18);
            if (rowFaction >= 0 && rowFaction != faction) continue;
            if (area.Level < rows.I32(r, 0x28) || area.Level > rows.I32(r, 0x2c)) continue;
            var list = rows.RefList(r, 0x44);   // ExpeditionAreas keys, 16-byte foreign refs
            if (list.Length != 0 && !list.Contains(areaIndex)) continue;
            int weight = rows.I32(r, 0x54);
            if (rows.Bool(r, 0x58)) { if (boss && weight != 0) exclusive.Add(r); }
            else if (count != 0 && rows.I32(r, 0x30) > 0 && weight != 0) normal.Add(r);
        }
        var picks = new List<int>();
        int Weighted(List<int> list, uint draw)
        {
            foreach (int r in list)
            {
                int w = rows.I32(r, 0x54);
                if ((uint)w > draw) return r;
                draw -= (uint)w;
            }
            return list[^1];
        }
        if (boss && exclusive.Count != 0) picks.Add(Weighted(exclusive, rng.Next((uint)exclusive.Sum(r => rows.I32(r, 0x54)))));
        for (int i = 0; i < count; i++)
        {
            if (normal.Count == 0) break;
            int row = Weighted(normal, rng.Next((uint)normal.Sum(r => rows.I32(r, 0x54))));
            picks.Add(row);
            if (picks.Count(p => p == row) >= rows.I32(row, 0x30)) normal.Remove(row);
        }
        foreach (int row in picks)
        {
            int feature = rows.Ref8(row, 8);
            if (feature >= 0 && feature < etf.Count) features.Add(new(etf.String(feature, 0), 1));
            int side = rows.Ref8(row, 0x34);
            if (side > 0) features.Add(new(SubArea, side));
        }
    }
}
