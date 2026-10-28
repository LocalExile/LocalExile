using System.Text;
using System.Text.Json;
using GameServer.Core.Accounts;
using GameServer.Core.Imports;
using GameServer.Storage;

/// <summary>Offline, create-only PoB import. Preparation and preview never open the save database.</summary>
static class PathOfBuildingCommand
{
    const int MaxInputCharacters = PathOfBuilding.MaximumXmlBytes;
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static int Run(string[] args, TextReader input, TextWriter output, TextWriter error)
    {
        try
        {
            var options = Options.Parse(args);
            if (options.Help)
            {
                output.WriteLine("""
                    Import a Path of Building Community Fork export as a NEW LocalExile character.
                    Run from the repository root, after extracting game data.

                    dotnet run --project src/GameServer.Gateway -- --import-pob --file build.txt --preview
                    dotnet run --project src/GameServer.Gateway -- --import-pob --file build.txt --account offline --name BuildWitch

                    --file PATH    File containing the copied PoB export code or saved build XML; '-' reads stdin.
                    --account NAME Existing local account (required when creating a character).
                    --name NAME    New character name (required when creating; existing characters are never replaced).
                    --league NAME  Offered local league (default: Standard).
                    --preview      Validate and describe the build without opening or writing a save database.
                    --help         Show this help.

                    Honors the build's active tree, item set and skill set. Unsupported build features are
                    skipped with warnings. Review Preview before creating. No remote links are fetched.
                    """);
                return 0;
            }

            string text;
            if (options.File == "-") text = ReadBounded(input);
            else
            {
                using var file = new StreamReader(options.File!, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
                text = ReadBounded(file);
            }
            var config = new PathOfBuildingServerConfig();
            var league = config.SelectLeague(options.League);
            var prepared = PathOfBuildingImport.Prepare(text, config);
            // Render before committing, using the same bounded result contract as HTTP.
            string response = JsonSerializer.Serialize(prepared.Response(options.Preview, options.Name, league.Name), JsonOptions);
            if (!options.Preview)
            {
                using var db = new SaveDatabase(config.DatabasePath);
                PathOfBuildingCharacters.Create(db, options.Account!, options.Name!, prepared.Progress,
                    prepared.Progress.ToSave(prepared.Equipment), league.Name, config.Leagues, config.DataFolder);
            }
            output.WriteLine(response);
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or JsonException or OverflowException
            or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }, JsonOptions));
            return 1;
        }
    }

    static string ReadBounded(TextReader input)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (result.Length + count > MaxInputCharacters)
                throw new ArgumentException("PoB input exceeds the 8 MiB limit.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }

    sealed record Options(string? File, string? Account, string? Name, string League, bool Preview, bool Help)
    {
        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            bool preview = false;
            foreach (string argument in args)
                if (argument is "--help" or "-h")
                    return new(null, null, null, LeagueCatalog.Standard, false, true);
            for (int i = 0; i < args.Length; i++)
            {
                string option = args[i];
                if (option == "--preview")
                {
                    if (preview) throw new ArgumentException("--preview was supplied more than once.");
                    preview = true;
                    continue;
                }
                if (option is not ("--file" or "--account" or "--name" or "--league"))
                    throw new ArgumentException($"Unknown option '{option}'. Use --import-pob --help.");
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[i]))
                    throw new ArgumentException($"{option} needs a value.");
                if (!values.TryAdd(option, args[i])) throw new ArgumentException($"{option} was supplied more than once.");
            }
            if (!values.ContainsKey("--file")) throw new ArgumentException("--file is required. Use --import-pob --help.");
            if (!preview && (!values.ContainsKey("--account") || !values.ContainsKey("--name")))
                throw new ArgumentException("Creating a character requires --account and --name. Use --preview to validate without saving.");
            return new(values["--file"], values.GetValueOrDefault("--account"), values.GetValueOrDefault("--name"),
                values.GetValueOrDefault("--league") ?? LeagueCatalog.Standard, preview, false);
        }
    }
}
