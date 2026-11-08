namespace GameServer.Data.Terrain.Generation;

/// <summary>The BetrayalSafehousePlugin's part in the generation (<c>0x141f30020</c>, run on a Syndicate safehouse's Arena
/// child graph, <c>Leagues/Betrayal/SafeHouses/Arena/Graphs/_SafeHouse_*.dgr</c>, before its exist rolls): the server
/// sends the safehouse's roster in 0x1d's TerrainPluginState (8-byte slots after a lead byte; byte 0 = rank | job &lt;&lt;
/// 4, byte 1 = the BetrayalTargets row). The graph's "targetroom1" node (the last one) and its "targetroom2" nodes in
/// node order are collected, draw(#targetroom2) at <c>0x141f30164</c> then the hidden forward Fisher-Yates over the
/// targetroom2 nodes, targetroom1 in front; the n-th node takes the n-th member: its tag becomes the member's Arena
/// room's (BetrayalTargets +0x59, room_&lt;member&gt;.arm, "targetroom_&lt;member&gt;"; the Arena room set already
/// lists every member room at 100, so the chooser draws draw(100) over the one candidate). A node beyond the roster,
/// or whose member has no room (Catarina), gets exist 0 and vanishes without a roll. The server corpus verified it on
/// the Assassins 226142710 (Tora, Leo, Vagan) and the Laboratory 1555932308 (four members under a lead byte of 3).</summary>
public sealed class SafehouseRooms(IReadOnlyList<int> rows)
{
    /// <summary>No roster: every target room drops (the draw and the shuffle still happen).</summary>
    public static readonly SafehouseRooms None = new([]);

    public void Apply(TileGraph graph, TerrainCache files, LevelRandom rng, List<string> notes)
    {
        var first = graph.Nodes.LastOrDefault(n => n.Tag == "targetroom1");
        var rest = graph.Nodes.Where(n => n.Tag == "targetroom2").ToList();
        if (first is null && rest.Count == 0) return;
        if (rest.Count != 0)
        {
            rng.Next(rest.Count);
            rng.Shuffle(rest);
        }
        if (first is not null) rest.Insert(0, first);
        if (files.Raw("Data/BetrayalTargets.datc64") is not { } data) throw new FileNotFoundException("Data/BetrayalTargets.datc64");
        var targets = new RawDat(data);
        var taken = new List<string>();
        for (int k = 0; k < rest.Count; k++)
        {
            int row = k < rows.Count ? rows[k] : -1;
            string arm = row >= 0 && row < targets.Count ? targets.String(row, 0x59) : "";
            if (arm.Length == 0)
            {
                rest[k].Exist = 0;
                continue;
            }
            rest[k].Tag = (files.Room(arm) ?? throw new FileNotFoundException(arm)).Tag;
            taken.Add(targets.String(row, 0));
        }
        notes.Add($"safehouse target rooms: [{string.Join(", ", taken)}], {rest.Count - taken.Count} node(s) without a member");
    }
}
