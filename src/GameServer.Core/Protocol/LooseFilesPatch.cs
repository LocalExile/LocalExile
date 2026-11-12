namespace GameServer.Core.Protocol;

/// <summary>
/// Makes a standalone-mode client run from loose game files (a Steam install's Bundles2\) instead of Content.ggpk.
/// The client picks a distribution mode from its file name or a flag (0x140df9010: 0 standalone, 1 Steam for "Steam" in
/// the name or --steam, 4 Kakao, 5 Epic). Steam mode also needs a running Steam (SteamAPI_Init at 0x1400f8654), so the
/// client stays in standalone mode (no Steam, account login) and two branches change:
/// <list type="bullet">
/// <item>0x1400fc3f0 opens Content.ggpk unless the mode is 1, 3 or 5, which read loose files. Unpatched in a Steam folder,
/// a standalone-mode client creates an empty Content.ggpk and asks to download everything.</item>
/// <item>0x1428f31c0 compares a local directory's hash with the patch welcome's. With no pack file it answers "differs",
/// so the client asks the patch server for the root contents ("Requesting root contents"), which ours does not serve.
/// Patched, it answers "matches": "No patching required" (decided at 0x1428f3b0c), and patching never downloads.</item>
/// </list>
/// </summary>
public static class LooseFilesPatch
{
    static readonly (long Va, byte[] Original, byte[] Patched)[] Sites =
    [
        // After call 0x1400f0530 (mode is 1 or 3): jne 0x1400fc557, the return without a pack file -> jmp 0x1400fc557; nop
        (0x1400fc426, [0x0f, 0x85, 0x2b, 0x01, 0x00, 0x00], [0xe9, 0x2c, 0x01, 0x00, 0x00, 0x90]),
        // No pack file: xor al, al -> mov al, 1
        (0x1428f3221, [0x32, 0xc0], [0xb0, 0x01]),
    ];

    /// <summary>Applies the patch in place (already patched is fine); other bytes there mean another build, and then
    /// nothing is changed.</summary>
    public static void Apply(byte[] image)
    {
        var offsets = Sites.Select(s => PeImage.FileOffset(image, s.Va, s.Original.Length)).ToArray();
        for (int i = 0; i < Sites.Length; i++)
        {
            var here = image.AsSpan(offsets[i], Sites[i].Original.Length);
            if (!here.SequenceEqual(Sites[i].Original) && !here.SequenceEqual(Sites[i].Patched))
                throw new FormatException($"the loose-file branch at 0x{Sites[i].Va:x} is not the expected 3.29.3c code");
        }
        for (int i = 0; i < Sites.Length; i++) Sites[i].Patched.CopyTo(image.AsSpan(offsets[i]));
    }

    public static bool IsApplied(byte[] image) => Sites.All(s =>
        image.AsSpan(PeImage.FileOffset(image, s.Va, s.Patched.Length), s.Patched.Length).SequenceEqual(s.Patched));
}
