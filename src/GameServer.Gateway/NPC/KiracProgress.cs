using System.Text.Json;
using GameServer.Core;

public static class KiracProgress
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadGameData()
    {
        Levels = LoadLevels();
    }

    public sealed record Level(int MapAreaLevelOffered, int MapCompletion, int ShopRerollCost)
    {
        public int Tier => Math.Clamp(MapAreaLevelOffered - 67,1,16);
    }
    static Level[] Levels = default!;

    static Level[] LoadLevels() =>
        [.. ClientTables.Table("KiracLevels")
        .Select(r => new Level(r.Int("MapAreaLevelOffered"), r.Int("MapCompletion"), r.Int("ShopRerollCost")))];
    public static Level For(AccountStash? account)
    {
        int count = Completed(account).Count;
        return Levels.Where(r => r.MapCompletion <= count).OrderByDescending(r => r.MapCompletion).First();
    }
    public static HashSet<string> Completed(AccountStash? account) =>
        JsonSerializer.Deserialize<HashSet<string>>(account?.ModuleData("vendors.completedMaps") ?? "[]")!;
    public static bool Complete(AccountStash account, string area, int tier) => Saves.Atomically(() =>
    {
        if (tier is < 1 or > 16) return false;
        account.ChangeModuleData("vendors.highestTier",old =>
        {
            int.TryParse(old,out int highest);
            return tier > highest ? tier.ToString() : null;
        });
        var completed = Completed(account);
        if (!completed.Add(area)) return false;
        account.ChangeModuleData("vendors.completedMaps",_ => JsonSerializer.Serialize(completed.Order()));
        return true;
    });
}
