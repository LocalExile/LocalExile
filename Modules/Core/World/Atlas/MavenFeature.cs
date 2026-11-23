using GameServer.Core.Protocol.Instance;
using GameServer.Modding;
using static Program;

namespace GameServer.Modules.Atlas;

static class MavenFeature
{
    const string Crucible = "MavenHub";
    const string CrescentSplinter = "Metadata/Items/MapFragments/CurrencyMavenKeyFragment";
    const int MavenInfluence = 1, ReadyFlag = 1019, PinnacleUnlocked = 1075;

    public static async Task OnKill(MonsterKill kill)
    {
        if (kill.Monster.Rarity != MonsterRarity.Unique) return;
        var player = kill.Player;
        if (global::Game.MapRunOf(player) is not { } run || run.Owner != CharacterName(player)) return;
        if (run.Area.Id == Crucible && run.Mods.Roster is { } roster)
        {
            if (!roster.Monsters.Contains(kill.Monster.VarietyId)) return;
            if (player.Area.Monsters.Values.Any(m => !m.Dead && m.Hostile && m.Variety is { } v
                && roster.Monsters.Contains(v.Id))) return;
            var data = WorldOf(player).CharacterData;
            var won = Saves.Atomically(() => MavenProgress.Change(data, p => MavenProgress.Win(p, roster.Generation, roster.Monsters)));
            if (won is null) return;
            // Local base reward for the level-83 round. A full stack converts to the Writ via CurrencyItems.
            if (won.Wins >= MavenProgress.Rounds.Count)
            {
                await global::Game.DropGroundItem(player,
                    CrescentSplinter, (kill.Monster.X, kill.Monster.Y), "The Maven's Crucible");
            }
            await Sync(player, won);
            await kill.ModPlayer.Tell("Atlas: The Maven's Invitation completed.");
            return;
        }

        if (run.Area.Tier <= 0 || run.Mods.MapRarity == ItemRarity.Unique
            || AtlasProgress.OfArea(run.Area.Id) is not { } node || !AtlasProgress.CompletesMap(node, kill.Monster)
            || global::Game.MapModsOf(player)?.Get(Stat.MapAtlasPrimordialBossInfluence) != MavenInfluence)
            return;
        var witnessed = Saves.Atomically(() => MavenProgress.Change(WorldOf(player).CharacterData,
            p => MavenProgress.Capture(p, node.Id, kill.Monster.VarietyId, run.Area.Tier)));
        if (witnessed is null || MavenProgress.Current(witnessed) is not { } round) return;
        if (!ClientHasQuestFlag(player, "MavenFirstMapBossCapture"))
            await global::Game.SetQuestFlags(player, ["MavenFirstMapBossCapture"]);
        await Sync(player, witnessed);
        await kill.ModPlayer.Tell($"The Maven witnessed {kill.Monster.Name} ({witnessed.Witnesses.Length}/{round.Count}).");
    }

    public static bool CanOffer(Player player, string invitation)
    {
        var round = MavenProgress.Rounds.FirstOrDefault(r => r.Id.Equals(invitation, StringComparison.OrdinalIgnoreCase));
        return round is null || MavenProgress.Ready(MavenProgress.Read(WorldOf(player).CharacterData), round);
    }

    public static void Open(MapOpening request)
    {
        if (request.Campaign || request.Slots.Count == 0) return;
        var rows = MavenProgress.Rounds;
        var round = rows.FirstOrDefault(r => request.Slots.Any(i => i.BaseRow == r.ItemRow));
        if (round is null) return;
        request.Opening = null;
        var progress = MavenProgress.Read(WorldOf(request.Player).CharacterData);
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

    public static bool ApplyFlags(Player player, MavenProgress.Progress progress)
    {
        var flags = player.Character.LeagueQuestFlags;
        bool Set(int id, bool on) => on ? flags.Add(id) : flags.Remove(id);
        var rounds = MavenProgress.Rounds;
        var current = MavenProgress.Current(progress);
        bool changed = Set(ReadyFlag, current is not null && MavenProgress.Ready(progress, current));
        for (int i = 0; i < rounds.Count; i++)
            changed |= Set(rounds[i].VictoryFlag, progress.Wins > i);
        changed |= Set(PinnacleUnlocked, rounds.Count > 0 && progress.Wins >= rounds.Count
            || ClientHasQuestFlag(player, "GrantAtlasUpgrade1"));
        return changed;
    }

    static async Task Sync(Player player, MavenProgress.Progress progress)
    {
        if (!ApplyFlags(player, progress) || !player.Connection.PlayerSent) return;
        await global::Game.SendWorldRecord(player, player.Id,
            InstanceEntities.PlayerLeagueQuestFlagsUpdate(player.Id, Quests.League.Bits(player.Character.LeagueQuestFlags)),
            "0x02bc Maven invitation progress");
    }
}
