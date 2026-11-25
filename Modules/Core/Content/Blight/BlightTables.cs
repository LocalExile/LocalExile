using GameServer.Core;

namespace GameServer.Modules.Blight;

/// <summary>
/// The client tables a defence reads, built once when the module loads (<see cref="BlightModule.LoadData"/>): the towers
/// (BlightTowers, with what each costs and which variety it is in BlightTowersPerLevel), the lane trees (BlightTopologies and
/// BlightTopologyNodes) and what a defeated enemy gives (BlightBalancePerLevel).
/// </summary>
public static class BlightTables
{
    /// <summary>One BlightTowers row; <see cref="Row"/> is what C→S 0x265 names. Row 0 is the empty foundation.</summary>
    public sealed record Tower(int Row, string Id, string Name, string Tier, int Radius, IReadOnlyList<int> Next, int Cost, int Variety);

    public static IReadOnlyList<Tower> Towers { get; private set; } = [];

    /// <summary>The resource a defeated enemy gives by rarity (normal, magic, rare, unique): the first BlightBalancePerLevel row's list.</summary>
    public static IReadOnlyList<int> KillResource { get; private set; } = [];

    public static IReadOnlyList<BlightLayout.Topology> Topologies { get; private set; } = [];
    public static IReadOnlyList<BlightLayout.Node> Nodes { get; private set; } = [];

    public static void Load()
    {
        var perLevel = new Dictionary<int, (int Cost, int Variety)>();
        foreach (var r in ClientTables.TableOrEmpty("BlightTowersPerLevel"))
            perLevel[r.Int("BlightTowersKey")] = (r.Int("Cost"), r.Int("MonsterVarietiesKey"));
        Towers = [.. ClientTables.TableOrEmpty("BlightTowers").Select(r =>
        {
            var (cost, variety) = perLevel.GetValueOrDefault(r.Index, (0, -1));
            return new Tower(r.Index, r.Text("Id") ?? "", r.Text("Name") ?? "", r.Text("Tier") ?? "", r.Int("Radius"), r.Ints("NextUpgradeOptions"), cost, variety);
        })];
        var balance = ClientTables.TableOrEmpty("BlightBalancePerLevel");
        KillResource = balance.Any() ? balance.First().Ints("__col2") : [];
        var topologies = ClientTables.TableOrEmpty("BlightTopologies");
        Nodes = BlightLayout.ReadNodes(ClientTables.TableOrEmpty("BlightTopologyNodes"), [.. topologies.Select(r => r.Int("BlightTopologyNodesKey"))]);
        Topologies = [.. topologies.Select(r => new BlightLayout.Topology(r.Index, r.Text("Id") ?? "", r.Int("BlightTopologyNodesKey"),
            r.Int("__col2"), r.Int("__col3"), r.Int("__col4"), r.Int("__col6"))).Where(t => t.Root >= 0 && t.Root < Nodes.Count)];
    }
}
