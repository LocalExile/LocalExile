using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServer.Core.Protocol.Instance;
using Xunit.Abstractions;

namespace GameServer.Gateway.Tests;

/// <summary>Every unique boss of the campaign (WorldAreas Bosses of the act 1–10 areas) stands exactly once in the
/// Gateway's own generation of the area (ServerLevels → AreaPopulation.FitRooms → AreaPopulation.Plan, with the map
/// boss override for a character without bandit flags), at the seeds of
/// Game_BOSS_CHECK_SEEDS (default "1,2,3,7"), and the monster blob can write its variety. A boss missing without a
/// reason in <see cref="Unplaced"/>, a unique standing twice or not in the list, or a variety the blob refuses fails
/// it; later forms (AreaPopulation.LaterForms) are reported. Needs the game files (Client/Content.ggpk, Tools/ooz) and
/// generates ~90 areas per seed (minutes), so it is skipped unless Game_BOSS_CHECK=1:
/// <c>$env:Game_BOSS_CHECK='1'; dotnet test tests/GameServer.Gateway.Tests --filter CampaignBossPlacement</c>
/// (Game_BOSS_CHECK_AREAS: an area id regex; Game_BOSS_CHECK_REPORT: a file for the report, which is also the test
/// output). 14-monster-packs.md §3.</summary>
public sealed class CampaignBossPlacementTests(ITestOutputHelper output)
{
    sealed class BossCheckFactAttribute : FactAttribute
    {
        public BossCheckFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("Game_BOSS_CHECK") != "1") Skip = "generates every campaign boss area: set Game_BOSS_CHECK=1";
        }
    }

    [BossCheckFact]
    public void Every_campaign_boss_stands_once()
    {
        Assert.True(File.Exists("Client/Content.ggpk"), "Game_BOSS_CHECK=1 needs the game files (Client/Content.ggpk)");
        uint[] seeds = [.. (Environment.GetEnvironmentVariable("Game_BOSS_CHECK_SEEDS") ?? "1,2,3,7").Split(',').Select(uint.Parse)];
        string? only = Environment.GetEnvironmentVariable("Game_BOSS_CHECK_AREAS");
        var (areas, names, ids, plain) = CampaignBossAreas();
        var packs = new PackCatalog();
        var report = new StringBuilder();
        var missing = new List<string>();
        var extra = new List<string>();
        var unsupported = new SortedSet<string>();
        foreach (var (areaId, bosses) in areas)
        {
            if (only is not null && !Regex.IsMatch(areaId, only)) continue;
            foreach (uint asked in seeds)
            {
                uint seed = ServerLevels.PlayableSeed(areaId, asked);
                var notes = new List<string>();
                var level = ServerLevels.For(areaId, seed);
                if (level is null) { report.AppendLine($"{areaId} seed {asked}: not generated ({(ServerLevels.IsSupported(areaId) ? ServerLevels.Error(areaId, seed)?.Split('\n')[0] : "client-side area")})"); continue; }
                var (points, objects, _) = AreaPopulation.FitRooms(areaId, seed, notes, "data", generated: true);
                var area = Area(packs.For(areaId)!, ids, plain);
                points.AddRange(AreaPopulation.SpawnerPoints(area, objects, v => ids.GetValueOrDefault(v)));
                var spawns = AreaPopulation.Plan(area, level.Width, level.Height, (x, y) => x >= 0 && y >= 0 && x < level.Width && y < level.Height && level.Walk[y * level.Width + x] >= 2,
                    (0, 0), [], points, 1, _ => true, notes);
                var uniques = spawns.Where(s => s.Rarity == MonsterRarity.Unique).ToList();
                var lines = new List<string>();
                var left = uniques.ToList();
                foreach (int b in bosses.Where((_, i) => area.Absent.Contains(i)))
                {
                    lines.Add($"{names[b]} left out");
                    if (!LeftOut.Contains(ids[b])) missing.Add($"{areaId} seed {asked}: {names[b]} left out, but not a known later form");
                }
                foreach (int boss in bosses.Where((_, i) => !area.Absent.Contains(i)))
                {
                    var hit = left.FirstOrDefault(s => s.Variety == boss);
                    if (hit is not null)
                    {
                        left.Remove(hit);
                        bool made = Program.Monsters.Find(boss.ToString()) is { Supported: true };
                        lines.Add($"{names[boss]} at {hit.Pack} ({hit.X}, {hit.Y}){(made ? "" : $" CANNOT BE CREATED ({string.Join(",", Program.Monsters.Find(boss.ToString())?.ComponentsOrBase ?? [])})")}");
                        if (!made) unsupported.Add($"{names[boss]}: {string.Join(",", Program.Monsters.Find(boss.ToString())?.ComponentsOrBase ?? [])}");
                    }
                    else if (Unplaced.TryGetValue((areaId, ids[boss]), out var why)) lines.Add($"{names[boss]} not placed: {why}");
                    else { lines.Add($"{names[boss]} MISSING"); missing.Add($"{areaId} seed {asked}: {names[boss]}"); }
                }
                lines.AddRange(left.Select(s => $"extra {names.GetValueOrDefault(s.Variety, s.Variety.ToString())} at {s.Pack}"));
                extra.AddRange(left.Select(s => $"{areaId} seed {asked}: {names.GetValueOrDefault(s.Variety, s.Variety.ToString())} at {s.Pack}"));
                if (lines.Any(l => l.EndsWith("MISSING", StringComparison.Ordinal) || l.StartsWith("extra ", StringComparison.Ordinal)))
                    lines.AddRange(notes.Where(n => n.Contains("boss", StringComparison.OrdinalIgnoreCase)).Select(n => $"note: {n}"));
                report.AppendLine($"{areaId} seed {asked}{(seed != asked ? $" (played {seed})" : "")}: {string.Join("; ", lines)} " +
                    $"[points: {string.Join(", ", points.Where(p => p.Name.Contains("boss", StringComparison.OrdinalIgnoreCase)).Select(p => p.Name).Distinct())}]");
            }
        }
        report.AppendLine($"varieties the monster blob cannot write: {unsupported.Count}");
        foreach (var u in unsupported) report.AppendLine($"  {u}");
        output.WriteLine(report.ToString());
        if (Environment.GetEnvironmentVariable("Game_BOSS_CHECK_REPORT") is { Length: > 0 } file) File.WriteAllText(file, report.ToString());
        Assert.True(missing.Count == 0 && extra.Count == 0 && unsupported.Count == 0,
            $"{missing.Count} boss(es) without a placement:\n{string.Join("\n", missing)}\n" +
            $"{extra.Count} unique(s) beyond the area's bosses:\n{string.Join("\n", extra)}\n" +
            $"{unsupported.Count} boss varieties the monster blob cannot write:\n{string.Join("\n", unsupported)}");
    }

    /// <summary>The area as PopulateArea plans it: its bosses' later forms left to their fight, and The Ashen Fields'
    /// boss for a character without the Act 2 bandit flags (as /goto gives): Greust alone.</summary>
    static PackCatalog.AreaPacks Area(PackCatalog.AreaPacks area, Dictionary<int, string> ids, Dictionary<int, string> names) =>
        MapBossOverride.Apply(AreaPopulation.LaterForms(area, v => ids.TryGetValue(v, out var id) ? (id, names[v]) : null),
            new HashSet<string>(), v => ids.GetValueOrDefault(v));

    /// <summary>The campaign bosses the Gateway does not place yet, and why (14-monster-packs.md §3). Areas the Gateway does
    /// not generate (1_2_3, 2_6_1, 2_6_5, 2_6_12: "client-side area") are only reported: they need a recorder capture.</summary>
    static readonly Dictionary<(string Area, string Variety), string> Unplaced = new()
    {
        [("1_3_5", "Metadata/Monsters/Statue/StoneStatueLargeBossMale")] =
            "a tile's statue spawner (Spawner_StoneStatueLargeMaleBoss) that wakes as the player comes near; tile spawners are not among the generated quest objects",
        [("1_3_8_2", "Metadata/Monsters/Guardians/GuardianBossHeadFire")] = Banners,
        [("1_3_8_2", "Metadata/Monsters/Guardians/GuardianBossHeadCold")] = Banners,
        [("1_3_8_2", "Metadata/Monsters/Guardians/GuardianBossHeadLightning")] = Banners,
        [("1_3_15", "Metadata/Monsters/Spiker/SpikerBoss")] =
            "its pack 1_3_15-bosspack (BossMonster Thistlesage) is named by no room or tile of the area's room set",
    };

    /// <summary>The bosses the area leaves out: to their fight (AreaPopulation.LaterForms), and the bandit lords The
    /// Ashen Fields' map boss override does not meet without bandit flags; any other boss left out fails.</summary>
    static readonly HashSet<string> LeftOut =
    [
        "Metadata/Monsters/Abberath/Abberath2", "Metadata/Monsters/Shakari/Shakari2", "Metadata/Monsters/Malachai/MalachaiBoss2",
        "Metadata/Monsters/UnholyTrio/UnholyTrio",
        "Metadata/Monsters/Ralakesh/Oak/Oak", "Metadata/Monsters/Ralakesh/Alira/Alira", "Metadata/Monsters/Ralakesh/Kraityn/Kraityn",
    ];

    const string Banners = "only tencent_lunaris_boss_1.arm has their point, a room the layouts never place (the area's unique is The Goddess of Purity, a gold statue spawner)";

    /// <summary>The act 1–10 areas with unique bosses (WorldAreas Id "1_…"/"2_…", Bosses_MonsterVarietiesKeys) and the
    /// varieties' names (with their Id's last part), Ids and plain names.</summary>
    static (List<(string Id, int[] Bosses)> Areas, Dictionary<int, string> Names, Dictionary<int, string> Ids, Dictionary<int, string> Plain) CampaignBossAreas()
    {
        var areas = new List<(string, int[])>();
        using (var doc = JsonDocument.Parse(File.ReadAllBytes("data/tables/WorldAreas.json")))
            foreach (var a in doc.RootElement.EnumerateArray())
            {
                string id = a.GetProperty("Id").GetString() ?? "";
                int[] bosses = [.. a.GetProperty("Bosses_MonsterVarietiesKeys").EnumerateArray().Select(b => b.GetInt32())];
                if (Regex.IsMatch(id, @"^[12]_\d+_") && bosses.Length > 0 && !a.GetProperty("IsTown").GetBoolean()) areas.Add((id, bosses));
            }
        var names = new Dictionary<int, string>();
        var ids = new Dictionary<int, string>();
        var plain = new Dictionary<int, string>();
        using (var doc = JsonDocument.Parse(File.ReadAllBytes("data/tables/MonsterVarieties.json")))
        {
            int row = 0;
            foreach (var v in doc.RootElement.EnumerateArray())
            {
                ids[row] = v.GetProperty("Id").GetString() ?? "";
                plain[row] = v.GetProperty("Name").GetString() ?? "";
                names[row] = $"{plain[row]} ({ids[row].Split('/')[^1]})";
                row++;
            }
        }
        return (areas, names, ids, plain);
    }
}
