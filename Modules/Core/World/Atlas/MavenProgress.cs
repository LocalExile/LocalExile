using System.Text.Json;
using GameServer.Core;
using GameServer.Modding;

namespace GameServer.Modules.Atlas;

/// <summary>The five successive Atlas invitations. The final round can be earned again.</summary>
public static class MavenProgress
{
    public sealed record Round(string Id, int ItemRow, int MapTier, int Count, int AreaLevel, int VictoryFlag);
    public sealed record Witness(string Area, string Boss);
    public sealed record Progress(int Wins, Witness[] Witnesses);

    const string SaveKey = "atlas.maven";
    static readonly Progress Empty = new(0, []);
    static readonly Lazy<IReadOnlyList<Round>> Catalog = new(() =>
        [.. ClientTables.TableOrEmpty("MavenFights")
            .Where(r => (r.Text("Id") ?? "").StartsWith("Atlas", StringComparison.Ordinal))
            .Select(r => new Round(r.Text("Id")!, r.Int("BaseItemType"), r.Int("MinMapTier"),
                r.Int("WitnessesRequired"), r.Int("AreaLevel"), r.Int("__col3")))
            .OrderBy(r => r.MapTier)]);

    public static IReadOnlyList<Round> Rounds => Catalog.Value;
    public static Round? Current(Progress progress) =>
        Rounds.Count == 0 ? null : Rounds[Math.Clamp(progress.Wins, 0, Rounds.Count - 1)];

    public static Progress Read(ISavedData data) => Decode(data.Get(SaveKey));

    static Progress Decode(string? text) => text is null ? Empty : JsonSerializer.Deserialize<Progress>(text) ?? Empty;

    public static Progress Capture(Progress progress, string area, string boss, int tier)
    {
        if (Current(progress) is not { } round || tier < round.MapTier || progress.Witnesses.Length >= round.Count
            || progress.Witnesses.Any(w => w.Area == area || w.Boss == boss))
            return progress;
        return progress with { Witnesses = [.. progress.Witnesses, new(area, boss)] };
    }

    public static bool Ready(Progress progress, Round round) =>
        Current(progress) == round && progress.Witnesses.Length == round.Count;

    public static Progress Win(Progress progress, int generation, IReadOnlyList<string> bosses)
    {
        if (progress.Wins != generation || Current(progress) is not { } round || !Ready(progress, round)
            || !progress.Witnesses.Select(w => w.Boss).SequenceEqual(bosses))
            return progress;
        return new(progress.Wins + 1, []);
    }

    internal static Progress? Change(ISavedData data, Func<Progress, Progress> change)
    {
        Progress? changed = null;
        data.Change(SaveKey, text =>
        {
            var before = Decode(text);
            var next = change(before);
            if (ReferenceEquals(before, next)) return null;
            changed = next;
            return JsonSerializer.Serialize(next);
        });
        return changed;
    }
}
