using System.Text.Json;

namespace GameServer.Core.World;

/// <summary>The walk grid captured by scripts/walkmaps: one byte per cell and its dimensions.</summary>
public static class WalkMapCapture
{
    public const string Folder = "data/walkability";

    public static bool Available(string area, uint seed, string folder = Folder) =>
        Dimensions(area, seed, folder, out _, out _);

    public static byte[]? Load(string area, uint seed, out int width, out int height, string folder = Folder)
    {
        if (!Dimensions(area, seed, folder, out width, out height)) return null;
        try
        {
            byte[] cells = File.ReadAllBytes(Path.Combine(folder, $"{area}_seed{seed}.bin"));
            return cells.LongLength == (long)width * height ? cells : null;
        }
        catch (IOException) { return null; }
    }

    static bool Dimensions(string area, uint seed, string folder, out int width, out int height)
    {
        (width, height) = (0, 0);
        string bin = Path.Combine(folder, $"{area}_seed{seed}.bin");
        string meta = Path.ChangeExtension(bin, ".json");
        if (!File.Exists(bin) || !File.Exists(meta)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(meta));
            var root = document.RootElement;
            (width, height) = (root.GetProperty("w").GetInt32(), root.GetProperty("h").GetInt32());
            return width > 0 && height > 0 && new FileInfo(bin).Length == (long)width * height;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
