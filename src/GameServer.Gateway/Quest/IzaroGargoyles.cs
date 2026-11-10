/// <summary>
/// Izaro's Gargoyles (LabyrinthSecrets <c>Gargoyles&lt;tier&gt;1</c>, room boss_gargoyles.arm). In that arena three
/// gargoyles (LabyrinthSecretEffects <c>Gargoyles&lt;tier&gt;_&lt;element&gt;</c>: Granite-Imbued, damage taken;
/// Quicksilver-Imbued, speed; Steel-Imbued, damage) stand on the small elevators, all three rising as the fight starts
/// (IzaroGargoyle.ot: Transitionable 1 lowered, 2 raising with <c>ascending_elevator</c>, 3 raised, 4 lowering, 5 lowered
/// again with <c>elevator_down_idle</c>; a new one is created in 5). Raised, they stand as statues for a while, then one at
/// a time a beam (MiscBeams <c>IzaroGargoyleBeam</c>, with <c>awaken_gargoyle</c> on the gargoyle) runs from it to
/// Izaro; when it ends the gargoyle awakens (its Emerge: <c>awaken</c> and the gold nova), gives Izaro its buff
/// (<c>izaro_gargoyles_*_buff_phase1</c>) and fights, and the next one's beam starts. Killing it takes the buff away. Every
/// gargoyle alive when the trial ends (awake, still a statue or not risen) stays on him for the run's later trials: in a
/// later room with the <c>Gargoyles&lt;tier&gt;2</c> secret it stands in an alcove (the "…2" variety,
/// IzaroGargoyle2), and as the fight starts the alcove's railing lowers, the buff (<c>izaro_gargoyles_*_buff</c>) goes on
/// him and it comes out to fight; on the ledge of the room before the arena it stands as a statue.
/// The original (live, the user): all three rise together, stand 5 s, each beam lasts 10 s; the buff comes as the
/// gargoyle awakens; the ones still fighting when he leaves keep fighting; a statue gargoyle takes little damage until
/// its beam starts. The speed buff's attack and movement speed are not applied by the server.
/// </summary>
static class IzaroGargoyles
{
    public const string Mechanic = "Gargoyles";

    /// <summary>How long the risen gargoyles stand before the first beam, and each beam (the original, Normal).</summary>
    public const double StandSeconds = 5, BeamSeconds = 10;

    /// <summary>The bone the beam leaves the gargoyle from: its mouth (Art/Models/MONSTERS/Gargoyle/rig.ast, live: the
    /// original's comes out of the mouth).</summary>
    public const string MouthBone = "Jnt_jaw";

    /// <summary>The MiscBeams row of the beam (IzaroGargoyleBeam, 31) and the buff on the gargoyle under it
    /// (awaken_gargoyle, BuffVisuals beamhit.epk).</summary>
    public static ushort BeamRow => Rows.Value.Beam;
    public static ushort? AwakenBuff => Rows.Value.Awaken;

    /// <summary>His line after the trial's end line: how many gargoyles live.</summary>
    public static string FinishLine(int alive) => $"IzaroFinishGargoyles{Math.Clamp(alive, 0, 3)}";

    /// <summary>His line when the last of the arena's gargoyles dies.</summary>
    public const string AllDeadLine = "IzaroAllGargoylesDead";

    static readonly Lazy<(ushort Beam, ushort? Awaken)> Rows = new(() =>
    {
        ushort beam = 31;
        ushort? awaken = null;
        try
        {
            using (var beams = GameTables.Open("data/tables/MiscBeams.json"))
                if (beams.RootElement.EnumerateArray().Select(r => r.GetProperty("Id").GetString()).ToList().IndexOf("IzaroGargoyleBeam") is int b and >= 0)
                    beam = (ushort)b;
            using var buffs = GameTables.Open("data/tables/BuffDefinitions.json");
            if (buffs.RootElement.EnumerateArray().Select(r => r.GetProperty("Id").GetString()).ToList().IndexOf("awaken_gargoyle") is int a and >= 0)
                awaken = (ushort)a;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Console.WriteLine($"[gateway] Izaro's gargoyles: {ex.Message}");
        }
        return (beam, awaken);
    });
}
