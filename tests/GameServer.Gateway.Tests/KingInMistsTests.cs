using GameServer.Core.Protocol.Instance;
using GameServer.Core.World;

namespace GameServer.Gateway.Tests;

public class KingInMistsTests
{
    static readonly Lazy<string> DataFolder = new(() => Path.GetFullPath("data"));
    static readonly Lazy<MonsterCatalog> Monsters = new(() => new MonsterCatalog(DataFolder.Value));

    [Fact]
    public void The_king_encounter_data_matches_the_official_capture()
    {
        // Both forms, their level-83 map-boss life and the 30% humanoid floor / treant grace.
        var boss = Monsters.Value.Find(KingInMists.Boss2);
        Assert.NotNull(boss);
        Assert.True(KingInMists.IsBoss2(boss));
        Assert.False(KingInMists.IsBoss3(boss));
        var scaling = Monsters.Value.MapBossScaling(83);
        Assert.Equal(20.73, scaling.Life, 2);
        Assert.Equal(1.15, scaling.Damage, 2);
        int baseLife = Monsters.Value.BaseLife(boss!, 83)!.Value;
        Assert.Equal(275_742, baseLife);
        long life = (long)Math.Round(MonsterCatalog.LifeWithRarity(baseLife, MonsterRarity.Unique) * scaling.Life);
        Assert.InRange(life, 45_614_000, 45_615_000);

        var treant = Monsters.Value.Find(KingInMists.Boss3);
        Assert.NotNull(treant);
        Assert.True(KingInMists.IsBoss3(treant));
        Assert.True(treant!.BossHealthBar);
        int treantBaseLife = Monsters.Value.BaseLife(treant, 83)!.Value;
        long treantLife = (long)Math.Round(MonsterCatalog.LifeWithRarity(treantBaseLife, MonsterRarity.Unique) * scaling.Life);
        Assert.InRange(treantLife, 26_608_000, 26_609_000);

        var humanoid = new SpawnedMonster("The King in the Mists", new WalkEstimator(10, 10, 30, null, 300, 300), 135, 4, 1000)
        {
            Life = 1000, KingPhaseLocked = true,
        };
        var (floor, starts) = Game.TakeDamageLocked(humanoid, 900);
        Assert.Equal(300u, floor);
        Assert.False(starts);
        humanoid.Life = floor;
        (floor, _) = Game.TakeDamageLocked(humanoid, 500);
        Assert.Equal(300u, floor);

        var grace = new SpawnedMonster("The King in the Mists", new WalkEstimator(10, 10, 30, null, 300, 300), 135, 4, 1000)
        {
            Life = 700, KingGrace = true,
        };
        (uint graced, bool graceStarts) = Game.TakeDamageLocked(grace, 500);
        Assert.Equal(700u, graced);
        Assert.False(graceStarts);

        // The Mystic Fetish is a stationary, supported shrine with the active state, and the official maze actors exist.
        var fetish = Monsters.Value.Find(KingInMists.MysticFetish);
        Assert.NotNull(fetish);
        Assert.True(KingInMists.IsFetish(fetish));
        Assert.False(KingInMists.IsBoss(fetish));
        Assert.True(fetish!.Supported);
        Assert.Equal(0, fetish.MovementSpeed);
        Assert.Contains("StateMachine", fetish.ComponentsOrBase);
        Assert.NotNull(fetish.StateMachine);
        Assert.Contains("active", fetish.StateMachine!.Names!);
        Assert.True(Monsters.Value.Find("Metadata/Monsters/LeagueAzmeri/GuidingLight/GuidingLightDaemonMaze_")?.Supported);
        Assert.True(Monsters.Value.Find("Metadata/Monsters/LeagueAzmeri/Werewolves/WerewolfMaze")?.Supported);
        Assert.True(Monsters.Value.Find("Metadata/Monsters/LeagueAzmeri/Spiders/MeleeSpiderMaze")?.Supported);

        // The guaranteed unique pool, the paired That Which Was Taken and its four Domain 35 charm modifiers.
        var items = new ItemCatalog(DataFolder.Value);
        var uniques = new UniqueCatalog(items, DataFolder.Value);
        var drops = new ItemDrops(items, new ItemRolls(items), DataFolder.Value, uniques: uniques);
        var counts = new Dictionary<uint, int>();
        var extras = new HashSet<string>(StringComparer.Ordinal);
        var random = new Random(12345);
        int paired = 0;
        for (int i = 0; i < 1000; i++)
        {
            var rolled = drops.KingInMists(83, random);
            var unique = rolled.Where(d => d.Item.Rarity == ItemRarity.Unique).ToList();
            var primary = unique.Where(d => d.Type.Id != "Metadata/Items/Jewels/JewelStr").ToList();
            Assert.Single(primary);
            counts[primary[0].Item.TypeHash] = counts.GetValueOrDefault(primary[0].Item.TypeHash) + 1;
            bool light = primary[0].Type.Id == "Metadata/Items/Jewels/JewelPrismatic";
            int taken = unique.Count(d => d.Type.Id == "Metadata/Items/Jewels/JewelStr");
            Assert.Equal(light ? 1 : 0, taken);
            if (light) paired++;
            foreach (var drop in rolled.Where(d => d.Item.Rarity != ItemRarity.Unique)) extras.Add(drop.Type.Name);
        }
        Assert.Equal(4, counts.Count);
        Assert.All(counts.Values, count => Assert.True(count > 0));
        Assert.True(paired > 150);
        Assert.Contains("Hextoad Support", extras);
        Assert.Contains("Hexpass Support", extras);
        Assert.Contains("The Endless Darkness", extras);

        var thatWhichWasTaken = uniques.Create("That Which Was Taken", new Random(7));
        Assert.NotNull(thatWhichWasTaken);
        Assert.Equal(ItemRarity.Unique, thatWhichWasTaken!.Rarity);
        Assert.Equal("Metadata/Items/Jewels/JewelStr", items.ByHash(thatWhichWasTaken.TypeHash)!.Id);
        var explicitMods = thatWhichWasTaken.Mods.Where(m => m.Section == ModSection.Explicit).ToList();
        Assert.Equal(4, explicitMods.Count);
        Assert.All(explicitMods, m => Assert.Equal(35, items.Mods.ByHash(m.Hash)!.Domain));
        Assert.Equal(4, explicitMods.Select(m => items.Mods.ByHash(m.Hash)!.Families.FirstOrDefault()).Distinct().Count());
    }
}
