using GameServer.Core;
using GameServer.Core.Protocol.Instance;

static class DivinationBlueprintRewards
{
    internal static Item? Create(Item item, ItemCatalog.BaseType type, Random random)
    {
        var area = ClientTables.Table("HeistAreas").Where(r => r.IntOrNull("Blueprint_BaseItemTypesKey") == type.Row).ToArray();
        if (area.Length != 1) return null;
        var jobs = area[0].Ints("HeistJobsKeys").Where(j => j is >= 0 and < 255).ToArray();
        if (jobs.Length < 3) return null;
        var rewards = ClientTables.Table("HeistChestRewardTypes");
        var rooms = rewards.Select((r, row) => (Row: row, Min: r.Int("MinLevel"), Max: r.Int("MaxLevel"),
            Weight: r.Int("Weight"), Jobs: r.Ints("HeistJobsKey").ToHashSet())).ToArray();
        var wings = new List<HeistWing>();
        for (int i = 0, count = random.Next(3, 5); i < count; i++)
        {
            var pickedJobs = jobs.OrderBy(_ => random.Next()).Take(3).ToArray();
            var allowed = rooms.Where(r => r.Row < 128 && r.Min <= 83 && r.Max >= 83 && r.Weight > 0
                && r.Jobs.Overlaps(pickedJobs)).ToArray();
            if (allowed.Length == 0) return null;
            var revealed = new List<HeistRoom>();
            for (int j = 0, countRooms = random.Next(4, 9); j < countRooms; j++)
            {
                int draw = random.Next(allowed.Sum(r => r.Weight));
                var selected = allowed[^1];
                foreach (var room in allowed) if ((draw -= room.Weight) < 0) { selected = room; break; }
                revealed.Add(new((byte)selected.Row, true));
            }
            // Escape routes share the same native room list and reveal bit.
            int escape = Enumerable.Range(0, rewards.Count).First(r => rewards[r].Text("Id") == "EscapeRoom");
            revealed.Add(new((byte)escape, true)); revealed.Add(new((byte)escape, true));
            wings.Add(new(true, [.. pickedJobs.Select((j, n) => new HeistJob((byte)j, n == 0 ? (byte)5 : (byte)random.Next(1, 6)))],
                [0xff, 0xff, 0xff], revealed));
        }
        return item with { ItemLevel = 83, HeistBlueprint = new((uint)random.NextInt64(1, (long)uint.MaxValue + 1), 83, 1, false, wings) };
    }
}
