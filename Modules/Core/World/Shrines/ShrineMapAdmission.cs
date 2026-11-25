using GameServer.Modding;

namespace GameServer.Modules.Shrines;

/// <summary>The module owns standard shrines and both special scarab encounters.</summary>
public static class ShrineMapAdmission
{
    public static void Check(MapOpening request)
    {
        request.HandledContent.Add(MapScarabContent.StandardShrines);
        request.HandledContent.Add("Shrines.Evolving");
        request.HandledContent.Add("Shrines.Apparitions");
    }
}
