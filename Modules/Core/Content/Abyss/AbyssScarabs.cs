using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Abyss;

public static class AbyssScarabs
{
    public const string Consort = "Metadata/Monsters/AbyssTwoLeague/MonolithicSlug",
        Spire = "Metadata/Monsters/Totems/AbyssCrystalPillarMapStandalone",
        Crystal = "Metadata/MiscellaneousObjects/LeagueAbyssTwo/AbyssTwoScarabCrystal";
    static StateMachineData? crystalStates;

    public static void Load()
    {
        var shared = ObjectTypes.SharedStates();
        crystalStates = ObjectTypes.All().Where(r => r.Type == Crystal).Select(r => ObjectTypes.StateMachine(r.Row, shared)).FirstOrDefault();
    }

    public static void Opening(MapOpening request)
    {
        if (request.Campaign || request.Opening is not { } opening || !MapDevices.IsMapArea(opening.Area.Hash16)
            || !AbyssTables.HasRewardChest || AbyssTables.Families.Count == 0 || crystalStates is null
            || QuestObjectTypes.Find(Crystal) is not { } crystal || !QuestObject.CanSerialize(crystal.Components, true)
            || global::Game.Monsters.Find(Consort) is not { Supported: true } || global::Game.Monsters.Find(Spire) is not { Supported: true }
            || opening.Mods.Get(Stat.MapContainsAbyssDepths) > 0 &&
                (!ServerLevels.IsSupported("AbyssLeagueBoss") || !ServerLevels.IsSupported("AbyssLeagueBoss2")
                || AreaTransitionCatalog.HashOf("AbyssLeagueBoss") is null || AreaTransitionCatalog.HashOf("AbyssLeagueBoss2") is null)) return;
        request.HandledContent.Add("Abyss");
    }

    internal static Task ShowCrystal(Player player, uint id, (int X, int Y) at)
    {
        var info = QuestObjectTypes.Find(Crystal)!;
        var crystal = new QuestObject(at.X, at.Y, info.Components) { StateMachine = crystalStates, Awake = true };
        return global::Game.SendWorldCreate(player, id, () => InstanceEntities.CreateObject(id, InstanceEntities.ObjectTypeHash(Crystal), crystal.Blob()), "Abyssal Crystal created");
    }
}
