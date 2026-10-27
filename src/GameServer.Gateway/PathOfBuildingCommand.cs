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
                    reported as errors before saving. No remote links are fetched; export the code in PoB.
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
            var build = PathOfBuilding.Parse(text);
            var progress = PathOfBuildingCharacters.Prepare(build);
            var league = LeagueCatalog.FromEnvironment().Find(options.League)
                ?? throw new ArgumentException($"Unknown league '{options.League}'. Use a league offered by this server.");
            // PoB uses ordinary rules. Importing an ordinary build under Ruthless's different data would change it.
            if (league.Ruthless) throw new ArgumentException("Ruthless build imports are not supported: ordinary PoB stats differ from Ruthless rules.");
            var items = PathOfBuildingItems.Materialize(build, new ItemCatalog());
            var save = progress.ToSave(items);
            var warnings = progress.Warnings.Concat(items.Warnings).Distinct().ToArray();
            Character? character = null;
            if (!options.Preview)
            {
                using var db = new SaveDatabase(SaveDatabase.DefaultPath);
                character = PathOfBuildingCharacters.Create(db, options.Account!, options.Name!, progress, save, league.Name);
            }
            output.WriteLine(JsonSerializer.Serialize(new
            {
                ok = true,
                preview = options.Preview,
                character = character?.Name ?? options.Name,
                account = character is null ? null : options.Account,
                @class = build.Class.ToString(),
                level = build.Level,
                league = league.Name,
                passives = progress.Passives.Count,
                masteries = progress.MasteryEffects.Count,
                items = items.Inventories.Values.Sum(placements => placements.Count),
                gems = items.Inventories.Values.SelectMany(placements => placements)
                    .Sum(placement => placement.Item.Sockets?.Count(socket => socket.Gem is not null) ?? 0),
                warnings,
                next = options.Preview ? "Validation passed. Remove --preview and provide --account and --name to create the character."
                    : "Log out to the login screen and log in again to refresh the character list, then select this character and Play."
            }, JsonOptions));
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
