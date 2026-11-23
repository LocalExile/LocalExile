/// <summary>
/// Izaro's Lieutenants (LabyrinthSecrets <c>Lieutenants1</c>, room boss_lieutenants.arm): Lieutenants of Rage, of the Mace
/// and of the Bow (LabyrinthSecretEffects <c>Lieutenants1_Bull/_Melee/_Ranged</c>) rise one at a time on the small
/// elevators, the first with Izaro, and fight. The "…2" ones (<c>Lieutenants2_*</c>) come back in a later trial and give
/// Izaro <c>izaro_buff_loot_bonus</c>.
/// The original (live, the user, 2026-11-11): one at a time; when the last is killed he says so.
/// </summary>
public static class IzaroLieutenants
{
    public const string Mechanic = "Lieutenants";

    /// <summary>The time between their rises (the user's choice).</summary>
    public const double RiseInterval = 10;

    /// <summary>His line after the trial's end line: how many lieutenants live.</summary>
    public static string FinishLine(int alive) => $"IzaroFinishLieutenants{Math.Clamp(alive, 0, 3)}";

    /// <summary>His line when the last of the arena's lieutenants dies.</summary>
    public const string AllDeadLine = "IzaroAllLieutenantsDead";
}
