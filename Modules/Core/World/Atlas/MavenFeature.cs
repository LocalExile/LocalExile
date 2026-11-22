using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Atlas;

static class MavenFeature
{
    const string Crucible = "MavenHub";
    const string InfluenceStat = "map_atlas_primordial_boss_influence";
    const string CrescentSplinter = "Metadata/Items/MapFragments/CurrencyMavenKeyFragment";
    const int MavenInfluence = 1, ReadyFlag = 1019, PinnacleUnlocked = 1075;

    public static async Task OnKill(MonsterKill kill)
    {
        if (kill.Monster.Rarity != MonsterRarity.Unique) return;
        var conn = kill.Conn;
        if (global::Game.MapRunOf(conn) is not { } run || run.Owner != CharacterName(conn)) return;
        if (run.Area.Id == Crucible && run.Mods.Roster is { } roster)
        {
            if (!roster.Monsters.Contains(kill.Monster.VarietyId)) return;
            lock (conn.Monsters)
                if (conn.Monsters.Values.Any(m => !m.Dead && m.Hostile && m.Variety is { } v
                    && roster.Monsters.Contains(v.Id))) return;
            var data = WorldOf(conn).CharacterData;
            var won = Saves.Atomically(() => MavenProgress.Change(data, p => MavenProgress.Win(p, roster.Generation, roster.Monsters)));
            if (won is null) return;
            // Local base reward for the level-83 round. A full stack converts to the Writ via CurrencyItems.
            if (won.Wins >= MavenProgress.Rounds.Count)
            {
                var wire = conn.Wire;
                await global::Game.DropGroundItem(wire.Remote, wire.Stream, wire.Ciphers, conn,
                    CrescentSplinter, (kill.Monster.X, kill.Monster.Y), "The Maven's Crucible");
            }
            await Sync(conn, won);
            await kill.Player.Tell("Atlas: The Maven's Invitation completed.");
            return;
        }

        if (run.Area.Tier <= 0 || run.Mods.MapRarity == ItemRarity.Unique
            || AtlasProgress.OfArea(run.Area.Id) is not { } node || !AtlasProgress.CompletesMap(node, kill.Monster)
            || global::Game.MapModsOf(conn)?.Get(InfluenceStat) != MavenInfluence)
            return;
        var witnessed = Saves.Atomically(() => MavenProgress.Change(WorldOf(conn).CharacterData,
            p => MavenProgress.Capture(p, node.Id, kill.Monster.VarietyId, run.Area.Tier)));
        if (witnessed is null || MavenProgress.Current(witnessed) is not { } round) return;
        if (!ClientHasQuestFlag(conn, "MavenFirstMapBossCapture"))
        {
            var (remote, stream, ciphers) = conn.Wire;
            await global::Game.SetQuestFlags(remote, stream, ciphers, conn, ["MavenFirstMapBossCapture"]);
        }
        await Sync(conn, witnessed);
        await kill.Player.Tell($"The Maven witnessed {kill.Monster.Name} ({witnessed.Witnesses.Length}/{round.Count}).");
    }

    public static bool CanOffer(ClientState conn, string invitation)
    {
        var round = MavenProgress.Rounds.FirstOrDefault(r => r.Id.Equals(invitation, StringComparison.OrdinalIgnoreCase));
        return round is null || MavenProgress.Ready(MavenProgress.Read(WorldOf(conn).CharacterData), round);
    }

    public static void Open(MapOpening request)
    {
        if (request.Campaign || request.Slots.Count == 0) return;
        var rows = MavenProgress.Rounds;
        var round = rows.FirstOrDefault(r => request.Slots.Any(i => i.BaseRow == r.ItemRow));
        if (round is null) return;
        request.Opening = null;
        var progress = MavenProgress.Read(WorldOf(request.Conn).CharacterData);
        if (!MavenProgress.Ready(progress, round) || request.Slots.Count != 1)
        {
            request.Reason = "The Maven must witness the required bosses before this invitation can be opened.";
            return;
        }
        if (AreaTransitionCatalog.HashOf(Crucible) is not { } hash)
        {
            request.Reason = "The Maven's Crucible is unavailable.";
            return;
        }
        var area = new MapDevice.MapArea(hash, Crucible, "The Maven's Crucible", 0) { Level = round.AreaLevel };
        var mods = new MapMods(MapMods.None.Stats)
        {
            Roster = new(progress.Wins, [.. progress.Witnesses.Select(w => w.Boss)]),
        };
        request.Opening = new(area, mods, round.ItemRow);
    }

    public static bool ApplyFlags(ClientState conn, MavenProgress.Progress progress)
    {
        var flags = conn.LeagueQuestFlags;
        bool Set(int id, bool on) => on ? flags.Add(id) : flags.Remove(id);
        var rounds = MavenProgress.Rounds;
        var current = MavenProgress.Current(progress);
        bool changed = Set(ReadyFlag, current is not null && MavenProgress.Ready(progress, current));
        for (int i = 0; i < rounds.Count; i++)
            changed |= Set(rounds[i].VictoryFlag, progress.Wins > i);
        changed |= Set(PinnacleUnlocked, rounds.Count > 0 && progress.Wins >= rounds.Count
            || ClientHasQuestFlag(conn, "GrantAtlasUpgrade1"));
        return changed;
    }

    static async Task Sync(ClientState conn, MavenProgress.Progress progress)
    {
        if (!ApplyFlags(conn, progress) || !conn.PlayerSent) return;
        var (remote, stream, ciphers) = conn.Wire;
        await global::Game.SendWorldRecord(remote, stream, ciphers, conn, PlayerId,
            InstanceEntities.PlayerLeagueQuestFlagsUpdate(PlayerId, Quests.League.Bits(conn.LeagueQuestFlags)),
            "0x02bc Maven invitation progress");
    }
}
