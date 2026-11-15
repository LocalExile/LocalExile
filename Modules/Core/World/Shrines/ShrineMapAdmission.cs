using GameServer.Modding;

namespace GameServer.Modules.Shrines;

/// <summary>Do not charge for an encounter whose summoned Conqueror actions have no server executor.</summary>
public static class ShrineMapAdmission
{
    public static void Check(MapOpening request)
    {
        if (request.Opening?.Mods.Get("map_num_extra_apparition_shrines") is not > 0) return;
        request.Opening = null;
        request.Reason = "Apparition Shrines are not supported yet. Remove the Domination Scarab of Apparitions; your map and scarab have not been consumed.";
    }
}
