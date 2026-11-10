using System.Diagnostics;
using System.Net.Sockets;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.Accounts;
using GameServer.Storage;
using GameServer.Core.Diagnostics;
using WalkEstimator = GameServer.Core.World.WalkEstimator;

static partial class Game
{
    // The real connection's admission path, shared with regression tests before any area packets are published.
    internal static int PrepareCharacterForArea(string remote, ClientState conn, int level)
    {
        conn.Instance?.Ground.Observe(conn);
        lock (conn.Monsters)
        {
            using var availability = AscendancyPolicies.Enter(conn,
                CharacterSaves.Load(CharacterName(conn))?.MainAscendancy?.Selection);
            CheckAscendancyClientCompatibility(conn);
            PlayerInventories.SetClass(CharacterName(conn), conn.ClassRow);   // before its items are first used (start kit)
            ValidateSavedProgress(conn, CharacterSaves.Load(CharacterName(conn)));
            // Existing items must load successfully before LoadProgress can migrate gold or normalize passives.
            // A new character's start kit is built and saved only after the read-only progress preflight above succeeds.
            var startItems = ItemsOf(conn);
            level = LoadProgress(conn, level);
            NormalizePassiveJewels(conn);
            // Towns and hideouts refill the flasks, before the inventories go to the client.
            if (LeagueFor(conn).Rules.TownRefillsFlasks && (WaypointCatalog.Areas.Find(conn.Layout.Hash) is { IsTown: true } || HideoutCatalog.Hideouts.Any(h => h.AreaHash16 == conn.Layout.Hash))
                && FillFlasks(startItems, int.MaxValue, catalog: ItemsFor(conn)).Count > 0)
                Console.WriteLine($"[gateway] {remote} flasks refilled in town");
            // A sale still open when the last connection ended (a disconnect, a crash) ended as a cancel: every offered
            // item is in its cell (it never left it).
            if (startItems.CancelOffers().Count is > 0 and var cancelled)
                Console.WriteLine($"[gateway] {remote} {cancelled} item(s) offered in an unfinished sale are back in their cells");
            return level;
        }
    }

    // The Escape menu (C→S 0x1d4, 22-pause.md) pauses the area outside towns and hideouts, where the client does not ask
    // anyway: the Clock stops, the monster AI and the player tick skip their steps, and S→C 0x1d3 pauses the client's world.
    internal static async Task SetPaused(string remote, NetworkStream stream, ConnectionCiphers ciphers, ClientState conn, bool pause)
    {
        ushort here = conn.Layout.Hash;
        if (pause && (WaypointCatalog.Areas.Find(here) is { IsTown: true } || HideoutCatalog.Hideouts.Any(h => h.AreaHash16 == here)))
        {
            Console.WriteLine($"[gateway] {remote} <= 0x01d4 pause: in a town or hideout, not paused");
            return;
        }
        // The clock is the instance's (InstanceWorld): with another player in it, it goes on.
        if (pause && conn.Instance.Presence.PlayersExcept(conn.PeerId).Any(p => p.Owner is { Closed: false }))
        {
            Console.WriteLine($"[gateway] {remote} <= 0x01d4 pause: another player is in the instance, not paused");
            return;
        }
        // Pause is a barrier: commit the preceding combat interval before freezing the world clock.
        // A module-owned pause (the Survivors reward window) must not be released by the client's own Escape-menu
        // unpause: the window's opening can close that menu, and restarting the clock under the open choice would let
        // the horde kill the player while they pick. Only the module that holds it resumes the clock.
        if (!pause && conn.PauseHeld)
        {
            await Send(remote, stream, ciphers, InstancePause.State(true),
                "0x01d3 still paused (C→S 0x01d4 0 ignored; a module holds the pause)");
            return;
        }
        using (ProjectileMutation(conn))
        {
            foreach (var changed in GameEvents.PauseChanged.For(conn)) changed(conn, pause);
            if (pause) conn.Clock.Stop(); else conn.Clock.Start();
        }
        await Send(remote, stream, ciphers, InstancePause.State(pause), $"0x01d3 {(pause ? "paused" : "resumed")} (C→S 0x01d4 {(pause ? 1 : 0)})");
    }
}
