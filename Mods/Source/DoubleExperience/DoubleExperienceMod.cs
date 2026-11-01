using GameServer.Modding;

namespace LocalExile.Mods.DoubleExperience;

/// <summary>Twice the experience from kills. Off by default: /mod_enable doubleexperience.</summary>
public sealed class DoubleExperienceMod : IMod
{
    public void Load(IModContext mod) => mod.ModifyExperience(gain => gain.Experience *= 2);
}
