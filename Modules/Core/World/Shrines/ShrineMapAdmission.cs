using GameServer.Modding;

namespace GameServer.Modules.Shrines;

/// <summary>Do not charge for a special shrine encounter whose executor is not complete.</summary>
public static class ShrineMapAdmission
{
    public static void Check(MapOpening request)
    {
        request.HandledContent.Add(MapScarabContent.StandardShrines);
        if (request.Opening?.Mods.Get(Stat.MapNumExtraEvolvingShrines) is > 0)
        {
            request.Opening = null;
            request.Reason = "Evolving Shrines are not supported yet. Remove the Domination Scarab of Evolution; your map and scarab have not been consumed.";
            return;
        }
        if (request.Opening?.Mods.Get(Stat.MapNumExtraApparitionShrines) is not > 0) return;
        request.Opening = null;
        request.Reason = "Apparition Shrines are not supported yet. Remove the Domination Scarab of Apparitions; your map and scarab have not been consumed.";
    }
}
