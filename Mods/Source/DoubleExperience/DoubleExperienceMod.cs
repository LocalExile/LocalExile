using GameServer.Modding;

namespace LocalExile.Mods.DoubleExperience;

/// <summary>More experience from kills, twice by default (its multiplier setting). Off by default: /mod_enable doubleexperience.</summary>
public sealed class DoubleExperienceMod : IMod
{
    sealed record Choices(double Multiplier);

    public void Load(IModContext mod)
    {
        double multiplier = mod.Settings<Choices>().Multiplier;
        mod.ModifyExperience(gain => gain.Experience = (uint)Math.Min(uint.MaxValue, Math.Floor(gain.Experience * multiplier)));
    }
}
