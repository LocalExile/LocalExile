using GameServer.Core;
using GameServer.Core.Protocol.Instance;

namespace GameServer.Modules.Delirium;

/// <summary>The fifteen-wave encounter introduced by 3.25; arena and affix identities come from the client tables.</summary>
public static class SimulacrumRules
{
    public const int Waves = 15;
    public const string Fragment = "Metadata/Items/MapFragments/CurrencyAfflictionFragment";
    public const string Omniphobia = "Metadata/Monsters/LeagueAffliction/AfflictionDemonBossPhysical";
    public const string Kosis = "Metadata/Monsters/LeagueAffliction/AfflictionDemonBossFinal";
    public static readonly string[] ArenaIds = ["AfflictionTown6", "AfflictionTown4", "AfflictionTown7", "AfflictionTown8", "AfflictionTown10"];

    public static bool IsArena(string id) => Array.IndexOf(ArenaIds, id) >= 0;
    public static int Level(int wave) => Math.Clamp(74 + wave, 75, 83);
    public static int RewardLevel(int wave) => Math.Clamp(74 + wave, 75, 84);
    public static int Delirious(int wave) => Math.Clamp(wave * 10, 10, 100);
    public static int RewardTypes(int wave) => wave <= 3 ? 4 : wave <= 9 ? 5 : wave <= 12 ? 6 : 7;
    public static int ModifierCount(int wave) => wave <= 4 ? 4 : wave <= 9 ? 5 : 6;
    // Population calibration from player observations, not a published GGG formula.
    public static int Population(int wave) => Math.Clamp(wave, 1, 15) switch
    {
        <= 4 => 300 + (Math.Clamp(wave, 1, 4) - 1) * 15,
        <= 9 => 380 + (wave - 5) * 40,
        _ => 550 + (Math.Min(wave, 15) - 10) * 20,
    };

    public static MapDevice.MapArea[] Arenas() => [.. ClientTables.Table("WorldAreas")
        .Where(r => IsArena(r.Text("Id") ?? ""))
        .Select(r => new MapDevice.MapArea((ushort)r.Int("HASH16"), r.Text("Id")!, r.Text("Name") ?? "Simulacrum", 0) { Level = 75 })];

    public static void Resolve(MapOpening request, Random random)
    {
        if (request.Campaign) return;
        uint fragment = InstanceEntities.ObjectTypeHash(Fragment);
        ushort node = (ushort)ClientTables.Table("AtlasNode").First(r => r.Text("Id") == "Simulacrum").Int("HASH16");
        if (request.Target != 0 && request.Target != node) return;
        int input = Enumerable.Range(0, request.Slots.Count).FirstOrDefault(i => request.Slots[i].TypeHash == fragment, -1);
        if (input < 0)
        {
            if (request.Target == node) { request.Opening = null; request.Reason = "Place a Simulacrum in the Map Device first."; }
            return;
        }
        request.Opening = null;
        var arenas = Arenas();
        if (arenas.Length != ArenaIds.Length)
        { request.Reason = "The Simulacrum arenas are not available in the client data."; return; }
        request.Opening = new(arenas[random.Next(arenas.Length)], MapMods.None, request.Slots[input].BaseRow);
        request.ConsumeSlots = new[] { input };
        request.Reason = "";
        request.ReturnToSource = true;
    }
}
