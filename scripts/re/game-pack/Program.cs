using System.IO.Compression;
using GameServer.Core.Protocol;
using GameServer.Core.Protocol.Instance;
using GameServer.Data.Ggpk;
using GgDump;

// What the servers need from the client as they run, so they never open Client/ (extract-data.ps1 runs both):
//   constants   data/client-constants.json: the HNC mask of Client/Game.exe, the root hash of Client/Content.ggpk
//   pack        data/game-files.zip: the game files GamePack covers
//   stat-descriptions   data/stat-descriptions.txt: the English stat descriptions (StatDescriptions.cs), for tools
const string Ggpk = "Client/Content.ggpk";

switch (args.FirstOrDefault())
{
    case "constants":
        byte[] exe = File.ReadAllBytes("Client/Game.exe");
        HncSender.LoadMask(exe); // Checks the mask against its known hash.
        byte[] mask = PeImage.ReadAtVa(exe, HncSender.MaskVa, 64);
        new ClientConstants(Convert.ToHexString(mask), Convert.ToHexString(GgpkMaintenance.RootHash(Ggpk))).Save();
        Console.WriteLine($"wrote {ClientConstants.DefaultFile}");
        return 0;
    case "pack":
        using (var decoder = new NodeOodleDecoder("node", Path.GetFullPath("Tools/ooz/ooz_worker.mjs")))
        using (var fs = GameFileSystem.Open(Ggpk, decoder))
        {
            // In bundle order, so each bundle is read once.
            var paths = fs.EnumeratePaths().Where(GamePack.Covers)
                .Select(p => (Path: p, Location: fs.Index.TryGetFile(p, out var l) ? l : throw new InvalidDataException(p)))
                .OrderBy(f => f.Location.BundleIndex).ThenBy(f => f.Location.Offset).ToList();
            string temp = GamePack.DefaultFile + ".tmp";
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
                foreach (var (path, _) in paths)
                {
                    using var entry = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
                    entry.Write(fs.ReadFile(path));
                }
            File.Move(temp, GamePack.DefaultFile, overwrite: true);
            Console.WriteLine($"wrote {GamePack.DefaultFile}: {paths.Count} files, {new FileInfo(GamePack.DefaultFile).Length >> 20} MB");
        }
        return 0;
    case "stat-descriptions":
        using (var decoder = new NodeOodleDecoder("node", Path.GetFullPath("Tools/ooz/ooz_worker.mjs")))
        using (var fs = GameFileSystem.Open(Ggpk, decoder))
        {
            const string source = "metadata/statdescriptions/stat_descriptions.txt", target = "data/stat-descriptions.txt";
            // UTF-16 with every translation after the English lines of a description: a `lang "…"` line starts them, and
            // they run to the next unindented line.
            string text = System.Text.Encoding.Unicode.GetString(fs.ReadFile(source)).TrimStart('﻿');
            var english = new List<string>();
            bool translation = false;
            foreach (string line in text.Split('\n').Select(l => l.TrimEnd('\r')))
            {
                if (line.Length > 0 && !char.IsWhiteSpace(line[0])) translation = false;
                else if (line.TrimStart().StartsWith("lang ", StringComparison.Ordinal)) translation = true;
                if (!translation) english.Add(line);
            }
            File.WriteAllLines(target + ".tmp", english);
            File.Move(target + ".tmp", target, overwrite: true);
            Console.WriteLine($"wrote {target}: {english.Count} lines");
        }
        return 0;
    default:
        Console.Error.WriteLine("usage: game-pack constants | pack | stat-descriptions");
        return 2;
}
