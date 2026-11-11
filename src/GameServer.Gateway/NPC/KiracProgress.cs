using System.Text.Json;
using GameServer.Core;

public static class KiracProgress
{
    public sealed record Level(int MapAreaLevelOffered, int MapCompletion, int ShopRerollCost)
    {
        public int Tier => Math.Clamp(MapAreaLevelOffered - 67,1,16);
    }
    static readonly Lazy<Level[]> Levels = new(() => JsonSerializer.Deserialize<Level[]>(
        ClientTables.ReadAllText("data/tables/KiracLevels.json"))!);
    public static Level For(AccountStash? account)
    {
        int count = Completed(account).Count;
        return Levels.Value.Where(r => r.MapCompletion <= count).OrderByDescending(r => r.MapCompletion).First();
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
