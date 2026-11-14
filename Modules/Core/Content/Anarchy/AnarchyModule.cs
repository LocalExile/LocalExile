using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using GameServer.Modding;

namespace GameServer.Modules.Anarchy;

/// <summary>
/// Anarchy: wild Rogue Exiles. A new instance rolls CoreLeagues row 0 at its WorldAreaLeagueChances +0x8 chance (5% in
/// the campaign's and the maps' areas; none in The Twilight Strand and The Coast), and each exile it gets stands alone
/// away from the packs, a unique monster with its level's more life (RogueExileLifeScalingPerLevel).
/// Agents/docs/game/league-encounters.md.
/// </summary>
public sealed class AnarchyModule : IModule
{
    /// <inheritdoc/>
    public void Load(IModuleContext module)
    {
        GameEvents.AreaPopulated.Add(module.Manifest.Id, area => Place(module, area));
    }

    static async Task Place(IModuleContext module, AreaPopulated area)
    {
        var conn = area.Conn;
        var layout = conn.Layout;
        if (layout.Walkable is not { } walkable) return;
        // The encounter's own draws, apart from the packs' (the same instance gets the same exiles).
        var rng = new Random(area.Seed ^ 0x416e6172);
        int count = LeagueChances.Current.Count(layout.Id, LeagueChances.Anarchy, rng);
        if (count == 0) return;
        var taken = area.Taken.ToList();
        var transitions = layout.AreaTransitions.Select(t => (t.Transition.GridX, t.Transition.GridY)).ToList();
        var (remote, stream, ciphers) = conn.Wire;
        for (int i = 0; i < count; i++)
        {
            if (RogueExiles.Current.Pick(area.Level, rng) is not { } exile)
            {
                module.Log($"{layout.Id}: no Rogue Exile can spawn at level {area.Level}");
                return;
            }
            if (AreaPopulation.EncounterSpot(layout.GridW, layout.GridH, walkable, (layout.SpawnX, layout.SpawnY), transitions, taken, rng)
                is not { } at) return;
            taken.Add(at);
            var mods = RogueExiles.Current.LifeMore(area.Level) is int more and > 0
                ? new MonsterModSet([], [], new MonsterModEffects(new Dictionary<string, int> { [MonsterModCatalog.EncounterLifeStat] = more }))
                : null;
            uint id = await SpawnMonster(remote, stream, ciphers, conn, exile, area.Level, MonsterRarity.Unique, at.X, at.Y,
                log: true, alone: true, extraMods: mods);
            module.Log($"{layout.Id}: Rogue Exile {exile.Name} ({exile.Id}) at ({at.X}, {at.Y}), level {area.Level}" +
                (mods is null ? "" : $", {RogueExiles.Current.LifeMore(area.Level)}% more life") + (id == 0 ? ": not created" : $", id {id}"));
        }
    }
}
