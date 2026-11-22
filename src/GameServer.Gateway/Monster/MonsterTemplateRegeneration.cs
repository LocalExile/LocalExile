using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using GameServer.Data.Ggpk;
using GameServer.Data.Terrain;

/// <summary>The life regeneration declared by the Stats sections of a monster's packed object templates.</summary>
static partial class MonsterTemplateRegeneration
{
    const string Stat = "life_regeneration_rate_per_minute_%";
    const string SetStat = "set_" + Stat;
    readonly record struct Contribution(bool Replaces, int Value);
    static readonly ConcurrentDictionary<string, Contribution> Declarations = new(StringComparer.OrdinalIgnoreCase);

    public static Lazy<int> FromTypes(string[] chain) => new(() => Rate(chain));

    static int Rate(string[] chain)
    {
        int rate = 0;
        foreach (string type in chain)
        {
            var declaration = Declarations.GetOrAdd(type, Read);
            rate = declaration.Replaces ? declaration.Value : rate + declaration.Value;
        }
        return rate;
    }

    static Contribution Read(string type)
    {
        if (GamePack.Default.TryRead(type + ".ot") is not { } bytes) return default;
        string text = TerrainText.Decode(bytes);
        if (!text.Contains(Stat, StringComparison.Ordinal)) return default;

        var tokens = Tokens().Matches(text).Select(m => m.Value).Where(t => !t.StartsWith("//", StringComparison.Ordinal)).ToArray();
        int depth = 0, value = 0;
        bool stats = false, replaces = false;
        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (token == "{")
            {
                if (depth == 0) stats = i > 0 && tokens[i - 1] == "Stats";
                depth++;
            }
            else if (token == "}") depth--;
            else if (stats && depth == 1 && token is Stat or SetStat)
            {
                if (i + 2 >= tokens.Length || tokens[i + 1] != "=")
                    throw new InvalidDataException($"{type}.ot: expected an assignment for {token}");
                int amount = int.Parse(tokens[i + 2].Trim('"', '\''), CultureInfo.InvariantCulture);
                if (token == SetStat) { value = amount; replaces = true; }
                else value += amount;
                i += 2;
            }
        }
        return new(replaces, value);
    }

    [GeneratedRegex("""//[^\n]*|"[^"]*"|'[^']*'|[{}=]|[^\s{}=/"']+|/""")]
    private static partial Regex Tokens();
}
