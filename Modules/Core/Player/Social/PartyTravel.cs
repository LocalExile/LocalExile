using GameServer.Core;
using GameServer.Core.Protocol.Instance;
using static Program;

namespace GameServer.Modules.Social;

/// <summary>C→S 0x92: travel to a current party member's checkpoint or portal.</summary>
static class PartyTravel
{
    /// <summary>Fills this type's game data at startup (the world loader, before the Gateway listens).</summary>
    public static void LoadPartyTravelData()
    {
        AreaMods = AreaModCatalog.Load();
        Access = LoadAccess();
    }

    static AreaModCatalog AreaMods = default!;
    static Dictionary<ushort, int[]> Access = default!;

    static Dictionary<ushort, int[]> LoadAccess() =>
        ClientTables.Table("WorldAreas")
        .GroupBy(r => (ushort)r.Int("HASH16"))
        .ToDictionary(g => g.Key, g => g.First().Ints("EndQuestFlags").ToArray());

    internal static Player? Member(Player player, string name) => PartyRoster.TravelMember(player, name);

    internal static PendingTransfer? Destination(Player player, Player member)
    {
        if (member.InstanceNumber == 0) return null;
        PendingTransfer destination;
        // Party travel to a map goes outside its device. Direct entry would bypass its six-portal budget.
        var map = MapDevice.ForInstance(member);
        if (map is not null || MapDevices.IsMapArea(member.Layout.Hash))
        {
            if ((map?.Entrance ?? map?.ReturnTo) is not { Instance: not 0 } origin) return null;
            destination = new(origin.Area, origin.Instance, false, ArriveAt: (origin.X, origin.Y));
        }
        else
        {
            // These areas have their own admission/lifecycle; a party button cannot enter them midway through a run.
            if (LabyrinthDay.DifficultyOf(member.Layout.Id) is not null || AreaMods.DisablesPortals(member.Layout.Id))
                return null;
            bool waypoint;
            lock (member.Character.Waypoints) waypoint = member.Character.Waypoints.Contains(member.Layout.Id);
            var arrival = member.ReturnPortal is { } portal && member.ReturnPortalTo != 0
                ? member.Layout.Near((portal.X, portal.Y)) : member.Layout.Checkpoint(waypoint);
            destination = new(member.Layout.Hash, member.InstanceNumber, false, ArriveAt: arrival);
        }
        if (!Access.TryGetValue(destination.Area, out var required)) return null;
        var flags = PlayerQuestFlags(player).ToHashSet();
        return required.All(flags.Contains) ? destination : null;
    }

    internal static async Task<bool> Travel(Player player, string name)
    {
        var member = Member(player, name);
        if (member is null) return false;
        var destination = Destination(player, member);
        if (destination is null)
        {
            if (player.Commands is { } commands) await commands.Reply("You cannot travel to that party member's area.");
            return true;
        }
        if (destination.Area == player.Layout.Hash && destination.Instance == player.InstanceNumber) return true;
        // Pin the selected member's instance: another party member may be in a different copy of the same area.
        await ChangeArea(player, destination, $"party panel: travel to {name}");
        return true;
    }
}
